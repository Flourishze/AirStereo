using System;
using System.Collections.Generic;
using AirStereo2.Services;

namespace AirStereo2.Models;

public static class LongInterruptionTracker
{
	private static readonly object _lock = new object();

	private static readonly Dictionary<string, DateTime> _lastPromptUtcByBssid = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	public static LongInterruptionResult CheckAndRecord(SnapshotDelta? delta, string? bssid = null, DateTime? nowUtc = null)
	{
		bool flag = delta != null && delta.IsValid && delta.RtxGap != null && delta.RtxGap.Metric == HistogramMetric.TransportDelay;
		if (!flag || (object)delta == null || delta.RtxGap?.Overflow != true)
		{
			return new LongInterruptionResult(Triggered: false, ShouldPrompt: false, string.Empty, flag);
		}
		string key = ((!string.IsNullOrWhiteSpace(bssid)) ? bssid.Trim().ToLowerInvariant() : "global_bssid");
		DateTime dateTime = nowUtc ?? DateTime.UtcNow;
		lock (_lock)
		{
			if (_lastPromptUtcByBssid.TryGetValue(key, out var value) && dateTime - value < TimeSpan.FromHours(24.0))
			{
				return new LongInterruptionResult(Triggered: true, ShouldPrompt: false, GetNoticeText());
			}
			_lastPromptUtcByBssid[key] = dateTime;
			return new LongInterruptionResult(Triggered: true, ShouldPrompt: true, GetNoticeText());
		}
	}

	public static void ResetForTesting()
	{
		lock (_lock)
		{
			_lastPromptUtcByBssid.Clear();
		}
	}

	public static string GetNoticeText()
	{
		return LocalizationManager.Instance.GetString("LongInterruption_Notice", "检测到较长的重传请求间隔，不能仅凭此确认声音曾中断。若确有卡顿，请检查网络；不会因此自动调高延迟。");
	}
}
