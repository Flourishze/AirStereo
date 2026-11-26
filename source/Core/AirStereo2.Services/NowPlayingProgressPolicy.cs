using System;

namespace AirStereo2.Services;

public static class NowPlayingProgressPolicy
{
	public const long SeekThresholdMs = 1500L;

	public const long DurationToleranceMs = 1000L;

	public static readonly TimeSpan SanityInterval = TimeSpan.FromSeconds(30.0);

	public static long Extrapolate(NowPlayingProgress sent, TimeSpan elapsed)
	{
		if (!IsPlaying(sent.State))
		{
			return sent.PositionMs;
		}
		double num = Math.Max(0.0, elapsed.TotalMilliseconds) * Math.Max(0.0, sent.Rate);
		long num2 = sent.PositionMs + (long)num;
		if (sent.DurationMs <= 0)
		{
			return num2;
		}
		return Math.Min(num2, sent.DurationMs);
	}

	public static bool ShouldSend(NowPlayingProgress? lastSent, NowPlayingProgress current, TimeSpan sinceLastSend)
	{
		if (lastSent.HasValue)
		{
			NowPlayingProgress valueOrDefault = lastSent.GetValueOrDefault();
			if (!string.Equals(valueOrDefault.State, current.State, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (Math.Abs(valueOrDefault.Rate - current.Rate) > 0.01)
			{
				return true;
			}
			if (Math.Abs(valueOrDefault.DurationMs - current.DurationMs) > 1000)
			{
				return true;
			}
			if (valueOrDefault.DurationMs <= 0 || current.DurationMs <= 0)
			{
				return false;
			}
			if (Math.Abs(current.PositionMs - Extrapolate(valueOrDefault, sinceLastSend)) >= 1500)
			{
				return true;
			}
			if (IsPlaying(current.State))
			{
				return sinceLastSend >= SanityInterval;
			}
			return false;
		}
		return true;
	}

	private static bool IsPlaying(string? state)
	{
		return string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase);
	}
}
