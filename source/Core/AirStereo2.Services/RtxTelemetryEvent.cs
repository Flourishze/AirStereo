namespace AirStereo2.Services;

public sealed record RtxTelemetryEvent(string Ip, ulong Requests, ulong RequestedPackets, ulong ResentPackets, long? TimestampNs);
