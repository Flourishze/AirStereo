using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AirStereo2.Messages;
using AirStereo2.Models;
using AirStereo2.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel.__Internals;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AirStereo2.ViewModels;

public class DeviceViewModel : ObservableObject, IDisposable
{
	internal enum FirewallCardUpdate
	{
		Keep,
		DropHint,
		Applied,
		StillBlocked
	}

	private bool _disposed;

	private readonly ConcurrentStringSet _volumeTargetAddresses = new ConcurrentStringSet();

	private readonly ConcurrentStringSet _connectMemberAddresses = new ConcurrentStringSet();

	private readonly DeviceLatencyTracker _latency = new DeviceLatencyTracker();

	[ObservableProperty]
	[NotifyPropertyChangedFor("IconKind")]
	[NotifyPropertyChangedFor("IconData")]
	[NotifyPropertyChangedFor("DockAccessibleLabel")]
	private string _name = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor("HasKnownIpAddress")]
	[NotifyPropertyChangedFor("DisplayIpAddress")]
	private string _iPAddress = "";

	[ObservableProperty]
	private int _port = 5000;

	[ObservableProperty]
	[NotifyPropertyChangedFor("VolumeVisibility")]
	[NotifyPropertyChangedFor("IsPlaybackActive")]
	[NotifyPropertyChangedFor("ConnectingRingVisibility")]
	[NotifyPropertyChangedFor("DockStatus")]
	[NotifyPropertyChangedFor("DockStatusText")]
	[NotifyPropertyChangedFor("DockAccessibleLabel")]
	private bool _isConnected;

	[ObservableProperty]
	[NotifyPropertyChangedFor("IsPlaybackActive")]
	[NotifyPropertyChangedFor("DockStatus")]
	[NotifyPropertyChangedFor("DockStatusText")]
	[NotifyPropertyChangedFor("DockAccessibleLabel")]
	private bool _isStandby;

	[ObservableProperty]
	[NotifyPropertyChangedFor("FailureVisibility")]
	[NotifyPropertyChangedFor("FirewallFixVisibility")]
	[NotifyPropertyChangedFor("DockStatus")]
	[NotifyPropertyChangedFor("DockStatusText")]
	[NotifyPropertyChangedFor("DockAccessibleLabel")]
	private bool _isFailed;

	[ObservableProperty]
	[NotifyPropertyChangedFor("PreemptionNoticeVisibility")]
	private bool _isPreempted;

	[ObservableProperty]
	[NotifyPropertyChangedFor("FailureVisibility")]
	[NotifyPropertyChangedFor("FirewallFixVisibility")]
	private string _failureMessage = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor("FirewallFixVisibility")]
	private bool _isFixingFirewall;

	[ObservableProperty]
	private string _iconPath = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor("IsConnectEnabled")]
	[NotifyPropertyChangedFor("ConnectingRingVisibility")]
	[NotifyPropertyChangedFor("DockStatus")]
	[NotifyPropertyChangedFor("DockStatusText")]
	[NotifyPropertyChangedFor("DockAccessibleLabel")]
	private bool _isConnecting;

	[ObservableProperty]
	private double _volume = 50.0;

	[ObservableProperty]
	[NotifyPropertyChangedFor("IconKind")]
	[NotifyPropertyChangedFor("IconData")]
	private string _model = string.Empty;

	[ObservableProperty]
	private string _stableId = string.Empty;

	[ObservableProperty]
	private bool _isDefaultOutput;

	private bool _suppressDefaultOutputPersist;

	[ObservableProperty]
	private string _groupId = string.Empty;

	[ObservableProperty]
	private bool _isGroupLeader;

	[ObservableProperty]
	private int _channelRole;

	[ObservableProperty]
	private string _groupPublicName = string.Empty;

	[ObservableProperty]
	private bool _groupContainsDiscoverableLeader;

	[ObservableProperty]
	private string _parentGroupId = string.Empty;

	[ObservableProperty]
	private bool _parentGroupContainsDiscoverableLeader;

	[ObservableProperty]
	[NotifyPropertyChangedFor("Access")]
	[NotifyPropertyChangedFor("AccessBadgeVisibility")]
	[NotifyPropertyChangedFor("IsHomeMembersOnlyAccess")]
	[NotifyPropertyChangedFor("AccessBadgeTooltip")]
	[NotifyPropertyChangedFor("IsStereoPartnerUnreachable")]
	[NotifyPropertyChangedFor("StereoPartnerHintVisibility")]
	private ulong _statusFlags;

	[ObservableProperty]
	[NotifyPropertyChangedFor("Access")]
	[NotifyPropertyChangedFor("AccessBadgeVisibility")]
	[NotifyPropertyChangedFor("IsHomeMembersOnlyAccess")]
	[NotifyPropertyChangedFor("AccessBadgeTooltip")]
	private bool _advertisesPassword;

	[ObservableProperty]
	[NotifyPropertyChangedFor("Access")]
	[NotifyPropertyChangedFor("AccessBadgeVisibility")]
	[NotifyPropertyChangedFor("IsHomeMembersOnlyAccess")]
	[NotifyPropertyChangedFor("AccessBadgeTooltip")]
	private int? _accessControlLevel;

	[ObservableProperty]
	private bool _supportsAirPlay2;

	[ObservableProperty]
	private string _tightSyncId = string.Empty;

	[ObservableProperty]
	private DateTimeOffset? _groupingObservedAt;

	[ObservableProperty]
	private DateTimeOffset? _groupingExpiresAt;

	[ObservableProperty]
	private string _groupingEvidenceSource = string.Empty;

	[ObservableProperty]
	private string _groupingEvidenceIdentity = string.Empty;

	[ObservableProperty]
	[NotifyPropertyChangedFor("IconKind")]
	[NotifyPropertyChangedFor("IconData")]
	private bool _isStereoPair;

	[ObservableProperty]
	[NotifyPropertyChangedFor("IconKind")]
	[NotifyPropertyChangedFor("IconData")]
	private bool _isHomeTheaterGroup;

	[ObservableProperty]
	[NotifyPropertyChangedFor("IconKind")]
	[NotifyPropertyChangedFor("IconData")]
	private bool _groupContainsAppleTv;

	[ObservableProperty]
	[NotifyPropertyChangedFor("IconKind")]
	[NotifyPropertyChangedFor("IconData")]
	private bool _groupContainsHomePod;

	[ObservableProperty]
	[NotifyPropertyChangedFor("IconKind")]
	[NotifyPropertyChangedFor("IconData")]
	private bool _groupContainsHomePodMini;

	[ObservableProperty]
	private int _pairMemberCount = 1;

	[ObservableProperty]
	[NotifyPropertyChangedFor("LatencyText")]
	private int _latencyMs = -1;

	[ObservableProperty]
	[NotifyPropertyChangedFor("LatencyText")]
	private bool _hasLatencyMeasurement;

	[ObservableProperty]
	[NotifyPropertyChangedFor("DockStatus")]
	[NotifyPropertyChangedFor("DockStatusText")]
	[NotifyPropertyChangedFor("DockAccessibleLabel")]
	private bool _isPinRequested;

	[ObservableProperty]
	private string _pinCode = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor("CalibrationStatusVisibility")]
	private string _calibrationStatusText = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor("ActiveCodecText")]
	[NotifyPropertyChangedFor("ActiveCodecVisibility")]
	private string _activeCodec = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor("DevTelemetryText")]
	[NotifyPropertyChangedFor("DevTelemetryVisibility")]
	private string _devTelemetry = "";

	private long _devDriftPpb;

	private long _devDriftSamples;

	private long _devRtxRequests;

	private long _devRtxRequested;

	private long _devRtxResent;

	private DispatcherTimer? _connectTimeout;

	private bool _suppressVolumeSideEffects;

	private bool _preserveReceiverVolumeOnConnect;

	private string? _lastFailureRawReason;

	private string? _failureResourceKey;

	private string? _networkHintCode;

	private string? _failureText;

	private (string Key, string English)? _firewallNote;

	private int _failureGeneration;

	private static readonly TimeSpan NetworkCheckTimeout = TimeSpan.FromSeconds(10.0);

	private bool _showingCalibrationSlow;

	private bool _passwordPromptOpen;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? stopWaitingForReclaimCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? submitPinCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? cancelPinCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? toggleConnectionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? fixFirewallCommand;

	internal string AirPlayInstanceName { get; set; } = string.Empty;

	internal string RaopInstanceName { get; set; } = string.Empty;

	internal bool HasAirPlayDiscoveryRecord { get; set; }

	internal bool HasRaopDiscoveryRecord { get; set; }

	internal string HomeKitPublicKey { get; set; } = string.Empty;

	internal string GroupingEvidenceScope { get; set; } = string.Empty;

	internal string PreferredBackendInstanceName
	{
		get
		{
			if (!SupportsAirPlay2 || string.IsNullOrWhiteSpace(AirPlayInstanceName))
			{
				if (string.IsNullOrWhiteSpace(RaopInstanceName))
				{
					return AirPlayInstanceName;
				}
				return RaopInstanceName;
			}
			return AirPlayInstanceName;
		}
	}

	internal bool PreferredBackendHintIsRaop
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(RaopInstanceName))
			{
				if (SupportsAirPlay2)
				{
					return string.IsNullOrWhiteSpace(AirPlayInstanceName);
				}
				return true;
			}
			return false;
		}
	}

	internal BackendEndpointSelection? PreferredBackendSelection
	{
		get
		{
			if (string.IsNullOrWhiteSpace(IPAddress) || string.IsNullOrWhiteSpace(PreferredBackendInstanceName))
			{
				return null;
			}
			return new BackendEndpointSelection(IPAddress.Trim(), PreferredBackendInstanceName.Trim(), PreferredBackendHintIsRaop);
		}
	}

	internal string PreferredBackendEventId => BuildBackendEndpointEventId(IPAddress, PreferredBackendInstanceName, PreferredBackendHintIsRaop);

	public bool HasKnownIpAddress => DeviceIpDisplay.HasKnownIpAddress(IPAddress);

	public string DisplayIpAddress
	{
		get
		{
			return DeviceIpDisplay.FormatDisplay(IPAddress, LocalizationManager.Instance.GetString("Device_UnknownIp", "Unknown IP"));
		}
		set
		{
		}
	}

	public Visibility PreemptionNoticeVisibility
	{
		get
		{
			if (!IsPreempted)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public ReceiverAccess Access => ReceiverAccessPolicy.Classify(StatusFlags, AdvertisesPassword, AccessControlLevel);

	public Visibility AccessBadgeVisibility
	{
		get
		{
			if (Access != ReceiverAccess.Open)
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public bool IsHomeMembersOnlyAccess => Access == ReceiverAccess.HomeMembersOnly;

	public string AccessBadgeTooltip => Access switch
	{
		ReceiverAccess.PasswordRequired => LocalizationManager.Instance.GetString("Device_AccessPasswordBadge", "Protected by an AirPlay password (set in the Home app)."), 
		ReceiverAccess.HomeMembersOnly => LocalizationManager.Instance.GetString("Device_AccessHomeOnlyBadge", "Only people sharing this Home can play to this speaker. To allow this PC, change Speakers & TV access in the Home app."), 
		_ => string.Empty, 
	};

	public bool IsStereoPartnerUnreachable => ReceiverStatusFlags.IsStereoPartnerUnreachable(StatusFlags);

	public Visibility StereoPartnerHintVisibility
	{
		get
		{
			if (!IsStereoPartnerUnreachable)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public string LatencyText
	{
		get
		{
			LocalizationManager instance = LocalizationManager.Instance;
			if (!HasLatencyMeasurement)
			{
				return instance.GetString("Device_LatencyUnmeasured", "--ms");
			}
			return instance.Format("Device_LatencyMs", "{0}ms", LatencyMs);
		}
	}

	public string ActiveCodecText
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(ActiveCodec))
			{
				return ActiveCodec;
			}
			return "";
		}
	}

	public Visibility ActiveCodecVisibility
	{
		get
		{
			if (!IsConnected || string.IsNullOrWhiteSpace(ActiveCodec))
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public string DevTelemetryText => DevTelemetry;

	public Visibility DevTelemetryVisibility
	{
		get
		{
			if (!IsConnected || !SettingsManager.ReadConfig((AppConfig config) => config.DeveloperModeEnabled) || string.IsNullOrWhiteSpace(DevTelemetry))
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public Visibility CalibrationStatusVisibility
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(CalibrationStatusText))
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public DockStatusKind DockStatus => DeviceDockStatus.Resolve(IsFailed, IsPinRequested, IsConnecting, IsConnected, IsStandby);

	public string DockStatusText => LocalizationManager.Instance.GetString(DeviceDockStatus.ResourceKey(DockStatus), DockStatus.ToString());

	public string DockAccessibleLabel => LocalizationManager.Instance.Format("Dock_DeviceStatusLabel", "{0} — {1}", Name, DockStatusText);

	public DeviceIconKind IconKind => DetectIconKind();

	public Geometry IconData => DeviceIconCatalog.GetGeometry(IconKind);

	public bool IsHomePodFamily => LooksLikeHomePodFamily(Model, Name);

	public bool IsHomePodMini => LooksLikeHomePodMini(Model, Name);

	public string? StereoPairKey => BuildStereoPairKey(Model, Name, GroupId, ParentGroupId, TightSyncId);

	public string? AirPlayGroupKey => BuildAirPlayGroupKey(GroupId, ParentGroupId, TightSyncId);

	public bool IsReceiverSessionActive => ReceiverStatusFlags.IsReceiverSessionActive(StatusFlags);

	public bool HasPersistentGroupMembership => !string.IsNullOrWhiteSpace(ParentGroupId);

	public bool HasAuthoritativeStereoPairMetadata => HasHomePodStereoMetadata(Model, Name, GroupId, GroupPublicName, TightSyncId);

	public bool CanSeedAppleGroup
	{
		get
		{
			if (SupportsAirPlay2 && (!IsReceiverSessionActive || HasPersistentGroupMembership || HasAuthoritativeStereoPairMetadata) && AirPlayGroupKey != null)
			{
				if (!LooksLikeAppleTv(Model, Name))
				{
					return LooksLikeHomePodFamily(Model, Name);
				}
				return true;
			}
			return false;
		}
	}

	public bool IsPlaybackActive
	{
		get
		{
			if (IsConnected)
			{
				return !IsStandby;
			}
			return false;
		}
	}

	public bool IsConnectEnabled => !IsConnecting;

	public Visibility VolumeVisibility
	{
		get
		{
			if (!IsConnected)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public Visibility ConnectingRingVisibility
	{
		get
		{
			if (!IsConnecting || IsConnected)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public Visibility FailureVisibility
	{
		get
		{
			if (!IsFailed || string.IsNullOrWhiteSpace(FailureMessage))
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public Visibility FirewallFixVisibility
	{
		get
		{
			if (!ShowsFirewallFix(IsFailed, IsFixingFirewall, _networkHintCode))
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string Name
	{
		get
		{
			return _name;
		}
		[MemberNotNull("_name")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_name, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.Name);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconKind);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconData);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockAccessibleLabel);
				_name = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.Name);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconKind);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconData);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockAccessibleLabel);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string IPAddress
	{
		get
		{
			return _iPAddress;
		}
		[MemberNotNull("_iPAddress")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_iPAddress, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IPAddress);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasKnownIpAddress);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DisplayIpAddress);
				_iPAddress = value;
				OnIPAddressChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IPAddress);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.HasKnownIpAddress);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DisplayIpAddress);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int Port
	{
		get
		{
			return _port;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_port, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.Port);
				_port = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.Port);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsConnected
	{
		get
		{
			return _isConnected;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isConnected, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsConnected);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.VolumeVisibility);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsPlaybackActive);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConnectingRingVisibility);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatus);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatusText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockAccessibleLabel);
				_isConnected = value;
				OnIsConnectedChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsConnected);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.VolumeVisibility);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsPlaybackActive);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ConnectingRingVisibility);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatus);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatusText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockAccessibleLabel);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsStandby
	{
		get
		{
			return _isStandby;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isStandby, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsStandby);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsPlaybackActive);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatus);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatusText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockAccessibleLabel);
				_isStandby = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsStandby);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsPlaybackActive);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatus);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatusText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockAccessibleLabel);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsFailed
	{
		get
		{
			return _isFailed;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isFailed, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsFailed);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.FailureVisibility);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.FirewallFixVisibility);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatus);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatusText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockAccessibleLabel);
				_isFailed = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsFailed);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.FailureVisibility);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.FirewallFixVisibility);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatus);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatusText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockAccessibleLabel);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsPreempted
	{
		get
		{
			return _isPreempted;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isPreempted, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsPreempted);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.PreemptionNoticeVisibility);
				_isPreempted = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsPreempted);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.PreemptionNoticeVisibility);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string FailureMessage
	{
		get
		{
			return _failureMessage;
		}
		[MemberNotNull("_failureMessage")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_failureMessage, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.FailureMessage);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.FailureVisibility);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.FirewallFixVisibility);
				_failureMessage = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.FailureMessage);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.FailureVisibility);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.FirewallFixVisibility);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsFixingFirewall
	{
		get
		{
			return _isFixingFirewall;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isFixingFirewall, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsFixingFirewall);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.FirewallFixVisibility);
				_isFixingFirewall = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsFixingFirewall);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.FirewallFixVisibility);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string IconPath
	{
		get
		{
			return _iconPath;
		}
		[MemberNotNull("_iconPath")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_iconPath, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconPath);
				_iconPath = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconPath);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsConnecting
	{
		get
		{
			return _isConnecting;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isConnecting, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsConnecting);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsConnectEnabled);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConnectingRingVisibility);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatus);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatusText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockAccessibleLabel);
				_isConnecting = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsConnecting);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsConnectEnabled);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ConnectingRingVisibility);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatus);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatusText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockAccessibleLabel);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public double Volume
	{
		get
		{
			return _volume;
		}
		set
		{
			if (!EqualityComparer<double>.Default.Equals(_volume, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.Volume);
				_volume = value;
				OnVolumeChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.Volume);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string Model
	{
		get
		{
			return _model;
		}
		[MemberNotNull("_model")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_model, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.Model);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconKind);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconData);
				_model = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.Model);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconKind);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconData);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string StableId
	{
		get
		{
			return _stableId;
		}
		[MemberNotNull("_stableId")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_stableId, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.StableId);
				_stableId = value;
				OnStableIdChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.StableId);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsDefaultOutput
	{
		get
		{
			return _isDefaultOutput;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isDefaultOutput, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsDefaultOutput);
				_isDefaultOutput = value;
				OnIsDefaultOutputChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsDefaultOutput);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string GroupId
	{
		get
		{
			return _groupId;
		}
		[MemberNotNull("_groupId")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_groupId, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.GroupId);
				_groupId = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.GroupId);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsGroupLeader
	{
		get
		{
			return _isGroupLeader;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isGroupLeader, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsGroupLeader);
				_isGroupLeader = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsGroupLeader);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int ChannelRole
	{
		get
		{
			return _channelRole;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_channelRole, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ChannelRole);
				_channelRole = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ChannelRole);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string GroupPublicName
	{
		get
		{
			return _groupPublicName;
		}
		[MemberNotNull("_groupPublicName")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_groupPublicName, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.GroupPublicName);
				_groupPublicName = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.GroupPublicName);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool GroupContainsDiscoverableLeader
	{
		get
		{
			return _groupContainsDiscoverableLeader;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_groupContainsDiscoverableLeader, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.GroupContainsDiscoverableLeader);
				_groupContainsDiscoverableLeader = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.GroupContainsDiscoverableLeader);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ParentGroupId
	{
		get
		{
			return _parentGroupId;
		}
		[MemberNotNull("_parentGroupId")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_parentGroupId, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ParentGroupId);
				_parentGroupId = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ParentGroupId);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool ParentGroupContainsDiscoverableLeader
	{
		get
		{
			return _parentGroupContainsDiscoverableLeader;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_parentGroupContainsDiscoverableLeader, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ParentGroupContainsDiscoverableLeader);
				_parentGroupContainsDiscoverableLeader = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ParentGroupContainsDiscoverableLeader);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public ulong StatusFlags
	{
		get
		{
			return _statusFlags;
		}
		set
		{
			if (!EqualityComparer<ulong>.Default.Equals(_statusFlags, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.StatusFlags);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.Access);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AccessBadgeVisibility);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsHomeMembersOnlyAccess);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AccessBadgeTooltip);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsStereoPartnerUnreachable);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.StereoPartnerHintVisibility);
				_statusFlags = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.StatusFlags);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.Access);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AccessBadgeVisibility);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsHomeMembersOnlyAccess);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AccessBadgeTooltip);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsStereoPartnerUnreachable);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.StereoPartnerHintVisibility);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool AdvertisesPassword
	{
		get
		{
			return _advertisesPassword;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_advertisesPassword, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AdvertisesPassword);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.Access);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AccessBadgeVisibility);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsHomeMembersOnlyAccess);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AccessBadgeTooltip);
				_advertisesPassword = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AdvertisesPassword);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.Access);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AccessBadgeVisibility);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsHomeMembersOnlyAccess);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AccessBadgeTooltip);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int? AccessControlLevel
	{
		get
		{
			return _accessControlLevel;
		}
		set
		{
			if (!EqualityComparer<int?>.Default.Equals(_accessControlLevel, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AccessControlLevel);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.Access);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AccessBadgeVisibility);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsHomeMembersOnlyAccess);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.AccessBadgeTooltip);
				_accessControlLevel = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AccessControlLevel);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.Access);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AccessBadgeVisibility);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsHomeMembersOnlyAccess);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.AccessBadgeTooltip);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool SupportsAirPlay2
	{
		get
		{
			return _supportsAirPlay2;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_supportsAirPlay2, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SupportsAirPlay2);
				_supportsAirPlay2 = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SupportsAirPlay2);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string TightSyncId
	{
		get
		{
			return _tightSyncId;
		}
		[MemberNotNull("_tightSyncId")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_tightSyncId, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.TightSyncId);
				_tightSyncId = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.TightSyncId);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public DateTimeOffset? GroupingObservedAt
	{
		get
		{
			return _groupingObservedAt;
		}
		set
		{
			if (!EqualityComparer<DateTimeOffset?>.Default.Equals(_groupingObservedAt, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.GroupingObservedAt);
				_groupingObservedAt = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.GroupingObservedAt);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public DateTimeOffset? GroupingExpiresAt
	{
		get
		{
			return _groupingExpiresAt;
		}
		set
		{
			if (!EqualityComparer<DateTimeOffset?>.Default.Equals(_groupingExpiresAt, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.GroupingExpiresAt);
				_groupingExpiresAt = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.GroupingExpiresAt);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string GroupingEvidenceSource
	{
		get
		{
			return _groupingEvidenceSource;
		}
		[MemberNotNull("_groupingEvidenceSource")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_groupingEvidenceSource, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.GroupingEvidenceSource);
				_groupingEvidenceSource = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.GroupingEvidenceSource);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string GroupingEvidenceIdentity
	{
		get
		{
			return _groupingEvidenceIdentity;
		}
		[MemberNotNull("_groupingEvidenceIdentity")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_groupingEvidenceIdentity, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.GroupingEvidenceIdentity);
				_groupingEvidenceIdentity = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.GroupingEvidenceIdentity);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsStereoPair
	{
		get
		{
			return _isStereoPair;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isStereoPair, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsStereoPair);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconKind);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconData);
				_isStereoPair = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsStereoPair);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconKind);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconData);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsHomeTheaterGroup
	{
		get
		{
			return _isHomeTheaterGroup;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isHomeTheaterGroup, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsHomeTheaterGroup);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconKind);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconData);
				_isHomeTheaterGroup = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsHomeTheaterGroup);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconKind);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconData);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool GroupContainsAppleTv
	{
		get
		{
			return _groupContainsAppleTv;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_groupContainsAppleTv, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.GroupContainsAppleTv);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconKind);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconData);
				_groupContainsAppleTv = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.GroupContainsAppleTv);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconKind);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconData);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool GroupContainsHomePod
	{
		get
		{
			return _groupContainsHomePod;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_groupContainsHomePod, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.GroupContainsHomePod);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconKind);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconData);
				_groupContainsHomePod = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.GroupContainsHomePod);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconKind);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconData);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool GroupContainsHomePodMini
	{
		get
		{
			return _groupContainsHomePodMini;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_groupContainsHomePodMini, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.GroupContainsHomePodMini);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconKind);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IconData);
				_groupContainsHomePodMini = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.GroupContainsHomePodMini);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconKind);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IconData);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int PairMemberCount
	{
		get
		{
			return _pairMemberCount;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_pairMemberCount, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.PairMemberCount);
				_pairMemberCount = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.PairMemberCount);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int LatencyMs
	{
		get
		{
			return _latencyMs;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_latencyMs, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.LatencyMs);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.LatencyText);
				_latencyMs = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.LatencyMs);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.LatencyText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasLatencyMeasurement
	{
		get
		{
			return _hasLatencyMeasurement;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_hasLatencyMeasurement, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasLatencyMeasurement);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.LatencyText);
				_hasLatencyMeasurement = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.HasLatencyMeasurement);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.LatencyText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsPinRequested
	{
		get
		{
			return _isPinRequested;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isPinRequested, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsPinRequested);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatus);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatusText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockAccessibleLabel);
				_isPinRequested = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsPinRequested);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatus);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatusText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockAccessibleLabel);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string PinCode
	{
		get
		{
			return _pinCode;
		}
		[MemberNotNull("_pinCode")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_pinCode, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.PinCode);
				_pinCode = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.PinCode);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string CalibrationStatusText
	{
		get
		{
			return _calibrationStatusText;
		}
		[MemberNotNull("_calibrationStatusText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_calibrationStatusText, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CalibrationStatusText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CalibrationStatusVisibility);
				_calibrationStatusText = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CalibrationStatusText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CalibrationStatusVisibility);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ActiveCodec
	{
		get
		{
			return _activeCodec;
		}
		[MemberNotNull("_activeCodec")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_activeCodec, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ActiveCodec);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ActiveCodecText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ActiveCodecVisibility);
				_activeCodec = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ActiveCodec);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ActiveCodecText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ActiveCodecVisibility);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DevTelemetry
	{
		get
		{
			return _devTelemetry;
		}
		[MemberNotNull("_devTelemetry")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_devTelemetry, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DevTelemetry);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DevTelemetryText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DevTelemetryVisibility);
				_devTelemetry = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DevTelemetry);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DevTelemetryText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DevTelemetryVisibility);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand StopWaitingForReclaimCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = stopWaitingForReclaimCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)StopWaitingForReclaim);
				RelayCommand val2 = val;
				stopWaitingForReclaimCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SubmitPinCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = submitPinCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)SubmitPin);
				RelayCommand val2 = val;
				submitPinCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand CancelPinCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = cancelPinCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)CancelPin);
				RelayCommand val2 = val;
				cancelPinCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ToggleConnectionCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = toggleConnectionCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ToggleConnectionAsync);
				AsyncRelayCommand val2 = val;
				toggleConnectionCommand = val;
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

	internal static string BuildBackendEndpointEventId(string? ipAddress, string? instanceName, bool isRaop)
	{
		if (string.IsNullOrWhiteSpace(ipAddress) || string.IsNullOrWhiteSpace(instanceName))
		{
			return string.Empty;
		}
		IPAddress address;
		string s = (System.Net.IPAddress.TryParse(ipAddress.Trim(), out address) ? address.ToString() : ipAddress.Trim().ToLowerInvariant());
		byte[] bytes = Encoding.UTF8.GetBytes(instanceName.Trim().ToLowerInvariant());
		byte[] bytes2 = Encoding.UTF8.GetBytes(s);
		return $"{(isRaop ? 82 : 65)}-{Convert.ToHexString(bytes)}-{Convert.ToHexString(bytes2)}";
	}

	[RelayCommand]
	internal void StopWaitingForReclaim()
	{
		if (IsPreempted)
		{
			IsPreempted = false;
			RemoveThisDeviceFromBackend();
			AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
		}
	}

	public bool HasFreshGroupingEvidence(DateTimeOffset now)
	{
		DateTimeOffset? groupingObservedAt = GroupingObservedAt;
		if (groupingObservedAt.HasValue)
		{
			DateTimeOffset valueOrDefault = groupingObservedAt.GetValueOrDefault();
			groupingObservedAt = GroupingExpiresAt;
			if (groupingObservedAt.HasValue)
			{
				DateTimeOffset valueOrDefault2 = groupingObservedAt.GetValueOrDefault();
				if (valueOrDefault <= now && now < valueOrDefault2 && !string.IsNullOrWhiteSpace(GroupingEvidenceSource) && !string.IsNullOrWhiteSpace(GroupingEvidenceIdentity))
				{
					return string.Equals(GroupingEvidenceIdentity, HomePodStereoPolicy.PhysicalIdentity(this), StringComparison.OrdinalIgnoreCase);
				}
			}
		}
		return false;
	}

	internal void SetGroupingEvidence(DateTimeOffset observedAt, DateTimeOffset expiresAt, string source)
	{
		GroupingObservedAt = observedAt;
		GroupingExpiresAt = expiresAt;
		GroupingEvidenceSource = source;
		GroupingEvidenceIdentity = HomePodStereoPolicy.PhysicalIdentity(this);
	}

	internal void ClearGroupingEvidence()
	{
		GroupingObservedAt = null;
		GroupingExpiresAt = null;
		GroupingEvidenceSource = string.Empty;
		GroupingEvidenceIdentity = string.Empty;
		GroupingEvidenceScope = string.Empty;
	}

	public static bool LooksLikeHomePodFamily(string? model, string? name)
	{
		string text = Normalize(model);
		string text2 = Normalize(name);
		if (!text.Contains("audioaccessory") && !text.Contains("homepod"))
		{
			return text2.Contains("homepod");
		}
		return true;
	}

	public static bool LooksLikeHomePodMini(string? model, string? name)
	{
		string text = Normalize(model);
		string text2 = Normalize(name);
		if (!text.StartsWith("audioaccessory5") && !text.Contains("homepodmini") && !text.Contains("homepod mini") && !text2.Contains("homepodmini"))
		{
			return text2.Contains("homepod mini");
		}
		return true;
	}

	public static bool LooksLikeAppleTv(string? model, string? name)
	{
		string text = Normalize(model);
		string text2 = Normalize(name);
		if (!text.Contains("appletv") && !text.Contains("apple tv") && !text2.Contains("appletv"))
		{
			return text2.Contains("apple tv");
		}
		return true;
	}

	public static bool LooksLikeAirPortExpress(string? model, string? name)
	{
		string text = Normalize(model);
		string text2 = Normalize(name);
		if (!text.StartsWith("airport") && !text2.Contains("airport"))
		{
			return text2.Contains("air port");
		}
		return true;
	}

	public static bool LooksLikeBeats(string? model, string? name)
	{
		string text = Normalize(model);
		string text2 = Normalize(name);
		if (!text.Contains("beats") && !text.Contains("solobuds") && !text.Contains("powerbeats") && !text.Contains("studiobuds"))
		{
			return text2.Contains("beats");
		}
		return true;
	}

	public static bool LooksLikeMacBook(string? model, string? name)
	{
		string text = Normalize(model);
		string text2 = Normalize(name);
		if (!text.Contains("macbook"))
		{
			return text2.Contains("macbook");
		}
		return true;
	}

	public static bool LooksLikeMacMini(string? model, string? name)
	{
		string text = Normalize(model);
		string text2 = Normalize(name);
		if (!text.Contains("macmini") && !text.Contains("mac mini") && !text2.Contains("macmini"))
		{
			return text2.Contains("mac mini");
		}
		return true;
	}

	public static bool LooksLikeMacStudio(string? model, string? name)
	{
		string text = Normalize(model);
		string text2 = Normalize(name);
		if (!text.Contains("macstudio") && !text.Contains("mac studio") && !text.Contains("macpro") && !text.Contains("mac pro") && !text2.Contains("macstudio") && !text2.Contains("mac studio") && !text2.Contains("macpro"))
		{
			return text2.Contains("mac pro");
		}
		return true;
	}

	public static bool HasHomePodStereoMetadata(string? model, string? name, string? groupId, string? groupPublicName, string? tightSyncId)
	{
		if (LooksLikeHomePodFamily(model, name))
		{
			return !string.IsNullOrWhiteSpace(tightSyncId);
		}
		return false;
	}

	public static string? BuildAirPlayGroupKey(string? groupId, string? parentGroupId, string? tightSyncId)
	{
		if (!string.IsNullOrWhiteSpace(parentGroupId))
		{
			return parentGroupId.Trim();
		}
		if (!string.IsNullOrWhiteSpace(tightSyncId))
		{
			return tightSyncId.Trim();
		}
		if (!string.IsNullOrWhiteSpace(groupId))
		{
			return groupId.Trim();
		}
		return null;
	}

	public static string? BuildStereoPairKey(string? model, string? name, string? groupId, string? parentGroupId, string? tightSyncId)
	{
		if (!LooksLikeHomePodFamily(model, name))
		{
			return null;
		}
		if (string.IsNullOrWhiteSpace(tightSyncId))
		{
			return BuildAirPlayGroupKey(groupId, parentGroupId, tightSyncId);
		}
		return tightSyncId.Trim();
	}

	private DeviceIconKind DetectIconKind()
	{
		if (IsHomeTheaterGroup)
		{
			if (GroupContainsHomePodMini || (IsHomePodMini && !GroupContainsHomePod))
			{
				return DeviceIconKind.HomePodMiniAndAppleTv;
			}
			return DeviceIconKind.HomePodAndAppleTv;
		}
		if (IsStereoPair)
		{
			if (IsHomePodMini)
			{
				return DeviceIconKind.HomePodMiniPair;
			}
			if (IsHomePodFamily)
			{
				return DeviceIconKind.HomePodPair;
			}
			return DeviceIconKind.HifiSpeakerPair;
		}
		if (IsHomePodMini)
		{
			return DeviceIconKind.HomePodMini;
		}
		if (IsHomePodFamily)
		{
			return DeviceIconKind.HomePod;
		}
		if (LooksLikeAppleTv(Model, Name))
		{
			return DeviceIconKind.AppleTv;
		}
		if (LooksLikeAirPortExpress(Model, Name))
		{
			return DeviceIconKind.AirPortExpress;
		}
		if (LooksLikeBeats(Model, Name))
		{
			return DeviceIconKind.Beats;
		}
		if (LooksLikeMacBook(Model, Name))
		{
			return DeviceIconKind.MacBook;
		}
		if (LooksLikeMacMini(Model, Name))
		{
			return DeviceIconKind.MacMini;
		}
		if (LooksLikeMacStudio(Model, Name))
		{
			return DeviceIconKind.MacStudio;
		}
		return DeviceIconKind.HifiSpeaker;
	}

	public void RegisterGroupMember(string ipAddress, string model, string name)
	{
		if (!string.IsNullOrWhiteSpace(ipAddress))
		{
			_volumeTargetAddresses.Add(ipAddress.Trim());
		}
		GroupContainsAppleTv = GroupContainsAppleTv || LooksLikeAppleTv(model, name);
		GroupContainsHomePod = GroupContainsHomePod || LooksLikeHomePodFamily(model, name);
		GroupContainsHomePodMini = GroupContainsHomePodMini || LooksLikeHomePodMini(model, name);
		PairMemberCount = Math.Max(PairMemberCount, _volumeTargetAddresses.Count);
	}

	internal void SetConnectMembers(IEnumerable<string> addresses)
	{
		_connectMemberAddresses.Reset(addresses);
	}

	internal IReadOnlyList<string> ConnectTargetAddresses()
	{
		return BuildConnectTargets(IPAddress, _connectMemberAddresses.Snapshot());
	}

	internal static IReadOnlyList<string> BuildConnectTargets(string? primaryIp, IEnumerable<string> memberAddresses)
	{
		List<string> list = new List<string>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (!string.IsNullOrWhiteSpace(primaryIp) && hashSet.Add(primaryIp.Trim()))
		{
			list.Add(primaryIp.Trim());
		}
		foreach (string memberAddress in memberAddresses)
		{
			if (!string.IsNullOrWhiteSpace(memberAddress))
			{
				string item = memberAddress.Trim();
				if (hashSet.Add(item))
				{
					list.Add(item);
				}
			}
		}
		return list;
	}

	private void AddThisDeviceToBackend()
	{
		if (!SupportsAirPlay2)
		{
			IReadOnlyList<BackendEndpointSelection> readOnlyList = CoreApp.GetRequiredService<HomeViewModel>().ClassicGroupSelectionsIncluding(this);
			if (readOnlyList.Count >= 2)
			{
				AirPlayStreamer.Instance.AddGroup(readOnlyList);
				return;
			}
			BackendEndpointSelection preferredBackendSelection = PreferredBackendSelection;
			if ((object)preferredBackendSelection != null)
			{
				AirPlayStreamer.Instance.AddDevice(preferredBackendSelection.Ip, preferredBackendSelection.InstanceName, preferredBackendSelection.IsRaop);
			}
			else
			{
				AirPlayStreamer.Instance.AddDevice(IPAddress);
			}
			return;
		}
		IReadOnlyList<string> readOnlyList2 = ConnectTargetAddresses();
		if (readOnlyList2.Count >= 2)
		{
			AirPlayStreamer.Instance.AddGroup(readOnlyList2);
			return;
		}
		foreach (string item in readOnlyList2)
		{
			AirPlayStreamer.Instance.AddDevice(item, PreferredBackendInstanceName, PreferredBackendHintIsRaop);
		}
	}

	internal void RemoveThisDeviceFromBackend()
	{
		if (!SupportsAirPlay2)
		{
			BackendEndpointSelection preferredBackendSelection = PreferredBackendSelection;
			if ((object)preferredBackendSelection != null)
			{
				AirPlayStreamer.Instance.RemoveDevice(preferredBackendSelection);
				return;
			}
		}
		foreach (string item in ConnectTargetAddresses())
		{
			AirPlayStreamer.Instance.RemoveDevice(item);
		}
	}

	private static string Normalize(string? value)
	{
		return value?.Trim().ToLowerInvariant() ?? string.Empty;
	}

	internal static bool ShowsFirewallFix(bool isFailed, bool isFixing, string? networkHintCode)
	{
		if (isFailed && !isFixing)
		{
			return FirewallFix.IsFixable(networkHintCode);
		}
		return false;
	}

	private void RegisterSavedAccessPassword()
	{
		if (!string.IsNullOrWhiteSpace(IPAddress) && !string.IsNullOrWhiteSpace(StableId) && AccessPasswordStore.TryGet(StableId, out string password))
		{
			AirPlayStreamer.Instance.SetAccessPassword(IPAddress, password);
		}
	}

	public void RefreshDefaultOutputFlag()
	{
		bool flag = SettingsManager.IsDefaultOutput(StableId);
		if (IsDefaultOutput == flag)
		{
			return;
		}
		_suppressDefaultOutputPersist = true;
		try
		{
			IsDefaultOutput = flag;
		}
		finally
		{
			_suppressDefaultOutputPersist = false;
		}
	}

	public DeviceViewModel()
	{
		IMessengerExtensions.Register<DisconnectAllMessage>((IMessenger)(object)WeakReferenceMessenger.Default, (object)this, (MessageHandler<object, DisconnectAllMessage>)delegate(object r, DisconnectAllMessage m)
		{
			if (IsConnected || IsConnecting)
			{
				BeginInvokeOnUi(delegate
				{
					if (m.BackendAlreadyCleared)
					{
						StopConnectTimeout();
						IsConnected = false;
						IsConnecting = false;
						IsStandby = false;
						IsPinRequested = false;
						CalibrationStatusText = "";
						ClearFailure();
						AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
					}
					else
					{
						RemoveThisDeviceFromBackend();
						StopConnectTimeout();
						IsConnected = false;
						IsConnecting = false;
						IsStandby = false;
						IsPinRequested = false;
						CalibrationStatusText = "";
						ClearFailure();
						AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
					}
				});
			}
		});
		IMessengerExtensions.Register<VolumeChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, (object)this, (MessageHandler<object, VolumeChangedMessage>)delegate(object r, VolumeChangedMessage m)
		{
			if (VolumeCoordination.ShouldApplyMasterToDevice(m.Origin))
			{
				int volume = VolumeCoordination.ClampVolume(m.Volume);
				ApplyVolumeFromMaster(volume);
			}
		});
		IMessengerExtensions.Register<SilenceDetectedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, (object)this, (MessageHandler<object, SilenceDetectedMessage>)delegate(object r, SilenceDetectedMessage m)
		{
			BeginInvokeOnUi(delegate
			{
				IsStandby = m.IsSilent;
			});
		});
		IMessengerExtensions.Register<LatencyChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, (object)this, (MessageHandler<object, LatencyChangedMessage>)delegate
		{
		});
		IMessengerExtensions.Register<DeveloperModeChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, (object)this, (MessageHandler<object, DeveloperModeChangedMessage>)delegate(object r, DeveloperModeChangedMessage m)
		{
			if (!m.Enabled)
			{
				DevTelemetry = "";
			}
			this.OnPropertyChanged("DevTelemetryText");
			this.OnPropertyChanged("DevTelemetryVisibility");
		});
		LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
	}

	internal bool PrepareForGroupConnect(double groupVolume, bool preserveReceiverVolume = false)
	{
		if (IsConnected || IsConnecting)
		{
			return false;
		}
		IsConnecting = true;
		IsPreempted = false;
		_preserveReceiverVolumeOnConnect = preserveReceiverVolume;
		CalibrationStatusText = "";
		ClearFailure();
		if (!preserveReceiverVolume)
		{
			Volume = VolumeCoordination.ClampVolume((int)groupVolume);
		}
		AirPlayStreamer.Instance.EnsureStarted();
		AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
		AirPlayStreamer.Instance.EventReceived += OnBackendEvent;
		if (!preserveReceiverVolume)
		{
			SetVolumeForTargets((int)Volume);
		}
		StartConnectTimeout();
		return true;
	}

	internal void MarkDisconnectedByGroup()
	{
		StopConnectTimeout();
		IsPreempted = false;
		IsConnected = false;
		IsConnecting = false;
		IsStandby = false;
		_preserveReceiverVolumeOnConnect = false;
		ClearFailure();
		AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
	}

	internal void Disconnect()
	{
		if (IsConnected || IsConnecting)
		{
			StopConnectTimeout();
			RemoveThisDeviceFromBackend();
			IsConnected = false;
			IsConnecting = false;
			IsStandby = false;
			_preserveReceiverVolumeOnConnect = false;
			ClearFailure();
			AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
		}
	}

	internal void MarkGroupConnectFailed(string message)
	{
		StopConnectTimeout();
		IsPreempted = false;
		IsConnected = false;
		IsConnecting = false;
		IsStandby = false;
		_preserveReceiverVolumeOnConnect = false;
		AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
		SetFailure(message);
	}

	internal void ApplyDemoState(bool isConnected, int volume, int latencyMs, string activeCodec = "ALAC")
	{
		IsConnected = isConnected;
		Volume = VolumeCoordination.ClampVolume(volume);
		if (isConnected && latencyMs >= 0)
		{
			HasLatencyMeasurement = true;
			LatencyMs = latencyMs;
			ActiveCodec = activeCodec;
		}
		else
		{
			HasLatencyMeasurement = false;
			LatencyMs = -1;
			ActiveCodec = string.Empty;
		}
	}

	private void OnLanguageChanged()
	{
		if (IsFailed)
		{
			RenderFailureMessage();
		}
		if (_showingCalibrationSlow)
		{
			CalibrationStatusText = LocalizationManager.Instance.GetString("Device_CalibratingSlow", "Optimizing audio connection...");
		}
		this.OnPropertyChanged("LatencyText");
		this.OnPropertyChanged("DisplayIpAddress");
	}

	[RelayCommand]
	private void SubmitPin()
	{
		if (!string.IsNullOrWhiteSpace(PinCode))
		{
			AirPlayStreamer.Instance.SetPin(IPAddress, PinCode);
			IsPinRequested = false;
			PinCode = "";
			IsConnecting = true;
			StartConnectTimeout();
		}
	}

	private async Task HandleAccessPasswordRequestAsync(string ip, bool rejected)
	{
		StopConnectTimeout();
		IsConnecting = false;
		CalibrationStatusText = "";
		if (rejected)
		{
			AccessPasswordStore.Remove(StableId);
		}
		if (_passwordPromptOpen)
		{
			return;
		}
		if (_disposed || SilentStartupContext.SuppressUiDialogs)
		{
			FailAccessPasswordConnection(ip);
			return;
		}
		_passwordPromptOpen = true;
		try
		{
			LocalizationManager instance = LocalizationManager.Instance;
			(string, bool)? tuple = await AppDialogService.Instance.PromptPasswordAsync(instance.GetString("Device_PasswordTitle", "AirPlay Password"), instance.Format("Device_PasswordPrompt", "\"{0}\" is protected by an AirPlay password. Enter the password set for this speaker in the Home app.", Name), instance.GetString("Device_PasswordRemember", "Remember on this PC"), rememberByDefault: true, instance.GetString("Device_PasswordConnect", "Connect"), null, rejected ? instance.GetString("Device_PasswordIncorrect", "The speaker did not accept that password. Try again.") : null).ConfigureAwait(continueOnCapturedContext: true);
			if (_disposed)
			{
				return;
			}
			if (tuple.HasValue)
			{
				(string, bool) valueOrDefault = tuple.GetValueOrDefault();
				if (valueOrDefault.Item2)
				{
					AccessPasswordStore.Save(StableId, valueOrDefault.Item1);
				}
				else
				{
					AccessPasswordStore.Remove(StableId);
				}
				ClearFailure();
				IsConnecting = true;
				StartConnectTimeout();
				AirPlayStreamer.Instance.SetAccessPassword(ip, valueOrDefault.Item1);
			}
			else
			{
				FailAccessPasswordConnection(ip);
			}
		}
		finally
		{
			_passwordPromptOpen = false;
		}
	}

	private async Task<bool> PromptAccessPasswordBeforeConnectAsync()
	{
		if (_passwordPromptOpen)
		{
			return false;
		}
		_passwordPromptOpen = true;
		try
		{
			LocalizationManager instance = LocalizationManager.Instance;
			(string, bool)? tuple = await AppDialogService.Instance.PromptPasswordAsync(instance.GetString("Device_PasswordTitle", "AirPlay Password"), instance.Format("Device_PasswordPrompt", "\"{0}\" is protected by an AirPlay password. Enter the password set for this speaker in the Home app.", Name), instance.GetString("Device_PasswordRemember", "Remember on this PC"), rememberByDefault: true, instance.GetString("Device_PasswordConnect", "Connect")).ConfigureAwait(continueOnCapturedContext: true);
			if (!_disposed && tuple.HasValue)
			{
				(string, bool) valueOrDefault = tuple.GetValueOrDefault();
				if (valueOrDefault.Item2)
				{
					AccessPasswordStore.Save(StableId, valueOrDefault.Item1);
				}
				else
				{
					AccessPasswordStore.Remove(StableId);
				}
				if (!string.IsNullOrWhiteSpace(IPAddress))
				{
					AirPlayStreamer.Instance.SetAccessPassword(IPAddress, valueOrDefault.Item1);
				}
				return true;
			}
			return false;
		}
		finally
		{
			_passwordPromptOpen = false;
		}
	}

	private void FailAccessPasswordConnection(string ip)
	{
		IsConnected = false;
		IsConnecting = false;
		SetFailureFromResource("Device_PasswordRequiredFailure", "This speaker requires its AirPlay password (set in the Home app).");
		AirPlayStreamer.Instance.ClearAccessPassword(ip);
		RemoveThisDeviceFromBackend();
		AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
	}

	[RelayCommand]
	private void CancelPin()
	{
		StopConnectTimeout();
		IsPinRequested = false;
		PinCode = "";
		RemoveThisDeviceFromBackend();
		AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
		IsConnected = false;
		IsConnecting = false;
		ClearFailure();
	}

	[RelayCommand]
	private async Task ToggleConnectionAsync()
	{
		try
		{
			await ToggleConnectionCoreAsync().ConfigureAwait(continueOnCapturedContext: true);
		}
		finally
		{
			RefreshConnectionSelection();
		}
	}

	internal void RefreshConnectionSelection()
	{
		this.OnPropertyChanged("IsPlaybackActive");
	}

	private async Task ToggleConnectionCoreAsync()
	{
		HomeViewModel home = CoreApp.GetRequiredService<HomeViewModel>();
		DeviceGroupViewModel deviceGroupViewModel = home.FindNativeStereoGroup(this);
		if (deviceGroupViewModel != null)
		{
			if (!SilentStartupContext.SuppressUiDialogs)
			{
				await deviceGroupViewModel.ToggleConnectionCommand.ExecuteAsync((object)null).ConfigureAwait(continueOnCapturedContext: true);
			}
			else if (!deviceGroupViewModel.IsPlaybackActive)
			{
				deviceGroupViewModel.Connect();
			}
			return;
		}
		if (IsConnected || IsConnecting)
		{
			StopConnectTimeout();
			List<CalibrationDeviceIdentity> playbackProfile = (from device in CoreApp.GetRequiredService<HomeViewModel>().Devices
				where device != this && (device.IsConnected || device.IsConnecting)
				select new CalibrationDeviceIdentity(device.StableId, device.Name, device.Model, device.SupportsAirPlay2)).ToList();
			AirPlayStreamer.Instance.SetPlaybackProfile(playbackProfile);
			AirPlayStreamer.Instance.SetCalibrationLatency(null);
			RemoveThisDeviceFromBackend();
			IsConnected = false;
			IsConnecting = false;
			IsStandby = false;
			_preserveReceiverVolumeOnConnect = false;
			ClearFailure();
			AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
			return;
		}
		if (!AirPlayStreamer.TryAuthorizeNewConnection(out string reason))
		{
			SetFailure(reason);
			if (!SilentStartupContext.SuppressUiDialogs)
			{
				await AppDialogService.Instance.ShowErrorAsync(LocalizationManager.Instance.GetString("Release_StoreLicense", "Microsoft Store license"), reason).ConfigureAwait(continueOnCapturedContext: true);
			}
			return;
		}
		await HomePodConnectionNotice.ConnectIntentGate.WaitAsync().ConfigureAwait(continueOnCapturedContext: true);
		try
		{
			if (_disposed || IsConnected || IsConnecting || IsPinRequested || !(await HomePodConnectionNotice.ConfirmAsync(home.Devices.Where((DeviceViewModel device) => device.SupportsAirPlay2 == SupportsAirPlay2 && (device.IsConnected || device.IsConnecting)).Append(this).ToList()).ConfigureAwait(continueOnCapturedContext: true)) || _disposed || IsConnected || IsConnecting || IsPinRequested || !(await ConfirmMultiDevicePcmFallbackAsync().ConfigureAwait(continueOnCapturedContext: true)) || _disposed || IsConnected || IsConnecting || IsPinRequested)
			{
				return;
			}
			string password;
			bool flag = ReceiverAccessPolicy.ShouldPromptBeforeConnect(Access, AccessPasswordStore.TryGet(StableId, out password), SilentStartupContext.SuppressUiDialogs);
			if (flag)
			{
				flag = !(await PromptAccessPasswordBeforeConnectAsync().ConfigureAwait(continueOnCapturedContext: true));
			}
			if (flag || _disposed || IsConnected || IsConnecting || IsPinRequested)
			{
				return;
			}
			IsConnecting = true;
			CalibrationStatusText = "";
			ClearFailure();
			bool anotherOutputActive = home.Devices.Any((DeviceViewModel device) => device != this && (device.IsConnected || device.IsConnecting));
			_preserveReceiverVolumeOnConnect = VolumeCoordination.ShouldPreserveReceiverVolumeOnConnect(IsHomePodFamily, anotherOutputActive, ConnectTargetAddresses().Count);
			if (Volume <= 0.0)
			{
				DeviceConfig deviceConfig = SettingsManager.GetDeviceConfig(IPAddress);
				Volume = ((deviceConfig.Volume > 0.0) ? deviceConfig.Volume : ((double)Math.Max(35, SettingsManager.ReadConfig((AppConfig config) => config.MasterVolume))));
			}
			if (!_preserveReceiverVolumeOnConnect)
			{
				ApplyVolumeFromMaster(SettingsManager.ReadConfig((AppConfig config) => config.MasterVolume));
			}
			int num = 0;
			object obj = default(object);
			try
			{
				AirPlayStreamer.Instance.EnsureStarted();
				AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
				AirPlayStreamer.Instance.EventReceived += OnBackendEvent;
				ApplyRememberedEqForThisDevice();
				if (!_preserveReceiverVolumeOnConnect)
				{
					SetVolumeForTargets((int)Volume);
				}
				List<CalibrationDeviceIdentity> playbackProfile2 = (from device in home.Devices.Where((DeviceViewModel device) => device == this || device.IsConnected || device.IsConnecting).Distinct()
					select new CalibrationDeviceIdentity(device.StableId, device.Name, device.Model, device.SupportsAirPlay2)).ToList();
				AirPlayStreamer.Instance.SetPlaybackProfile(playbackProfile2);
				AirPlayStreamer.Instance.SetCalibrationLatency(null);
				AddThisDeviceToBackend();
				StartConnectTimeout();
				home.OnDeviceConnectedTogether(this);
			}
			catch (Exception ex)
			{
				obj = ex;
				num = 1;
			}
			if (num == 1)
			{
				Exception ex2 = (Exception)obj;
				StopConnectTimeout();
				try
				{
					RemoveThisDeviceFromBackend();
				}
				catch
				{
				}
				AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
				string message = LocalizationManager.Instance.Format("Device_BackendStartFailed", "Failed to start AirPlay backend: {0}", ex2.Message);
				if (!SilentStartupContext.SuppressUiDialogs)
				{
					await AppDialogService.Instance.ShowErrorAsync(LocalizationManager.Instance.GetString("Dialog_Error", "Error"), message).ConfigureAwait(continueOnCapturedContext: true);
				}
				IsConnecting = false;
				_preserveReceiverVolumeOnConnect = false;
				SetFailure(message);
			}
		}
		finally
		{
			HomePodConnectionNotice.ConnectIntentGate.Release();
		}
	}

	private static async Task<bool> ConfirmMultiDevicePcmFallbackAsync()
	{
		if (!string.Equals(SettingsManager.ReadConfig((AppConfig config) => config.AudioCodec), "PCM", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		try
		{
			if (!CoreApp.GetRequiredService<HomeViewModel>().Devices.Any((DeviceViewModel device) => device.IsConnected || device.IsConnecting))
			{
				return true;
			}
		}
		catch
		{
			return true;
		}
		if (SilentStartupContext.SuppressUiDialogs)
		{
			return true;
		}
		LocalizationManager instance = LocalizationManager.Instance;
		if (!(await AppDialogService.Instance.ConfirmAsync(instance.GetString("Codec_MultiDevicePcmFallbackTitle", "PCM will switch to ALAC"), instance.GetString("Codec_MultiDevicePcmFallbackMessage", "Experimental PCM is not stable for multi-device AirPlay 2 playback. This connection will use ALAC for all devices. Continue?"), instance.GetString("Dialog_OK", "OK"), instance.GetString("Dialog_Cancel", "Cancel")).ConfigureAwait(continueOnCapturedContext: true)))
		{
			return false;
		}
		SettingsManager.SetAudioCodec("ALAC");
		try
		{
			CoreApp.GetRequiredService<SettingsViewModel>().SyncCodecIndexFromConfig();
		}
		catch
		{
		}
		AirPlayStreamer.Instance.SetCodec("ALAC");
		return true;
	}

	private void StartConnectTimeout()
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Expected O, but got Unknown
		StopConnectTimeout();
		_connectTimeout = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(45.0)
		};
		_connectTimeout.Tick += delegate
		{
			StopConnectTimeout();
			if (IsConnecting && !IsConnected && !IsPinRequested)
			{
				IsConnecting = false;
				SetFailureFromResource("Device_ConnectTimeout", "Connection timed out after 45 seconds without CONNECTED, FAILED, or PIN_REQUIRED from the backend.");
				RemoveThisDeviceFromBackend();
				AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
				AddNetworkHintToFailureAsync(includeWarnings: true);
			}
		};
		_connectTimeout.Start();
	}

	private void StopConnectTimeout()
	{
		DispatcherTimer? connectTimeout = _connectTimeout;
		if (connectTimeout != null)
		{
			connectTimeout.Stop();
		}
		_connectTimeout = null;
	}

	private void OnBackendEvent(BackendEvent evt)
	{
		switch (EventScope().Route(evt))
		{
		case DeviceEventRoute.ResetLatencySmoothing:
			BeginInvokeOnUi(_latency.ResetSmoothing);
			break;
		case DeviceEventRoute.Codec:
			HandleCodecEvent(evt);
			break;
		case DeviceEventRoute.Volume:
		{
			if (!VolumeCoordination.TryReadReceiverVolume(evt, out string _, out int receiverVolume))
			{
				break;
			}
			BeginInvokeOnUi(delegate
			{
				ApplyVolumeFromReceiver(receiverVolume);
				if (IsConnected)
				{
					NotifyMasterOfLocalVolumeForDisplay();
				}
			});
			break;
		}
		case DeviceEventRoute.Latency:
			HandleLatencyEvent(evt);
			break;
		case DeviceEventRoute.Connection:
			BeginInvokeOnUi(delegate
			{
				DeviceTransition deviceTransition = DeviceConnectionMachine.Apply(ConnectionState(), evt, SilentStartupContext.SuppressUiDialogs);
				if (deviceTransition != null)
				{
					ApplyTransition(deviceTransition);
				}
			});
			break;
		}
	}

	private DeviceEventScope EventScope()
	{
		return new DeviceEventScope(IsAddressForThisDevice, PreferredBackendEventId);
	}

	private DeviceConnectionState ConnectionState()
	{
		return new DeviceConnectionState(IsConnected, IsConnecting, IsStandby, IsPreempted, IsPinRequested, _showingCalibrationSlow, _preserveReceiverVolumeOnConnect);
	}

	private void ApplyTransition(DeviceTransition transition)
	{
		DeviceConnectionState state = transition.State;
		if (transition.Has(DeviceEffects.StopConnectTimeout))
		{
			StopConnectTimeout();
		}
		IsPreempted = state.IsPreempted;
		IsPinRequested = state.IsPinRequested;
		IsConnecting = state.IsConnecting;
		IsConnected = state.IsConnected;
		IsStandby = state.IsStandby;
		_showingCalibrationSlow = state.IsCalibrationSlow;
		_preserveReceiverVolumeOnConnect = state.PreserveReceiverVolume;
		if (transition.Has(DeviceEffects.ClearCalibrationText))
		{
			CalibrationStatusText = "";
		}
		if (transition.Has(DeviceEffects.ShowCalibrationSlowText))
		{
			CalibrationStatusText = LocalizationManager.Instance.GetString("Device_CalibratingSlow", "Optimizing audio connection...");
		}
		if (transition.Has(DeviceEffects.ClearFailure))
		{
			ClearFailure();
		}
		if (transition.Has(DeviceEffects.ApplyConnectedDefaults))
		{
			if (string.IsNullOrWhiteSpace(ActiveCodec))
			{
				ActiveCodec = SettingsManager.GetAudioCodec();
			}
			if (transition.Has(DeviceEffects.PushVolume))
			{
				SetVolumeForTargets((int)Volume);
			}
			NotifyMasterOfLocalVolumeForDisplay();
			ApplyRememberedEqForThisDevice();
		}
		if (transition.Has(DeviceEffects.FailFromBackend))
		{
			SetFailureFromBackend(transition.FailureReason ?? string.Empty);
		}
		if (transition.Has(DeviceEffects.FailPinPairing))
		{
			SetFailureFromResource("Device_PinFailed", "PIN pairing failed. Check the PIN shown on the device and try again.");
		}
		if (transition.Has(DeviceEffects.ShowPinFailedDialog))
		{
			string title = LocalizationManager.Instance.GetString("Device_AuthErrorTitle", "Authentication Error");
			AppDialogService.Instance.ShowErrorAsync(title, FailureMessage);
		}
		if (transition.Has(DeviceEffects.StopBackendRetries))
		{
			RemoveThisDeviceFromBackend();
			AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
		}
		if (transition.Has(DeviceEffects.AddNetworkHint))
		{
			AddNetworkHintToFailureAsync(NetworkCheckHint.IsNetworkSymptom(_lastFailureRawReason));
		}
		if (transition.Has(DeviceEffects.StartConnectTimeout))
		{
			StartConnectTimeout();
		}
		if (transition.Has(DeviceEffects.RequestAccessPassword) && transition.EventIp != null)
		{
			HandleAccessPasswordRequestAsync(transition.EventIp, transition.PasswordRejected);
		}
	}

	private void HandleLatencyEvent(BackendEvent evt)
	{
		switch (evt.Kind)
		{
		case "PLAYBACK":
		{
			string text = evt.GetString("ip");
			if (text == null)
			{
				break;
			}
			long? @int = evt.GetInt64("ms");
			if (!@int.HasValue)
			{
				break;
			}
			long valueOrDefault6 = @int.GetValueOrDefault();
			BeginInvokeOnUi(delegate
			{
				int? num = _latency.AddPlayback(text, (int)Math.Clamp(valueOrDefault6, 0L, 2147483647L));
				if (num.HasValue)
				{
					int valueOrDefault7 = num.GetValueOrDefault();
					LatencyMs = valueOrDefault7;
					HasLatencyMeasurement = true;
				}
			});
			break;
		}
		case "RTX":
		{
			if (!SettingsManager.ReadConfig((AppConfig config) => config.DeveloperModeEnabled))
			{
				break;
			}
			long? @int = evt.GetInt64("requests");
			if (!@int.HasValue)
			{
				break;
			}
			long valueOrDefault3 = @int.GetValueOrDefault();
			@int = evt.GetInt64("requested");
			if (!@int.HasValue)
			{
				break;
			}
			long valueOrDefault4 = @int.GetValueOrDefault();
			@int = evt.GetInt64("resent");
			if (@int.HasValue)
			{
				long valueOrDefault5 = @int.GetValueOrDefault();
				BeginInvokeOnUi(delegate
				{
					_devRtxRequests = valueOrDefault3;
					_devRtxRequested = valueOrDefault4;
					_devRtxResent = valueOrDefault5;
					RefreshDevTelemetry();
				});
			}
			break;
		}
		case "DRIFT":
		{
			if (!SettingsManager.ReadConfig((AppConfig config) => config.DeveloperModeEnabled))
			{
				break;
			}
			long? @int = evt.GetInt64("drift_ppb");
			if (!@int.HasValue)
			{
				break;
			}
			long valueOrDefault = @int.GetValueOrDefault();
			@int = evt.GetInt64("samples");
			if (@int.HasValue)
			{
				long valueOrDefault2 = @int.GetValueOrDefault();
				BeginInvokeOnUi(delegate
				{
					_devDriftPpb = valueOrDefault;
					_devDriftSamples = valueOrDefault2;
					RefreshDevTelemetry();
				});
			}
			break;
		}
		}
	}

	private void HandleCodecEvent(BackendEvent evt)
	{
		string codec = evt.GetString("codec")?.ToUpperInvariant();
		BeginInvokeOnUi(delegate
		{
			switch (evt.Kind)
			{
			case "ACTIVE":
				if (codec != null)
				{
					ActiveCodec = codec;
				}
				break;
			case "ENDPOINT_ACTIVE":
				if (codec == null)
				{
					break;
				}
				goto IL_0092;
			case "EXACT_ENDPOINT_ACTIVE":
				if (codec == null)
				{
					break;
				}
				goto IL_0092;
			case "REQUESTED":
				if (codec != null && (IsConnected || IsConnecting))
				{
					ActiveCodec = codec;
				}
				break;
			case "CONFIRM_REQUIRED":
			{
				string text = evt.GetString("ip");
				if (text != null)
				{
					string text2 = evt.GetString("requested");
					if (text2 != null)
					{
						string text3 = evt.GetString("suggested");
						if (text3 != null)
						{
							HandleCodecConfirmRequiredAsync(text, text2, text3, evt.GetString("reason") ?? "incompatible");
						}
					}
				}
				break;
			}
			case "AIRPORT_PCM_WARNING":
				{
					ShowAirportPcmWarningOnceAsync();
					break;
				}
				IL_0092:
				ActiveCodec = codec;
				break;
			}
		});
	}

	private static void BeginInvokeOnUi(Action action)
	{
		Application current = Application.Current;
		Dispatcher val = ((current != null) ? ((DispatcherObject)current).Dispatcher : null);
		if (val == null || val.HasShutdownStarted || val.HasShutdownFinished)
		{
			return;
		}
		try
		{
			val.BeginInvoke((Delegate)action, Array.Empty<object>());
		}
		catch (InvalidOperationException)
		{
		}
	}

	private async Task HandleCodecConfirmRequiredAsync(string ip, string requested, string suggested, string reason)
	{
		StopConnectTimeout();
		IsConnecting = false;
		LocalizationManager loc = LocalizationManager.Instance;
		if (SilentStartupContext.SuppressUiDialogs)
		{
			AirPlayStreamer.Instance.ConfirmCodecSwitch(ip, accept: false);
			SetFailure(loc.Format("Codec_CancelledMessage", "Connection cancelled: {0} is not supported on this device.", requested.ToUpperInvariant()));
			AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
			return;
		}
		string reasonText = string.Equals(reason, "homepod_requires_alac", StringComparison.OrdinalIgnoreCase)
			? loc.GetString("Codec_Reason_HomePodRequiresAlac", "HomePod requires ALAC")
			: reason.Replace('_', ' ');
		string message = loc.Format("Codec_IncompatibleMessage", "This device does not support {0} ({1}). Switch the global codec to {2} and retry?", requested.ToUpperInvariant(), reasonText, suggested.ToUpperInvariant());
		string dedupeKey = "codec-confirm:" + ip;
		bool? flag = await AppDialogService.Instance.TryConfirmOnceAsync(dedupeKey, loc.GetString("Codec_IncompatibleTitle", "Codec Not Supported"), message, loc.GetString("Dialog_Yes", "Yes"), loc.GetString("Dialog_No", "No")).ConfigureAwait(continueOnCapturedContext: true);
		if (!flag.HasValue)
		{
			return;
		}
		if (flag == true)
		{
			SettingsManager.SetAudioCodec(suggested);
			try
			{
				CoreApp.GetRequiredService<SettingsViewModel>().SyncCodecIndexFromConfig();
			}
			catch
			{
			}
			AirPlayStreamer.Instance.ConfirmCodecSwitch(ip, accept: true);
			IsConnecting = true;
			StartConnectTimeout();
		}
		else
		{
			AirPlayStreamer.Instance.ConfirmCodecSwitch(ip, accept: false);
			SetFailure(loc.Format("Codec_CancelledMessage", "Connection cancelled: {0} is not supported on this device.", requested.ToUpperInvariant()));
			AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
		}
	}

	private static async Task ShowAirportPcmWarningOnceAsync()
	{
		bool shouldShow = false;
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			if (!config.AirportPcmWarningShown)
			{
				config.AirportPcmWarningShown = true;
				shouldShow = true;
			}
		});
		if (shouldShow && !SilentStartupContext.SuppressUiDialogs)
		{
			LocalizationManager instance = LocalizationManager.Instance;
			await AppDialogService.Instance.ShowInfoAsync(instance.GetString("Codec_AirportPcmWarningTitle", "AirPort Express PCM"), instance.GetString("Codec_AirportPcmWarningMessage", "You selected PCM for an older AirPort Express. ALAC is usually more stable on old Wi-Fi, but PCM is allowed because you chose it explicitly.")).ConfigureAwait(continueOnCapturedContext: true);
		}
	}

	private void ApplyRememberedEqForThisDevice()
	{
		try
		{
			DeviceEqMemorySnapshot deviceEqMemorySnapshot = SettingsManager.ResolveDeviceEqMemory(IPAddress);
			if (!deviceEqMemorySnapshot.HadStoredMemory)
			{
				EqConfigSnapshot eqConfigSnapshot = SettingsManager.ReadEqConfigSnapshot();
				SettingsManager.RememberDeviceEq(IPAddress, eqConfigSnapshot.PresetId, eqConfigSnapshot.PreampDb, eqConfigSnapshot.BandGains);
			}
			else
			{
				IMessengerExtensions.Send<EqDeviceMemoryApplyMessage>((IMessenger)(object)WeakReferenceMessenger.Default, new EqDeviceMemoryApplyMessage(IPAddress, deviceEqMemorySnapshot));
			}
		}
		catch (Exception)
		{
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
			StopConnectTimeout();
			RemoveThisDeviceFromBackend();
			AirPlayStreamer.Instance.EventReceived -= OnBackendEvent;
			WeakReferenceMessenger.Default.UnregisterAll((object)this);
		}
	}

	private void ApplyVolumeFromMaster(int volume)
	{
		int num = VolumeCoordination.ClampVolume(volume);
		_suppressVolumeSideEffects = true;
		try
		{
			if (Math.Abs(Volume - (double)num) > 0.01)
			{
				Volume = num;
			}
			else
			{
				SettingsManager.SetDeviceConfig(IPAddress, num);
			}
		}
		finally
		{
			_suppressVolumeSideEffects = false;
		}
		if (VolumeCoordination.ShouldSendBackendAfterMasterApply(IsConnected))
		{
			SetVolumeForTargets(num);
		}
	}

	private void ApplyVolumeFromReceiver(int volume)
	{
		int num = VolumeCoordination.ClampVolume(volume);
		_suppressVolumeSideEffects = true;
		try
		{
			if (Math.Abs(Volume - (double)num) > 0.01)
			{
				Volume = num;
			}
			else
			{
				SettingsManager.SetDeviceConfig(IPAddress, num);
			}
		}
		finally
		{
			_suppressVolumeSideEffects = false;
		}
	}

	internal void ApplyVolumeFromGroup(int volume)
	{
		ApplyVolumeFromMaster(volume);
	}

	private void NotifyMasterOfLocalVolumeForDisplay()
	{
		if (IsConnected)
		{
			int volume = VolumeCoordination.ClampVolume(Volume);
			IMessengerExtensions.Send<VolumeChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, new VolumeChangedMessage(volume, VolumeChangeOrigin.Device, IPAddress));
		}
	}

	private void SetVolumeForTargets(int volume)
	{
		int volume2 = VolumeCoordination.ClampVolume(volume);
		if (!SupportsAirPlay2)
		{
			BackendEndpointSelection preferredBackendSelection = PreferredBackendSelection;
			if ((object)preferredBackendSelection != null)
			{
				AirPlayStreamer.Instance.SetVolume(preferredBackendSelection, volume2);
				return;
			}
		}
		foreach (string item in VolumeCoordination.DedupeTargetIps(VolumeTargetAddresses()))
		{
			AirPlayStreamer.Instance.SetVolume(item, volume2);
		}
	}

	private IEnumerable<string> VolumeTargetAddresses()
	{
		if (!string.IsNullOrWhiteSpace(IPAddress))
		{
			yield return IPAddress;
		}
		string[] array = _volumeTargetAddresses.Snapshot();
		foreach (string text in array)
		{
			if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, IPAddress, StringComparison.OrdinalIgnoreCase))
			{
				yield return text;
			}
		}
	}

	internal bool IsAddressForThisDevice(string ipAddress)
	{
		if (!string.Equals(ipAddress, IPAddress, StringComparison.OrdinalIgnoreCase))
		{
			return _volumeTargetAddresses.Contains(ipAddress);
		}
		return true;
	}

	private void SetFailure(string message)
	{
		ResetFailureAddenda();
		_lastFailureRawReason = null;
		_failureResourceKey = null;
		_failureText = (string.IsNullOrWhiteSpace(message) ? LocalizationManager.Instance.GetString("Device_ConnectionFailedNoReason", "Connection failed. No detailed reason was provided by the backend.") : message.Trim());
		FailureMessage = _failureText;
		IsFailed = true;
	}

	private void SetFailureFromResource(string resourceKey, string englishFallback)
	{
		ResetFailureAddenda();
		_lastFailureRawReason = null;
		_failureResourceKey = resourceKey;
		FailureMessage = LocalizationManager.Instance.GetString(resourceKey, englishFallback);
		IsFailed = true;
	}

	private void SetFailureFromBackend(string rawReason)
	{
		ResetFailureAddenda();
		_failureResourceKey = null;
		_lastFailureRawReason = rawReason ?? string.Empty;
		FailureMessage = BackendFailureFormatter.Format(_lastFailureRawReason);
		IsFailed = true;
	}

	private void ClearFailure()
	{
		ResetFailureAddenda();
		FailureMessage = "";
		IsFailed = false;
		_lastFailureRawReason = null;
		_failureResourceKey = null;
	}

	private void ResetFailureAddenda()
	{
		_networkHintCode = null;
		_firewallNote = null;
		_failureText = null;
		_failureGeneration++;
	}

	private string FailureBaseText()
	{
		if (!string.IsNullOrEmpty(_failureResourceKey))
		{
			return LocalizationManager.Instance.GetString(_failureResourceKey, _failureResourceKey);
		}
		if (_lastFailureRawReason != null)
		{
			return BackendFailureFormatter.Format(_lastFailureRawReason);
		}
		return _failureText ?? FailureMessage;
	}

	private void RenderFailureMessage()
	{
		string text = NetworkCheckHint.Append(FailureBaseText(), _networkHintCode);
		(string, string)? firewallNote = _firewallNote;
		if (firewallNote.HasValue)
		{
			(string, string) valueOrDefault = firewallNote.GetValueOrDefault();
			text = NetworkCheckHint.AppendParagraph(text, LocalizationManager.Instance.GetString(valueOrDefault.Item1, valueOrDefault.Item2));
		}
		FailureMessage = text;
	}

	private async Task AddNetworkHintToFailureAsync(bool includeWarnings)
	{
		int generation = _failureGeneration;
		string ip = IPAddress;
		string json;
		try
		{
			json = await AirPlayStreamer.Instance.RequestNetworkCheckAsync((IReadOnlyCollection<string>)(object)new string[1] { ip }, NetworkCheckTimeout).ConfigureAwait(continueOnCapturedContext: true);
		}
		catch (Exception)
		{
			return;
		}
		NetworkFinding networkFinding = NetworkCheckResult.TryParse(json)?.MostRelevantFor(ip, includeWarnings);
		if (!(networkFinding == null) && generation == _failureGeneration && IsFailed)
		{
			_networkHintCode = networkFinding.Code;
			RenderFailureMessage();
		}
	}

	[RelayCommand]
	private async Task FixFirewallAsync()
	{
		if (IsFixingFirewall || !FirewallFix.IsFixable(_networkHintCode))
		{
			return;
		}
		int generation = _failureGeneration;
		string ip = IPAddress;
		IsFixingFirewall = true;
		try
		{
			FirewallFixFlowResult result = await FirewallFixFlow.RunAsync((IReadOnlyCollection<string>)(object)new string[1] { ip }, AppDialogService.Instance).ConfigureAwait(continueOnCapturedContext: true);
			if (generation == _failureGeneration && IsFailed)
			{
				FirewallCardUpdate firewallCardUpdate = CardUpdateAfterFirewallFix(result, ip);
				if (firewallCardUpdate != FirewallCardUpdate.Keep)
				{
					_networkHintCode = null;
					_firewallNote = firewallCardUpdate switch
					{
						FirewallCardUpdate.Applied => ("Network_FixFirewall_Applied", "Windows Firewall now lets speakers reach AirStereo2. Connect again."), 
						FirewallCardUpdate.StillBlocked => ("Network_FixFirewall_StillBlocked", "The rule was added, but Windows Firewall still blocks AirStereo2. These settings may be managed by your organization."), 
						_ => null, 
					};
					RenderFailureMessage();
					this.OnPropertyChanged("FirewallFixVisibility");
				}
			}
		}
		finally
		{
			IsFixingFirewall = false;
		}
	}

	internal static FirewallCardUpdate CardUpdateAfterFirewallFix(FirewallFixFlowResult result, string ip)
	{
		FirewallFixOutcome? outcome = result.Outcome;
		if (outcome.HasValue)
		{
			if (outcome.GetValueOrDefault() == FirewallFixOutcome.Applied)
			{
				return FirewallFixFlow.StillBlocked(result.After, ip) ? FirewallCardUpdate.StillBlocked : FirewallCardUpdate.Applied;
			}
		}
		else if (result.After != null && !FirewallFixFlow.StillBlocked(result.After, ip))
		{
			return FirewallCardUpdate.DropHint;
		}
		return FirewallCardUpdate.Keep;
	}

	private void RefreshDevTelemetry()
	{
		LocalizationManager instance = LocalizationManager.Instance;
		DevTelemetry = instance.Format("Device_DevTelemetry", "drift {0}ppb ({1}) · rtx {2}/{3}/{4}", _devDriftPpb, _devDriftSamples, _devRtxRequests, _devRtxRequested, _devRtxResent);
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnIPAddressChanged(string value)
	{
		RegisterSavedAccessPassword();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnIsConnectedChanged(bool value)
	{
		_latency.Clear();
		LatencyMs = -1;
		HasLatencyMeasurement = false;
		this.OnPropertyChanged("ActiveCodecVisibility");
		if (!value)
		{
			ActiveCodec = "";
			DevTelemetry = "";
			_devDriftPpb = 0L;
			_devDriftSamples = 0L;
			_devRtxRequests = 0L;
			_devRtxRequested = 0L;
			_devRtxResent = 0L;
		}
		this.OnPropertyChanged("DevTelemetryText");
		this.OnPropertyChanged("DevTelemetryVisibility");
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnVolumeChanged(double value)
	{
		int num = VolumeCoordination.ClampVolume(value);
		SettingsManager.SetDeviceConfig(IPAddress, num);
		if (VolumeCoordination.ShouldSendBackendFromLocalVolumeChange(_suppressVolumeSideEffects, IsConnected))
		{
			SetVolumeForTargets(num);
			if (VolumeCoordination.ShouldNotifyMasterOfDeviceVolume(_suppressVolumeSideEffects, IsConnected))
			{
				IMessengerExtensions.Send<VolumeChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, new VolumeChangedMessage(num, VolumeChangeOrigin.Device, IPAddress));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnStableIdChanged(string value)
	{
		RegisterSavedAccessPassword();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnIsDefaultOutputChanged(bool value)
	{
		if (!_suppressDefaultOutputPersist && !string.IsNullOrWhiteSpace(StableId))
		{
			SettingsManager.SetDefaultOutput(new DefaultOutputDeviceRecord
			{
				StableId = StableId,
				DisplayName = Name,
				Model = Model,
				LastKnownIp = IPAddress,
				GroupKey = (AirPlayGroupKey ?? StereoPairKey)
			}, value);
			IMessengerExtensions.Send<DefaultOutputsChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, new DefaultOutputsChangedMessage());
		}
	}
}
