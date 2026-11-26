using System;

namespace AirStereo2.Services;

public sealed class PlaybackLatencySmoother
{
	private const int WindowSize = 5;

	private const int StableDeltaMs = 3;

	private const int RequireConsecutive = 2;

	private const int PendingToleranceMs = 2;

	private int _sampleCount;

	private int? _displayMs;

	private int? _pendingMs;

	private int _pendingCount;

	public int? DisplayMs => _displayMs;

	public int? Add(int rawMs)
	{
		int num = Math.Max(0, rawMs);
		_sampleCount++;
		if (_sampleCount < 5)
		{
			_displayMs = num;
			return num;
		}
		int? displayMs = _displayMs;
		if (!displayMs.HasValue)
		{
			_displayMs = num;
			return num;
		}
		if (Math.Abs(num - _displayMs.Value) <= 3)
		{
			_pendingMs = null;
			_pendingCount = 0;
			return null;
		}
		if (_pendingMs.HasValue && Math.Abs(_pendingMs.Value - num) <= 2)
		{
			_pendingCount++;
		}
		else
		{
			_pendingMs = num;
			_pendingCount = 1;
		}
		if (_pendingCount >= 2)
		{
			_displayMs = num;
			_pendingMs = null;
			_pendingCount = 0;
			return num;
		}
		return null;
	}

	public void Reset()
	{
		_sampleCount = 0;
		_displayMs = null;
		_pendingMs = null;
		_pendingCount = 0;
	}
}
