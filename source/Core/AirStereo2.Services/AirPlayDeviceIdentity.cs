using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace AirStereo2.Services;

public static class AirPlayDeviceIdentity
{
	public static string BuildStableId(string? deviceIdTxt, string? piTxt, string? tightSyncId, string? groupId, string? parentGroupId, string? model, string? displayName)
	{
		if (!string.IsNullOrWhiteSpace(deviceIdTxt))
		{
			return "deviceid:" + Normalize(deviceIdTxt);
		}
		if (!string.IsNullOrWhiteSpace(piTxt))
		{
			return "pi:" + Normalize(piTxt);
		}
		if (!string.IsNullOrWhiteSpace(tightSyncId))
		{
			return "tsid:" + Normalize(tightSyncId);
		}
		if (!string.IsNullOrWhiteSpace(groupId))
		{
			return "gid:" + Normalize(groupId);
		}
		if (!string.IsNullOrWhiteSpace(parentGroupId))
		{
			return "pgid:" + Normalize(parentGroupId);
		}
		string text = Normalize(model);
		string text2 = Normalize(displayName);
		if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(text2))
		{
			return "unknown";
		}
		return "fallback:" + text + "|" + text2;
	}

	public static string BuildStableIdFromTxt(IReadOnlyDictionary<string, string>? txt, string? tightSyncId, string? groupId, string? parentGroupId, string? model, string? displayName)
	{
		string text = Get(txt, "deviceid");
		if (string.IsNullOrEmpty(text))
		{
			text = Get(txt, "DeviceID");
		}
		string text2 = Get(txt, "pi");
		if (string.IsNullOrEmpty(text2))
		{
			text2 = Get(txt, "pk");
		}
		return BuildStableId(text, text2, tightSyncId, groupId, parentGroupId, model, displayName);
	}

	public static bool IdsEqual(string? a, string? b)
	{
		if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b))
		{
			return string.Equals(NormalizeForComparison(a), NormalizeForComparison(b), StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	public static bool IsDeviceScoped(string? stableId)
	{
		if (string.IsNullOrWhiteSpace(stableId))
		{
			return false;
		}
		string text = stableId.Trim();
		if (!text.StartsWith("deviceid:", StringComparison.OrdinalIgnoreCase))
		{
			return text.StartsWith("pi:", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static string ExtractRaopDeviceId(string? instanceName)
	{
		if (string.IsNullOrWhiteSpace(instanceName))
		{
			return string.Empty;
		}
		int num = instanceName.IndexOf('@');
		if (num <= 0)
		{
			return string.Empty;
		}
		return NormalizeDeviceId(instanceName.Substring(0, num));
	}

	internal static string NormalizeDeviceId(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}
		string compact = value.Trim().Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
		if (compact.Length != 12 || compact.Any((char ch) => !Uri.IsHexDigit(ch)))
		{
			return string.Empty;
		}
		return string.Join(":", from i in Enumerable.Range(0, 6)
			select compact.Substring(i * 2, 2));
	}

	internal static bool MatchesReceiverAdvertisement(string? deviceId, string? publicKey, string? siblingId, string? siblingKey)
	{
		if (!string.IsNullOrWhiteSpace(publicKey) && !string.IsNullOrWhiteSpace(siblingKey))
		{
			return string.Equals(publicKey, siblingKey, StringComparison.OrdinalIgnoreCase);
		}
		string text = NormalizeDeviceId(deviceId);
		if (!string.IsNullOrEmpty(text))
		{
			return string.Equals(text, NormalizeDeviceId(siblingId), StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	[return: NotNullIfNotNull("raw")]
	public static string? NormalizeGroupIdValue(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return raw;
		}
		string text = raw.Trim();
		if (!text.Contains('+'))
		{
			return text;
		}
		string[] array = text.Split('+');
		for (int i = 0; i < array.Length; i++)
		{
			string text2 = array[i].Trim();
			if (Guid.TryParse(text2, out var _))
			{
				return text2;
			}
		}
		return text;
	}

	private static string NormalizeForComparison(string id)
	{
		string text = id.Trim();
		if (text.StartsWith("deviceid:", StringComparison.OrdinalIgnoreCase))
		{
			string text2 = NormalizeDeviceId(text.Substring("deviceid:".Length));
			if (!string.IsNullOrEmpty(text2))
			{
				return "deviceid:" + text2;
			}
		}
		string[] array = new string[2] { "gid:", "pgid:" };
		foreach (string text3 in array)
		{
			if (text.StartsWith(text3, StringComparison.OrdinalIgnoreCase) && text.Contains('+'))
			{
				string text4 = NormalizeGroupIdValue(text.Substring(text3.Length));
				return text3 + text4;
			}
		}
		return text;
	}

	private static string Get(IReadOnlyDictionary<string, string>? txt, string key)
	{
		if (txt == null)
		{
			return string.Empty;
		}
		if (!txt.TryGetValue(key, out string value))
		{
			return string.Empty;
		}
		return value ?? string.Empty;
	}

	private static string Normalize(string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Trim().ToLowerInvariant();
		}
		return string.Empty;
	}
}
