using System;

namespace AirStereo2.Models;

public static class AcousticOffsetConfig
{
	public const int MinOffsetMs = -250;

	public const int MaxOffsetMs = 250;

	public const int StepMs = 1;

	public const int DefaultOffsetMs = 0;

	public static int Clamp(int offsetMs)
	{
		return Math.Clamp(offsetMs, -250, 250);
	}
}
