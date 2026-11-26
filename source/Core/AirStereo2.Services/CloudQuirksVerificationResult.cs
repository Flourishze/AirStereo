namespace AirStereo2.Services;

public sealed record CloudQuirksVerificationResult(bool IsValid, string? ErrorMessage, VerifiedCloudQuirksDatabase? Database = null)
{
	public static CloudQuirksVerificationResult Success(VerifiedCloudQuirksDatabase db)
	{
		return new CloudQuirksVerificationResult(IsValid: true, null, db);
	}

	public static CloudQuirksVerificationResult Failed(string error)
	{
		return new CloudQuirksVerificationResult(IsValid: false, error);
	}
}
