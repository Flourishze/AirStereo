using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AirStereo2.Services;

public sealed class OnboardingAudioPlayer : IDisposable
{
	private readonly record struct PlaybackResources(IWavePlayer? Output, LoopingWaveStream? LoopStream, MMDevice? Device, MMDeviceEnumerator? Enumerator);

	private readonly struct RenderDeviceResources : IDisposable
	{
		public MMDeviceEnumerator? Enumerator { get; }

		public MMDevice? Device { get; }

		public RenderDeviceResources(MMDeviceEnumerator? enumerator, MMDevice? device)
		{
			Enumerator = enumerator;
			Device = device;
		}

		public void Dispose()
		{
			MMDevice? device = Device;
			if (device != null)
			{
				device.Dispose();
			}
			MMDeviceEnumerator? enumerator = Enumerator;
			if (enumerator != null)
			{
				enumerator.Dispose();
			}
		}
	}

	private sealed class LoopingWaveStream : WaveStream
	{
		private readonly WaveStream _source;

		public override WaveFormat WaveFormat => _source.WaveFormat;

		public override long Length => long.MaxValue;

		public override long Position
		{
			get
			{
				return ((Stream)(object)_source).Position;
			}
			set
			{
				((Stream)(object)_source).Position = value;
			}
		}

		public LoopingWaveStream(WaveStream source)
		{
			_source = source;
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			if (count == 0)
			{
				return 0;
			}
			int num = 0;
			bool flag = false;
			while (num < count)
			{
				int num2 = ((Stream)(object)_source).Read(buffer, offset + num, count - num);
				if (num2 > 0)
				{
					num += num2;
					continue;
				}
				if (flag)
				{
					break;
				}
				((Stream)(object)_source).Position = 0L;
				flag = true;
			}
			return num;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				((Stream)(object)_source).Dispose();
			}
			base.Dispose(disposing);
		}
	}

	private const string PackagedAudioPath = "Assets/Testing/onboarding_test_audio.mp3";

	private const string HistoricalAudioPath = "airplay2-rs/test_tone.mp3";

	private const float PlaybackVolume = 0.9f;

	private readonly object _lock = new object();

	private IWavePlayer? _output;

	private LoopingWaveStream? _loopStream;

	private MMDeviceEnumerator? _deviceEnumerator;

	private MMDevice? _outputDevice;

	private bool _stopping;

	private bool _disposed;

	public event Action<Exception>? PlaybackFailed;

	public void ValidateConfiguredRoute()
	{
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Expected O, but got Unknown
		ThrowIfDisposed();
		AudioFileReader val = new AudioFileReader(ResolveAudioPath() ?? throw new InvalidOperationException("The onboarding test recording is missing from this installation."));
		try
		{
			using RenderDeviceResources renderDeviceResources = OpenConfiguredRenderDevice();
			IWavePlayer val2 = CreateOutput(renderDeviceResources.Device);
			try
			{
				val2.Init((IWaveProvider)(object)val);
			}
			finally
			{
				((IDisposable)val2)?.Dispose();
			}
		}
		finally
		{
			((IDisposable)val)?.Dispose();
		}
	}

	public void Start()
	{
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Expected O, but got Unknown
		ThrowIfDisposed();
		Stop();
		string text = ResolveAudioPath() ?? throw new InvalidOperationException("The onboarding test recording is missing from this installation.");
		RenderDeviceResources renderDeviceResources = OpenConfiguredRenderDevice();
		LoopingWaveStream loopingWaveStream = null;
		IWavePlayer val = null;
		bool flag = false;
		try
		{
			loopingWaveStream = new LoopingWaveStream((WaveStream)new AudioFileReader(text)
			{
				Volume = 0.9f
			});
			val = CreateOutput(renderDeviceResources.Device);
			val.PlaybackStopped += OnPlaybackStopped;
			val.Init((IWaveProvider)(object)loopingWaveStream);
			lock (_lock)
			{
				ThrowIfDisposed();
				_deviceEnumerator = renderDeviceResources.Enumerator;
				_outputDevice = renderDeviceResources.Device;
				_loopStream = loopingWaveStream;
				_output = val;
				_stopping = false;
				renderDeviceResources = default(RenderDeviceResources);
				flag = true;
				val.Play();
			}
		}
		catch
		{
			if (flag)
			{
				Stop();
			}
			else
			{
				if (val != null)
				{
					val.PlaybackStopped -= OnPlaybackStopped;
				}
				((IDisposable)val)?.Dispose();
				((Stream)(object)loopingWaveStream)?.Dispose();
				renderDeviceResources.Dispose();
			}
			throw;
		}
	}

	public void Stop()
	{
		PlaybackResources resources;
		lock (_lock)
		{
			_stopping = true;
			resources = TakePlaybackResourcesLocked();
		}
		DisposePlaybackResources(resources);
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
		}
		Stop();
		this.PlaybackFailed = null;
	}

	private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
	{
		PlaybackResources resources;
		lock (_lock)
		{
			if (_stopping || sender != _output)
			{
				return;
			}
			_stopping = true;
			resources = TakePlaybackResourcesLocked();
		}
		Exception failure = e.Exception ?? new InvalidOperationException("The onboarding test recording stopped unexpectedly.");
		Task.Run(delegate
		{
			try
			{
				DisposePlaybackResources(resources);
				Action<Exception> action;
				lock (_lock)
				{
					action = (_disposed ? null : this.PlaybackFailed);
				}
				action?.Invoke(failure);
			}
			catch (Exception)
			{
			}
		});
	}

	private PlaybackResources TakePlaybackResourcesLocked()
	{
		PlaybackResources result = new PlaybackResources(_output, _loopStream, _outputDevice, _deviceEnumerator);
		_output = null;
		_loopStream = null;
		_outputDevice = null;
		_deviceEnumerator = null;
		return result;
	}

	private void DisposePlaybackResources(PlaybackResources resources)
	{
		if (resources.Output != null)
		{
			resources.Output.PlaybackStopped -= OnPlaybackStopped;
			try
			{
				resources.Output.Stop();
			}
			catch
			{
			}
			((IDisposable)resources.Output).Dispose();
		}
		((Stream)(object)resources.LoopStream)?.Dispose();
		MMDevice? device = resources.Device;
		if (device != null)
		{
			device.Dispose();
		}
		MMDeviceEnumerator? enumerator = resources.Enumerator;
		if (enumerator != null)
		{
			enumerator.Dispose();
		}
	}

	private static IWavePlayer CreateOutput(MMDevice? device)
	{
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Expected O, but got Unknown
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Expected O, but got Unknown
		if (device == null)
		{
			return (IWavePlayer)new WasapiOut((AudioClientShareMode)0, true, 100);
		}
		return (IWavePlayer)new WasapiOut(device, (AudioClientShareMode)0, true, 100);
	}

	private RenderDeviceResources OpenConfiguredRenderDevice()
	{
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Expected O, but got Unknown
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		(int, string) tuple = SettingsManager.ReadConfig((AppConfig value) => (CaptureSourceIndex: value.CaptureSourceIndex, SelectedCaptureDeviceId: value.SelectedCaptureDeviceId));
		if (tuple.Item1 == 2)
		{
			throw new InvalidOperationException("The onboarding recording cannot be captured in per-process mode. Temporarily choose Default audio or a virtual Loopback device in Settings.");
		}
		if (tuple.Item1 != 1)
		{
			return new RenderDeviceResources(null, null);
		}
		if (string.IsNullOrWhiteSpace(tuple.Item2))
		{
			throw new InvalidOperationException("Choose a virtual Loopback capture device before starting onboarding.");
		}
		MMDeviceEnumerator val = new MMDeviceEnumerator();
		try
		{
			MMDevice device = val.GetDevice(tuple.Item2);
			if ((int)device.DataFlow != 0)
			{
				device.Dispose();
				throw new InvalidOperationException("The selected capture device is not a render/Loopback endpoint. Choose the playback-side VB-CABLE or virtual Loopback device.");
			}
			return new RenderDeviceResources(val, device);
		}
		catch
		{
			val.Dispose();
			throw;
		}
	}

	private static string? ResolveAudioPath()
	{
		string baseDirectory = AppContext.BaseDirectory;
		string[] array = new string[3]
		{
			Path.Combine(baseDirectory, "Assets/Testing/onboarding_test_audio.mp3".Replace('/', Path.DirectorySeparatorChar)),
			Path.Combine(baseDirectory, "test_tone.mp3"),
			Path.Combine(Environment.CurrentDirectory, "airplay2-rs/test_tone.mp3".Replace('/', Path.DirectorySeparatorChar))
		};
		foreach (string text in array)
		{
			if (File.Exists(text))
			{
				return text;
			}
		}
		return null;
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}
}
