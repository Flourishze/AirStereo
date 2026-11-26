namespace AirStereo2.Services;

public sealed record FirewallFixResult(FirewallFixOutcome Outcome, int? ExitCode = null);
