using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AirStereo2.Models;

public sealed class EqPresetPackageEntry
{
	[JsonPropertyName("kind")]
	public string Kind { get; set; } = "user";

	[JsonPropertyName("id")]
	public string? Id { get; set; }

	[JsonPropertyName("name")]
	public string? Name { get; set; }

	[JsonPropertyName("gainsDb")]
	public List<double>? GainsDb { get; set; }
}
