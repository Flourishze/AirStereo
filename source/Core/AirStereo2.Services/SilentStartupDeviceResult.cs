namespace AirStereo2.Services;

public sealed record SilentStartupDeviceResult
{
	public string StableId { get; init; } = string.Empty;

	public string DisplayName { get; init; } = string.Empty;

	public bool Connected { get; init; }

	public bool Discovered { get; init; }

	public bool ActionRequired { get; init; }

	public string? Detail { get; init; }
}
