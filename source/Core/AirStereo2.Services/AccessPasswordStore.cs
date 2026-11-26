using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AirStereo2.Services;

public static class AccessPasswordStore
{
	private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AirStereo2.AirPlayAccessPassword.v1");

	public static bool TryGet(string stableId, out string password)
	{
		password = string.Empty;
		if (string.IsNullOrWhiteSpace(stableId))
		{
			return false;
		}
		string value;
		string text = SettingsManager.ReadConfig((AppConfig config) => (config.AccessPasswords == null || !config.AccessPasswords.TryGetValue(stableId, out value)) ? null : value);
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}
		string text2 = Unprotect(text);
		if (string.IsNullOrEmpty(text2))
		{
			return false;
		}
		password = text2;
		return true;
	}

	public static void Save(string stableId, string password)
	{
		if (string.IsNullOrWhiteSpace(stableId) || string.IsNullOrEmpty(password))
		{
			return;
		}
		string blob = Protect(password);
		SettingsManager.UpdateConfig(delegate(AppConfig config)
		{
			if (config.AccessPasswords == null)
			{
				Dictionary<string, string> dictionary = (config.AccessPasswords = new Dictionary<string, string>());
			}
			config.AccessPasswords[stableId] = blob;
		});
	}

	public static void Remove(string stableId)
	{
		if (!string.IsNullOrWhiteSpace(stableId) && SettingsManager.ReadConfig((AppConfig config) => config.AccessPasswords?.ContainsKey(stableId) ?? false))
		{
			SettingsManager.UpdateConfig(delegate(AppConfig config)
			{
				config.AccessPasswords?.Remove(stableId);
			});
		}
	}

	internal static string Protect(string password)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(password);
		try
		{
			return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	internal static string? Unprotect(string blob)
	{
		try
		{
			byte[] array = ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser);
			try
			{
				return Encoding.UTF8.GetString(array);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(array);
			}
		}
		catch (Exception ex) when (((ex is CryptographicException || ex is FormatException) ? 1 : 0) != 0)
		{
			return null;
		}
	}
}
