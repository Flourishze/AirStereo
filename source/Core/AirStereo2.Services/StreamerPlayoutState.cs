namespace AirStereo2.Services;

public sealed record StreamerPlayoutState(SessionSnapshot Snapshot, BindingToken ActiveBindingToken, int? AppliedOffsetMs);
