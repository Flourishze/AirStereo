using System.Collections.Generic;

namespace AirStereo2.Models;

public sealed class DeviceEqMemorySnapshot
{
	public string PresetId { get; init; } = "builtin:flat";

	public double PreampDb { get; init; }

	public IReadOnlyList<double> BandGains { get; init; } = EqLayout.NormalizeGains(null);

	public bool HadStoredMemory { get; init; }

	public bool UsedFallback { get; init; }

	public string FallbackReason { get; init; } = string.Empty;
}
