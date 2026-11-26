using System.Collections.Generic;

namespace AirStereo2.Models;

public sealed record CalibrationPlan(string Topology, string Timing, IReadOnlyList<int> CandidatesMs, bool SupportsLiveLatencyTuning);
