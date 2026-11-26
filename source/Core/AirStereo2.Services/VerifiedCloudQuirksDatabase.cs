using System;
using System.Collections.Generic;

namespace AirStereo2.Services;

public sealed class VerifiedCloudQuirksDatabase
{
	public int SchemaVersion { get; set; } = 1;

	public DateTime GeneratedAtUtc { get; set; }

	public string SignatureAlgorithm { get; set; } = "ECDSA-P256-SHA256";

	public string Signature { get; set; } = string.Empty;

	public Dictionary<string, VerifiedDeviceQuirkEntry> Devices { get; set; } = new Dictionary<string, VerifiedDeviceQuirkEntry>(StringComparer.OrdinalIgnoreCase);
}
