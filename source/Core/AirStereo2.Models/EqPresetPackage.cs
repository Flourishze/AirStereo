using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AirStereo2.Models;

public sealed class EqPresetPackage
{
	public const string FormatId = "airstereo2-eq-presets";

	public const int CurrentSchemaVersion = 1;

	[JsonPropertyName("format")]
	public string Format { get; set; } = "airstereo2-eq-presets";

	[JsonPropertyName("schemaVersion")]
	public int SchemaVersion { get; set; } = 1;

	[JsonPropertyName("bandCount")]
	public int BandCount { get; set; } = 10;

	[JsonPropertyName("frequenciesHz")]
	public List<double>? FrequenciesHz { get; set; }

	[JsonPropertyName("exportedAt")]
	public string? ExportedAt { get; set; }

	[JsonPropertyName("presets")]
	public List<EqPresetPackageEntry> Presets { get; set; } = new List<EqPresetPackageEntry>();
}
