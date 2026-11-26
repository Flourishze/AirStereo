using System.Collections.Generic;

namespace AirStereo2.Models;

public sealed record CalibrationHealthSnapshot(string Timing, string Codec, int? PlayoutMs, int Members, long QueueDepth, long QueueCapacity, long Packets, long RetransmitRequested, long RetransmitResent, long Underruns, long InputDrops, long DecodeDrops, long DeadlineResets, long SendErrors, long CaptureGlitches = 0L, long EventsDropped = 0L, IReadOnlyList<long>? LatenessHist = null, IReadOnlyList<long>? PathDelayHist = null, long PathDelayCount = 0L, IReadOnlyList<long>? RtxGapHist = null, IReadOnlyList<TargetHealth>? PerTarget = null, ulong? SessionEpoch = null, TelemetryValidity Validity = TelemetryValidity.Valid)
{
	public bool IsValid => Validity == TelemetryValidity.Valid;

	public bool IsDegraded => !IsValid;
}
