using System;
using AirStereo2.Models;

namespace AirStereo2.Services;

public sealed record CalibrationSegmentReport(int? CandidateLatencyMs, int? RequestedLatencyMs, int? AppliedLatencyMs, string ApplyStatus, string HearingResult, string TelemetryVerdict, string? SessionId, string? NetworkIdentity, DateTime StartedAtUtc, DateTime EndedAtUtc, bool SavedByUser, int? SavedLatencyMs, CalibrationHealthDelta? Health = null)
{
	public int? LatencyMs => AppliedLatencyMs ?? CandidateLatencyMs;

	public bool BackendHealthy => string.Equals(TelemetryVerdict, "Healthy", StringComparison.OrdinalIgnoreCase);

	public CalibrationSegmentReport(int? latencyMs, bool backendHealthy, string hearingResult, CalibrationHealthDelta? health)
		: this(latencyMs, latencyMs, latencyMs, "Applied", hearingResult, backendHealthy ? "Healthy" : "Unhealthy", null, null, DateTime.UtcNow, DateTime.UtcNow, string.Equals(hearingResult, "good", StringComparison.OrdinalIgnoreCase), string.Equals(hearingResult, "good", StringComparison.OrdinalIgnoreCase) ? latencyMs : ((int?)null), health)
	{
	}
}
