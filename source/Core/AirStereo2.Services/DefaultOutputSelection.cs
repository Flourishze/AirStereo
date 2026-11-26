using System.Collections.Generic;
using System.Linq;

namespace AirStereo2.Services;

public static class DefaultOutputSelection
{
	public static bool? EntryState(IReadOnlyList<bool> memberFlags, bool isNativeStereoPair)
	{
		if (memberFlags.Count == 0)
		{
			return false;
		}
		if (isNativeStereoPair)
		{
			return memberFlags.Any((bool flag) => flag);
		}
		if (memberFlags.All((bool flag) => flag))
		{
			return true;
		}
		if (!memberFlags.Any((bool flag) => flag))
		{
			return false;
		}
		return null;
	}

	public static bool FlagAfterClick(bool? current, bool? requested)
	{
		if (requested != true)
		{
			if (!current.HasValue)
			{
				return requested == false;
			}
			return false;
		}
		return true;
	}
}
