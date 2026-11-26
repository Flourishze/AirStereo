using System;
using System.Collections.Generic;

namespace AirStereo2.Models;

public sealed record SnapshotDelta(ulong Packets, ulong Underruns, ulong InputDrops, ulong DecodeDrops, ulong DeadlineResets, ulong SendErrors, ulong RtxRequested, ulong RtxResent, ulong CaptureGlitches, LatencyHistogram Lateness, LatencyHistogram PathDelay, ulong PathDelayCount, ulong EventsDropped = 0uL, LatencyHistogram? RtxGap = null, IReadOnlyList<TargetHealth>? PerTarget = null, ulong? SessionEpoch = null, TelemetryValidity Validity = TelemetryValidity.Valid)
{
	public bool IsValid => Validity == TelemetryValidity.Valid;

	public bool IsDegraded => !IsValid;

	public IReadOnlyList<TargetHealth> Targets => PerTarget ?? Array.Empty<TargetHealth>();
}
