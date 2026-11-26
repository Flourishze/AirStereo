using System;
using System.Collections.Generic;

namespace AirStereo2.Models;

public sealed class LatencyCalibrationProfile
{
	public string ProfileKey { get; set; } = string.Empty;

	public NetworkKey? NetworkKey { get; set; }

	public string? Ssid { get; set; }

	public int? AdvertisedMaxMs { get; set; }

	public List<string> DeviceStableIds { get; set; } = new List<string>();

	public List<string> DeviceModels { get; set; } = new List<string>();

	public string Topology { get; set; } = string.Empty;

	public string Codec { get; set; } = string.Empty;

	public string Timing { get; set; } = string.Empty;

	public int? RecommendedLatencyMs { get; set; }

	public int? UserLatencyMs { get; set; }

	public int? EffectiveLatencyMs { get; set; }

	public int LowestConfirmedStableMs { get; set; }

	public DateTime TestedAtUtc { get; set; }

	public int? CandidateLatencyMs { get; set; }

	public int? RequestedLatencyMs { get; set; }

	public int? AppliedLatencyMs { get; set; }

	public string ApplyStatus { get; set; } = "Applied";

	public string HearingResult { get; set; } = "Good";

	public string TelemetryVerdict { get; set; } = "Healthy";

	public string SessionId { get; set; } = string.Empty;

	public string NetworkIdentity { get; set; } = string.Empty;

	public DateTime StartedAtUtc { get; set; }

	public DateTime EndedAtUtc { get; set; }

	public bool SavedByUser { get; set; } = true;

	public int? SavedLatencyMs { get; set; }

	public int? ArchivedAudioVideoOffsetMs { get; set; }

	public int? ArchivedAudioVideoOffsetUserMs { get; set; }

	public int? ArchivedAudioVideoOffsetEffectiveMs { get; set; }

	public int AudioVideoOffsetMs { get; set; }

	public int AudioVideoOffsetUserMs { get; set; }

	public int AudioVideoOffsetEffectiveMs { get; set; }
}
