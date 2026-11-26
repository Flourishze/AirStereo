using System.Threading;

namespace AirStereo2.Services;

public static class OrderedDiscoveryDispatch
{
	public static bool TrySchedule(ref int drainScheduledFlag)
	{
		return Interlocked.CompareExchange(ref drainScheduledFlag, 1, 0) == 0;
	}

	public static void ClearScheduled(ref int drainScheduledFlag)
	{
		Interlocked.Exchange(ref drainScheduledFlag, 0);
	}

	public static bool ShouldRearmAfterDrain(bool queueIsEmpty)
	{
		return !queueIsEmpty;
	}
}
