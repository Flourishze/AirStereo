using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AirStereo2.Services;

public sealed class WindowsMediaSessionService : IDisposable
{
	private sealed record PreparedArtwork(byte[] Bytes, string Mime);

	private static WindowsMediaSessionService? _instance;

	private GlobalSystemMediaTransportControlsSessionManager? _manager;

	private GlobalSystemMediaTransportControlsSession? _currentSession;

	private readonly object _lock = new object();

	private readonly SemaphoreSlim _mediaCommandGate = new SemaphoreSlim(1, 1);

	private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);

	private CancellationTokenSource? _debounceCts;

	private bool _disposed;

	private bool _started;

	private bool _backendSubscribed;

	private string _lastSentTrackKey = string.Empty;

	private string _lastSentArtworkFingerprint = string.Empty;

	private string? _cachedArtworkBase64;

	private string _cachedArtworkMime = "image/jpeg";

	private NowPlayingProgress? _lastSentProgress;

	private DateTime _lastProgressSentUtc = DateTime.MinValue;

	private Timer? _progressTimer;

	public static WindowsMediaSessionService Instance => _instance ?? (_instance = new WindowsMediaSessionService());

	public void Start()
	{
		lock (_lock)
		{
			if (_disposed || _started)
			{
				return;
			}
			_started = true;
			AirPlayStreamer.Instance.LogReceived += OnBackendLogReceived;
			_backendSubscribed = true;
			_progressTimer = new Timer(delegate
			{
				if (!_disposed && SettingsManager.ReadConfig((AppConfig config) => config.SendNowPlayingMetadata) && AirPlayStreamer.Instance.IsConnected)
				{
					SchedulePoll();
				}
			}, null, TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(2.0));
		}
		InitializeAsync();
	}

	private void OnBackendLogReceived(string logLine)
	{
		if (IsBackendRecoveryEvent(logLine))
		{
			ResetTrackCache();
			SchedulePoll();
		}
	}

	internal static bool IsBackendRecoveryEvent(string logLine)
	{
		return logLine.StartsWith("[Status] BACKEND_RECOVERED ", StringComparison.Ordinal);
	}

	private async Task InitializeAsync()
	{
		try
		{
			GlobalSystemMediaTransportControlsSessionManager result = await WindowsRuntimeSystemExtensions.AsTask<GlobalSystemMediaTransportControlsSessionManager>(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
			if (result == (GlobalSystemMediaTransportControlsSessionManager)null)
			{
				return;
			}
			lock (_lock)
			{
				if (_disposed)
				{
					return;
				}
				_manager = result;
				result.CurrentSessionChanged += OnCurrentSessionChanged;
			}
			UpdateCurrentSession(result.GetCurrentSession());
		}
		catch (Exception)
		{
		}
	}

	private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
	{
		try
		{
			UpdateCurrentSession(sender.GetCurrentSession());
		}
		catch (Exception)
		{
		}
	}

	private void UpdateCurrentSession(GlobalSystemMediaTransportControlsSession? session)
	{
		lock (_lock)
		{
			if (_disposed)
			{
				return;
			}
			if (_currentSession != (GlobalSystemMediaTransportControlsSession)null)
			{
				_currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
				_currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
				_currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
			}
			_currentSession = session;
			if (_currentSession != (GlobalSystemMediaTransportControlsSession)null)
			{
				_currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
				_currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
				_currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
			}
		}
		SchedulePoll();
	}

	private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
	{
		SchedulePoll();
	}

	private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
	{
		SchedulePoll();
	}

	private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
	{
		SchedulePoll();
	}

	public void ResetTrackCache()
	{
		lock (_lock)
		{
			_lastSentTrackKey = string.Empty;
			_lastSentArtworkFingerprint = string.Empty;
			_cachedArtworkBase64 = null;
			_cachedArtworkMime = "image/jpeg";
			_lastSentProgress = null;
			_lastProgressSentUtc = DateTime.MinValue;
		}
	}

	public void SchedulePoll()
	{
		CancellationTokenSource cts;
		lock (_lock)
		{
			if (_disposed)
			{
				return;
			}
			_debounceCts?.Cancel();
			cts = new CancellationTokenSource();
			_debounceCts = cts;
		}
		Task.Run(async delegate
		{
			CancellationToken token = cts.Token;
			try
			{
				_ = 2;
				try
				{
					await Task.Delay(300, token).ConfigureAwait(continueOnCapturedContext: false);
					await _refreshGate.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
					try
					{
						await RefreshNowPlayingAsync(token).ConfigureAwait(continueOnCapturedContext: false);
					}
					finally
					{
						_refreshGate.Release();
					}
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception)
				{
				}
			}
			finally
			{
				lock (_lock)
				{
					if (_debounceCts == cts)
					{
						_debounceCts = null;
					}
				}
				cts.Dispose();
			}
		});
	}

	public async Task<bool> TryHandleMediaCommandAsync(string logLine)
	{
		if (_disposed || !WindowsMediaKeySimulator.TryParseMediaCommandVerb(logLine, out var command))
		{
			return false;
		}
		await _mediaCommandGate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			if (_disposed)
			{
				return false;
			}
			bool processScoped = SettingsManager.ReadConfig((AppConfig config) => config.CaptureSourceIndex == 2);
			GlobalSystemMediaTransportControlsSession session = ResolveMediaCommandSession(processScoped);
			if (session != (GlobalSystemMediaTransportControlsSession)null)
			{
				try
				{
					TaskAwaiter<bool> taskAwaiter2 = default(TaskAwaiter<bool>);
					bool handled;
					switch (command)
					{
					case WindowsMediaCommand.Play:
					{
						bool result = await WindowsRuntimeSystemExtensions.AsTask<bool>(session.TryPlayAsync());
						handled = result;
						break;
					}
					case WindowsMediaCommand.Pause:
					{
						bool result = await WindowsRuntimeSystemExtensions.AsTask<bool>(session.TryPauseAsync());
						handled = result;
						break;
					}
					case WindowsMediaCommand.Toggle:
					{
						GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo = session.GetPlaybackInfo();
						bool result;
						if ((int)((playbackInfo != null) ? new GlobalSystemMediaTransportControlsSessionPlaybackStatus?(playbackInfo.PlaybackStatus) : ((GlobalSystemMediaTransportControlsSessionPlaybackStatus?)null)).GetValueOrDefault() == 4)
						{
							result = await WindowsRuntimeSystemExtensions.AsTask<bool>(session.TryPauseAsync());
						}
						else
						{
							result = await WindowsRuntimeSystemExtensions.AsTask<bool>(session.TryPlayAsync());
						}
						handled = result;
						break;
					}
					case WindowsMediaCommand.Next:
						try
						{
							bool result = await WindowsRuntimeSystemExtensions.AsTask<bool>(session.TrySkipNextAsync());
							handled = result;
						}
						catch
						{
							handled = false;
						}
						if (!handled)
						{
							handled = WindowsMediaKeySimulator.TryHandleMediaCommand(logLine);
						}
						break;
					case WindowsMediaCommand.Previous:
					{
						long posMs = 0L;
						try
						{
							GlobalSystemMediaTransportControlsSessionTimelineProperties timelineProperties = session.GetTimelineProperties();
							if (timelineProperties != (GlobalSystemMediaTransportControlsSessionTimelineProperties)null)
							{
								posMs = (long)timelineProperties.Position.TotalMilliseconds;
							}
						}
						catch
						{
						}
						try
						{
							bool result = await WindowsRuntimeSystemExtensions.AsTask<bool>(session.TrySkipPreviousAsync());
							handled = result;
							if (handled && posMs > 3000)
							{
								await Task.Delay(100).ConfigureAwait(continueOnCapturedContext: false);
								session.TrySkipPreviousAsync();
							}
						}
						catch
						{
							handled = false;
						}
						if (!handled)
						{
							handled = WindowsMediaKeySimulator.TryHandleMediaCommand(logLine);
						}
						break;
					}
					case WindowsMediaCommand.Stop:
					{
						bool result = await WindowsRuntimeSystemExtensions.AsTask<bool>(session.TryStopAsync());
						handled = result;
						break;
					}
					default:
						handled = false;
						break;
					}
					if (handled)
					{
						SchedulePoll();
						return true;
					}
				}
				catch (Exception)
				{
				}
			}
			if (processScoped)
			{
				return false;
			}
			bool num = WindowsMediaKeySimulator.TryHandleMediaCommand(logLine);
			if (num)
			{
				SchedulePoll();
			}
			return num;
		}
		finally
		{
			_mediaCommandGate.Release();
		}
	}

	private GlobalSystemMediaTransportControlsSession? ResolveMediaCommandSession(bool processScoped)
	{
		GlobalSystemMediaTransportControlsSessionManager manager;
		GlobalSystemMediaTransportControlsSession currentSession;
		lock (_lock)
		{
			manager = _manager;
			currentSession = _currentSession;
		}
		if (!processScoped)
		{
			try
			{
				IReadOnlyList<GlobalSystemMediaTransportControlsSession> readOnlyList = ((manager != null) ? manager.GetSessions() : null);
				if (readOnlyList != null)
				{
					GlobalSystemMediaTransportControlsSession val = ((IEnumerable<GlobalSystemMediaTransportControlsSession>)readOnlyList).FirstOrDefault((Func<GlobalSystemMediaTransportControlsSession, bool>)delegate(GlobalSystemMediaTransportControlsSession s)
					{
						//IL_000c: Unknown result type (might be due to invalid IL or missing references)
						//IL_0012: Invalid comparison between Unknown and I4
						GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo = s.GetPlaybackInfo();
						return playbackInfo != null && (int)playbackInfo.PlaybackStatus == 4;
					});
					if (val != (GlobalSystemMediaTransportControlsSession)null)
					{
						return val;
					}
				}
				GlobalSystemMediaTransportControlsSession val2 = ((manager != null) ? manager.GetCurrentSession() : null);
				if (val2 != (GlobalSystemMediaTransportControlsSession)null)
				{
					return val2;
				}
				if (currentSession != (GlobalSystemMediaTransportControlsSession)null)
				{
					return currentSession;
				}
				if (readOnlyList != null)
				{
					return readOnlyList.FirstOrDefault();
				}
			}
			catch (Exception)
			{
			}
			return null;
		}
		try
		{
			var (processName, executablePath) = SettingsManager.ReadConfig((AppConfig config) => (SelectedProcessName: config.SelectedProcessName, SelectedProcessExecutablePath: config.SelectedProcessExecutablePath));
			if (currentSession != (GlobalSystemMediaTransportControlsSession)null && SourceAppMatchesProcessIdentity(currentSession.SourceAppUserModelId, processName, executablePath))
			{
				return currentSession;
			}
			if (manager == (GlobalSystemMediaTransportControlsSessionManager)null)
			{
				return null;
			}
			return ((IEnumerable<GlobalSystemMediaTransportControlsSession>)manager.GetSessions()).FirstOrDefault((Func<GlobalSystemMediaTransportControlsSession, bool>)((GlobalSystemMediaTransportControlsSession session) => SourceAppMatchesProcessIdentity(session.SourceAppUserModelId, processName, executablePath)));
		}
		catch (Exception)
		{
			return null;
		}
	}

	internal static bool SourceAppMatchesProcessIdentity(string? sourceAppUserModelId, string? processName, string? executablePath)
	{
		if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
		{
			return false;
		}
		if (sourceAppUserModelId.IndexOf("ZuneMusic", StringComparison.OrdinalIgnoreCase) >= 0 && ((processName != null && processName.IndexOf("Media.Player", StringComparison.OrdinalIgnoreCase) >= 0) || (executablePath != null && executablePath.IndexOf("Media.Player", StringComparison.OrdinalIgnoreCase) >= 0)))
		{
			return true;
		}
		List<string> list = IdentityTokens(sourceAppUserModelId);
		if (list.Count == 0)
		{
			return false;
		}
		string text;
		try
		{
			text = Path.GetFileNameWithoutExtension(executablePath ?? string.Empty);
		}
		catch
		{
			text = string.Empty;
		}
		string[] array = new string[2]
		{
			processName ?? string.Empty,
			text
		};
		for (int i = 0; i < array.Length; i++)
		{
			List<string> list2 = IdentityTokens(array[i]);
			if (list2.Count == 0 || list2.Count > list.Count)
			{
				continue;
			}
			for (int j = 0; j <= list.Count - list2.Count; j++)
			{
				bool flag = true;
				for (int k = 0; k < list2.Count; k++)
				{
					if (!string.Equals(list[j + k], list2[k], StringComparison.OrdinalIgnoreCase))
					{
						flag = false;
						break;
					}
				}
				if (flag)
				{
					return true;
				}
			}
		}
		return false;
	}

	private static List<string> IdentityTokens(string value)
	{
		char[] separator = value.Where((char character) => !char.IsLetterOrDigit(character)).Distinct().ToArray();
		return value.Split(separator, StringSplitOptions.RemoveEmptyEntries).ToList();
	}

	private async Task RefreshNowPlayingAsync(CancellationToken token)
	{
		if (!SettingsManager.ReadConfig((AppConfig config) => config.SendNowPlayingMetadata))
		{
			return;
		}
		bool processScoped = SettingsManager.ReadConfig((AppConfig config) => config.CaptureSourceIndex == 2);
		var (val, val2) = await ResolveMetadataSessionAsync(processScoped, token).ConfigureAwait(continueOnCapturedContext: false);
		if (val == (GlobalSystemMediaTransportControlsSession)null || val2 == (GlobalSystemMediaTransportControlsSessionMediaProperties)null)
		{
			return;
		}
		try
		{
			if (token.IsCancellationRequested)
			{
				return;
			}
			string title = val2.Title?.Trim() ?? string.Empty;
			string artist = ((!string.IsNullOrWhiteSpace(val2.Artist)) ? val2.Artist : val2.AlbumArtist)?.Trim() ?? string.Empty;
			string album = val2.AlbumTitle?.Trim() ?? string.Empty;
			if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist) && string.IsNullOrEmpty(album) && val2.Thumbnail == null)
			{
				return;
			}
			string trackKey = $"{title}|{artist}|{album}";
			long positionMs = 0L;
			long durationMs = 0L;
			GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo = val.GetPlaybackInfo();
			bool flag = playbackInfo != null && (int)playbackInfo.PlaybackStatus == 4;
			GlobalSystemMediaTransportControlsSessionPlaybackStatus? val3 = ((playbackInfo != null) ? new GlobalSystemMediaTransportControlsSessionPlaybackStatus?(playbackInfo.PlaybackStatus) : ((GlobalSystemMediaTransportControlsSessionPlaybackStatus?)null));
			if (!val3.HasValue)
			{
				goto IL_029e;
			}
			GlobalSystemMediaTransportControlsSessionPlaybackStatus valueOrDefault = val3.GetValueOrDefault();
			string text;
			if ((int)valueOrDefault != 4)
			{
				if ((int)valueOrDefault != 5)
				{
					goto IL_029e;
				}
				text = "paused";
			}
			else
			{
				text = "playing";
			}
			goto IL_02a5;
			IL_029e:
			text = "stopped";
			goto IL_02a5;
			IL_02a5:
			string playbackState = text;
			double playbackRate = (flag ? Math.Max(0.01, ((playbackInfo != null) ? playbackInfo.PlaybackRate : ((double?)null)) ?? 1.0) : 0.0);
			try
			{
				GlobalSystemMediaTransportControlsSessionTimelineProperties timelineProperties = val.GetTimelineProperties();
				if (timelineProperties != (GlobalSystemMediaTransportControlsSessionTimelineProperties)null)
				{
					double totalMilliseconds = (timelineProperties.EndTime - timelineProperties.StartTime).TotalMilliseconds;
					if (totalMilliseconds > 0.0)
					{
						durationMs = (long)totalMilliseconds;
					}
					positionMs = (long)((timelineProperties.Position >= timelineProperties.StartTime) ? (timelineProperties.Position - timelineProperties.StartTime) : timelineProperties.Position).TotalMilliseconds;
					if (flag && playbackInfo != (GlobalSystemMediaTransportControlsSessionPlaybackInfo)null)
					{
						TimeSpan timeSpan = DateTimeOffset.UtcNow - timelineProperties.LastUpdatedTime;
						if (timeSpan > -TimeSpan.FromSeconds(2.0))
						{
							double num = playbackRate;
							long num2 = ((durationMs > 0) ? (durationMs + 5000) : 86400000);
							if (timeSpan.TotalMilliseconds > 0.0 && timeSpan.TotalMilliseconds < (double)num2)
							{
								positionMs += (long)(timeSpan.TotalMilliseconds * num);
							}
						}
					}
					positionMs = ((durationMs <= 0) ? Math.Max(0L, positionMs) : Math.Clamp(positionMs, 0L, durationMs));
				}
			}
			catch
			{
			}
			NowPlayingProgress progress = new NowPlayingProgress(positionMs, durationMs, playbackState, playbackRate);
			bool flag2;
			bool flag3;
			bool flag4;
			lock (_lock)
			{
				flag2 = !string.Equals(_lastSentTrackKey, trackKey, StringComparison.Ordinal);
				flag3 = _cachedArtworkBase64 == null;
				flag4 = _cachedArtworkBase64 != null && !string.Equals(_lastSentArtworkFingerprint, ArtworkFingerprint(_cachedArtworkBase64), StringComparison.Ordinal);
			}
			if (flag2 || flag4 || (flag3 && val2.Thumbnail != null))
			{
				lock (_lock)
				{
					if (flag2)
					{
						_cachedArtworkBase64 = null;
						_cachedArtworkMime = "image/jpeg";
					}
				}
				if (val2.Thumbnail != null)
				{
					try
					{
						IRandomAccessStreamWithContentType result = await WindowsRuntimeSystemExtensions.AsTask<IRandomAccessStreamWithContentType>(val2.Thumbnail.OpenReadAsync());
						IRandomAccessStreamWithContentType stream = result;
						try
						{
							if (stream != null && ((IRandomAccessStream)stream).Size != 0 && ((IRandomAccessStream)stream).Size <= 8388608)
							{
								byte[] bytes = new byte[((IRandomAccessStream)stream).Size];
								DataReader reader = new DataReader(((IRandomAccessStream)stream).GetInputStreamAt(0uL));
								try
								{
									await WindowsRuntimeSystemExtensions.AsTask<uint>((IAsyncOperation<uint>)(object)reader.LoadAsync((uint)((IRandomAccessStream)stream).Size));
									reader.ReadBytes(bytes);
									if (token.IsCancellationRequested)
									{
										return;
									}
									PreparedArtwork preparedArtwork = PrepareArtwork(bytes);
									if (preparedArtwork != null && !token.IsCancellationRequested)
									{
										lock (_lock)
										{
											_cachedArtworkMime = preparedArtwork.Mime;
											_cachedArtworkBase64 = Convert.ToBase64String(preparedArtwork.Bytes);
										}
									}
								}
								finally
								{
									((IDisposable)reader)?.Dispose();
								}
							}
						}
						finally
						{
							((IDisposable)stream)?.Dispose();
						}
					}
					catch (Exception)
					{
					}
				}
				string cachedArtworkBase;
				string cachedArtworkMime;
				lock (_lock)
				{
					cachedArtworkBase = _cachedArtworkBase64;
					cachedArtworkMime = _cachedArtworkMime;
				}
				if (AirPlayStreamer.Instance.SendNowPlaying(title, artist, album, cachedArtworkBase, cachedArtworkMime, positionMs, durationMs, playbackState, playbackRate))
				{
					lock (_lock)
					{
						_lastSentTrackKey = trackKey;
						_lastSentArtworkFingerprint = ArtworkFingerprint(cachedArtworkBase);
						_lastSentProgress = progress;
						_lastProgressSentUtc = DateTime.UtcNow;
						return;
					}
				}
				return;
			}
			bool flag5;
			lock (_lock)
			{
				flag5 = NowPlayingProgressPolicy.ShouldSend(_lastSentProgress, progress, DateTime.UtcNow - _lastProgressSentUtc);
			}
			if (flag5 && AirPlayStreamer.Instance.SendNowPlaying(null, null, null, null, null, positionMs, durationMs, playbackState, playbackRate))
			{
				lock (_lock)
				{
					_lastSentProgress = progress;
					_lastProgressSentUtc = DateTime.UtcNow;
				}
			}
		}
		catch (Exception)
		{
		}
	}

	private async Task<(GlobalSystemMediaTransportControlsSession? Session, GlobalSystemMediaTransportControlsSessionMediaProperties? Properties)> ResolveMetadataSessionAsync(bool processScoped, CancellationToken token)
	{
		GlobalSystemMediaTransportControlsSessionManager manager;
		GlobalSystemMediaTransportControlsSession currentSession;
		lock (_lock)
		{
			manager = _manager;
			currentSession = _currentSession;
		}
		List<GlobalSystemMediaTransportControlsSession> candidates = new List<GlobalSystemMediaTransportControlsSession>();
		try
		{
			IReadOnlyList<GlobalSystemMediaTransportControlsSession> readOnlyList = ((manager != null) ? manager.GetSessions() : null);
			if (readOnlyList != null)
			{
				foreach (GlobalSystemMediaTransportControlsSession item in readOnlyList)
				{
					GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo = item.GetPlaybackInfo();
					if (playbackInfo != null && (int)playbackInfo.PlaybackStatus == 4)
					{
						AddCandidate(item);
					}
				}
				AddCandidate((manager != null) ? manager.GetCurrentSession() : null);
				AddCandidate(currentSession);
				foreach (GlobalSystemMediaTransportControlsSession item2 in readOnlyList)
				{
					AddCandidate(item2);
				}
			}
			else
			{
				AddCandidate(currentSession);
			}
		}
		catch (Exception)
		{
			AddCandidate(currentSession);
		}
		TaskAwaiter<GlobalSystemMediaTransportControlsSessionMediaProperties> taskAwaiter2 = default(TaskAwaiter<GlobalSystemMediaTransportControlsSessionMediaProperties>);
		foreach (GlobalSystemMediaTransportControlsSession candidate in candidates)
		{
			if (token.IsCancellationRequested)
			{
				break;
			}
			if (processScoped)
			{
				(string, string) tuple = SettingsManager.ReadConfig((AppConfig config) => (SelectedProcessName: config.SelectedProcessName, SelectedProcessExecutablePath: config.SelectedProcessExecutablePath));
				if (!SourceAppMatchesProcessIdentity(candidate.SourceAppUserModelId, tuple.Item1, tuple.Item2))
				{
					continue;
				}
			}
			try
			{
				GlobalSystemMediaTransportControlsSessionMediaProperties result = await WindowsRuntimeSystemExtensions.AsTask<GlobalSystemMediaTransportControlsSessionMediaProperties>(candidate.TryGetMediaPropertiesAsync());
				if (result == (GlobalSystemMediaTransportControlsSessionMediaProperties)null || (string.IsNullOrWhiteSpace(result.Title) && string.IsNullOrWhiteSpace(result.Artist) && string.IsNullOrWhiteSpace(result.AlbumArtist) && string.IsNullOrWhiteSpace(result.AlbumTitle) && result.Thumbnail == null))
				{
					continue;
				}
				return (Session: candidate, Properties: result);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception)
			{
			}
		}
		return (Session: null, Properties: null);
		void AddCandidate(GlobalSystemMediaTransportControlsSession? val)
		{
			if (val != (GlobalSystemMediaTransportControlsSession)null && !candidates.Contains(val))
			{
				candidates.Add(val);
			}
		}
	}

	private static string ArtworkFingerprint(string? artworkBase64)
	{
		if (string.IsNullOrEmpty(artworkBase64))
		{
			return string.Empty;
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artworkBase64)));
	}

	private static PreparedArtwork? PrepareArtwork(byte[] original)
	{
		string text = DetectArtworkMime(original);
		try
		{
			using MemoryStream bitmapStream = new MemoryStream(original, writable: false);
			BitmapFrame bitmapFrame = BitmapDecoder.Create(bitmapStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.Default).Frames.FirstOrDefault();
			if (bitmapFrame == null)
			{
				return (original.Length <= 4194304 && text != null) ? new PreparedArtwork(original, text) : null;
			}
			bool flag = bitmapFrame.PixelWidth > 2048 || bitmapFrame.PixelHeight > 2048;
			if (!ShouldTranscodeArtwork(original.Length, bitmapFrame.PixelWidth, bitmapFrame.PixelHeight, text) && text != null)
			{
				return new PreparedArtwork(original, text);
			}
			BitmapSource bitmapSource;
			if (flag)
			{
				using MemoryStream streamSource = new MemoryStream(original, writable: false);
				BitmapImage bitmapImage = new BitmapImage();
				bitmapImage.BeginInit();
				bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
				bitmapImage.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
				bitmapImage.StreamSource = streamSource;
				if (bitmapFrame.PixelWidth >= bitmapFrame.PixelHeight)
				{
					bitmapImage.DecodePixelWidth = 2048;
				}
				else
				{
					bitmapImage.DecodePixelHeight = 2048;
				}
				bitmapImage.EndInit();
				((Freezable)bitmapImage).Freeze();
				bitmapSource = bitmapImage;
			}
			else
			{
				using MemoryStream bitmapStream2 = new MemoryStream(original, writable: false);
				bitmapSource = BitmapDecoder.Create(bitmapStream2, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
				((Freezable)bitmapSource).Freeze();
			}
			JpegBitmapEncoder jpegBitmapEncoder = new JpegBitmapEncoder
			{
				QualityLevel = 95
			};
			jpegBitmapEncoder.Frames.Add(BitmapFrame.Create(bitmapSource));
			using MemoryStream memoryStream = new MemoryStream();
			jpegBitmapEncoder.Save(memoryStream);
			if (memoryStream.Length <= 0 || memoryStream.Length > 4194304)
			{
				return null;
			}
			return new PreparedArtwork(memoryStream.ToArray(), "image/jpeg");
		}
		catch (Exception)
		{
			return (original.Length <= 4194304 && text != null) ? new PreparedArtwork(original, text) : null;
		}
	}

	internal static bool ShouldTranscodeArtwork(int byteLength, int pixelWidth, int pixelHeight, string? mime)
	{
		if (byteLength <= 4194304 && pixelWidth <= 2048 && pixelHeight <= 2048)
		{
			return mime == null;
		}
		return true;
	}

	internal static string? DetectArtworkMime(byte[] bytes)
	{
		if (bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71)
		{
			return "image/png";
		}
		if (bytes.Length >= 3 && bytes[0] == byte.MaxValue && bytes[1] == 216 && bytes[2] == byte.MaxValue)
		{
			return "image/jpeg";
		}
		return null;
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (!_disposed)
			{
				_disposed = true;
				_debounceCts?.Cancel();
				_debounceCts = null;
				_progressTimer?.Dispose();
				_progressTimer = null;
				if (_backendSubscribed)
				{
					AirPlayStreamer.Instance.LogReceived -= OnBackendLogReceived;
					_backendSubscribed = false;
				}
				if (_currentSession != (GlobalSystemMediaTransportControlsSession)null)
				{
					_currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
					_currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
					_currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
					_currentSession = null;
				}
				if (_manager != (GlobalSystemMediaTransportControlsSessionManager)null)
				{
					_manager.CurrentSessionChanged -= OnCurrentSessionChanged;
					_manager = null;
				}
			}
		}
	}
}
