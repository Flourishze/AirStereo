namespace AirStereo2.Models;

public enum VerifyAction
{
	Pass,
	InsufficientEvidence,
	SourceGlitchRetry,
	UserChoppyRecompute,
	NetworkRetryRecompute,
	GoPtpDiagnosis,
	BumpVolumeRetry
}
