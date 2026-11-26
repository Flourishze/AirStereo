namespace AirStereo2.Models;

public sealed record TargetHealth(ulong TargetId, string Ip, bool Active, ulong Packets, ulong RtxRequested, ulong RtxResent, ulong SendErrors, LatencyHistogram? PtpOffset = null, ulong PtpOffsetCount = 0uL, TelemetryValidity Validity = TelemetryValidity.Valid)
{
	public bool IsValid => Validity == TelemetryValidity.Valid;

	public bool IsDegraded => !IsValid;
}
