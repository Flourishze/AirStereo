namespace AirStereo2.Messages;

public record VolumeChangedMessage(int Volume, VolumeChangeOrigin Origin, string? SourceDeviceIp = null);
