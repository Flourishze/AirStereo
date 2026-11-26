namespace AirStereo2.Models;

public sealed record LongInterruptionResult(bool Triggered, bool ShouldPrompt, string NoticeText, bool IsDataAvailable = true);
