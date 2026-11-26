using System;
using System.Collections.Generic;

namespace AirStereo2.Services;

public sealed class DeviceLatencyTracker
{
	private readonly Dictionary<string, int> _byAddress = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, PlaybackLatencySmoother> _smoothers = new Dictionary<string, PlaybackLatencySmoother>(StringComparer.OrdinalIgnoreCase);

	public int? AddPlayback(string ip, int ms)
	{
		if (!_smoothers.TryGetValue(ip, out PlaybackLatencySmoother value))
		{
			value = new PlaybackLatencySmoother();
			_smoothers[ip] = value;
		}
		int? num = value.Add(Math.Max(0, ms));
		if (!num.HasValue)
		{
			return null;
		}
		_byAddress[ip] = num.Value;
		int num2 = 0;
		foreach (int value2 in _byAddress.Values)
		{
			num2 = Math.Max(num2, value2);
		}
		return num2;
	}

	public void ResetSmoothing()
	{
		foreach (PlaybackLatencySmoother value in _smoothers.Values)
		{
			value.Reset();
		}
	}

	public void Clear()
	{
		_byAddress.Clear();
		_smoothers.Clear();
	}
}
