namespace AirStereo2.Services;

internal sealed record LocalOutputMuteLease(string DeviceId, bool WasMuted, int OwnerProcessId = 0, long OwnerStartTimeUtcTicks = 0L);
