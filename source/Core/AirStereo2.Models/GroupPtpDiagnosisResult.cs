using System.Collections.Generic;

namespace AirStereo2.Models;

public sealed record GroupPtpDiagnosisResult(GroupPtpStatus Status, bool IsVisible, string? OffendingDeviceName, string SummaryText, string? ActionSuggestion, IReadOnlyDictionary<string, double> MemberSigmas);
