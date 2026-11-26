using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace AirStereo2.Services;

public static class AppDataLocation
{
	private const int ErrorSuccess = 0;

	private const int ErrorInsufficientBuffer = 122;

	public static string AppDataDirectory { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

	public static bool IsPackaged()
	{
		try
		{
			int packageFullNameLength = 0;
			return IsPackagedResult(GetCurrentPackageFullName(ref packageFullNameLength, null));
		}
		catch (Exception ex) when (((ex is DllNotFoundException || ex is EntryPointNotFoundException) ? 1 : 0) != 0)
		{
			return false;
		}
	}

	internal static bool IsPackagedResult(int result)
	{
		if (result == 0 || result == 122)
		{
			return true;
		}
		return false;
	}

	public static string RealPath(string path)
	{
		string localCacheFolder = PackageLocalCacheFolder();
		string text = MapToPackageCache(path, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), localCacheFolder);
		if (string.Equals(text, path, StringComparison.OrdinalIgnoreCase) || (!Directory.Exists(text) && !File.Exists(text)))
		{
			return path;
		}
		return text;
	}

	internal static string MapToPackageCache(string path, string roamingRoot, string? localCacheFolder)
	{
		if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(roamingRoot) || string.IsNullOrWhiteSpace(localCacheFolder))
		{
			return path;
		}
		string text = Path.TrimEndingDirectorySeparator(roamingRoot);
		if (!path.StartsWith(text, StringComparison.OrdinalIgnoreCase))
		{
			return path;
		}
		string text2 = path.Substring(text.Length);
		if (text2.Length > 0 && text2[0] != Path.DirectorySeparatorChar && text2[0] != Path.AltDirectorySeparatorChar)
		{
			return path;
		}
		return Path.Combine(localCacheFolder, "Roaming") + text2;
	}

	private static string? PackageLocalCacheFolder()
	{
		if (!IsPackaged())
		{
			return null;
		}
		try
		{
			return ApplicationData.Current.LocalCacheFolder.Path;
		}
		catch (Exception)
		{
			return null;
		}
	}
}
