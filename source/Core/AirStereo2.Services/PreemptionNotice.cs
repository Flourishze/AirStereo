namespace AirStereo2.Services;

public static class PreemptionNotice
{
	public static bool ShouldNotify(int takeover)
	{
		return takeover == 1;
	}
}
