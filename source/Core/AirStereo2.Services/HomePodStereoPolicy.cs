using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using AirStereo2.ViewModels;

namespace AirStereo2.Services;

public static class HomePodStereoPolicy
{
	public static HomePodAssessment Assess(IEnumerable<DeviceViewModel> devices, DateTimeOffset? now = null)
	{
		DateTimeOffset instant = now ?? DateTimeOffset.UtcNow;
		List<DeviceViewModel> list = devices.Where((DeviceViewModel device) => device.IsHomePodFamily).ToList();
		IReadOnlyList<DeviceViewModel> pods = DistinctPhysicalDevices(list);
		List<HomePodPairingEvidence> evidence = pods.Select((DeviceViewModel device) => new HomePodPairingEvidence(PhysicalIdentity(device), device.GroupingEvidenceIdentity, device.TightSyncId.Trim(), device.GroupingEvidenceSource, device.GroupingObservedAt, device.GroupingExpiresAt, device.HasFreshGroupingEvidence(instant))).ToList();
		if (pods.Count < 2)
		{
			return Result(HomePodAssessmentState.Unknown, "fewer-than-two-physical-homepods");
		}
		if (evidence.Any((HomePodPairingEvidence item) => !item.IsFresh))
		{
			return Result(HomePodAssessmentState.Unknown, "missing-stale-or-mismatched-authoritative-evidence");
		}
		if (evidence.Any((HomePodPairingEvidence item) => string.IsNullOrWhiteSpace(item.TightSyncId)))
		{
			return Result(HomePodAssessmentState.Unknown, "missing-tight-sync-id");
		}
		if (list.Any((DeviceViewModel row) => row.HasFreshGroupingEvidence(instant) && pods.Any((DeviceViewModel pod) => SamePhysicalDevice(pod, row) && !string.Equals(pod.TightSyncId.Trim(), row.TightSyncId.Trim(), StringComparison.OrdinalIgnoreCase))))
		{
			return Result(HomePodAssessmentState.Unknown, "conflicting-physical-device-observations");
		}
		List<IGrouping<string, HomePodPairingEvidence>> source = evidence.GroupBy<HomePodPairingEvidence, string>((HomePodPairingEvidence item) => item.TightSyncId, StringComparer.OrdinalIgnoreCase).ToList();
		if (source.All((IGrouping<string, HomePodPairingEvidence> pair) => pair.Count() == 2))
		{
			return Result(HomePodAssessmentState.ConfirmedPair, "complete-tight-sync-pairs");
		}
		if (source.Any((IGrouping<string, HomePodPairingEvidence> pair) => pair.Count() > 2))
		{
			return Result(HomePodAssessmentState.Unknown, "ambiguous-tight-sync-membership");
		}
		return Result(HomePodAssessmentState.ConfirmedIndependent, "distinct-tight-sync-ids-with-unpaired-selection");
		HomePodAssessment Result(HomePodAssessmentState state, string reason)
		{
			return new HomePodAssessment(state, reason, evidence);
		}
	}

	public static bool IsNativeStereoPair(IEnumerable<DeviceViewModel> devices, DateTimeOffset? now = null)
	{
		List<DeviceViewModel> devices2 = devices.ToList();
		IReadOnlyList<DeviceViewModel> readOnlyList = DistinctPhysicalDevices(devices2);
		if (readOnlyList.Count == 2 && readOnlyList.All((DeviceViewModel device) => device.IsHomePodFamily))
		{
			return Assess(devices2, now).State == HomePodAssessmentState.ConfirmedPair;
		}
		return false;
	}

	public static bool ShouldWarn(IEnumerable<DeviceViewModel> prospectiveDevices)
	{
		return Assess(prospectiveDevices).State == HomePodAssessmentState.ConfirmedIndependent;
	}

	public static string PhysicalIdentity(DeviceViewModel device)
	{
		if (AirPlayDeviceIdentity.IsDeviceScoped(device.StableId))
		{
			string text = device.StableId.Trim();
			if (text.StartsWith("deviceid:", StringComparison.OrdinalIgnoreCase))
			{
				string text2 = AirPlayDeviceIdentity.NormalizeDeviceId(text.Substring("deviceid:".Length));
				if (!string.IsNullOrEmpty(text2))
				{
					return "deviceid:" + text2.ToLowerInvariant();
				}
			}
			return text.ToLowerInvariant();
		}
		if (!string.IsNullOrWhiteSpace(device.AirPlayInstanceName))
		{
			return "airplay:" + device.AirPlayInstanceName.Trim().ToLowerInvariant();
		}
		string text3 = AirPlayDeviceIdentity.ExtractRaopDeviceId(device.RaopInstanceName);
		if (!string.IsNullOrWhiteSpace(text3))
		{
			return "raop-device:" + text3.Trim().ToLowerInvariant();
		}
		if (!string.IsNullOrWhiteSpace(device.RaopInstanceName))
		{
			return "raop:" + device.RaopInstanceName.Trim().ToLowerInvariant();
		}
		if (!string.IsNullOrWhiteSpace(device.IPAddress))
		{
			return "ip:" + NormalizeIp(device.IPAddress).ToLowerInvariant();
		}
		return string.Empty;
	}

	public static IReadOnlyList<DeviceViewModel> DistinctPhysicalDevices(IEnumerable<DeviceViewModel> devices)
	{
		List<DeviceViewModel> list = new List<DeviceViewModel>();
		foreach (DeviceViewModel device in devices)
		{
			if (!list.Any((DeviceViewModel existing) => SamePhysicalDevice(existing, device)))
			{
				list.Add(device);
			}
		}
		return list;
	}

	private static bool SamePhysicalDevice(DeviceViewModel first, DeviceViewModel second)
	{
		if (first == second)
		{
			return true;
		}
		if (AirPlayDeviceIdentity.IsDeviceScoped(first.StableId) && AirPlayDeviceIdentity.IsDeviceScoped(second.StableId))
		{
			return AirPlayDeviceIdentity.IdsEqual(first.StableId, second.StableId);
		}
		if (!string.IsNullOrWhiteSpace(first.AirPlayInstanceName) && !string.IsNullOrWhiteSpace(second.AirPlayInstanceName))
		{
			return EqualNonEmpty(first.AirPlayInstanceName, second.AirPlayInstanceName);
		}
		string text = AirPlayDeviceIdentity.ExtractRaopDeviceId(first.RaopInstanceName);
		string text2 = AirPlayDeviceIdentity.ExtractRaopDeviceId(second.RaopInstanceName);
		if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(text2))
		{
			return EqualNonEmpty(text, text2);
		}
		if (!string.IsNullOrWhiteSpace(first.RaopInstanceName) && !string.IsNullOrWhiteSpace(second.RaopInstanceName))
		{
			return EqualNonEmpty(first.RaopInstanceName, second.RaopInstanceName);
		}
		return EqualNonEmpty(NormalizeIp(first.IPAddress), NormalizeIp(second.IPAddress));
	}

	private static string NormalizeIp(string value)
	{
		if (!IPAddress.TryParse(value.Trim(), out IPAddress address))
		{
			return value.Trim();
		}
		return address.ToString();
	}

	private static bool EqualNonEmpty(string first, string second)
	{
		if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second))
		{
			return string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}
}
