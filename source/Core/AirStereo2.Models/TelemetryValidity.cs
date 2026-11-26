using System;

namespace AirStereo2.Models;

[Flags]
public enum TelemetryValidity
{
	Valid = 0,
	MissingFields = 1,
	InvalidData = 2,
	CounterReset = 4,
	SessionChanged = 8,
	MissingSessionEpoch = 0x10,
	TargetSetChanged = 0x20,
	AmbiguousTarget = 0x40
}
