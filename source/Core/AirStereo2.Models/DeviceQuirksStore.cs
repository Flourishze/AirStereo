using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirStereo2.Models;

public static class DeviceQuirksStore
{
	private static readonly object _lock = new object();

	public static string QuirksFilePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "speaker_rules.json");

	public static Dictionary<string, DeviceQuirksEntry> LoadAll(string? path = null)
	{
		lock (_lock)
		{
			try
			{
				string path2 = path ?? QuirksFilePath;
				if (!File.Exists(path2))
				{
					return new Dictionary<string, DeviceQuirksEntry>(StringComparer.OrdinalIgnoreCase);
				}
				string json = File.ReadAllText(path2);
				JsonSerializerOptions options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true,
					PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
				};
				Dictionary<string, DeviceQuirksEntry> dictionary = JsonSerializer.Deserialize<Dictionary<string, DeviceQuirksEntry>>(json, options);
				if (dictionary == null)
				{
					return new Dictionary<string, DeviceQuirksEntry>(StringComparer.OrdinalIgnoreCase);
				}
				foreach (KeyValuePair<string, DeviceQuirksEntry> item in dictionary)
				{
					DeviceQuirksEntry value = item.Value;
					if (value.SafeProfiles == null)
					{
						value.SafeProfiles = new Dictionary<string, SafeProfileEntry>(StringComparer.OrdinalIgnoreCase);
					}
					if (value.SafeProfiles.Count == 0 && value.VerifiedSafeLatencyMs.HasValue && value.NetworkKey != null)
					{
						string key = value.NetworkKey.ComputeHash();
						value.SafeProfiles[key] = new SafeProfileEntry
						{
							NetworkKey = value.NetworkKey,
							VerifiedSafeLatencyMs = value.VerifiedSafeLatencyMs.Value,
							Source = value.Source,
							ExpiresAfterNetworkChange = value.ExpiresAfterNetworkChange
						};
					}
				}
				return dictionary;
			}
			catch
			{
				return new Dictionary<string, DeviceQuirksEntry>(StringComparer.OrdinalIgnoreCase);
			}
		}
	}

	public static void SaveAll(Dictionary<string, DeviceQuirksEntry> quirks, string? path = null)
	{
		lock (_lock)
		{
			try
			{
				string? path2 = path ?? QuirksFilePath;
				string directoryName = Path.GetDirectoryName(path2);
				if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
				{
					Directory.CreateDirectory(directoryName);
				}
				JsonSerializerOptions options = new JsonSerializerOptions
				{
					WriteIndented = true,
					PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
					DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
				};
				string contents = JsonSerializer.Serialize(quirks, options);
				File.WriteAllText(path2, contents);
			}
			catch
			{
			}
		}
	}

	public static DeviceQuirksEntry? GetEntry(string fingerprint, string? path = null)
	{
		if (!LoadAll(path).TryGetValue(fingerprint, out DeviceQuirksEntry value))
		{
			return null;
		}
		return value;
	}

	public static void UpsertEntry(DeviceQuirksEntry entry, string? path = null)
	{
		Dictionary<string, DeviceQuirksEntry> dictionary = LoadAll(path);
		dictionary[entry.Fingerprint] = entry;
		SaveAll(dictionary, path);
	}

	public static void UpsertSafeProfile(string fingerprint, string model, string manufacturer, string srcvers, SafeProfileEntry profile, string? path = null)
	{
		Dictionary<string, DeviceQuirksEntry> dictionary = LoadAll(path);
		if (!dictionary.TryGetValue(fingerprint, out var value))
		{
			value = (dictionary[fingerprint] = new DeviceQuirksEntry
			{
				Fingerprint = fingerprint,
				Model = model,
				Manufacturer = manufacturer,
				SrcVers = srcvers,
				Source = profile.Source
			});
		}
		if (value.SafeProfiles == null)
		{
			value.SafeProfiles = new Dictionary<string, SafeProfileEntry>(StringComparer.OrdinalIgnoreCase);
		}
		string key = profile.NetworkKey.ComputeHash();
		value.SafeProfiles[key] = profile;
		SaveAll(dictionary, path);
	}

	public static int? GetVerifiedSafeLatency(string fingerprint, NetworkKey currentNetwork, string? path = null)
	{
		DeviceQuirksEntry entry = GetEntry(fingerprint, path);
		if (entry == null)
		{
			return null;
		}
		if (entry.SafeProfiles != null && entry.SafeProfiles.Count > 0)
		{
			foreach (SafeProfileEntry value in entry.SafeProfiles.Values)
			{
				if (value.NetworkKey != null && value.NetworkKey.Matches(currentNetwork))
				{
					return value.VerifiedSafeLatencyMs;
				}
			}
		}
		if (entry.ExpiresAfterNetworkChange && (entry.NetworkKey == null || !entry.NetworkKey.Matches(currentNetwork)))
		{
			return null;
		}
		return entry.VerifiedSafeLatencyMs;
	}
}
