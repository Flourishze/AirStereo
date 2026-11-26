using System;
using System.Collections.Generic;

namespace AirStereo2.ViewModels;

public sealed record CalibrationResultState
{
	public bool CanApply { get; init; } = true;

	public bool UsesTelemetry { get; init; }

	public bool ShowTiers { get; init; }

	public bool NeedsListeningConfirmation { get; init; }

	public int RecommendedMs { get; init; }

	public int SelectedMs { get; init; }

	public string SourceText { get; init; } = "";

	public string FastText { get; init; } = "";

	public string BalancedText { get; init; } = "";

	public string StableText { get; init; } = "";

	public int FastMs { get; init; }

	public int BalancedMs { get; init; }

	public int StableMs { get; init; }

	public bool ClockVisible { get; init; }

	public string ClockText { get; init; } = "";

	public bool GapVisible { get; init; }

	public string GapText { get; init; } = "";

	public IReadOnlyList<CalibrationDisclosureRow> DeviceDisclosures { get; init; } = Array.Empty<CalibrationDisclosureRow>();
}
