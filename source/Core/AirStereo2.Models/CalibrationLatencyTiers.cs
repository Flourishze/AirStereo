namespace AirStereo2.Models;

public static class CalibrationLatencyTiers
{
	public const int LowMaxMs = 60;

	public const int BalancedMaxMs = 120;

	public static CalibrationLatencyTier ForPlayoutMs(int playoutMs)
	{
		if (playoutMs > 60)
		{
			if (playoutMs <= 120)
			{
				return CalibrationLatencyTier.Balanced;
			}
			return CalibrationLatencyTier.Stable;
		}
		return CalibrationLatencyTier.Low;
	}

	public static string ResourceKey(CalibrationLatencyTier tier)
	{
		return tier switch
		{
			CalibrationLatencyTier.Low => "Calibration_Tier_Low", 
			CalibrationLatencyTier.Balanced => "Calibration_Tier_Balanced", 
			_ => "Calibration_Tier_Stable", 
		};
	}
}
