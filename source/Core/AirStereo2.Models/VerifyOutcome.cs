namespace AirStereo2.Models;

public sealed record VerifyOutcome(VerifyAction Action, string UserMessage, bool ShouldIncreaseLatency);
