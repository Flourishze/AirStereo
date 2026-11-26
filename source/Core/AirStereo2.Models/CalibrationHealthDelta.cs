using System;
using System.Collections.Generic;

namespace AirStereo2.Models;

public sealed record CalibrationHealthDelta(long Packets, long RetransmitRequested, long RetransmitResent, long Underruns, long InputDrops, long DecodeDrops, long DeadlineResets, long SendErrors, long CaptureGlitches = 0L, long EventsDropped = 0L, IReadOnlyList<long>? LatenessHist = null, IReadOnlyList<long>? PathDelayHist = null, long PathDelayCount = 0L, IReadOnlyList<long>? RtxGapHist = null, IReadOnlyList<TargetHealth>? PerTarget = null, ulong? SessionEpoch = null, TelemetryValidity Validity = TelemetryValidity.Valid)
{
	public bool IsValid => Validity == TelemetryValidity.Valid;

	public bool IsDegraded => !IsValid;

	public bool IsStable
	{
		get
		{
			if (IsValid)
			{
				return LatencyCalibrationPolicy.IsHealthy(this);
			}
			return false;
		}
	}

	public SnapshotDelta ToSnapshotDelta()
	{
		return new SnapshotDelta((ulong)Math.Max(0L, Packets), (ulong)Math.Max(0L, Underruns), (ulong)Math.Max(0L, InputDrops), (ulong)Math.Max(0L, DecodeDrops), (ulong)Math.Max(0L, DeadlineResets), (ulong)Math.Max(0L, SendErrors), (ulong)Math.Max(0L, RetransmitRequested), (ulong)Math.Max(0L, RetransmitResent), (ulong)Math.Max(0L, CaptureGlitches), new LatencyHistogram(LatenessHist), new LatencyHistogram(PathDelayHist), (ulong)Math.Max(0L, PathDelayCount), (ulong)Math.Max(0L, EventsDropped), (RtxGapHist == null) ? null : new LatencyHistogram(RtxGapHist), PerTarget, SessionEpoch, Validity);
	}
}
