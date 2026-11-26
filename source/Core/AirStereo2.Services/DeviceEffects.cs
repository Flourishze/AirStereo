using System;

namespace AirStereo2.Services;

[Flags]
public enum DeviceEffects
{
	None = 0,
	StopConnectTimeout = 1,
	ClearCalibrationText = 2,
	ShowCalibrationSlowText = 4,
	ClearFailure = 8,
	ApplyConnectedDefaults = 0x10,
	PushVolume = 0x20,
	FailFromBackend = 0x40,
	FailPinPairing = 0x80,
	ShowPinFailedDialog = 0x100,
	StopBackendRetries = 0x200,
	AddNetworkHint = 0x400,
	StartConnectTimeout = 0x800,
	RequestAccessPassword = 0x1000
}
