namespace AirStereo2.Services;

public static class MainWindowTaskbarPolicy
{
	public static bool HidesToTrayOnMinimize(bool isMinimized, bool showInTaskbar, bool isVisible)
	{
		if (isMinimized && isVisible)
		{
			return !showInTaskbar;
		}
		return false;
	}
}
