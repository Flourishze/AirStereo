using System;

namespace AirStereo2.Services;

public static class AudioCodecPreference
{
	public const string Alac = "ALAC";

	public const string Pcm = "PCM";

	public static bool IsValid(string? value)
	{
		if (!string.Equals(value, "ALAC", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(value, "PCM", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static string Normalize(string? value)
	{
		if (string.Equals(value, "PCM", StringComparison.OrdinalIgnoreCase))
		{
			return "PCM";
		}
		return "ALAC";
	}
}
