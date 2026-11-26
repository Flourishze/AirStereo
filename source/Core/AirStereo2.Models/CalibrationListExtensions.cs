using System.Collections.Generic;

namespace AirStereo2.Models;

internal static class CalibrationListExtensions
{
	public static int IndexOf<T>(this IReadOnlyList<T> source, T value)
	{
		EqualityComparer<T> equalityComparer = EqualityComparer<T>.Default;
		for (int i = 0; i < source.Count; i++)
		{
			if (equalityComparer.Equals(source[i], value))
			{
				return i;
			}
		}
		return -1;
	}
}
