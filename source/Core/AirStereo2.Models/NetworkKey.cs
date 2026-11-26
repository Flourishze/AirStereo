using System;
using System.Security.Cryptography;
using System.Text;

namespace AirStereo2.Models;

public sealed class NetworkKey
{
	public const int CurrentWiredIdentityVersion = 1;

	public const int CurrentWifiIdentityVersion = 1;

	public string Transport { get; set; } = "wired";

	public string? Ssid { get; set; }

	public string? BssidPrefix { get; set; }

	public string? Band { get; set; }

	public string? GatewayMacPrefix { get; set; }

	public string? ActiveAdapterId { get; set; }

	public string? WindowsNetworkId { get; set; }

	public int? WiredIdentityVersion { get; set; }

	public int? WifiIdentityVersion { get; set; }

	private bool HasWindowsIdentity
	{
		get
		{
			if (string.IsNullOrWhiteSpace(WindowsNetworkId) && !WiredIdentityVersion.HasValue)
			{
				return WifiIdentityVersion.HasValue;
			}
			return true;
		}
	}

	private bool HasValidWindowsIds
	{
		get
		{
			if (Guid.TryParse(WindowsNetworkId, out var result) && result != Guid.Empty && Guid.TryParse(ActiveAdapterId, out var result2))
			{
				return result2 != Guid.Empty;
			}
			return false;
		}
	}

	public bool IsKnown
	{
		get
		{
			if (string.Equals(Transport, "wifi", StringComparison.OrdinalIgnoreCase))
			{
				if (!HasWindowsIdentity)
				{
					if (!string.IsNullOrWhiteSpace(Ssid) && !string.IsNullOrWhiteSpace(BssidPrefix))
					{
						return !string.IsNullOrWhiteSpace(Band);
					}
					return false;
				}
				if (WifiIdentityVersion == 1)
				{
					return HasValidWindowsIds;
				}
				return false;
			}
			if (string.Equals(Transport, "wired", StringComparison.OrdinalIgnoreCase))
			{
				if (!HasWindowsIdentity)
				{
					if (string.IsNullOrWhiteSpace(GatewayMacPrefix))
					{
						return !string.IsNullOrWhiteSpace(ActiveAdapterId);
					}
					return true;
				}
				if (WiredIdentityVersion == 1)
				{
					return HasValidWindowsIds;
				}
				return false;
			}
			return false;
		}
	}

	public string ComputeHash()
	{
		if (string.Equals(Transport, "wifi", StringComparison.OrdinalIgnoreCase))
		{
			if (HasWindowsIdentity)
			{
				string s = $"wifi|v{WifiIdentityVersion}|{NormalizeGuid(WindowsNetworkId).ToLowerInvariant()}|{NormalizeGuid(ActiveAdapterId).ToLowerInvariant()}";
				using SHA256 sHA = SHA256.Create();
				return Convert.ToHexString(sHA.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant().Substring(0, 16);
			}
			string value = Ssid?.Trim().ToLowerInvariant() ?? "none";
			string value2 = BssidPrefix?.Trim().ToLowerInvariant() ?? "none";
			string value3 = Band?.Trim().ToLowerInvariant() ?? "none";
			string s2 = $"wifi|{value}|{value2}|{value3}";
			using SHA256 sHA2 = SHA256.Create();
			return Convert.ToHexString(sHA2.ComputeHash(Encoding.UTF8.GetBytes(s2))).ToLowerInvariant().Substring(0, 16);
		}
		string value4 = WiredIdentityVersion?.ToString() ?? "0";
		string value5 = ((WindowsNetworkId != null) ? NormalizeGuid(WindowsNetworkId).ToLowerInvariant() : "none");
		string value6 = GatewayMacPrefix?.Trim().ToLowerInvariant() ?? "none";
		string value7 = ((ActiveAdapterId != null) ? NormalizeGuid(ActiveAdapterId).ToLowerInvariant() : "none");
		string s3 = $"wired|v{value4}|{value5}|{value6}|{value7}";
		using SHA256 sHA3 = SHA256.Create();
		return Convert.ToHexString(sHA3.ComputeHash(Encoding.UTF8.GetBytes(s3))).ToLowerInvariant().Substring(0, 16);
	}

	public bool Matches(NetworkKey? other)
	{
		if (other == null || !IsKnown || !other.IsKnown)
		{
			return false;
		}
		if (!string.Equals(Transport, other.Transport, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (HasWindowsIdentity || other.HasWindowsIdentity)
		{
			if (!HasWindowsIdentity || !other.HasWindowsIdentity)
			{
				return false;
			}
			if (WiredIdentityVersion != other.WiredIdentityVersion || WifiIdentityVersion != other.WifiIdentityVersion)
			{
				return false;
			}
			if (NormalizeGuid(WindowsNetworkId).Equals(NormalizeGuid(other.WindowsNetworkId), StringComparison.OrdinalIgnoreCase))
			{
				return NormalizeGuid(ActiveAdapterId).Equals(NormalizeGuid(other.ActiveAdapterId), StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		if (string.Equals(Transport, "wifi", StringComparison.OrdinalIgnoreCase))
		{
			if (!string.Equals(Ssid, other.Ssid, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (!string.Equals(BssidPrefix, other.BssidPrefix, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (!string.Equals(Band, other.Band, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			return true;
		}
		if (string.Equals(GatewayMacPrefix, other.GatewayMacPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(ActiveAdapterId, other.ActiveAdapterId, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	internal static string NormalizeGuid(string? id)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return string.Empty;
		}
		if (!Guid.TryParse(id.Trim(), out var result))
		{
			return id.Trim();
		}
		return result.ToString("D");
	}
}
