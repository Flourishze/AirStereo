namespace AirStereo2.Services;

public readonly record struct NowPlayingProgress(long PositionMs, long DurationMs, string State, double Rate);
