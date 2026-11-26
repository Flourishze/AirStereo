using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using Makaretu.Dns;

namespace AirStereo2.Services;

public sealed class WindowsDnsSdDiscovery : IDisposable
{
	[UnmanagedFunctionPointer(CallingConvention.Winapi)]
	internal delegate void NativeCallback(uint status, nint context, nint result);

	internal struct NativeRequest
	{
		internal uint Version;

		internal uint InterfaceIndex;

		internal nint QueryName;

		internal nint Callback;

		internal nint Context;
	}

	internal struct NativeCancel
	{
		internal nint Reserved;
	}

	internal struct NativeInstance
	{
		internal nint InstanceName;

		internal nint HostName;

		internal nint Ipv4;

		internal nint Ipv6;

		internal ushort Port;

		internal ushort Priority;

		internal ushort Weight;

		internal uint PropertyCount;

		internal nint Keys;

		internal nint Values;

		internal uint InterfaceIndex;
	}

	internal struct NativeRecordHeader
	{
		internal nint Next;

		internal nint Name;

		internal ushort Type;

		internal ushort DataLength;

		internal uint Flags;

		internal uint Ttl;

		internal uint Reserved;
	}

	internal struct NativeSrv
	{
		internal nint Target;

		internal ushort Priority;

		internal ushort Weight;

		internal ushort Port;

		internal ushort Pad;
	}

	internal interface INativeApi
	{
		uint Start(bool browse, nint request, nint cancel);

		uint Cancel(bool browse, nint cancel);

		void FreeRecords(nint records);

		void FreeInstance(nint instance);
	}

	private sealed class Operation
	{
		private readonly object _gate = new object();

		internal readonly WindowsDnsSdDiscovery Owner;

		internal readonly INativeApi Native;

		internal readonly string Name;

		internal readonly bool Browse;

		private readonly nint _token;

		private nint _request;

		private nint _cancel;

		private nint _name;

		private bool _starting = true;

		private bool _cancelling;

		private bool _cancelWanted;

		private bool _terminal;

		private bool _released;

		internal Operation(WindowsDnsSdDiscovery owner, INativeApi native, string name, bool browse)
		{
			Owner = owner;
			Native = native;
			Name = name;
			Browse = browse;
			_token = new IntPtr(Interlocked.Increment(ref _nextToken));
			try
			{
				_name = Marshal.StringToHGlobalUni(name);
				_request = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRequest>());
				_cancel = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCancel>());
				Marshal.StructureToPtr(default(NativeCancel), _cancel, fDeleteOld: false);
				Marshal.StructureToPtr(new NativeRequest
				{
					Version = 1u,
					QueryName = _name,
					Callback = Marshal.GetFunctionPointerForDelegate(browse ? BrowseRoot : ResolveRoot),
					Context = _token
				}, _request, fDeleteOld: false);
				Registry[_token] = this;
			}
			catch
			{
				FreeMemory();
				throw;
			}
		}

		internal void Start()
		{
			lock (_gate)
			{
				if (_cancelWanted)
				{
					_starting = false;
					_terminal = true;
				}
			}
			ReleaseIfSafe();
			lock (_gate)
			{
				if (_released)
				{
					return;
				}
			}
			uint num = 0u;
			try
			{
				num = Native.Start(Browse, _request, _cancel);
			}
			catch (Exception ex)
			{
				num = uint.MaxValue;
				Owner.ReportFailure("DNS-SD start " + Name + ": " + ex.Message);
			}
			finally
			{
				lock (_gate)
				{
					_starting = false;
					if (num != 9506)
					{
						_terminal = true;
					}
				}
				ReleaseIfSafe();
			}
			if (num != 9506 && num != uint.MaxValue)
			{
				Owner.ReportFailure($"DNS-SD start {Name}: {num}");
			}
			CancelIfRequested();
		}

		internal void Cancel()
		{
			lock (_gate)
			{
				_cancelWanted = true;
			}
			CancelIfRequested();
		}

		private void CancelIfRequested()
		{
			lock (_gate)
			{
				if (!_cancelWanted || _starting || _cancelling || _terminal || _released)
				{
					return;
				}
				_cancelling = true;
			}
			uint num = uint.MaxValue;
			try
			{
				num = Native.Cancel(Browse, _cancel);
			}
			catch (Exception ex)
			{
				Owner.ReportFailure("DNS-SD cancel " + Name + ": " + ex.Message);
			}
			finally
			{
				lock (_gate)
				{
					_cancelling = false;
					if (num == 0)
					{
						_terminal = true;
					}
				}
				ReleaseIfSafe();
			}
			if (num != 0)
			{
				Owner.ReportFailure($"DNS-SD cancel {Name}: {num}; request retained until completion");
			}
		}

		internal void Complete()
		{
			lock (_gate)
			{
				_terminal = true;
			}
			ReleaseIfSafe();
		}

		private void ReleaseIfSafe()
		{
			lock (_gate)
			{
				if (!_terminal || _starting || _cancelling || _released)
				{
					return;
				}
				_released = true;
				Registry.TryRemove(_token, out Operation _);
				FreeMemory();
			}
			Owner.Retire(this);
		}

		private void FreeMemory()
		{
			if (_request != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(_request);
			}
			if (_cancel != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(_cancel);
			}
			if (_name != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(_name);
			}
			_request = (_cancel = (_name = IntPtr.Zero));
		}
	}

	private sealed class WindowsNativeApi : INativeApi
	{
		internal static readonly WindowsNativeApi Instance = new WindowsNativeApi();

		public uint Start(bool browse, nint request, nint cancel)
		{
			if (!browse)
			{
				return DnsServiceResolve(request, cancel);
			}
			return DnsServiceBrowse(request, cancel);
		}

		public uint Cancel(bool browse, nint cancel)
		{
			if (!browse)
			{
				return DnsServiceResolveCancel(cancel);
			}
			return DnsServiceBrowseCancel(cancel);
		}

		public void FreeRecords(nint records)
		{
			DnsRecordListFree(records, 1);
		}

		public void FreeInstance(nint instance)
		{
			DnsServiceFreeInstance(instance);
		}

		[DllImport("dnsapi.dll", ExactSpelling = true)]
		private static extern uint DnsServiceBrowse(nint request, nint cancel);

		[DllImport("dnsapi.dll", ExactSpelling = true)]
		private static extern uint DnsServiceResolve(nint request, nint cancel);

		[DllImport("dnsapi.dll", ExactSpelling = true)]
		private static extern uint DnsServiceBrowseCancel(nint cancel);

		[DllImport("dnsapi.dll", ExactSpelling = true)]
		private static extern uint DnsServiceResolveCancel(nint cancel);

		[DllImport("dnsapi.dll", ExactSpelling = true)]
		private static extern void DnsRecordListFree(nint records, int freeType);

		[DllImport("dnsapi.dll", ExactSpelling = true)]
		private static extern void DnsServiceFreeInstance(nint instance);
	}

	internal const uint RequestPending = 9506u;

	internal const uint Cancelled = 1223u;

	internal static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(60.0);

	internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(20.0);

	private static readonly string[] ServiceTypes = new string[2] { "_airplay._tcp.local", "_raop._tcp.local" };

	private static readonly ConcurrentDictionary<nint, Operation> Registry = new ConcurrentDictionary<nint, Operation>();

	private static long _nextToken;

	private static readonly NativeCallback BrowseRoot = BrowseCallback;

	private static readonly NativeCallback ResolveRoot = ResolveCallback;

	private readonly object _gate = new object();

	private readonly INativeApi _native;

	private readonly Func<DateTimeOffset> _now;

	private readonly bool _automaticRefresh;

	private readonly HashSet<Operation> _operations = new HashSet<Operation>();

	private readonly Dictionary<string, DateTimeOffset> _known = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, Operation> _resolves = new Dictionary<string, Operation>(StringComparer.OrdinalIgnoreCase);

	private Timer? _timer;

	private bool _started;

	private bool _disposed;

	public event Action<Message>? MessageReceived;

	public event Action<Message, uint>? InterfaceMessageReceived;

	public event Action<string>? Failure;

	internal static bool IsRequestRegistered(nint token)
	{
		return Registry.ContainsKey(token);
	}

	public WindowsDnsSdDiscovery()
		: this(WindowsNativeApi.Instance, () => DateTimeOffset.UtcNow, automaticRefresh: true)
	{
	}

	internal WindowsDnsSdDiscovery(INativeApi native, Func<DateTimeOffset> now, bool automaticRefresh = false)
	{
		_native = native;
		_now = now;
		_automaticRefresh = automaticRefresh;
	}

	public void Start()
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_started)
			{
				return;
			}
			_started = true;
		}
		if (_native == WindowsNativeApi.Instance && (!OperatingSystem.IsWindows() || IntPtr.Size != 8))
		{
			ReportFailure("Windows DNS-SD requires Windows 10 or later, x64.");
			return;
		}
		string[] serviceTypes = ServiceTypes;
		foreach (string name in serviceTypes)
		{
			Operation operation;
			lock (_gate)
			{
				operation = (_disposed ? null : CreateOperation(name, browse: true));
			}
			operation?.Start();
		}
		lock (_gate)
		{
			if (!_disposed && _automaticRefresh)
			{
				_timer = new Timer(delegate
				{
					Refresh();
				}, null, RefreshInterval, RefreshInterval);
			}
		}
	}

	internal void Refresh()
	{
		Operation[] array2;
		lock (_gate)
		{
			if (_disposed || !_started)
			{
				return;
			}
			DateTimeOffset now = _now();
			string[] array = (from pair in _known
				where pair.Value <= now
				select pair.Key).ToArray();
			foreach (string key in array)
			{
				_known.Remove(key);
			}
			array2 = (from name in _known.Keys
				where !_resolves.ContainsKey(name)
				select CreateOperation(name, browse: false)).ToArray();
		}
		Operation[] array3 = array2;
		for (int num = 0; num < array3.Length; num++)
		{
			array3[num].Start();
		}
	}

	private Operation CreateOperation(string name, bool browse)
	{
		Operation operation = new Operation(this, _native, name, browse);
		_operations.Add(operation);
		if (!browse)
		{
			_resolves[name] = operation;
		}
		return operation;
	}

	private void Receive(Operation operation, uint status, nint result)
	{
		switch (status)
		{
		default:
			ReportFailure($"DNS-SD {(operation.Browse ? "browse" : "resolve")} {operation.Name}: {status}");
			break;
		case 1223u:
			break;
		case 0u:
		{
			lock (_gate)
			{
				if (_disposed || (!operation.Browse && (!_known.ContainsKey(operation.Name) || !_resolves.TryGetValue(operation.Name, out Operation value) || operation != value)))
				{
					break;
				}
			}
			Message val = (operation.Browse ? ConvertRecords(result) : ConvertInstance(result));
			uint interfaceIndex = ((!operation.Browse) ? ReadInterfaceIndex(result) : 0u);
			List<Operation> list = new List<Operation>();
			List<Operation> list2 = new List<Operation>();
			lock (_gate)
			{
				if (_disposed || (!operation.Browse && (!_known.ContainsKey(operation.Name) || !_resolves.TryGetValue(operation.Name, out Operation value2) || operation != value2)))
				{
					break;
				}
				foreach (PTRRecord item in val.Answers.OfType<PTRRecord>())
				{
					string text = NativeQueryName(item.DomainName);
					if (ServiceType(text) == null)
					{
						continue;
					}
					if (((ResourceRecord)item).TTL <= TimeSpan.Zero)
					{
						_known.Remove(text);
						if (_resolves.Remove(text, out Operation value3))
						{
							list2.Add(value3);
						}
					}
					else
					{
						_known[text] = _now() + SnapshotTtl;
						if (operation.Browse && !_resolves.ContainsKey(text))
						{
							list.Add(CreateOperation(text, browse: false));
						}
					}
				}
				Publish(val, interfaceIndex);
			}
			foreach (Operation item2 in list2)
			{
				item2.Cancel();
			}
			{
				foreach (Operation item3 in list)
				{
					item3.Start();
				}
				break;
			}
		}
		}
	}

	private void Publish(Message message, uint interfaceIndex)
	{
		if (_disposed || message.Answers.Count == 0)
		{
			return;
		}
		Delegate[] array = this.MessageReceived?.GetInvocationList() ?? Array.Empty<Delegate>();
		foreach (Delegate obj in array)
		{
			if (_disposed)
			{
				break;
			}
			try
			{
				((Action<Message>)obj)(message);
			}
			catch (Exception ex)
			{
				ReportFailure("DNS-SD subscriber: " + ex.Message);
			}
		}
		array = this.InterfaceMessageReceived?.GetInvocationList() ?? Array.Empty<Delegate>();
		foreach (Delegate obj2 in array)
		{
			if (!_disposed)
			{
				try
				{
					((Action<Message, uint>)obj2)(message, interfaceIndex);
				}
				catch (Exception ex2)
				{
					ReportFailure("DNS-SD subscriber: " + ex2.Message);
				}
				continue;
			}
			break;
		}
	}

	private void ReportFailure(string error)
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			Delegate[] array = this.Failure?.GetInvocationList() ?? Array.Empty<Delegate>();
			foreach (Delegate obj in array)
			{
				if (_disposed)
				{
					break;
				}
				try
				{
					((Action<string>)obj)(error);
				}
				catch
				{
				}
			}
		}
	}

	private void Retire(Operation operation)
	{
		lock (_gate)
		{
			_operations.Remove(operation);
			if (_resolves.TryGetValue(operation.Name, out Operation value) && value == operation)
			{
				_resolves.Remove(operation.Name);
			}
		}
	}

	public void Dispose()
	{
		Operation[] array;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			_timer?.Dispose();
			_timer = null;
			_known.Clear();
			array = _operations.ToArray();
			this.MessageReceived = null;
			this.InterfaceMessageReceived = null;
			this.Failure = null;
		}
		Operation[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			array2[i].Cancel();
		}
	}

	private static void BrowseCallback(uint status, nint context, nint result)
	{
		Dispatch(browse: true, status, context, result);
	}

	private static void ResolveCallback(uint status, nint context, nint result)
	{
		Dispatch(browse: false, status, context, result);
	}

	private static void Dispatch(bool browse, uint status, nint context, nint result)
	{
		Registry.TryGetValue(context, out Operation value);
		INativeApi nativeApi = value?.Native ?? WindowsNativeApi.Instance;
		try
		{
			value?.Owner.Receive(value, status, result);
		}
		catch (Exception ex)
		{
			value?.Owner.ReportFailure("DNS-SD callback: " + ex.Message);
		}
		finally
		{
			if (result != IntPtr.Zero)
			{
				if (browse)
				{
					nativeApi.FreeRecords(result);
				}
				else
				{
					nativeApi.FreeInstance(result);
				}
			}
			if (!browse || status == 1223)
			{
				value?.Complete();
			}
		}
	}

	private static uint ReadInterfaceIndex(nint pointer)
	{
		if (pointer != IntPtr.Zero)
		{
			return Marshal.PtrToStructure<NativeInstance>(pointer).InterfaceIndex;
		}
		return 0u;
	}

	internal static Message ConvertInstance(nint pointer)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Expected O, but got Unknown
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Expected O, but got Unknown
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0109: Expected O, but got Unknown
		//IL_01a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cf: Expected O, but got Unknown
		//IL_020b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0210: Unknown result type (might be due to invalid IL or missing references)
		//IL_021c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0224: Unknown result type (might be due to invalid IL or missing references)
		//IL_0234: Expected O, but got Unknown
		Message val = new Message
		{
			QR = true
		};
		if (pointer == IntPtr.Zero)
		{
			return val;
		}
		NativeInstance nativeInstance = Marshal.PtrToStructure<NativeInstance>(pointer);
		string text = Marshal.PtrToStringUni(nativeInstance.InstanceName) ?? "";
		string text2 = Marshal.PtrToStringUni(nativeInstance.HostName) ?? "";
		string text3 = ServiceType(text);
		if (text3 == null || string.IsNullOrWhiteSpace(text2) || nativeInstance.Port == 0)
		{
			return val;
		}
		if (nativeInstance.PropertyCount > 4096)
		{
			throw new InvalidDataException("DNS-SD property count is invalid.");
		}
		val.Answers.Add((ResourceRecord)new PTRRecord
		{
			Name = (DomainName)(text3),
			DomainName = (DomainName)(text),
			TTL = SnapshotTtl
		});
		val.Answers.Add((ResourceRecord)new SRVRecord
		{
			Name = (DomainName)(text),
			Target = (DomainName)(text2),
			Port = nativeInstance.Port,
			Priority = nativeInstance.Priority,
			Weight = nativeInstance.Weight,
			TTL = SnapshotTtl
		});
		List<string> list = new List<string>();
		for (int i = 0; i < nativeInstance.PropertyCount; i++)
		{
			if (nativeInstance.Keys == IntPtr.Zero)
			{
				break;
			}
			checked
			{
				string text4 = Marshal.PtrToStringUni(Marshal.ReadIntPtr(nativeInstance.Keys, i * IntPtr.Size));
				if (text4 != null)
				{
					string text5 = Marshal.PtrToStringUni((nativeInstance.Values == IntPtr.Zero) ? IntPtr.Zero : Marshal.ReadIntPtr(nativeInstance.Values, i * IntPtr.Size));
					list.Add((text5 == null) ? text4 : (text4 + "=" + text5));
				}
			}
		}
		val.Answers.Add((ResourceRecord)new TXTRecord
		{
			Name = (DomainName)(text),
			Strings = list,
			TTL = SnapshotTtl
		});
		if (nativeInstance.Ipv4 != IntPtr.Zero)
		{
			IPAddress iPAddress = ReadIpv4(nativeInstance.Ipv4);
			if (!iPAddress.Equals(IPAddress.Any) && !iPAddress.Equals(IPAddress.Broadcast))
			{
				val.Answers.Add((ResourceRecord)new ARecord
				{
					Name = (DomainName)(text2),
					Address = iPAddress,
					TTL = SnapshotTtl
				});
			}
		}
		return val;
	}

	internal static Message ConvertRecords(nint pointer)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Expected O, but got Unknown
		//IL_01b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d1: Expected O, but got Unknown
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_011b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_0137: Expected O, but got Unknown
		//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Expected O, but got Unknown
		//IL_0193: Unknown result type (might be due to invalid IL or missing references)
		//IL_0198: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01af: Expected O, but got Unknown
		Message val = new Message
		{
			QR = true
		};
		HashSet<nint> hashSet = new HashSet<nint>();
		while (pointer != IntPtr.Zero && hashSet.Add(pointer))
		{
			NativeRecordHeader nativeRecordHeader = Marshal.PtrToStructure<NativeRecordHeader>(pointer);
			nint num = IntPtr.Add(pointer, Marshal.SizeOf<NativeRecordHeader>());
			uint charset = (nativeRecordHeader.Flags >> 3) & 3;
			string text = ReadString(nativeRecordHeader.Name, charset);
			ResourceRecord val2 = null;
			if (!string.IsNullOrWhiteSpace(text))
			{
				switch (nativeRecordHeader.Type)
				{
				case 12:
				{
					string text2 = ReadString(Marshal.ReadIntPtr(num), charset);
					if (!string.IsNullOrWhiteSpace(text2))
					{
						val2 = (ResourceRecord)new PTRRecord
						{
							Name = (DomainName)(text),
							DomainName = (DomainName)(text2)
						};
					}
					break;
				}
				case 33:
				{
					NativeSrv nativeSrv = Marshal.PtrToStructure<NativeSrv>(num);
					string text3 = ReadString(nativeSrv.Target, charset);
					if (!string.IsNullOrWhiteSpace(text3))
					{
						val2 = (ResourceRecord)new SRVRecord
						{
							Name = (DomainName)(text),
							Target = (DomainName)(text3),
							Priority = nativeSrv.Priority,
							Weight = nativeSrv.Weight,
							Port = nativeSrv.Port
						};
					}
					break;
				}
				case 16:
				{
					int num2 = Marshal.ReadInt32(num);
					if (num2 < 0 || num2 > 4096)
					{
						throw new InvalidDataException("DNS-SD TXT count is invalid.");
					}
					List<string> list = new List<string>();
					for (int i = 0; i < num2; i++)
					{
						list.Add(ReadString(Marshal.ReadIntPtr(num, checked(8 + i * IntPtr.Size)), charset));
					}
					val2 = (ResourceRecord)new TXTRecord
					{
						Name = (DomainName)(text),
						Strings = list
					};
					break;
				}
				case 1:
					val2 = (ResourceRecord)new ARecord
					{
						Name = (DomainName)(text),
						Address = ReadIpv4(num)
					};
					break;
				}
			}
			if (val2 != (ResourceRecord)null)
			{
				val2.TTL = ((nativeRecordHeader.Ttl == 0) ? TimeSpan.Zero : SnapshotTtl);
				val.Answers.Add(val2);
			}
			pointer = nativeRecordHeader.Next;
		}
		return val;
	}

	private static string ReadString(nint pointer, uint charset)
	{
		if (pointer == IntPtr.Zero)
		{
			return "";
		}
		return charset switch
		{
			1u => Marshal.PtrToStringUni(pointer) ?? "", 
			2u => Marshal.PtrToStringUTF8(pointer) ?? "", 
			_ => Marshal.PtrToStringAnsi(pointer) ?? "", 
		};
	}

	private static IPAddress ReadIpv4(nint pointer)
	{
		byte[] array = new byte[4];
		Marshal.Copy(pointer, array, 0, array.Length);
		return new IPAddress(array);
	}

	private static string NativeQueryName(DomainName name)
	{
		return string.Join(".", name.Labels.Select((string label) => label.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(".", "\\.", StringComparison.Ordinal)));
	}

	private static string? ServiceType(string name)
	{
		name = name.TrimEnd('.');
		return ServiceTypes.FirstOrDefault((string type) => name.EndsWith("." + type, StringComparison.OrdinalIgnoreCase));
	}
}
