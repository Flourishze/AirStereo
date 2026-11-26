namespace AirStereo2.Models;

public sealed record CalibrationDeviceIdentity(string StableId, string Name, string Model, bool SupportsAirPlay2, string? TargetAddress = null);
