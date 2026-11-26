using System;

namespace AirStereo2.Services;

public static class ReceiverAccessPolicy
{
	public const ulong PasswordRequiredFlag = 128uL;

	public static ReceiverAccess Classify(ulong statusFlags, bool advertisesPassword, int? accessControlLevel)
	{
		if (advertisesPassword || (statusFlags & 0x80) != 0L)
		{
			return ReceiverAccess.PasswordRequired;
		}
		if (accessControlLevel != 2)
		{
			return ReceiverAccess.Open;
		}
		return ReceiverAccess.HomeMembersOnly;
	}

	public static bool ParsePasswordFlag(string? value)
	{
		string text = value?.Trim() ?? string.Empty;
		if (!(text == "1") && !text.Equals("true", StringComparison.OrdinalIgnoreCase))
		{
			return text.Equals("yes", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static int? ParseAccessControlLevel(string? value)
	{
		if (!int.TryParse(value?.Trim(), out var result) || result < 0)
		{
			return null;
		}
		return result;
	}

	public static bool ShouldPromptBeforeConnect(ReceiverAccess access, bool hasSavedPassword, bool silent)
	{
		if (access == ReceiverAccess.PasswordRequired && !hasSavedPassword)
		{
			return !silent;
		}
		return false;
	}
}
