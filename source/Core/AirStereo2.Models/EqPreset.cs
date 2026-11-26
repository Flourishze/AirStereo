using System.Collections.Generic;

namespace AirStereo2.Models;

public static class EqPreset
{
	public static IReadOnlyList<EqBuiltInPreset> BuiltIn => EqBuiltInPreset.All;

	public static EqBuiltInPreset? Find(string? id)
	{
		return EqBuiltInPreset.Find(id);
	}

	public static string MatchId(IReadOnlyList<double> gains, IEnumerable<EqUserPresetConfig>? userPresets = null)
	{
		double[] a = EqLayout.NormalizeGains(gains);
		foreach (EqBuiltInPreset item in EqBuiltInPreset.All)
		{
			if (EqLayout.GainsEqual(a, item.GainsDb))
			{
				return EqPresetItem.BuiltInId(item.Id);
			}
		}
		if (userPresets != null)
		{
			foreach (EqUserPresetConfig userPreset in userPresets)
			{
				if (!string.IsNullOrWhiteSpace(userPreset?.Name))
				{
					double[] b = EqLayout.NormalizeGains(userPreset.Gains);
					if (EqLayout.GainsEqual(a, b))
					{
						return EqPresetItem.UserId(userPreset.Name);
					}
				}
			}
		}
		return "custom";
	}
}
