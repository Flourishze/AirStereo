using System.Collections.Generic;
using System.Linq;

namespace AirStereo2.Services;

public static class DeviceDockSelection
{
	public static DeviceDockClickAction ResolveClick(bool isConnected, bool isConnecting, int clickCount, bool connectionStartedByFirstClick = false)
	{
		if (clickCount >= 2)
		{
			if (connectionStartedByFirstClick || isConnecting)
			{
				return DeviceDockClickAction.Ignore;
			}
			if (!isConnected)
			{
				return DeviceDockClickAction.Connect;
			}
			return DeviceDockClickAction.Disconnect;
		}
		if (isConnected)
		{
			return DeviceDockClickAction.Select;
		}
		if (!isConnecting)
		{
			return DeviceDockClickAction.Connect;
		}
		return DeviceDockClickAction.Ignore;
	}

	public static bool CanSelect(bool? isConnected)
	{
		return isConnected != false;
	}

	public static T? Toggle<T>(T? current, T clicked) where T : class
	{
		if (current != clicked)
		{
			return clicked;
		}
		return null;
	}

	public static bool ShouldCollapseOnRemoval<T>(T? selected, IEnumerable<T> devices) where T : class
	{
		if (selected != null)
		{
			if (devices != null)
			{
				return !devices.Contains<T>(selected);
			}
			return true;
		}
		return false;
	}

	public static int NormalizePlacement(int index)
	{
		return (index == 1) ? 1 : 0;
	}

	public static DockEdge EdgeFromIndex(int index)
	{
		if (NormalizePlacement(index) != 1)
		{
			return DockEdge.Bottom;
		}
		return DockEdge.Right;
	}
}
