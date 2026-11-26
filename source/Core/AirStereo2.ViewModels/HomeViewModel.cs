using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AirStereo2.Messages;
using AirStereo2.Services;
using AirStereo2.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel.__Internals;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Makaretu.Dns;

namespace AirStereo2.ViewModels;

public class HomeViewModel : ObservableObject, IDisposable
{
	internal sealed record DiscoveredDeviceInfo(string DisplayName, string IPAddress, int Port, string Model, bool IsAirPlay, string GroupId, bool IsGroupLeader, string GroupPublicName, bool GroupContainsDiscoverableLeader, string ParentGroupId, bool ParentGroupContainsDiscoverableLeader, string TightSyncId, ulong StatusFlags, string StableId, bool SupportsAirPlay2 = false, string HomeKitPublicKey = "")
	{
		public string InstanceName { get; init; } = string.Empty;

		public bool HasAuthoritativeGrouping { get; init; }

		public DateTimeOffset? GroupingObservedAt { get; init; }

		public DateTimeOffset? GroupingExpiresAt { get; init; }

		public string GroupingEvidenceSource { get; init; } = string.Empty;

		public string GroupingEvidenceScope { get; init; } = string.Empty;

		public bool HasGroupLeader { get; init; }

		public bool HasGroupContainsDiscoverableLeader { get; init; }

		public bool HasParentGroupContainsDiscoverableLeader { get; init; }

		public int ChannelRole { get; init; }

		public bool AdvertisesPassword { get; init; }

		public int? AccessControlLevel { get; init; }

		public bool IsReceiverSessionActive => ReceiverStatusFlags.IsReceiverSessionActive(StatusFlags);

		public bool HasPersistentGroupMembership => !string.IsNullOrWhiteSpace(ParentGroupId);

		public bool HasAuthoritativeStereoPairMetadata => DeviceViewModel.HasHomePodStereoMetadata(Model, DisplayName, GroupId, GroupPublicName, TightSyncId);

		public string? StereoPairKey
		{
			get
			{
				if (!IsReceiverSessionActive || HasAuthoritativeStereoPairMetadata)
				{
					return DeviceViewModel.BuildStereoPairKey(Model, DisplayName, GroupId, ParentGroupId, TightSyncId);
				}
				return null;
			}
		}

		public string? AirPlayGroupKey => DeviceViewModel.BuildAirPlayGroupKey(GroupId, ParentGroupId, TightSyncId);

		public bool IsAppleTv => DeviceViewModel.LooksLikeAppleTv(Model, DisplayName);

		public bool IsHomePodFamily => DeviceViewModel.LooksLikeHomePodFamily(Model, DisplayName);

		public bool CanFormHomeTheaterGroup
		{
			get
			{
				if ((!IsReceiverSessionActive || HasPersistentGroupMembership || HasAuthoritativeStereoPairMetadata) && AirPlayGroupKey != null)
				{
					if (!IsAppleTv)
					{
						return IsHomePodFamily;
					}
					return true;
				}
				return false;
			}
		}
	}

	private const string AirPlayServiceType = "_airplay._tcp";

	private const string RaopServiceType = "_raop._tcp";

	[ObservableProperty]
	private ObservableCollection<DeviceViewModel> _devices = new ObservableCollection<DeviceViewModel>();

	private readonly object _discoveryProjectionLock = new object();

	private readonly Dictionary<string, DiscoveredDeviceInfo> _discoveryProjections = new Dictionary<string, DiscoveredDeviceInfo>(StringComparer.OrdinalIgnoreCase);

	private readonly MdnsDiscoveryCache _discoveryCache = new MdnsDiscoveryCache();

	private readonly Dictionary<string, MdnsDiscoveryCache> _nativeDiscoveryCaches = new Dictionary<string, MdnsDiscoveryCache>();

	private WindowsDnsSdDiscovery? _windowsDiscovery;

	private DispatcherTimer? _discoveryExpiryTimer;

	private MulticastService? _mdns;

	private ServiceDiscovery? _serviceDiscovery;

	private CancellationTokenSource? _directDiscoveryCts;

	private Task? _directDiscoveryTask;

	private readonly object _deviceHintLock = new object();

	private readonly Dictionary<string, string> _deviceHintFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private readonly bool _persistGroupSettings;

	private readonly HashSet<string> _dismissedAppleGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private bool _disposed;

	[ObservableProperty]
	private bool _isScanning;

	[ObservableProperty]
	private bool _showScanningState;

	[ObservableProperty]
	private bool _showEmptyState;

	private CancellationTokenSource? _scanTimerCts;

	private readonly ConcurrentQueue<DiscoveredDeviceInfo> _pendingDeviceUpserts = new ConcurrentQueue<DiscoveredDeviceInfo>();

	private int _upsertDrainScheduled;

	private static readonly string[] GroupingTxtKeys = new string[8] { "gid", "pgid", "tsid", "gpn", "igl", "gcgl", "pgcgl", "c" };

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openCalibrationCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? refreshCommand;

	public ObservableCollection<DeviceGroupViewModel> Groups { get; } = new ObservableCollection<DeviceGroupViewModel>();

	public ObservableCollection<object> DisplayItems { get; } = new ObservableCollection<object>();

	internal string DiscoveryDiagnostics
	{
		get
		{
			lock (_discoveryProjectionLock)
			{
				List<string> list = new List<string>
				{
					"Multicast/direct mDNS:",
					_discoveryCache.Diagnostics()
				};
				foreach (KeyValuePair<string, MdnsDiscoveryCache> nativeDiscoveryCache in _nativeDiscoveryCaches)
				{
					list.Add("Windows DNS-SD (" + nativeDiscoveryCache.Key + "):");
					list.Add(nativeDiscoveryCache.Value.Diagnostics());
				}
				list.Add("");
				list.Add("Discovery projections:");
				foreach (DiscoveredDeviceInfo value in _discoveryProjections.Values)
				{
					list.Add($"{value.InstanceName}: service={(value.IsAirPlay ? "AirPlay" : "RAOP")}; model={value.Model}; authoritative-grouping={value.IsAirPlay || value.HasAuthoritativeGrouping}; tsid={value.TightSyncId}; gpn={value.GroupPublicName}; airplay2={value.SupportsAirPlay2}");
				}
				if (Application.Current == null || ((DispatcherObject)Application.Current).Dispatcher.CheckAccess())
				{
					list.Add($"UI projection: physical-rows={Devices.Count}; groups={Groups.Count}; display-items={DisplayItems.Count}");
					foreach (DeviceViewModel item in Devices.Where((DeviceViewModel device) => device.IsHomePodFamily))
					{
						int count = FindNativeStereoPairMembers(Devices, item).Count;
						list.Add($"{item.Name}: airplay-record={item.HasAirPlayDiscoveryRecord}; raop-record={item.HasRaopDiscoveryRecord}; tsid={item.TightSyncId}; native-pair-members={count}; grouped={FindNativeStereoGroup(item) != null}; evidence-source={item.GroupingEvidenceSource}; observed={item.GroupingObservedAt:O}; expires={item.GroupingExpiresAt:O}; fresh={item.HasFreshGroupingEvidence(DateTimeOffset.UtcNow)}");
					}
				}
				return string.Join(Environment.NewLine, list);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public ObservableCollection<DeviceViewModel> Devices
	{
		get
		{
			return _devices;
		}
		[MemberNotNull("_devices")]
		set
		{
			if (!EqualityComparer<ObservableCollection<DeviceViewModel>>.Default.Equals(_devices, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.Devices);
				_devices = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.Devices);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsScanning
	{
		get
		{
			return _isScanning;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isScanning, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsScanning);
				_isScanning = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsScanning);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool ShowScanningState
	{
		get
		{
			return _showScanningState;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_showScanningState, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ShowScanningState);
				_showScanningState = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ShowScanningState);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool ShowEmptyState
	{
		get
		{
			return _showEmptyState;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_showEmptyState, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ShowEmptyState);
				_showEmptyState = value;
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ShowEmptyState);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenCalibrationCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = openCalibrationCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)OpenCalibration);
				RelayCommand val2 = val;
				openCalibrationCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand RefreshCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = refreshCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)Refresh);
				RelayCommand val2 = val;
				refreshCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	public HomeViewModel()
		: this(startDiscovery: true, persistGroupSettings: true)
	{
	}

	internal HomeViewModel(bool startDiscovery, bool persistGroupSettings = false)
	{
		_persistGroupSettings = persistGroupSettings;
		if (persistGroupSettings)
		{
			foreach (string dismissedAppleGroupKey in SettingsManager.GetDismissedAppleGroupKeys())
			{
				_dismissedAppleGroupKeys.Add(dismissedAppleGroupKey);
			}
		}
		if (UseDemoDevices())
		{
			LoadDemoDevices();
			IsScanning = false;
			ShowScanningState = false;
			ShowEmptyState = false;
		}
		else if (startDiscovery)
		{
			StartDiscovery();
		}
	}

	internal static bool ShouldRemoveOnRefresh(bool isConnected, bool isConnecting, bool belongsToGroup = false)
	{
		if (!isConnected && !isConnecting)
		{
			return !belongsToGroup;
		}
		return false;
	}

	[RelayCommand]
	private void OpenCalibration()
	{
		try
		{
			foreach (DeviceGroupViewModel item in Groups.Where((DeviceGroupViewModel g) => g.IsConnected || g.IsConnecting))
			{
				item.Disconnect();
			}
			foreach (DeviceViewModel item2 in Devices.Where((DeviceViewModel d) => d.IsConnected || d.IsConnecting))
			{
				item2.Disconnect();
			}
			AirPlayStreamer.Instance.RemoveAllDevices();
			OnboardingWindow requiredService = CoreApp.GetRequiredService<OnboardingWindow>();
			((Window)(object)requiredService).Topmost = false;
			((Window)(object)requiredService).Closed += delegate
			{
				Refresh();
			};
			((Window)(object)requiredService).Show();
			((Window)(object)requiredService).Activate();
		}
		catch (Exception ex)
		{
			LocalizationManager instance = LocalizationManager.Instance;
			AppDialogService.Instance.ShowErrorAsync(instance.GetString("Dialog_Error", "Error"), instance.Format("Calibration_OpenFailed", "The calibration window could not be opened: {0}", ex.Message)).ConfigureAwait(continueOnCapturedContext: true);
		}
	}

	[RelayCommand]
	private void Refresh()
	{
		if (UseDemoDevices())
		{
			LoadDemoDevices();
			return;
		}
		StopDiscovery();
		foreach (DeviceViewModel device in Devices.Where((DeviceViewModel d) => ShouldRemoveOnRefresh(d.IsConnected, d.IsConnecting, Groups.Any((DeviceGroupViewModel g) => g.ContainsDevice(d)))).ToList())
		{
			foreach (DeviceGroupViewModel item in Groups.Where((DeviceGroupViewModel g) => g.ContainsDevice(device)).ToList())
			{
				item.Members.Remove(device);
				OnGroupMembershipChanged(item);
			}
			device.Dispose();
			Devices.Remove(device);
		}
		UpdateEmptyState();
		RebuildDisplayItems();
		StartDiscovery();
	}

	private void StartDiscovery()
	{
		//IL_01b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bb: Expected O, but got Unknown
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cf: Expected O, but got Unknown
		//IL_00d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Expected O, but got Unknown
		try
		{
			WindowsDnsSdDiscovery windowsDiscovery = new WindowsDnsSdDiscovery();
			_windowsDiscovery = windowsDiscovery;
			windowsDiscovery.InterfaceMessageReceived += delegate(Message message, uint index)
			{
				ProcessWindowsDiscoveryMessage(windowsDiscovery, message, index);
			};
			windowsDiscovery.Failure += delegate
			{
			};
			windowsDiscovery.Start();
		}
		catch (Exception)
		{
			_windowsDiscovery?.Dispose();
			_windowsDiscovery = null;
		}
		try
		{
			MulticastService mdns = new MulticastService((Func<IEnumerable<NetworkInterface>, IEnumerable<NetworkInterface>>)SelectDiscoveryInterfaces)
			{
				UseIpv4 = true,
				UseIpv6 = false
			};
			ServiceDiscovery val = new ServiceDiscovery(mdns);
			_mdns = mdns;
			_serviceDiscovery = val;
			mdns.AnswerReceived += delegate(object? s, MessageEventArgs e)
			{
				//IL_0068: Unknown result type (might be due to invalid IL or missing references)
				if (_disposed || _mdns != mdns)
				{
					return;
				}
				HomeViewModel homeViewModel = this;
				Message message = e.Message;
				MulticastService expectedService = mdns;
				foreach (Question item in homeViewModel.ProcessDiscoveryMessage(message, default(CancellationToken), expectedService))
				{
					try
					{
						mdns.SendQuery(item.Name, (DnsClass)1, item.Type);
					}
					catch (Exception ex3) when (((ex3 is ObjectDisposedException || ex3 is SocketException || ex3 is IOException) ? 1 : 0) != 0)
					{
					}
				}
			};
			mdns.Start();
			val.QueryServiceInstances((DomainName)("_raop._tcp"));
			val.QueryServiceInstances((DomainName)("_airplay._tcp"));
			val.QueryUnicastServiceInstances((DomainName)("_raop._tcp"));
			val.QueryUnicastServiceInstances((DomainName)("_airplay._tcp"));
			_directDiscoveryCts = new CancellationTokenSource();
			_directDiscoveryTask = RunDirectDiscoveryAsync(_directDiscoveryCts.Token);
		}
		catch (Exception)
		{
			StopDiscovery(stopWindows: false);
		}
		StartScanTimer();
		if (Application.Current != null)
		{
			_discoveryExpiryTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.0), (DispatcherPriority)4, (EventHandler)delegate
			{
				InvalidateGroupingEvidence(DateTimeOffset.UtcNow);
			}, ((DispatcherObject)Application.Current).Dispatcher);
			_discoveryExpiryTimer.Start();
		}
	}

	private static bool UseDemoDevices()
	{
		return Environment.GetCommandLineArgs().Any((string arg) => string.Equals(arg, "--airstereo2-demo-devices", StringComparison.OrdinalIgnoreCase));
	}

	private void LoadDemoDevices()
	{
		Devices.Clear();
		Groups.Clear();
		DeviceViewModel deviceViewModel = CreateDemoDevice("Living Room HomePod (L)", "AudioAccessory5,1", "10.0.0.21", isConnected: true, 62, 42);
		DeviceViewModel deviceViewModel2 = CreateDemoDevice("Living Room HomePod (R)", "AudioAccessory5,1", "10.0.0.22", isConnected: true, 62, 42);
		DeviceGroupViewModel deviceGroupViewModel = NewGroup(isAppleSeeded: true);
		deviceGroupViewModel.SetMembers(new DeviceViewModel[2] { deviceViewModel, deviceViewModel2 });
		deviceGroupViewModel.SetExplicitName("Living Room HomePod Pair");
		deviceGroupViewModel.IsExpanded = true;
		Groups.Add(deviceGroupViewModel);
		Devices.Add(deviceViewModel);
		Devices.Add(deviceViewModel2);
		DeviceViewModel deviceViewModel3 = CreateDemoDevice("Studio Apple TV 4K", "AppleTV14,1", "10.0.0.31", isConnected: true, 48, 76);
		DeviceViewModel deviceViewModel4 = CreateDemoDevice("Studio HomePod mini", "AudioAccessory5,1", "10.0.0.32", isConnected: true, 48, 76);
		DeviceGroupViewModel deviceGroupViewModel2 = NewGroup(isAppleSeeded: true);
		deviceGroupViewModel2.SetMembers(new DeviceViewModel[2] { deviceViewModel3, deviceViewModel4 });
		deviceGroupViewModel2.SetExplicitName("Studio Home Theater");
		deviceGroupViewModel2.IsExpanded = false;
		Groups.Add(deviceGroupViewModel2);
		Devices.Add(deviceViewModel3);
		Devices.Add(deviceViewModel4);
		AddDemoDevice("Office HomePod", "AudioAccessory1,1", "10.0.0.68", isConnected: false, 50, -1);
		AddDemoDevice("Sonos Era 300", "SonosEra300", "10.0.0.44", isConnected: false, 55, -1);
		AddDemoDevice("Samsung HW-Q990D Soundbar", "SamsungQ990", "10.0.0.52", isConnected: false, 60, -1);
		AddDemoDevice("Mac Studio AirPlay", "MacStudio", "10.0.0.58", isConnected: false, 50, -1);
		AddDemoDevice("Beats Pill", "BeatsPill", "10.0.0.64", isConnected: false, 45, -1);
		AddDemoDevice("Bedroom AirPort Express", "AirPort10,115", "10.0.0.72", isConnected: false, 50, -1);
		RebuildDisplayItems();
	}

	private DeviceViewModel CreateDemoDevice(string name, string model, string ipAddress, bool isConnected, int volume, int latencyMs)
	{
		DeviceViewModel deviceViewModel = new DeviceViewModel();
		deviceViewModel.Name = name;
		deviceViewModel.Model = model;
		deviceViewModel.IPAddress = ipAddress;
		deviceViewModel.Port = 7000;
		deviceViewModel.StableId = "demo:" + ipAddress;
		deviceViewModel.SupportsAirPlay2 = !model.Contains("AirPlay1", StringComparison.OrdinalIgnoreCase) && !model.Contains("AirPort", StringComparison.OrdinalIgnoreCase);
		deviceViewModel.ApplyDemoState(isConnected, volume, latencyMs);
		return deviceViewModel;
	}

	private void AddDemoDevice(string name, string model, string ipAddress, bool isConnected, int volume, int latencyMs, bool isStereoPair = false, int pairMemberCount = 1)
	{
		DeviceViewModel deviceViewModel = new DeviceViewModel
		{
			Name = name,
			Model = model,
			IPAddress = ipAddress,
			Port = 7000,
			StableId = "demo:" + ipAddress,
			SupportsAirPlay2 = (!model.Contains("AirPlay1", StringComparison.OrdinalIgnoreCase) && !model.Contains("AirPort", StringComparison.OrdinalIgnoreCase)),
			IsStereoPair = isStereoPair,
			PairMemberCount = pairMemberCount
		};
		deviceViewModel.ApplyDemoState(isConnected, volume, latencyMs);
		Devices.Add(deviceViewModel);
	}

	private void StartScanTimer()
	{
		_scanTimerCts?.Cancel();
		_scanTimerCts?.Dispose();
		_scanTimerCts = new CancellationTokenSource();
		CancellationToken token = _scanTimerCts.Token;
		IsScanning = true;
		UpdateEmptyState();
		RunScanTimerAsync(token);
	}

	private async Task RunScanTimerAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(4000, token);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		Application current = Application.Current;
		Dispatcher val = ((current != null) ? ((DispatcherObject)current).Dispatcher : null);
		if (val == null)
		{
			return;
		}
		await val.InvokeAsync((Action)delegate
		{
			if (!token.IsCancellationRequested)
			{
				CancellationTokenSource? scanTimerCts = _scanTimerCts;
				if (scanTimerCts != null && !(scanTimerCts.Token != token))
				{
					IsScanning = false;
					UpdateEmptyState();
				}
			}
		});
	}

	private void UpdateEmptyState()
	{
		bool flag = Devices.Count > 0;
		ShowScanningState = IsScanning && !flag;
		ShowEmptyState = !IsScanning && !flag;
	}

	private async Task RunDirectDiscoveryAsync(CancellationToken cancellationToken)
	{
		try
		{
			await Task.WhenAll(from address in (from address in SelectDiscoveryInterfaces(NetworkInterface.GetAllNetworkInterfaces()).SelectMany((NetworkInterface networkInterface) => networkInterface.GetIPProperties().UnicastAddresses)
					select address.Address into address
					where address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(address)
					select address).Distinct().ToArray()
				select BrowseDirectlyOnAddressAsync(address, cancellationToken));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception)
		{
		}
	}

	private async Task BrowseDirectlyOnAddressAsync(IPAddress localAddress, CancellationToken cancellationToken)
	{
		using UdpClient client = new UdpClient(new IPEndPoint(localAddress, 0));
		client.Ttl = 255;
		client.MulticastLoopback = false;
		IPEndPoint destination = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
		byte[][] queries = new byte[2][]
		{
			BuildMdnsQuery((DomainName)("_raop._tcp.local"), (DnsType)12),
			BuildMdnsQuery((DomainName)("_airplay._tcp.local"), (DnsType)12)
		};
		while (!cancellationToken.IsCancellationRequested)
		{
			byte[][] array = queries;
			foreach (byte[] array2 in array)
			{
				await client.SendAsync(array2, destination, cancellationToken);
			}
			foreach (Question item in _discoveryCache.RefreshQueries(DateTimeOffset.UtcNow, $"direct:{localAddress}"))
			{
				await client.SendAsync(BuildMdnsQuery(item.Name, item.Type), destination, cancellationToken);
			}
			foreach (string item2 in _discoveryCache.HomePodProbeAddresses())
			{
				IPEndPoint receiver = new IPEndPoint(IPAddress.Parse(item2), 5353);
				array = queries;
				foreach (byte[] array3 in array)
				{
					await client.SendAsync(array3, receiver, cancellationToken);
				}
			}
			DateTime receiveUntil = DateTime.UtcNow.AddSeconds(3.0);
			while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < receiveUntil)
			{
				TimeSpan timeSpan = receiveUntil - DateTime.UtcNow;
				if (timeSpan <= TimeSpan.Zero)
				{
					break;
				}
				using CancellationTokenSource receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				receiveCts.CancelAfter(timeSpan);
				try
				{
					UdpReceiveResult result = await client.ReceiveAsync(receiveCts.Token);
					foreach (Question item3 in ProcessDirectDiscoveryResponse(result.Buffer, cancellationToken))
					{
						byte[] query = BuildMdnsQuery(item3.Name, item3.Type);
						await client.SendAsync(query, destination, cancellationToken);
						await client.SendAsync(query, new IPEndPoint(result.RemoteEndPoint.Address, 5353), cancellationToken);
					}
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					break;
				}
			}
			await Task.Delay(TimeSpan.FromSeconds(7.0), cancellationToken);
		}
	}

	private static byte[] BuildMdnsQuery(DomainName serviceName, DnsType type)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Expected O, but got Unknown
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Expected O, but got Unknown
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Expected O, but got Unknown
		Message val = new Message();
		val.Questions.Add(new Question
		{
			Name = serviceName,
			Type = type,
			Class = (DnsClass)32769
		});
		using MemoryStream memoryStream = new MemoryStream();
		((DnsObject)val).Write(new WireWriter((Stream)memoryStream));
		return memoryStream.ToArray();
	}

	private IReadOnlyList<Question> ProcessDirectDiscoveryResponse(byte[] payload, CancellationToken cancellationToken)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Expected O, but got Unknown
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Expected O, but got Unknown
		try
		{
			Message val = new Message();
			using MemoryStream memoryStream = new MemoryStream(payload, writable: false);
			((DnsObject)val).Read(new WireReader((Stream)memoryStream));
			return ProcessDiscoveryMessage(val, cancellationToken, null, publishHint: true, null, "direct");
		}
		catch (Exception)
		{
			return Array.Empty<Question>();
		}
	}

	private void ProcessWindowsDiscoveryMessage(WindowsDnsSdDiscovery source, Message message, uint interfaceIndex)
	{
		lock (_discoveryProjectionLock)
		{
			if (_disposed || source != _windowsDiscovery)
			{
				return;
			}
			if (interfaceIndex == 0 && message.Answers.OfType<PTRRecord>().Any((PTRRecord ptr) => ((ResourceRecord)ptr).TTL <= TimeSpan.Zero))
			{
				foreach (KeyValuePair<string, MdnsDiscoveryCache> nativeDiscoveryCache in _nativeDiscoveryCaches)
				{
					string key = nativeDiscoveryCache.Key;
					MdnsDiscoveryCache value = nativeDiscoveryCache.Value;
					ProcessDiscoveryMessage(message, default(CancellationToken), null, publishHint: true, null, key, value);
				}
				return;
			}
			if (message.Answers.OfType<TXTRecord>().Any())
			{
				string text = $"windows:{interfaceIndex}";
				if (!_nativeDiscoveryCaches.TryGetValue(text, out MdnsDiscoveryCache value2))
				{
					value2 = (_nativeDiscoveryCaches[text] = new MdnsDiscoveryCache());
				}
				string key = text;
				MdnsDiscoveryCache value = value2;
				ProcessDiscoveryMessage(message, default(CancellationToken), null, publishHint: true, null, key, value);
			}
		}
	}

	internal IReadOnlyList<Question> ProcessDiscoveryMessage(Message message, CancellationToken cancellationToken = default(CancellationToken), MulticastService? expectedService = null, bool publishHint = true, Action<DiscoveredDeviceInfo>? applyDevice = null, string queryScope = "multicast", MdnsDiscoveryCache? cache = null)
	{
		lock (_discoveryProjectionLock)
		{
			if (_disposed || cancellationToken.IsCancellationRequested || (expectedService != null && _mdns != expectedService))
			{
				return Array.Empty<Question>();
			}
			if (cache == null)
			{
				cache = _discoveryCache;
			}
			string projectionPrefix = ((cache == _discoveryCache) ? string.Empty : (queryScope + "|"));
			if (projectionPrefix.Length != 0)
			{
				_nativeDiscoveryCaches[queryScope] = cache;
			}
			MdnsDiscoveryCache.Result result = cache.Apply(message, DateTimeOffset.UtcNow, queryScope);
			HashSet<string> liveInstances = result.Services.Select((MdnsDiscoveryCache.ServiceRecord record) => projectionPrefix + record.FullName.TrimEnd('.')).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
			string[] array = _discoveryProjections.Keys.Where((string text) => ((projectionPrefix.Length == 0) ? (!text.StartsWith("windows:", StringComparison.Ordinal)) : text.StartsWith(projectionPrefix, StringComparison.Ordinal)) && !liveInstances.Contains(text)).ToArray();
			foreach (string key in array)
			{
				_discoveryProjections.Remove(key);
			}
			foreach (MdnsDiscoveryCache.ServiceRecord item in result.Services.OrderBy((MdnsDiscoveryCache.ServiceRecord record) => record.FullName.Contains("_raop._tcp", StringComparison.OrdinalIgnoreCase)))
			{
				DiscoveredDeviceInfo discoveredDeviceInfo = CreateDeviceInfo(item.FullName, ((ResourceRecord)item.Srv).Name.Labels[0].ToString(), item.Addresses, (IEnumerable<SRVRecord>)(object)new SRVRecord[1] { item.Srv }, (IEnumerable<TXTRecord>)(object)new TXTRecord[1] { item.Txt }, publishHint, cache);
				if (discoveredDeviceInfo != null)
				{
					discoveredDeviceInfo = discoveredDeviceInfo with
					{
						GroupingEvidenceScope = ((projectionPrefix.Length == 0) ? string.Empty : queryScope)
					};
				}
				string key2 = projectionPrefix + item.FullName.TrimEnd('.');
				if (discoveredDeviceInfo != null && (!_discoveryProjections.TryGetValue(key2, out DiscoveredDeviceInfo value) || value != discoveredDeviceInfo))
				{
					_discoveryProjections[key2] = discoveredDeviceInfo;
					(applyDevice ?? new Action<DiscoveredDeviceInfo>(EnqueueDeviceUpsert))(discoveredDeviceInfo);
				}
			}
			return result.Queries;
		}
	}

	private void EnqueueDeviceUpsert(DiscoveredDeviceInfo info)
	{
		_pendingDeviceUpserts.Enqueue(info);
		ScheduleUpsertDrain();
	}

	private void ScheduleUpsertDrain()
	{
		if (!OrderedDiscoveryDispatch.TrySchedule(ref _upsertDrainScheduled))
		{
			return;
		}
		Application current = Application.Current;
		Dispatcher val = ((current != null) ? ((DispatcherObject)current).Dispatcher : null);
		if (val == null)
		{
			OrderedDiscoveryDispatch.ClearScheduled(ref _upsertDrainScheduled);
			DiscoveredDeviceInfo result;
			while (_pendingDeviceUpserts.TryDequeue(out result))
			{
			}
			return;
		}
		try
		{
			val.BeginInvoke((DispatcherPriority)4, (Delegate)new Action(DrainPendingDeviceUpserts));
		}
		catch (Exception)
		{
			OrderedDiscoveryDispatch.ClearScheduled(ref _upsertDrainScheduled);
		}
	}

	private void DrainPendingDeviceUpserts()
	{
		try
		{
			DiscoveredDeviceInfo result;
			while (_pendingDeviceUpserts.TryDequeue(out result))
			{
				try
				{
					UpsertDevice(result);
				}
				catch (Exception)
				{
				}
			}
		}
		finally
		{
			OrderedDiscoveryDispatch.ClearScheduled(ref _upsertDrainScheduled);
			if (OrderedDiscoveryDispatch.ShouldRearmAfterDrain(_pendingDeviceUpserts.IsEmpty))
			{
				ScheduleUpsertDrain();
			}
		}
	}

	internal static bool DnsNamesEqual(string left, string right)
	{
		return string.Equals(left.TrimEnd('.'), right.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
	}

	private static IEnumerable<NetworkInterface> SelectDiscoveryInterfaces(IEnumerable<NetworkInterface> interfaces)
	{
		NetworkInterface[] array = interfaces.Where((NetworkInterface networkInterface) => networkInterface.OperationalStatus == OperationalStatus.Up && networkInterface.SupportsMulticast && networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback && networkInterface.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToArray();
		NetworkInterface[] array2 = array.Where(IsLanInterface).ToArray();
		if (array2.Length == 0)
		{
			return array;
		}
		return array2;
	}

	private static bool IsLanInterface(NetworkInterface networkInterface)
	{
		try
		{
			IPInterfaceProperties iPProperties = networkInterface.GetIPProperties();
			bool flag = iPProperties.UnicastAddresses.Any((UnicastIPAddressInformation address) => address.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(address.Address));
			bool flag2 = iPProperties.GatewayAddresses.Any((GatewayIPAddressInformation gateway) => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(gateway.Address));
			return flag && flag2;
		}
		catch (NetworkInformationException)
		{
			return false;
		}
	}

	private static bool IsPrivateIpv4(IPAddress address)
	{
		byte[] addressBytes = address.GetAddressBytes();
		if (addressBytes.Length == 4)
		{
			if (addressBytes[0] != 10)
			{
				if (addressBytes[0] == 172)
				{
					byte b = addressBytes[1];
					if (b >= 16 && b <= 31)
					{
						goto IL_0043;
					}
				}
				if (addressBytes[0] == 192)
				{
					return addressBytes[1] == 168;
				}
				return false;
			}
			goto IL_0043;
		}
		return false;
		IL_0043:
		return true;
	}

	internal void InvalidateGroupingEvidence(DateTimeOffset now, bool clearAll = false)
	{
		bool flag = false;
		foreach (DeviceViewModel device in Devices)
		{
			MdnsDiscoveryCache mdnsDiscoveryCache;
			lock (_discoveryProjectionLock)
			{
				mdnsDiscoveryCache = (string.IsNullOrEmpty(device.GroupingEvidenceScope) ? _discoveryCache : _nativeDiscoveryCaches.GetValueOrDefault(device.GroupingEvidenceScope));
			}
			bool flag2 = !string.IsNullOrWhiteSpace(device.GroupingEvidenceSource) && device.GroupingEvidenceSource.EndsWith("._airplay._tcp.local", StringComparison.OrdinalIgnoreCase) && mdnsDiscoveryCache?.AirPlayEvidence(device.GroupingEvidenceSource) == null;
			if (!clearAll && device.HasFreshGroupingEvidence(now) && !flag2)
			{
				continue;
			}
			if (!clearAll)
			{
				DiscoveredDeviceInfo discoveredDeviceInfo;
				lock (_discoveryProjectionLock)
				{
					discoveredDeviceInfo = (from info in _discoveryProjections.Values.Where(delegate(DiscoveredDeviceInfo info)
						{
							if (info.HasAuthoritativeGrouping && AirPlayDeviceIdentity.IdsEqual(info.StableId, device.StableId) && string.Equals(info.TightSyncId, device.TightSyncId, StringComparison.OrdinalIgnoreCase))
							{
								DateTimeOffset? groupingObservedAt2 = info.GroupingObservedAt;
								DateTimeOffset dateTimeOffset = now;
								if (groupingObservedAt2.HasValue && groupingObservedAt2.GetValueOrDefault() <= dateTimeOffset && info.GroupingExpiresAt > now)
								{
									return (string.IsNullOrEmpty(info.GroupingEvidenceScope) ? _discoveryCache : _nativeDiscoveryCaches.GetValueOrDefault(info.GroupingEvidenceScope))?.AirPlayEvidence(info.GroupingEvidenceSource) != null;
								}
							}
							return false;
						})
						orderby info.GroupingObservedAt descending
						select info).FirstOrDefault();
				}
				if (discoveredDeviceInfo != null)
				{
					DateTimeOffset? groupingObservedAt = discoveredDeviceInfo.GroupingObservedAt;
					if (groupingObservedAt.HasValue)
					{
						DateTimeOffset valueOrDefault = groupingObservedAt.GetValueOrDefault();
						groupingObservedAt = discoveredDeviceInfo.GroupingExpiresAt;
						if (groupingObservedAt.HasValue)
						{
							DateTimeOffset valueOrDefault2 = groupingObservedAt.GetValueOrDefault();
							device.SetGroupingEvidence(valueOrDefault, valueOrDefault2, discoveredDeviceInfo.GroupingEvidenceSource);
							device.GroupingEvidenceScope = discoveredDeviceInfo.GroupingEvidenceScope;
							continue;
						}
					}
				}
			}
			if (device.GroupingObservedAt.HasValue || !string.IsNullOrWhiteSpace(device.TightSyncId))
			{
				device.ClearGroupingEvidence();
				device.TightSyncId = string.Empty;
				device.GroupId = string.Empty;
				device.ParentGroupId = string.Empty;
				device.GroupPublicName = string.Empty;
				device.IsGroupLeader = false;
				device.GroupContainsDiscoverableLeader = false;
				device.ParentGroupContainsDiscoverableLeader = false;
				device.ChannelRole = 0;
				flag = true;
			}
		}
		if (flag)
		{
			ReconcileNativeStereoGroups();
			PersistGroups();
			RebuildDisplayItems();
		}
	}

	private void StopDiscovery(bool stopWindows = true)
	{
		if (stopWindows)
		{
			WindowsDnsSdDiscovery? windowsDiscovery = _windowsDiscovery;
			_windowsDiscovery = null;
			windowsDiscovery?.Dispose();
		}
		DispatcherTimer? discoveryExpiryTimer = _discoveryExpiryTimer;
		if (discoveryExpiryTimer != null)
		{
			discoveryExpiryTimer.Stop();
		}
		_discoveryExpiryTimer = null;
		InvalidateGroupingEvidence(DateTimeOffset.UtcNow, clearAll: true);
		_scanTimerCts?.Cancel();
		_scanTimerCts?.Dispose();
		_scanTimerCts = null;
		IsScanning = false;
		UpdateEmptyState();
		_directDiscoveryCts?.Cancel();
		_directDiscoveryCts?.Dispose();
		_directDiscoveryCts = null;
		_directDiscoveryTask = null;
		ServiceDiscovery? serviceDiscovery = _serviceDiscovery;
		if (serviceDiscovery != null)
		{
			serviceDiscovery.Dispose();
		}
		_serviceDiscovery = null;
		MulticastService? mdns = _mdns;
		if (mdns != null)
		{
			mdns.Stop();
		}
		MulticastService? mdns2 = _mdns;
		if (mdns2 != null)
		{
			mdns2.Dispose();
		}
		_mdns = null;
		lock (_discoveryProjectionLock)
		{
			_discoveryCache.Clear();
			_nativeDiscoveryCaches.Clear();
			_discoveryProjections.Clear();
			DiscoveredDeviceInfo result;
			while (_pendingDeviceUpserts.TryDequeue(out result))
			{
			}
		}
	}

	internal DiscoveredDeviceInfo? CreateDeviceInfo(string serviceText, string instanceName, IEnumerable<string> addresses, IEnumerable<SRVRecord> srvRecords, IEnumerable<TXTRecord> txtRecords, bool publishHint = true, MdnsDiscoveryCache? cache = null)
	{
		if (cache == null)
		{
			cache = _discoveryCache;
		}
		bool flag = serviceText.Contains("_airplay._tcp", StringComparison.OrdinalIgnoreCase);
		bool flag2 = serviceText.Contains("_raop._tcp", StringComparison.OrdinalIgnoreCase);
		if (!flag && !flag2)
		{
			return null;
		}
		string[] array = (from address in addresses.Where((string address) => !string.IsNullOrWhiteSpace(address)).Distinct<string>(StringComparer.OrdinalIgnoreCase)
			orderby IsLinkLocal(address)
			select address).ThenBy<string, string>((string address) => address, StringComparer.OrdinalIgnoreCase).ToArray();
		string text = array.FirstOrDefault();
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		SRVRecord? obj = srvRecords.FirstOrDefault();
		int port = ((int?)((obj != null) ? new ushort?(obj.Port) : ((ushort?)null))) ?? (flag ? 7000 : 5000);
		Dictionary<string, string> dictionary = ParseTxtRecords(txtRecords);
		if (!flag2 && string.IsNullOrWhiteSpace(GetTxt(dictionary, "deviceid")))
		{
			return null;
		}
		MdnsDiscoveryCache.TxtEvidence txtEvidence = (flag ? cache.AirPlayEvidence(serviceText) : cache.AirPlayCompanionEvidence(instanceName, GetTxt(dictionary, "pk")));
		Dictionary<string, string> dictionary2 = ((flag2 && txtEvidence != null) ? ParseTxtRecords((IEnumerable<TXTRecord>)(object)new TXTRecord[1] { txtEvidence.Txt }) : null);
		Dictionary<string, string> txtRecords2 = (flag2 ? MergeSiblingGrouping(dictionary, dictionary2, GetTxt(dictionary, "pk"), instanceName) : dictionary);
		bool flag3 = dictionary2 != null;
		Dictionary<string, string> dictionary3 = dictionary2 ?? dictionary;
		TXTRecord val = (TXTRecord)(flag ? ((object)txtRecords.FirstOrDefault()) : ((object)txtEvidence?.Txt));
		DateTimeOffset dateTimeOffset = txtEvidence?.Observed ?? DateTimeOffset.UtcNow;
		DateTimeOffset value = txtEvidence?.Expires ?? (dateTimeOffset + ((val != null) ? ((ResourceRecord)val).TTL : TimeSpan.Zero));
		BackendDeviceHint hint = new BackendDeviceHint(instanceName, array, port, flag2, txtRecords2);
		if (publishHint)
		{
			PublishDeviceHintIfChanged(hint);
		}
		string text2 = instanceName;
		if (flag2 && text2.Contains('@'))
		{
			text2 = text2.Split('@').Last();
		}
		string txt = GetTxt(dictionary3, "gpn");
		string displayName = text2;
		string txt2 = GetTxt(dictionary, flag ? "model" : "am");
		if (string.IsNullOrWhiteSpace(txt2))
		{
			txt2 = GetTxt(dictionary, flag ? "am" : "model");
		}
		string groupId = AirPlayDeviceIdentity.NormalizeGroupIdValue(GetTxt(dictionary3, "gid"));
		string parentGroupId = AirPlayDeviceIdentity.NormalizeGroupIdValue(GetTxt(dictionary3, "pgid"));
		string txt3 = GetTxt(dictionary3, "tsid");
		string text3 = (flag2 ? AirPlayDeviceIdentity.ExtractRaopDeviceId(instanceName) : string.Empty);
		string stableId = (string.IsNullOrWhiteSpace(text3) ? AirPlayDeviceIdentity.BuildStableIdFromTxt(dictionary, txt3, groupId, parentGroupId, txt2, displayName) : AirPlayDeviceIdentity.BuildStableId(text3, null, txt3, groupId, parentGroupId, txt2, displayName));
		return new DiscoveredDeviceInfo(displayName, text, port, txt2, flag, groupId, IsTruthy(GetTxt(dictionary3, "igl")), txt, IsTruthy(GetTxt(dictionary3, "gcgl")), parentGroupId, IsTruthy(GetTxt(dictionary3, "pgcgl")), txt3, (flag3 && dictionary2 != null) ? ParseFlags(PreferNonEmpty(GetTxt(dictionary2, "flags"), GetTxt(dictionary2, "sf"))) : ParseFlags(PreferNonEmpty(GetTxt(dictionary, "flags"), GetTxt(dictionary, "sf"))), stableId, (flag3 && dictionary2 != null) ? SupportsAirPlay2(dictionary2, isAirPlay: true) : SupportsAirPlay2(dictionary, flag), GetTxt(dictionary, "pk"))
		{
			InstanceName = instanceName,
			HasAuthoritativeGrouping = (flag || flag3),
			GroupingObservedAt = dateTimeOffset,
			GroupingExpiresAt = value,
			GroupingEvidenceSource = ((((object)txtEvidence != null) ? ((object)((ResourceRecord)txtEvidence.Txt).Name).ToString().TrimEnd('.') : null) ?? (flag ? serviceText.TrimEnd('.') : string.Empty)),
			HasGroupLeader = dictionary3.ContainsKey("igl"),
			HasGroupContainsDiscoverableLeader = dictionary3.ContainsKey("gcgl"),
			HasParentGroupContainsDiscoverableLeader = dictionary3.ContainsKey("pgcgl"),
			ChannelRole = ParseChannelRole(GetTxt(dictionary3, "c")),
			AdvertisesPassword = ReceiverAccessPolicy.ParsePasswordFlag((flag3 && dictionary2 != null) ? PreferNonEmpty(GetTxt(dictionary2, "pw"), GetTxt(dictionary, "pw")) : GetTxt(dictionary, "pw")),
			AccessControlLevel = ReceiverAccessPolicy.ParseAccessControlLevel((flag3 && dictionary2 != null) ? PreferNonEmpty(GetTxt(dictionary2, "acl"), GetTxt(dictionary, "acl")) : GetTxt(dictionary, "acl"))
		};
	}

	internal static Dictionary<string, string> MergeSiblingGrouping(IReadOnlyDictionary<string, string> txt, IReadOnlyDictionary<string, string>? sibling, string publicKey, string raopInstance = "")
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(txt, StringComparer.OrdinalIgnoreCase);
		if (sibling == null || !AirPlayDeviceIdentity.MatchesReceiverAdvertisement(AirPlayDeviceIdentity.ExtractRaopDeviceId(raopInstance), publicKey, GetTxt(sibling, "deviceid"), GetTxt(sibling, "pk")))
		{
			return dictionary;
		}
		string[] groupingTxtKeys = GroupingTxtKeys;
		foreach (string key in groupingTxtKeys)
		{
			if (!dictionary.ContainsKey(key) && sibling.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value))
			{
				dictionary[key] = value;
			}
		}
		return dictionary;
	}

	private void PublishDeviceHintIfChanged(BackendDeviceHint hint)
	{
		string key = $"{hint.IsRaop}:{hint.InstanceName}";
		string text = BuildDeviceHintFingerprint(hint);
		lock (_deviceHintLock)
		{
			if (_deviceHintFingerprints.TryGetValue(key, out string value) && string.Equals(value, text, StringComparison.Ordinal))
			{
				return;
			}
			_deviceHintFingerprints[key] = text;
		}
		try
		{
			AirPlayStreamer.Instance.PublishDeviceHint(hint);
		}
		catch (Exception)
		{
			lock (_deviceHintLock)
			{
				_deviceHintFingerprints.Remove(key);
			}
		}
	}

	internal static string BuildDeviceHintFingerprint(BackendDeviceHint hint)
	{
		string[] criticalKeys = new string[18]
		{
			"deviceid", "am", "model", "pk", "pi", "psi", "gid", "pgid", "tsid", "igl",
			"gcgl", "pgcgl", "c", "cn", "et", "md", "tp", "srcvers"
		};
		string value = string.Join("|", from pair in hint.TxtRecords.Where<KeyValuePair<string, string>>((KeyValuePair<string, string> pair) => criticalKeys.Contains<string>(pair.Key, StringComparer.OrdinalIgnoreCase)).OrderBy<KeyValuePair<string, string>, string>((KeyValuePair<string, string> pair) => pair.Key, StringComparer.OrdinalIgnoreCase)
			select pair.Key + "=" + pair.Value);
		IPAddress address2;
		string value2 = string.Join(",", hint.Addresses.Where((string address) => IPAddress.TryParse(address, out address2) && address2.AddressFamily == AddressFamily.InterNetwork).OrderBy<string, string>((string result) => result, StringComparer.OrdinalIgnoreCase));
		return $"{hint.Port}|{value2}|{value}";
	}

	private void UpsertDevice(DiscoveredDeviceInfo info)
	{
		var (deviceViewModel, flag) = UpsertDeviceCore(Devices, info);
		deviceViewModel.RefreshDefaultOutputFlag();
		RebindPersistedGroups();
		SeedAppleGroupIfLinked(deviceViewModel);
		if (flag)
		{
			CheckFirstDeviceDiscovered();
		}
		RebuildDisplayItems();
	}

	internal static (DeviceViewModel Row, bool Created) UpsertDeviceCore(IList<DeviceViewModel> devices, DiscoveredDeviceInfo info)
	{
		DeviceViewModel deviceViewModel = devices.FirstOrDefault((DeviceViewModel d) => ShouldFoldByStableId(d.StableId, info.StableId)) ?? devices.FirstOrDefault((DeviceViewModel d) => MatchesDiscoveryInstance(d, info)) ?? devices.FirstOrDefault((DeviceViewModel d) => IsComplementaryServiceRecord(d, info));
		if (deviceViewModel != null)
		{
			bool allowEndpointUpdate = !string.Equals(deviceViewModel.IPAddress, info.IPAddress, StringComparison.OrdinalIgnoreCase) && !deviceViewModel.IsConnected && !deviceViewModel.IsConnecting;
			ApplyDeviceInfo(deviceViewModel, info, allowEndpointUpdate);
			deviceViewModel.RegisterGroupMember(info.IPAddress, info.Model, info.DisplayName);
			return (Row: deviceViewModel, Created: false);
		}
		DeviceViewModel deviceViewModel2 = new DeviceViewModel();
		ApplyDeviceInfo(deviceViewModel2, info, allowEndpointUpdate: true);
		deviceViewModel2.RegisterGroupMember(info.IPAddress, info.Model, info.DisplayName);
		devices.Add(deviceViewModel2);
		return (Row: deviceViewModel2, Created: true);
	}

	internal static bool ShouldFoldByStableId(string? existingId, string? incomingId)
	{
		if (AirPlayDeviceIdentity.IsDeviceScoped(incomingId) && AirPlayDeviceIdentity.IsDeviceScoped(existingId))
		{
			return AirPlayDeviceIdentity.IdsEqual(existingId, incomingId);
		}
		return false;
	}

	private static bool MatchesDiscoveryInstance(DeviceViewModel device, DiscoveredDeviceInfo info)
	{
		if (string.IsNullOrWhiteSpace(info.InstanceName))
		{
			return false;
		}
		return string.Equals(info.IsAirPlay ? device.AirPlayInstanceName : device.RaopInstanceName, info.InstanceName, StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsComplementaryServiceRecord(DeviceViewModel device, DiscoveredDeviceInfo info)
	{
		bool num;
		if (!info.IsAirPlay)
		{
			if (!device.HasRaopDiscoveryRecord)
			{
				num = device.HasAirPlayDiscoveryRecord;
				goto IL_002c;
			}
		}
		else if (!device.HasAirPlayDiscoveryRecord)
		{
			num = device.HasRaopDiscoveryRecord;
			goto IL_002c;
		}
		goto IL_0042;
		IL_002c:
		if (num && string.Equals(device.IPAddress, info.IPAddress, StringComparison.OrdinalIgnoreCase))
		{
			if (!string.IsNullOrWhiteSpace(device.HomeKitPublicKey) && !string.IsNullOrWhiteSpace(info.HomeKitPublicKey))
			{
				return string.Equals(device.HomeKitPublicKey, info.HomeKitPublicKey, StringComparison.OrdinalIgnoreCase);
			}
			return string.Equals(NormalizeDiscoveryName(device.Name), NormalizeDiscoveryName(info.DisplayName), StringComparison.OrdinalIgnoreCase);
		}
		goto IL_0042;
		IL_0042:
		return false;
	}

	private static string NormalizeDiscoveryName(string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Trim();
		}
		return string.Empty;
	}

	internal DeviceGroupViewModel? FindNativeStereoGroup(DeviceViewModel device)
	{
		return Groups.FirstOrDefault((DeviceGroupViewModel group) => group.IsNativeStereoPair && group.ContainsDevice(device));
	}

	internal bool IsAwaitingGroupPartners(DeviceViewModel device)
	{
		bool inLiveGroup = Groups.Any((DeviceGroupViewModel group) => group.ContainsDevice(device));
		if (!StartupAutoConnectPolicy.AwaitsGroupMembers(device.StableId, inLiveGroup, (from record in SettingsManager.GetSenderGroups()
			where record.IsAppleSeeded
			select record).Select((Func<SenderGroupRecord, IReadOnlyCollection<string>>)((SenderGroupRecord record) => record.MemberStableIds)), (from d in Devices
			select d.StableId into id
			where !string.IsNullOrWhiteSpace(id)
			select id).ToList()))
		{
			return StartupAutoConnectPolicy.AwaitsAdvertisedPairPartner(device.IsHomePodFamily, device.TightSyncId, inLiveGroup, from other in Devices
				where other != device
				select other.TightSyncId);
		}
		return true;
	}

	internal DeviceGroupViewModel? FindAutoConnectGroup(DeviceViewModel device)
	{
		DeviceGroupViewModel deviceGroupViewModel = Groups.FirstOrDefault((DeviceGroupViewModel candidate) => candidate.ContainsDevice(device));
		if (deviceGroupViewModel == null)
		{
			return null;
		}
		if (!StartupAutoConnectPolicy.ConnectsWholeGroup(deviceGroupViewModel.IsNativeStereoPair, deviceGroupViewModel.MemberStableIds, deviceGroupViewModel.MemberStableIds.Count == deviceGroupViewModel.Members.Count && deviceGroupViewModel.TransportMembers().Count == deviceGroupViewModel.Members.Count, SettingsManager.IsDefaultOutput))
		{
			return null;
		}
		return deviceGroupViewModel;
	}

	public void OnDeviceConnectedTogether(DeviceViewModel device)
	{
		if (FindNativeStereoGroup(device) != null)
		{
			return;
		}
		List<DeviceViewModel> list = Devices.Where((DeviceViewModel d) => d != device && FindNativeStereoGroup(d) == null && d.SupportsAirPlay2 == device.SupportsAirPlay2 && (d.IsConnected || d.IsConnecting)).ToList();
		if (list.Count != 0)
		{
			List<DeviceViewModel> involved = list.Append(device).ToList();
			DeviceGroupViewModel deviceGroupViewModel = Groups.FirstOrDefault((DeviceGroupViewModel g) => involved.Any(g.ContainsDevice));
			IEnumerable<DeviceViewModel> enumerable = deviceGroupViewModel?.Members;
			List<DeviceViewModel> members = (enumerable ?? Enumerable.Empty<DeviceViewModel>()).Concat(involved).Distinct().ToList();
			if (deviceGroupViewModel == null)
			{
				deviceGroupViewModel = NewGroup(isAppleSeeded: false);
				BindGroupMembers(deviceGroupViewModel, members);
				Groups.Add(deviceGroupViewModel);
			}
			else
			{
				BindGroupMembers(deviceGroupViewModel, members);
			}
			deviceGroupViewModel.RefreshAggregate();
			PersistGroups();
			RebuildDisplayItems();
		}
	}

	internal void ApplyMasterVolume(int volume)
	{
		int volume2 = VolumeCoordination.ClampVolume(volume);
		using (VolumeCoordination.BeginApply())
		{
			foreach (DeviceGroupViewModel group in Groups)
			{
				group.ApplyVolumeFromMaster(volume2);
			}
			IMessengerExtensions.Send<VolumeChangedMessage>((IMessenger)(object)WeakReferenceMessenger.Default, new VolumeChangedMessage(volume2, VolumeChangeOrigin.Master));
		}
	}

	internal int CountConnectedLogicalDevices()
	{
		return Groups.Count((DeviceGroupViewModel group) => group.IsConnected) + Devices.Count((DeviceViewModel device) => device.IsConnected && !Groups.Any((DeviceGroupViewModel group) => group.ContainsDevice(device)));
	}

	internal bool IsGroupedVolumeSource(string? sourceDeviceIp)
	{
		if (!string.IsNullOrWhiteSpace(sourceDeviceIp))
		{
			return Groups.Any((DeviceGroupViewModel group) => group.Members.Any((DeviceViewModel member) => string.Equals(member.IPAddress, sourceDeviceIp, StringComparison.OrdinalIgnoreCase)));
		}
		return false;
	}

	internal IReadOnlyList<BackendEndpointSelection> ClassicGroupSelectionsIncluding(DeviceViewModel target)
	{
		return (from @group in (from device in Devices
				where !device.SupportsAirPlay2 && (device == target || device.IsConnected || device.IsConnecting)
				select device.PreferredBackendSelection).OfType<BackendEndpointSelection>().GroupBy<BackendEndpointSelection, string>((BackendEndpointSelection selection) => DeviceViewModel.BuildBackendEndpointEventId(selection.Ip, selection.InstanceName, selection.IsRaop), StringComparer.Ordinal)
			select @group.First()).ToList();
	}

	internal void SeedAppleGroupIfLinked(DeviceViewModel row)
	{
		ReconcileNativeStereoGroups();
		IReadOnlyList<DeviceViewModel> readOnlyList = FindNativeStereoPairMembers(Devices, row);
		if (readOnlyList.Count == 2)
		{
			SeedNativeStereoPair(readOnlyList);
			return;
		}
		IReadOnlyList<DeviceViewModel> all = FindAppleGroupMembers(Devices, row);
		if (all.Count < 2)
		{
			PersistGroups();
			RebuildDisplayItems();
			return;
		}
		string text = row.AirPlayGroupKey ?? string.Empty;
		if (!_dismissedAppleGroupKeys.Contains(text))
		{
			DeviceGroupViewModel deviceGroupViewModel = Groups.FirstOrDefault((DeviceGroupViewModel g) => all.Any(g.ContainsDevice));
			IEnumerable<DeviceViewModel> enumerable = deviceGroupViewModel?.Members;
			List<DeviceViewModel> members = (enumerable ?? Enumerable.Empty<DeviceViewModel>()).Concat(all).Distinct().ToList();
			if (deviceGroupViewModel == null)
			{
				deviceGroupViewModel = NewGroup(isAppleSeeded: true);
				deviceGroupViewModel.AppleGroupKey = text;
				BindGroupMembers(deviceGroupViewModel, members);
				Groups.Add(deviceGroupViewModel);
			}
			else
			{
				BindGroupMembers(deviceGroupViewModel, members);
			}
			string text2 = all.Select((DeviceViewModel d) => d.GroupPublicName).FirstOrDefault((string n) => !string.IsNullOrWhiteSpace(n));
			if (!string.IsNullOrWhiteSpace(text2))
			{
				deviceGroupViewModel.Name = text2;
			}
			PersistGroups();
			RebuildDisplayItems();
		}
	}

	internal static IReadOnlyList<DeviceViewModel> FindNativeStereoPairMembers(IEnumerable<DeviceViewModel> devices, DeviceViewModel row)
	{
		if (!row.IsHomePodFamily || string.IsNullOrWhiteSpace(row.TightSyncId))
		{
			return Array.Empty<DeviceViewModel>();
		}
		List<DeviceViewModel> list = devices.Where((DeviceViewModel device) => device.IsHomePodFamily && string.Equals(device.TightSyncId.Trim(), row.TightSyncId.Trim(), StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
		if (!HomePodStereoPolicy.IsNativeStereoPair(list))
		{
			return Array.Empty<DeviceViewModel>();
		}
		return list;
	}

	private void ReconcileNativeStereoGroups()
	{
		foreach (DeviceGroupViewModel item in Groups.Where((DeviceGroupViewModel group) => !string.IsNullOrEmpty(group.NativeStereoPairId)).ToList())
		{
			if (!item.IsNativeStereoPair || FindNativeStereoPairMembers(Devices, item.Members[0]).Count != 2)
			{
				Groups.Remove(item);
				item.Dispose();
			}
		}
	}

	private void SeedNativeStereoPair(IReadOnlyList<DeviceViewModel> members)
	{
		string text = members[0].TightSyncId.Trim();
		List<DeviceGroupViewModel> source = Groups.Where((DeviceGroupViewModel deviceGroupViewModel2) => members.Any(deviceGroupViewModel2.ContainsDevice)).ToList();
		DeviceGroupViewModel group = source.FirstOrDefault((DeviceGroupViewModel candidate) => candidate.Members.Count == 2 && candidate.Members.All(((IEnumerable<DeviceViewModel>)members).Contains<DeviceViewModel>));
		foreach (DeviceGroupViewModel item in source.Where((DeviceGroupViewModel candidate) => candidate != group))
		{
			List<DeviceViewModel> list = item.Members.Where((DeviceViewModel member) => !members.Contains(member)).ToList();
			Groups.Remove(item);
			item.Dispose();
			if (list.Count >= 2)
			{
				DeviceGroupViewModel deviceGroupViewModel = NewGroup(item.IsAppleSeeded);
				BindGroupMembers(deviceGroupViewModel, list);
				Groups.Add(deviceGroupViewModel);
			}
		}
		bool num = group == null;
		if (group == null)
		{
			group = NewGroup(isAppleSeeded: true);
		}
		group.IsAppleSeeded = true;
		group.AppleGroupKey = text;
		group.NativeStereoPairId = text;
		BindGroupMembers(group, members);
		string text2 = members.Select((DeviceViewModel member) => member.GroupPublicName).FirstOrDefault((string name) => !string.IsNullOrWhiteSpace(name));
		if (!string.IsNullOrWhiteSpace(text2))
		{
			group.Name = text2.Trim();
		}
		if (num)
		{
			Groups.Add(group);
		}
		_dismissedAppleGroupKeys.Remove(text);
		PersistGroups();
		RebuildDisplayItems();
	}

	internal static IReadOnlyList<DeviceViewModel> FindAppleGroupMembers(IEnumerable<DeviceViewModel> devices, DeviceViewModel row)
	{
		string key = row.AirPlayGroupKey;
		if (!row.CanSeedAppleGroup || string.IsNullOrWhiteSpace(key))
		{
			return Array.Empty<DeviceViewModel>();
		}
		List<DeviceViewModel> list = devices.Where((DeviceViewModel d) => d.CanSeedAppleGroup && !string.IsNullOrWhiteSpace(d.AirPlayGroupKey) && string.Equals(d.AirPlayGroupKey, key, StringComparison.OrdinalIgnoreCase) && FindNativeStereoPairMembers(devices, d).Count == 0).Distinct().ToList();
		if (!list.Contains(row))
		{
			list.Add(row);
		}
		bool flag = list.Any((DeviceViewModel d) => DeviceViewModel.LooksLikeAppleTv(d.Model, d.Name)) || HomePodStereoPolicy.IsNativeStereoPair(list);
		if (!(list.Count >= 2 && flag))
		{
			return Array.Empty<DeviceViewModel>();
		}
		return list;
	}

	private static void BindGroupMembers(DeviceGroupViewModel group, IEnumerable<DeviceViewModel> members)
	{
		List<DeviceViewModel> list = members.Distinct().ToList();
		bool num = list.Count != group.Members.Count || list.Any((DeviceViewModel member) => !group.ContainsDevice(member));
		group.SetMembers(list);
		if (num)
		{
			int volume = SettingsManager.ReadConfig((AppConfig config) => config.MasterVolume);
			group.ReconcileVolume(VolumeCoordination.ClampVolume(volume));
		}
	}

	private DeviceGroupViewModel NewGroup(bool isAppleSeeded)
	{
		DeviceGroupViewModel deviceGroupViewModel = new DeviceGroupViewModel(OnGroupMembershipChanged, OnGroupPlaybackStopped);
		deviceGroupViewModel.IsAppleSeeded = isAppleSeeded;
		deviceGroupViewModel.ApplyVolumeFromMaster(SettingsManager.ReadConfig((AppConfig config) => config.MasterVolume));
		return deviceGroupViewModel;
	}

	private void OnGroupPlaybackStopped(DeviceGroupViewModel group)
	{
		if (!group.IsAppleSeeded && Groups.Remove(group))
		{
			group.Dispose();
			PersistGroups();
			RebuildDisplayItems();
		}
	}

	private void OnGroupMembershipChanged(DeviceGroupViewModel group)
	{
		if (group.Members.Count <= 1 && Groups.Contains(group))
		{
			Groups.Remove(group);
			if (group.IsAppleSeeded && !string.IsNullOrWhiteSpace(group.AppleGroupKey))
			{
				_dismissedAppleGroupKeys.Add(group.AppleGroupKey);
				if (_persistGroupSettings)
				{
					SettingsManager.SaveDismissedAppleGroupKeys(_dismissedAppleGroupKeys);
				}
			}
			group.Dispose();
		}
		PersistGroups();
		RebuildDisplayItems();
	}

	private void PersistGroups()
	{
		if (_persistGroupSettings)
		{
			SettingsManager.SaveSenderGroups(from g in Groups
				where g.IsAppleSeeded
				select new SenderGroupRecord
				{
					Name = g.Name,
					IsAppleSeeded = g.IsAppleSeeded,
					MemberStableIds = g.MemberStableIds.ToList()
				});
		}
	}

	private void RebindPersistedGroups()
	{
		if (!_persistGroupSettings)
		{
			return;
		}
		foreach (SenderGroupRecord record in SettingsManager.GetSenderGroups())
		{
			if (!record.IsAppleSeeded || record.MemberStableIds.Count < 2)
			{
				continue;
			}
			List<DeviceViewModel> rows = Devices.Where((DeviceViewModel d) => record.MemberStableIds.Any((string id) => AirPlayDeviceIdentity.IdsEqual(id, d.StableId))).ToList();
			if (rows.Count >= 2 && !Groups.Any((DeviceGroupViewModel g) => rows.Any(g.ContainsDevice)) && (!rows.All((DeviceViewModel device) => device.IsHomePodFamily) || HomePodStereoPolicy.IsNativeStereoPair(rows)))
			{
				DeviceGroupViewModel deviceGroupViewModel = NewGroup(record.IsAppleSeeded);
				BindGroupMembers(deviceGroupViewModel, rows);
				if (!string.IsNullOrWhiteSpace(record.Name))
				{
					deviceGroupViewModel.Name = record.Name;
				}
				Groups.Add(deviceGroupViewModel);
			}
		}
	}

	internal static List<object> BuildDisplayItems(IEnumerable<DeviceGroupViewModel> groups, IEnumerable<DeviceViewModel> devices)
	{
		List<object> list = new List<object>();
		HashSet<DeviceViewModel> hashSet = new HashSet<DeviceViewModel>();
		foreach (DeviceGroupViewModel group in groups)
		{
			list.Add(group);
			foreach (DeviceViewModel member in group.Members)
			{
				hashSet.Add(member);
			}
		}
		foreach (DeviceViewModel device in devices)
		{
			if (!hashSet.Contains(device))
			{
				list.Add(device);
			}
		}
		return list;
	}

	private void RebuildDisplayItems()
	{
		List<object> desired = BuildDisplayItems(Groups, Devices);
		ReconcileDisplayItems(DisplayItems, desired);
		foreach (DeviceGroupViewModel group in Groups)
		{
			group.RefreshAggregate();
		}
	}

	internal static void ReconcileDisplayItems<T>(ObservableCollection<T> current, IReadOnlyList<T> desired) where T : class
	{
		for (int i = 0; i < desired.Count; i++)
		{
			T val = desired[i];
			if (i < current.Count && current[i] == val)
			{
				continue;
			}
			int num = -1;
			for (int j = i + 1; j < current.Count; j++)
			{
				if (current[j] == val)
				{
					num = j;
					break;
				}
			}
			if (num >= 0)
			{
				current.Move(num, i);
			}
			else
			{
				current.Insert(i, val);
			}
		}
		while (current.Count > desired.Count)
		{
			current.RemoveAt(current.Count - 1);
		}
	}

	private void CheckFirstDeviceDiscovered()
	{
		if (IsScanning && Devices.Count > 0)
		{
			_scanTimerCts?.Cancel();
			IsScanning = false;
		}
		UpdateEmptyState();
	}

	private static void ApplyDeviceInfo(DeviceViewModel target, DiscoveredDeviceInfo info, bool allowEndpointUpdate, bool allowDisplayNameUpdate = true, bool allowStableIdUpdate = true)
	{
		if (allowDisplayNameUpdate && (string.IsNullOrWhiteSpace(target.Name) || info.IsAirPlay))
		{
			target.Name = info.DisplayName;
		}
		if (allowEndpointUpdate)
		{
			target.IPAddress = info.IPAddress;
			target.Port = info.Port;
		}
		if (info.IsAirPlay)
		{
			target.HasAirPlayDiscoveryRecord = true;
			target.AirPlayInstanceName = info.InstanceName;
		}
		else
		{
			target.HasRaopDiscoveryRecord = true;
			target.RaopInstanceName = info.InstanceName;
		}
		if (ShouldReplaceModel(target.Model, target.Name, info))
		{
			target.Model = info.Model;
		}
		DateTimeOffset? groupingObservedAt = info.GroupingObservedAt;
		int num;
		if (groupingObservedAt.HasValue)
		{
			DateTimeOffset valueOrDefault = groupingObservedAt.GetValueOrDefault();
			groupingObservedAt = target.GroupingObservedAt;
			if (groupingObservedAt.HasValue)
			{
				DateTimeOffset valueOrDefault2 = groupingObservedAt.GetValueOrDefault();
				num = ((valueOrDefault < valueOrDefault2) ? 1 : 0);
				goto IL_00cb;
			}
		}
		num = 0;
		goto IL_00cb;
		IL_00cb:
		bool flag = (byte)num != 0;
		if ((info.IsAirPlay || info.HasAuthoritativeGrouping) && !flag)
		{
			target.GroupId = info.GroupId;
			target.IsGroupLeader = info.HasGroupLeader && info.IsGroupLeader;
			target.GroupPublicName = info.GroupPublicName;
			target.GroupContainsDiscoverableLeader = info.HasGroupContainsDiscoverableLeader && info.GroupContainsDiscoverableLeader;
			target.ParentGroupId = info.ParentGroupId;
			target.ParentGroupContainsDiscoverableLeader = info.HasParentGroupContainsDiscoverableLeader && info.ParentGroupContainsDiscoverableLeader;
			target.ChannelRole = info.ChannelRole;
			target.TightSyncId = info.TightSyncId;
			target.StatusFlags = info.StatusFlags;
			target.AdvertisesPassword = info.AdvertisesPassword;
			target.AccessControlLevel = info.AccessControlLevel;
			target.SupportsAirPlay2 = info.SupportsAirPlay2;
		}
		if (!string.IsNullOrWhiteSpace(info.HomeKitPublicKey))
		{
			target.HomeKitPublicKey = info.HomeKitPublicKey;
		}
		if (allowStableIdUpdate && !flag && !string.IsNullOrWhiteSpace(info.StableId) && (info.IsAirPlay || !target.HasAirPlayDiscoveryRecord))
		{
			target.StableId = info.StableId;
		}
		else if (string.IsNullOrWhiteSpace(target.StableId))
		{
			target.StableId = AirPlayDeviceIdentity.BuildStableId(null, null, target.TightSyncId, target.GroupId, target.ParentGroupId, target.Model, target.Name);
		}
		if (!info.HasAuthoritativeGrouping || flag)
		{
			return;
		}
		groupingObservedAt = info.GroupingObservedAt;
		if (!groupingObservedAt.HasValue)
		{
			return;
		}
		DateTimeOffset valueOrDefault3 = groupingObservedAt.GetValueOrDefault();
		groupingObservedAt = info.GroupingExpiresAt;
		if (groupingObservedAt.HasValue)
		{
			DateTimeOffset valueOrDefault4 = groupingObservedAt.GetValueOrDefault();
			if (!string.IsNullOrWhiteSpace(info.GroupingEvidenceSource))
			{
				target.SetGroupingEvidence(valueOrDefault3, valueOrDefault4, info.GroupingEvidenceSource);
				target.GroupingEvidenceScope = info.GroupingEvidenceScope;
			}
		}
	}

	private static bool ShouldReplaceModel(string currentModel, string currentName, DiscoveredDeviceInfo info)
	{
		if (string.IsNullOrWhiteSpace(info.Model))
		{
			return false;
		}
		if (string.IsNullOrWhiteSpace(currentModel))
		{
			return true;
		}
		if (DeviceViewModel.LooksLikeHomePodMini(info.Model, info.DisplayName))
		{
			return !DeviceViewModel.LooksLikeHomePodMini(currentModel, currentName);
		}
		return false;
	}

	private static Dictionary<string, string> ParseTxtRecords(IEnumerable<TXTRecord> records)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (TXTRecord record in records)
		{
			foreach (string @string in record.Strings)
			{
				int num = @string.IndexOf('=');
				if (num > 0)
				{
					string text = @string.Substring(0, num).Trim();
					string value = @string.Substring(num + 1).Trim();
					if (!string.IsNullOrWhiteSpace(text))
					{
						dictionary[text] = value;
					}
				}
			}
		}
		return dictionary;
	}

	private static string GetTxt(IReadOnlyDictionary<string, string> values, string key)
	{
		if (!values.TryGetValue(key, out string value))
		{
			return string.Empty;
		}
		return value;
	}

	private static bool IsTruthy(string value)
	{
		if (!string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	internal static int ParseChannelRole(string value)
	{
		if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && (result == 1 || result == 2))
		{
			return result;
		}
		return 0;
	}

	private static bool IsLinkLocal(string value)
	{
		if (!IPAddress.TryParse(value, out IPAddress address))
		{
			return false;
		}
		if (address.AddressFamily == AddressFamily.InterNetworkV6)
		{
			return address.IsIPv6LinkLocal;
		}
		byte[] addressBytes = address.GetAddressBytes();
		if (addressBytes.Length == 4 && addressBytes[0] == 169)
		{
			return addressBytes[1] == 254;
		}
		return false;
	}

	private static ulong ParseFlags(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return 0uL;
		}
		string text = value.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			if (!ulong.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result))
			{
				return 0uL;
			}
			return result;
		}
		if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2))
		{
			return 0uL;
		}
		return result2;
	}

	internal static bool SupportsAirPlay2(IReadOnlyDictionary<string, string> txt, bool isAirPlay)
	{
		if (!isAirPlay)
		{
			return false;
		}
		if ((ParseFeatureFlags(PreferNonEmpty(GetTxt(txt, "features"), GetTxt(txt, "ft"))) & 0x10000000000L) != 0 && Version.TryParse(GetTxt(txt, "srcvers"), out Version result))
		{
			return result >= new Version(354, 54, 6);
		}
		return false;
	}

	internal static ulong ParseFeatureFlags(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return 0uL;
		}
		string[] array = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (array.Length == 2 && TryParseUnsigned(array[0], out var result) && TryParseUnsigned(array[1], out var result2))
		{
			return (result & 0xFFFFFFFFu) | ((result2 & 0xFFFFFFFFu) << 32);
		}
		if (array.Length != 1 || !TryParseUnsigned(array[0], out var result3))
		{
			return 0uL;
		}
		return result3;
	}

	private static bool TryParseUnsigned(string value, out ulong result)
	{
		string text = value.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			return ulong.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
		}
		return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
	}

	private static string PreferNonEmpty(string preferred, string fallback)
	{
		if (!string.IsNullOrWhiteSpace(preferred))
		{
			return preferred;
		}
		return fallback;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		StopDiscovery();
		foreach (DeviceGroupViewModel group in Groups)
		{
			group.Dispose();
		}
		foreach (DeviceViewModel device in Devices)
		{
			device.Dispose();
		}
	}
}
