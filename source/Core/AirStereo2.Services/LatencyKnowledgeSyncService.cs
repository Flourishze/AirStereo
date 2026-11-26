using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AirStereo2.Models;

namespace AirStereo2.Services;

public sealed class LatencyKnowledgeSyncService
{
	private static readonly object _lock = new object();

	private readonly HttpClient _httpClient;

	private readonly string _cacheFilePath;

	public static readonly TimeSpan MaxStaleDuration = TimeSpan.FromDays(30.0);

	public string? CachedEtag { get; private set; }

	public DateTime? LastFetchedAtUtc { get; private set; }

	public Dictionary<string, LatencyKnowledgeEntry> KnowledgeBase { get; private set; } = new Dictionary<string, LatencyKnowledgeEntry>(StringComparer.OrdinalIgnoreCase);

	public LatencyKnowledgeSyncService(HttpClient? httpClient = null, string? cacheFilePath = null)
	{
		_httpClient = httpClient ?? new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(5.0)
		};
		_cacheFilePath = cacheFilePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "device_profiles.json");
		LoadFromLocalCacheOrSeed();
	}

	public bool IsCacheExpired(DateTime? nowUtc = null)
	{
		lock (_lock)
		{
			if (!LastFetchedAtUtc.HasValue)
			{
				return false;
			}
			return (nowUtc ?? DateTime.UtcNow) - LastFetchedAtUtc.Value > MaxStaleDuration;
		}
	}

	public void LoadFromLocalCacheOrSeed()
	{
		lock (_lock)
		{
			try
			{
				if (File.Exists(_cacheFilePath))
				{
					CloudSyncResponse cloudSyncResponse = ParseSyncPayload(File.ReadAllText(_cacheFilePath));
					if (cloudSyncResponse != null && cloudSyncResponse.SchemaVersion <= 1)
					{
						CachedEtag = cloudSyncResponse.Etag;
						LastFetchedAtUtc = cloudSyncResponse.FetchedAtUtc;
						Dictionary<string, LatencyKnowledgeEntry> dictionary = new Dictionary<string, LatencyKnowledgeEntry>(StringComparer.OrdinalIgnoreCase);
						foreach (LatencyKnowledgeEntry entry in cloudSyncResponse.Entries)
						{
							if (ValidateEntry(entry, out string _))
							{
								entry.Origin = LatencyKnowledgeOrigin.VerifiedCloud;
								dictionary[entry.DeviceFingerprint] = entry;
							}
						}
						KnowledgeBase = dictionary;
						return;
					}
				}
			}
			catch
			{
			}
			LoadBuiltInSeeds();
		}
	}

	public void LoadBuiltInSeeds()
	{
		List<LatencyKnowledgeEntry> defaultSeedEntries = GetDefaultSeedEntries();
		KnowledgeBase = defaultSeedEntries.ToDictionary<LatencyKnowledgeEntry, string, LatencyKnowledgeEntry>((LatencyKnowledgeEntry e) => e.DeviceFingerprint, (LatencyKnowledgeEntry e) => e, StringComparer.OrdinalIgnoreCase);
		CachedEtag = "built-in-seed-v1";
		LastFetchedAtUtc = null;
	}

	public LatencyKnowledgeSyncResult ProcessSyncPayload(string payloadJson, string? incomingEtag, HttpStatusCode statusCode = HttpStatusCode.OK, DateTime? fetchTimeUtc = null)
	{
		lock (_lock)
		{
			if (statusCode == HttpStatusCode.NotModified || (incomingEtag != null && string.Equals(incomingEtag, CachedEtag, StringComparison.OrdinalIgnoreCase)))
			{
				return new LatencyKnowledgeSyncResult
				{
					Success = true,
					NotModified = true,
					Etag = CachedEtag,
					EntryCount = KnowledgeBase.Count
				};
			}
			try
			{
				CloudSyncResponse cloudSyncResponse = ParseSyncPayload(payloadJson);
				if (cloudSyncResponse == null)
				{
					return new LatencyKnowledgeSyncResult
					{
						Success = false,
						ErrorMessage = "Failed to deserialize sync payload",
						EntryCount = KnowledgeBase.Count
					};
				}
				if (cloudSyncResponse.SchemaVersion > 1)
				{
					return new LatencyKnowledgeSyncResult
					{
						Success = false,
						ErrorMessage = $"Incompatible schema version: {cloudSyncResponse.SchemaVersion} (supported <= 1)",
						EntryCount = KnowledgeBase.Count
					};
				}
				DateTime dateTime = fetchTimeUtc ?? ((cloudSyncResponse.FetchedAtUtc != default(DateTime)) ? cloudSyncResponse.FetchedAtUtc : DateTime.UtcNow);
				LastFetchedAtUtc = dateTime;
				CachedEtag = ((!string.IsNullOrWhiteSpace(incomingEtag)) ? incomingEtag : cloudSyncResponse.Etag);
				Dictionary<string, LatencyKnowledgeEntry> dictionary = new Dictionary<string, LatencyKnowledgeEntry>(StringComparer.OrdinalIgnoreCase);
				foreach (LatencyKnowledgeEntry entry in cloudSyncResponse.Entries)
				{
					if (ValidateEntry(entry, out string _))
					{
						entry.Origin = LatencyKnowledgeOrigin.VerifiedCloud;
						dictionary[entry.DeviceFingerprint] = entry;
					}
				}
				KnowledgeBase = dictionary;
				try
				{
					string directoryName = Path.GetDirectoryName(_cacheFilePath);
					if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
					{
						Directory.CreateDirectory(directoryName);
					}
					cloudSyncResponse.FetchedAtUtc = dateTime;
					string contents = JsonSerializer.Serialize(cloudSyncResponse, new JsonSerializerOptions
					{
						WriteIndented = true,
						PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
					});
					File.WriteAllText(_cacheFilePath, contents);
				}
				catch
				{
				}
				return new LatencyKnowledgeSyncResult
				{
					Success = true,
					NotModified = false,
					Etag = CachedEtag,
					EntryCount = KnowledgeBase.Count
				};
			}
			catch (Exception ex)
			{
				return new LatencyKnowledgeSyncResult
				{
					Success = false,
					ErrorMessage = "Corrupt JSON or sync error: " + ex.Message,
					EntryCount = KnowledgeBase.Count
				};
			}
		}
	}

	public LatencyKnowledgeEntry? GetCandidateKnowledge(string fingerprint, NetworkKey currentNetwork, DateTime? nowUtc = null)
	{
		lock (_lock)
		{
			if (IsCacheExpired(nowUtc))
			{
				return null;
			}
			if (!KnowledgeBase.TryGetValue(fingerprint, out LatencyKnowledgeEntry value))
			{
				return null;
			}
			if (string.Equals(value.NetworkScope, "network_bound", StringComparison.OrdinalIgnoreCase) && (value.BoundNetworkKey == null || !value.BoundNetworkKey.Matches(currentNetwork)))
			{
				return null;
			}
			return value;
		}
	}

	public static bool ValidateEntry(LatencyKnowledgeEntry entry, out string validationError)
	{
		if (entry == null)
		{
			validationError = "Entry is null";
			return false;
		}
		if (string.IsNullOrWhiteSpace(entry.DeviceFingerprint) || entry.DeviceFingerprint.Length != 64 || !entry.DeviceFingerprint.All(Uri.IsHexDigit))
		{
			validationError = "Invalid device fingerprint: '" + entry.DeviceFingerprint + "' (must be 64-char hex)";
			return false;
		}
		if (!string.Equals(entry.NetworkScope, "global", StringComparison.OrdinalIgnoreCase) && !string.Equals(entry.NetworkScope, "network_bound", StringComparison.OrdinalIgnoreCase))
		{
			validationError = "Invalid network_scope: '" + entry.NetworkScope + "'";
			return false;
		}
		if (!string.Equals(entry.Confidence, "seed", StringComparison.OrdinalIgnoreCase) && !string.Equals(entry.Confidence, "observed", StringComparison.OrdinalIgnoreCase) && !string.Equals(entry.Confidence, "validated", StringComparison.OrdinalIgnoreCase))
		{
			validationError = "Invalid confidence: '" + entry.Confidence + "'";
			return false;
		}
		if (!string.Equals(entry.Source, "cloud_seed", StringComparison.OrdinalIgnoreCase) && !string.Equals(entry.Source, "local_probe", StringComparison.OrdinalIgnoreCase) && !string.Equals(entry.Source, "acoustic_test", StringComparison.OrdinalIgnoreCase))
		{
			validationError = "Invalid source: '" + entry.Source + "'";
			return false;
		}
		if (entry.VerifiedSafeLatencyMs.HasValue && (entry.VerifiedSafeLatencyMs.Value < 10 || entry.VerifiedSafeLatencyMs.Value > 2000))
		{
			validationError = $"VerifiedSafeLatencyMs out of range: {entry.VerifiedSafeLatencyMs.Value}";
			return false;
		}
		if (entry.AcousticOffsetPriorMs.HasValue && (entry.AcousticOffsetPriorMs.Value < -250 || entry.AcousticOffsetPriorMs.Value > 250))
		{
			validationError = $"AcousticOffsetPriorMs out of range: {entry.AcousticOffsetPriorMs.Value}";
			return false;
		}
		if (entry.SampleCount < 1)
		{
			validationError = $"SampleCount must be >= 1, got {entry.SampleCount}";
			return false;
		}
		if (string.Equals(entry.NetworkScope, "network_bound", StringComparison.OrdinalIgnoreCase) && entry.BoundNetworkKey == null)
		{
			validationError = "network_bound entry must include BoundNetworkKey";
			return false;
		}
		validationError = string.Empty;
		return true;
	}

	private static CloudSyncResponse? ParseSyncPayload(string json)
	{
		JsonSerializerOptions options = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
		};
		return JsonSerializer.Deserialize<CloudSyncResponse>(json, options);
	}

	public static List<LatencyKnowledgeEntry> GetDefaultSeedEntries()
	{
		return new List<LatencyKnowledgeEntry>
		{
			new LatencyKnowledgeEntry
			{
				Origin = LatencyKnowledgeOrigin.BuiltInSeed,
				SchemaVersion = 1,
				DeviceFingerprint = "26a2dede627788c3c98bb3da982c40b30feb4944f2aa55534bd0898cd1fb3de1",
				Model = "AudioAccessory6,1",
				Manufacturer = "Apple",
				SrcversMajorMinor = "960.13",
				NetworkScope = "global",
				VerifiedSafeLatencyMs = 45,
				AcousticOffsetPriorMs = 7,
				Confidence = "validated",
				Source = "cloud_seed",
				SampleCount = 100
			},
			new LatencyKnowledgeEntry
			{
				Origin = LatencyKnowledgeOrigin.BuiltInSeed,
				SchemaVersion = 1,
				DeviceFingerprint = "985d9c27bed16c9bf97de2dbfa75682d377305b59411e1c1539f16f72aec1329",
				Model = "AppleTV6,2",
				Manufacturer = "Apple",
				SrcversMajorMinor = "960.13",
				NetworkScope = "global",
				VerifiedSafeLatencyMs = 40,
				AcousticOffsetPriorMs = 23,
				Confidence = "validated",
				Source = "cloud_seed",
				SampleCount = 100
			},
			new LatencyKnowledgeEntry
			{
				Origin = LatencyKnowledgeOrigin.BuiltInSeed,
				SchemaVersion = 1,
				DeviceFingerprint = "9d799c0302004a7d586231fdb9156db925164a0bf4aeb2793f365fa3f2ea4b31",
				Model = "ShairportSync",
				Manufacturer = "MikeBrady",
				SrcversMajorMinor = "366.0",
				NetworkScope = "global",
				VerifiedSafeLatencyMs = 60,
				AcousticOffsetPriorMs = null,
				Confidence = "seed",
				Source = "cloud_seed",
				SampleCount = 10
			}
		};
	}
}
