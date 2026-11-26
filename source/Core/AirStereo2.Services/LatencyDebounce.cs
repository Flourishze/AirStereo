namespace AirStereo2.Services;

public static class LatencyDebounce
{
	public static bool ShouldSend(int generation, int currentGeneration, bool isCancellationRequested, int? lastSentLatency, int latency)
	{
		if (!isCancellationRequested && generation == currentGeneration)
		{
			return lastSentLatency != latency;
		}
		return false;
	}
}
