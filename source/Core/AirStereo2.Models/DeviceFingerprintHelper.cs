using System;
using System.Security.Cryptography;
using System.Text;

namespace AirStereo2.Models;

public static class DeviceFingerprintHelper
{
	public static string ComputeFingerprint(string oui, string model, string manufacturer, string? srcvers)
	{
		string text = oui.Trim().Replace("-", ":").ToLowerInvariant();
		string[] array = text.Split(':');
		if (array.Length > 3)
		{
			text = $"{array[0]}:{array[1]}:{array[2]}";
		}
		string text2 = model.Trim().ToLowerInvariant();
		string text3 = manufacturer.Trim().ToLowerInvariant();
		string text4 = NormalizeSrcVers(srcvers);
		string s = $"{text}|{text2}|{text3}|{text4}";
		using SHA256 sHA = SHA256.Create();
		return Convert.ToHexString(sHA.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
	}

	public static string NormalizeSrcVers(string? srcvers)
	{
		if (string.IsNullOrWhiteSpace(srcvers))
		{
			return "unknown";
		}
		string[] array = srcvers.Trim().Split('.');
		if (array.Length >= 2)
		{
			return $"{array[0]}.{array[1]}";
		}
		return array[0];
	}
}
