namespace AirStereo2.Services;

public sealed class CalibrationReportOptions
{
	public bool IncludeDeviceNames { get; set; } = true;

	public bool IncludeNetworkAddresses { get; set; } = true;

	public bool IncludeTechnicalMetrics { get; set; } = true;

	public bool IncludeBackendLog { get; set; } = true;
}
