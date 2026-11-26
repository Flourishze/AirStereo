using System;

namespace AirStereo2.Services;

public static class SingleInstancePolicy
{
	public static bool ShouldEnforce(string[]? args)
	{
		return true;
	}

	public static bool ShouldActivateExisting(string[]? args)
	{
		return !WindowsStartupService.IsSilentStart(args);
	}
}
