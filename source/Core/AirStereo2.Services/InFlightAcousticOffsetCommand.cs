namespace AirStereo2.Services;

public sealed record InFlightAcousticOffsetCommand(long RequestId, int OffsetMs, SessionSnapshot Snapshot, BindingToken Token);
