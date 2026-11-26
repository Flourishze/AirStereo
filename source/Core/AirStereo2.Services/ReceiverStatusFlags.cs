namespace AirStereo2.Services;

public static class ReceiverStatusFlags
{
	public const ulong StereoPartnerUnreachable = 16384uL;

	public const ulong ReceiverSessionActive = 131072uL;

	public static bool IsStereoPartnerUnreachable(ulong statusFlags)
	{
		return (statusFlags & 0x4000) != 0;
	}

	public static bool IsReceiverSessionActive(ulong statusFlags)
	{
		return (statusFlags & 0x20000) != 0;
	}
}
