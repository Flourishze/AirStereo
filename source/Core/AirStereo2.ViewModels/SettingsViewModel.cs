using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AirStereo2.Messages;
using AirStereo2.Models;
using AirStereo2.Services;
using AirStereo2.Views;
using AirStereo2.Views.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel.__Internals;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Win32;

namespace AirStereo2.ViewModels;

public class SettingsViewModel : ObservableObject, IRecipient<EqDeviceMemoryApplyMessage>, IDisposable
{
	[ObservableProperty]
	private int _captureSourceIndex;

	[ObservableProperty]
	private ObservableCollection<AudioDeviceItem> _availableCaptureDevices = new ObservableCollection<AudioDeviceItem>();

	[ObservableProperty]
	private AudioDeviceItem? _selectedCaptureDevice;

	[ObservableProperty]
	private ObservableCollection<ProcessItem> _availableProcesses = new ObservableCollection<ProcessItem>();

	[ObservableProperty]
	private ProcessItem? _selectedProcess;

	[ObservableProperty]
	private bool _isProcessCaptureSupported = Environment.OSVersion.Version >= new Version(10, 0, 19041);

	private int _processRefreshGeneration;

	private int _processRecoveryInProgress;

	private bool _transientCaptureCatalogsActive;

	private bool _suppressCaptureSelectionSideEffects;

	private bool _applyCaptureSourceAfterProcessRefresh;

	private const string PublicSiteBaseUrl = "https://flourishze.dev";

	private const string MicrosoftStoreProductUrl = "https://github.com/Flourishze/AirStereo#readme";

	private const string FeedbackEmailUrl = "mailto:feedback@flourishze.dev";

	private readonly AudioCaptureService _captureService;

	private readonly HomeViewModel _homeViewModel;

	private readonly StoreLicenseService _storeLicenseService;

	private readonly ReleaseDiagnosticsService _diagnosticsService;

	private readonly DiagnosticsUploadService _diagnosticsUploadService;

	private readonly LocalOutputMuteService _localOutputMuteService;

	private readonly WindowsStartupService _startupService = new WindowsStartupService();

	private bool _suppressStartupSideEffects;

	[ObservableProperty]
	private bool _startOnBoot;

	[ObservableProperty]
	private int _defaultWindowModeIndex;

	[ObservableProperty]
	private int _dockPlacementIndex;

	[ObservableProperty]
	private bool _silentStartEnabled;

	[ObservableProperty]
	private bool _autoConnectOnLaunch;

	[ObservableProperty]
	private bool _checkForUpdates = true;

	private bool _disposed;

	[ObservableProperty]
	private int _languageIndex;

	[ObservableProperty]
	private string _appVersionText = string.Empty;

	[ObservableProperty]
	private string _backendStatusText = string.Empty;

	[ObservableProperty]
	private string _backendPathText = string.Empty;

	[ObservableProperty]
	private string _backendLogPathText = string.Empty;

	[ObservableProperty]
	private string _diagnosticsUploadStatusText = string.Empty;

	[ObservableProperty]
	private string _storeLicenseStatusText = "Loading...";

	[ObservableProperty]
	private string _storeLicenseDetailText = string.Empty;

	[ObservableProperty]
	private string _defaultOutputSummaryText = string.Empty;

	[ObservableProperty]
	private bool _developerModeEnabled;

	[ObservableProperty]
	private bool _devSoloLatencyOverrideEnabled;

	[ObservableProperty]
	private bool _devGroupLatencyOverrideEnabled;

	[ObservableProperty]
	private bool _devMixedLatencyOverrideEnabled;

	[ObservableProperty]
	private string? _devSoloLatencyText;

	[ObservableProperty]
	private string? _devGroupLatencyText;

	[ObservableProperty]
	private string? _devMixedLatencyText;

	private bool _suppressDeviceEqMemoryWrite;

	private readonly ConcurrentQueue<DeviceEqMemorySnapshot> _pendingEqSnapshots = new ConcurrentQueue<DeviceEqMemorySnapshot>();

	private int _eqSnapshotDrainScheduled;

	[ObservableProperty]
	private bool _eqEnabled;

	[ObservableProperty]
	private double _eqPreampDb;

	[ObservableProperty]
	private EqPresetItem? _selectedEqPreset;

	[ObservableProperty]
	private bool _eqIsCustom;

	[ObservableProperty]
	private bool _canDeleteEqPreset;

	private bool _suppressEqSideEffects;

	private readonly object _eqBandDebounceCtsLock = new object();

	private CancellationTokenSource? _eqBandDebounceCts;

	private readonly Dictionary<int, double> _pendingEqBandSends = new Dictionary<int, double>();

	[ObservableProperty]
	[NotifyPropertyChangedFor("NetworkCheckStatusVisibility")]
	private string _networkCheckStatusText = string.Empty;

	[ObservableProperty]
	[NotifyPropertyChangedFor("FirewallFixVisibility")]
	private bool _canFixFirewall;

	private NetworkCheckResult? _shownNetworkCheck;

	private (string Key, string English)? _networkStatusMessage;

	private bool _networkStatusShown;

	[ObservableProperty]
	private bool _silenceDetectionEnabled;

	[ObservableProperty]
	private int _silenceTimeoutMinutes = 120;

	[ObservableProperty]
	private int _streamingModeIndex;

	[ObservableProperty]
	private int _customBufferSize = 500;

	[ObservableProperty]
	private bool _muteLocalOutputWhileStreaming;

	[ObservableProperty]
	private bool _receiverMediaControlEnabled;

	[ObservableProperty]
	private bool _sendNowPlayingMetadata;

	[ObservableProperty]
	private bool _automaticReceiverRecoveryEnabled;

	[ObservableProperty]
	private int _audioCodecIndex;

	private bool _suppressCodecSideEffects;

	private int _isConfirmingCodecChange;

	private readonly object _latencyDebounceCtsLock = new object();

	private CancellationTokenSource? _latencyDebounceCts;

	private int _latencyDebounceGeneration;

	private int? _lastSentLatency;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? refreshStoreLicenseCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openLogsFolderCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? exportDiagnosticsCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? uploadDiagnosticsCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openWebsiteCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openPrivacyPolicyCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openSupportCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openStoreListingCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openThirdPartyNoticesCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? reportIssueCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? refreshCaptureDevicesCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? refreshProcessesCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? clearPairingsCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<string>? setDevSoloPresetCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<string>? setDevGroupPresetCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<string>? setDevMixedPresetCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<string>? adjustDevSoloLatencyCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<string>? adjustDevGroupLatencyCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<string>? adjustDevMixedLatencyCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? resetAllDevOverridesCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? resetEqPreampCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<EqBandViewModel?>? resetEqBandCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? resetEqCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? saveEqPresetCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? deleteEqPresetCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? exportEqPresetsCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? importEqPresetsCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? checkNetworkCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? fixFirewallCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<string>? setSilenceTimeoutPresetCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? runLatencyCalibrationCommand;

	public List<string> AvailableLanguages => new List<string> { "English (US)", "简体中文", "繁體中文", "日本語", "한국어", "Français", "Español", "Русский" };

	public string AppVersionDisplay => LocalizationManager.Instance.Format("Release_AppVersionFormat", "Version {0}", AppVersionText);

	public static bool WasCreated { get; private set; }

	public ObservableCollection<DefaultOutputEntry> DefaultOutputEntries { get; } = new ObservableCollection<DefaultOutputEntry>();

	public int DevSoloLatencyValue
	{
		get
		{
			return ParseDevLatencyText(DevSoloLatencyText) ?? 45;
		}
		set
		{
			DevSoloLatencyText = Math.Clamp(value, 0, 500).ToString();
			this.OnPropertyChanged("DevSoloLatencyValue");
		}
	}

	public int DevGroupLatencyValue
	{
		get
		{
			return ParseDevLatencyText(DevGroupLatencyText) ?? 120;
		}
		set
		{
			DevGroupLatencyText = Math.Clamp(value, 0, 500).ToString();
			this.OnPropertyChanged("DevGroupLatencyValue");
		}
	}

	public int DevMixedLatencyValue
	{
		get
		{
			return ParseDevLatencyText(DevMixedLatencyText) ?? 140;
		}
		set
		{
			DevMixedLatencyText = Math.Clamp(value, 0, 500).ToString();
			this.OnPropertyChanged("DevMixedLatencyValue");
		}
	}

	public string EqPreampText => FormatEqGainDisplay(EqPreampDb);

	public ObservableCollection<EqBandViewModel> EqBands { get; } = new ObservableCollection<EqBandViewModel>();

	public ObservableCollection<EqPresetItem> EqPresets { get; } = new ObservableCollection<EqPresetItem>();

	public string EqPresetStatusText
	{
		get
		{
			if (EqIsCustom || SelectedEqPreset == null)
			{
				return LocalizationManager.Instance.GetString("Eq_StatusCustom", "Custom");
			}
			return SelectedEqPreset.DisplayName;
		}
	}

	public Visibility NetworkCheckStatusVisibility
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(NetworkCheckStatusText))
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public Visibility FirewallFixVisibility
	{
		get
		{
			if (!CanFixFirewall)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public string SilenceTimeoutText
	{
		get
		{
			int silenceTimeoutMinutes = SilenceTimeoutMinutes;
			LocalizationManager instance = LocalizationManager.Instance;
			if (silenceTimeoutMinutes >= 60 && silenceTimeoutMinutes % 60 == 0)
			{
				return instance.Format("Silence_TimeoutHoursFormat", "{0} h", silenceTimeoutMinutes / 60);
			}
			if (silenceTimeoutMinutes >= 60)
			{
				return instance.Format("Silence_TimeoutHoursMinutesFormat", "{0} h {1} min", silenceTimeoutMinutes / 60, silenceTimeoutMinutes % 60);
			}
			return instance.Format("Silence_TimeoutMinutesFormat", "{0} min", silenceTimeoutMinutes);
		}
	}

	public int CurrentLatencyMs => StreamingModeIndex switch
	{
		0 => 0, 
		1 => 200, 
		2 => 500, 
		3 => Math.Clamp(CustomBufferSize, 0, 1000), 
		_ => 0, 
	};

	public string SelectedAudioCodec
	{
		get
		{
			if (AudioCodecIndex != 1)
			{
				return "ALAC";
			}
			return "PCM";
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int CaptureSourceIndex
	{
		get
		{
			return _captureSourceIndex;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_captureSourceIndex, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CaptureSourceIndex);
				_captureSourceIndex = value;
				OnCaptureSourceIndexChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CaptureSourceIndex);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public ObservableCollection<AudioDeviceItem> AvailableCaptureDevices
	{
		get
		{
			return _availableCaptureDevices;
		}
		[MemberNotNull("_availableCaptureDevices")]
		set
		{
			if (!EqualityComparer<ObservableCollection<AudioDeviceItem>>.Default.Equals(_availableCaptureDevices, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AvailableCaptureDevices);
				_availableCaptureDevices = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AvailableCaptureDevices);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public AudioDeviceItem? SelectedCaptureDevice
	{
		get
		{
			return _selectedCaptureDevice;
		}
		set
		{
			if (!EqualityComparer<AudioDeviceItem>.Default.Equals(_selectedCaptureDevice, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedCaptureDevice);
				_selectedCaptureDevice = value;
				OnSelectedCaptureDeviceChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedCaptureDevice);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public ObservableCollection<ProcessItem> AvailableProcesses
	{
		get
		{
			return _availableProcesses;
		}
		[MemberNotNull("_availableProcesses")]
		set
		{
			if (!EqualityComparer<ObservableCollection<ProcessItem>>.Default.Equals(_availableProcesses, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AvailableProcesses);
				_availableProcesses = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AvailableProcesses);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public ProcessItem? SelectedProcess
	{
		get
		{
			return _selectedProcess;
		}
		set
		{
			if (!EqualityComparer<ProcessItem>.Default.Equals(_selectedProcess, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedProcess);
				_selectedProcess = value;
				OnSelectedProcessChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedProcess);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsProcessCaptureSupported
	{
		get
		{
			return _isProcessCaptureSupported;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isProcessCaptureSupported, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsProcessCaptureSupported);
				_isProcessCaptureSupported = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsProcessCaptureSupported);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool StartOnBoot
	{
		get
		{
			return _startOnBoot;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_startOnBoot, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.StartOnBoot);
				_startOnBoot = value;
				OnStartOnBootChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.StartOnBoot);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int DefaultWindowModeIndex
	{
		get
		{
			return _defaultWindowModeIndex;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_defaultWindowModeIndex, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DefaultWindowModeIndex);
				_defaultWindowModeIndex = value;
				OnDefaultWindowModeIndexChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DefaultWindowModeIndex);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int DockPlacementIndex
	{
		get
		{
			return _dockPlacementIndex;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_dockPlacementIndex, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockPlacementIndex);
				_dockPlacementIndex = value;
				OnDockPlacementIndexChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockPlacementIndex);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool SilentStartEnabled
	{
		get
		{
			return _silentStartEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_silentStartEnabled, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SilentStartEnabled);
				_silentStartEnabled = value;
				OnSilentStartEnabledChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SilentStartEnabled);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool AutoConnectOnLaunch
	{
		get
		{
			return _autoConnectOnLaunch;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_autoConnectOnLaunch, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AutoConnectOnLaunch);
				_autoConnectOnLaunch = value;
				OnAutoConnectOnLaunchChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AutoConnectOnLaunch);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool CheckForUpdates
	{
		get
		{
			return _checkForUpdates;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_checkForUpdates, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CheckForUpdates);
				_checkForUpdates = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CheckForUpdates);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int LanguageIndex
	{
		get
		{
			return _languageIndex;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_languageIndex, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.LanguageIndex);
				_languageIndex = value;
				OnLanguageIndexChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.LanguageIndex);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string AppVersionText
	{
		get
		{
			return _appVersionText;
		}
		[MemberNotNull("_appVersionText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_appVersionText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AppVersionText);
				_appVersionText = value;
				OnAppVersionTextChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AppVersionText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string BackendStatusText
	{
		get
		{
			return _backendStatusText;
		}
		[MemberNotNull("_backendStatusText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_backendStatusText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.BackendStatusText);
				_backendStatusText = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.BackendStatusText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string BackendPathText
	{
		get
		{
			return _backendPathText;
		}
		[MemberNotNull("_backendPathText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_backendPathText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.BackendPathText);
				_backendPathText = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.BackendPathText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string BackendLogPathText
	{
		get
		{
			return _backendLogPathText;
		}
		[MemberNotNull("_backendLogPathText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_backendLogPathText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.BackendLogPathText);
				_backendLogPathText = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.BackendLogPathText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DiagnosticsUploadStatusText
	{
		get
		{
			return _diagnosticsUploadStatusText;
		}
		[MemberNotNull("_diagnosticsUploadStatusText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_diagnosticsUploadStatusText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DiagnosticsUploadStatusText);
				_diagnosticsUploadStatusText = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DiagnosticsUploadStatusText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string StoreLicenseStatusText
	{
		get
		{
			return _storeLicenseStatusText;
		}
		[MemberNotNull("_storeLicenseStatusText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_storeLicenseStatusText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.StoreLicenseStatusText);
				_storeLicenseStatusText = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.StoreLicenseStatusText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string StoreLicenseDetailText
	{
		get
		{
			return _storeLicenseDetailText;
		}
		[MemberNotNull("_storeLicenseDetailText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_storeLicenseDetailText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.StoreLicenseDetailText);
				_storeLicenseDetailText = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.StoreLicenseDetailText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DefaultOutputSummaryText
	{
		get
		{
			return _defaultOutputSummaryText;
		}
		[MemberNotNull("_defaultOutputSummaryText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_defaultOutputSummaryText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DefaultOutputSummaryText);
				_defaultOutputSummaryText = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DefaultOutputSummaryText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool DeveloperModeEnabled
	{
		get
		{
			return _developerModeEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_developerModeEnabled, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DeveloperModeEnabled);
				_developerModeEnabled = value;
				OnDeveloperModeEnabledChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DeveloperModeEnabled);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool DevSoloLatencyOverrideEnabled
	{
		get
		{
			return _devSoloLatencyOverrideEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_devSoloLatencyOverrideEnabled, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DevSoloLatencyOverrideEnabled);
				_devSoloLatencyOverrideEnabled = value;
				OnDevSoloLatencyOverrideEnabledChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DevSoloLatencyOverrideEnabled);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool DevGroupLatencyOverrideEnabled
	{
		get
		{
			return _devGroupLatencyOverrideEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_devGroupLatencyOverrideEnabled, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DevGroupLatencyOverrideEnabled);
				_devGroupLatencyOverrideEnabled = value;
				OnDevGroupLatencyOverrideEnabledChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DevGroupLatencyOverrideEnabled);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool DevMixedLatencyOverrideEnabled
	{
		get
		{
			return _devMixedLatencyOverrideEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_devMixedLatencyOverrideEnabled, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DevMixedLatencyOverrideEnabled);
				_devMixedLatencyOverrideEnabled = value;
				OnDevMixedLatencyOverrideEnabledChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DevMixedLatencyOverrideEnabled);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string? DevSoloLatencyText
	{
		get
		{
			return _devSoloLatencyText;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_devSoloLatencyText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DevSoloLatencyText);
				_devSoloLatencyText = value;
				OnDevSoloLatencyTextChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DevSoloLatencyText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string? DevGroupLatencyText
	{
		get
		{
			return _devGroupLatencyText;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_devGroupLatencyText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DevGroupLatencyText);
				_devGroupLatencyText = value;
				OnDevGroupLatencyTextChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DevGroupLatencyText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string? DevMixedLatencyText
	{
		get
		{
			return _devMixedLatencyText;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_devMixedLatencyText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DevMixedLatencyText);
				_devMixedLatencyText = value;
				OnDevMixedLatencyTextChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DevMixedLatencyText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool EqEnabled
	{
		get
		{
			return _eqEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_eqEnabled, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.EqEnabled);
				_eqEnabled = value;
				OnEqEnabledChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.EqEnabled);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public double EqPreampDb
	{
		get
		{
			return _eqPreampDb;
		}
		set
		{
			if (!EqualityComparer<double>.Default.Equals(_eqPreampDb, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.EqPreampDb);
				_eqPreampDb = value;
				OnEqPreampDbChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.EqPreampDb);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public EqPresetItem? SelectedEqPreset
	{
		get
		{
			return _selectedEqPreset;
		}
		set
		{
			if (!EqualityComparer<EqPresetItem>.Default.Equals(_selectedEqPreset, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedEqPreset);
				_selectedEqPreset = value;
				OnSelectedEqPresetChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedEqPreset);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool EqIsCustom
	{
		get
		{
			return _eqIsCustom;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_eqIsCustom, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.EqIsCustom);
				_eqIsCustom = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.EqIsCustom);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool CanDeleteEqPreset
	{
		get
		{
			return _canDeleteEqPreset;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_canDeleteEqPreset, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanDeleteEqPreset);
				_canDeleteEqPreset = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CanDeleteEqPreset);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string NetworkCheckStatusText
	{
		get
		{
			return _networkCheckStatusText;
		}
		[MemberNotNull("_networkCheckStatusText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_networkCheckStatusText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.NetworkCheckStatusText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.NetworkCheckStatusVisibility);
				_networkCheckStatusText = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.NetworkCheckStatusText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.NetworkCheckStatusVisibility);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool CanFixFirewall
	{
		get
		{
			return _canFixFirewall;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_canFixFirewall, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanFixFirewall);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.FirewallFixVisibility);
				_canFixFirewall = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CanFixFirewall);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.FirewallFixVisibility);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool SilenceDetectionEnabled
	{
		get
		{
			return _silenceDetectionEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_silenceDetectionEnabled, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SilenceDetectionEnabled);
				_silenceDetectionEnabled = value;
				OnSilenceDetectionEnabledChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SilenceDetectionEnabled);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int SilenceTimeoutMinutes
	{
		get
		{
			return _silenceTimeoutMinutes;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_silenceTimeoutMinutes, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SilenceTimeoutMinutes);
				_silenceTimeoutMinutes = value;
				OnSilenceTimeoutMinutesChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SilenceTimeoutMinutes);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int StreamingModeIndex
	{
		get
		{
			return _streamingModeIndex;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_streamingModeIndex, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.StreamingModeIndex);
				_streamingModeIndex = value;
				OnStreamingModeIndexChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.StreamingModeIndex);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int CustomBufferSize
	{
		get
		{
			return _customBufferSize;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_customBufferSize, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CustomBufferSize);
				_customBufferSize = value;
				OnCustomBufferSizeChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CustomBufferSize);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool MuteLocalOutputWhileStreaming
	{
		get
		{
			return _muteLocalOutputWhileStreaming;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_muteLocalOutputWhileStreaming, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.MuteLocalOutputWhileStreaming);
				_muteLocalOutputWhileStreaming = value;
				OnMuteLocalOutputWhileStreamingChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.MuteLocalOutputWhileStreaming);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool ReceiverMediaControlEnabled
	{
		get
		{
			return _receiverMediaControlEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_receiverMediaControlEnabled, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ReceiverMediaControlEnabled);
				_receiverMediaControlEnabled = value;
				OnReceiverMediaControlEnabledChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ReceiverMediaControlEnabled);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool SendNowPlayingMetadata
	{
		get
		{
			return _sendNowPlayingMetadata;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_sendNowPlayingMetadata, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SendNowPlayingMetadata);
				_sendNowPlayingMetadata = value;
				OnSendNowPlayingMetadataChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SendNowPlayingMetadata);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool AutomaticReceiverRecoveryEnabled
	{
		get
		{
			return _automaticReceiverRecoveryEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_automaticReceiverRecoveryEnabled, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AutomaticReceiverRecoveryEnabled);
				_automaticReceiverRecoveryEnabled = value;
				OnAutomaticReceiverRecoveryEnabledChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AutomaticReceiverRecoveryEnabled);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int AudioCodecIndex
	{
		get
		{
			return _audioCodecIndex;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_audioCodecIndex, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AudioCodecIndex);
				_audioCodecIndex = value;
				OnAudioCodecIndexChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AudioCodecIndex);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RefreshStoreLicenseCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = refreshStoreLicenseCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)RefreshStoreLicenseAsync);
				AsyncRelayCommand val2 = val;
				refreshStoreLicenseCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenLogsFolderCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = openLogsFolderCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)OpenLogsFolder);
				RelayCommand val2 = val;
				openLogsFolderCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ExportDiagnosticsCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = exportDiagnosticsCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ExportDiagnosticsAsync);
				AsyncRelayCommand val2 = val;
				exportDiagnosticsCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand UploadDiagnosticsCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = uploadDiagnosticsCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)UploadDiagnosticsAsync);
				AsyncRelayCommand val2 = val;
				uploadDiagnosticsCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenWebsiteCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = openWebsiteCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)OpenWebsite);
				RelayCommand val2 = val;
				openWebsiteCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenPrivacyPolicyCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = openPrivacyPolicyCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)OpenPrivacyPolicy);
				RelayCommand val2 = val;
				openPrivacyPolicyCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenSupportCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = openSupportCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)OpenSupport);
				RelayCommand val2 = val;
				openSupportCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenStoreListingCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = openStoreListingCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)OpenStoreListing);
				RelayCommand val2 = val;
				openStoreListingCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenThirdPartyNoticesCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = openThirdPartyNoticesCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)OpenThirdPartyNotices);
				RelayCommand val2 = val;
				openThirdPartyNoticesCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ReportIssueCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = reportIssueCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)ReportIssue);
				RelayCommand val2 = val;
				reportIssueCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand RefreshCaptureDevicesCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = refreshCaptureDevicesCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)RefreshCaptureDevices);
				RelayCommand val2 = val;
				refreshCaptureDevicesCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RefreshProcessesCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = refreshProcessesCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)RefreshProcesses);
				AsyncRelayCommand val2 = val;
				refreshProcessesCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ClearPairingsCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = clearPairingsCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ClearPairings);
				AsyncRelayCommand val2 = val;
				clearPairingsCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<string> SetDevSoloPresetCommand => (IRelayCommand<string>)(object)(setDevSoloPresetCommand ?? (setDevSoloPresetCommand = new RelayCommand<string>((Action<string>)SetDevSoloPreset)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<string> SetDevGroupPresetCommand => (IRelayCommand<string>)(object)(setDevGroupPresetCommand ?? (setDevGroupPresetCommand = new RelayCommand<string>((Action<string>)SetDevGroupPreset)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<string> SetDevMixedPresetCommand => (IRelayCommand<string>)(object)(setDevMixedPresetCommand ?? (setDevMixedPresetCommand = new RelayCommand<string>((Action<string>)SetDevMixedPreset)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<string> AdjustDevSoloLatencyCommand => (IRelayCommand<string>)(object)(adjustDevSoloLatencyCommand ?? (adjustDevSoloLatencyCommand = new RelayCommand<string>((Action<string>)AdjustDevSoloLatency)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<string> AdjustDevGroupLatencyCommand => (IRelayCommand<string>)(object)(adjustDevGroupLatencyCommand ?? (adjustDevGroupLatencyCommand = new RelayCommand<string>((Action<string>)AdjustDevGroupLatency)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<string> AdjustDevMixedLatencyCommand => (IRelayCommand<string>)(object)(adjustDevMixedLatencyCommand ?? (adjustDevMixedLatencyCommand = new RelayCommand<string>((Action<string>)AdjustDevMixedLatency)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ResetAllDevOverridesCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = resetAllDevOverridesCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)ResetAllDevOverrides);
				RelayCommand val2 = val;
				resetAllDevOverridesCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ResetEqPreampCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = resetEqPreampCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)ResetEqPreamp);
				RelayCommand val2 = val;
				resetEqPreampCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<EqBandViewModel?> ResetEqBandCommand => (IRelayCommand<EqBandViewModel?>)(object)(resetEqBandCommand ?? (resetEqBandCommand = new RelayCommand<EqBandViewModel>((Action<EqBandViewModel>)ResetEqBand)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ResetEqCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = resetEqCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)ResetEq);
				RelayCommand val2 = val;
				resetEqCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand SaveEqPresetCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = saveEqPresetCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)SaveEqPreset);
				AsyncRelayCommand val2 = val;
				saveEqPresetCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand DeleteEqPresetCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = deleteEqPresetCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)DeleteEqPreset);
				AsyncRelayCommand val2 = val;
				deleteEqPresetCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ExportEqPresetsCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = exportEqPresetsCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ExportEqPresets);
				AsyncRelayCommand val2 = val;
				exportEqPresetsCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ImportEqPresetsCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = importEqPresetsCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ImportEqPresets);
				AsyncRelayCommand val2 = val;
				importEqPresetsCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand CheckNetworkCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = checkNetworkCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)CheckNetworkAsync);
				AsyncRelayCommand val2 = val;
				checkNetworkCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand FixFirewallCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = fixFirewallCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)FixFirewallAsync);
				AsyncRelayCommand val2 = val;
				fixFirewallCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<string> SetSilenceTimeoutPresetCommand => (IRelayCommand<string>)(object)(setSilenceTimeoutPresetCommand ?? (setSilenceTimeoutPresetCommand = new RelayCommand<string>((Action<string>)SetSilenceTimeoutPreset)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RunLatencyCalibrationCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = runLatencyCalibrationCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)RunLatencyCalibration);
				AsyncRelayCommand val2 = val;
				runLatencyCalibrationCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[RelayCommand]
	private async Task RefreshStoreLicenseAsync()
	{
		ApplyStoreLicenseSnapshot(await _storeLicenseService.RefreshAsync().ConfigureAwait(continueOnCapturedContext: true));
		RefreshBackendStatusText();
	}

	private void OnStoreLicenseChanged(object? sender, StoreLicenseSnapshot snapshot)
	{
		Application current = Application.Current;
		Dispatcher val = ((current != null) ? ((DispatcherObject)current).Dispatcher : null);
		if (val != null && !val.CheckAccess())
		{
			val.BeginInvoke((Delegate)(Action)delegate
			{
				ApplyStoreLicenseSnapshot(snapshot);
			}, Array.Empty<object>());
		}
		else
		{
			ApplyStoreLicenseSnapshot(snapshot);
		}
	}

	private void ApplyStoreLicenseSnapshot(StoreLicenseSnapshot snapshot)
	{
		StoreLicenseStatusText = snapshot.StatusText;
		StoreLicenseDetailText = ((snapshot.IsStoreContextAvailable || snapshot.AllowsNewConnections) ? LocalizationManager.Instance.GetString("Release_StoreManaged", "Purchase and trial are managed by Microsoft Store.") : LocalizationManager.Instance.GetString("Release_StoreUnavailable", "Store license is unavailable in this developer build or environment."));
	}

	[RelayCommand]
	private void OpenLogsFolder()
	{
		string path = Path.GetDirectoryName(_diagnosticsService.BackendLogPath) ?? AppDataLocation.AppDataDirectory;
		try
		{
			Directory.CreateDirectory(path);
			Process.Start(new ProcessStartInfo(AppDataLocation.RealPath(path))
			{
				UseShellExecute = true
			});
		}
		catch (Exception)
		{
		}
	}

	[RelayCommand]
	private async Task ExportDiagnosticsAsync()
	{
		StoreLicenseSnapshot storeLicenseSnapshot = await _storeLicenseService.RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
		StoreLicenseStatusText = storeLicenseSnapshot.StatusText;
		await ExportDiagnosticsToFileAsync(storeLicenseSnapshot, showSuccess: true).ConfigureAwait(continueOnCapturedContext: true);
	}

	[RelayCommand(AllowConcurrentExecutions = false)]
	private async Task UploadDiagnosticsAsync()
	{
		LocalizationManager loc = LocalizationManager.Instance;
		(string, string)? tuple = await AppDialogService.Instance.PromptProblemReportAsync(loc.GetString("Release_DiagnosticsUploadConfirmTitle", "Upload diagnostics?"), loc.GetString("Release_DiagnosticsUploadConfirmMessage", "AirStereo2 will upload app, Windows, Store, configuration, and recent backend log information after removing local paths, user names, IP/MAC addresses, and PIN-like values. Captured audio, media, and pairing keys are not included. The report is retained for up to 30 days."), loc.GetString("Release_DiagnosticsProblemLabel", "What went wrong?"), loc.GetString("Release_DiagnosticsProblemPlaceholder", "Describe the problem: what you were doing, which speakers were involved, and what you heard or saw."), loc.GetString("Release_DiagnosticsContactLabel", "Contact (optional)"), loc.GetString("Release_DiagnosticsContactPlaceholder", "Email, if you'd like a reply"), loc.GetString("Release_DiagnosticsUploadAction", "Upload report"), loc.GetString("Dialog_Cancel", "Cancel")).ConfigureAwait(continueOnCapturedContext: true);
		if (!tuple.HasValue)
		{
			return;
		}
		(string Description, string Contact) problemReport = tuple.GetValueOrDefault();
		StoreLicenseSnapshot snapshot;
		try
		{
			snapshot = await _storeLicenseService.RefreshAsync().ConfigureAwait(continueOnCapturedContext: true);
			StoreLicenseStatusText = snapshot.StatusText;
		}
		catch
		{
			snapshot = _storeLicenseService.Current;
		}
		try
		{
			DiagnosticsUploadStatusText = loc.GetString("Release_DiagnosticsPreparing", "Preparing and redacting diagnostics...");
			string sessionSummary = await _diagnosticsService.CollectSessionSummaryAsync().ConfigureAwait(continueOnCapturedContext: true);
			string report = await Task.Run(() => _diagnosticsService.BuildUploadReport(snapshot, sessionSummary)).ConfigureAwait(continueOnCapturedContext: true);
			string orCreateDiagnosticsInstallationId = SettingsManager.GetOrCreateDiagnosticsInstallationId();
			int currentLanguageIndex = LocalizationManager.Instance.CurrentLanguageIndex;
			string locale = ((currentLanguageIndex >= 0 && currentLanguageIndex < LocalizationManager.Instance.SupportedLanguages.Count) ? LocalizationManager.Instance.SupportedLanguages[currentLanguageIndex] : "en-US");
			DiagnosticsUploadStatusText = loc.GetString("Release_DiagnosticsUploading", "Uploading diagnostics...");
			DiagnosticsUploadResult diagnosticsUploadResult = await _diagnosticsUploadService.UploadAsync(orCreateDiagnosticsInstallationId, _diagnosticsService.AppVersion, locale, report, problemReport.Description, problemReport.Contact).ConfigureAwait(continueOnCapturedContext: true);
			bool flag = false;
			try
			{
				Clipboard.SetText(diagnosticsUploadResult.ReportId);
				flag = true;
			}
			catch
			{
			}
			DiagnosticsUploadStatusText = loc.Format("Release_DiagnosticsUploadedStatus", "Uploaded report: {0}", diagnosticsUploadResult.ReportId);
			string message = (flag ? loc.Format("Release_DiagnosticsUploadSuccessCopied", "Report ID: {0}\n\nThe ID was copied to the clipboard. The report will be deleted after {1:d}.", diagnosticsUploadResult.ReportId, diagnosticsUploadResult.RetainedUntil.LocalDateTime) : loc.Format("Release_DiagnosticsUploadSuccess", "Report ID: {0}\n\nThe report will be deleted after {1:d}.", diagnosticsUploadResult.ReportId, diagnosticsUploadResult.RetainedUntil.LocalDateTime));
			await AppDialogService.Instance.ShowInfoAsync(loc.GetString("Release_DiagnosticsUploadSuccessTitle", "Diagnostics uploaded"), message).ConfigureAwait(continueOnCapturedContext: true);
		}
		catch (Exception ex)
		{
			DiagnosticsUploadStatusText = loc.GetString("Release_DiagnosticsUploadFailedStatus", "Upload failed; local export is still available.");
			if (!(await AppDialogService.Instance.ConfirmAsync(loc.GetString("Release_DiagnosticsUploadFailedTitle", "Upload failed"), loc.Format("Release_DiagnosticsUploadFailedMessage", "The diagnostics service could not accept the report: {0}\n\nExport the full report locally and open an email to the developer instead?", ex.Message), loc.GetString("Release_DiagnosticsFallbackAction", "Export and email"), loc.GetString("Dialog_Cancel", "Cancel")).ConfigureAwait(continueOnCapturedContext: true)))
			{
				return;
			}
			string text = await ExportDiagnosticsToFileAsync(snapshot, showSuccess: false).ConfigureAwait(continueOnCapturedContext: true);
			if (text == null)
			{
				return;
			}
			string stringToEscape = loc.GetString("Release_DiagnosticsEmailSubject", "AirStereo2 diagnostics report");
			string stringToEscape2 = loc.Format("Release_DiagnosticsEmailBody", "Please attach the diagnostics file saved at:\n{0}\n\nUpload error: {1}", text, ex.Message);
			if (OpenExternalUrl("mailto:feedback@flourishze.dev?subject=" + Uri.EscapeDataString(stringToEscape) + "&body=" + Uri.EscapeDataString(stringToEscape2)))
			{
				await AppDialogService.Instance.ShowInfoAsync(loc.GetString("Release_DiagnosticsFallbackReadyTitle", "Diagnostics exported"), loc.Format("Release_DiagnosticsFallbackReadyMessage", "The report was saved to:\n{0}\n\nAttach this file to the email draft that was opened.", text)).ConfigureAwait(continueOnCapturedContext: true);
			}
		}
	}

	private async Task<string?> ExportDiagnosticsToFileAsync(StoreLicenseSnapshot snapshot, bool showSuccess)
	{
		LocalizationManager loc = LocalizationManager.Instance;
		SaveFileDialog dlg = new SaveFileDialog
		{
			Title = loc.GetString("Release_ExportDiagnosticsDialogTitle", "Save diagnostics report"),
			Filter = loc.GetString("Release_DiagnosticsFileFilter", "Text file (*.txt)|*.txt|All files (*.*)|*.*"),
			DefaultExt = ".txt",
			FileName = $"airstereo2-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
			AddExtension = true,
			OverwritePrompt = true
		};
		if (dlg.ShowDialog() != true)
		{
			return null;
		}
		try
		{
			string sessionSummary = await _diagnosticsService.CollectSessionSummaryAsync().ConfigureAwait(continueOnCapturedContext: true);
			string path = _diagnosticsService.WriteReport(snapshot, dlg.FileName, sessionSummary);
			if (showSuccess)
			{
				await AppDialogService.Instance.ShowInfoAsync(loc.GetString("Release_DiagnosticsExportedTitle", "Diagnostics exported"), loc.Format("Release_DiagnosticsExportedMessage", "Diagnostics were saved to:\n{0}", path)).ConfigureAwait(continueOnCapturedContext: true);
			}
			return path;
		}
		catch (Exception ex)
		{
			await AppDialogService.Instance.ShowErrorAsync(loc.GetString("Release_DiagnosticsExportFailedTitle", "Export failed"), loc.Format("Release_DiagnosticsExportFailedMessage", "Diagnostics could not be saved: {0}", ex.Message)).ConfigureAwait(continueOnCapturedContext: true);
			return null;
		}
	}

	[RelayCommand]
	private static void OpenWebsite()
	{
		OpenExternalUrl("https://flourishze.dev/");
	}

	[RelayCommand]
	private static void OpenPrivacyPolicy()
	{
		OpenExternalUrl("https://github.com/Flourishze/");
	}

	[RelayCommand]
	private static void OpenSupport()
	{
		OpenExternalUrl("https://github.com/Flourishze/");
	}

	[RelayCommand]
	private static void OpenStoreListing()
	{
		OpenExternalUrl("https://github.com/Flourishze/AirStereo#readme");
	}

	[RelayCommand]
	private static void OpenThirdPartyNotices()
	{
		OpenExternalUrl("https://github.com/Flourishze/AirStereo#md");
	}

	[RelayCommand]
	private static void ReportIssue()
	{
		OpenExternalUrl("mailto:feedback@flourishze.dev");
	}

	private void RefreshBackendStatusText()
	{
		BackendStatusText = (AirPlayStreamer.IsInstanceRunning ? LocalizationManager.Instance.GetString("Release_BackendRunning", "Running") : LocalizationManager.Instance.GetString("Release_BackendNotRunning", "Not running"));
	}

	private static bool OpenExternalUrl(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo(url)
			{
				UseShellExecute = true
			});
			return true;
		}
		catch (Exception ex)
		{
			LocalizationManager instance = LocalizationManager.Instance;
			AppDialogService.Instance.ShowErrorAsync(instance.GetString("Dialog_Error", "Error"), instance.Format("Release_OpenLinkFailed", "Could not open link: {0}", ex.Message));
			return false;
		}
	}

	[RelayCommand]
	private void RefreshCaptureDevices()
	{
		List<AudioDeviceItem> list = _captureService.EnumerateVirtualDevices();
		AvailableCaptureDevices.Clear();
		foreach (AudioDeviceItem item in list)
		{
			AvailableCaptureDevices.Add(item);
		}
		string savedId = SettingsManager.ReadConfig((AppConfig config) => config.SelectedCaptureDeviceId);
		if (string.IsNullOrEmpty(savedId))
		{
			return;
		}
		_suppressCaptureSelectionSideEffects = true;
		try
		{
			SelectedCaptureDevice = AvailableCaptureDevices.FirstOrDefault((AudioDeviceItem d) => d.DeviceId == savedId);
		}
		finally
		{
			_suppressCaptureSelectionSideEffects = false;
		}
	}

	[RelayCommand]
	private async Task RefreshProcesses()
	{
		int generation = Interlocked.Increment(ref _processRefreshGeneration);
		List<ProcessItem> list;
		try
		{
			list = await Task.Run(() => _captureService.EnumerateAudioProcesses()).ConfigureAwait(continueOnCapturedContext: true);
		}
		catch (Exception)
		{
			return;
		}
		if (generation != Volatile.Read(in _processRefreshGeneration))
		{
			if (!_transientCaptureCatalogsActive)
			{
				ProcessMetadataCache.Clear();
			}
			return;
		}
		ProcessItem selectedProcess = SelectedProcess;
		(int, string, string) tuple = SettingsManager.ReadConfig((AppConfig config) => (SelectedProcessId: config.SelectedProcessId, SelectedProcessExecutablePath: config.SelectedProcessExecutablePath, SelectedProcessName: config.SelectedProcessName));
		AvailableProcesses.Clear();
		foreach (ProcessItem item in list)
		{
			AvailableProcesses.Add(item);
		}
		ProcessItem processItem = ((selectedProcess != null) ? FindPersistedProcessSelection(list, selectedProcess.CaptureProcessId, selectedProcess.ExecutablePath, selectedProcess.ProcessName) : FindPersistedProcessSelection(list, tuple.Item1, tuple.Item2, tuple.Item3));
		if (processItem != null)
		{
			_suppressCaptureSelectionSideEffects = true;
			try
			{
				SelectedProcess = processItem;
			}
			finally
			{
				_suppressCaptureSelectionSideEffects = false;
			}
		}
		if (_applyCaptureSourceAfterProcessRefresh)
		{
			_applyCaptureSourceAfterProcessRefresh = false;
			ApplyCaptureSource();
		}
	}

	public void ActivateTransientCaptureCatalogs()
	{
		if (!_disposed && !_transientCaptureCatalogsActive)
		{
			_transientCaptureCatalogsActive = true;
			RefreshActiveCaptureCatalog();
		}
	}

	public void ReleaseTransientCaptureCatalogs()
	{
		_transientCaptureCatalogsActive = false;
		_applyCaptureSourceAfterProcessRefresh = false;
		Interlocked.Increment(ref _processRefreshGeneration);
		AvailableCaptureDevices.Clear();
		AvailableProcesses.Clear();
		SelectedCaptureDevice = null;
		SelectedProcess = null;
		ProcessMetadataCache.Clear();
	}

	private void RefreshActiveCaptureCatalog()
	{
		if (_transientCaptureCatalogsActive)
		{
			Interlocked.Increment(ref _processRefreshGeneration);
			AvailableCaptureDevices.Clear();
			AvailableProcesses.Clear();
			SelectedCaptureDevice = null;
			SelectedProcess = null;
			ProcessMetadataCache.Clear();
			if (CaptureSourceIndex == 1)
			{
				RefreshCaptureDevices();
			}
			else if (CaptureSourceIndex == 2 && IsProcessCaptureSupported)
			{
				RefreshProcesses();
			}
		}
	}

	private async Task RecoverExitedProcessCaptureAsync()
	{
		if (_disposed || SettingsManager.ReadConfig((AppConfig config) => config.CaptureSourceIndex) != 2 || Interlocked.Exchange(ref _processRecoveryInProgress, 1) != 0)
		{
			return;
		}
		try
		{
			(string, string) tuple = SettingsManager.ReadConfig((AppConfig config) => (SelectedProcessExecutablePath: config.SelectedProcessExecutablePath, SelectedProcessName: config.SelectedProcessName));
			string configuredPath = tuple.Item1 ?? string.Empty;
			string configuredName = tuple.Item2 ?? string.Empty;
			if (string.IsNullOrWhiteSpace(configuredPath) && string.IsNullOrWhiteSpace(configuredName))
			{
				return;
			}
			ProcessItem processItem = null;
			int[] array = new int[4] { 1, 2, 5, 10 };
			for (int num = 0; num < array.Length; num++)
			{
				await Task.Delay(TimeSpan.FromSeconds(array[num])).ConfigureAwait(continueOnCapturedContext: false);
				if (_disposed || SettingsManager.ReadConfig((AppConfig config) => config.CaptureSourceIndex) != 2)
				{
					return;
				}
				processItem = FindReplacementProcess(await Task.Run(() => _captureService.EnumerateAudioProcesses()).ConfigureAwait(continueOnCapturedContext: false), configuredPath, configuredName);
				if (processItem == null)
				{
					processItem = await Task.Run(() => _captureService.FindRunningProcessByIdentity(configuredPath, configuredName)).ConfigureAwait(continueOnCapturedContext: false);
				}
				if (processItem != null)
				{
					break;
				}
			}
			ProcessItem recoveredProcess;
			if (processItem != null)
			{
				recoveredProcess = processItem;
				Application current = Application.Current;
				Dispatcher val = ((current != null) ? ((DispatcherObject)current).Dispatcher : null);
				if (val != null && !val.CheckAccess())
				{
					await val.InvokeAsync((Action)ApplyReplacement);
				}
				else
				{
					ApplyReplacement();
				}
			}
			void ApplyReplacement()
			{
				if (!_disposed && SettingsManager.ReadConfig((AppConfig config) => config.CaptureSourceIndex) == 2)
				{
					_suppressCaptureSelectionSideEffects = true;
					try
					{
						SelectedProcess = recoveredProcess;
					}
					finally
					{
						_suppressCaptureSelectionSideEffects = false;
					}
					int[] captureProcess = ((recoveredProcess.CaptureProcessIds.Count > 0) ? recoveredProcess.CaptureProcessIds.ToArray() : new int[1] { recoveredProcess.CaptureProcessId });
					SettingsManager.UpdateConfig(delegate(AppConfig config)
					{
						config.SelectedProcessId = recoveredProcess.CaptureProcessId;
						config.SelectedProcessName = recoveredProcess.ProcessName;
						config.SelectedProcessExecutablePath = recoveredProcess.ExecutablePath;
					});
					AirPlayStreamer.Instance.SetCaptureProcess(captureProcess);
				}
			}
		}
		catch (Exception)
		{
		}
		finally
		{
			Interlocked.Exchange(ref _processRecoveryInProgress, 0);
		}
	}

	internal static ProcessItem? FindReplacementProcess(IEnumerable<ProcessItem> processes, string? configuredPath, string? configuredName)
	{
		string path = configuredPath?.Trim() ?? string.Empty;
		string name = configuredName?.Trim() ?? string.Empty;
		if (path.Length == 0 && name.Length == 0)
		{
			return null;
		}
		if (path.Length > 0)
		{
			return processes.FirstOrDefault((ProcessItem process) => string.Equals(process.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
		}
		if (name.Length != 0)
		{
			return processes.FirstOrDefault((ProcessItem process) => string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase));
		}
		return null;
	}

	internal static ProcessItem? FindPersistedProcessSelection(IEnumerable<ProcessItem> processes, int persistedProcessId, string? configuredPath, string? configuredName)
	{
		string path = configuredPath?.Trim() ?? string.Empty;
		string name = configuredName?.Trim() ?? string.Empty;
		if (path.Length == 0 && name.Length == 0)
		{
			return null;
		}
		IEnumerable<ProcessItem> source = processes.Where((ProcessItem process) => (path.Length <= 0) ? string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase) : string.Equals(process.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
		if (persistedProcessId > 0)
		{
			ProcessItem processItem = source.FirstOrDefault((ProcessItem process) => process.CaptureProcessIds.Contains(persistedProcessId) || process.CaptureProcessId == persistedProcessId || process.ProcessId == persistedProcessId);
			if (processItem != null)
			{
				return processItem;
			}
		}
		return source.FirstOrDefault();
	}

	private void ApplyCaptureSource()
	{
		switch (CaptureSourceIndex)
		{
		default:
			return;
		case 0:
			AirPlayStreamer.Instance.SetCaptureDefault();
			return;
		case 1:
			if (SelectedCaptureDevice != null)
			{
				AirPlayStreamer.Instance.SetCaptureDevice(SelectedCaptureDevice.DeviceId);
				return;
			}
			break;
		case 2:
			if (SelectedProcess != null)
			{
				int[] captureProcess = ((SelectedProcess.CaptureProcessIds.Count > 0) ? SelectedProcess.CaptureProcessIds.ToArray() : new int[1] { SelectedProcess.CaptureProcessId });
				AirPlayStreamer.Instance.SetCaptureProcess(captureProcess);
				return;
			}
			break;
		}
		AirPlayStreamer.Instance.SetCaptureDisabled();
	}

	[RelayCommand]
	private async Task ClearPairings()
	{
		LocalizationManager loc = LocalizationManager.Instance;
		AppDialogService dialogs = AppDialogService.Instance;
		string text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
		int deleted = 0;
		List<string> errors = new List<string>();
		try
		{
			if (!Directory.Exists(text))
			{
				await dialogs.ShowInfoAsync(loc.GetString("Dialog_Info", "Info"), loc.GetString("Pairings_NoneFound", "No paired devices found.")).ConfigureAwait(continueOnCapturedContext: true);
				return;
			}
			string confPath = Path.Combine(text, "pyatv.conf");
			string[] identityPaths = Directory.EnumerateFiles(text, ".airplay_sender_identity_*.json").ToArray();
			if (!File.Exists(confPath) && identityPaths.Length == 0)
			{
				await dialogs.ShowInfoAsync(loc.GetString("Dialog_Info", "Info"), loc.GetString("Pairings_NoneFound", "No paired devices found.")).ConfigureAwait(continueOnCapturedContext: true);
				return;
			}
			if (!(await dialogs.ConfirmAsync(loc.GetString("Pairings_ClearTitle", "Clear Paired Devices"), loc.GetString("Pairings_ClearConfirm", "This will disconnect active AirPlay sessions and delete all saved PIN pairings from this computer. Continue?"), loc.GetString("Pairings_ClearButton", "Clear Pairings"), loc.GetString("Dialog_Cancel", "Cancel"), isDestructive: true).ConfigureAwait(continueOnCapturedContext: true)))
			{
				return;
			}
			if (AirPlayStreamer.IsInstanceRunning)
			{
				AirPlayStreamer.Instance.RemoveAllDevices();
			}
			if (File.Exists(confPath))
			{
				try
				{
					File.Delete(confPath);
					deleted++;
				}
				catch (Exception ex)
				{
					errors.Add(ex.Message);
				}
			}
			string[] array = identityPaths;
			foreach (string path in array)
			{
				try
				{
					File.Delete(path);
					deleted++;
				}
				catch (Exception ex2)
				{
					errors.Add(ex2.Message);
				}
			}
			if (errors.Count > 0)
			{
				await dialogs.ShowErrorAsync(loc.GetString("Dialog_Error", "Error"), loc.Format("Pairings_ClearFailed", "Failed to clear paired devices: {0}", string.Join("; ", errors))).ConfigureAwait(continueOnCapturedContext: true);
			}
			else if (deleted != 0)
			{
				await dialogs.ShowInfoAsync(loc.GetString("Pairings_ClearSuccessTitle", "Success"), loc.GetString("Pairings_ClearSuccess", "All paired devices have been cleared successfully.")).ConfigureAwait(continueOnCapturedContext: true);
			}
			else
			{
				await dialogs.ShowInfoAsync(loc.GetString("Dialog_Info", "Info"), loc.GetString("Pairings_NoneFound", "No paired devices found.")).ConfigureAwait(continueOnCapturedContext: true);
			}
		}
		catch (Exception ex3)
		{
			await dialogs.ShowErrorAsync(loc.GetString("Dialog_Error", "Error"), loc.Format("Pairings_ClearFailed", "Failed to clear paired devices: {0}", ex3.Message)).ConfigureAwait(continueOnCapturedContext: true);
		}
	}

	private void OnBackendEvent(BackendEvent evt)
	{
		string codec;
		if (evt.Is("Capture", "PROCESS_EXITED"))
		{
			if (evt.GetInt64("pid").HasValue)
			{
				RecoverExitedProcessCaptureAsync();
			}
		}
		else
		{
			if (!evt.Is("Codec", "ACTIVE"))
			{
				return;
			}
			string text = evt.GetString("codec");
			if (text == null)
			{
				return;
			}
			codec = AudioCodecPreference.Normalize(text);
			if (AudioCodecPreference.IsValid(text))
			{
				Application current = Application.Current;
				Dispatcher val = ((current != null) ? ((DispatcherObject)current).Dispatcher : null);
				if (val != null && !val.CheckAccess())
				{
					val.BeginInvoke((Delegate)new Action(ApplyActiveCodec), Array.Empty<object>());
				}
				else
				{
					ApplyActiveCodec();
				}
			}
		}
		void ApplyActiveCodec()
		{
			if (!_disposed)
			{
				SettingsManager.SetAudioCodec(codec);
				SyncCodecIndexFromConfig();
			}
		}
	}

	public SettingsViewModel(AudioCaptureService captureService, HomeViewModel homeViewModel, StoreLicenseService storeLicenseService, ReleaseDiagnosticsService diagnosticsService, DiagnosticsUploadService diagnosticsUploadService, LocalOutputMuteService localOutputMuteService)
	{
		WasCreated = true;
		_captureService = captureService;
		_homeViewModel = homeViewModel;
		_storeLicenseService = storeLicenseService;
		_storeLicenseService.Changed += OnStoreLicenseChanged;
		LocalizationManager.Instance.LanguageChanged += RenderNetworkStatus;
		_diagnosticsService = diagnosticsService;
		_diagnosticsUploadService = diagnosticsUploadService;
		_localOutputMuteService = localOutputMuteService;
		AppVersionText = diagnosticsService.AppVersion;
		BackendPathText = diagnosticsService.BackendPath;
		BackendLogPathText = diagnosticsService.BackendLogPath;
		DiagnosticsUploadStatusText = LocalizationManager.Instance.GetString("Release_DiagnosticsReady", "Upload a redacted report or export the full local report.");
		RefreshBackendStatusText();
		_languageIndex = LocalizationManager.Instance.CurrentLanguageIndex;
		(int, int, bool, bool, bool, bool, string, int, bool, int, int, int, bool, bool, bool, bool, bool, bool, int?, int?, int?) tuple = SettingsManager.ReadConfig(delegate(AppConfig config)
		{
			SettingsManager.NormalizeAudioCodecConfig(config);
			return (StreamingModeIndex: config.StreamingModeIndex, CustomBufferSize: config.CustomBufferSize, MuteLocalOutputWhileStreaming: config.MuteLocalOutputWhileStreaming, ReceiverMediaControlEnabled: config.ReceiverMediaControlEnabled, SendNowPlayingMetadata: config.SendNowPlayingMetadata, AutomaticReceiverRecoveryEnabled: config.AutomaticReceiverRecoveryEnabled, AudioCodec: config.AudioCodec, CaptureSourceIndex: config.CaptureSourceIndex, SilenceDetectionEnabled: config.SilenceDetectionEnabled, SilenceTimeoutMinutes: config.SilenceTimeoutMinutes, DefaultWindowModeIndex: config.DefaultWindowModeIndex, DockPlacementIndex: config.DockPlacementIndex, SilentStartEnabled: config.SilentStartEnabled, AutoConnectOnLaunch: config.AutoConnectOnLaunch, DeveloperModeEnabled: config.DeveloperModeEnabled, DevSoloLatencyOverrideEnabled: config.DevSoloLatencyOverrideEnabled, DevGroupLatencyOverrideEnabled: config.DevGroupLatencyOverrideEnabled, DevMixedLatencyOverrideEnabled: config.DevMixedLatencyOverrideEnabled, DevSoloLatencyMs: config.DevSoloLatencyMs, DevGroupLatencyMs: config.DevGroupLatencyMs, DevMixedLatencyMs: config.DevMixedLatencyMs);
		});
		_streamingModeIndex = tuple.Item1;
		_customBufferSize = Math.Clamp(tuple.Item2, 0, 1000);
		_muteLocalOutputWhileStreaming = tuple.Item3;
		_receiverMediaControlEnabled = tuple.Item4;
		_sendNowPlayingMetadata = tuple.Item5;
		_automaticReceiverRecoveryEnabled = tuple.Item6;
		_audioCodecIndex = (string.Equals(tuple.Item7, "PCM", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
		_captureSourceIndex = tuple.Rest.Item1;
		_silenceDetectionEnabled = tuple.Rest.Item2;
		_silenceTimeoutMinutes = tuple.Rest.Item3;
		InitializeEqBandsFromConfig();
		IMessengerExtensions.Register<EqDeviceMemoryApplyMessage>((IMessenger)(object)WeakReferenceMessenger.Default, (IRecipient<EqDeviceMemoryApplyMessage>)(object)this);
		AirPlayStreamer.Instance.EventReceived += OnBackendEvent;
		InitializeDefaultOutputSummary();
		_startOnBoot = false;
		RefreshStartupStateAsync();
		_defaultWindowModeIndex = tuple.Rest.Item4;
		_dockPlacementIndex = tuple.Rest.Item5;
		_silentStartEnabled = tuple.Rest.Item6;
		_autoConnectOnLaunch = tuple.Rest.Item7;
		_developerModeEnabled = tuple.Rest.Rest.Item1;
		_devSoloLatencyOverrideEnabled = tuple.Rest.Rest.Item2;
		_devGroupLatencyOverrideEnabled = tuple.Rest.Rest.Item3;
		_devMixedLatencyOverrideEnabled = tuple.Rest.Rest.Item4;
		_devSoloLatencyText = tuple.Item19?.ToString();
		_devGroupLatencyText = tuple.Item20?.ToString();
		_devMixedLatencyText = tuple.Item21?.ToString();
		ApplyStoreLicenseSnapshot(_storeLicenseService.Current);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		ReleaseTransientCaptureCatalogs();
		if (_storeLicenseService != null)
		{
			_storeLicenseService.Changed -= OnStoreLicenseChanged;
		}
		LocalizationManager.Instance.LanguageChanged -= RenderNetworkStatus;
		AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
		ReleaseDefaultOutputSummary();
		WeakReferenceMessenger.Default.UnregisterAll((object)this);
		lock (_latencyDebounceCtsLock)
		{
			_latencyDebounceCts?.Cancel();
			_latencyDebounceCts?.Dispose();
			_latencyDebounceCts = null;
		}
		lock (_eqBandDebounceCtsLock)
		{
			_eqBandDebounceCts?.Cancel();
			_eqBandDebounceCts?.Dispose();
			_eqBandDebounceCts = null;
		}
	}

	private async Task RefreshStartupStateAsync()
	{
		bool startOnBoot = await _startupService.IsRegisteredAsync().ConfigureAwait(continueOnCapturedContext: true);
		_suppressStartupSideEffects = true;
		try
		{
			StartOnBoot = startOnBoot;
		}
		finally
		{
			_suppressStartupSideEffects = false;
		}
	}

	private async Task ApplyStartOnBootAsync(bool value)
	{
		(bool, string) tuple = ((!value) ? (await _startupService.TryDisableAsync().ConfigureAwait(continueOnCapturedContext: true)) : (await _startupService.TryEnableAsync().ConfigureAwait(continueOnCapturedContext: true)));
		(bool, string) tuple2 = tuple;
		if (!tuple2.Item1)
		{
			_suppressStartupSideEffects = true;
			try
			{
				StartOnBoot = !value;
			}
			finally
			{
				_suppressStartupSideEffects = false;
			}
			LocalizationManager instance = LocalizationManager.Instance;
			string item = tuple2.Item2;
			string text = ((item == "startup_disabled_by_user") ? instance.GetString("General_StartOnBootDisabledByUser", "Windows has disabled this startup task. Re-enable AirStereo2 in Settings > Apps > Startup, then try again.") : ((!(item == "startup_disabled_by_policy")) ? (tuple2.Item2 ?? "unknown") : instance.GetString("General_StartOnBootDisabledByPolicy", "This startup task is disabled by Windows policy.")));
			string text2 = text;
			await AppDialogService.Instance.ShowErrorAsync(instance.GetString("Dialog_Error", "Error"), instance.Format("General_StartOnBootFailed", "Could not configure Start with Windows: {0}", text2)).ConfigureAwait(continueOnCapturedContext: true);
		}
	}

	private void InitializeDefaultOutputSummary()
	{
		IMessengerExtensions.Register<SettingsViewModel, DefaultOutputsChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, this, (MessageHandler<SettingsViewModel, DefaultOutputsChangedMessage>)delegate(SettingsViewModel recipient, DefaultOutputsChangedMessage _)
		{
			recipient.RefreshDefaultOutputSummary();
		});
		_homeViewModel.DisplayItems.CollectionChanged += OnHomeDisplayItemsChanged;
		LocalizationManager.Instance.LanguageChanged += RefreshDefaultOutputSummary;
		RefreshDefaultOutputSummary();
	}

	private void ReleaseDefaultOutputSummary()
	{
		_homeViewModel.DisplayItems.CollectionChanged -= OnHomeDisplayItemsChanged;
		LocalizationManager.Instance.LanguageChanged -= RefreshDefaultOutputSummary;
	}

	private void OnHomeDisplayItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		RefreshDefaultOutputSummary();
	}

	internal void RefreshDefaultOutputEntries()
	{
		if (_disposed)
		{
			return;
		}
		List<DefaultOutputEntry> desired = _homeViewModel.DisplayItems.ToList().Select(delegate(object item)
		{
			DefaultOutputEntry defaultOutputEntry = DefaultOutputEntries.FirstOrDefault((DefaultOutputEntry entry) => entry.Represents(item));
			if (defaultOutputEntry == null)
			{
				DefaultOutputEntry defaultOutputEntry2 = ((item is DeviceGroupViewModel deviceGroupViewModel) ? new DefaultOutputEntry(deviceGroupViewModel) : ((!(item is DeviceViewModel device)) ? null : new DefaultOutputEntry(device)));
				defaultOutputEntry = defaultOutputEntry2;
			}
			return defaultOutputEntry;
		}).OfType<DefaultOutputEntry>()
			.ToList();
		HomeViewModel.ReconcileDisplayItems(DefaultOutputEntries, desired);
		foreach (DefaultOutputEntry defaultOutputEntry3 in DefaultOutputEntries)
		{
			defaultOutputEntry3.Refresh();
		}
	}

	internal void RefreshDefaultOutputSummary()
	{
		if (_disposed)
		{
			return;
		}
		RefreshDefaultOutputEntries();
		List<DeviceViewModel> devices = _homeViewModel.Devices.ToList();
		List<DefaultOutputSummary.Group> groups = _homeViewModel.Groups.Select((DeviceGroupViewModel group) => new DefaultOutputSummary.Group(group.Name, group.MemberStableIds, group.IsNativeStereoPair)).ToList();
		IReadOnlyList<string> names = DefaultOutputSummary.Names(SettingsManager.GetDefaultOutputDevices(), (string id) => devices.FirstOrDefault((DeviceViewModel device) => AirPlayDeviceIdentity.IdsEqual(device.StableId, id) && !string.IsNullOrWhiteSpace(device.Name))?.Name, groups);
		DefaultOutputSummaryText = DefaultOutputSummary.Text(names, LocalizationManager.Instance.GetString("Device_DefaultOutputNone", "None selected"));
	}

	private void UpdateDevOverride(string configProperty, string mode, string? text, bool enabled)
	{
		int? parsed = ParseDevLatencyText(text);
		bool num = SettingsManager.ReadConfig((AppConfig config) => config.DeveloperModeEnabled);
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			if (configProperty == "DevSoloLatencyMs")
			{
				config.DevSoloLatencyMs = parsed;
			}
			else if (configProperty == "DevGroupLatencyMs")
			{
				config.DevGroupLatencyMs = parsed;
			}
			else
			{
				config.DevMixedLatencyMs = parsed;
			}
		});
		if (num && enabled)
		{
			SendDevOverride(mode, parsed);
		}
	}

	private static void UpdateDevOverrideEnabled(string configProperty, string mode, bool enabled, string? text)
	{
		int? num = ParseDevLatencyText(text);
		bool num2 = SettingsManager.ReadConfig((AppConfig config) => config.DeveloperModeEnabled);
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			if (configProperty == "DevSoloLatencyOverrideEnabled")
			{
				config.DevSoloLatencyOverrideEnabled = enabled;
			}
			else if (configProperty == "DevGroupLatencyOverrideEnabled")
			{
				config.DevGroupLatencyOverrideEnabled = enabled;
			}
			else
			{
				config.DevMixedLatencyOverrideEnabled = enabled;
			}
		});
		if (num2)
		{
			AirPlayStreamer.Instance.SetDevPtpLatency(mode, enabled ? num : ((int?)null));
		}
	}

	private static void SendDevOverride(string mode, int? ms)
	{
		AirPlayStreamer.Instance.SetDevPtpLatency(mode, ms);
	}

	[RelayCommand]
	private void SetDevSoloPreset(string msStr)
	{
		if (int.TryParse(msStr, out var result))
		{
			DevSoloLatencyText = result.ToString();
			DevSoloLatencyOverrideEnabled = true;
		}
	}

	[RelayCommand]
	private void SetDevGroupPreset(string msStr)
	{
		if (int.TryParse(msStr, out var result))
		{
			DevGroupLatencyText = result.ToString();
			DevGroupLatencyOverrideEnabled = true;
		}
	}

	[RelayCommand]
	private void SetDevMixedPreset(string msStr)
	{
		if (int.TryParse(msStr, out var result))
		{
			DevMixedLatencyText = result.ToString();
			DevMixedLatencyOverrideEnabled = true;
		}
	}

	[RelayCommand]
	private void AdjustDevSoloLatency(string deltaStr)
	{
		if (int.TryParse(deltaStr, out var result))
		{
			int devSoloLatencyValue = DevSoloLatencyValue;
			DevSoloLatencyValue = Math.Clamp(devSoloLatencyValue + result, 0, 500);
			DevSoloLatencyOverrideEnabled = true;
		}
	}

	[RelayCommand]
	private void AdjustDevGroupLatency(string deltaStr)
	{
		if (int.TryParse(deltaStr, out var result))
		{
			int devGroupLatencyValue = DevGroupLatencyValue;
			DevGroupLatencyValue = Math.Clamp(devGroupLatencyValue + result, 0, 500);
			DevGroupLatencyOverrideEnabled = true;
		}
	}

	[RelayCommand]
	private void AdjustDevMixedLatency(string deltaStr)
	{
		if (int.TryParse(deltaStr, out var result))
		{
			int devMixedLatencyValue = DevMixedLatencyValue;
			DevMixedLatencyValue = Math.Clamp(devMixedLatencyValue + result, 0, 500);
			DevMixedLatencyOverrideEnabled = true;
		}
	}

	[RelayCommand]
	private void ResetAllDevOverrides()
	{
		DevSoloLatencyOverrideEnabled = false;
		DevGroupLatencyOverrideEnabled = false;
		DevMixedLatencyOverrideEnabled = false;
	}

	internal static int? ParseDevLatencyText(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		if (!int.TryParse(text.Trim(), out var result))
		{
			return null;
		}
		return Math.Clamp(result, 0, 1000);
	}

	[RelayCommand]
	private void ResetEqPreamp()
	{
		EqPreampDb = 0.0;
	}

	private void OnEqBandGainChanged(EqBandViewModel band)
	{
		if (!_suppressEqSideEffects)
		{
			double gainDb = SettingsManager.SetEqBandGain(band.Index, band.GainDb);
			SettingsManager.Save();
			QueueDebouncedEqBand(band.Index, gainDb);
			UpdateEqPresetSelectionFromGains();
			PersistEqMemoryForConnectedDevices();
		}
	}

	[RelayCommand]
	private void ResetEqBand(EqBandViewModel? band)
	{
		if (band != null && EqEnabled)
		{
			band.GainDb = 0.0;
		}
	}

	private void QueueDebouncedEqBand(int index, double gainDb)
	{
		lock (_pendingEqBandSends)
		{
			_pendingEqBandSends[index] = gainDb;
		}
		CancellationToken token;
		lock (_eqBandDebounceCtsLock)
		{
			try
			{
				_eqBandDebounceCts?.Cancel();
				_eqBandDebounceCts?.Dispose();
			}
			catch
			{
			}
			_eqBandDebounceCts = new CancellationTokenSource();
			token = _eqBandDebounceCts.Token;
		}
		FlushDebouncedEqBandsAsync(token);
	}

	private async Task FlushDebouncedEqBandsAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(60, token);
		}
		catch (TaskCanceledException)
		{
			return;
		}
		if (token.IsCancellationRequested)
		{
			return;
		}
		Dictionary<int, double> snapshot;
		lock (_pendingEqBandSends)
		{
			snapshot = new Dictionary<int, double>(_pendingEqBandSends);
			_pendingEqBandSends.Clear();
		}
		if (snapshot.Count == 0)
		{
			return;
		}
		TrySendEqCommand(delegate
		{
			foreach (KeyValuePair<int, double> item in snapshot.OrderBy((KeyValuePair<int, double> k) => k.Key))
			{
				AirPlayStreamer.Instance.SetEqBand(item.Key, item.Value);
			}
		});
	}

	private void FlushPendingEqBandsNow()
	{
		lock (_eqBandDebounceCtsLock)
		{
			try
			{
				_eqBandDebounceCts?.Cancel();
				_eqBandDebounceCts?.Dispose();
				_eqBandDebounceCts = null;
			}
			catch
			{
			}
		}
		Dictionary<int, double> snapshot;
		lock (_pendingEqBandSends)
		{
			snapshot = new Dictionary<int, double>(_pendingEqBandSends);
			_pendingEqBandSends.Clear();
		}
		if (snapshot.Count == 0)
		{
			return;
		}
		TrySendEqCommand(delegate
		{
			foreach (KeyValuePair<int, double> item in snapshot.OrderBy((KeyValuePair<int, double> k) => k.Key))
			{
				AirPlayStreamer.Instance.SetEqBand(item.Key, item.Value);
			}
		});
	}

	private static void TrySendEqCommand(Action send)
	{
		try
		{
			send();
		}
		catch (Exception)
		{
		}
	}

	[RelayCommand]
	private void ResetEq()
	{
		FlushPendingEqBandsNow();
		EqPresetItem eqPresetItem = EqPresetItem.FromBuiltIn(EqBuiltInPreset.Find("flat") ?? EqBuiltInPreset.All[0]);
		ApplyEqGains(eqPresetItem.GainsDb, eqPresetItem.Id, pushToBackend: true);
		SelectPresetById(eqPresetItem.Id);
	}

	private static string FormatEqGainDisplay(double gainDb)
	{
		LocalizationManager instance = LocalizationManager.Instance;
		double num = Math.Round(gainDb, 1);
		if (Math.Abs(num) < 0.05)
		{
			return instance.GetString("Eq_GainZero", "0 dB");
		}
		if (num > 0.0)
		{
			return instance.Format("Eq_GainPositive", "+{0} dB", num.ToString("0.#"));
		}
		return instance.Format("Eq_GainNegative", "{0} dB", num.ToString("0.#"));
	}

	[RelayCommand]
	private async Task SaveEqPreset()
	{
		FlushPendingEqBandsNow();
		LocalizationManager instance = LocalizationManager.Instance;
		string defaultValue = ((SelectedEqPreset?.IsUser ?? false) ? SelectedEqPreset.DisplayName : SuggestNextUserPresetName());
		string text = EqLayout.SanitizePresetName(await SimpleTextInputDialog.PromptAsync(instance.GetString("Eq_SaveDialogTitle", "Save EQ Preset"), instance.GetString("Eq_SaveDialogMessage", "Name for this curve (saved in app settings):"), defaultValue, instance.GetString("Dialog_OK", "OK"), instance.GetString("Dialog_Cancel", "Cancel")).ConfigureAwait(continueOnCapturedContext: true));
		if (!string.IsNullOrEmpty(text))
		{
			double[] gains = EqBands.Select((EqBandViewModel b) => b.GainDb).ToArray();
			SettingsManager.SaveUserPreset(text, gains);
			RebuildEqPresetList(preserveSelection: false);
			SelectPresetById(EqPresetItem.UserId(text));
			EqIsCustom = false;
			CanDeleteEqPreset = true;
			this.OnPropertyChanged("EqPresetStatusText");
		}
	}

	[RelayCommand]
	private async Task DeleteEqPreset()
	{
		EqPresetItem? selectedEqPreset = SelectedEqPreset;
		if (selectedEqPreset != null && selectedEqPreset.IsUser)
		{
			string name = SelectedEqPreset.DisplayName;
			LocalizationManager instance = LocalizationManager.Instance;
			if (await AppDialogService.Instance.ConfirmAsync(instance.GetString("Eq_DeleteDialogTitle", "Delete EQ Preset"), instance.Format("Eq_DeleteConfirm", "Delete user preset \"{0}\"?", name), instance.GetString("Dialog_Yes", "Yes"), instance.GetString("Dialog_No", "No"), isDestructive: true).ConfigureAwait(continueOnCapturedContext: true))
			{
				SettingsManager.DeleteUserPreset(name);
				RebuildEqPresetList(preserveSelection: false);
				UpdateEqPresetSelectionFromGains();
			}
		}
	}

	private static string SuggestNextUserPresetName()
	{
		HashSet<string> hashSet = new HashSet<string>(SettingsManager.ReadEqConfigSnapshot().UserPresets.Select((EqUserPresetConfig p) => p.Name), StringComparer.OrdinalIgnoreCase);
		string text = LocalizationManager.Instance.GetString("Eq_UserPresetPrefix", "User");
		for (int num = 1; num <= 24; num++)
		{
			string text2 = $"{text} {num}";
			if (!hashSet.Contains(text2))
			{
				return text2;
			}
		}
		return text;
	}

	private void ApplyEqGains(IReadOnlyList<double> gains, string presetId, bool pushToBackend)
	{
		FlushPendingEqBandsNow();
		double[] normalized = EqLayout.NormalizeGains(gains);
		_suppressEqSideEffects = true;
		try
		{
			for (int i = 0; i < EqBands.Count && i < normalized.Length; i++)
			{
				EqBands[i].SetGainSilent(normalized[i]);
			}
		}
		finally
		{
			_suppressEqSideEffects = false;
		}
		SettingsManager.SetEqBandGains(normalized, presetId);
		SettingsManager.Save();
		if (pushToBackend)
		{
			TrySendEqCommand(delegate
			{
				for (int j = 0; j < 10; j++)
				{
					AirPlayStreamer.Instance.SetEqBand(j, normalized[j]);
				}
			});
		}
		UpdateEqPresetSelectionFromGains();
		PersistEqMemoryForConnectedDevices();
	}

	public void Receive(EqDeviceMemoryApplyMessage message)
	{
		if (message?.Snapshot != null)
		{
			_pendingEqSnapshots.Enqueue(message.Snapshot);
			ScheduleEqSnapshotDrain();
		}
	}

	private void ScheduleEqSnapshotDrain()
	{
		if (!OrderedDiscoveryDispatch.TrySchedule(ref _eqSnapshotDrainScheduled))
		{
			return;
		}
		Application current = Application.Current;
		Dispatcher val = ((current != null) ? ((DispatcherObject)current).Dispatcher : null);
		if (val == null)
		{
			OrderedDiscoveryDispatch.ClearScheduled(ref _eqSnapshotDrainScheduled);
			DeviceEqMemorySnapshot result;
			while (_pendingEqSnapshots.TryDequeue(out result))
			{
			}
			return;
		}
		try
		{
			val.BeginInvoke((DispatcherPriority)9, (Delegate)new Action(DrainPendingEqSnapshots));
		}
		catch (Exception)
		{
			OrderedDiscoveryDispatch.ClearScheduled(ref _eqSnapshotDrainScheduled);
		}
	}

	private void DrainPendingEqSnapshots()
	{
		try
		{
			DeviceEqMemorySnapshot result;
			while (_pendingEqSnapshots.TryDequeue(out result))
			{
				try
				{
					_suppressDeviceEqMemoryWrite = true;
					try
					{
						ApplyDeviceEqSnapshot(result);
					}
					finally
					{
						_suppressDeviceEqMemoryWrite = false;
					}
				}
				catch (Exception)
				{
					_suppressDeviceEqMemoryWrite = false;
				}
			}
		}
		finally
		{
			OrderedDiscoveryDispatch.ClearScheduled(ref _eqSnapshotDrainScheduled);
			if (OrderedDiscoveryDispatch.ShouldRearmAfterDrain(_pendingEqSnapshots.IsEmpty))
			{
				ScheduleEqSnapshotDrain();
			}
		}
	}

	private void ApplyDeviceEqSnapshot(DeviceEqMemorySnapshot snapshot)
	{
		FlushPendingEqBandsNow();
		RebuildEqPresetList(preserveSelection: false);
		ApplyEqGains(snapshot.BandGains, snapshot.PresetId, pushToBackend: true);
		_suppressEqSideEffects = true;
		try
		{
			EqPreampDb = snapshot.PreampDb;
		}
		finally
		{
			_suppressEqSideEffects = false;
		}
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.EqPreampDb = snapshot.PreampDb;
		});
		TrySendEqCommand(delegate
		{
			AirPlayStreamer.Instance.SetEqPreamp(snapshot.PreampDb);
		});
		this.OnPropertyChanged("EqPreampText");
		SelectPresetById(snapshot.PresetId);
	}

	private void PersistEqMemoryForConnectedDevices()
	{
		if (_suppressDeviceEqMemoryWrite || _suppressEqSideEffects)
		{
			return;
		}
		try
		{
			List<string> connectedDeviceIps = GetConnectedDeviceIps();
			if (connectedDeviceIps.Count == 0)
			{
				return;
			}
			EqConfigSnapshot eqConfigSnapshot = SettingsManager.ReadEqConfigSnapshot();
			foreach (string item in connectedDeviceIps)
			{
				SettingsManager.RememberDeviceEq(item, eqConfigSnapshot.PresetId, eqConfigSnapshot.PreampDb, eqConfigSnapshot.BandGains);
			}
		}
		catch (Exception)
		{
		}
	}

	private static List<string> GetConnectedDeviceIps()
	{
		try
		{
			return (from d in CoreApp.GetRequiredService<HomeViewModel>().Devices
				where d.IsConnected && !string.IsNullOrWhiteSpace(d.IPAddress)
				select d.IPAddress).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		}
		catch
		{
			return new List<string>();
		}
	}

	[RelayCommand]
	private async Task ExportEqPresets()
	{
		LocalizationManager loc = LocalizationManager.Instance;
		SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			Title = loc.GetString("Eq_ExportDialogTitle", "Export EQ Presets"),
			Filter = loc.GetString("Eq_PresetFileFilter", "EQ preset package (*.json)|*.json|All files (*.*)|*.*"),
			DefaultExt = ".json",
			FileName = "airstereo2-eq-presets.json",
			AddExtension = true,
			OverwritePrompt = true
		};
		if (saveFileDialog.ShowDialog() == true)
		{
			try
			{
				string contents = SettingsManager.ExportEqPresetsJson();
				File.WriteAllText(saveFileDialog.FileName, contents);
				await AppDialogService.Instance.ShowInfoAsync(loc.GetString("Eq_ExportDialogTitle", "Export EQ Presets"), loc.GetString("Eq_ExportSuccess", "EQ presets exported successfully.")).ConfigureAwait(continueOnCapturedContext: true);
			}
			catch (Exception ex)
			{
				await AppDialogService.Instance.ShowErrorAsync(loc.GetString("Eq_ExportDialogTitle", "Export EQ Presets"), loc.Format("Eq_ExportFailed", "Export failed: {0}", ex.Message)).ConfigureAwait(continueOnCapturedContext: true);
			}
		}
	}

	[RelayCommand]
	private async Task ImportEqPresets()
	{
		LocalizationManager loc = LocalizationManager.Instance;
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Title = loc.GetString("Eq_ImportDialogTitle", "Import EQ Presets"),
			Filter = loc.GetString("Eq_PresetFileFilter", "EQ preset package (*.json)|*.json|All files (*.*)|*.*"),
			DefaultExt = ".json",
			CheckFileExists = true,
			Multiselect = false
		};
		if (openFileDialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			EqPresetImportResult eqPresetImportResult = SettingsManager.ImportEqPresetsJson(File.ReadAllText(openFileDialog.FileName));
			RebuildEqPresetList();
			UpdateEqPresetSelectionFromGains();
			if (!eqPresetImportResult.Success)
			{
				await AppDialogService.Instance.ShowWarningAsync(loc.GetString("Eq_ImportDialogTitle", "Import EQ Presets"), BackendFailureFormatter.FormatEqImportError(eqPresetImportResult.Message)).ConfigureAwait(continueOnCapturedContext: true);
				return;
			}
			string text = ((eqPresetImportResult.ImportedUserCount != 0) ? loc.Format("Eq_ImportCount", "Imported {0} user preset(s).", eqPresetImportResult.ImportedUserCount) : ((eqPresetImportResult.SkippedCount > 0) ? loc.GetString("Eq_ImportNoneSkipped", "No new user presets imported (built-ins ignored or names full).") : loc.GetString("Eq_ImportNone", "No user presets imported.")));
			string message = loc.Format("Eq_ImportSuccessDetail", "{0}\nImported: {1}, renamed: {2}, skipped: {3}", text, eqPresetImportResult.ImportedUserCount, eqPresetImportResult.RenamedCount, eqPresetImportResult.SkippedCount);
			await AppDialogService.Instance.ShowInfoAsync(loc.GetString("Eq_ImportDialogTitle", "Import EQ Presets"), message).ConfigureAwait(continueOnCapturedContext: true);
		}
		catch (Exception ex)
		{
			await AppDialogService.Instance.ShowErrorAsync(loc.GetString("Eq_ImportDialogTitle", "Import EQ Presets"), BackendFailureFormatter.FormatEqImportError(ex.Message)).ConfigureAwait(continueOnCapturedContext: true);
		}
	}

	private void RebuildEqPresetList(bool preserveSelection = true)
	{
		SettingsManager.NormalizeCurrentEqConfig();
		string previousId = SelectedEqPreset?.Id;
		LocalizationManager instance = LocalizationManager.Instance;
		EqPresets.Clear();
		foreach (EqBuiltInPreset item in EqBuiltInPreset.All)
		{
			string displayName = instance.GetString("Eq_Preset_" + item.Id, item.DisplayName);
			EqPresets.Add(EqPresetItem.FromBuiltIn(item, displayName));
		}
		foreach (EqUserPresetConfig userPreset in SettingsManager.ReadEqConfigSnapshot().UserPresets)
		{
			EqPresets.Add(EqPresetItem.FromUser(userPreset.Name, userPreset.Gains));
		}
		if (preserveSelection && !string.IsNullOrEmpty(previousId))
		{
			SelectPresetById(previousId);
		}
		else if (!preserveSelection && !string.IsNullOrEmpty(previousId) && EqPresets.All((EqPresetItem p) => p.Id != previousId))
		{
			_suppressEqSideEffects = true;
			try
			{
				SelectedEqPreset = null;
				CanDeleteEqPreset = false;
				EqIsCustom = true;
			}
			finally
			{
				_suppressEqSideEffects = false;
			}
		}
	}

	private void SelectPresetById(string? id)
	{
		EqConfigSnapshot eqConfigSnapshot = SettingsManager.ReadEqConfigSnapshot();
		id = SettingsManager.NormalizeEqPresetId(id, eqConfigSnapshot.UserPresets);
		_suppressEqSideEffects = true;
		try
		{
			SelectedEqPreset = ((id == "custom") ? null : EqPresets.FirstOrDefault((EqPresetItem p) => p.Id == id));
			if (SelectedEqPreset == null && id != "custom")
			{
				id = "custom";
			}
			CanDeleteEqPreset = SelectedEqPreset?.IsUser ?? false;
			EqIsCustom = SelectedEqPreset == null;
			if (eqConfigSnapshot.PresetId != id)
			{
				string normalizedId = id;
				SettingsManager.UpdateConfig(delegate(AppConfig config)
				{
					config.EqPresetId = normalizedId;
				});
			}
		}
		finally
		{
			_suppressEqSideEffects = false;
		}
		this.OnPropertyChanged("EqPresetStatusText");
	}

	private void UpdateEqPresetSelectionFromGains()
	{
		List<double> gains = EqBands.Select((EqBandViewModel b) => b.GainDb).ToList();
		EqConfigSnapshot eqConfigSnapshot = SettingsManager.ReadEqConfigSnapshot();
		string matched = EqPreset.MatchId(gains, eqConfigSnapshot.UserPresets);
		matched = SettingsManager.NormalizeEqPresetId(matched, eqConfigSnapshot.UserPresets);
		EqIsCustom = matched == "custom";
		_suppressEqSideEffects = true;
		try
		{
			SelectedEqPreset = (EqIsCustom ? null : EqPresets.FirstOrDefault((EqPresetItem p) => p.Id == matched));
			if (!EqIsCustom && SelectedEqPreset == null)
			{
				EqIsCustom = true;
				matched = "custom";
			}
			CanDeleteEqPreset = SelectedEqPreset?.IsUser ?? false;
		}
		finally
		{
			_suppressEqSideEffects = false;
		}
		if (eqConfigSnapshot.PresetId != matched)
		{
			string matchedCopy = matched;
			SettingsManager.UpdateConfig(delegate(AppConfig config)
			{
				config.EqPresetId = matchedCopy;
			});
		}
		this.OnPropertyChanged("EqPresetStatusText");
	}

	private void InitializeEqBandsFromConfig()
	{
		SettingsManager.NormalizeCurrentEqConfig();
		RebuildEqPresetList(preserveSelection: false);
		EqBands.Clear();
		EqConfigSnapshot eqConfigSnapshot = SettingsManager.ReadEqConfigSnapshot();
		IReadOnlyList<double> bandGains = eqConfigSnapshot.BandGains;
		for (int i = 0; i < 10; i++)
		{
			double gainDb = ((i < bandGains.Count) ? bandGains[i] : 0.0);
			EqBands.Add(new EqBandViewModel(i, gainDb, OnEqBandGainChanged));
		}
		string matched = EqPreset.MatchId(bandGains, eqConfigSnapshot.UserPresets);
		matched = SettingsManager.NormalizeEqPresetId(matched, eqConfigSnapshot.UserPresets);
		EqIsCustom = matched == "custom";
		_suppressEqSideEffects = true;
		try
		{
			EqEnabled = eqConfigSnapshot.Enabled;
			EqPreampDb = eqConfigSnapshot.PreampDb;
			SelectedEqPreset = (EqIsCustom ? null : EqPresets.FirstOrDefault((EqPresetItem p) => p.Id == matched));
			CanDeleteEqPreset = SelectedEqPreset?.IsUser ?? false;
		}
		finally
		{
			_suppressEqSideEffects = false;
		}
		this.OnPropertyChanged("EqPreampText");
		if (eqConfigSnapshot.PresetId != matched)
		{
			string matchedCopy2 = matched;
			SettingsManager.UpdateConfig(delegate(AppConfig config)
			{
				config.EqPresetId = matchedCopy2;
			});
		}
		this.OnPropertyChanged("EqPresetStatusText");
	}

	[RelayCommand(AllowConcurrentExecutions = false)]
	private async Task CheckNetworkAsync()
	{
		CanFixFirewall = false;
		ShowNetworkStatus(null, ("Network_Check_Running", "Checking…"));
		NetworkCheckResult result = await FirewallFixFlow.CheckAsync(NetworkCheckTargets()).ConfigureAwait(continueOnCapturedContext: true);
		ShowNetworkStatus(result, null);
		CanFixFirewall = FirewallFix.PlanFor(result) != null;
	}

	[RelayCommand(AllowConcurrentExecutions = false)]
	private async Task FixFirewallAsync()
	{
		FirewallFixFlowResult firewallFixFlowResult = await FirewallFixFlow.RunAsync(NetworkCheckTargets(), AppDialogService.Instance).ConfigureAwait(continueOnCapturedContext: true);
		if (firewallFixFlowResult.Outcome == FirewallFixOutcome.Applied && firewallFixFlowResult.After == null)
		{
			ShowNetworkStatus(null, ("Network_FixFirewall_Applied", "Windows Firewall now lets speakers reach AirStereo2. Connect again."));
			CanFixFirewall = false;
		}
		else if (firewallFixFlowResult.After != null)
		{
			ShowNetworkStatus(firewallFixFlowResult.After, null);
			CanFixFirewall = FirewallFix.PlanFor(firewallFixFlowResult.After) != null;
		}
	}

	private IReadOnlyList<string> NetworkCheckTargets()
	{
		return FirewallFixFlow.SettingsCheckTargets(from device in _homeViewModel.Devices
			where device.HasKnownIpAddress
			select device.IPAddress);
	}

	private void ShowNetworkStatus(NetworkCheckResult? result, (string Key, string English)? message)
	{
		_shownNetworkCheck = result;
		_networkStatusMessage = message;
		_networkStatusShown = true;
		RenderNetworkStatus();
	}

	private void RenderNetworkStatus()
	{
		if (_networkStatusShown)
		{
			LocalizationManager instance = LocalizationManager.Instance;
			(string, string)? networkStatusMessage = _networkStatusMessage;
			if (networkStatusMessage.HasValue)
			{
				(string, string) valueOrDefault = networkStatusMessage.GetValueOrDefault();
				NetworkCheckStatusText = instance.GetString(valueOrDefault.Item1, valueOrDefault.Item2);
				return;
			}
			(string Key, string English, string? HintCode) tuple = NetworkCheckSummary(_shownNetworkCheck);
			string item = tuple.Key;
			string item2 = tuple.English;
			string item3 = tuple.HintCode;
			NetworkCheckStatusText = ((item3 != null) ? (NetworkCheckHint.Text(item3) ?? item2) : instance.GetString(item, item2));
		}
	}

	internal static (string Key, string English, string? HintCode) NetworkCheckSummary(NetworkCheckResult? result)
	{
		if (result == null)
		{
			return (Key: "Network_Check_Unavailable", English: "The network check could not run: the audio engine did not start or answer in time. Try again.", HintCode: null);
		}
		if (result.Status == "timeout")
		{
			return (Key: "Network_Check_TimedOut", English: "The network check took too long. Try again.", HintCode: null);
		}
		NetworkFinding networkFinding = result.Findings.FirstOrDefault(delegate(NetworkFinding finding)
		{
			string severity = finding.Severity;
			return ((severity == "error" || severity == "warning") ? true : false) && NetworkCheckHint.IsKnown(finding.Code);
		});
		if (networkFinding != null)
		{
			return (Key: string.Empty, English: string.Empty, HintCode: networkFinding.Code);
		}
		return (Key: "Network_Check_Ok", English: "No problems found: speakers on this network can reach this PC.", HintCode: null);
	}

	[RelayCommand]
	private void SetSilenceTimeoutPreset(string minutesStr)
	{
		if (int.TryParse(minutesStr, out var result))
		{
			SilenceTimeoutMinutes = Math.Clamp(result, 1, 1440);
		}
	}

	private void DebounceSendLatency(int latency, int delayMs = 300)
	{
		CancellationTokenSource cancellationTokenSource;
		lock (_latencyDebounceCtsLock)
		{
			try
			{
				_latencyDebounceCts?.Cancel();
				_latencyDebounceCts?.Dispose();
			}
			catch
			{
			}
			cancellationTokenSource = (_latencyDebounceCts = new CancellationTokenSource());
		}
		int generation = Interlocked.Increment(ref _latencyDebounceGeneration);
		SendLatencyDebouncedAsync(latency, delayMs, generation, cancellationTokenSource.Token);
	}

	private async Task SendLatencyDebouncedAsync(int latency, int delayMs, int generation, CancellationToken token)
	{
		try
		{
			if (delayMs > 0)
			{
				await Task.Delay(delayMs, token).ConfigureAwait(continueOnCapturedContext: false);
			}
			if (LatencyDebounce.ShouldSend(generation, Volatile.Read(in _latencyDebounceGeneration), token.IsCancellationRequested, _lastSentLatency, latency))
			{
				AirPlayStreamer.Instance.SetLatency(latency);
				_lastSentLatency = latency;
				IMessengerExtensions.Send<LatencyChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, new LatencyChangedMessage(latency));
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception)
		{
		}
	}

	private async Task ConfirmCodecChangeAsync(int value, string previous, string requested)
	{
		if (Interlocked.CompareExchange(ref _isConfirmingCodecChange, 1, 0) != 0)
		{
			_suppressCodecSideEffects = true;
			try
			{
				AudioCodecIndex = (string.Equals(previous, "PCM", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
				return;
			}
			finally
			{
				_suppressCodecSideEffects = false;
			}
		}
		try
		{
			LocalizationManager instance = LocalizationManager.Instance;
			if (!(await AppDialogService.Instance.ConfirmAsync(instance.GetString("Codec_ChangeConfirmTitle", "Change Audio Codec"), instance.GetString("Codec_ChangeConfirmMessage", "Changing the audio codec will briefly stop playback and reconnect all active devices. Continue?"), instance.GetString("Dialog_OK", "OK"), instance.GetString("Dialog_Cancel", "Cancel")).ConfigureAwait(continueOnCapturedContext: true)))
			{
				_suppressCodecSideEffects = true;
				try
				{
					AudioCodecIndex = (string.Equals(previous, "PCM", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
					return;
				}
				finally
				{
					_suppressCodecSideEffects = false;
				}
			}
			ApplyCodecPreference(requested);
		}
		finally
		{
			Interlocked.Exchange(ref _isConfirmingCodecChange, 0);
		}
	}

	private static void ApplyCodecPreference(string requested)
	{
		SettingsManager.SetAudioCodec(requested);
		try
		{
			AirPlayStreamer.Instance.SetCodec(requested);
		}
		catch (Exception)
		{
		}
	}

	private static bool HasActiveStreamingDevices()
	{
		try
		{
			return CoreApp.GetRequiredService<HomeViewModel>().Devices.Any((DeviceViewModel d) => d.IsConnected || d.IsConnecting);
		}
		catch
		{
			return false;
		}
	}

	public void SyncCodecIndexFromConfig()
	{
		int num = (string.Equals(SettingsManager.GetAudioCodec(), "PCM", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
		if (AudioCodecIndex == num)
		{
			return;
		}
		_suppressCodecSideEffects = true;
		try
		{
			AudioCodecIndex = num;
		}
		finally
		{
			_suppressCodecSideEffects = false;
		}
	}

	[RelayCommand]
	private async Task RunLatencyCalibration()
	{
		try
		{
			OnboardingWindow requiredService = CoreApp.GetRequiredService<OnboardingWindow>();
			((Window)(object)requiredService).Topmost = false;
			((Window)(object)requiredService).Show();
			((Window)(object)requiredService).Activate();
		}
		catch (Exception ex)
		{
			LocalizationManager instance = LocalizationManager.Instance;
			await AppDialogService.Instance.ShowErrorAsync(instance.GetString("Dialog_Error", "Error"), instance.Format("Calibration_OpenFailed", "The calibration window could not be opened: {0}", ex.Message)).ConfigureAwait(continueOnCapturedContext: true);
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnCaptureSourceIndexChanged(int value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.CaptureSourceIndex = value;
		});
		_applyCaptureSourceAfterProcessRefresh = _transientCaptureCatalogsActive && value == 2 && IsProcessCaptureSupported;
		RefreshActiveCaptureCatalog();
		ApplyCaptureSource();
		if (value != 2 || SelectedProcess != null)
		{
			_applyCaptureSourceAfterProcessRefresh = false;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedCaptureDeviceChanged(AudioDeviceItem? value)
	{
		if (value != null && !_suppressCaptureSelectionSideEffects)
		{
			SettingsManager.UpdateConfig(delegate(AppConfig config)
			{
				config.SelectedCaptureDeviceId = value.DeviceId;
			});
			if (CaptureSourceIndex == 1)
			{
				ApplyCaptureSource();
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedProcessChanged(ProcessItem? value)
	{
		if (value != null && !_suppressCaptureSelectionSideEffects)
		{
			_applyCaptureSourceAfterProcessRefresh = false;
			SettingsManager.UpdateConfig(delegate(AppConfig config)
			{
				config.SelectedProcessId = value.CaptureProcessId;
				config.SelectedProcessName = value.ProcessName;
				config.SelectedProcessExecutablePath = value.ExecutablePath;
			});
			if (CaptureSourceIndex == 2)
			{
				ApplyCaptureSource();
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnStartOnBootChanged(bool value)
	{
		if (!_suppressStartupSideEffects)
		{
			ApplyStartOnBootAsync(value);
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnDefaultWindowModeIndexChanged(int value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.DefaultWindowModeIndex = Math.Clamp(value, 0, 2);
		});
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnDockPlacementIndexChanged(int value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.DockPlacementIndex = Math.Clamp(value, 0, 1);
		});
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSilentStartEnabledChanged(bool value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.SilentStartEnabled = value;
		});
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnAutoConnectOnLaunchChanged(bool value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.AutoConnectOnLaunch = value;
		});
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnLanguageIndexChanged(int value)
	{
		List<string> supportedLanguages = LocalizationManager.Instance.SupportedLanguages;
		if (value >= 0 && value < supportedLanguages.Count)
		{
			LocalizationManager.Instance.ChangeLanguage(supportedLanguages[value]);
			RebuildEqPresetList();
			this.OnPropertyChanged("EqPresetStatusText");
			this.OnPropertyChanged("SilenceTimeoutText");
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnAppVersionTextChanged(string value)
	{
		this.OnPropertyChanged("AppVersionDisplay");
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnDeveloperModeEnabledChanged(bool value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.DeveloperModeEnabled = value;
		});
		IMessengerExtensions.Send<DeveloperModeChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, new DeveloperModeChangedMessage(value));
		if (value)
		{
			(bool, bool, bool, int?, int?, int?) tuple = SettingsManager.ReadConfig((AppConfig config) => (DevSoloLatencyOverrideEnabled: config.DevSoloLatencyOverrideEnabled, DevGroupLatencyOverrideEnabled: config.DevGroupLatencyOverrideEnabled, DevMixedLatencyOverrideEnabled: config.DevMixedLatencyOverrideEnabled, DevSoloLatencyMs: config.DevSoloLatencyMs, DevGroupLatencyMs: config.DevGroupLatencyMs, DevMixedLatencyMs: config.DevMixedLatencyMs));
			SendDevOverride("SOLO", tuple.Item1 ? tuple.Item4 : ((int?)null));
			SendDevOverride("GROUP", tuple.Item2 ? tuple.Item5 : ((int?)null));
			SendDevOverride("MIXED", tuple.Item3 ? tuple.Item6 : ((int?)null));
		}
		else
		{
			AirPlayStreamer.Instance.SetDevPtpLatency("SOLO", null);
			AirPlayStreamer.Instance.SetDevPtpLatency("GROUP", null);
			AirPlayStreamer.Instance.SetDevPtpLatency("MIXED", null);
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnDevSoloLatencyOverrideEnabledChanged(bool value)
	{
		UpdateDevOverrideEnabled("DevSoloLatencyOverrideEnabled", "SOLO", value, DevSoloLatencyText);
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnDevGroupLatencyOverrideEnabledChanged(bool value)
	{
		UpdateDevOverrideEnabled("DevGroupLatencyOverrideEnabled", "GROUP", value, DevGroupLatencyText);
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnDevMixedLatencyOverrideEnabledChanged(bool value)
	{
		UpdateDevOverrideEnabled("DevMixedLatencyOverrideEnabled", "MIXED", value, DevMixedLatencyText);
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnDevSoloLatencyTextChanged(string? value)
	{
		UpdateDevOverride("DevSoloLatencyMs", "SOLO", value, DevSoloLatencyOverrideEnabled);
		this.OnPropertyChanged("DevSoloLatencyValue");
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnDevGroupLatencyTextChanged(string? value)
	{
		UpdateDevOverride("DevGroupLatencyMs", "GROUP", value, DevGroupLatencyOverrideEnabled);
		this.OnPropertyChanged("DevGroupLatencyValue");
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnDevMixedLatencyTextChanged(string? value)
	{
		UpdateDevOverride("DevMixedLatencyMs", "MIXED", value, DevMixedLatencyOverrideEnabled);
		this.OnPropertyChanged("DevMixedLatencyValue");
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnEqEnabledChanged(bool value)
	{
		if (!_suppressEqSideEffects)
		{
			SettingsManager.UpdateConfig(delegate(AppConfig config)
			{
				config.EqEnabled = value;
			});
			TrySendEqCommand(delegate
			{
				AirPlayStreamer.Instance.SetEqEnabled(value);
			});
			PersistEqMemoryForConnectedDevices();
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnEqPreampDbChanged(double value)
	{
		if (_suppressEqSideEffects)
		{
			this.OnPropertyChanged("EqPreampText");
			return;
		}
		double clamped = EqLayout.NormalizePreampDb(value);
		if (Math.Abs(clamped - value) > 0.0001)
		{
			EqPreampDb = clamped;
			return;
		}
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.EqPreampDb = clamped;
		});
		TrySendEqCommand(delegate
		{
			AirPlayStreamer.Instance.SetEqPreamp(clamped);
		});
		this.OnPropertyChanged("EqPreampText");
		PersistEqMemoryForConnectedDevices();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedEqPresetChanged(EqPresetItem? value)
	{
		CanDeleteEqPreset = value?.IsUser ?? false;
		this.OnPropertyChanged("EqPresetStatusText");
		if (!_suppressEqSideEffects && value != null && !value.IsCustom)
		{
			ApplyEqGains(value.GainsDb, value.Id, pushToBackend: true);
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSilenceDetectionEnabledChanged(bool value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.SilenceDetectionEnabled = value;
		});
		AirPlayStreamer.Instance.SetStandbyConfig(value, SilenceTimeoutMinutes);
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSilenceTimeoutMinutesChanged(int value)
	{
		int clamped = Math.Clamp(value, 1, 1440);
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.SilenceTimeoutMinutes = clamped;
		});
		AirPlayStreamer.Instance.SetStandbyConfig(SilenceDetectionEnabled, clamped);
		this.OnPropertyChanged("SilenceTimeoutText");
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnStreamingModeIndexChanged(int value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.StreamingModeIndex = value;
			config.CustomBufferSize = Math.Clamp(CustomBufferSize, 0, 1000);
		});
		DebounceSendLatency(CurrentLatencyMs, 1200);
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnCustomBufferSizeChanged(int value)
	{
		int clamped = Math.Clamp(value, 0, 1000);
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.CustomBufferSize = clamped;
		});
		if (StreamingModeIndex == 3)
		{
			DebounceSendLatency(clamped, 750);
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnMuteLocalOutputWhileStreamingChanged(bool value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.MuteLocalOutputWhileStreaming = value;
		});
		_localOutputMuteService.SetEnabled(value);
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnReceiverMediaControlEnabledChanged(bool value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.ReceiverMediaControlEnabled = value;
		});
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSendNowPlayingMetadataChanged(bool value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.SendNowPlayingMetadata = value;
		});
		if (value)
		{
			WindowsMediaSessionService.Instance.ResetTrackCache();
			WindowsMediaSessionService.Instance.SchedulePoll();
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnAutomaticReceiverRecoveryEnabledChanged(bool value)
	{
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			config.AutomaticReceiverRecoveryEnabled = value;
		});
		AirPlayStreamer.Instance.SendAutomaticReceiverRecovery(value);
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnAudioCodecIndexChanged(int value)
	{
		if (_suppressCodecSideEffects)
		{
			return;
		}
		string text = ((value == 1) ? "PCM" : "ALAC");
		string audioCodec = SettingsManager.GetAudioCodec();
		if (string.Equals(text, audioCodec, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		Application current = Application.Current;
		Dispatcher val = ((current != null) ? ((DispatcherObject)current).Dispatcher : null);
		if (val != null && !val.CheckAccess())
		{
			val.BeginInvoke((Delegate)(Action)delegate
			{
				OnAudioCodecIndexChanged(value);
			}, Array.Empty<object>());
		}
		else if (HasActiveStreamingDevices())
		{
			_ = LocalizationManager.Instance;
			ConfirmCodecChangeAsync(value, audioCodec, text);
		}
		else
		{
			ApplyCodecPreference(text);
		}
	}
}
