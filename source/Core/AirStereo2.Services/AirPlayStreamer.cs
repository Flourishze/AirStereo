using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AirStereo2.Models;

namespace AirStereo2.Services;

public class AirPlayStreamer : IDisposable
{
	private static AirPlayStreamer? _instance;

	private static readonly object _lock = new object();

	private static StoreLicenseService? _connectionAuthorization;

	private Process? _backendProcess;

	private readonly object _commandLock = new object();

	private readonly object _lifecycleLock = new object();

	private readonly HashSet<string> _desiredDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, BackendEndpointSelection> _desiredSelectors = new Dictionary<string, BackendEndpointSelection>(StringComparer.OrdinalIgnoreCase);

	private string[]? _desiredGroupIps;

	private BackendEndpointSelection[]? _desiredGroupSelectors;

	private int? _sessionProfileLatencyMs;

	private CalibrationDeviceIdentity[]? _playbackProfileDevices;

	private readonly Action<string>? _commandWriterForTesting;

	private int? _sessionCalibrationLatencyMs;

	private bool _calibrationCaptureIsolated;

	private readonly Dictionary<string, BackendDeviceHint> _latestHints = new Dictionary<string, BackendDeviceHint>(StringComparer.OrdinalIgnoreCase);

	private readonly object _volumeLock = new object();

	private readonly Dictionary<string, int> _pendingVolumes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, string> _pendingVolumeCommands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, DateTime> _lastVolumeSentUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, Timer> _volumeTimers = new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, int> _lastSentVolume = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private static readonly TimeSpan VolumeCommandInterval = TimeSpan.FromMilliseconds(30.0);

	private volatile bool _disposing;

	private int _restartScheduled;

	private int _consecutiveRestartAttempts;

	private DateTime _backendStartedUtc;

	private const int MaxAutomaticRestartAttempts = 5;

	private int _backendProtocolVersion = 1;

	private long? _lastEventTimestampNs;

	private BackendBuildInfo? _backendBuild;

	private TaskCompletionSource<string>? _pendingDiagSummary;

	private readonly Dictionary<string, TaskCompletionSource<string>> _pendingNetworkChecks = new Dictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);

	private readonly Dictionary<string, string> _accessPasswords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private static readonly Regex LineRx = new Regex("^(?:ts=(?<ts>\\d+)\\s)?\\[(?<tag>[A-Za-z]+)\\]\\s(?<body>.*)$", RegexOptions.Compiled);

	private static readonly char[] CommandUnsafeChars = new char[3] { '\r', '\n', '\0' };

	private int _sessionAcousticOffsetMs;

	private int? _sessionAcousticOffsetAcceptedMs;

	private int? _sessionAcousticOffsetAppliedMs;

	private StreamerPlayoutState _playoutState = new StreamerPlayoutState(new SessionSnapshot(new StreamerSessionContext(1uL, StreamerProtocolMode.GenerationAware, Guid.NewGuid()), 1L), BindingToken.Empty, null);

	private ReaderBinding? _activeReaderBinding;

	private readonly List<WeakReference<IGenerationAwareOffsetTarget>> _registeredOffsetTargets = new List<WeakReference<IGenerationAwareOffsetTarget>>();

	private static long _globalAcousticOffsetRequestId = 0L;

	private long _lastSubmittedAcousticOffsetRequestId;

	private readonly Dictionary<long, InFlightAcousticOffsetCommand> _inFlightAcousticOffsetCommands = new Dictionary<long, InFlightAcousticOffsetCommand>();

	private const string DiagSummaryPrefix = "[Diag] SUMMARY ";

	private const string DiagNetworkPrefix = "[Diag] NETWORK ";

	private const int NetworkCheckMinimumProtocol = 3;

	private static readonly TimeSpan NetworkCheckBackendStartTimeout = TimeSpan.FromSeconds(15.0);

	internal static readonly Encoding BackendPipeEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	public static AirPlayStreamer Instance
	{
		get
		{
			lock (_lock)
			{
				if (_instance == null)
				{
					_instance = new AirPlayStreamer();
				}
				return _instance;
			}
		}
	}

	public int BackendProtocolVersion => _backendProtocolVersion;

	public BackendBuildInfo? BackendBuild => _backendBuild;

	public bool V2Available => _backendProtocolVersion >= 2;

	public long? LastEventTimestampNs => _lastEventTimestampNs;

	public bool IsConnected => ProcessIsAlive(_backendProcess);

	public static bool IsInstanceRunning
	{
		get
		{
			lock (_lock)
			{
				return _instance?.IsConnected ?? false;
			}
		}
	}

	internal Action? BeforeAcousticOffsetAppliedInvokeForTesting { get; set; }

	public StreamerPlayoutState PlayoutState => Volatile.Read(in _playoutState);

	public SessionSnapshot CurrentSessionSnapshot => PlayoutState.Snapshot;

	public StreamerSessionContext CurrentSessionContext => CurrentSessionSnapshot.Context;

	private StreamerSessionContext _currentSessionContext => CurrentSessionSnapshot.Context;

	public StreamerSessionContext CurrentSessionContextSnapshot => CurrentSessionSnapshot.Context;

	public ulong ActiveSessionGeneration
	{
		get
		{
			lock (_commandLock)
			{
				return CurrentSessionSnapshot.Context.Generation;
			}
		}
		set
		{
			lock (_commandLock)
			{
				StreamerSessionContext context = CurrentSessionSnapshot.Context with
				{
					Generation = value,
					TransportInstanceId = Guid.NewGuid()
				};
				SessionSnapshot snapshot = CurrentSessionSnapshot with
				{
					Context = context
				};
				Volatile.Write(ref _playoutState, _playoutState with
				{
					Snapshot = snapshot
				});
			}
		}
	}

	public StreamerProtocolMode ProtocolMode => CurrentSessionSnapshot.Context.ProtocolMode;

	public Guid TransportInstanceId => CurrentSessionSnapshot.Context.TransportInstanceId;

	public long SessionEpoch => CurrentSessionSnapshot.Epoch;

	public int? LastAcousticOffsetAppliedMs => PlayoutState.AppliedOffsetMs;

	internal ManualResetEventSlim? PreCommitFenceForTesting { get; set; }

	public ReaderBinding? ActiveReaderBinding
	{
		get
		{
			lock (_commandLock)
			{
				return _activeReaderBinding;
			}
		}
	}

	public int? LastAcousticOffsetAcceptedMs
	{
		get
		{
			lock (_commandLock)
			{
				return _sessionAcousticOffsetAcceptedMs;
			}
		}
	}

	internal Action<InFlightAcousticOffsetCommand>? OnInFlightCommandRegisteredForTesting { get; set; }

	internal int InFlightCommandCount
	{
		get
		{
			lock (_commandLock)
			{
				return _inFlightAcousticOffsetCommands.Count;
			}
		}
	}

	public event Action<string>? LogReceived;

	public event Action<BackendEvent>? EventReceived;

	public event Action<int>? AcousticOffsetApplied;

	public event Action<ulong, int>? AcousticOffsetAppliedWithGeneration;

	public event Action<AcousticOffsetAppliedEvent>? AcousticOffsetAppliedWithContext;

	public event Action<RtxTelemetryEvent>? RtxTelemetryReceived;

	public event Action<RtxDetailEvent>? RtxDetailReceived;

	public static DateTimeOffset UnixNsToDto(long ns)
	{
		return DateTimeOffset.FromUnixTimeMilliseconds(ns / 1000000).AddTicks(ns % 1000000 / 100);
	}

	public static bool TryStripTimestamp(string rawLine, out string cleanLine, out long? timestampNs)
	{
		Match match = LineRx.Match(rawLine);
		if (match.Success)
		{
			if (match.Groups["ts"].Success && long.TryParse(match.Groups["ts"].Value, out var result))
			{
				timestampNs = result;
			}
			else
			{
				timestampNs = null;
			}
			cleanLine = "[" + match.Groups["tag"].Value + "] " + match.Groups["body"].Value;
			return match.Groups["ts"].Success;
		}
		if (rawLine.StartsWith("ts=", StringComparison.Ordinal))
		{
			int num = rawLine.IndexOf(' ');
			if (num > 3 && long.TryParse(rawLine.AsSpan(3, num - 3), out var result2))
			{
				timestampNs = result2;
				cleanLine = rawLine.Substring(num + 1);
				return true;
			}
		}
		cleanLine = rawLine;
		timestampNs = null;
		return false;
	}

	private static bool ProcessIsAlive(Process? process)
	{
		if (process == null)
		{
			return false;
		}
		try
		{
			return !process.HasExited;
		}
		catch
		{
			return false;
		}
	}

	public static void ConfigureConnectionAuthorization(StoreLicenseService service)
	{
		ArgumentNullException.ThrowIfNull(service, "service");
		lock (_lock)
		{
			if (_connectionAuthorization != service)
			{
				if (_connectionAuthorization != null)
				{
					_connectionAuthorization.Changed -= OnConnectionAuthorizationChanged;
				}
				_connectionAuthorization = service;
				service.Changed += OnConnectionAuthorizationChanged;
			}
		}
	}

	public static void ClearConnectionAuthorization(StoreLicenseService service)
	{
		ArgumentNullException.ThrowIfNull(service, "service");
		lock (_lock)
		{
			if (_connectionAuthorization == service)
			{
				service.Changed -= OnConnectionAuthorizationChanged;
				_connectionAuthorization = null;
			}
		}
	}

	public static bool TryAuthorizeNewConnection(out string reason)
	{
		StoreLicenseService connectionAuthorization;
		lock (_lock)
		{
			connectionAuthorization = _connectionAuthorization;
		}
		if (connectionAuthorization != null)
		{
			return connectionAuthorization.TryAuthorizeNewConnection(out reason);
		}
		reason = "The Microsoft Store license has not been initialized.";
		return false;
	}

	private static void OnConnectionAuthorizationChanged(object? sender, StoreLicenseSnapshot snapshot)
	{
		if (snapshot.AllowsNewConnections)
		{
			AirPlayStreamer instance;
			lock (_lock)
			{
				instance = _instance;
			}
			instance?.ResumeAuthorizedRecovery();
		}
	}

	public static void DisposeIfCreated()
	{
		AirPlayStreamer instance;
		lock (_lock)
		{
			instance = _instance;
			_instance = null;
		}
		instance?.Dispose();
	}

	private void PublishSynthesizedStatus(string line)
	{
		if (BackendEventText.TryParse(line, null, out BackendEvent evt))
		{
			PublishEventReceived(evt);
		}
		PublishLogReceived(line);
	}

	private void PublishEventReceived(BackendEvent evt)
	{
		Action<BackendEvent> action = this.EventReceived;
		if (action == null)
		{
			return;
		}
		Delegate[] invocationList = action.GetInvocationList();
		for (int i = 0; i < invocationList.Length; i++)
		{
			Action<BackendEvent> action2 = (Action<BackendEvent>)invocationList[i];
			try
			{
				action2(evt);
			}
			catch (Exception)
			{
			}
		}
	}

	private void PublishLogReceived(string line)
	{
		Action<string> action = this.LogReceived;
		if (action == null)
		{
			return;
		}
		Delegate[] invocationList = action.GetInvocationList();
		for (int i = 0; i < invocationList.Length; i++)
		{
			Action<string> action2 = (Action<string>)invocationList[i];
			try
			{
				action2(line);
			}
			catch (Exception)
			{
			}
		}
	}

	private AirPlayStreamer()
	{
	}

	internal AirPlayStreamer(Action<string> commandWriter, IEnumerable<string>? desiredDevices = null, IEnumerable<string>? desiredGroupIps = null)
	{
		_commandWriterForTesting = commandWriter;
		if (desiredDevices != null)
		{
			_desiredDevices.UnionWith(desiredDevices);
		}
		if (desiredGroupIps != null)
		{
			_desiredGroupIps = MergeGroupIps(null, desiredGroupIps);
			_desiredDevices.UnionWith(_desiredGroupIps);
		}
	}

	public void EnsureStarted()
	{
		lock (_lifecycleLock)
		{
			if (_disposing || IsConnected)
			{
				return;
			}
			bool flag;
			lock (_commandLock)
			{
				flag = _desiredDevices.Count > 0;
			}
			if (flag)
			{
				DemandConnectionAuthorization();
			}
			if (_backendProcess != null)
			{
				try
				{
					_backendProcess.Dispose();
				}
				catch
				{
				}
				_backendProcess = null;
			}
			ProcessStartInfo startInfo = BuildBackendStartInfo(FindBackendExecutable());
			Process process = new Process
			{
				StartInfo = startInfo,
				EnableRaisingEvents = true
			};
			_backendProcess = process;
			process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
			{
				if (e.Data != null)
				{
					PublishLogReceived(e.Data);
				}
			};
			ReaderBinding binding;
			lock (_commandLock)
			{
				_activeReaderBinding?.Cancel();
				_lastSubmittedAcousticOffsetRequestId = 0L;
				_lastSubmittedAcousticOffsetRequestId = 0L;
				_inFlightAcousticOffsetCommands.Clear();
				binding = new ReaderBinding(_currentSessionContext.TransportInstanceId, _currentSessionContext.Generation);
				_activeReaderBinding = binding;
			}
			process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
			{
				if (e.Data != null)
				{
					binding.OnLineReceived(e.Data, delegate(ReaderBinding b, string line)
					{
						ProcessBackendLine(line, b.TransportInstanceId, b);
					});
				}
			};
			process.Exited += delegate
			{
				OnBackendExited(process);
			};
			try
			{
				process.Start();
			}
			catch
			{
				lock (_commandLock)
				{
					binding.Cancel();
					if (_activeReaderBinding == binding)
					{
						_activeReaderBinding = null;
					}
					_lastSubmittedAcousticOffsetRequestId = 0L;
					_inFlightAcousticOffsetCommands.Clear();
				}
				throw;
			}
			lock (_commandLock)
			{
				if (CurrentSessionSnapshot.Context.TransportInstanceId == binding.TransportInstanceId)
				{
					binding.MarkActive(delegate(ReaderBinding b, string line)
					{
						ProcessBackendLine(line, b.TransportInstanceId, b);
					});
				}
				else
				{
					binding.Cancel();
				}
			}
			_backendStartedUtc = DateTime.UtcNow;
			process.BeginErrorReadLine();
			process.BeginOutputReadLine();
			if (process.HasExited)
			{
				lock (_commandLock)
				{
					binding.Cancel();
					if (_activeReaderBinding == binding)
					{
						_activeReaderBinding = null;
					}
				}
				throw new Exception($"AirPlay backend exited immediately (code {process.ExitCode}).");
			}
			(bool, int) tuple = SettingsManager.ReadConfig((AppConfig config) => (SilenceDetectionEnabled: config.SilenceDetectionEnabled, SilenceTimeoutMinutes: config.SilenceTimeoutMinutes));
			SetStandbyConfig(tuple.Item1, tuple.Item2);
			SetLatency(CurrentLatencyMs());
			SetCodec(SettingsManager.GetAudioCodec());
			WriteCommand($"SAMPLE_RATE {_preferredSampleRateHz}");
			ReplayDeveloperModeOverrides();
			ApplyCaptureSourceFromConfig();
			SyncEqStateFromConfig();
			ReplayDesiredState();
		}
	}

	internal void HandleBackendExitedForTesting()
	{
		lock (_commandLock)
		{
			_activeReaderBinding?.Cancel();
			_activeReaderBinding = null;
			_lastSubmittedAcousticOffsetRequestId = 0L;
			_inFlightAcousticOffsetCommands.Clear();
		}
	}

	private void OnBackendExited(Process process)
	{
		if (_disposing || _backendProcess != process)
		{
			return;
		}
		if (DateTime.UtcNow - _backendStartedUtc > TimeSpan.FromSeconds(30.0))
		{
			_consecutiveRestartAttempts = 0;
		}
		PublishSynthesizedStatus($"[Status] BACKEND_EXITED {SafeExitCode(process)}");
		string[] array;
		lock (_commandLock)
		{
			_activeReaderBinding?.Cancel();
			_activeReaderBinding = null;
			_lastSubmittedAcousticOffsetRequestId = 0L;
			_inFlightAcousticOffsetCommands.Clear();
			array = _desiredDevices.ToArray();
		}
		string[] array2 = array;
		foreach (string text in array2)
		{
			PublishSynthesizedStatus("[Status] RECONNECTING " + text);
		}
		if (!TryAuthorizeNewConnection(out string reason))
		{
			array2 = array;
			foreach (string text2 in array2)
			{
				PublishSynthesizedStatus("[Status] FAILED " + text2 + " " + reason);
			}
		}
		else
		{
			ScheduleBackendRestart();
		}
	}

	private static int SafeExitCode(Process process)
	{
		try
		{
			return process.ExitCode;
		}
		catch
		{
			return -1;
		}
	}

	private void ScheduleBackendRestart()
	{
		if (!TryAuthorizeNewConnection(out string _) || _disposing || Interlocked.Exchange(ref _restartScheduled, 1) != 0)
		{
			return;
		}
		int attempt = Interlocked.Increment(ref _consecutiveRestartAttempts);
		if (attempt > 5)
		{
			Interlocked.Exchange(ref _restartScheduled, 0);
			PublishSynthesizedStatus("[Status] BACKEND_RECOVERY_FAILED");
			string[] array;
			lock (_commandLock)
			{
				array = _desiredDevices.ToArray();
			}
			string[] array2 = array;
			foreach (string value in array2)
			{
				PublishSynthesizedStatus($"[Status] FAILED {value} Backend recovery exhausted after {5} attempts");
			}
			return;
		}
		int delaySeconds = Math.Min(15, 1 << Math.Min(attempt - 1, 4));
		Task.Run(async delegate
		{
			await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(continueOnCapturedContext: false);
			bool flag = false;
			try
			{
				if (_disposing || !TryAuthorizeNewConnection(out string _))
				{
					return;
				}
				EnsureStarted();
				PublishSynthesizedStatus($"[Status] BACKEND_RECOVERED {attempt}");
			}
			catch (Exception)
			{
				flag = true;
			}
			finally
			{
				Interlocked.Exchange(ref _restartScheduled, 0);
			}
			if (flag)
			{
				ScheduleBackendRestart();
			}
		});
	}

	private void ResumeAuthorizedRecovery()
	{
		if (_disposing || IsConnected)
		{
			return;
		}
		lock (_commandLock)
		{
			if (_desiredDevices.Count == 0)
			{
				return;
			}
		}
		ScheduleBackendRestart();
	}

	internal void ReplayDesiredState()
	{
		RefreshPlaybackProfile(SettingsManager.GetAudioCodec());
		BackendDeviceHint[] array;
		string[] array2;
		BackendEndpointSelection[] array3;
		int? sessionProfileLatencyMs;
		int? sessionCalibrationLatencyMs;
		bool calibrationCaptureIsolated;
		(string, BackendEndpointSelection)[] desired;
		lock (_commandLock)
		{
			array = _latestHints.Values.ToArray();
			array2 = _desiredGroupIps?.ToArray();
			array3 = _desiredGroupSelectors?.ToArray();
			sessionProfileLatencyMs = _sessionProfileLatencyMs;
			sessionCalibrationLatencyMs = _sessionCalibrationLatencyMs;
			calibrationCaptureIsolated = _calibrationCaptureIsolated;
			desired = BuildDesiredReplayTargets(_desiredDevices, _desiredSelectors.Values);
		}
		BackendDeviceHint[] array4 = array;
		foreach (BackendDeviceHint value in array4)
		{
			WriteCommand("DEVICE_HINT " + JsonSerializer.Serialize(value));
		}
		KeyValuePair<string, string>[] array5;
		lock (_commandLock)
		{
			array5 = _accessPasswords.ToArray();
		}
		KeyValuePair<string, string>[] array6 = array5;
		for (int i = 0; i < array6.Length; i++)
		{
			KeyValuePair<string, string> keyValuePair = array6[i];
			var (ip, password) = keyValuePair;
			WriteCommand(BuildAccessPasswordCommand(ip, password));
		}
		bool flag = SettingsManager.ReadConfig((AppConfig config) => config.AutomaticReceiverRecoveryEnabled);
		WriteCommand("AUTO_RECEIVER_RECOVERY " + (flag ? "true" : "false"));
		WriteCommand("PROFILE_LATENCY " + (sessionProfileLatencyMs.HasValue ? sessionProfileLatencyMs.Value.ToString() : "auto"));
		WriteCommand("CALIBRATION_LATENCY " + (sessionCalibrationLatencyMs.HasValue ? sessionCalibrationLatencyMs.Value.ToString() : "auto"));
		if (calibrationCaptureIsolated)
		{
			WriteCommand("CALIBRATION_TONE PAUSE");
		}
		if (array3 != null && array3.Length > 0)
		{
			WriteHintedGroupCommand(array3);
		}
		if (array2 != null && array2.Length > 0)
		{
			WriteIpGroupCommand(array2);
		}
		(string, BackendEndpointSelection)[] array7 = ReplayTargetsOutsideGroups(desired, array3, array2);
		for (int i = 0; i < array7.Length; i++)
		{
			(string, BackendEndpointSelection) tuple = array7[i];
			WriteAddCommand(tuple.Item1, tuple.Item2);
		}
	}

	internal static (string Ip, BackendEndpointSelection? Selector)[] ReplayTargetsOutsideGroups(IEnumerable<(string Ip, BackendEndpointSelection? Selector)> desired, IEnumerable<BackendEndpointSelection>? groupSelectors, IEnumerable<string>? groupIps)
	{
		HashSet<string> grouped = (groupIps ?? Array.Empty<string>()).Concat((groupSelectors ?? Array.Empty<BackendEndpointSelection>()).Select((BackendEndpointSelection selector) => selector.Ip)).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		return desired.Where<(string, BackendEndpointSelection)>(((string Ip, BackendEndpointSelection Selector) target) => !grouped.Contains(target.Ip)).ToArray();
	}

	internal static string[] MergeGroupIps(IEnumerable<string>? existing, IEnumerable<string> added)
	{
		return (existing ?? Array.Empty<string>()).Concat(added).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	internal static BackendEndpointSelection[] MergeGroupSelectors(IEnumerable<BackendEndpointSelection>? existing, IEnumerable<BackendEndpointSelection> added)
	{
		return (from @group in (existing ?? Array.Empty<BackendEndpointSelection>()).Concat(added).GroupBy<BackendEndpointSelection, string>(EndpointSelectorKey, StringComparer.OrdinalIgnoreCase)
			select @group.First()).ToArray();
	}

	public void AddDevice(string ip, string? instanceName = null, bool isRaop = false)
	{
		if (!TryNormalizeIp(ip, out string normalized))
		{
			return;
		}
		DemandConnectionAuthorization();
		BackendEndpointSelection backendEndpointSelection = null;
		if (!string.IsNullOrWhiteSpace(instanceName) && !ContainsCommandUnsafeChars(instanceName))
		{
			backendEndpointSelection = new BackendEndpointSelection(normalized, instanceName.Trim(), isRaop);
		}
		EnsureStarted();
		lock (_commandLock)
		{
			_desiredDevices.Add(normalized);
			if (backendEndpointSelection != null)
			{
				_desiredSelectors[EndpointSelectorKey(backendEndpointSelection)] = backendEndpointSelection;
			}
			else
			{
				RemoveDesiredSelectorsForIp(_desiredSelectors, normalized);
			}
		}
		WriteAddCommand(normalized, backendEndpointSelection);
	}

	private void WriteAddCommand(string ip, BackendEndpointSelection? selector)
	{
		if (selector == null)
		{
			WriteCommand("ADD " + ip);
			return;
		}
		string text = JsonSerializer.Serialize(selector);
		if (!ContainsCommandUnsafeChars(text))
		{
			WriteCommand("ADD_HINTED " + text);
		}
	}

	public void AddGroup(IEnumerable<string> ips)
	{
		List<string> list = new List<string>();
		foreach (string ip in ips)
		{
			if (TryNormalizeIp(ip, out string normalized))
			{
				list.Add(normalized);
			}
		}
		if (list.Count == 0)
		{
			return;
		}
		DemandConnectionAuthorization();
		EnsureStarted();
		lock (_commandLock)
		{
			string[] array = list.Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
			_desiredGroupIps = MergeGroupIps(_desiredGroupIps, array);
			string[] array2 = array;
			foreach (string item in array2)
			{
				_desiredDevices.Add(item);
			}
		}
		WriteIpGroupCommand(list);
	}

	public void AddGroup(IEnumerable<BackendEndpointSelection> endpoints)
	{
		string normalized;
		BackendEndpointSelection[] array = (from @group in (from endpoint in endpoints
				where endpoint != null && TryNormalizeIp(endpoint.Ip, out normalized) && !string.IsNullOrWhiteSpace(endpoint.InstanceName) && !ContainsCommandUnsafeChars(endpoint.InstanceName)
				select new BackendEndpointSelection(NormalizeIp(endpoint.Ip), endpoint.InstanceName.Trim(), endpoint.IsRaop)).GroupBy<BackendEndpointSelection, string>(EndpointSelectorKey, StringComparer.OrdinalIgnoreCase)
			select @group.First()).ToArray();
		if (array.Length == 0)
		{
			return;
		}
		DemandConnectionAuthorization();
		EnsureStarted();
		lock (_commandLock)
		{
			_desiredGroupSelectors = MergeGroupSelectors(_desiredGroupSelectors, array);
			BackendEndpointSelection[] array2 = array;
			foreach (BackendEndpointSelection backendEndpointSelection in array2)
			{
				_desiredDevices.Add(backendEndpointSelection.Ip);
			}
		}
		WriteHintedGroupCommand(array);
	}

	private void WriteIpGroupCommand(IEnumerable<string> ips)
	{
		string text = JsonSerializer.Serialize(ips.Distinct<string>(StringComparer.OrdinalIgnoreCase));
		if (!ContainsCommandUnsafeChars(text))
		{
			WriteCommand("ADD_GROUP " + text);
		}
	}

	private void WriteHintedGroupCommand(IEnumerable<BackendEndpointSelection> endpoints)
	{
		string text = JsonSerializer.Serialize(endpoints);
		if (!ContainsCommandUnsafeChars(text))
		{
			WriteCommand("ADD_GROUP_HINTED " + text);
		}
	}

	internal static string EndpointSelectorKey(BackendEndpointSelection endpoint)
	{
		return $"{endpoint.IsRaop}:{endpoint.Ip}:{endpoint.InstanceName.Trim()}";
	}

	internal static (string Ip, BackendEndpointSelection? Selector)[] BuildDesiredReplayTargets(IEnumerable<string> desiredDevices, IEnumerable<BackendEndpointSelection> desiredSelectors)
	{
		string[] source = desiredDevices.Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		HashSet<string> desiredIps = source.ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		BackendEndpointSelection[] source2 = (from @group in desiredSelectors.Where((BackendEndpointSelection selector) => desiredIps.Contains(selector.Ip)).GroupBy<BackendEndpointSelection, string>(EndpointSelectorKey, StringComparer.OrdinalIgnoreCase)
			select @group.First()).ToArray();
		HashSet<string> selectedIps = source2.Select((BackendEndpointSelection selector) => selector.Ip).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		return source2.Select((BackendEndpointSelection selector) => (Ip: selector.Ip, selector)).Concat<(string, BackendEndpointSelection)>(from ip in source
			where !selectedIps.Contains(ip)
			select ((string ip, BackendEndpointSelection))(ip: ip, null)).ToArray();
	}

	private static void RemoveDesiredSelectorsForIp(Dictionary<string, BackendEndpointSelection> selectors, string ip)
	{
		string[] array = (from entry in selectors
			where string.Equals(entry.Value.Ip, ip, StringComparison.OrdinalIgnoreCase)
			select entry.Key).ToArray();
		foreach (string key in array)
		{
			selectors.Remove(key);
		}
	}

	private static void DemandConnectionAuthorization()
	{
		if (!TryAuthorizeNewConnection(out string reason))
		{
			throw new InvalidOperationException(reason);
		}
	}

	private static string NormalizeIp(string ip)
	{
		if (!TryNormalizeIp(ip, out string normalized))
		{
			return string.Empty;
		}
		return normalized;
	}

	public void PublishDeviceHint(BackendDeviceHint hint)
	{
		if (hint.Addresses.Length == 0 || hint.Port <= 0 || string.IsNullOrWhiteSpace(hint.InstanceName) || ContainsCommandUnsafeChars(hint.InstanceName) || hint.Addresses.Any((string a) => string.IsNullOrWhiteSpace(a) || ContainsCommandUnsafeChars(a)))
		{
			return;
		}
		string text = JsonSerializer.Serialize(hint);
		if (!ContainsCommandUnsafeChars(text))
		{
			lock (_commandLock)
			{
				string key = $"{hint.IsRaop}:{hint.InstanceName}";
				_latestHints[key] = hint;
			}
			if (IsConnected)
			{
				WriteCommand("DEVICE_HINT " + text);
			}
		}
	}

	internal static bool ShouldRemoveAllDevices(IEnumerable<string> desiredDevices, IEnumerable<string> removalTargets)
	{
		HashSet<string> hashSet = desiredDevices.ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> other = removalTargets.ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (hashSet.Count > 0)
		{
			return hashSet.IsSubsetOf(other);
		}
		return false;
	}

	public void RemoveDevices(IEnumerable<string> ips)
	{
		string normalized;
		string[] array = (from ip2 in ips
			select (!TryNormalizeIp(ip2, out normalized)) ? null : normalized into text
			where text != null
			select text).Cast<string>().Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		if (array.Length == 0)
		{
			return;
		}
		lock (_commandLock)
		{
			if (ShouldRemoveAllDevices(_desiredDevices, array))
			{
				RemoveAllDevices();
				return;
			}
			string[] array2 = array;
			foreach (string ip in array2)
			{
				RemoveDevice(ip);
			}
		}
	}

	public void RemoveDevice(string ip)
	{
		if (!TryNormalizeIp(ip, out string safeIp))
		{
			return;
		}
		lock (_commandLock)
		{
			_desiredDevices.Remove(safeIp);
			RemoveDesiredSelectorsForIp(_desiredSelectors, safeIp);
			_desiredGroupIps = _desiredGroupIps?.Where((string a) => !string.Equals(a, safeIp, StringComparison.OrdinalIgnoreCase)).ToArray();
			_desiredGroupSelectors = _desiredGroupSelectors?.Where((BackendEndpointSelection endpoint) => !string.Equals(endpoint.Ip, safeIp, StringComparison.OrdinalIgnoreCase)).ToArray();
		}
		WriteCommand("REMOVE " + safeIp);
	}

	public void RemoveDevice(BackendEndpointSelection endpoint)
	{
		if (!TryNormalizeIp(endpoint.Ip, out string safeIp) || string.IsNullOrWhiteSpace(endpoint.InstanceName) || ContainsCommandUnsafeChars(endpoint.InstanceName))
		{
			return;
		}
		BackendEndpointSelection backendEndpointSelection = new BackendEndpointSelection(safeIp, endpoint.InstanceName.Trim(), endpoint.IsRaop);
		string key = EndpointSelectorKey(backendEndpointSelection);
		lock (_commandLock)
		{
			_desiredSelectors.Remove(key);
			_desiredGroupSelectors = _desiredGroupSelectors?.Where((BackendEndpointSelection candidate) => !string.Equals(EndpointSelectorKey(candidate), key, StringComparison.OrdinalIgnoreCase)).ToArray();
			bool num = _desiredSelectors.Values.Any((BackendEndpointSelection candidate) => string.Equals(candidate.Ip, safeIp, StringComparison.OrdinalIgnoreCase));
			bool flag = _desiredGroupSelectors?.Any((BackendEndpointSelection candidate) => string.Equals(candidate.Ip, safeIp, StringComparison.OrdinalIgnoreCase)) ?? false;
			if (!num && !flag)
			{
				_desiredDevices.Remove(safeIp);
			}
		}
		string text = JsonSerializer.Serialize(backendEndpointSelection);
		if (!ContainsCommandUnsafeChars(text))
		{
			WriteCommand("REMOVE_HINTED " + text);
		}
	}

	public void RemoveAllDevices()
	{
		lock (_commandLock)
		{
			_desiredDevices.Clear();
			_desiredSelectors.Clear();
			_desiredGroupIps = null;
			_desiredGroupSelectors = null;
			_sessionProfileLatencyMs = null;
			_playbackProfileDevices = null;
			_sessionCalibrationLatencyMs = null;
			_calibrationCaptureIsolated = false;
		}
		WriteCommand("REMOVE_ALL");
	}

	public void SetVolume(string ip, int volume)
	{
		if (TryNormalizeIp(ip, out string normalized))
		{
			volume = Math.Clamp(volume, 0, 100);
			QueueVolumeCommand(normalized, volume, $"VOL {normalized} {volume}");
		}
	}

	public void SetVolume(BackendEndpointSelection endpoint, int volume)
	{
		if (TryNormalizeIp(endpoint.Ip, out string normalized) && !string.IsNullOrWhiteSpace(endpoint.InstanceName) && !ContainsCommandUnsafeChars(endpoint.InstanceName))
		{
			volume = Math.Clamp(volume, 0, 100);
			BackendEndpointSelection backendEndpointSelection = new BackendEndpointSelection(normalized, endpoint.InstanceName.Trim(), endpoint.IsRaop);
			string text = JsonSerializer.Serialize(new object[2] { backendEndpointSelection, volume });
			if (!ContainsCommandUnsafeChars(text))
			{
				QueueVolumeCommand("endpoint:" + EndpointSelectorKey(backendEndpointSelection), volume, "VOL_HINTED " + text);
			}
		}
	}

	private void QueueVolumeCommand(string key, int volume, string command)
	{
		lock (_volumeLock)
		{
			if (_lastSentVolume.TryGetValue(key, out var value) && value == volume && !_pendingVolumes.ContainsKey(key))
			{
				return;
			}
		}
		bool flag = false;
		lock (_volumeLock)
		{
			_pendingVolumes[key] = volume;
			_pendingVolumeCommands[key] = command;
			_lastSentVolume[key] = volume;
			DateTime utcNow = DateTime.UtcNow;
			_lastVolumeSentUtc.TryGetValue(key, out var value2);
			TimeSpan timeSpan = utcNow - value2;
			if (!_volumeTimers.ContainsKey(key) && timeSpan >= VolumeCommandInterval)
			{
				_pendingVolumes.Remove(key);
				_pendingVolumeCommands.Remove(key);
				_lastVolumeSentUtc[key] = utcNow;
				flag = true;
			}
			else if (!_volumeTimers.ContainsKey(key))
			{
				TimeSpan dueTime = VolumeCommandInterval - timeSpan;
				_volumeTimers[key] = new Timer(delegate
				{
					FlushPendingVolume(key);
				}, null, dueTime, Timeout.InfiniteTimeSpan);
			}
		}
		if (flag)
		{
			WriteCommand(command);
		}
	}

	private void FlushPendingVolume(string key)
	{
		string command;
		lock (_volumeLock)
		{
			if (_volumeTimers.Remove(key, out Timer value))
			{
				value.Dispose();
			}
			if (!_pendingVolumes.Remove(key, out var value2) || !_pendingVolumeCommands.Remove(key, out string value3))
			{
				return;
			}
			command = value3;
			_lastSentVolume[key] = value2;
			_lastVolumeSentUtc[key] = DateTime.UtcNow;
		}
		WriteCommand(command);
	}

	public void SetLatency(int milliseconds)
	{
		WriteCommand($"LATENCY {milliseconds}");
	}

	public void SetDevPtpLatency(string mode, int? ms)
	{
		string text = mode?.Trim().ToUpperInvariant() switch
		{
			"SOLO" => "SOLO", 
			"GROUP" => "GROUP", 
			"MIXED" => "MIXED", 
			_ => string.Empty, 
		};
		if (text.Length != 0)
		{
			string text2 = (ms.HasValue ? Math.Clamp(ms.Value, 0, 1000).ToString(CultureInfo.InvariantCulture) : "auto");
			WriteCommand("DEV_PTP_LATENCY " + text + " " + text2);
		}
	}

	public void SetDevPtpFilter(string mode)
	{
		string text;
		switch (mode?.Trim().ToLowerInvariant())
		{
		case "log":
			text = "log";
			break;
		case "on":
		case "true":
		case "1":
			text = "on";
			break;
		default:
			text = "off";
			break;
		}
		string text2 = text;
		WriteCommand("DEV_PTP_FILTER " + text2);
	}

	public void SetCalibrationTone(bool enabled)
	{
		lock (_commandLock)
		{
			_calibrationCaptureIsolated = enabled;
		}
		WriteCommand("CALIBRATION_TONE " + (enabled ? "START" : "STOP"));
	}

	public void PauseCalibrationTone()
	{
		lock (_commandLock)
		{
			_calibrationCaptureIsolated = true;
		}
		WriteCommand("CALIBRATION_TONE PAUSE");
	}

	public int CurrentPlayoutLatencyMs()
	{
		lock (_commandLock)
		{
			return _sessionCalibrationLatencyMs ?? _sessionProfileLatencyMs ?? 55;
		}
	}

	internal void ProcessBackendLine(string rawLine, Guid sourceTransportInstanceId, ReaderBinding? binding = null)
	{
		BackendEvent backendEvent = null;
		if (BackendEvent.TryParse(rawLine, out BackendEvent evt))
		{
			backendEvent = evt;
			rawLine = evt.ToLegacyRawLine();
		}
		TryStripTimestamp(rawLine, out string cleanLine, out long? timestampNs);
		_lastEventTimestampNs = timestampNs;
		int? num = null;
		BackendBuildInfo info;
		if (cleanLine.StartsWith("[Engine] PROTOCOL ", StringComparison.Ordinal))
		{
			if (int.TryParse(cleanLine.Substring(18).Trim(), out var result))
			{
				_backendProtocolVersion = result;
				num = result;
			}
		}
		else if (BackendBuildInfo.TryParse(cleanLine, out info))
		{
			_backendBuild = info;
		}
		if (cleanLine.StartsWith("[MediaCommand] ", StringComparison.Ordinal) && SettingsManager.ReadConfig((AppConfig config) => config.ReceiverMediaControlEnabled))
		{
			WindowsMediaSessionService.Instance.TryHandleMediaCommandAsync(cleanLine);
		}
		bool flag = false;
		lock (_commandLock)
		{
			StreamerSessionContext context = CurrentSessionSnapshot.Context;
			if (sourceTransportInstanceId == Guid.Empty || sourceTransportInstanceId != context.TransportInstanceId || (_activeReaderBinding != null && (!_activeReaderBinding.IsActive || _activeReaderBinding.TransportInstanceId != sourceTransportInstanceId)) || (binding != null && (!binding.IsActive || (_activeReaderBinding != null && binding != _activeReaderBinding))))
			{
				flag = true;
			}
		}
		if (flag)
		{
			PublishLogReceived(rawLine);
			return;
		}
		if (num >= 3)
		{
			WriteCommand("IPC_FORMAT JSON 3");
		}
		if (cleanLine.StartsWith("[Diag] SUMMARY ", StringComparison.Ordinal))
		{
			TaskCompletionSource<string> pendingDiagSummary;
			lock (_commandLock)
			{
				pendingDiagSummary = _pendingDiagSummary;
				_pendingDiagSummary = null;
			}
			pendingDiagSummary?.TrySetResult(cleanLine.Substring("[Diag] SUMMARY ".Length).Trim());
		}
		if (cleanLine.StartsWith("[Diag] NETWORK ", StringComparison.Ordinal))
		{
			CompleteNetworkCheck(cleanLine.Substring("[Diag] NETWORK ".Length).Trim());
		}
		if (cleanLine.StartsWith("[Latency] RTX ", StringComparison.Ordinal))
		{
			string[] array = cleanLine.Substring("[Latency] RTX ".Length).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (array.Length >= 4 && ulong.TryParse(array[1], out var result2) && ulong.TryParse(array[2], out var result3) && ulong.TryParse(array[3], out var result4))
			{
				RtxTelemetryEvent obj = new RtxTelemetryEvent(array[0], result2, result3, result4, timestampNs);
				this.RtxTelemetryReceived?.Invoke(obj);
			}
		}
		else if (cleanLine.StartsWith("[Latency] RTX_REQ ", StringComparison.Ordinal))
		{
			string[] array2 = cleanLine.Substring("[Latency] RTX_REQ ".Length).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (array2.Length >= 3 && ushort.TryParse(array2[1], out var result5) && ushort.TryParse(array2[2], out var result6))
			{
				RtxDetailEvent obj2 = new RtxDetailEvent(array2[0], result5, result6, timestampNs);
				this.RtxDetailReceived?.Invoke(obj2);
			}
		}
		if (cleanLine.StartsWith("ACOUSTIC_OFFSET_ACCEPTED ", StringComparison.Ordinal))
		{
			string[] array3 = cleanLine.Substring("ACOUSTIC_OFFSET_ACCEPTED ".Length).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			int result9;
			if (array3.Length >= 2 && ulong.TryParse(array3[0], out var result7) && int.TryParse(array3[1], out var result8))
			{
				lock (_commandLock)
				{
					if (_currentSessionContext.ProtocolMode == StreamerProtocolMode.GenerationAware && result7 == _currentSessionContext.Generation)
					{
						_sessionAcousticOffsetAcceptedMs = result8;
					}
				}
			}
			else if (array3.Length == 1 && int.TryParse(array3[0], out result9))
			{
				lock (_commandLock)
				{
					if (_currentSessionContext.ProtocolMode == StreamerProtocolMode.LegacySingleSession)
					{
						_sessionAcousticOffsetAcceptedMs = result9;
					}
				}
			}
		}
		else if (cleanLine.StartsWith("ACOUSTIC_OFFSET_APPLIED ", StringComparison.Ordinal))
		{
			string[] array4 = cleanLine.Substring("ACOUSTIC_OFFSET_APPLIED ".Length).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			int? num2 = null;
			ulong? num3 = null;
			Guid? guid = null;
			StreamerProtocolMode? streamerProtocolMode = null;
			SessionSnapshot sessionSnapshot = null;
			BindingToken token = null;
			long num4 = 0L;
			lock (_commandLock)
			{
				int result13;
				if (array4.Length >= 2 && ulong.TryParse(array4[0], out var result10) && int.TryParse(array4[1], out var result11))
				{
					num4 = ((array4.Length < 3 || !long.TryParse(array4[2], out var result12)) ? _lastSubmittedAcousticOffsetRequestId : result12);
					InFlightAcousticOffsetCommand value = null;
					if (num4 > 0)
					{
						_inFlightAcousticOffsetCommands.TryGetValue(num4, out value);
						_inFlightAcousticOffsetCommands.Remove(num4);
					}
					BindingToken bindingToken = ((value != null) ? value.Token : ((num4 == 0L) ? Volatile.Read(in _playoutState).ActiveBindingToken : BindingToken.Empty));
					SessionSnapshot sessionSnapshot2 = value?.Snapshot ?? CurrentSessionSnapshot;
					if (_currentSessionContext.ProtocolMode == StreamerProtocolMode.GenerationAware && result10 == _currentSessionContext.Generation)
					{
						_sessionAcousticOffsetAppliedMs = result11;
						SessionSnapshot currentSessionSnapshot = CurrentSessionSnapshot;
						StreamerPlayoutState streamerPlayoutState;
						StreamerPlayoutState value2;
						do
						{
							streamerPlayoutState = Volatile.Read(in _playoutState);
							if (!streamerPlayoutState.Snapshot.Equals(currentSessionSnapshot) || streamerPlayoutState.ActiveBindingToken.IsEmpty)
							{
								break;
							}
							value2 = streamerPlayoutState with
							{
								AppliedOffsetMs = result11
							};
						}
						while ((object)Interlocked.CompareExchange(ref _playoutState, value2, streamerPlayoutState) != streamerPlayoutState);
						num2 = result11;
						num3 = result10;
						guid = _currentSessionContext.TransportInstanceId;
						streamerProtocolMode = _currentSessionContext.ProtocolMode;
						sessionSnapshot = CurrentSessionSnapshot;
						token = bindingToken;
						sessionSnapshot = sessionSnapshot2;
					}
				}
				else if (array4.Length == 1 && int.TryParse(array4[0], out result13) && _currentSessionContext.ProtocolMode == StreamerProtocolMode.LegacySingleSession)
				{
					_sessionAcousticOffsetAppliedMs = result13;
					StreamerPlayoutState streamerPlayoutState2;
					StreamerPlayoutState value3;
					do
					{
						streamerPlayoutState2 = Volatile.Read(in _playoutState);
						value3 = streamerPlayoutState2 with
						{
							AppliedOffsetMs = result13
						};
					}
					while ((object)Interlocked.CompareExchange(ref _playoutState, value3, streamerPlayoutState2) != streamerPlayoutState2);
					num2 = result13;
					num3 = 0uL;
					guid = _currentSessionContext.TransportInstanceId;
					streamerProtocolMode = _currentSessionContext.ProtocolMode;
					sessionSnapshot = CurrentSessionSnapshot;
					token = Volatile.Read(in _playoutState).ActiveBindingToken;
				}
			}
			BeforeAcousticOffsetAppliedInvokeForTesting?.Invoke();
			if (num2.HasValue && num3.HasValue && guid.HasValue && streamerProtocolMode.HasValue && sessionSnapshot != null)
			{
				DispatchAppliedEvent(streamerProtocolMode.Value, num3.Value, num2.Value, guid.Value, sessionSnapshot, token, num4);
			}
		}
		if (backendEvent == null && BackendEventText.TryParse(cleanLine, timestampNs, out BackendEvent evt2))
		{
			backendEvent = evt2;
		}
		if (backendEvent != null)
		{
			PublishEventReceived(backendEvent);
		}
		PublishLogReceived(cleanLine);
	}

	private void DispatchAppliedEvent(StreamerProtocolMode mode, ulong gen, int offsetMs, Guid transportId, SessionSnapshot snapshot, BindingToken? token = null, long requestId = 0L)
	{
		AcousticOffsetAppliedEvent obj = new AcousticOffsetAppliedEvent(snapshot, offsetMs, requestId, token);
		switch (mode)
		{
		case StreamerProtocolMode.GenerationAware:
		{
			if (!Volatile.Read(in _playoutState).Snapshot.Equals(snapshot))
			{
				break;
			}
			Delegate[] array3 = this.AcousticOffsetAppliedWithContext?.GetInvocationList();
			Delegate[] array2;
			if (array3 != null)
			{
				array2 = array3;
				for (int i = 0; i < array2.Length; i++)
				{
					Action<AcousticOffsetAppliedEvent> action2 = (Action<AcousticOffsetAppliedEvent>)array2[i];
					bool flag2 = false;
					lock (_commandLock)
					{
						flag2 = gen == _currentSessionContext.Generation && transportId == _currentSessionContext.TransportInstanceId && _currentSessionContext.ProtocolMode == StreamerProtocolMode.GenerationAware;
					}
					if (!flag2)
					{
						break;
					}
					action2(obj);
				}
			}
			Delegate[] array4 = this.AcousticOffsetAppliedWithGeneration?.GetInvocationList();
			if (array4 == null)
			{
				break;
			}
			array2 = array4;
			for (int i = 0; i < array2.Length; i++)
			{
				Action<ulong, int> action3 = (Action<ulong, int>)array2[i];
				bool flag3 = false;
				lock (_commandLock)
				{
					flag3 = gen == _currentSessionContext.Generation && transportId == _currentSessionContext.TransportInstanceId && _currentSessionContext.ProtocolMode == StreamerProtocolMode.GenerationAware;
				}
				if (flag3)
				{
					action3(gen, offsetMs);
					continue;
				}
				break;
			}
			break;
		}
		case StreamerProtocolMode.LegacySingleSession:
		{
			Delegate[] array = this.AcousticOffsetApplied?.GetInvocationList();
			if (array == null)
			{
				break;
			}
			Delegate[] array2 = array;
			for (int i = 0; i < array2.Length; i++)
			{
				Action<int> action = (Action<int>)array2[i];
				bool flag = false;
				lock (_commandLock)
				{
					flag = transportId == _currentSessionContext.TransportInstanceId && _currentSessionContext.ProtocolMode == StreamerProtocolMode.LegacySingleSession;
				}
				if (flag)
				{
					action(offsetMs);
					continue;
				}
				break;
			}
			break;
		}
		}
	}

	internal void ResetAcousticOffsetStateForTesting(ulong? generation = 1uL, StreamerProtocolMode mode = StreamerProtocolMode.GenerationAware, bool createActiveBinding = true)
	{
		List<IGenerationAwareOffsetTarget> list = null;
		lock (_commandLock)
		{
			PreCommitFenceForTesting = null;
			BeforeAcousticOffsetAppliedInvokeForTesting = null;
			OnInFlightCommandRegisteredForTesting = null;
			_sessionAcousticOffsetMs = 0;
			_sessionAcousticOffsetAcceptedMs = null;
			_sessionAcousticOffsetAppliedMs = null;
			_activeReaderBinding?.Cancel();
			_activeReaderBinding = null;
			_inFlightAcousticOffsetCommands.Clear();
			StreamerPlayoutState streamerPlayoutState;
			StreamerPlayoutState value;
			do
			{
				streamerPlayoutState = Volatile.Read(in _playoutState);
				StreamerSessionContext streamerSessionContext = new StreamerSessionContext(generation ?? (streamerPlayoutState.Snapshot.Context.Generation + 1), mode, Guid.NewGuid());
				SessionSnapshot snapshot = new SessionSnapshot(streamerSessionContext, streamerPlayoutState.Snapshot.Epoch + 1);
				BindingToken activeBindingToken = (createActiveBinding ? new BindingToken(Guid.NewGuid(), streamerSessionContext.TransportInstanceId) : BindingToken.Empty);
				value = new StreamerPlayoutState(snapshot, activeBindingToken, null);
			}
			while ((object)Interlocked.CompareExchange(ref _playoutState, value, streamerPlayoutState) != streamerPlayoutState);
			List<IGenerationAwareOffsetTarget> list2 = new List<IGenerationAwareOffsetTarget>(_registeredOffsetTargets.Count);
			for (int num = _registeredOffsetTargets.Count - 1; num >= 0; num--)
			{
				if (_registeredOffsetTargets[num].TryGetTarget(out IGenerationAwareOffsetTarget target))
				{
					list2.Add(target);
				}
			}
			_registeredOffsetTargets.Clear();
			list = list2;
		}
		if (list == null)
		{
			return;
		}
		foreach (IGenerationAwareOffsetTarget item in list)
		{
			item.InvalidateSession();
		}
	}

	internal void ResetSessionOnlyForTesting(ulong? generation = 1uL, StreamerProtocolMode mode = StreamerProtocolMode.GenerationAware)
	{
		ResetAcousticOffsetStateForTesting(generation, mode, createActiveBinding: false);
	}

	public void SetRtxDetail(bool enabled)
	{
		WriteCommand(enabled ? "CALIBRATION_RTX_DETAIL ON" : "CALIBRATION_RTX_DETAIL OFF");
	}

	public void CancelActiveBinding(BindingToken? expectedToken = null)
	{
		StreamerPlayoutState streamerPlayoutState;
		StreamerPlayoutState value;
		do
		{
			streamerPlayoutState = Volatile.Read(in _playoutState);
			if (!streamerPlayoutState.ActiveBindingToken.IsEmpty && (!(expectedToken != null) || streamerPlayoutState.ActiveBindingToken.Equals(expectedToken)))
			{
				lock (_commandLock)
				{
					_inFlightAcousticOffsetCommands.Clear();
				}
				value = streamerPlayoutState with
				{
					ActiveBindingToken = BindingToken.Empty
				};
				continue;
			}
			break;
		}
		while ((object)Interlocked.CompareExchange(ref _playoutState, value, streamerPlayoutState) != streamerPlayoutState);
	}

	internal void SetActiveReaderBindingForTesting(ReaderBinding? binding)
	{
		lock (_commandLock)
		{
			_activeReaderBinding = binding;
			_inFlightAcousticOffsetCommands.Clear();
			BindingToken activeBindingToken = ((binding != null) ? binding.Token : BindingToken.Empty);
			StreamerPlayoutState streamerPlayoutState;
			StreamerPlayoutState value;
			do
			{
				streamerPlayoutState = Volatile.Read(in _playoutState);
				value = streamerPlayoutState with
				{
					ActiveBindingToken = activeBindingToken
				};
			}
			while ((object)Interlocked.CompareExchange(ref _playoutState, value, streamerPlayoutState) != streamerPlayoutState);
		}
	}

	public void RegisterOffsetTarget(IGenerationAwareOffsetTarget target)
	{
		lock (_commandLock)
		{
			target.BindSession(CurrentSessionSnapshot);
			_registeredOffsetTargets.Add(new WeakReference<IGenerationAwareOffsetTarget>(target));
		}
	}

	public bool TryApplyAcousticOffset(AcousticOffsetAppliedEvent evt, BindingToken? expectedToken = null)
	{
		if ((object)expectedToken == null || expectedToken.IsEmpty)
		{
			return false;
		}
		StreamerPlayoutState streamerPlayoutState;
		StreamerPlayoutState value;
		do
		{
			streamerPlayoutState = Volatile.Read(in _playoutState);
			if (!streamerPlayoutState.Snapshot.Equals(evt.Snapshot))
			{
				return false;
			}
			if (streamerPlayoutState.Snapshot.Context.ProtocolMode != StreamerProtocolMode.GenerationAware)
			{
				return false;
			}
			if (PreCommitFenceForTesting != null)
			{
				PreCommitFenceForTesting.Wait(1000);
			}
			if (streamerPlayoutState.ActiveBindingToken.IsEmpty || streamerPlayoutState.ActiveBindingToken.TransportInstanceId != evt.Snapshot.Context.TransportInstanceId)
			{
				return false;
			}
			if (expectedToken != null && !streamerPlayoutState.ActiveBindingToken.Equals(expectedToken))
			{
				return false;
			}
			value = streamerPlayoutState with
			{
				AppliedOffsetMs = evt.OffsetMs
			};
		}
		while ((object)Interlocked.CompareExchange(ref _playoutState, value, streamerPlayoutState) != streamerPlayoutState);
		return true;
	}

	public bool TryApplyAcousticOffset(AcousticOffsetAppliedEvent evt, IGenerationAwareOffsetTarget? target)
	{
		if (target == null || !target.CanApply(evt))
		{
			return false;
		}
		BindingToken bindingToken = (target as GenerationAwareOffsetState)?.BoundToken;
		if ((object)bindingToken == null || bindingToken.IsEmpty)
		{
			return false;
		}
		return TryApplyAcousticOffset(evt, bindingToken);
	}

	public void InitializeProtocolMode(StreamerProtocolMode mode)
	{
		lock (_commandLock)
		{
			SessionSnapshot currentSessionSnapshot = CurrentSessionSnapshot;
			if (currentSessionSnapshot.Context.ProtocolMode != StreamerProtocolMode.Unknown && currentSessionSnapshot.Context.ProtocolMode != mode)
			{
				throw new InvalidOperationException($"Cannot switch protocol mode from {currentSessionSnapshot.Context.ProtocolMode} to {mode} mid-session");
			}
			StreamerSessionContext context = currentSessionSnapshot.Context with
			{
				ProtocolMode = mode
			};
			SessionSnapshot snapshot = currentSessionSnapshot with
			{
				Context = context
			};
			Volatile.Write(ref _playoutState, _playoutState with
			{
				Snapshot = snapshot
			});
		}
	}

	public int CurrentAcousticOffsetMs()
	{
		lock (_commandLock)
		{
			return _sessionAcousticOffsetMs;
		}
	}

	public static long NextAcousticOffsetRequestId()
	{
		return Interlocked.Increment(ref _globalAcousticOffsetRequestId);
	}

	public void SetAcousticOffset(int offsetMs, long requestId = 0L)
	{
		int num = AcousticOffsetConfig.Clamp(offsetMs);
		lock (_commandLock)
		{
			if (_sessionAcousticOffsetMs == num && _lastSubmittedAcousticOffsetRequestId == requestId)
			{
				return;
			}
			_sessionAcousticOffsetMs = num;
			_lastSubmittedAcousticOffsetRequestId = requestId;
			if (requestId > 0)
			{
				SessionSnapshot currentSessionSnapshot = CurrentSessionSnapshot;
				BindingToken activeBindingToken = _playoutState.ActiveBindingToken;
				InFlightAcousticOffsetCommand inFlightAcousticOffsetCommand = new InFlightAcousticOffsetCommand(requestId, num, currentSessionSnapshot, activeBindingToken);
				_inFlightAcousticOffsetCommands[requestId] = inFlightAcousticOffsetCommand;
				OnInFlightCommandRegisteredForTesting?.Invoke(inFlightAcousticOffsetCommand);
			}
			string value = ((requestId > 0) ? ("ACOUSTIC_OFFSET " + num + " " + requestId) : ("ACOUSTIC_OFFSET " + num));
			Process backendProcess = _backendProcess;
			if (!ProcessIsAlive(backendProcess))
			{
				return;
			}
			try
			{
				backendProcess.StandardInput.WriteLine(value);
				backendProcess.StandardInput.Flush();
			}
			catch (Exception)
			{
				if (requestId > 0)
				{
					_inFlightAcousticOffsetCommands.Remove(requestId);
				}
				ScheduleBackendRestart();
			}
		}
	}

	public void SetCalibrationLatency(int? milliseconds, int slewMsPerSync = 0)
	{
		int? sessionCalibrationLatencyMs = (milliseconds.HasValue ? new int?(Math.Clamp(milliseconds.Value, 0, 1000)) : ((int?)null));
		lock (_commandLock)
		{
			_sessionCalibrationLatencyMs = sessionCalibrationLatencyMs;
		}
		string text = (sessionCalibrationLatencyMs.HasValue ? sessionCalibrationLatencyMs.Value.ToString() : "auto");
		if (slewMsPerSync > 0)
		{
			WriteCommand($"CALIBRATION_LATENCY {text} {slewMsPerSync}");
		}
		else
		{
			WriteCommand("CALIBRATION_LATENCY " + text);
		}
	}

	public void SetProfileLatency(int? milliseconds)
	{
		int? sessionProfileLatencyMs = (milliseconds.HasValue ? new int?(Math.Clamp(milliseconds.Value, 0, 1000)) : ((int?)null));
		lock (_commandLock)
		{
			_playbackProfileDevices = null;
			_sessionProfileLatencyMs = sessionProfileLatencyMs;
		}
		string text = (sessionProfileLatencyMs.HasValue ? sessionProfileLatencyMs.Value.ToString() : "auto");
		WriteCommand("PROFILE_LATENCY " + text);
	}

	public void SetPlaybackProfile(IReadOnlyCollection<CalibrationDeviceIdentity> devices)
	{
		lock (_commandLock)
		{
			_playbackProfileDevices = devices.ToArray();
		}
		RefreshPlaybackProfile(SettingsManager.GetAudioCodec());
	}

	private void RefreshPlaybackProfile(string codec)
	{
		CalibrationDeviceIdentity[] playbackProfileDevices;
		lock (_commandLock)
		{
			playbackProfileDevices = _playbackProfileDevices;
		}
		if (playbackProfileDevices == null)
		{
			return;
		}
		int? savedLatencyForDevices = SettingsManager.GetSavedLatencyForDevices((IReadOnlyCollection<CalibrationDeviceIdentity>)(object)playbackProfileDevices, codec);
		lock (_commandLock)
		{
			if (playbackProfileDevices == _playbackProfileDevices)
			{
				_sessionProfileLatencyMs = savedLatencyForDevices;
				WriteCommand("PROFILE_LATENCY " + (savedLatencyForDevices.HasValue ? savedLatencyForDevices.Value.ToString() : "auto"));
			}
		}
	}

	public void RequestCalibrationSnapshot()
	{
		WriteCommand("CALIBRATION_SNAPSHOT");
	}

	private int _preferredSampleRateHz = 44100;

	public void SetSampleRate(int hz)
	{
		_preferredSampleRateHz = hz == 48000 ? 48000 : 44100;
		if (IsConnected)
		{
			WriteCommand($"SAMPLE_RATE {_preferredSampleRateHz}");
		}
	}

	public void SetCodec(string codec)
	{
		string text = AudioCodecPreference.Normalize(codec);
		RefreshPlaybackProfile(text);
		WriteCommand("CODEC " + text);
	}

	public void ConfirmCodecSwitch(string ip, bool accept)
	{
		if (TryNormalizeIp(ip, out string normalized))
		{
			if (accept)
			{
				RefreshPlaybackProfile(SettingsManager.GetAudioCodec());
			}
			WriteCommand("CODEC_CONFIRM " + normalized + " " + (accept ? "ACCEPT" : "DECLINE"));
		}
	}

	public async Task<string?> RequestDiagnosticsSummaryAsync(TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsConnected || _backendProtocolVersion < 2)
		{
			return null;
		}
		TaskCompletionSource<string> pending;
		lock (_commandLock)
		{
			if (_pendingDiagSummary == null)
			{
				_pendingDiagSummary = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			}
			pending = _pendingDiagSummary;
		}
		WriteCommand("DIAG_SUMMARY");
		return (await Task.WhenAny(pending.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(continueOnCapturedContext: false) == pending.Task) ? pending.Task.Result : null;
	}

	public async Task<string?> RequestNetworkCheckAsync(IReadOnlyCollection<string> receiverIps, TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsConnected)
		{
			try
			{
				EnsureStarted();
			}
			catch (Exception ex) when (!(ex is OperationCanceledException))
			{
				return null;
			}
		}
		if (!IsConnected)
		{
			return null;
		}
		if (!(await WaitForProtocolAsync(() => Volatile.Read(in _backendProtocolVersion), 3, NetworkCheckBackendStartTimeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)))
		{
			return null;
		}
		List<string> list = new List<string>(receiverIps.Count);
		foreach (string receiverIp in receiverIps)
		{
			if (!TryNormalizeIp(receiverIp, out string normalized))
			{
				return null;
			}
			list.Add(normalized);
		}
		string key = ((list.Count == 0) ? null : NetworkCheckResult.ReceiverKey(list));
		if (key == null)
		{
			return null;
		}
		bool flag;
		TaskCompletionSource<string> pending;
		lock (_commandLock)
		{
			flag = !_pendingNetworkChecks.TryGetValue(key, out TaskCompletionSource<string> value);
			pending = value ?? new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			_pendingNetworkChecks[key] = pending;
		}
		if (flag)
		{
			WriteCommand("NETWORK_CHECK " + string.Join(' ', list));
		}
		try
		{
			return (await Task.WhenAny(pending.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(continueOnCapturedContext: false) == pending.Task) ? pending.Task.Result : null;
		}
		finally
		{
			lock (_commandLock)
			{
				if (_pendingNetworkChecks.TryGetValue(key, out TaskCompletionSource<string> value2) && value2 == pending && !pending.Task.IsCompleted)
				{
					_pendingNetworkChecks.Remove(key);
				}
			}
		}
	}

	internal static async Task<bool> WaitForProtocolAsync(Func<int> version, int minimum, TimeSpan timeout, CancellationToken cancellationToken)
	{
		Stopwatch waited = Stopwatch.StartNew();
		while (version() < minimum)
		{
			if (waited.Elapsed >= timeout)
			{
				return false;
			}
			await Task.Delay(50, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		return true;
	}

	private void CompleteNetworkCheck(string json)
	{
		NetworkCheckResult networkCheckResult = NetworkCheckResult.TryParse(json);
		string text = ((networkCheckResult != null) ? NetworkCheckResult.ReceiverKey(networkCheckResult.Receivers) : null);
		if (text == null)
		{
			return;
		}
		TaskCompletionSource<string> value;
		lock (_commandLock)
		{
			if (!_pendingNetworkChecks.Remove(text, out value))
			{
				return;
			}
		}
		value.TrySetResult(json);
	}

	public void SetAccessPassword(string ip, string password)
	{
		if (TryNormalizeIp(ip, out string normalized) && !string.IsNullOrEmpty(password))
		{
			lock (_commandLock)
			{
				_accessPasswords[normalized] = password;
			}
			if (IsConnected)
			{
				WriteCommand(BuildAccessPasswordCommand(normalized, password));
			}
		}
	}

	public void ClearAccessPassword(string ip)
	{
		if (TryNormalizeIp(ip, out string normalized))
		{
			bool flag;
			lock (_commandLock)
			{
				flag = _accessPasswords.Remove(normalized);
			}
			if (flag && IsConnected)
			{
				WriteCommand(BuildAccessPasswordCommand(normalized, null));
			}
		}
	}

	internal static string BuildAccessPasswordCommand(string ip, string? password)
	{
		if (!string.IsNullOrEmpty(password))
		{
			return "PASSWORD " + ip + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
		}
		return "PASSWORD " + ip;
	}

	public void SetPin(string ip, string pin)
	{
		if (TryNormalizeIp(ip, out string normalized) && TryNormalizePin(pin, out string normalized2))
		{
			WriteCommand("PIN " + normalized + " " + normalized2);
		}
	}

	public void SetCaptureDefault()
	{
		WriteCommand("SET_CAPTURE DEFAULT");
	}

	public void SetCaptureDisabled()
	{
		WriteCommand("SET_CAPTURE DISABLED");
	}

	public void SetCaptureDevice(string deviceId)
	{
		if (TryNormalizeDeviceId(deviceId, out string normalized))
		{
			WriteCommand("SET_CAPTURE DEVICE " + normalized);
		}
	}

	public void SetCaptureProcess(params int[] processIds)
	{
		int[] array = processIds.Where((int pid) => pid > 0).Distinct().ToArray();
		if (array.Length != 0)
		{
			WriteCommand("SET_PROCESS " + string.Join(" ", array));
		}
	}

	public void ApplyCaptureSourceFromConfig()
	{
		(int, string, int) tuple = SettingsManager.ReadConfig((AppConfig value) => (CaptureSourceIndex: value.CaptureSourceIndex, SelectedCaptureDeviceId: value.SelectedCaptureDeviceId, SelectedProcessId: value.SelectedProcessId));
		WriteCommand(BuildCaptureCommand(tuple.Item1, tuple.Item2, tuple.Item3));
	}

	internal static string BuildCaptureCommand(int captureSourceIndex, string? deviceId, int processId)
	{
		string normalized;
		if (captureSourceIndex != 1)
		{
			if (captureSourceIndex != 2)
			{
				return "SET_CAPTURE DEFAULT";
			}
			if (processId > 0)
			{
				return $"SET_PROCESS {processId}";
			}
		}
		else if (TryNormalizeDeviceId(deviceId, out normalized))
		{
			return "SET_CAPTURE DEVICE " + normalized;
		}
		return "SET_CAPTURE DISABLED";
	}

	public void SetStandbyConfig(bool enable, int timeoutMinutes)
	{
		WriteCommand($"SET_STANDBY {enable.ToString().ToLower()} {timeoutMinutes}");
	}

	public void SetEqEnabled(bool enabled)
	{
		WriteCommand("EQ_ENABLE " + enabled.ToString().ToLowerInvariant());
	}

	public void ResetEq()
	{
		WriteCommand("EQ_RESET");
	}

	public void SetEqBand(int index, double gainDb)
	{
		if (index >= 0 && index < 10 && !double.IsNaN(gainDb) && !double.IsInfinity(gainDb))
		{
			double num = Math.Clamp(gainDb, -12.0, 12.0);
			WriteCommand(string.Format(CultureInfo.InvariantCulture, "EQ_BAND {0} {1:0.###}", index, num));
		}
	}

	public void SetEqPreamp(double gainDb)
	{
		if (!double.IsNaN(gainDb) && !double.IsInfinity(gainDb))
		{
			double num = Math.Clamp(gainDb, -12.0, 12.0);
			WriteCommand(string.Format(CultureInfo.InvariantCulture, "EQ_PREAMP {0:0.###}", num));
		}
	}

	public bool SendNowPlaying(string? title, string? artist, string? album, string? artworkBase64, string? artworkMime, long positionMs = 0L, long durationMs = 0L, string? playbackState = null, double? playbackRate = null)
	{
		if (!IsConnected)
		{
			return false;
		}
		try
		{
			string text = JsonSerializer.Serialize(new
			{
				title = (string.IsNullOrWhiteSpace(title) ? null : title),
				artist = (string.IsNullOrWhiteSpace(artist) ? null : artist),
				album = (string.IsNullOrWhiteSpace(album) ? null : album),
				artwork_base64 = (string.IsNullOrWhiteSpace(artworkBase64) ? null : artworkBase64),
				artwork_mime = (string.IsNullOrWhiteSpace(artworkMime) ? null : artworkMime),
				position_ms = ((positionMs >= 0 && durationMs > 0) ? new long?(positionMs) : ((long?)null)),
				duration_ms = ((durationMs > 0) ? new long?(durationMs) : ((long?)null)),
				playback_state = (string.IsNullOrWhiteSpace(playbackState) ? null : playbackState),
				playback_rate = ((playbackRate.HasValue && double.IsFinite(playbackRate.Value)) ? new double?(Math.Clamp(playbackRate.Value, 0.0, 4.0)) : ((double?)null))
			});
			if (ContainsCommandUnsafeChars(text))
			{
				return false;
			}
			return WriteCommand("NOW_PLAYING " + text);
		}
		catch (Exception)
		{
			return false;
		}
	}

	public bool SendAutomaticReceiverRecovery(bool enabled)
	{
		return WriteCommand("AUTO_RECEIVER_RECOVERY " + (enabled ? "true" : "false"));
	}

	public void SyncEqStateFromConfig()
	{
		(bool, double, double[]) tuple = SettingsManager.ReadConfig(delegate(AppConfig value)
		{
			SettingsManager.NormalizeEqConfig(value);
			return (EqEnabled: value.EqEnabled, EqPreampDb: value.EqPreampDb, Gains: value.EqBandGains.ToArray());
		});
		SetEqEnabled(tuple.Item1);
		SetEqPreamp(tuple.Item2);
		double[] item = tuple.Item3;
		for (int num = 0; num < 10; num++)
		{
			double gainDb = ((num < item.Length) ? item[num] : 0.0);
			SetEqBand(num, gainDb);
		}
	}

	private static int CurrentLatencyMs()
	{
		return SettingsManager.ReadConfig((AppConfig config) => config.StreamingModeIndex switch
		{
			0 => 0, 
			1 => 200, 
			2 => 500, 
			3 => Math.Clamp(config.CustomBufferSize, 0, 1000), 
			_ => 0, 
		});
	}

	private void ReplayDeveloperModeOverrides()
	{
		(bool, bool, bool, bool, int?, int?, int?) tuple = SettingsManager.ReadConfig((AppConfig value) => (DeveloperModeEnabled: value.DeveloperModeEnabled, DevSoloLatencyOverrideEnabled: value.DevSoloLatencyOverrideEnabled, DevGroupLatencyOverrideEnabled: value.DevGroupLatencyOverrideEnabled, DevMixedLatencyOverrideEnabled: value.DevMixedLatencyOverrideEnabled, DevSoloLatencyMs: value.DevSoloLatencyMs, DevGroupLatencyMs: value.DevGroupLatencyMs, DevMixedLatencyMs: value.DevMixedLatencyMs));
		if (tuple.Item1)
		{
			SetDevPtpLatency("SOLO", tuple.Item2 ? tuple.Item5 : ((int?)null));
			SetDevPtpLatency("GROUP", tuple.Item3 ? tuple.Item6 : ((int?)null));
			SetDevPtpLatency("MIXED", tuple.Item4 ? tuple.Item7 : ((int?)null));
		}
	}

	public void SetCalibrationRttRate(double hz)
	{
		WriteCommand($"CALIBRATION_RTT_RATE {hz:0.0}");
	}

	public void SetCalibrationSession(int? maxMs)
	{
		if (maxMs.HasValue && maxMs.Value > 0)
		{
			WriteCommand($"CALIBRATION_SESSION {maxMs.Value}");
		}
		else
		{
			WriteCommand("CALIBRATION_SESSION OFF");
		}
	}

	public bool WriteCommand(string command)
	{
		if (_commandWriterForTesting != null)
		{
			_commandWriterForTesting(command);
			return true;
		}
		lock (_commandLock)
		{
			Process backendProcess = _backendProcess;
			if (!ProcessIsAlive(backendProcess))
			{
				return false;
			}
			try
			{
				backendProcess.StandardInput.WriteLine(command);
				backendProcess.StandardInput.Flush();
				return true;
			}
			catch (Exception)
			{
				ScheduleBackendRestart();
				return false;
			}
		}
	}

	internal static string RedactCommandForLog(string command)
	{
		string text = command.TrimStart();
		if (text.StartsWith("PIN ", StringComparison.OrdinalIgnoreCase))
		{
			return "PIN <redacted>";
		}
		if (text.StartsWith("PASSWORD ", StringComparison.OrdinalIgnoreCase))
		{
			return "PASSWORD <redacted>";
		}
		return command;
	}

	internal static bool TryNormalizeIp(string? ip, out string normalized)
	{
		normalized = string.Empty;
		if (string.IsNullOrWhiteSpace(ip))
		{
			return false;
		}
		string text = ip.Trim();
		if (ContainsCommandUnsafeChars(text) || text.Any(char.IsWhiteSpace) || !IPAddress.TryParse(text, out IPAddress _))
		{
			return false;
		}
		normalized = text;
		return true;
	}

	internal static bool TryNormalizePin(string? pin, out string normalized)
	{
		normalized = string.Empty;
		if (string.IsNullOrWhiteSpace(pin))
		{
			return false;
		}
		string text = pin.Trim();
		if (ContainsCommandUnsafeChars(text) || text.Length < 4 || text.Length > 8 || !text.All(char.IsDigit))
		{
			return false;
		}
		normalized = text;
		return true;
	}

	internal static bool TryNormalizeDeviceId(string? deviceId, out string normalized)
	{
		normalized = string.Empty;
		if (string.IsNullOrWhiteSpace(deviceId))
		{
			return false;
		}
		string text = deviceId.Trim();
		if (ContainsCommandUnsafeChars(text) || text.Any(char.IsWhiteSpace) || text.Length > 256)
		{
			return false;
		}
		normalized = text;
		return true;
	}

	private static bool ContainsCommandUnsafeChars(string value)
	{
		return value.IndexOfAny(CommandUnsafeChars) >= 0;
	}

	internal static ProcessStartInfo BuildBackendStartInfo(string backendPath)
	{
		return new ProcessStartInfo
		{
			FileName = backendPath,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			StandardOutputEncoding = BackendPipeEncoding,
			StandardErrorEncoding = BackendPipeEncoding,
			StandardInputEncoding = BackendPipeEncoding,
			CreateNoWindow = true
		};
	}

	internal static string FindBackendExecutable()
	{
		string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
		List<string> list = new List<string>
		{
			Path.Combine(baseDirectory, "AirStereo2.Engine.exe")
		};
		foreach (string item2 in list.Select(Path.GetFullPath).Distinct<string>(StringComparer.OrdinalIgnoreCase))
		{
			if (File.Exists(item2))
			{
				return item2;
			}
		}
		throw new FileNotFoundException("Cannot find AirStereo2.Engine.exe next to AirStereo2.exe.\nSearched:\n" + string.Join("\n", list.Select((string c) => "  " + Path.GetFullPath(c))));
	}

	private static IEnumerable<string> EnumerateProjectRoots(string baseDirectory)
	{
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		DirectoryInfo current = new DirectoryInfo(baseDirectory);
		while (current != null && seen.Add(current.FullName))
		{
			if (Directory.Exists(Path.Combine(current.FullName, "airplay2-rs")))
			{
				yield return current.FullName;
			}
			current = current.Parent;
		}
	}

	public void Dispose()
	{
		_disposing = true;
		lock (_volumeLock)
		{
			foreach (Timer value in _volumeTimers.Values)
			{
				value.Dispose();
			}
			_volumeTimers.Clear();
			_pendingVolumes.Clear();
			_pendingVolumeCommands.Clear();
		}
		Process backendProcess;
		lock (_lifecycleLock)
		{
			backendProcess = _backendProcess;
			_backendProcess = null;
		}
		if (backendProcess == null)
		{
			return;
		}
		try
		{
			if (ProcessIsAlive(backendProcess))
			{
				TryWriteQuit(backendProcess);
				if (!backendProcess.WaitForExit(3000) && ProcessIsAlive(backendProcess))
				{
					backendProcess.Kill();
				}
			}
		}
		catch
		{
		}
		try
		{
			backendProcess.Dispose();
		}
		catch
		{
		}
	}

	private void TryWriteQuit(Process process)
	{
		lock (_commandLock)
		{
			try
			{
				process.StandardInput.WriteLine("QUIT");
				process.StandardInput.Flush();
			}
			catch
			{
			}
		}
	}
}
