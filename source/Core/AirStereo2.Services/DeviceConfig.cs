using System.Collections.Generic;

namespace AirStereo2.Services;

public class DeviceConfig
{
	public double Volume { get; set; } = 50.0;

	public string? EqPresetId { get; set; }

	public double? EqPreampDb { get; set; }

	public List<double>? EqBandGains { get; set; }
}
