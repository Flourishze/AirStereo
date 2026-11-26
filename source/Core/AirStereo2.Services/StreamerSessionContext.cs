using System;

namespace AirStereo2.Services;

public sealed record StreamerSessionContext(ulong Generation, StreamerProtocolMode ProtocolMode, Guid TransportInstanceId);
