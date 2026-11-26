using Microsoft.Win32;

namespace AirStereo2.Services;

public sealed class HkcuRunRegistryStore : IRegistryRunStore
{
	public const string RunKeyPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";

	public string? GetValue(string valueName)
	{
		using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", writable: false);
		return registryKey?.GetValue(valueName) as string;
	}

	public void SetValue(string valueName, string commandLine)
	{
		using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true) ?? Registry.CurrentUser.CreateSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run");
		registryKey.SetValue(valueName, commandLine);
	}

	public void DeleteValue(string valueName)
	{
		using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
		registryKey?.DeleteValue(valueName, throwOnMissingValue: false);
	}
}
