using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AirStereo2.Models;

public static class EqPresetTransfer
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	public static EqPresetPackage BuildExportPackage(IEnumerable<EqUserPresetConfig> userPresets, bool includeBuiltIns = true)
	{
		EqPresetPackage eqPresetPackage = new EqPresetPackage
		{
			Format = "airstereo2-eq-presets",
			SchemaVersion = 1,
			BandCount = 10,
			FrequenciesHz = EqLayout.FrequenciesHz.Select((double f) => f).ToList(),
			ExportedAt = DateTimeOffset.UtcNow.ToString("o"),
			Presets = new List<EqPresetPackageEntry>()
		};
		if (includeBuiltIns)
		{
			foreach (EqBuiltInPreset item in EqBuiltInPreset.All)
			{
				eqPresetPackage.Presets.Add(new EqPresetPackageEntry
				{
					Kind = "builtin",
					Id = item.Id,
					Name = item.DisplayName,
					GainsDb = item.GainsDb.ToList()
				});
			}
		}
		foreach (EqUserPresetConfig item2 in SettingsManagerNormalizeUsers(userPresets))
		{
			eqPresetPackage.Presets.Add(new EqPresetPackageEntry
			{
				Kind = "user",
				Name = item2.Name,
				GainsDb = EqLayout.NormalizeGains(item2.Gains).ToList()
			});
		}
		return eqPresetPackage;
	}

	public static string SerializePackage(EqPresetPackage package)
	{
		return JsonSerializer.Serialize(package, JsonOptions);
	}

	public static bool TryParsePackage(string json, out EqPresetPackage? package, out string error)
	{
		package = null;
		error = string.Empty;
		if (string.IsNullOrWhiteSpace(json))
		{
			error = "Import file is empty.";
			return false;
		}
		try
		{
			package = JsonSerializer.Deserialize<EqPresetPackage>(json, JsonOptions);
		}
		catch (Exception ex)
		{
			error = "Invalid JSON: " + ex.Message;
			package = null;
			return false;
		}
		if (package == null)
		{
			error = "Import file did not contain a preset package.";
			return false;
		}
		if (!string.Equals(package.Format, "airstereo2-eq-presets", StringComparison.OrdinalIgnoreCase))
		{
			error = $"Unsupported format '{package.Format}'. Expected '{"airstereo2-eq-presets"}'.";
			package = null;
			return false;
		}
		if (package.SchemaVersion != 1)
		{
			error = $"Unsupported schemaVersion {package.SchemaVersion}. Supported: {1}.";
			package = null;
			return false;
		}
		if (package.BandCount != 10)
		{
			error = $"Unsupported bandCount {package.BandCount}. Expected {10}.";
			package = null;
			return false;
		}
		if (package.FrequenciesHz != null && package.FrequenciesHz.Count > 0)
		{
			if (package.FrequenciesHz.Count != 10)
			{
				error = "frequenciesHz length does not match the 10-band layout.";
				package = null;
				return false;
			}
			for (int i = 0; i < 10; i++)
			{
				double num = EqLayout.FrequenciesHz[i];
				double num2 = package.FrequenciesHz[i];
				if (double.IsNaN(num2) || double.IsInfinity(num2) || Math.Abs(num2 - num) > 0.5)
				{
					error = $"frequenciesHz[{i}] does not match the fixed 10-band layout.";
					package = null;
					return false;
				}
			}
		}
		EqPresetPackage eqPresetPackage = package;
		if (eqPresetPackage.Presets == null)
		{
			List<EqPresetPackageEntry> list = (eqPresetPackage.Presets = new List<EqPresetPackageEntry>());
		}
		return true;
	}

	public static EqPresetImportResult MergeUserPresets(IReadOnlyList<EqUserPresetConfig> existing, EqPresetPackage package, out List<EqUserPresetConfig> merged)
	{
		merged = SettingsManagerNormalizeUsers(existing);
		if (package.Presets == null || package.Presets.Count == 0)
		{
			return EqPresetImportResult.Ok(0, 0, 0, "No presets found in file.");
		}
		HashSet<string> hashSet = new HashSet<string>(merged.Select((EqUserPresetConfig p) => p.Name), StringComparer.OrdinalIgnoreCase);
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		foreach (EqPresetPackageEntry preset in package.Presets)
		{
			if (preset == null)
			{
				num2++;
				continue;
			}
			string text = (preset.Kind ?? "user").Trim().ToLowerInvariant();
			if (text == "builtin")
			{
				num2++;
				continue;
			}
			if (text != "user")
			{
				num2++;
				continue;
			}
			string text2 = EqLayout.SanitizePresetName(preset.Name);
			if (string.IsNullOrEmpty(text2))
			{
				num2++;
				continue;
			}
			if (preset.GainsDb == null || preset.GainsDb.Count == 0)
			{
				num2++;
				continue;
			}
			List<double> gains = EqLayout.NormalizeGains(preset.GainsDb).ToList();
			string text3 = text2;
			if (hashSet.Contains(text3))
			{
				text3 = AllocateImportName(text2, hashSet);
				if (string.IsNullOrEmpty(text3))
				{
					num2++;
					continue;
				}
				num3++;
			}
			if (merged.Count >= 24)
			{
				num2++;
				continue;
			}
			merged.Add(new EqUserPresetConfig
			{
				Name = text3,
				Gains = gains
			});
			hashSet.Add(text3);
			num++;
		}
		string message = ((num != 0) ? $"Imported {num} user preset(s)." : ((num2 > 0) ? "No new user presets imported (built-ins ignored or names full)." : "No user presets imported."));
		return EqPresetImportResult.Ok(num, num2, num3, message);
	}

	private static string AllocateImportName(string baseName, HashSet<string> existing)
	{
		for (int i = 1; i <= 24; i++)
		{
			string text = ((i == 1) ? " (import)" : $" (import {i})");
			string text2 = baseName;
			int num = 40 - text.Length;
			if (num < 1)
			{
				return string.Empty;
			}
			if (text2.Length > num)
			{
				text2 = text2.Substring(0, num).Trim();
			}
			text2 += text;
			if (!existing.Contains(text2))
			{
				return text2;
			}
		}
		return string.Empty;
	}

	private static List<EqUserPresetConfig> SettingsManagerNormalizeUsers(IEnumerable<EqUserPresetConfig>? users)
	{
		List<EqUserPresetConfig> list = new List<EqUserPresetConfig>();
		if (users == null)
		{
			return list;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (EqUserPresetConfig user in users)
		{
			if (user == null)
			{
				continue;
			}
			string text = EqLayout.SanitizePresetName(user.Name);
			if (!string.IsNullOrEmpty(text) && hashSet.Add(text))
			{
				list.Add(new EqUserPresetConfig
				{
					Name = text,
					Gains = EqLayout.NormalizeGains(user.Gains).ToList()
				});
				if (list.Count >= 24)
				{
					break;
				}
			}
		}
		return list;
	}
}
