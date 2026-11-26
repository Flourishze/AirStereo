namespace AirStereo2.Services;

public sealed record DeviceTransition(DeviceConnectionState State, DeviceEffects Effects, string? FailureReason = null, string? EventIp = null, bool PasswordRejected = false)
{
	public bool Has(DeviceEffects effect)
	{
		return (Effects & effect) == effect;
	}
}
