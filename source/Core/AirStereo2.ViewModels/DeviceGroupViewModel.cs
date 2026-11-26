using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using AirStereo2.Messages;
using AirStereo2.Models;
using AirStereo2.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel.__Internals;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AirStereo2.ViewModels;

public class DeviceGroupViewModel : ObservableObject, IDisposable
{
	private readonly Action<DeviceGroupViewModel> _onMembershipChanged;

	private readonly Action<DeviceGroupViewModel> _onPlaybackStopped;

	private bool _suppressAggregateRefresh;

	private bool _suppressVolumePropagation;

	private bool _disposed;

	[ObservableProperty]
	[NotifyPropertyChangedFor("DockAccessibleLabel")]
	private string _name = string.Empty;

	[ObservableProperty]
	private bool _isExpanded = true;

	[ObservableProperty]
	private bool _isAppleSeeded;

	[ObservableProperty]
	[NotifyPropertyChangedFor("IsPlaybackActive")]
	[NotifyPropertyChangedFor("VolumeVisibility")]
	[NotifyPropertyChangedFor("DockStatus")]
	[NotifyPropertyChangedFor("DockStatusText")]
	[NotifyPropertyChangedFor("DockAccessibleLabel")]
	[NotifyPropertyChangedFor("ActiveCodecVisibility")]
	[NotifyPropertyChangedFor("ConnectingRingVisibility")]
	private bool _isConnected;

	[ObservableProperty]
	[NotifyPropertyChangedFor("IsPlaybackActive")]
	[NotifyPropertyChangedFor("IsConnectEnabled")]
	[NotifyPropertyChangedFor("DockStatus")]
	[NotifyPropertyChangedFor("DockStatusText")]
	[NotifyPropertyChangedFor("DockAccessibleLabel")]
	[NotifyPropertyChangedFor("ConnectingRingVisibility")]
	private bool _isConnecting;

	[ObservableProperty]
	[NotifyPropertyChangedFor("DockStatus")]
	[NotifyPropertyChangedFor("DockStatusText")]
	[NotifyPropertyChangedFor("DockAccessibleLabel")]
	private bool _isFailed;

	[ObservableProperty]
	[NotifyPropertyChangedFor("LatencyText")]
	private int _latencyMs = -1;

	[ObservableProperty]
	[NotifyPropertyChangedFor("LatencyText")]
	private bool _hasLatencyMeasurement;

	[ObservableProperty]
	[NotifyPropertyChangedFor("ActiveCodecText")]
	[NotifyPropertyChangedFor("ActiveCodecVisibility")]
	private string _activeCodec = string.Empty;

	[ObservableProperty]
	private double _volume = 50.0;

	private bool _autoNamed = true;

	[ObservableProperty]
	[NotifyPropertyChangedFor("PreemptionNoticeVisibility")]
	private bool _isPreempted;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<DeviceViewModel?>? removeMemberCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? stopWaitingForReclaimCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? toggleConnectionCommand;

	public ObservableCollection<DeviceViewModel> Members { get; } = new ObservableCollection<DeviceViewModel>();

	public string AppleGroupKey { get; internal set; } = string.Empty;

	internal string NativeStereoPairId { get; set; } = string.Empty;

	public bool IsNativeStereoPair => HomePodStereoPolicy.IsNativeStereoPair(Members);

	public bool CanEditMembership => !IsNativeStereoPair;

	public bool IsPlaybackActive
	{
		get
		{
			if (!IsConnected)
			{
				return IsConnecting;
			}
			return true;
		}
	}

	public bool IsConnectEnabled
	{
		get
		{
			if (!IsConnecting)
			{
				return Members.Count > 0;
			}
			return false;
		}
	}

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

	public Visibility FailureVisibility => Visibility.Collapsed;

	public string FailureMessage => string.Empty;

	public bool IsPinRequested => false;

	public bool IsStandby => false;

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

	public DockStatusKind DockStatus => DeviceDockStatus.Resolve(IsFailed, isPinRequested: false, IsConnecting, IsConnected, isStandby: false);

	public string DockStatusText => LocalizationManager.Instance.GetString(DeviceDockStatus.ResourceKey(DockStatus), DockStatus.ToString());

	public string DockAccessibleLabel => LocalizationManager.Instance.Format("Dock_DeviceStatusLabel", "{0} — {1}", Name, DockStatusText);

	public DeviceIconKind IconKind => ResolveGroupIcon(Members);

	public Geometry? IconData => DeviceIconCatalog.GetGeometry(IconKind);

	public IReadOnlyList<string> MemberStableIds => (from m in Members
		select m.StableId into id
		where !string.IsNullOrWhiteSpace(id)
		select id).ToList();

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
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockAccessibleLabel);
				_name = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.Name);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockAccessibleLabel);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsExpanded
	{
		get
		{
			return _isExpanded;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isExpanded, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsExpanded);
				_isExpanded = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsExpanded);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsAppleSeeded
	{
		get
		{
			return _isAppleSeeded;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isAppleSeeded, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsAppleSeeded);
				_isAppleSeeded = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsAppleSeeded);
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
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsPlaybackActive);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.VolumeVisibility);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatus);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatusText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockAccessibleLabel);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ActiveCodecVisibility);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConnectingRingVisibility);
				_isConnected = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsConnected);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsPlaybackActive);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.VolumeVisibility);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatus);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatusText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockAccessibleLabel);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ActiveCodecVisibility);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ConnectingRingVisibility);
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
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsPlaybackActive);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsConnectEnabled);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatus);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatusText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockAccessibleLabel);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConnectingRingVisibility);
				_isConnecting = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsConnecting);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsPlaybackActive);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsConnectEnabled);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatus);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatusText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockAccessibleLabel);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ConnectingRingVisibility);
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
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatus);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockStatusText);
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DockAccessibleLabel);
				_isFailed = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsFailed);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatus);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockStatusText);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DockAccessibleLabel);
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

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<DeviceViewModel?> RemoveMemberCommand => (IRelayCommand<DeviceViewModel?>)(object)(removeMemberCommand ?? (removeMemberCommand = new RelayCommand<DeviceViewModel>((Action<DeviceViewModel>)RemoveMember, (Predicate<DeviceViewModel>)CanRemoveMember)));

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

	public DeviceGroupViewModel(Action<DeviceGroupViewModel>? onMembershipChanged = null, Action<DeviceGroupViewModel>? onPlaybackStopped = null)
	{
		_onMembershipChanged = onMembershipChanged ?? ((Action<DeviceGroupViewModel>)delegate
		{
		});
		_onPlaybackStopped = onPlaybackStopped ?? ((Action<DeviceGroupViewModel>)delegate
		{
		});
	}

	internal static DeviceIconKind ResolveGroupIcon(IReadOnlyCollection<DeviceViewModel> members)
	{
		bool flag = members.Any((DeviceViewModel m) => DeviceViewModel.LooksLikeAppleTv(m.Model, m.Name));
		bool flag2 = members.Any((DeviceViewModel m) => DeviceViewModel.LooksLikeHomePodFamily(m.Model, m.Name));
		bool flag3 = members.Any((DeviceViewModel m) => DeviceViewModel.LooksLikeHomePodMini(m.Model, m.Name));
		if (flag && flag3)
		{
			return DeviceIconKind.HomePodMiniAndAppleTv;
		}
		if (flag && flag2)
		{
			return DeviceIconKind.HomePodAndAppleTv;
		}
		if (flag3 && members.Count >= 2)
		{
			return DeviceIconKind.HomePodMiniPair;
		}
		if (flag2 && members.Count >= 2)
		{
			return DeviceIconKind.HomePodPair;
		}
		return DeviceIconKind.HifiSpeakerPair;
	}

	internal static string BuildDefaultName(IEnumerable<DeviceViewModel> members)
	{
		List<string> list = (from m in members
			select m.Name into n
			where !string.IsNullOrWhiteSpace(n)
			select n).ToList();
		if (list.Count != 0)
		{
			return string.Join(" + ", list);
		}
		return LocalizationManager.Instance.GetString("Group_DefaultName", "Device Group");
	}

	public bool ContainsDevice(DeviceViewModel device)
	{
		return Members.Contains(device);
	}

	public bool ContainsStableId(string? stableId)
	{
		if (!string.IsNullOrWhiteSpace(stableId))
		{
			return Members.Any((DeviceViewModel m) => AirPlayDeviceIdentity.IdsEqual(m.StableId, stableId));
		}
		return false;
	}

	internal void AddMember(DeviceViewModel device)
	{
		if ((Members.Count <= 0 || Members[0].SupportsAirPlay2 == device.SupportsAirPlay2) && !ContainsDevice(device) && !ContainsStableId(device.StableId))
		{
			Members.Add(device);
			AttachMember(device);
			RefreshAfterMembershipChange();
		}
	}

	private bool CanRemoveMember(DeviceViewModel? device)
	{
		if (CanEditMembership && device != null)
		{
			return Members.Contains(device);
		}
		return false;
	}

	[RelayCommand(CanExecute = "CanRemoveMember")]
	private void RemoveMember(DeviceViewModel? device)
	{
		if (!CanRemoveMember(device) || device == null)
		{
			return;
		}
		if (device.IsConnected || device.IsConnecting)
		{
			if (!device.SupportsAirPlay2)
			{
				BackendEndpointSelection preferredBackendSelection = device.PreferredBackendSelection;
				if ((object)preferredBackendSelection != null)
				{
					AirPlayStreamer.Instance.RemoveDevice(preferredBackendSelection);
					goto IL_004c;
				}
			}
			AirPlayStreamer.Instance.RemoveDevice(device.IPAddress);
			goto IL_004c;
		}
		goto IL_0052;
		IL_0052:
		DetachMember(device);
		Members.Remove(device);
		RefreshAfterMembershipChange();
		return;
		IL_004c:
		device.MarkDisconnectedByGroup();
		goto IL_0052;
	}

	private void RefreshAfterMembershipChange()
	{
		if (!_suppressAggregateRefresh)
		{
			if (string.IsNullOrWhiteSpace(Name) || _autoNamed)
			{
				Name = BuildDefaultName(Members);
				_autoNamed = true;
			}
			this.OnPropertyChanged("IconKind");
			this.OnPropertyChanged("IconData");
			RefreshNativePairState();
			RefreshAggregate();
			_onMembershipChanged(this);
		}
	}

	internal void SetExplicitName(string name)
	{
		_autoNamed = false;
		Name = name;
	}

	internal void SetMembers(IEnumerable<DeviceViewModel> devices)
	{
		List<DeviceViewModel> list = devices.ToList();
		bool? flag = list.FirstOrDefault()?.SupportsAirPlay2;
		_suppressAggregateRefresh = true;
		foreach (DeviceViewModel member in Members)
		{
			DetachMember(member);
		}
		Members.Clear();
		foreach (DeviceViewModel item in list)
		{
			if ((!flag.HasValue || item.SupportsAirPlay2 == flag.Value) && !ContainsDevice(item) && !ContainsStableId(item.StableId))
			{
				Members.Add(item);
				AttachMember(item);
			}
		}
		_suppressAggregateRefresh = false;
		RefreshAfterMembershipChange();
	}

	public void RefreshAggregate()
	{
		IReadOnlyList<DeviceViewModel> source = TransportMembers();
		IsConnecting = source.Any((DeviceViewModel m) => m.IsConnecting);
		IsConnected = source.Any((DeviceViewModel m) => m.IsConnected);
		IsFailed = !IsConnected && !IsConnecting && source.Any((DeviceViewModel m) => m.IsFailed);
		List<int> list = (from m in source
			where m.IsConnected && m.HasLatencyMeasurement
			select m.LatencyMs).ToList();
		HasLatencyMeasurement = list.Count > 0;
		LatencyMs = ((list.Count > 0) ? list.Max() : (-1));
		ActiveCodec = source.FirstOrDefault((DeviceViewModel m) => m.IsConnected && !string.IsNullOrWhiteSpace(m.ActiveCodec))?.ActiveCodec ?? string.Empty;
		IsPreempted = Members.Any((DeviceViewModel m) => m.IsPreempted);
		this.OnPropertyChanged("LatencyText");
	}

	[RelayCommand]
	private void StopWaitingForReclaim()
	{
		foreach (DeviceViewModel item in Members.ToList())
		{
			item.StopWaitingForReclaim();
		}
	}

	private void AttachMember(DeviceViewModel member)
	{
		((ObservableObject)member).PropertyChanged += OnMemberPropertyChanged;
	}

	private void DetachMember(DeviceViewModel member)
	{
		((ObservableObject)member).PropertyChanged -= OnMemberPropertyChanged;
	}

	private void RefreshNativePairState()
	{
		this.OnPropertyChanged("IsNativeStereoPair");
		this.OnPropertyChanged("CanEditMembership");
		((IRelayCommand)RemoveMemberCommand).NotifyCanExecuteChanged();
	}

	private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		bool flag;
		switch (e.PropertyName)
		{
		case "TightSyncId":
		case "GroupingObservedAt":
		case "GroupingExpiresAt":
		case "GroupingEvidenceSource":
		case "GroupingEvidenceIdentity":
		case "StableId":
		case "AirPlayInstanceName":
		case "RaopInstanceName":
		case "IPAddress":
		case "Model":
		case "Name":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			RefreshNativePairState();
		}
		if (e.PropertyName == "IsPinRequested" && sender is DeviceViewModel { IsPinRequested: not false })
		{
			IsExpanded = true;
		}
		switch (e.PropertyName)
		{
		case "IsConnected":
		case "IsPreempted":
		case "ActiveCodec":
		case "IsConnecting":
		case "IsFailed":
		case "LatencyMs":
		case "HasLatencyMeasurement":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			bool isPlaybackActive = IsPlaybackActive;
			RefreshAggregate();
			if (isPlaybackActive && !IsPlaybackActive)
			{
				_onPlaybackStopped(this);
			}
		}
		string propertyName = e.PropertyName;
		if ((propertyName == "Name" || propertyName == "Model") ? true : false)
		{
			if (_autoNamed)
			{
				Name = BuildDefaultName(Members);
			}
			this.OnPropertyChanged("IconKind");
			this.OnPropertyChanged("IconData");
		}
		if (!(e.PropertyName == "Volume") || !IsConnected || _suppressVolumePropagation || VolumeCoordination.IsApplyingVolume)
		{
			return;
		}
		IReadOnlyList<DeviceViewModel> readOnlyList = TransportMembers();
		if (readOnlyList.Count <= 0)
		{
			return;
		}
		int num = VolumeCoordination.ClampVolume(readOnlyList.Select((DeviceViewModel m) => m.Volume).Average());
		if (Math.Abs(Volume - (double)num) >= 1.0)
		{
			_suppressVolumePropagation = true;
			try
			{
				Volume = num;
			}
			finally
			{
				_suppressVolumePropagation = false;
			}
			IMessengerExtensions.Send<VolumeChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, new VolumeChangedMessage(num, VolumeChangeOrigin.Group));
		}
	}

	public void Dispose()
	{
		_disposed = true;
		foreach (DeviceViewModel member in Members)
		{
			DetachMember(member);
		}
	}

	internal IReadOnlyList<DeviceViewModel> TransportMembers()
	{
		HashSet<string> parentGroupIds = (from member in Members
			where member.IsHomePodFamily && member.ParentGroupContainsDiscoverableLeader && !string.IsNullOrWhiteSpace(member.GroupPublicName) && !string.IsNullOrWhiteSpace(member.ParentGroupId)
			select member.ParentGroupId.Trim()).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<DeviceViewModel> list = Members.Where((DeviceViewModel member) => !DeviceViewModel.LooksLikeAppleTv(member.Model, member.Name) || !member.IsGroupLeader || string.IsNullOrWhiteSpace(member.GroupId) || !parentGroupIds.Contains(member.GroupId.Trim())).ToList();
		if (list.Count <= 0)
		{
			return Members.ToList();
		}
		return list;
	}

	internal IReadOnlyList<string> ConnectTargets()
	{
		IEnumerable<string> source = from m in TransportMembers()
			orderby DeviceViewModel.LooksLikeAppleTv(m.Model, m.Name) descending
			select m.IPAddress;
		return DeviceViewModel.BuildConnectTargets(source.FirstOrDefault(), source.Skip(1));
	}

	internal IReadOnlyList<BackendEndpointSelection> ExactClassicConnectTargets()
	{
		return (from @group in (from member in Members
				where !member.SupportsAirPlay2
				select member.PreferredBackendSelection).OfType<BackendEndpointSelection>().GroupBy<BackendEndpointSelection, string>((BackendEndpointSelection endpoint) => DeviceViewModel.BuildBackendEndpointEventId(endpoint.Ip, endpoint.InstanceName, endpoint.IsRaop), StringComparer.Ordinal)
			select @group.First()).ToList();
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
		if (_disposed)
		{
			return;
		}
		if (IsConnected || IsConnecting)
		{
			Disconnect();
			return;
		}
		await HomePodConnectionNotice.ConnectIntentGate.WaitAsync().ConfigureAwait(continueOnCapturedContext: true);
		try
		{
			if (!_disposed && !IsPlaybackActive && Members.Count != 0 && await HomePodConnectionNotice.ConfirmAsync(CoreApp.GetRequiredService<HomeViewModel>().Devices.Where((DeviceViewModel device) => device.SupportsAirPlay2 == Members[0].SupportsAirPlay2 && (device.IsConnected || device.IsConnecting)).Concat(TransportMembers()).Distinct()
				.ToList()).ConfigureAwait(continueOnCapturedContext: true) && !_disposed && !IsPlaybackActive)
			{
				Connect();
			}
		}
		finally
		{
			HomePodConnectionNotice.ConnectIntentGate.Release();
		}
	}

	internal void Connect(bool preserveReceiverVolume = false)
	{
		if (_disposed)
		{
			return;
		}
		if (!AirPlayStreamer.TryAuthorizeNewConnection(out string reason))
		{
			if (!SilentStartupContext.SuppressUiDialogs)
			{
				AppDialogService.Instance.ShowErrorAsync(LocalizationManager.Instance.GetString("Release_StoreLicense", "Microsoft Store license"), reason);
			}
			return;
		}
		bool flag = Members.Count > 0 && Members.All((DeviceViewModel member) => !member.SupportsAirPlay2);
		IReadOnlyList<BackendEndpointSelection> readOnlyList2;
		if (!flag)
		{
			IReadOnlyList<BackendEndpointSelection> readOnlyList = Array.Empty<BackendEndpointSelection>();
			readOnlyList2 = readOnlyList;
		}
		else
		{
			readOnlyList2 = ExactClassicConnectTargets();
		}
		IReadOnlyList<BackendEndpointSelection> readOnlyList3 = readOnlyList2;
		IReadOnlyList<string> readOnlyList4 = ConnectTargets();
		if ((flag && readOnlyList3.Count == 0) || (!flag && readOnlyList4.Count == 0))
		{
			return;
		}
		if (!preserveReceiverVolume)
		{
			IMessengerExtensions.Send<VolumeChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, new VolumeChangedMessage(VolumeCoordination.ClampVolume(Volume), VolumeChangeOrigin.Group));
		}
		IReadOnlyList<DeviceViewModel> readOnlyList5 = TransportMembers();
		List<DeviceViewModel> list = new List<DeviceViewModel>();
		try
		{
			foreach (DeviceViewModel item in readOnlyList5)
			{
				if (item.PrepareForGroupConnect(Volume, preserveReceiverVolume))
				{
					list.Add(item);
				}
			}
			List<CalibrationDeviceIdentity> playbackProfile = readOnlyList5.Select((DeviceViewModel member) => new CalibrationDeviceIdentity(member.StableId, member.Name, member.Model, member.SupportsAirPlay2)).ToList();
			AirPlayStreamer.Instance.SetPlaybackProfile(playbackProfile);
			AirPlayStreamer.Instance.SetCalibrationLatency(null);
			if (flag && readOnlyList3.Count >= 2)
			{
				AirPlayStreamer.Instance.AddGroup(readOnlyList3);
				return;
			}
			if (flag)
			{
				BackendEndpointSelection backendEndpointSelection = readOnlyList3[0];
				AirPlayStreamer.Instance.AddDevice(backendEndpointSelection.Ip, backendEndpointSelection.InstanceName, backendEndpointSelection.IsRaop);
				return;
			}
			if (readOnlyList4.Count >= 2)
			{
				AirPlayStreamer.Instance.AddGroup(readOnlyList4);
				return;
			}
			if (readOnlyList5.Count == 1)
			{
				BackendEndpointSelection preferredBackendSelection = readOnlyList5[0].PreferredBackendSelection;
				if ((object)preferredBackendSelection != null)
				{
					AirPlayStreamer.Instance.AddDevice(preferredBackendSelection.Ip, preferredBackendSelection.InstanceName, preferredBackendSelection.IsRaop);
					return;
				}
			}
			AirPlayStreamer.Instance.AddDevice(readOnlyList4[0]);
		}
		catch (Exception ex)
		{
			string message = LocalizationManager.Instance.Format("Device_BackendStartFailed", "Failed to start AirPlay backend: {0}", ex.Message);
			foreach (DeviceViewModel item2 in list)
			{
				try
				{
					item2.RemoveThisDeviceFromBackend();
				}
				catch (Exception)
				{
				}
				item2.MarkGroupConnectFailed(message);
			}
			if (!SilentStartupContext.SuppressUiDialogs)
			{
				AppDialogService.Instance.ShowErrorAsync(LocalizationManager.Instance.GetString("Dialog_Error", "Error"), message);
			}
		}
		finally
		{
			RefreshAggregate();
		}
	}

	internal void Disconnect()
	{
		if (Members.Count > 0 && Members.All((DeviceViewModel member) => !member.SupportsAirPlay2))
		{
			foreach (BackendEndpointSelection item in ExactClassicConnectTargets())
			{
				AirPlayStreamer.Instance.RemoveDevice(item);
			}
		}
		else
		{
			AirPlayStreamer.Instance.RemoveDevices(ConnectTargets());
		}
		foreach (DeviceViewModel item2 in TransportMembers())
		{
			item2.MarkDisconnectedByGroup();
		}
		AirPlayStreamer.Instance.SetProfileLatency(null);
		AirPlayStreamer.Instance.SetCalibrationLatency(null);
		RefreshAggregate();
	}

	internal void ApplyVolumeFromMaster(int volume)
	{
		bool suppressVolumePropagation = _suppressVolumePropagation;
		_suppressVolumePropagation = true;
		try
		{
			Volume = VolumeCoordination.ClampVolume(volume);
		}
		finally
		{
			_suppressVolumePropagation = suppressVolumePropagation;
		}
	}

	internal void ReconcileVolume(int volume)
	{
		using (VolumeCoordination.BeginApply())
		{
			ApplyVolumeFromMaster(volume);
			foreach (DeviceViewModel item in TransportMembers())
			{
				item.ApplyVolumeFromGroup(VolumeCoordination.ClampVolume(volume));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnVolumeChanged(double value)
	{
		if (!_suppressVolumePropagation)
		{
			int volume = VolumeCoordination.ClampVolume(value);
			ReconcileVolume(volume);
			if (IsConnected)
			{
				IMessengerExtensions.Send<VolumeChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, new VolumeChangedMessage(volume, VolumeChangeOrigin.Group));
			}
		}
	}
}
