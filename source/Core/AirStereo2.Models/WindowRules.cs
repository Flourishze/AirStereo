namespace AirStereo2.Models;

public static class WindowRules
{
	public static WindowVerdict Judge(SnapshotDelta d, double seconds, bool statusFailed)
	{
		if (!d.IsValid)
		{
			return WindowVerdict.InsufficientEvidence;
		}
		if ((double)d.Packets < 112.5 * seconds)
		{
			return WindowVerdict.InvalidTooFewPackets;
		}
		if (d.Underruns != 0 || d.InputDrops != 0)
		{
			return WindowVerdict.SourceGlitch;
		}
		if (d.SendErrors != 0 || statusFailed)
		{
			return WindowVerdict.LinkFailure;
		}
		ulong num = (ulong)((seconds >= 12.0) ? 3 : 2);
		if (d.RtxRequested > num || d.RtxResent < d.RtxRequested)
		{
			return WindowVerdict.RtxOverBudget;
		}
		return WindowVerdict.Valid;
	}

	public static bool IsPcLoadHeavy(SnapshotDelta d, double seconds)
	{
		return d.DeadlineResets > (ulong)(10.0 * seconds);
	}
}
