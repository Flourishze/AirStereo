using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using AirStereo2.Models;

namespace AirStereo2.Services;

public static class SettingsManager
{
	private enum ConfigFileState
	{
		Loaded,
		Missing,
		Corrupt,
		Unreadable
	}

	private readonly record struct ConfigFileRead(ConfigFileState State, AppConfig? Config, string? Error);

	private static readonly string DefaultConfigPath;

	private static string? _configPathOverride;

	private static AppConfig _config;

	private static readonly object _lock;

	private static readonly object _persistLock;

	private static readonly TimeSpan SaveDebounceInterval;

	private static Timer? _saveDebounceTimer;

	private static bool _savePending;

	private static readonly TimeSpan[] WriteRetryDelays;

	internal const int MaxDeferredSaveRetries = 5;

	private static readonly TimeSpan DeferredSaveRetryBase;

	private static readonly TimeSpan DeferredSaveRetryMax;

	private static string? _unreadPrimaryBaseJson;

	private static readonly TimeSpan[] LoadRetryDelays;

	private static int _consecutiveSaveFailures;

	private static DateTime? _lastSaveFailureUtc;

	private static string? _lastSaveFailure;

	private static DateTime? _lastSaveSuccessUtc;

	private static string ConfigPath => _configPathOverride ?? DefaultConfigPath;

	internal static string? ConfigPathOverrideForTesting
	{
		get
		{
			return _configPathOverride;
		}
		set
		{
			_configPathOverride = value;
		}
	}

	static SettingsManager()
	{
		DefaultConfigPath = (AppContext.GetData("AirStereo2.TestSettingsPath") as string) ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "user_options.json");
		_config = new AppConfig();
		_lock = new object();
		_persistLock = new object();
		SaveDebounceInterval = TimeSpan.FromMilliseconds(400.0);
		WriteRetryDelays = new TimeSpan[2]
		{
			TimeSpan.FromMilliseconds(50.0),
			TimeSpan.FromMilliseconds(200.0)
		};
		DeferredSaveRetryBase = TimeSpan.FromSeconds(2.0);
		DeferredSaveRetryMax = TimeSpan.FromSeconds(30.0);
		LoadRetryDelays = new TimeSpan[4]
		{
			TimeSpan.FromMilliseconds(100.0),
			TimeSpan.FromMilliseconds(250.0),
			TimeSpan.FromMilliseconds(500.0),
			TimeSpan.FromMilliseconds(1000.0)
		};
		Load();
	}

	public static T ReadConfig<T>(Func<AppConfig, T> reader)
	{
		ArgumentNullException.ThrowIfNull(reader, "reader");
		lock (_lock)
		{
			return reader(_config);
		}
	}

	public static void UpdateConfig(Action<AppConfig> update, bool requestSave = true)
	{
		ArgumentNullException.ThrowIfNull(update, "update");
		lock (_lock)
		{
			update(_config);
			NormalizeAppConfigBounds(_config);
			if (requestSave)
			{
				RequestSaveUnlocked();
			}
		}
	}

	public static string GetOrCreateDiagnosticsInstallationId()
	{
		lock (_lock)
		{
			if (DiagnosticsUploadService.IsValidInstallationId(_config.DiagnosticsInstallationId))
			{
				return _config.DiagnosticsInstallationId;
			}
			string text = Guid.NewGuid().ToString("D");
			_config.DiagnosticsInstallationId = text;
			RequestSaveUnlocked();
			return text;
		}
	}

	public static void NormalizeCurrentEqConfig()
	{
		lock (_lock)
		{
			NormalizeEqConfig(_config);
		}
	}

	public static EqConfigSnapshot ReadEqConfigSnapshot()
	{
		lock (_lock)
		{
			NormalizeEqConfig(_config);
			return new EqConfigSnapshot(_config.EqEnabled, _config.EqPreampDb, _config.EqPresetId, _config.EqBandGains.ToArray(), _config.EqUserPresets.Select((EqUserPresetConfig preset) => new EqUserPresetConfig
			{
				Name = preset.Name,
				Gains = preset.Gains.ToList()
			}).ToArray());
		}
	}

	public static void Load()
	{
		lock (_lock)
		{
			_unreadPrimaryBaseJson = null;
			string text = ConfigPath + ".bak";
			ConfigFileRead configFileRead = (File.Exists(ConfigPath) ? ReadConfigFileWithRetry(ConfigPath) : new ConfigFileRead(ConfigFileState.Missing, null, null));
			if (configFileRead.Config != null)
			{
				_config = configFileRead.Config;
			}
			else
			{
				if (configFileRead.State == ConfigFileState.Corrupt)
				{
					PreserveUnreadableConfig();
				}
				else
				{
					_ = configFileRead.State;
					_ = 3;
				}
				ConfigFileRead configFileRead2 = (File.Exists(text) ? ReadConfigFileWithRetry(text) : new ConfigFileRead(ConfigFileState.Missing, null, null));
				if (configFileRead2.Config != null)
				{
					_config = configFileRead2.Config;
					if (configFileRead.State != ConfigFileState.Unreadable)
					{
						RestorePrimaryFromBackup(text);
					}
				}
				else
				{
					_ = configFileRead2.State;
					_ = 1;
				}
			}
			NormalizeLoadedConfig(_config);
			if (configFileRead.State == ConfigFileState.Unreadable)
			{
				_unreadPrimaryBaseJson = SerializeConfig(_config);
			}
		}
	}

	private static void NormalizeLoadedConfig(AppConfig config)
	{
		NormalizeEqConfig(config);
		NormalizeAudioCodecConfig(config);
		NormalizeAppConfigBounds(config);
		NormalizeDefaultOutputDevices(config);
		NormalizeLatencyCalibrationProfiles(config);
	}

	private static ConfigFileRead ReadConfigFileWithRetry(string path)
	{
		int num = 0;
		ConfigFileRead result;
		while (true)
		{
			result = ReadConfigFile(path);
			if (result.State != ConfigFileState.Unreadable || num >= LoadRetryDelays.Length)
			{
				break;
			}
			Thread.Sleep(LoadRetryDelays[num]);
			num++;
		}
		return result;
	}

	private static ConfigFileRead ReadConfigFile(string path)
	{
		string json;
		try
		{
			json = File.ReadAllText(path);
		}
		catch (Exception ex) when (((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? 1 : 0) != 0)
		{
			return new ConfigFileRead(ConfigFileState.Missing, null, ex.Message);
		}
		catch (Exception ex2) when (((ex2 is IOException || ex2 is UnauthorizedAccessException) ? 1 : 0) != 0)
		{
			return new ConfigFileRead(ConfigFileState.Unreadable, null, DescribePersistenceError(ex2));
		}
		return ParseConfigJson(json);
	}

	private static ConfigFileRead ParseConfigJson(string json)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(json.Replace('\0', ' ')))
			{
				return new ConfigFileRead(ConfigFileState.Corrupt, null, "config file is empty");
			}
			bool flag = ConfigJsonPredatesOnboarding(json);
			AppConfig appConfig = JsonSerializer.Deserialize<AppConfig>(json);
			if (appConfig == null)
			{
				return new ConfigFileRead(ConfigFileState.Corrupt, null, "config file deserialized to null");
			}
			MigrateDeveloperLatencyOverrideSwitches(json, appConfig);
			MigrateDeactivateAcousticOffset(json, appConfig);
			if (flag)
			{
				appConfig.OnboardingCompletedVersion = 1;
			}
			return new ConfigFileRead(ConfigFileState.Loaded, appConfig, null);
		}
		catch (Exception ex)
		{
			return new ConfigFileRead(ConfigFileState.Corrupt, null, ex.Message);
		}
	}

	private static bool ReconcileUnreadPrimary(out Exception? failure)
	{
		failure = null;
		string unreadPrimaryBaseJson;
		lock (_lock)
		{
			unreadPrimaryBaseJson = _unreadPrimaryBaseJson;
		}
		if (unreadPrimaryBaseJson == null)
		{
			return true;
		}
		string json;
		try
		{
			json = File.ReadAllText(ConfigPath);
		}
		catch (Exception ex) when (((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? 1 : 0) != 0)
		{
			lock (_lock)
			{
				_unreadPrimaryBaseJson = null;
			}
			return true;
		}
		catch (Exception ex2) when (((ex2 is IOException || ex2 is UnauthorizedAccessException) ? 1 : 0) != 0)
		{
			failure = ex2;
			return false;
		}
		lock (_lock)
		{
			ConfigFileRead configFileRead = ParseConfigJson(json);
			if (configFileRead.Config == null)
			{
				PreserveUnreadableConfig();
			}
			else
			{
				_config = ReconcileWithDisk(unreadPrimaryBaseJson, _config, configFileRead.Config);
			}
			_unreadPrimaryBaseJson = null;
		}
		return true;
	}

	internal static AppConfig ReconcileWithDisk(string baseJson, AppConfig mine, AppConfig theirs)
	{
		AppConfig? obj = MergeSessionChanges(baseJson, SerializeConfig(mine), SerializeConfig(theirs)) ?? theirs;
		NormalizeLoadedConfig(obj);
		return obj;
	}

	internal static AppConfig? MergeSessionChanges(string baseJson, string mineJson, string theirsJson)
	{
		try
		{
			if (!(JsonNode.Parse(theirsJson) is JsonObject theirs))
			{
				return null;
			}
			return MergeNode(JsonNode.Parse(baseJson), JsonNode.Parse(mineJson), theirs)?.Deserialize<AppConfig>();
		}
		catch (Exception ex) when (((ex is JsonException || ex is InvalidOperationException || ex is NotSupportedException) ? 1 : 0) != 0)
		{
			return null;
		}
	}

	private static JsonNode? MergeNode(JsonNode? @base, JsonNode? mine, JsonNode? theirs)
	{
		if (JsonNode.DeepEquals(mine, @base))
		{
			return theirs?.DeepClone();
		}
		if (mine is JsonObject jsonObject && theirs is JsonObject jsonObject2)
		{
			JsonObject jsonObject3 = @base as JsonObject;
			JsonObject jsonObject4 = new JsonObject();
			string key;
			JsonNode value;
			foreach (KeyValuePair<string, JsonNode> item in jsonObject)
			{
				item.Deconstruct(out key, out value);
				string propertyName = key;
				JsonNode jsonNode = value;
				if (!jsonObject2.TryGetPropertyValue(propertyName, out JsonNode jsonNode2))
				{
					jsonObject4[propertyName] = jsonNode?.DeepClone();
					continue;
				}
				JsonNode jsonNode3 = null;
				bool flag = jsonObject3?.TryGetPropertyValue(propertyName, out jsonNode3) ?? false;
				jsonObject4[propertyName] = (flag ? MergeNode(jsonNode3, jsonNode, jsonNode2) : jsonNode?.DeepClone());
			}
			{
				foreach (KeyValuePair<string, JsonNode> item2 in jsonObject2)
				{
					item2.Deconstruct(out key, out value);
					string propertyName2 = key;
					JsonNode jsonNode4 = value;
					bool flag2 = jsonObject3?.ContainsKey(propertyName2) ?? false;
					if (!jsonObject.ContainsKey(propertyName2) && !flag2)
					{
						jsonObject4[propertyName2] = jsonNode4?.DeepClone();
					}
				}
				return jsonObject4;
			}
		}
		return mine?.DeepClone();
	}

	private static void PreserveUnreadableConfig()
	{
		try
		{
			string destFileName = ConfigPath + ".malformed.old";
			File.Copy(ConfigPath, destFileName, overwrite: true);
		}
		catch (Exception)
		{
		}
	}

	private static void RestorePrimaryFromBackup(string backupPath)
	{
		try
		{
			File.Copy(backupPath, ConfigPath, overwrite: true);
		}
		catch (Exception)
		{
		}
	}

	internal static bool ConfigJsonPredatesOnboarding(string json)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(json);
		JsonElement value = jsonDocument.RootElement;
		return value.ValueKind == JsonValueKind.Object && !jsonDocument.RootElement.TryGetProperty("OnboardingCompletedVersion", out value);
	}

	internal static void MigrateDeveloperLatencyOverrideSwitches(string json, AppConfig config)
	{
		if (config == null || string.IsNullOrWhiteSpace(json))
		{
			return;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			JsonElement value = jsonDocument.RootElement;
			if (value.ValueKind == JsonValueKind.Object)
			{
				JsonElement rootElement = jsonDocument.RootElement;
				if (!rootElement.TryGetProperty("DevSoloLatencyOverrideEnabled", out value))
				{
					config.DevSoloLatencyOverrideEnabled = config.DeveloperModeEnabled && config.DevSoloLatencyMs.HasValue;
				}
				if (!rootElement.TryGetProperty("DevGroupLatencyOverrideEnabled", out value))
				{
					config.DevGroupLatencyOverrideEnabled = config.DeveloperModeEnabled && config.DevGroupLatencyMs.HasValue;
				}
				if (!rootElement.TryGetProperty("DevMixedLatencyOverrideEnabled", out value))
				{
					config.DevMixedLatencyOverrideEnabled = config.DeveloperModeEnabled && config.DevMixedLatencyMs.HasValue;
				}
			}
		}
		catch (JsonException)
		{
		}
	}

	internal static void MigrateDeactivateAcousticOffset(string json, AppConfig config)
	{
		if (config != null && !string.IsNullOrWhiteSpace(json))
		{
			DeactivateAcousticOffset(config);
		}
	}

	private static void DeactivateAcousticOffset(AppConfig config)
	{
		if (config.AcousticOffsetMs != 0)
		{
			int? archivedAcousticOffsetMs = config.ArchivedAcousticOffsetMs;
			int valueOrDefault = archivedAcousticOffsetMs.GetValueOrDefault();
			if (!archivedAcousticOffsetMs.HasValue)
			{
				valueOrDefault = config.AcousticOffsetMs;
				int? archivedAcousticOffsetMs2 = valueOrDefault;
				config.ArchivedAcousticOffsetMs = archivedAcousticOffsetMs2;
			}
		}
		config.AcousticOffsetMs = 0;
		config.AcousticOffsetDeactivated = true;
	}

	public static void NormalizeDefaultOutputDevices(AppConfig config)
	{
		if (config == null)
		{
			return;
		}
		if (config.DefaultOutputDevices == null)
		{
			List<DefaultOutputDeviceRecord> list = (config.DefaultOutputDevices = new List<DefaultOutputDeviceRecord>());
		}
		List<DefaultOutputDeviceRecord> list3 = new List<DefaultOutputDeviceRecord>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (DefaultOutputDeviceRecord defaultOutputDevice in config.DefaultOutputDevices)
		{
			if (defaultOutputDevice != null && !string.IsNullOrWhiteSpace(defaultOutputDevice.StableId))
			{
				string text = defaultOutputDevice.StableId.Trim();
				if (hashSet.Add(text))
				{
					list3.Add(new DefaultOutputDeviceRecord
					{
						StableId = text,
						DisplayName = (defaultOutputDevice.DisplayName?.Trim() ?? string.Empty),
						Model = (defaultOutputDevice.Model?.Trim() ?? string.Empty),
						LastKnownIp = (defaultOutputDevice.LastKnownIp?.Trim() ?? string.Empty),
						GroupKey = (string.IsNullOrWhiteSpace(defaultOutputDevice.GroupKey) ? null : defaultOutputDevice.GroupKey.Trim())
					});
				}
			}
		}
		config.DefaultOutputDevices = list3;
	}

	public static bool IsDefaultOutput(string? stableId)
	{
		if (string.IsNullOrWhiteSpace(stableId))
		{
			return false;
		}
		lock (_lock)
		{
			NormalizeDefaultOutputDevices(_config);
			return _config.DefaultOutputDevices.Any((DefaultOutputDeviceRecord d) => AirPlayDeviceIdentity.IdsEqual(d.StableId, stableId));
		}
	}

	public static void SetDefaultOutput(DefaultOutputDeviceRecord record, bool isDefault)
	{
		if (record == null || string.IsNullOrWhiteSpace(record.StableId))
		{
			return;
		}
		lock (_lock)
		{
			NormalizeDefaultOutputDevices(_config);
			string id = record.StableId.Trim();
			int num = _config.DefaultOutputDevices.FindIndex((DefaultOutputDeviceRecord d) => AirPlayDeviceIdentity.IdsEqual(d.StableId, id));
			if (isDefault)
			{
				DefaultOutputDeviceRecord defaultOutputDeviceRecord = new DefaultOutputDeviceRecord
				{
					StableId = id,
					DisplayName = (record.DisplayName ?? string.Empty),
					Model = (record.Model ?? string.Empty),
					LastKnownIp = (record.LastKnownIp ?? string.Empty),
					GroupKey = record.GroupKey
				};
				if (num >= 0)
				{
					_config.DefaultOutputDevices[num] = defaultOutputDeviceRecord;
				}
				else
				{
					_config.DefaultOutputDevices.Add(defaultOutputDeviceRecord);
				}
			}
			else if (num >= 0)
			{
				_config.DefaultOutputDevices.RemoveAt(num);
			}
			Save();
		}
	}

	public static IReadOnlyList<DefaultOutputDeviceRecord> GetDefaultOutputDevices()
	{
		lock (_lock)
		{
			NormalizeDefaultOutputDevices(_config);
			return _config.DefaultOutputDevices.Select((DefaultOutputDeviceRecord d) => new DefaultOutputDeviceRecord
			{
				StableId = d.StableId,
				DisplayName = d.DisplayName,
				Model = d.Model,
				LastKnownIp = d.LastKnownIp,
				GroupKey = d.GroupKey
			}).ToList();
		}
	}

	public static void TouchDefaultOutputHint(string stableId, string? displayName, string? lastIp, string? model)
	{
		if (string.IsNullOrWhiteSpace(stableId))
		{
			return;
		}
		lock (_lock)
		{
			NormalizeDefaultOutputDevices(_config);
			DefaultOutputDeviceRecord defaultOutputDeviceRecord = _config.DefaultOutputDevices.FirstOrDefault((DefaultOutputDeviceRecord d) => AirPlayDeviceIdentity.IdsEqual(d.StableId, stableId));
			if (defaultOutputDeviceRecord != null)
			{
				if (!string.IsNullOrWhiteSpace(displayName))
				{
					defaultOutputDeviceRecord.DisplayName = displayName.Trim();
				}
				if (!string.IsNullOrWhiteSpace(lastIp))
				{
					defaultOutputDeviceRecord.LastKnownIp = lastIp.Trim();
				}
				if (!string.IsNullOrWhiteSpace(model))
				{
					defaultOutputDeviceRecord.Model = model.Trim();
				}
				Save();
			}
		}
	}

	public static void NormalizeAppConfigBounds(AppConfig config)
	{
		if (config != null)
		{
			config.MasterVolume = Math.Clamp(config.MasterVolume, 0, 100);
			config.DefaultWindowModeIndex = Math.Clamp(config.DefaultWindowModeIndex, 0, 2);
			config.DockPlacementIndex = Math.Clamp(config.DockPlacementIndex, 0, 1);
			config.StreamingModeIndex = Math.Clamp(config.StreamingModeIndex, 0, 3);
			config.CustomBufferSize = Math.Clamp(config.CustomBufferSize, 0, 1000);
			config.CaptureSourceIndex = Math.Clamp(config.CaptureSourceIndex, 0, 2);
			config.SilenceTimeoutMinutes = Math.Clamp(config.SilenceTimeoutMinutes, 1, 1440);
			config.DevSoloLatencyMs = ClampDevOverride(config.DevSoloLatencyMs);
			config.DevGroupLatencyMs = ClampDevOverride(config.DevGroupLatencyMs);
			config.DevMixedLatencyMs = ClampDevOverride(config.DevMixedLatencyMs);
			config.OnboardingCompletedVersion = Math.Max(0, config.OnboardingCompletedVersion);
			config.AcousticOffsetMs = AcousticOffsetConfig.Clamp(config.AcousticOffsetMs);
			if (double.IsNaN(config.SilenceThresholdDb) || double.IsInfinity(config.SilenceThresholdDb))
			{
				config.SilenceThresholdDb = -60.0;
			}
			else
			{
				config.SilenceThresholdDb = Math.Clamp(config.SilenceThresholdDb, -120.0, 0.0);
			}
			if (config.SelectedProcessId < 0)
			{
				config.SelectedProcessId = 0;
			}
		}
	}

	private static int? ClampDevOverride(int? value)
	{
		if (!value.HasValue)
		{
			return null;
		}
		return Math.Clamp(value.Value, 0, 1000);
	}

	public static void NormalizeLatencyCalibrationProfiles(AppConfig config)
	{
		if (config == null)
		{
			return;
		}
		if (config.LatencyCalibrationProfiles == null)
		{
			List<LatencyCalibrationProfile> list = (config.LatencyCalibrationProfiles = new List<LatencyCalibrationProfile>());
		}
		config.LatencyCalibrationProfiles = (from profile in (from @group in config.LatencyCalibrationProfiles.Where((LatencyCalibrationProfile profile) => profile != null && !string.IsNullOrWhiteSpace(profile.ProfileKey)).GroupBy<LatencyCalibrationProfile, string>((LatencyCalibrationProfile profile) => profile.ProfileKey.Trim(), StringComparer.OrdinalIgnoreCase)
				select @group.OrderByDescending((LatencyCalibrationProfile profile) => profile.TestedAtUtc).First()).Select(delegate(LatencyCalibrationProfile profile)
			{
				profile.ProfileKey = profile.ProfileKey.Trim();
				LatencyCalibrationProfile latencyCalibrationProfile = profile;
				if (latencyCalibrationProfile.DeviceStableIds == null)
				{
					List<string> list3 = (latencyCalibrationProfile.DeviceStableIds = new List<string>());
				}
				latencyCalibrationProfile = profile;
				if (latencyCalibrationProfile.DeviceModels == null)
				{
					List<string> list3 = (latencyCalibrationProfile.DeviceModels = new List<string>());
				}
				profile.RecommendedLatencyMs = ClampDevOverride(profile.RecommendedLatencyMs);
				profile.UserLatencyMs = ClampDevOverride(profile.UserLatencyMs);
				profile.LowestConfirmedStableMs = Math.Clamp(profile.LowestConfirmedStableMs, 0, 1000);
				if (profile.AudioVideoOffsetMs != 0)
				{
					latencyCalibrationProfile = profile;
					int? archivedAudioVideoOffsetMs = latencyCalibrationProfile.ArchivedAudioVideoOffsetMs;
					int valueOrDefault = archivedAudioVideoOffsetMs.GetValueOrDefault();
					if (!archivedAudioVideoOffsetMs.HasValue)
					{
						valueOrDefault = profile.AudioVideoOffsetMs;
						LatencyCalibrationProfile latencyCalibrationProfile2 = latencyCalibrationProfile;
						int? archivedAudioVideoOffsetMs2 = valueOrDefault;
						latencyCalibrationProfile2.ArchivedAudioVideoOffsetMs = archivedAudioVideoOffsetMs2;
					}
				}
				if (profile.AudioVideoOffsetUserMs != 0)
				{
					latencyCalibrationProfile = profile;
					int? archivedAudioVideoOffsetMs = latencyCalibrationProfile.ArchivedAudioVideoOffsetUserMs;
					int valueOrDefault = archivedAudioVideoOffsetMs.GetValueOrDefault();
					if (!archivedAudioVideoOffsetMs.HasValue)
					{
						valueOrDefault = profile.AudioVideoOffsetUserMs;
						LatencyCalibrationProfile latencyCalibrationProfile3 = latencyCalibrationProfile;
						int? archivedAudioVideoOffsetMs2 = valueOrDefault;
						latencyCalibrationProfile3.ArchivedAudioVideoOffsetUserMs = archivedAudioVideoOffsetMs2;
					}
				}
				if (profile.AudioVideoOffsetEffectiveMs != 0)
				{
					latencyCalibrationProfile = profile;
					int? archivedAudioVideoOffsetMs = latencyCalibrationProfile.ArchivedAudioVideoOffsetEffectiveMs;
					int valueOrDefault = archivedAudioVideoOffsetMs.GetValueOrDefault();
					if (!archivedAudioVideoOffsetMs.HasValue)
					{
						valueOrDefault = profile.AudioVideoOffsetEffectiveMs;
						LatencyCalibrationProfile latencyCalibrationProfile4 = latencyCalibrationProfile;
						int? archivedAudioVideoOffsetMs2 = valueOrDefault;
						latencyCalibrationProfile4.ArchivedAudioVideoOffsetEffectiveMs = archivedAudioVideoOffsetMs2;
					}
				}
				profile.AudioVideoOffsetMs = 0;
				profile.AudioVideoOffsetUserMs = 0;
				profile.AudioVideoOffsetEffectiveMs = 0;
				return profile;
			})
			orderby profile.TestedAtUtc descending
			select profile).Take(64).ToList();
	}

	private static bool SameLatencyProfileIdentity(LatencyCalibrationProfile left, LatencyCalibrationProfile right)
	{
		return string.Equals(left.ProfileKey.Trim(), right.ProfileKey.Trim(), StringComparison.OrdinalIgnoreCase);
	}

	public static bool SaveLatencyCalibrationProfile(LatencyCalibrationProfile profile)
	{
		if (profile == null || string.IsNullOrWhiteSpace(profile.ProfileKey))
		{
			return false;
		}
		bool result;
		lock (_persistLock)
		{
			if (!ReconcileUnreadPrimary(out Exception failure))
			{
				lock (_lock)
				{
					RecordSaveFailureUnlocked(failure ?? new IOException("settings file unreadable"));
				}
				result = false;
			}
			else
			{
				lock (_lock)
				{
					AppConfig appConfig;
					try
					{
						appConfig = CloneConfig(_config);
						LatencyCalibrationProfile candidateProfile = CloneProfile(profile);
						LatencyCalibrationProfile latencyCalibrationProfile = candidateProfile;
						if (latencyCalibrationProfile.Ssid == null)
						{
							string text = (latencyCalibrationProfile.Ssid = candidateProfile.NetworkKey?.Ssid);
						}
						NormalizeLatencyCalibrationProfiles(appConfig);
						appConfig.LatencyCalibrationProfiles.RemoveAll((LatencyCalibrationProfile existing) => SameLatencyProfileIdentity(existing, candidateProfile));
						appConfig.LatencyCalibrationProfiles.Add(candidateProfile);
						NormalizeLatencyCalibrationProfiles(appConfig);
					}
					catch (Exception)
					{
						result = false;
						goto end_IL_006e;
					}
					try
					{
						string json = SerializeConfig(appConfig);
						string directoryName = Path.GetDirectoryName(ConfigPath);
						if (!string.IsNullOrWhiteSpace(directoryName))
						{
							Directory.CreateDirectory(directoryName);
						}
						WriteConfigWithRetry(json);
						_config = appConfig;
						_savePending = false;
						RecordSaveSuccessUnlocked();
						result = true;
					}
					catch (Exception ex2)
					{
						RecordSaveFailureUnlocked(ex2);
						result = false;
					}
					end_IL_006e:;
				}
			}
		}
		return result;
	}

	private static AppConfig CloneConfig(AppConfig config)
	{
		return JsonSerializer.Deserialize<AppConfig>(SerializeConfig(config)) ?? new AppConfig();
	}

	private static LatencyCalibrationProfile CloneProfile(LatencyCalibrationProfile profile)
	{
		return JsonSerializer.Deserialize<LatencyCalibrationProfile>(JsonSerializer.Serialize(profile)) ?? new LatencyCalibrationProfile();
	}

	private static string SerializeConfig(AppConfig config)
	{
		return JsonSerializer.Serialize(config, new JsonSerializerOptions
		{
			WriteIndented = true
		});
	}

	public static int? GetSavedLatencyForDevices(IReadOnlyCollection<CalibrationDeviceIdentity> devices, string? preferredCodec = null)
	{
		lock (_lock)
		{
			return LatencyCalibrationPolicy.ResolveSavedLatency(_config.LatencyCalibrationProfiles, devices, preferredCodec);
		}
	}

	public static void NormalizeEqConfig(AppConfig config)
	{
		if (config != null)
		{
			config.EqBandGains = EqLayout.NormalizeGains(config.EqBandGains).ToList();
			config.EqPreampDb = EqLayout.NormalizePreampDb(config.EqPreampDb);
			config.EqUserPresets = NormalizeUserPresets(config.EqUserPresets);
			config.EqPresetId = NormalizeEqPresetId(config.EqPresetId, config.EqUserPresets);
		}
	}

	public static double SetEqBandGain(int index, double gainDb)
	{
		lock (_lock)
		{
			NormalizeEqConfig(_config);
			double num = Math.Clamp(gainDb, -12.0, 12.0);
			if (index >= 0 && index < _config.EqBandGains.Count)
			{
				_config.EqBandGains[index] = num;
			}
			_config.EqPresetId = "custom";
			return num;
		}
	}

	public static void SetEqBandGains(IReadOnlyList<double> gains, string presetId)
	{
		lock (_lock)
		{
			NormalizeEqConfig(_config);
			_config.EqBandGains = EqLayout.NormalizeGains(gains).ToList();
			_config.EqPresetId = NormalizeEqPresetId(presetId, _config.EqUserPresets);
		}
	}

	public static void NormalizeAudioCodecConfig(AppConfig config)
	{
		if (config != null)
		{
			config.AudioCodec = AudioCodecPreference.Normalize(config.AudioCodec);
		}
	}

	public static List<EqUserPresetConfig> NormalizeUserPresets(IEnumerable<EqUserPresetConfig>? presets)
	{
		List<EqUserPresetConfig> list = new List<EqUserPresetConfig>();
		if (presets == null)
		{
			return list;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (EqUserPresetConfig preset in presets)
		{
			if (preset == null)
			{
				continue;
			}
			string text = EqLayout.SanitizePresetName(preset.Name);
			if (!string.IsNullOrEmpty(text) && hashSet.Add(text))
			{
				list.Add(new EqUserPresetConfig
				{
					Name = text,
					Gains = EqLayout.NormalizeGains(preset.Gains).ToList()
				});
				if (list.Count >= 24)
				{
					break;
				}
			}
		}
		return list;
	}

	public static string NormalizeEqPresetId(string? id, IEnumerable<EqUserPresetConfig>? userPresets = null)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return "builtin:flat";
		}
		id = id.Trim();
		if (id.Equals("custom", StringComparison.OrdinalIgnoreCase))
		{
			return "custom";
		}
		if (EqPresetItem.TryParseBuiltInId(id, out string builtinId))
		{
			if (EqBuiltInPreset.Find(builtinId) == null)
			{
				return "custom";
			}
			return EqPresetItem.BuiltInId(builtinId);
		}
		if (EqPresetItem.TryParseUserId(id, out string userName))
		{
			userName = EqLayout.SanitizePresetName(userName);
			if (string.IsNullOrEmpty(userName))
			{
				return "custom";
			}
			if (userPresets != null && !userPresets.Any((EqUserPresetConfig p) => p != null && string.Equals(EqLayout.SanitizePresetName(p.Name), userName, StringComparison.OrdinalIgnoreCase)))
			{
				return "custom";
			}
			return EqPresetItem.UserId(userName);
		}
		if (EqBuiltInPreset.Find(id) != null)
		{
			return EqPresetItem.BuiltInId(id);
		}
		return "custom";
	}

	public static void SaveUserPreset(string name, IReadOnlyList<double> gains)
	{
		lock (_lock)
		{
			name = EqLayout.SanitizePresetName(name);
			if (string.IsNullOrEmpty(name))
			{
				return;
			}
			NormalizeEqConfig(_config);
			List<double> list = EqLayout.NormalizeGains(gains).ToList();
			EqUserPresetConfig eqUserPresetConfig = _config.EqUserPresets.FirstOrDefault((EqUserPresetConfig p) => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
			if (eqUserPresetConfig != null)
			{
				eqUserPresetConfig.Name = name;
				eqUserPresetConfig.Gains = list;
			}
			else
			{
				if (_config.EqUserPresets.Count >= 24)
				{
					_config.EqUserPresets.RemoveAt(0);
				}
				_config.EqUserPresets.Add(new EqUserPresetConfig
				{
					Name = name,
					Gains = list
				});
			}
			_config.EqPresetId = EqPresetItem.UserId(name);
			_config.EqBandGains = list;
			NormalizeEqConfig(_config);
			Save();
		}
	}

	public static bool DeleteUserPreset(string name)
	{
		lock (_lock)
		{
			name = EqLayout.SanitizePresetName(name);
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			if (_config.EqUserPresets.RemoveAll((EqUserPresetConfig p) => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
			{
				return false;
			}
			string eqPresetId = EqPreset.MatchId(_config.EqBandGains, _config.EqUserPresets);
			_config.EqPresetId = eqPresetId;
			NormalizeEqConfig(_config);
			Save();
			return true;
		}
	}

	public static double[] GetNormalizedEqBandGains()
	{
		lock (_lock)
		{
			NormalizeEqConfig(_config);
			return _config.EqBandGains.ToArray();
		}
	}

	public static void SetEqConfig(bool enabled, IReadOnlyList<double> bandGains)
	{
		lock (_lock)
		{
			_config.EqEnabled = enabled;
			_config.EqBandGains = ((bandGains != null) ? bandGains.ToList() : AppConfig.CreateDefaultEqBandGains());
			NormalizeEqConfig(_config);
			Save();
		}
	}

	public static void Save()
	{
		lock (_lock)
		{
			RequestSaveUnlocked();
		}
	}

	private static void RequestSaveUnlocked()
	{
		_savePending = true;
		if (_saveDebounceTimer == null)
		{
			_saveDebounceTimer = new Timer(delegate
			{
				FlushPendingSave();
			}, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		}
		_saveDebounceTimer.Change(SaveDebounceInterval, Timeout.InfiniteTimeSpan);
	}

	public static IReadOnlyList<SenderGroupRecord> GetSenderGroups()
	{
		lock (_lock)
		{
			return (from g in _config.SenderGroups
				where g != null && g.MemberStableIds != null
				select new SenderGroupRecord
				{
					Name = (g.Name ?? string.Empty),
					IsAppleSeeded = g.IsAppleSeeded,
					MemberStableIds = g.MemberStableIds.Where((string id) => !string.IsNullOrWhiteSpace(id)).ToList()
				}).ToList();
		}
	}

	public static void SaveSenderGroups(IEnumerable<SenderGroupRecord> groups)
	{
		lock (_lock)
		{
			_config.SenderGroups = (from g in groups.Where(delegate(SenderGroupRecord g)
				{
					if (g != null)
					{
						List<string> memberStableIds = g.MemberStableIds;
						if (memberStableIds != null)
						{
							return memberStableIds.Count >= 2;
						}
						return false;
					}
					return false;
				})
				select new SenderGroupRecord
				{
					Name = (g.Name ?? string.Empty),
					IsAppleSeeded = g.IsAppleSeeded,
					MemberStableIds = g.MemberStableIds.Where((string id) => !string.IsNullOrWhiteSpace(id)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList()
				}).ToList();
			Save();
		}
	}

	public static IReadOnlyList<string> GetDismissedAppleGroupKeys()
	{
		lock (_lock)
		{
			return _config.DismissedAppleGroupKeys.Where((string key) => !string.IsNullOrWhiteSpace(key)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		}
	}

	public static void SaveDismissedAppleGroupKeys(IEnumerable<string> keys)
	{
		lock (_lock)
		{
			_config.DismissedAppleGroupKeys = (from key in keys
				where !string.IsNullOrWhiteSpace(key)
				select key.Trim()).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
			Save();
		}
	}

	public static void SaveNow()
	{
		lock (_lock)
		{
			_savePending = true;
		}
		FlushPendingSave();
	}

	public static void FlushPendingSave()
	{
		lock (_persistLock)
		{
			lock (_lock)
			{
				if (!_savePending)
				{
					return;
				}
			}
			if (!ReconcileUnreadPrimary(out Exception failure))
			{
				lock (_lock)
				{
					RecordSaveFailureUnlocked(failure ?? new IOException("settings file unreadable"));
					if (_consecutiveSaveFailures <= 5)
					{
						ScheduleDeferredSaveUnlocked(DeferredSaveRetryDelay(_consecutiveSaveFailures));
					}
					return;
				}
			}
			string json;
			lock (_lock)
			{
				if (!_savePending)
				{
					return;
				}
				try
				{
					NormalizeEqConfig(_config);
					NormalizeAudioCodecConfig(_config);
					NormalizeAppConfigBounds(_config);
					NormalizeDefaultOutputDevices(_config);
					NormalizeLatencyCalibrationProfiles(_config);
					json = SerializeConfig(_config);
					_savePending = false;
				}
				catch (Exception)
				{
					_savePending = true;
					return;
				}
			}
			try
			{
				string directoryName = Path.GetDirectoryName(ConfigPath);
				if (!string.IsNullOrWhiteSpace(directoryName))
				{
					Directory.CreateDirectory(directoryName);
				}
				WriteConfigWithRetry(json);
				lock (_lock)
				{
					RecordSaveSuccessUnlocked();
				}
			}
			catch (Exception ex2)
			{
				lock (_lock)
				{
					_savePending = true;
					RecordSaveFailureUnlocked(ex2);
					if (_consecutiveSaveFailures <= 5)
					{
						ScheduleDeferredSaveUnlocked(DeferredSaveRetryDelay(_consecutiveSaveFailures));
					}
				}
			}
		}
	}

	internal static TimeSpan DeferredSaveRetryDelay(int failures)
	{
		int num = Math.Clamp(failures - 1, 0, 8);
		TimeSpan timeSpan = TimeSpan.FromTicks(DeferredSaveRetryBase.Ticks << num);
		if (!(timeSpan > DeferredSaveRetryMax))
		{
			return timeSpan;
		}
		return DeferredSaveRetryMax;
	}

	private static void ScheduleDeferredSaveUnlocked(TimeSpan delay)
	{
		if (_saveDebounceTimer == null)
		{
			_saveDebounceTimer = new Timer(delegate
			{
				FlushPendingSave();
			}, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		}
		_saveDebounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
	}

	private static void RecordSaveSuccessUnlocked()
	{
		_consecutiveSaveFailures = 0;
		_lastSaveSuccessUtc = DateTime.UtcNow;
	}

	private static void RecordSaveFailureUnlocked(Exception ex)
	{
		_consecutiveSaveFailures++;
		_lastSaveFailureUtc = DateTime.UtcNow;
		_lastSaveFailure = DescribePersistenceError(ex);
	}

	internal static string DescribePersistenceError(Exception ex)
	{
		return $"{ex.GetType().Name} HResult=0x{ex.HResult:X8} Win32={ex.HResult & 0xFFFF}";
	}

	public static string GetPersistenceDiagnostics()
	{
		lock (_lock)
		{
			string value = _lastSaveSuccessUtc?.ToString("O") ?? "none this session";
			string value2 = ((_unreadPrimaryBaseJson != null) ? "; settings file unreadable since startup (protected, changes merged once readable)" : string.Empty);
			if (_lastSaveFailureUtc.HasValue)
			{
				return $"last failure {_lastSaveFailureUtc.Value:O}: {_lastSaveFailure}; consecutive failures={_consecutiveSaveFailures}; pending={_savePending}; last save: {value}{value2}";
			}
			return $"ok (last save: {value}; pending={_savePending}){value2}";
		}
	}

	private static void WriteConfigWithRetry(string json)
	{
		int num = 0;
		while (true)
		{
			try
			{
				WriteConfigAtomically(json);
				break;
			}
			catch (IOException) when (num < WriteRetryDelays.Length)
			{
				Thread.Sleep(WriteRetryDelays[num]);
			}
			num++;
		}
	}

	private static void WriteConfigAtomically(string json)
	{
		string text = ConfigPath + ".tmp";
		try
		{
			using (FileStream fileStream = new FileStream(text, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			{
				using StreamWriter streamWriter = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				streamWriter.Write(json);
				streamWriter.Flush();
				fileStream.Flush(flushToDisk: true);
			}
			if (File.Exists(ConfigPath))
			{
				string text2 = ConfigPath + ".bak";
				ClearReadOnly(ConfigPath);
				ClearReadOnly(text2);
				try
				{
					File.Replace(text, ConfigPath, text2, ignoreMetadataErrors: true);
					return;
				}
				catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException) ? 1 : 0) != 0)
				{
					try
					{
						if (File.Exists(ConfigPath))
						{
							File.Copy(ConfigPath, text2, overwrite: true);
						}
					}
					catch (Exception)
					{
					}
					File.Move(text, ConfigPath, overwrite: true);
					return;
				}
			}
			File.Move(text, ConfigPath, overwrite: true);
		}
		finally
		{
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch
			{
			}
		}
	}

	private static void ClearReadOnly(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				FileAttributes attributes = File.GetAttributes(path);
				if ((attributes & FileAttributes.ReadOnly) != FileAttributes.None)
				{
					File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
				}
			}
		}
		catch (Exception)
		{
		}
	}

	public static string GetAudioCodec()
	{
		lock (_lock)
		{
			NormalizeAudioCodecConfig(_config);
			return _config.AudioCodec;
		}
	}

	public static void SetAudioCodec(string codec)
	{
		lock (_lock)
		{
			_config.AudioCodec = AudioCodecPreference.Normalize(codec);
		}
		SaveNow();
	}

	public static DeviceConfig GetDeviceConfig(string ip)
	{
		lock (_lock)
		{
			if (_config.Devices.TryGetValue(ip, out DeviceConfig value))
			{
				return new DeviceConfig
				{
					Volume = value.Volume,
					EqPresetId = value.EqPresetId,
					EqPreampDb = value.EqPreampDb,
					EqBandGains = value.EqBandGains?.ToList()
				};
			}
			return new DeviceConfig();
		}
	}

	public static void SetDeviceConfig(string ip, double volume)
	{
		lock (_lock)
		{
			if (!_config.Devices.ContainsKey(ip))
			{
				_config.Devices[ip] = new DeviceConfig();
			}
			_config.Devices[ip].Volume = volume;
			Save();
		}
	}

	public static void RememberDeviceEq(string ip, string presetId, double preampDb, IReadOnlyList<double> bandGains)
	{
		if (string.IsNullOrWhiteSpace(ip))
		{
			return;
		}
		lock (_lock)
		{
			NormalizeEqConfig(_config);
			if (!_config.Devices.TryGetValue(ip, out DeviceConfig value) || value == null)
			{
				value = new DeviceConfig();
				_config.Devices[ip] = value;
			}
			value.EqPresetId = NormalizeEqPresetId(presetId, _config.EqUserPresets);
			value.EqPreampDb = EqLayout.NormalizePreampDb(preampDb);
			value.EqBandGains = EqLayout.NormalizeGains(bandGains).ToList();
			Save();
		}
	}

	public static DeviceEqMemorySnapshot ResolveDeviceEqMemory(string ip)
	{
		lock (_lock)
		{
			NormalizeEqConfig(_config);
			if (string.IsNullOrWhiteSpace(ip) || !_config.Devices.TryGetValue(ip, out DeviceConfig value) || value == null)
			{
				return new DeviceEqMemorySnapshot
				{
					HadStoredMemory = false,
					UsedFallback = true,
					FallbackReason = "no_device_memory",
					PresetId = "builtin:flat",
					PreampDb = 0.0,
					BandGains = EqLayout.NormalizeGains(null)
				};
			}
			if (string.IsNullOrWhiteSpace(value.EqPresetId) && !value.EqPreampDb.HasValue && (value.EqBandGains == null || value.EqBandGains.Count <= 0))
			{
				return new DeviceEqMemorySnapshot
				{
					HadStoredMemory = false,
					UsedFallback = true,
					FallbackReason = "empty_device_memory",
					PresetId = "builtin:flat",
					PreampDb = 0.0,
					BandGains = EqLayout.NormalizeGains(null)
				};
			}
			double num = EqLayout.NormalizePreampDb(value.EqPreampDb.GetValueOrDefault());
			double[] array = EqLayout.NormalizeGains(value.EqBandGains);
			string text = NormalizeEqPresetId(value.EqPresetId ?? "custom", _config.EqUserPresets);
			bool flag = false;
			string fallbackReason = string.Empty;
			double[] array2;
			string userName;
			if (EqPresetItem.TryParseBuiltInId(text, out string builtinId))
			{
				EqBuiltInPreset eqBuiltInPreset = EqBuiltInPreset.Find(builtinId);
				if (eqBuiltInPreset != null)
				{
					array2 = eqBuiltInPreset.GainsDb.ToArray();
				}
				else
				{
					array2 = array;
					text = "custom";
					flag = true;
					fallbackReason = "unknown_builtin";
				}
			}
			else if (EqPresetItem.TryParseUserId(text, out userName))
			{
				EqUserPresetConfig eqUserPresetConfig = _config.EqUserPresets.FirstOrDefault((EqUserPresetConfig p) => string.Equals(p.Name, userName, StringComparison.OrdinalIgnoreCase));
				if (eqUserPresetConfig != null)
				{
					array2 = EqLayout.NormalizeGains(eqUserPresetConfig.Gains);
				}
				else if (value.EqBandGains != null && value.EqBandGains.Count > 0)
				{
					array2 = array;
					text = "custom";
					flag = true;
					fallbackReason = "missing_user_preset_using_stored_gains";
				}
				else
				{
					array2 = EqLayout.NormalizeGains(null);
					text = "builtin:flat";
					flag = true;
					fallbackReason = "missing_user_preset";
				}
			}
			else
			{
				array2 = ((value.EqBandGains != null && value.EqBandGains.Count > 0) ? array : EqLayout.NormalizeGains(null));
				text = "custom";
				if (value.EqBandGains == null || value.EqBandGains.Count == 0)
				{
					flag = true;
					fallbackReason = "custom_without_gains";
					array2 = EqLayout.NormalizeGains(null);
					text = "builtin:flat";
				}
			}
			if (flag)
			{
				value.EqPresetId = text;
				value.EqBandGains = array2.ToList();
				value.EqPreampDb = num;
				Save();
			}
			return new DeviceEqMemorySnapshot
			{
				HadStoredMemory = true,
				UsedFallback = flag,
				FallbackReason = fallbackReason,
				PresetId = text,
				PreampDb = num,
				BandGains = array2
			};
		}
	}

	public static EqPresetImportResult ImportEqPresetsJson(string json)
	{
		if (!EqPresetTransfer.TryParsePackage(json, out EqPresetPackage package, out string error) || package == null)
		{
			return EqPresetImportResult.Fail(error);
		}
		lock (_lock)
		{
			NormalizeEqConfig(_config);
			List<EqUserPresetConfig> merged;
			EqPresetImportResult eqPresetImportResult = EqPresetTransfer.MergeUserPresets(_config.EqUserPresets, package, out merged);
			if (!eqPresetImportResult.Success)
			{
				return eqPresetImportResult;
			}
			_config.EqUserPresets = merged;
			_config.EqPresetId = NormalizeEqPresetId(_config.EqPresetId, _config.EqUserPresets);
			NormalizeEqConfig(_config);
			Save();
			return eqPresetImportResult;
		}
	}

	public static string ExportEqPresetsJson(bool includeBuiltIns = true)
	{
		lock (_lock)
		{
			NormalizeEqConfig(_config);
			return EqPresetTransfer.SerializePackage(EqPresetTransfer.BuildExportPackage(_config.EqUserPresets, includeBuiltIns));
		}
	}
}
