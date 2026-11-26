using System.Collections.Generic;

namespace AirStereo2.Services;

public sealed record HomePodAssessment(HomePodAssessmentState State, string Reason, IReadOnlyList<HomePodPairingEvidence> Evidence);
