using System;
using System.Collections.Generic;
using System.Linq;
using AirStereo2.Services;

namespace AirStereo2.Models;

public static class LatencyCalibrationPolicy
{
	public const int SoloHomePodAlacValidatedFloorMs = 50;

	public const int SoloHomePodPcmValidatedFloorMs = 45;

	public static int? ResolveSavedLatency(IEnumerable<LatencyCalibrationProfile> profiles, IReadOnlyCollection<CalibrationDeviceIdentity> devices, string? codec)
	{
		if (devices == null || devices.Count == 0 || devices.Any((CalibrationDeviceIdentity device) => string.IsNullOrWhiteSpace(device.StableId)))
		{
			return null;
		}
		string text = ((devices.Count > 1) ? "ALAC" : AudioCodecPreference.Normalize(codec));
		CalibrationPlan calibrationPlan = CreatePlan(devices, text);
		if (!calibrationPlan.SupportsLiveLatencyTuning)
		{
			return null;
		}
		string b = BuildProfileKey(devices.Select((CalibrationDeviceIdentity device) => device.StableId), calibrationPlan.Topology, text);
		foreach (LatencyCalibrationProfile item in profiles.OrderByDescending((LatencyCalibrationProfile profile) => profile.TestedAtUtc))
		{
			if (!string.Equals(item.ProfileKey, b, StringComparison.OrdinalIgnoreCase) || !string.Equals(item.Topology, calibrationPlan.Topology, StringComparison.OrdinalIgnoreCase) || !string.Equals(item.Codec, text, StringComparison.OrdinalIgnoreCase) || item.DeviceStableIds == null || !string.Equals(BuildProfileKey(item.DeviceStableIds, calibrationPlan.Topology, text), b, StringComparison.OrdinalIgnoreCase) || !item.SavedByUser || !string.Equals(item.ApplyStatus, "Applied", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			int? result = item.AppliedLatencyMs;
			if (!result.HasValue && !item.RequestedLatencyMs.HasValue && !item.SavedLatencyMs.HasValue && string.IsNullOrEmpty(item.SessionId))
			{
				result = item.EffectiveLatencyMs;
			}
			if (result.HasValue)
			{
				int valueOrDefault = result.GetValueOrDefault();
				if (valueOrDefault >= 0 && valueOrDefault <= 1000)
				{
					return result;
				}
			}
		}
		return null;
	}

	public static int? ValidatedFloorMs(CalibrationPlan plan, string? codec)
	{
		if (plan == null || !plan.Topology.Contains("HomePod solo", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return (plan.Topology.Contains("(PCM)", StringComparison.OrdinalIgnoreCase) || string.Equals(codec, "PCM", StringComparison.OrdinalIgnoreCase)) ? 45 : 50;
	}

	public static CalibrationPlan CreatePlan(IReadOnlyCollection<CalibrationDeviceIdentity> devices, string? codec = null)
	{
		if (devices.Count == 0)
		{
			return new CalibrationPlan("None", "Unknown", Array.Empty<int>(), SupportsLiveLatencyTuning: false);
		}
		if (devices.Any((CalibrationDeviceIdentity device) => !device.SupportsAirPlay2))
		{
			return new CalibrationPlan("Classic RAOP", "NTP", Array.Empty<int>(), SupportsLiveLatencyTuning: false);
		}
		bool flag = devices.Any((CalibrationDeviceIdentity device) => LooksLikeAppleTv(device.Model, device.Name));
		bool flag2 = devices.All((CalibrationDeviceIdentity device) => LooksLikeHomePod(device.Model, device.Name));
		bool flag3 = string.Equals(codec, "PCM", StringComparison.OrdinalIgnoreCase);
		if (devices.Count == 1 && flag)
		{
			return new CalibrationPlan("Apple TV solo", "NTP", new int[2] { 100, 80 }, SupportsLiveLatencyTuning: true);
		}
		if (devices.Count == 1 && flag2 && flag3)
		{
			return new CalibrationPlan("HomePod solo (PCM)", "NTP", new int[3] { 60, 45, 30 }, SupportsLiveLatencyTuning: true);
		}
		if (devices.Count == 1 && flag2)
		{
			return new CalibrationPlan("HomePod solo", "PTP", new int[3] { 50, 40, 30 }, SupportsLiveLatencyTuning: true);
		}
		if (devices.Count > 1 && flag2)
		{
			return new CalibrationPlan("HomePod group", "PTP", new int[4] { 120, 110, 100, 90 }, SupportsLiveLatencyTuning: true);
		}
		if (devices.Count > 1 && flag)
		{
			return new CalibrationPlan("Mixed Apple group", "PTP", new int[3] { 120, 110, 100 }, SupportsLiveLatencyTuning: true);
		}
		if (devices.Count == 1)
		{
			return new CalibrationPlan("AirPlay 2 receiver", "PTP", new int[3] { 100, 80, 60 }, SupportsLiveLatencyTuning: true);
		}
		return new CalibrationPlan("AirPlay 2 group", "PTP", new int[3] { 180, 160, 140 }, SupportsLiveLatencyTuning: true);
	}

	public static CalibrationHealthDelta Delta(CalibrationHealthSnapshot before, CalibrationHealthSnapshot after)
	{
		TelemetryValidity validity = before.Validity | after.Validity;
		bool sessionChanged = before.SessionEpoch.HasValue && after.SessionEpoch.HasValue && before.SessionEpoch != after.SessionEpoch;
		if (!before.SessionEpoch.HasValue || !after.SessionEpoch.HasValue)
		{
			validity |= TelemetryValidity.MissingSessionEpoch;
		}
		if (sessionChanged)
		{
			validity |= TelemetryValidity.SessionChanged;
		}
		if (before.Members != after.Members)
		{
			validity |= TelemetryValidity.TargetSetChanged;
		}
		if (!string.Equals(before.Timing, after.Timing, StringComparison.OrdinalIgnoreCase) || !string.Equals(before.Codec, after.Codec, StringComparison.OrdinalIgnoreCase))
		{
			validity |= TelemetryValidity.SessionChanged;
			sessionChanged = true;
		}
		long packets = D(before.Packets, after.Packets);
		long retransmitRequested = D(before.RetransmitRequested, after.RetransmitRequested);
		long retransmitResent = D(before.RetransmitResent, after.RetransmitResent);
		long underruns = D(before.Underruns, after.Underruns);
		long inputDrops = D(before.InputDrops, after.InputDrops);
		long decodeDrops = D(before.DecodeDrops, after.DecodeDrops);
		long deadlineResets = D(before.DeadlineResets, after.DeadlineResets);
		long sendErrors = D(before.SendErrors, after.SendErrors);
		long captureGlitches = D(before.CaptureGlitches, after.CaptureGlitches);
		long eventsDropped = D(before.EventsDropped, after.EventsDropped);
		IReadOnlyList<long> latenessHist = H(before.LatenessHist, after.LatenessHist);
		IReadOnlyList<long> pathDelayHist = H(before.PathDelayHist, after.PathDelayHist);
		long pathDelayCount = D(before.PathDelayCount, after.PathDelayCount);
		IReadOnlyList<long> rtxGapHist = H(before.RtxGapHist, after.RtxGapHist);
		IReadOnlyList<TargetHealth> perTarget = DeltaTargets(before.PerTarget, after.PerTarget, sessionChanged, ref validity);
		return new CalibrationHealthDelta(packets, retransmitRequested, retransmitResent, underruns, inputDrops, decodeDrops, deadlineResets, sendErrors, captureGlitches, eventsDropped, latenessHist, pathDelayHist, pathDelayCount, rtxGapHist, perTarget, after.SessionEpoch, validity);
		long D(long b, long a)
		{
			if (b < 0 || a < 0)
			{
				validity |= TelemetryValidity.MissingFields;
				return 0L;
			}
			if (a < b)
			{
				validity |= TelemetryValidity.CounterReset;
				return 0L;
			}
			if (!sessionChanged)
			{
				return a - b;
			}
			return 0L;
		}
		IReadOnlyList<long>? H(IReadOnlyList<long>? b, IReadOnlyList<long>? a)
		{
			if (b == null || a == null)
			{
				validity |= TelemetryValidity.MissingFields;
				return null;
			}
			if (b.Count != 64 || a.Count != 64 || b.Any((long x) => x < 0) || a.Any((long x) => x < 0))
			{
				validity |= TelemetryValidity.InvalidData;
				return null;
			}
			if (Enumerable.Range(0, 64).Any((int i) => a[i] < b[i]))
			{
				validity |= TelemetryValidity.CounterReset;
				return null;
			}
			if (!sessionChanged)
			{
				return (from i in Enumerable.Range(0, 64)
					select a[i] - b[i]).ToArray();
			}
			return null;
		}
	}

	private static IReadOnlyList<TargetHealth>? DeltaTargets(IReadOnlyList<TargetHealth>? before, IReadOnlyList<TargetHealth>? after, bool sessionChanged, ref TelemetryValidity validity)
	{
		if (before == null || after == null)
		{
			validity |= TelemetryValidity.MissingFields;
			return null;
		}
		if ((from t in before
			group t by t.TargetId).Any((IGrouping<ulong, TargetHealth> g) => g.Count() != 1) || (from t in after
			group t by t.TargetId).Any((IGrouping<ulong, TargetHealth> g) => g.Count() != 1))
		{
			validity |= TelemetryValidity.AmbiguousTarget;
			return null;
		}
		if (!before.Select((TargetHealth t) => (TargetId: t.TargetId, Ip: t.Ip, Active: t.Active)).ToHashSet().SetEquals(after.Select((TargetHealth t) => (TargetId: t.TargetId, Ip: t.Ip, Active: t.Active))))
		{
			validity |= TelemetryValidity.TargetSetChanged;
		}
		List<TargetHealth> list = new List<TargetHealth>();
		foreach (TargetHealth a in after)
		{
			TargetHealth b = before.FirstOrDefault((TargetHealth t) => t.TargetId == a.TargetId && string.Equals(t.Ip, a.Ip, StringComparison.OrdinalIgnoreCase));
			TelemetryValidity state = a.Validity | (b?.Validity ?? TelemetryValidity.TargetSetChanged);
			if (sessionChanged)
			{
				state |= TelemetryValidity.SessionChanged;
			}
			if (b != null && b.Active != a.Active)
			{
				state |= TelemetryValidity.TargetSetChanged;
			}
			ulong packets = D(b?.Packets ?? 0, a.Packets);
			ulong rtxRequested = D(b?.RtxRequested ?? 0, a.RtxRequested);
			ulong rtxResent = D(b?.RtxResent ?? 0, a.RtxResent);
			ulong sendErrors = D(b?.SendErrors ?? 0, a.SendErrors);
			ulong num = D(b?.PtpOffsetCount ?? 0, a.PtpOffsetCount);
			LatencyHistogram latencyHistogram = null;
			if (a.PtpOffset == null || b?.PtpOffset == null)
			{
				state |= TelemetryValidity.MissingFields;
			}
			else if (a.PtpOffset.Metric != b.PtpOffset.Metric)
			{
				state |= TelemetryValidity.InvalidData;
			}
			else if (Enumerable.Range(0, 64).Any((int i) => a.PtpOffset.Counts[i] < b.PtpOffset.Counts[i]))
			{
				state |= TelemetryValidity.CounterReset;
			}
			else if (state == TelemetryValidity.Valid)
			{
				latencyHistogram = new LatencyHistogram((from i in Enumerable.Range(0, 64)
					select a.PtpOffset.Counts[i] - b.PtpOffset.Counts[i]).ToArray(), a.PtpOffset.Metric);
			}
			if (latencyHistogram != null && latencyHistogram.Total != num)
			{
				state |= TelemetryValidity.InvalidData;
			}
			validity |= state;
			list.Add(new TargetHealth(a.TargetId, a.Ip, a.Active, packets, rtxRequested, rtxResent, sendErrors, (state == TelemetryValidity.Valid) ? latencyHistogram : null, num, state));
			ulong D(ulong oldValue, ulong newValue)
			{
				if (newValue < oldValue)
				{
					state |= TelemetryValidity.CounterReset;
					return 0uL;
				}
				if (!(b == null || sessionChanged))
				{
					return newValue - oldValue;
				}
				return 0uL;
			}
		}
		return list;
	}

	public static bool IsHealthy(CalibrationHealthDelta delta)
	{
		if (!delta.IsValid || delta.Packets < 100 || delta.Underruns > 0 || delta.InputDrops > 0 || delta.DecodeDrops > 0 || delta.DeadlineResets > 0 || delta.SendErrors > 0)
		{
			return false;
		}
		long num = Math.Max(2L, delta.Packets / 500);
		return delta.RetransmitRequested <= num;
	}

	public static bool IsSegmentHealthy(CalibrationHealthSnapshot before, CalibrationHealthSnapshot after, CalibrationHealthDelta delta, int expectedMembers, string expectedTiming, int? expectedPlayoutMs)
	{
		if (!IsHealthy(delta) || before.Members != expectedMembers || after.Members != expectedMembers || string.Equals(before.Codec, "UNKNOWN", StringComparison.OrdinalIgnoreCase) || !string.Equals(before.Codec, after.Codec, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (!string.Equals(before.Timing, after.Timing, StringComparison.OrdinalIgnoreCase) || string.Equals(before.Timing, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (expectedPlayoutMs.HasValue)
		{
			if (before.PlayoutMs.HasValue && after.PlayoutMs.HasValue && Math.Abs(before.PlayoutMs.Value - expectedPlayoutMs.Value) <= 1)
			{
				return Math.Abs(after.PlayoutMs.Value - expectedPlayoutMs.Value) <= 1;
			}
			return false;
		}
		return true;
	}

	public static int Recommend(IReadOnlyList<int> candidates, IReadOnlyCollection<int> stable)
	{
		if (candidates.Count == 0 || stable.Count == 0)
		{
			return 0;
		}
		int num = stable.Min();
		int num2 = candidates.IndexOf(num);
		if (num2 <= 0)
		{
			return num;
		}
		return candidates[num2 - 1];
	}

	public static string BuildProfileKey(IEnumerable<string> stableIds, string topology, string codec)
	{
		string value = string.Join("|", (from text in stableIds
			where !string.IsNullOrWhiteSpace(text)
			select text.Trim().ToLowerInvariant()).OrderBy<string, string>((string result) => result, StringComparer.Ordinal));
		return $"{topology.Trim().ToLowerInvariant()}::{codec.Trim().ToUpperInvariant()}::{value}";
	}

	public static NetworkStats CalculateNetworkStats(SnapshotDelta d, IReadOnlyList<(double Rtt, double Proc)> rttSamples, bool wifi = true)
	{
		if (d.PathDelayCount != 0)
		{
			LatencyHistogram pathDelay = d.PathDelay;
			double jNet = Math.Max(pathDelay.Max - pathDelay.P50, 2.0 * pathDelay.Sigma);
			return new NetworkStats(pathDelay.P50, jNet, RttQuantile(rttSamples, 0.95), pathDelay.Sigma, RttDerived: false);
		}
		if (rttSamples.Count == 0)
		{
			return new NetworkStats(2.0, 2.0, 5.0, 1.0, RttDerived: true, HasMeasuredEvidence: false);
		}
		double[] array = (from s in rttSamples
			select Math.Max(0.2, (s.Rtt - Math.Min(s.Rtt, s.Proc)) / 2.0) into x
			orderby x
			select x).ToArray();
		double num = QuantileArray(array, 0.5);
		double num2 = array[^1];
		double num3 = StandardDeviation(array);
		double jNet2 = Math.Max(num2 - num, 2.0 * num3);
		return new NetworkStats(num, jNet2, RttQuantile(rttSamples, 0.95), num3, RttDerived: true);
	}

	public static (double RtxMs, bool IsGapDerived, bool OverflowFlag) ComputeRRtx(SnapshotDelta d, NetworkStats n, bool wifi)
	{
		double item = ((!wifi) ? 0.5 : ((d.RtxRequested != 0) ? 1.5 : 1.0)) * n.RttP95;
		LatencyHistogram rtxGap = d.RtxGap;
		if (rtxGap == null || rtxGap.Total == 0L)
		{
			return (RtxMs: item, IsGapDerived: false, OverflowFlag: false);
		}
		if (rtxGap.Counts.Length > 63 && rtxGap.Counts[63] != 0)
		{
			return (RtxMs: item, IsGapDerived: false, OverflowFlag: true);
		}
		if (rtxGap.Total >= 10)
		{
			double p = rtxGap.P95;
			if (!double.IsNaN(p) && p > 0.0)
			{
				return (RtxMs: p, IsGapDerived: true, OverflowFlag: false);
			}
		}
		return (RtxMs: item, IsGapDerived: false, OverflowFlag: false);
	}

	public static LatencyRecommendation ComputeRecommendation(DeviceGrade grade, NetworkStats n, TDevEstimate tdev, SnapshotDelta d, double floorClass, double? recvClampMs = null, bool wifi = true, bool selfRetiming = false)
	{
		(double RtxMs, bool IsGapDerived, bool OverflowFlag) tuple = ComputeRRtx(d, n, wifi);
		double item = tuple.RtxMs;
		bool item2 = tuple.IsGapDerived;
		bool item3 = tuple.OverflowFlag;
		double num = d.Lateness.P99_9;
		if (double.IsNaN(num) || num <= 0.0)
		{
			num = d.Lateness.P99;
		}
		if (double.IsNaN(num) || num <= 0.0)
		{
			num = 0.5;
		}
		double num2 = Math.Max(grade switch
		{
			DeviceGrade.A => n.RttDerived ? 3.5 : 3.0, 
			DeviceGrade.B => 4.0, 
			_ => 4.0, 
		} * n.Sigma + tdev.Width / 2.0, (grade >= DeviceGrade.CPlus) ? 15.0 : 0.0);
		double num3 = Math.Max(Math.Max(floorClass, tdev.Low + 16.0), Math.Max(n.DPath + n.JNet + item, recvClampMs.GetValueOrDefault()));
		double num4 = Math.Max(num3, tdev.Value + n.DPath + n.JNet + item + num + num2);
		double num5 = Ceil5(num3);
		double num6 = Math.Max(num5, Round5(num4 - num2 / 2.0));
		double balanced = Math.Max(num6, Round5(num4));
		double stable = Round5(num4 + Math.Max(40.0, 2.0 * num2));
		string value = (selfRetiming ? $"音箱最低约 {Round5(floorClass)} ms" : $"音箱约 {Round5(tdev.Value)} ms");
		string explain = $"{value}，网络约 {Round5(n.DPath + n.JNet + item)} ms";
		return new LatencyRecommendation(num6, balanced, stable, num5, explain, selfRetiming, item2, item3);
	}

	public static double Ceil5(double x)
	{
		return Math.Ceiling(x / 5.0) * 5.0;
	}

	public static double Round5(double x)
	{
		return Math.Round(x / 5.0) * 5.0;
	}

	private static double RttQuantile(IReadOnlyList<(double Rtt, double Proc)> samples, double q)
	{
		if (samples.Count == 0)
		{
			return 4.0;
		}
		return QuantileArray((from s in samples
			select s.Rtt into x
			orderby x
			select x).ToArray(), q);
	}

	private static double QuantileArray(double[] sorted, double q)
	{
		if (sorted.Length == 0)
		{
			return 0.0;
		}
		int value = (int)Math.Ceiling(q * (double)sorted.Length) - 1;
		return sorted[Math.Clamp(value, 0, sorted.Length - 1)];
	}

	private static double StandardDeviation(double[] values)
	{
		if (values.Length < 2)
		{
			return 0.0;
		}
		double avg = values.Average();
		return Math.Sqrt(values.Sum((double v) => (v - avg) * (v - avg)) / (double)(values.Length - 1));
	}

	public static bool LooksLikeHomePod(string model, string name)
	{
		if (!model.Contains("AudioAccessory", StringComparison.OrdinalIgnoreCase))
		{
			return name.Contains("HomePod", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static bool LooksLikeAppleTv(string model, string name)
	{
		if (!model.Contains("AppleTV", StringComparison.OrdinalIgnoreCase))
		{
			return name.Contains("Apple TV", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}
}
