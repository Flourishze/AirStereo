namespace AirStereo2.Services;

public sealed class VerifiedDeviceQuirkEntry
{
	public string Fingerprint { get; set; } = string.Empty;

	public string Model { get; set; } = string.Empty;

	public string Manufacturer { get; set; } = string.Empty;

	public string SrcversMajorMinor { get; set; } = string.Empty;

	public double TDevMs { get; set; }

	public string Confidence { get; set; } = "seed";

	public double ConfidenceWeight { get; set; } = 0.3;

	public int SampleCount { get; set; } = 1;

	public double P10Ms { get; set; }

	public double P90Ms { get; set; }

	public double SpreadMs { get; set; }

	public int? ClampMs { get; set; }

	public string? Notes { get; set; }
}
