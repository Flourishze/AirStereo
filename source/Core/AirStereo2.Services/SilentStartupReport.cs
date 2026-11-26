using System;
using System.Collections.Generic;
using System.Linq;

namespace AirStereo2.Services;

public sealed class SilentStartupReport
{
	public SilentStartupOutcomeKind Kind { get; init; }

	public IReadOnlyList<SilentStartupDeviceResult> Devices { get; init; } = Array.Empty<SilentStartupDeviceResult>();

	public IEnumerable<SilentStartupDeviceResult> Unresolved => Devices.Where((SilentStartupDeviceResult d) => !d.Connected);
}
