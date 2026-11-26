namespace AirStereo2.Models;

public static class AcousticOffsetResolutionPolicy
{
	public enum AcousticOffsetSource
	{
		DefaultZero,
		CloudAcousticPrior,
		LocalAcousticMeasurement,
		UserExplicitSetting
	}

	public sealed record ResolvedAcousticOffset(int OffsetMs, AcousticOffsetSource Source, string Description);

	public static ResolvedAcousticOffset Resolve(int? userOffsetMs, DeviceQuirksEntry? localQuirks = null, LatencyKnowledgeEntry? cloudEntry = null)
	{
		return ResolveManualOnly(userOffsetMs);
	}

	public static ResolvedAcousticOffset ResolveManualOnly(int? userOffsetMs)
	{
		if (userOffsetMs.HasValue)
		{
			return new ResolvedAcousticOffset(AcousticOffsetConfig.Clamp(userOffsetMs.Value), AcousticOffsetSource.UserExplicitSetting, "UserExplicitSetting");
		}
		return new ResolvedAcousticOffset(0, AcousticOffsetSource.DefaultZero, "DefaultZero");
	}
}
