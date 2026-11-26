using System;

namespace AirStereo2.Services;

internal static class CaptureStatusPresentation
{
	internal const string OccupiedEvent = "[Capture] OCCUPIED";

	private const string FailedPrefix = "[Capture] FAILED ";

	internal static bool ShouldOfferVirtualDeviceSwitch(string? logLine, int captureSourceIndex)
	{
		if (captureSourceIndex == 0)
		{
			return string.Equals(logLine, "[Capture] OCCUPIED", StringComparison.Ordinal);
		}
		return false;
	}

	internal static bool TryGetCurrentSourceFailure(string? logLine, int captureSourceIndex, out string reason)
	{
		reason = string.Empty;
		if (string.IsNullOrWhiteSpace(logLine) || !logLine.StartsWith("[Capture] FAILED ", StringComparison.Ordinal))
		{
			return false;
		}
		string text = logLine.Substring("[Capture] FAILED ".Length).Trim();
		int num = text.IndexOf(' ');
		if (num <= 0 || num == text.Length - 1)
		{
			return false;
		}
		string a = text.Substring(0, num);
		if (!string.Equals(a, captureSourceIndex switch
		{
			0 => "DEFAULT", 
			1 => "DEVICE", 
			2 => "PROCESS", 
			_ => string.Empty, 
		}, StringComparison.Ordinal))
		{
			return false;
		}
		reason = text.Substring(num + 1).Trim();
		return reason.Length > 0;
	}
}
