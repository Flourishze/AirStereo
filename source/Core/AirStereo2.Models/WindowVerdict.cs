namespace AirStereo2.Models;

public enum WindowVerdict
{
	Valid,
	InvalidTooFewPackets,
	InsufficientEvidence,
	SourceGlitch,
	LinkFailure,
	RtxOverBudget
}
