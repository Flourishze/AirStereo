using System;

namespace AirStereo2.Services;

public static class StoreLicensePolicy
{
	public static StoreEntitlementState Evaluate(bool developmentBuild, bool storeContextAvailable, bool isActive, bool isTrial, DateTimeOffset? expirationDate, DateTimeOffset now)
	{
		if (developmentBuild)
		{
			return StoreEntitlementState.Development;
		}
		if (!storeContextAvailable)
		{
			return StoreEntitlementState.StoreUnavailable;
		}
		if (isTrial && expirationDate.HasValue)
		{
			DateTimeOffset valueOrDefault = expirationDate.GetValueOrDefault();
			if (valueOrDefault <= now)
			{
				return StoreEntitlementState.Expired;
			}
		}
		if (!isActive)
		{
			return StoreEntitlementState.Inactive;
		}
		if (!isTrial)
		{
			return StoreEntitlementState.Purchased;
		}
		return StoreEntitlementState.Trial;
	}

	public static bool AllowsNewConnections(StoreEntitlementState state)
	{
		if ((uint)(state - 1) <= 2u)
		{
			return true;
		}
		return false;
	}
}
