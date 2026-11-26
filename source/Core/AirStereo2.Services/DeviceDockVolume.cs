using System;

namespace AirStereo2.Services;

public static class DeviceDockVolume
{
	public const double PercentPerNotch = 2.0;

	public const double PercentPerArrow = 2.0;

	public const double PercentPerPage = 10.0;

	public const double DragTravel = 160.0;

	public const string AdjustingTag = "adjusting";

	public static readonly TimeSpan AdjustingHold = TimeSpan.FromMilliseconds(1200.0);

	public static double Clamp(double volume)
	{
		if (!double.IsFinite(volume))
		{
			return 0.0;
		}
		return Math.Clamp(volume, 0.0, 100.0);
	}

	public static double AfterWheel(double target, int wheelDelta)
	{
		return Clamp(target + (double)wheelDelta / 120.0 * 2.0);
	}

	public static double AfterKey(double volume, DockVolumeKey key)
	{
		return key switch
		{
			DockVolumeKey.Up => Clamp(volume + 2.0), 
			DockVolumeKey.Down => Clamp(volume - 2.0), 
			DockVolumeKey.PageUp => Clamp(volume + 10.0), 
			DockVolumeKey.PageDown => Clamp(volume - 10.0), 
			DockVolumeKey.Home => 0.0, 
			DockVolumeKey.End => 100.0, 
			_ => Clamp(volume), 
		};
	}

	public static double AfterDrag(double startVolume, double raisedBy)
	{
		return Clamp(startVolume + raisedBy / 160.0 * 100.0);
	}

	public static int Applied(double volume)
	{
		return (int)Math.Round(Clamp(volume), MidpointRounding.AwayFromZero);
	}

	public static bool RevealsLevel(bool isConnected, bool isHovered, bool isAdjusting, bool isKeyboardFocused)
	{
		if (isConnected)
		{
			return isHovered || isAdjusting || isKeyboardFocused;
		}
		return false;
	}
}
