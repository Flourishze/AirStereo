namespace AirStereo2.Services;

public class AudioDeviceItem
{
	public string DeviceId { get; set; } = "";

	public string Name { get; set; } = "";

	public string Flow { get; set; } = "";

	public bool IsLoopback { get; set; }

	public string DisplayName => Name + " (" + Flow + ")";
}
