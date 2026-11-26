using System;
using System.Text.Json.Serialization;

namespace AirStereo2.Models;

public sealed class LatencyKnowledgeEntry
{
	[JsonIgnore]
	public LatencyKnowledgeOrigin Origin { get; internal set; }

	public int SchemaVersion { get; set; } = 1;

	public string DeviceFingerprint { get; set; } = string.Empty;

	public string NetworkScope { get; set; } = "global";

	public string Model { get; set; } = string.Empty;

	public string Manufacturer { get; set; } = string.Empty;

	public string SrcversMajorMinor { get; set; } = string.Empty;

	public double? TDevMs { get; set; }

	public int? ClampMs { get; set; }

	public double? ConfidenceWeight { get; set; }

	public int? VerifiedSafeLatencyMs { get; set; }

	public int? AcousticOffsetPriorMs { get; set; }

	public string Confidence { get; set; } = "seed";

	public string Source { get; set; } = "cloud_seed";

	public int SampleCount { get; set; } = 1;

	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

	public NetworkKey? BoundNetworkKey { get; set; }
}
