using System;

namespace AirStereo2.Models;

public static class SafeLatencyResolutionPolicy
{
	public static ResolvedLatencyKnowledge Resolve(string fingerprint, NetworkKey currentNetwork, LatencyKnowledgeEntry? cloudEntry, DeviceQuirksEntry? localQuirks)
	{
		if (localQuirks?.SafeProfiles != null && localQuirks.SafeProfiles.Count > 0)
		{
			foreach (SafeProfileEntry value in localQuirks.SafeProfiles.Values)
			{
				if (value.NetworkKey != null && value.NetworkKey.Matches(currentNetwork))
				{
					return new ResolvedLatencyKnowledge(value.VerifiedSafeLatencyMs, KnowledgeSourcePriority.LocalFinalVerification, "LocalVerified", RequiresLocalFinalVerification: false);
				}
			}
		}
		if (localQuirks != null && localQuirks.VerifiedSafeLatencyMs.HasValue && !localQuirks.ExpiresAfterNetworkChange)
		{
			return new ResolvedLatencyKnowledge(localQuirks.VerifiedSafeLatencyMs.Value, KnowledgeSourcePriority.LocalObserved, "LocalObserved", RequiresLocalFinalVerification: true);
		}
		if (cloudEntry != null && string.Equals(cloudEntry.Confidence, "validated", StringComparison.OrdinalIgnoreCase))
		{
			if (string.Equals(cloudEntry.NetworkScope, "network_bound", StringComparison.OrdinalIgnoreCase) && (cloudEntry.BoundNetworkKey == null || !cloudEntry.BoundNetworkKey.Matches(currentNetwork)))
			{
				return FallbackDefault(currentNetwork);
			}
			if (cloudEntry.VerifiedSafeLatencyMs.HasValue)
			{
				return new ResolvedLatencyKnowledge(cloudEntry.VerifiedSafeLatencyMs.Value, KnowledgeSourcePriority.CloudValidated, "CloudValidated", RequiresLocalFinalVerification: true);
			}
		}
		if (cloudEntry != null && cloudEntry.VerifiedSafeLatencyMs.HasValue)
		{
			if (string.Equals(cloudEntry.NetworkScope, "network_bound", StringComparison.OrdinalIgnoreCase) && (cloudEntry.BoundNetworkKey == null || !cloudEntry.BoundNetworkKey.Matches(currentNetwork)))
			{
				return FallbackDefault(currentNetwork);
			}
			return new ResolvedLatencyKnowledge(cloudEntry.VerifiedSafeLatencyMs.Value, KnowledgeSourcePriority.CloudSeed, "CloudSeed", RequiresLocalFinalVerification: true);
		}
		return FallbackDefault(currentNetwork);
	}

	private static ResolvedLatencyKnowledge FallbackDefault(NetworkKey network)
	{
		return new ResolvedLatencyKnowledge(string.Equals(network.Transport, "wired", StringComparison.OrdinalIgnoreCase) ? 60 : 80, KnowledgeSourcePriority.CloudSeed, "FallbackDefault", RequiresLocalFinalVerification: true);
	}
}
