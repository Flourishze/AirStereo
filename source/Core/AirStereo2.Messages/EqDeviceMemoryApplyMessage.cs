using AirStereo2.Models;

namespace AirStereo2.Messages;

public sealed record EqDeviceMemoryApplyMessage(string DeviceIp, DeviceEqMemorySnapshot Snapshot);
