using System;
using System.Collections.Generic;
using System.Linq;

namespace AirStereo2.Services;

public static class DefaultOutputSummary
{
	public sealed record Group(string Name, IReadOnlyList<string> MemberStableIds, bool IsNativeStereoPair);

	public static IReadOnlyList<string> Names(IReadOnlyList<DefaultOutputDeviceRecord> defaults, Func<string, string?> liveName, IEnumerable<Group> groups)
	{
		List<DefaultOutputDeviceRecord> selected = defaults.Where((DefaultOutputDeviceRecord defaultOutputDeviceRecord) => !string.IsNullOrWhiteSpace(defaultOutputDeviceRecord.StableId)).ToList();
		List<Group> source = groups.Where((Group group) => group.MemberStableIds.Count >= 2 && ((!group.IsNativeStereoPair) ? group.MemberStableIds.All(IsSelected) : group.MemberStableIds.Any(IsSelected))).ToList();
		List<string> list = new List<string>();
		HashSet<Group> hashSet = new HashSet<Group>();
		foreach (DefaultOutputDeviceRecord record in selected)
		{
			Group obj = source.FirstOrDefault((Group candidate) => candidate.MemberStableIds.Any((string id) => AirPlayDeviceIdentity.IdsEqual(id, record.StableId)));
			if (obj != null && !string.IsNullOrWhiteSpace(obj.Name))
			{
				if (hashSet.Add(obj))
				{
					list.Add(obj.Name.Trim());
				}
				continue;
			}
			string text = liveName(record.StableId);
			if (string.IsNullOrWhiteSpace(text))
			{
				text = record.DisplayName;
			}
			list.Add(string.IsNullOrWhiteSpace(text) ? record.StableId.Trim() : text.Trim());
		}
		return list;
		bool IsSelected(string id)
		{
			return selected.Any((DefaultOutputDeviceRecord defaultOutputDeviceRecord) => AirPlayDeviceIdentity.IdsEqual(defaultOutputDeviceRecord.StableId, id));
		}
	}

	public static string Text(IReadOnlyList<string> names, string noneText)
	{
		if (names.Count != 0)
		{
			return string.Join(", ", names);
		}
		return noneText;
	}
}
