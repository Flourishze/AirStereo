namespace AirStereo2.Services;

public sealed record DeviceConnectionState(bool IsConnected = false, bool IsConnecting = false, bool IsStandby = false, bool IsPreempted = false, bool IsPinRequested = false, bool IsCalibrationSlow = false, bool PreserveReceiverVolume = false);
