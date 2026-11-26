using System;
using System.Collections.Generic;

namespace AirStereo2.Models;

public sealed class DeviceQuirksEntry
{
	public string Fingerprint { get; set; } = string.Empty;

	public string Model { get; set; } = string.Empty;

	public string Manufacturer { get; set; } = string.Empty;

	public string SrcVers { get; set; } = string.Empty;

	public int? KClampMs { get; set; }

	public int? TDev { get; set; }

	public bool SelfRetiming { get; set; }

	public int AcousticOffsetPrior { get; set; }

	public bool? TvDependent { get; set; }

	public string Source { get; set; } = "profile";

	public int? VerifiedSafeLatencyMs { get; set; }

	public NetworkKey? NetworkKey { get; set; }

	public bool ExpiresAfterNetworkChange { get; set; }

	public Dictionary<string, SafeProfileEntry> SafeProfiles { get; set; } = new Dictionary<string, SafeProfileEntry>(StringComparer.OrdinalIgnoreCase);
}
