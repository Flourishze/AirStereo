using System;
using System.Collections.Generic;
using System.Linq;

namespace AirStereo2.Services;

public static class StartupAutoConnectPolicy
{
	public static bool ShouldRun(bool startupLaunch, bool autoConnectOnLaunch, int defaultOutputCount)
	{
		if (!startupLaunch)
		{
			if (autoConnectOnLaunch)
			{
				return defaultOutputCount > 0;
			}
			return false;
		}
		return true;
	}

	public static bool AwaitsGroupMembers(string? stableId, bool inLiveGroup, IEnumerable<IReadOnlyCollection<string>> savedGroupMemberIds, IReadOnlyCollection<string> discoveredStableIds)
	{
		if (inLiveGroup || string.IsNullOrWhiteSpace(stableId))
		{
			return false;
		}
		return savedGroupMemberIds.Any((IReadOnlyCollection<string> members) => members.Count >= 2 && members.Any((string id) => AirPlayDeviceIdentity.IdsEqual(id, stableId)) && members.Any((string id) => !AirPlayDeviceIdentity.IdsEqual(id, stableId) && !discoveredStableIds.Any((string found) => AirPlayDeviceIdentity.IdsEqual(found, id))));
	}

	public static bool AwaitsAdvertisedPairPartner(bool isHomePodFamily, string? tightSyncId, bool inLiveGroup, IEnumerable<string?> otherDiscoveredTightSyncIds)
	{
		if (inLiveGroup || !isHomePodFamily || string.IsNullOrWhiteSpace(tightSyncId))
		{
			return false;
		}
		string pair = tightSyncId.Trim();
		return !otherDiscoveredTightSyncIds.Any((string other) => !string.IsNullOrWhiteSpace(other) && string.Equals(other.Trim(), pair, StringComparison.OrdinalIgnoreCase));
	}

	public static bool CompletesPartialPair(bool isNativeStereoPair, IReadOnlyCollection<bool> memberActive)
	{
		if (isNativeStereoPair && memberActive.Any((bool active) => active))
		{
			return memberActive.Any((bool active) => !active);
		}
		return false;
	}

	public static bool ConnectsWholeGroup(bool isNativeStereoPair, IReadOnlyCollection<string> memberStableIds, bool everyMemberCarriesAudio, Func<string, bool> isDefaultOutput)
	{
		if (isNativeStereoPair)
		{
			return true;
		}
		if (everyMemberCarriesAudio && memberStableIds.Count >= 2)
		{
			return memberStableIds.All(isDefaultOutput);
		}
		return false;
	}
}
