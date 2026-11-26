using System.Collections.Generic;

namespace AirStereo2.Models;

public sealed record GroupBottleneckResult(bool IsVisible, string SummaryText, string? SuggestionText, IReadOnlyList<MemberLatencyEstimate> MemberEstimates, bool IsTie);
