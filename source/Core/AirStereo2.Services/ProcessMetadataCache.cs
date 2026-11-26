using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AirStereo2.Services;

public static class ProcessMetadataCache
{
	private sealed class CachedMeta
	{
		public string? FileDescription;

		public string? ProductName;

		public ImageSource? Icon;
	}

	internal const int MaximumEntryCount = 64;

	private static readonly ConcurrentDictionary<string, CachedMeta> Cache = new ConcurrentDictionary<string, CachedMeta>(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentQueue<string> InsertionOrder = new ConcurrentQueue<string>();

	internal static int EntryCount => Cache.Count;

	public static (string? Description, string? ProductName, ImageSource? Icon) Get(string? executablePath)
	{
		if (string.IsNullOrWhiteSpace(executablePath))
		{
			return (Description: null, ProductName: null, Icon: null);
		}
		CachedMeta cachedMeta;
		do
		{
			if (Cache.TryGetValue(executablePath, out CachedMeta value))
			{
				return (Description: value.FileDescription, ProductName: value.ProductName, Icon: value.Icon);
			}
			cachedMeta = Load(executablePath);
		}
		while (!Cache.TryAdd(executablePath, cachedMeta));
		InsertionOrder.Enqueue(executablePath);
		TrimToLimit();
		return (Description: cachedMeta.FileDescription, ProductName: cachedMeta.ProductName, Icon: cachedMeta.Icon);
	}

	public static void Clear()
	{
		Cache.Clear();
		string result;
		while (InsertionOrder.TryDequeue(out result))
		{
		}
	}

	private static void TrimToLimit()
	{
		string result;
		while (Cache.Count > 64 && InsertionOrder.TryDequeue(out result))
		{
			Cache.TryRemove(result, out CachedMeta _);
		}
	}

	private static CachedMeta Load(string path)
	{
		CachedMeta cachedMeta = new CachedMeta();
		try
		{
			if (File.Exists(path))
			{
				try
				{
					FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
					cachedMeta.FileDescription = NullIfEmpty(versionInfo.FileDescription);
					cachedMeta.ProductName = NullIfEmpty(versionInfo.ProductName);
				}
				catch
				{
				}
				cachedMeta.Icon = TryExtractIcon(path);
			}
		}
		catch
		{
		}
		return cachedMeta;
	}

	private static string? NullIfEmpty(string? s)
	{
		if (!string.IsNullOrWhiteSpace(s))
		{
			return s.Trim();
		}
		return null;
	}

	private static ImageSource? TryExtractIcon(string path) => null;
}
