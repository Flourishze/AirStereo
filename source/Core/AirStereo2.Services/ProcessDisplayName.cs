using System;

namespace AirStereo2.Services;

public static class ProcessDisplayName
{
	public static string ResolveApplicationName(string processName, string? fileDescription, string? productName, string? sessionDisplayName = null)
	{
		if (!string.IsNullOrWhiteSpace(fileDescription))
		{
			return fileDescription.Trim();
		}
		if (!string.IsNullOrWhiteSpace(productName))
		{
			return productName.Trim();
		}
		if (!string.IsNullOrWhiteSpace(sessionDisplayName))
		{
			return CleanSessionDisplayName(sessionDisplayName);
		}
		if (!string.IsNullOrWhiteSpace(processName))
		{
			return processName.Trim();
		}
		return "?";
	}

	public static string CleanSessionDisplayName(string displayName)
	{
		string text = displayName.Trim();
		if (text.StartsWith("@", StringComparison.Ordinal))
		{
			int num = text.IndexOf("?ms-resource", StringComparison.OrdinalIgnoreCase);
			if (num > 1)
			{
				text = text.Substring(2, num - 2);
			}
			else if (text.Length > 1)
			{
				text = text.TrimStart('@', '{').TrimEnd('}');
			}
		}
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text.Trim();
		}
		return string.Empty;
	}

	public static string FormatSecondaryText(string processName, int processId, int sessionProcessId)
	{
		if (sessionProcessId != 0 && sessionProcessId != processId)
		{
			return $"{processName} · PID {processId} · session {sessionProcessId}";
		}
		return $"{processName} · PID {processId}";
	}
}
