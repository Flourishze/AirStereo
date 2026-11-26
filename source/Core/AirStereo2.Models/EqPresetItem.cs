using System;
using System.Collections.Generic;

namespace AirStereo2.Models;

public sealed class EqPresetItem
{
	public string Id { get; }

	public string DisplayName { get; }

	public IReadOnlyList<double> GainsDb { get; }

	public bool IsBuiltIn { get; }

	public bool IsUser { get; }

	public bool IsCustom
	{
		get
		{
			if (!IsBuiltIn)
			{
				return !IsUser;
			}
			return false;
		}
	}

	private EqPresetItem(string id, string displayName, IReadOnlyList<double> gainsDb, bool isBuiltIn, bool isUser)
	{
		Id = id;
		DisplayName = displayName;
		GainsDb = gainsDb;
		IsBuiltIn = isBuiltIn;
		IsUser = isUser;
	}

	public static EqPresetItem FromBuiltIn(EqBuiltInPreset preset, string? displayName = null)
	{
		return new EqPresetItem("builtin:" + preset.Id, displayName ?? preset.DisplayName, preset.GainsDb, isBuiltIn: true, isUser: false);
	}

	public static EqPresetItem FromUser(string name, IReadOnlyList<double> gains)
	{
		return new EqPresetItem("user:" + name, name, EqLayout.NormalizeGains(gains), isBuiltIn: false, isUser: true);
	}

	public static EqPresetItem Custom(string displayName = "Custom")
	{
		return new EqPresetItem("custom", displayName, EqLayout.NormalizeGains(null), isBuiltIn: false, isUser: false);
	}

	public static string BuiltInId(string builtinId)
	{
		return "builtin:" + builtinId;
	}

	public static string UserId(string name)
	{
		return "user:" + name;
	}

	public static bool TryParseBuiltInId(string? id, out string builtinId)
	{
		builtinId = string.Empty;
		if (string.IsNullOrEmpty(id) || !id.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		builtinId = id.Substring("builtin:".Length);
		return builtinId.Length > 0;
	}

	public static bool TryParseUserId(string? id, out string name)
	{
		name = string.Empty;
		if (string.IsNullOrEmpty(id) || !id.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		name = id.Substring("user:".Length);
		return name.Length > 0;
	}
}
