using System;

namespace AirStereo2.Services;

public sealed record HomePodPairingEvidence(string Identity, string ObservationIdentity, string TightSyncId, string Source, DateTimeOffset? ObservedAt, DateTimeOffset? ExpiresAt, bool IsFresh);
