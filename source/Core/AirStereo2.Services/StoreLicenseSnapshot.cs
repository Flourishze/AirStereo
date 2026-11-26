using System;

namespace AirStereo2.Services;

public sealed record StoreLicenseSnapshot(bool IsStoreContextAvailable, bool IsActive, bool IsTrial, DateTimeOffset? ExpirationDate, string StatusText, string? ErrorMessage, StoreEntitlementState EntitlementState)
{
	public bool AllowsNewConnections => StoreLicensePolicy.AllowsNewConnections(EntitlementState);
}
