using System;
using AirStereo2.Services;

namespace AirStereo2.Models;

public sealed record LatencyRecommendation(double Fast, double Balanced, double Stable, double Floor, string Explain, bool SelfRetiming, bool RtxGapDerived = false, bool RtxOverflow = false)
{
	public bool IsFastBalancedCollapsed => Math.Abs(Fast - Balanced) < 0.001;

	public string GetFastDisplayLabel(string? customFormat = null)
	{
		if (!IsFastBalancedCollapsed)
		{
			return $"{Fast:0} ms";
		}
		return customFormat ?? $"{Fast:0} ms ({ExplainCollapse()})";
	}

	public string GetBalancedDisplayLabel()
	{
		return $"{Balanced:0} ms";
	}

	public string GetStableDisplayLabel()
	{
		return $"{Stable:0} ms";
	}

	private static string ExplainCollapse()
	{
		return LocalizationManager.Instance.GetString("Calibration_Collapse_FastBalanced", "与均衡推荐一致");
	}
}
