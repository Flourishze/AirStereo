using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using AirStereo2.Models;
using AirStereo2.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AirStereo2.ViewModels;

public sealed class CalibrationResultViewModel : ObservableObject
{
	private readonly object _lock = new object();

	private readonly LatencyKnowledgeSyncService _knowledge;

	private readonly List<(double Rtt, double Proc)> _rtt = new List<(double, double)>();

	private IReadOnlyList<CalibrationDeviceEvidence> _devices = Array.Empty<CalibrationDeviceEvidence>();

	private CalibrationPlan? _plan;

	private CalibrationHealthSnapshot? _before;

	private CalibrationHealthDelta? _delta;

	private readonly Dictionary<string, int?> _reported = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

	private bool _windowOpen;

	private readonly string _unknownNetworkScope = "unknown-session:" + Guid.NewGuid().ToString("N");

	private CalibrationResultState _result = new CalibrationResultState();

	public CalibrationResultState Result
	{
		get
		{
			return _result;
		}
		private set
		{
			this.SetProperty<CalibrationResultState>(ref _result, value, "Result");
		}
	}

	public CalibrationHealthDelta? LastDelta
	{
		get
		{
			lock (_lock)
			{
				return _delta;
			}
		}
	}

	public CalibrationResultViewModel(LatencyKnowledgeSyncService? knowledge = null)
	{
		_knowledge = knowledge ?? new LatencyKnowledgeSyncService();
	}

	public void Configure(CalibrationPlan plan, IReadOnlyList<CalibrationDeviceEvidence> devices)
	{
		lock (_lock)
		{
			_plan = plan;
			_devices = devices.ToArray();
			_before = null;
			_delta = null;
			_windowOpen = false;
			_rtt.Clear();
			_reported.Clear();
			Result = new CalibrationResultState();
		}
	}

	public void ObserveBackendEvent(BackendEvent evt)
	{
		lock (_lock)
		{
			if (evt.Is("Info", "LATENCY"))
			{
				string text = NormalizedIp(evt);
				if (text != null && IsTestDevice(text))
				{
					_reported[text] = ReportedLatencyMs(evt);
				}
			}
			else
			{
				if (!_windowOpen || !evt.Is("Latency", "RTT"))
				{
					return;
				}
				string text2 = evt.GetString("ip");
				if (text2 != null && IsTestDevice(text2) && TryReadRttSample(evt, out var rtt, out var proc))
				{
					_rtt.Add((rtt, proc));
					if (_rtt.Count > 256)
					{
						_rtt.RemoveAt(0);
					}
				}
			}
		}
	}

	private bool IsTestDevice(string ip)
	{
		return _devices.Any((CalibrationDeviceEvidence d) => string.Equals(d.Identity.TargetAddress, ip, StringComparison.OrdinalIgnoreCase));
	}

	private static string? NormalizedIp(BackendEvent evt)
	{
		if (!IPAddress.TryParse(evt.GetString("ip"), out IPAddress address))
		{
			return null;
		}
		return address.ToString();
	}

	internal static int? ReportedLatencyMs(BackendEvent evt)
	{
		if (!evt.TryGet("latency", out var value) || value.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		int? num = null;
		if (value.TryGetProperty("entries", out var value2) && value2.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in value2.EnumerateArray())
			{
				int? num2 = ReadPositiveMs(item, "output_us", 1000m);
				if (num2.HasValue && (!num.HasValue || num2.Value > num.Value))
				{
					num = num2;
				}
			}
		}
		int? num3 = num;
		if (!num3.HasValue)
		{
			num = ReadPositiveMs(value, "advertised_min_ms", 1m);
		}
		num3 = num;
		if (!num3.HasValue)
		{
			num = ReadPositiveMs(value, "raop_audio_latency_ms", 1m);
		}
		return num;
	}

	private static int? ReadPositiveMs(JsonElement parent, string name, decimal divisor)
	{
		if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var value2) || value2 <= 0m)
		{
			return null;
		}
		decimal num = decimal.Ceiling(value2 / divisor);
		if (!(num <= 2147483647m))
		{
			return null;
		}
		return (int)num;
	}

	internal static bool TryReadRttSample(BackendEvent evt, out double rtt, out double proc)
	{
		rtt = evt.GetDouble("ms") ?? double.NaN;
		proc = evt.GetDouble("proc_ms").GetValueOrDefault();
		if (!double.IsFinite(proc) || proc < 0.0)
		{
			proc = 0.0;
		}
		if (double.IsFinite(rtt) && rtt > 0.0)
		{
			return rtt < 5000.0;
		}
		return false;
	}

	public void BeginWindow(CalibrationHealthSnapshot snapshot)
	{
		lock (_lock)
		{
			_before = snapshot;
			_delta = null;
			_rtt.Clear();
			_windowOpen = true;
		}
	}

	public CalibrationHealthDelta CompleteWindow(CalibrationHealthSnapshot snapshot)
	{
		lock (_lock)
		{
			if (_before == null)
			{
				throw new InvalidOperationException("No baseline telemetry snapshot");
			}
			_windowOpen = false;
			_delta = LatencyCalibrationPolicy.Delta(_before, snapshot);
			if (_before.Members != _devices.Count || snapshot.Members != _devices.Count || !string.Equals(snapshot.Timing, _plan?.Timing, StringComparison.OrdinalIgnoreCase))
			{
				_delta = _delta with
				{
					Validity = (_delta.Validity | TelemetryValidity.TargetSetChanged)
				};
			}
			return _delta;
		}
	}

	public CalibrationResultState BuildResult(int fallbackMs, int floorMs, IReadOnlyCollection<int> confirmed, int currentAppliedMs, string? hearing, string? bssid = null)
	{
		lock (_lock)
		{
			int num = Math.Max(floorMs, fallbackMs);
			SnapshotDelta d = _delta?.ToSnapshotDelta();
			CalibrationDeviceIdentity[] identities = _devices.Select((CalibrationDeviceEvidence calibrationDeviceEvidence) => calibrationDeviceEvidence.Identity).ToArray();
			GroupPtpDiagnosisResult groupPtpDiagnosisResult = GroupPtpDiagnosis.Diagnose((_plan?.Timing == "PTP") ? identities : null, d);
			LongInterruptionResult longInterruptionResult = LongInterruptionTracker.CheckAndRecord(d, bssid ?? _unknownNetworkScope);
			List<CalibrationDisclosureRow> list = new List<CalibrationDisclosureRow>();
			List<TDevEstimate> list2 = new List<TDevEstimate>();
			int? num2 = null;
			bool flag = false;
			foreach (CalibrationDeviceEvidence device in _devices)
			{
				LatencyKnowledgeEntry latencyKnowledgeEntry = (string.IsNullOrEmpty(device.Fingerprint) ? null : _knowledge.GetCandidateKnowledge(device.Fingerprint, new NetworkKey()));
				double? num3 = latencyKnowledgeEntry?.TDevMs;
				double? num4 = num3;
				if (!num4.HasValue)
				{
					num3 = SeedProcessingPrior(device.Identity.Model);
				}
				if (num3.HasValue)
				{
					double valueOrDefault = num3.GetValueOrDefault();
					if (double.IsFinite(valueOrDefault) && valueOrDefault >= 5.0 && valueOrDefault <= 800.0)
					{
						list2.Add(new TDevEstimate(valueOrDefault, 2.0 * valueOrDefault, 0.0));
						goto IL_01c1;
					}
				}
				flag = true;
				goto IL_01c1;
				IL_01c1:
				int? num5 = latencyKnowledgeEntry?.ClampMs;
				if (num5.HasValue && num5.GetValueOrDefault() > 0)
				{
					num2 = Math.Max(num2.GetValueOrDefault(), latencyKnowledgeEntry.ClampMs.Value);
				}
				int? selfReportedMs = device.SelfReportedMs;
				string targetAddress = device.Identity.TargetAddress;
				if (targetAddress != null && _reported.TryGetValue(targetAddress, out var value))
				{
					selfReportedMs = value;
				}
				DeviceClampDisclosureInfo deviceClampDisclosureInfo = DeviceClampDisclosureHelper.FormatDisclosure(selfReportedMs, latencyKnowledgeEntry?.ClampMs);
				if (deviceClampDisclosureInfo.IsVisible)
				{
					list.Add(new CalibrationDisclosureRow(device.Identity.Name, string.Join(" ", new string[2] { deviceClampDisclosureInfo.SelfReportedLatencyText, deviceClampDisclosureInfo.ClampNoticeText }.Where((string t) => !string.IsNullOrEmpty(t)))));
				}
			}
			bool flag2 = num2.GetValueOrDefault() <= 1000;
			if (flag2)
			{
				num = Math.Max(num, num2.GetValueOrDefault());
			}
			bool flag3 = longInterruptionResult.Triggered || string.Equals(hearing, "unsynced", StringComparison.OrdinalIgnoreCase);
			bool flag4 = d != null && identities.Length != 0 && d.Targets.Count((TargetHealth t) => t.Active) == identities.Length && identities.All((CalibrationDeviceIdentity device) => d.Targets.Count((TargetHealth t) => t.Active && t.IsValid && GroupPtpDiagnosis.Matches(device, t)) == 1) && d.Targets.Where((TargetHealth t) => t.Active).All((TargetHealth t) => identities.Count((CalibrationDeviceIdentity device) => GroupPtpDiagnosis.Matches(device, t)) == 1);
			int num6;
			if (flag2 && flag4)
			{
				CalibrationPlan? plan = _plan;
				if ((object)plan != null && plan.SupportsLiveLatencyTuning && d != null && d.IsValid && d.Packets >= 100 && d.Lateness.Total != 0 && d.RtxGap != null && (d.PathDelayCount != 0 || _rtt.Count > 0) && !flag && list2.Count == _devices.Count && _devices.Count > 0 && _delta != null && LatencyCalibrationPolicy.IsHealthy(_delta))
				{
					num6 = ((!flag3) ? 1 : 0);
					goto IL_04b3;
				}
			}
			num6 = 0;
			goto IL_04b3;
			IL_04b3:
			bool flag5 = (byte)num6 != 0;
			LatencyRecommendation latencyRecommendation = null;
			if (flag5)
			{
				NetworkStats n = LatencyCalibrationPolicy.CalculateNetworkStats(d, _rtt);
				TDevEstimate tdev = new TDevEstimate(list2.Max((TDevEstimate e) => e.Value), list2.Max((TDevEstimate e) => e.Width), list2.Min((TDevEstimate e) => e.Low));
				latencyRecommendation = LatencyCalibrationPolicy.ComputeRecommendation((!_devices.All((CalibrationDeviceEvidence x) => x.Identity.Model.Contains("AudioAccessory", StringComparison.OrdinalIgnoreCase) || x.Identity.Model.Contains("HomePod", StringComparison.OrdinalIgnoreCase))) ? DeviceGrade.CPlus : DeviceGrade.A, n, tdev, d, floorMs, num2);
			}
			int num7 = (flag3 ? Math.Max(floorMs, currentAppliedMs) : ((latencyRecommendation != null) ? ((int)Math.Ceiling(latencyRecommendation.Balanced)) : num));
			if (num7 > 1000 || num7 < floorMs || ((object)latencyRecommendation != null && latencyRecommendation.Stable > 1000.0))
			{
				latencyRecommendation = null;
				num7 = num;
				flag5 = false;
			}
			if (!flag2)
			{
				latencyRecommendation = null;
				flag5 = false;
				num7 = Math.Clamp(currentAppliedMs, 20, 1000);
			}
			string sourceText = ((!flag2) ? L("Calibration_UnsupportedReceiverLimit", "The device limit exceeds the supported range. This setting cannot be applied; the current setting is retained.") : (flag3 ? L("Calibration_DiagnosticNoAutoUp", "Kept the current setting; timing advice alone does not increase delay.") : (flag5 ? L("Calibration_TelemetryRecommendation", "Calculated from this test's network samples and device priors; listening confirmation is still required.") : L("Calibration_TelemetryIncomplete", "Data is incomplete or this mode is unsupported. Using a previously confirmed or conservative setting, not a measured recommendation."))));
			Result = new CalibrationResultState
			{
				CanApply = flag2,
				UsesTelemetry = flag5,
				ShowTiers = (latencyRecommendation != null),
				RecommendedMs = num7,
				SelectedMs = num7,
				NeedsListeningConfirmation = (flag2 && !confirmed.Contains(num7)),
				SourceText = sourceText,
				FastText = (latencyRecommendation?.GetFastDisplayLabel() ?? ""),
				BalancedText = (latencyRecommendation?.GetBalancedDisplayLabel() ?? ""),
				StableText = (latencyRecommendation?.GetStableDisplayLabel() ?? ""),
				FastMs = (int)Math.Ceiling(latencyRecommendation?.Fast ?? ((double)num7)),
				BalancedMs = (int)Math.Ceiling(latencyRecommendation?.Balanced ?? ((double)num7)),
				StableMs = (int)Math.Ceiling(latencyRecommendation?.Stable ?? ((double)num7)),
				ClockVisible = groupPtpDiagnosisResult.IsVisible,
				ClockText = groupPtpDiagnosisResult.SummaryText,
				GapVisible = longInterruptionResult.ShouldPrompt,
				GapText = (longInterruptionResult.ShouldPrompt ? longInterruptionResult.NoticeText : ""),
				DeviceDisclosures = list
			};
			return Result;
		}
	}

	public void Select(int value)
	{
		lock (_lock)
		{
			Result = Result with
			{
				SelectedMs = Math.Clamp(value, 20, 1000)
			};
		}
	}

	public void ApplySelection(Action<int> existingProfileApply)
	{
		lock (_lock)
		{
			if (Result.CanApply)
			{
				existingProfileApply(Result.SelectedMs);
			}
		}
	}

	private static string L(string key, string fallback)
	{
		return LocalizationManager.Instance.GetString(key, fallback);
	}

	private static double? SeedProcessingPrior(string model)
	{
		return model.Trim().ToLowerInvariant() switch
		{
			"audioaccessory6,1" => 10.0, 
			"audioaccessory5,1" => 10.0, 
			"appletv6,2" => 5.0, 
			"airport4,107" => 25.0, 
			_ => null, 
		};
	}
}
