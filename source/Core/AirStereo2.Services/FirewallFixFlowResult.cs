namespace AirStereo2.Services;

public sealed record FirewallFixFlowResult(FirewallFixOutcome? Outcome, NetworkCheckResult? After);
