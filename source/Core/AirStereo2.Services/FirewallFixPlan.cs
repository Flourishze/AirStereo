using System.Collections.Generic;

namespace AirStereo2.Services;

public sealed record FirewallFixPlan(string Program, IReadOnlyList<string> Profiles);
