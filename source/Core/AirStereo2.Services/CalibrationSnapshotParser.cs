using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using AirStereo2.Models;

namespace AirStereo2.Services;

public static class CalibrationSnapshotParser
{
	public static CalibrationHealthSnapshot Parse(string json)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(json);
		JsonElement root = jsonDocument.RootElement;
		if (root.ValueKind != JsonValueKind.Object)
		{
			throw new JsonException("Calibration snapshot must be a JSON object.");
		}
		TelemetryValidity state = TelemetryValidity.Valid;
		string timing = Text("timing");
		string codec = Text("codec");
		int? playoutMs = null;
		if (!root.TryGetProperty("playout_ms", out var value))
		{
			state |= TelemetryValidity.MissingFields;
		}
		else if (value.ValueKind != JsonValueKind.Null)
		{
			if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var value2) && value2 >= 0)
			{
				playoutMs = value2;
			}
			else
			{
				state |= TelemetryValidity.InvalidData;
			}
		}
		long num = L("members");
		if (num > int.MaxValue)
		{
			state |= TelemetryValidity.InvalidData;
			num = -1L;
		}
		ulong? sessionEpoch = null;
		ulong value4;
		if (!root.TryGetProperty("session_epoch", out var value3) || value3.ValueKind == JsonValueKind.Null)
		{
			state |= TelemetryValidity.MissingSessionEpoch;
		}
		else if (value3.ValueKind == JsonValueKind.Number && value3.TryGetUInt64(out value4))
		{
			sessionEpoch = value4;
		}
		else
		{
			state |= TelemetryValidity.InvalidData | TelemetryValidity.MissingSessionEpoch;
		}
		long queueDepth = L("queue_depth");
		long queueCapacity = L("queue_capacity");
		long packets = L("packets");
		long retransmitRequested = L("rtx_requested");
		long retransmitResent = L("rtx_resent");
		long underruns = L("underruns");
		long inputDrops = L("input_drops");
		long decodeDrops = L("decode_drops");
		long deadlineResets = L("deadline_resets");
		long sendErrors = L("send_errors");
		long captureGlitches = L("capture_glitches");
		long eventsDropped = L("events_dropped");
		IReadOnlyList<long> latenessHist = H("lateness_hist");
		IReadOnlyList<long> readOnlyList = H("path_delay_hist");
		long num2 = L("path_delay_count");
		if (readOnlyList != null && num2 >= 0 && new LatencyHistogram(readOnlyList).Total != (ulong)num2)
		{
			state |= TelemetryValidity.InvalidData;
		}
		IReadOnlyList<long> rtxGapHist = H("rtx_gap_hist");
		IReadOnlyList<TargetHealth> readOnlyList2 = ReadTargets(root, ref state);
		if (readOnlyList2 != null && num >= 0 && readOnlyList2.Count((TargetHealth t) => t.Active) != num)
		{
			state |= TelemetryValidity.InvalidData;
		}
		return new CalibrationHealthSnapshot(timing, codec, playoutMs, (int)num, queueDepth, queueCapacity, packets, retransmitRequested, retransmitResent, underruns, inputDrops, decodeDrops, deadlineResets, sendErrors, captureGlitches, eventsDropped, latenessHist, readOnlyList, num2, rtxGapHist, readOnlyList2, sessionEpoch, state);
		IReadOnlyList<long>? H(string name)
		{
			return ReadHistogram(root, name, ref state);
		}
		long L(string name)
		{
			return ReadLong(root, name, ref state);
		}
		string Text(string name)
		{
			if (!root.TryGetProperty(name, out var value5))
			{
				state |= TelemetryValidity.MissingFields;
				return "UNKNOWN";
			}
			if (value5.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value5.GetString()))
			{
				state |= TelemetryValidity.InvalidData;
				return "UNKNOWN";
			}
			return value5.GetString() ?? "UNKNOWN";
		}
	}

	private static long ReadLong(JsonElement root, string name, ref TelemetryValidity state)
	{
		if (!root.TryGetProperty(name, out var value))
		{
			state |= TelemetryValidity.MissingFields;
			return -1L;
		}
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var value2) && value2 >= 0)
		{
			return value2;
		}
		state |= TelemetryValidity.InvalidData;
		return -1L;
	}

	private static ulong ReadUnsigned(JsonElement root, string name, ref TelemetryValidity state)
	{
		if (!root.TryGetProperty(name, out var value))
		{
			state |= TelemetryValidity.MissingFields;
			return 0uL;
		}
		if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var value2))
		{
			return value2;
		}
		state |= TelemetryValidity.InvalidData;
		return 0uL;
	}

	private static IReadOnlyList<long>? ReadHistogram(JsonElement root, string name, ref TelemetryValidity state)
	{
		if (!root.TryGetProperty(name, out var value))
		{
			state |= TelemetryValidity.MissingFields;
			return null;
		}
		if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 64)
		{
			state |= TelemetryValidity.InvalidData;
			return null;
		}
		long[] array = new long[64];
		int num = 0;
		ulong num2 = 0uL;
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var value2) || value2 < 0 || (ulong)(-1L - (long)num2) < (ulong)value2)
			{
				state |= TelemetryValidity.InvalidData;
				return null;
			}
			num2 += (ulong)value2;
			array[num++] = value2;
		}
		return array;
	}

	private static IReadOnlyList<TargetHealth>? ReadTargets(JsonElement root, ref TelemetryValidity state)
	{
		if (!root.TryGetProperty("per_target", out var value))
		{
			state |= TelemetryValidity.MissingFields;
			return null;
		}
		if (value.ValueKind != JsonValueKind.Array)
		{
			state |= TelemetryValidity.InvalidData;
			return null;
		}
		List<TargetHealth> list = new List<TargetHealth>();
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object)
			{
				state |= TelemetryValidity.InvalidData;
				continue;
			}
			TelemetryValidity state2 = TelemetryValidity.Valid;
			ulong targetId = ReadUnsigned(item, "target_id", ref state2);
			string ip = string.Empty;
			IPAddress address;
			if (!item.TryGetProperty("ip", out var value2))
			{
				state2 |= TelemetryValidity.MissingFields;
			}
			else if (value2.ValueKind == JsonValueKind.String && IPAddress.TryParse(value2.GetString(), out address))
			{
				ip = address.ToString();
			}
			else
			{
				state2 |= TelemetryValidity.InvalidData;
			}
			bool active = false;
			if (!item.TryGetProperty("active", out var value3))
			{
				state2 |= TelemetryValidity.MissingFields;
			}
			else
			{
				JsonValueKind valueKind = value3.ValueKind;
				if (valueKind - 5 <= JsonValueKind.Object)
				{
					active = value3.GetBoolean();
				}
				else
				{
					state2 |= TelemetryValidity.InvalidData;
				}
			}
			ulong packets = ReadUnsigned(item, "packets", ref state2);
			ulong rtxRequested = ReadUnsigned(item, "rtx_requested", ref state2);
			ulong rtxResent = ReadUnsigned(item, "rtx_resent", ref state2);
			ulong sendErrors = ReadUnsigned(item, "send_errors", ref state2);
			IReadOnlyList<long> readOnlyList = ReadHistogram(item, "ptp_offset_hist", ref state2);
			ulong num = ReadUnsigned(item, "ptp_offset_count", ref state2);
			LatencyHistogram latencyHistogram = ((readOnlyList == null) ? null : new LatencyHistogram(readOnlyList, HistogramMetric.PtpClockOffsetProxy));
			if (latencyHistogram != null && latencyHistogram.Total != num)
			{
				state2 |= TelemetryValidity.InvalidData;
			}
			list.Add(new TargetHealth(targetId, ip, active, packets, rtxRequested, rtxResent, sendErrors, latencyHistogram, num, state2));
			state |= state2;
		}
		if ((from t in list
			group t by t.TargetId).Any((IGrouping<ulong, TargetHealth> g) => g.Count() > 1))
		{
			state |= TelemetryValidity.AmbiguousTarget;
		}
		return list;
	}
}
