using System;

namespace AirStereo2.Services;

public static class AppShutdownPolicy
{
	public static TimeSpan HostStopTimeout { get; } = TimeSpan.FromSeconds(3.0);
}
