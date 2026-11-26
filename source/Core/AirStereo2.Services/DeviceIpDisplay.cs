using System.Net;

namespace AirStereo2.Services;

public static class DeviceIpDisplay
{
	public static bool HasKnownIpAddress(string? ipAddress)
	{
		IPAddress address;
		if (!string.IsNullOrWhiteSpace(ipAddress))
		{
			return IPAddress.TryParse(ipAddress.Trim(), out address);
		}
		return false;
	}

	public static string FormatDisplay(string? ipAddress, string unknownLabel)
	{
		if (!HasKnownIpAddress(ipAddress))
		{
			return unknownLabel;
		}
		return ipAddress.Trim();
	}
}
