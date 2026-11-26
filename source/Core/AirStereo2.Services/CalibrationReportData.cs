using System;
using System.Collections.Generic;
using AirStereo2.Models;

namespace AirStereo2.Services;

public sealed class CalibrationReportData
{
	public DateTime StartedAtUtc { get; init; }

	public DateTime CompletedAtUtc { get; set; }

	public string AppVersion { get; init; } = string.Empty;

	public string Topology { get; init; } = string.Empty;

	public string Timing { get; init; } = string.Empty;

	public string Codec { get; set; } = string.Empty;

	public int? RecommendedLatencyMs { get; set; }

	public int? UserLatencyMs { get; set; }

	public List<CalibrationDeviceIdentity> Devices { get; init; } = new List<CalibrationDeviceIdentity>();

	public List<CalibrationSegmentReport> Segments { get; init; } = new List<CalibrationSegmentReport>();

	public List<string> BackendLines { get; init; } = new List<string>();
}
