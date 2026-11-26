using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AirStereo2.Models;

public static class WlanHelper
{
	[ComImport]
	[Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
	private class NetworkListManagerClass
	{
	}

	[ComImport]
	[Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
	[InterfaceType(ComInterfaceType.InterfaceIsDual)]
	private interface INetworkListManager
	{
		[return: MarshalAs(UnmanagedType.Interface)]
		object GetNetworks(int Flags);

		object GetNetwork(Guid gdNetworkId);

		[return: MarshalAs(UnmanagedType.Interface)]
		IEnumerable GetNetworkConnections();

		object GetNetworkConnection(Guid gdNetworkConnectionId);

		bool IsConnectedToInternet { get; }

		bool IsConnected { get; }

		int GetConnectivity();
	}

	[ComImport]
	[Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
	[InterfaceType(ComInterfaceType.InterfaceIsDual)]
	private interface INetworkConnection
	{
		[return: MarshalAs(UnmanagedType.Interface)]
		INetwork GetNetwork();

		bool IsConnectedToInternet { get; }

		bool IsConnected { get; }

		int GetConnectivity();

		Guid GetConnectionId();

		Guid GetAdapterId();

		int GetDomainType();
	}

	[ComImport]
	[Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
	[InterfaceType(ComInterfaceType.InterfaceIsDual)]
	private interface INetwork
	{
		[return: MarshalAs(UnmanagedType.BStr)]
		string GetName();

		void SetName([MarshalAs(UnmanagedType.BStr)] string szNetworkNewName);

		[return: MarshalAs(UnmanagedType.BStr)]
		string GetDescription();

		void SetDescription([MarshalAs(UnmanagedType.BStr)] string szDescription);

		Guid GetNetworkId();
	}

	internal sealed record AdapterIdentity(Guid AdapterId, NetworkInterfaceType Type, string Name, string Description, int? Ipv4Index, int? Ipv6Index);

	public static string? GetCurrentNetworkName()
	{
		try
		{
			using Process process = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
			{
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			});
			if (process != null)
			{
				string text = process.StandardOutput.ReadToEnd();
				process.WaitForExit(1000);
				string[] array = text.Split('\n');
				for (int i = 0; i < array.Length; i++)
				{
					string text2 = array[i].Trim();
					if (!text2.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) || text2.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					string[] array2 = text2.Split(':', 2);
					if (array2.Length == 2)
					{
						string text3 = array2[1].Trim();
						if (!string.IsNullOrEmpty(text3))
						{
							return text3;
						}
					}
				}
			}
		}
		catch
		{
		}
		return "LAN";
	}

	[DllImport("iphlpapi.dll")]
	private static extern int GetBestInterfaceEx([In] byte[] destination, out uint bestIfIndex);

	public static NetworkKey GetCurrentNetworkKey()
	{
		return ReadWindowsNetworkKey(null);
	}

	public static NetworkKey GetCurrentNetworkKey(IEnumerable<string?> targetAddresses)
	{
		return ReadWindowsNetworkKey(targetAddresses);
	}

	internal static bool IsPhysicalAdapter(AdapterIdentity adapter)
	{
		NetworkInterfaceType type = adapter.Type;
		if ((type == NetworkInterfaceType.Ethernet || type == NetworkInterfaceType.Wireless80211 || type == NetworkInterfaceType.GigabitEthernet) ? true : false)
		{
			return !new string[8] { "virtual", "pseudo", "vethernet", "wintun", "tap-", "tun adapter", "tun/tap", "vpn" }.Any((string marker) => adapter.Name.Contains(marker, StringComparison.OrdinalIgnoreCase) || adapter.Description.Contains(marker, StringComparison.OrdinalIgnoreCase));
		}
		return false;
	}

	internal static NetworkKey ResolveWindowsNetworkKey(IEnumerable<string?>? targetAddresses, IReadOnlyList<AdapterIdentity> adapters, IReadOnlyList<(Guid NetworkId, Guid AdapterId)> connections, Func<IPAddress, int?> readRoute)
	{
		List<AdapterIdentity> list = adapters.Where(IsPhysicalAdapter).ToList();
		Guid? selectedAdapter = null;
		if (targetAddresses != null)
		{
			List<string> list2 = targetAddresses.Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
			if (list2.Count == 0)
			{
				return new NetworkKey();
			}
			foreach (string item in list2)
			{
				if (!IPAddress.TryParse(item, out IPAddress address) || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address) || IPAddress.IsLoopback(address))
				{
					return new NetworkKey();
				}
				int? index = readRoute(address);
				bool ipv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
				List<AdapterIdentity> list3 = list.Where((AdapterIdentity adapter) => index.HasValue && (ipv6 ? adapter.Ipv6Index : adapter.Ipv4Index) == index).ToList();
				if (list3.Count != 1 || (selectedAdapter.HasValue && selectedAdapter != list3[0].AdapterId))
				{
					return new NetworkKey();
				}
				selectedAdapter = list3[0].AdapterId;
			}
		}
		List<(AdapterIdentity, Guid)> list4 = (from adapter in list
			join connection in connections on adapter.AdapterId equals connection.AdapterId
			where connection.NetworkId != Guid.Empty && adapter.AdapterId != Guid.Empty && (!selectedAdapter.HasValue || selectedAdapter == adapter.AdapterId)
			select (Adapter: adapter, NetworkId: connection.NetworkId)).Distinct().ToList();
		if (list4.Count != 1)
		{
			return new NetworkKey();
		}
		(AdapterIdentity, Guid) tuple = list4[0];
		bool flag = tuple.Item1.Type == NetworkInterfaceType.Wireless80211;
		return new NetworkKey
		{
			Transport = (flag ? "wifi" : "wired"),
			WindowsNetworkId = tuple.Item2.ToString("D"),
			ActiveAdapterId = tuple.Item1.AdapterId.ToString("D"),
			WiredIdentityVersion = (flag ? ((int?)null) : new int?(1)),
			WifiIdentityVersion = (flag ? new int?(1) : ((int?)null))
		};
	}

	private static int? ReadRoute(IPAddress address)
	{
		SocketAddress socketAddress = new IPEndPoint(address, 0).Serialize();
		byte[] array = new byte[socketAddress.Size];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = socketAddress[i];
		}
		if (GetBestInterfaceEx(array, out var bestIfIndex) != 0)
		{
			return null;
		}
		return (int)bestIfIndex;
	}

	private static NetworkKey ReadWindowsNetworkKey(IEnumerable<string?>? targetAddresses)
	{
		object obj = null;
		object obj2 = null;
		try
		{
			List<AdapterIdentity> list = new List<AdapterIdentity>();
			NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface networkInterface in allNetworkInterfaces)
			{
				if (networkInterface.OperationalStatus == OperationalStatus.Up && Guid.TryParse(networkInterface.Id, out var result))
				{
					try
					{
						IPInterfaceProperties iPProperties = networkInterface.GetIPProperties();
						int? ipv4Index = ((!networkInterface.Supports(NetworkInterfaceComponent.IPv4)) ? ((int?)null) : iPProperties.GetIPv4Properties()?.Index);
						int? ipv6Index = ((!networkInterface.Supports(NetworkInterfaceComponent.IPv6)) ? ((int?)null) : iPProperties.GetIPv6Properties()?.Index);
						list.Add(new AdapterIdentity(result, networkInterface.NetworkInterfaceType, networkInterface.Name, networkInterface.Description, ipv4Index, ipv6Index));
					}
					catch
					{
					}
				}
			}
			obj = new NetworkListManagerClass();
			obj2 = ((INetworkListManager)obj).GetNetworkConnections();
			List<(Guid, Guid)> list2 = new List<(Guid, Guid)>();
			foreach (INetworkConnection item in (IEnumerable)obj2)
			{
				INetwork network = null;
				try
				{
					if (item.IsConnected)
					{
						network = item.GetNetwork();
						list2.Add((network.GetNetworkId(), item.GetAdapterId()));
					}
				}
				catch
				{
				}
				finally
				{
					ReleaseComObject(network);
					ReleaseComObject(item);
				}
			}
			return ResolveWindowsNetworkKey(targetAddresses, list, list2, ReadRoute);
		}
		catch
		{
			return new NetworkKey();
		}
		finally
		{
			ReleaseComObject(obj2);
			ReleaseComObject(obj);
		}
	}

	private static void ReleaseComObject(object? value)
	{
		if (value != null && Marshal.IsComObject(value))
		{
			Marshal.ReleaseComObject(value);
		}
	}

	private static (Guid NetworkId, Guid AdapterId)? ReadWiredIdentity()
	{
		NetworkKey networkKey = ReadWindowsNetworkKey(null);
		if (!networkKey.IsKnown || !(networkKey.Transport == "wired") || !Guid.TryParse(networkKey.WindowsNetworkId, out var result) || !Guid.TryParse(networkKey.ActiveAdapterId, out var result2))
		{
			return null;
		}
		return (result, result2);
	}

	internal static NetworkKey GetCurrentNetworkKey(Func<string?> readWirelessInterfaces)
	{
		return GetCurrentNetworkKey(readWirelessInterfaces, ReadWiredIdentity);
	}

	internal static NetworkKey GetCurrentNetworkKey(Func<string?> readWirelessInterfaces, Func<(Guid NetworkId, Guid AdapterId)?> readWiredIdentity)
	{
		try
		{
			string text = readWirelessInterfaces();
			if (text != null)
			{
				string text2 = null;
				string text3 = null;
				string band = null;
				string a = string.Empty;
				string[] array = text.Split('\n');
				for (int i = 0; i < array.Length; i++)
				{
					string text4 = array[i].Trim();
					if (text4.StartsWith("State", StringComparison.OrdinalIgnoreCase))
					{
						string[] array2 = text4.Split(':', 2);
						if (array2.Length == 2)
						{
							a = array2[1].Trim();
						}
					}
					else if (text4.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !text4.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
					{
						string[] array3 = text4.Split(':', 2);
						if (array3.Length == 2)
						{
							text2 = array3[1].Trim();
						}
					}
					else if (text4.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
					{
						string[] array4 = text4.Split(':', 2);
						if (array4.Length == 2)
						{
							text3 = array4[1].Trim();
						}
					}
					else if (text4.StartsWith("Band", StringComparison.OrdinalIgnoreCase) || text4.StartsWith("Radio type", StringComparison.OrdinalIgnoreCase))
					{
						string[] array5 = text4.Split(':', 2);
						if (array5.Length == 2)
						{
							band = array5[1].Trim();
						}
					}
				}
				if (string.Equals(a, "connected", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(text2))
				{
					string bssidPrefix = null;
					if (!string.IsNullOrEmpty(text3))
					{
						string[] array6 = text3.Split(':');
						if (array6.Length >= 3)
						{
							bssidPrefix = $"{array6[0]}:{array6[1]}:{array6[2]}".ToLowerInvariant();
						}
					}
					return new NetworkKey
					{
						Transport = "wifi",
						Ssid = text2,
						BssidPrefix = bssidPrefix,
						Band = band,
						GatewayMacPrefix = null,
						ActiveAdapterId = null,
						WindowsNetworkId = null,
						WiredIdentityVersion = null
					};
				}
			}
		}
		catch
		{
		}
		try
		{
			(Guid, Guid)? tuple = readWiredIdentity();
			if (tuple.HasValue)
			{
				return new NetworkKey
				{
					Transport = "wired",
					WiredIdentityVersion = 1,
					WindowsNetworkId = tuple.Value.Item1.ToString("D"),
					ActiveAdapterId = tuple.Value.Item2.ToString("D"),
					GatewayMacPrefix = null
				};
			}
		}
		catch
		{
		}
		return new NetworkKey
		{
			Transport = "wired",
			Ssid = null,
			BssidPrefix = null,
			Band = null,
			GatewayMacPrefix = null,
			ActiveAdapterId = null,
			WindowsNetworkId = null,
			WiredIdentityVersion = null
		};
	}
}
