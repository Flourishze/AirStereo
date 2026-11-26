namespace AirStereo2.Services;

public sealed record RtxDetailEvent(string Ip, ushort FirstSequence, ushort Count, long? TimestampNs);
