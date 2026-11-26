namespace AirStereo2.Services;

public sealed class DefaultOutputDeviceRecord
{
	public string StableId { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	public string Model { get; set; } = string.Empty;

	public string LastKnownIp { get; set; } = string.Empty;

	public string? GroupKey { get; set; }
}
