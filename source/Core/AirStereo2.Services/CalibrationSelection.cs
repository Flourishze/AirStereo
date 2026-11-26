using System;
using System.Collections.Generic;
using System.Linq;

namespace AirStereo2.Services;

public static class CalibrationSelection
{
	public sealed record Entry(string? Name, IReadOnlyList<string> MemberNames);

	public static IReadOnlyList<string> EntryNames(IEnumerable<Entry> entries)
	{
		return (from entry in entries
			select string.IsNullOrWhiteSpace(entry.Name) ? string.Join(" + ", (from name in entry.MemberNames
				where !string.IsNullOrWhiteSpace(name)
				select name.Trim()).Distinct<string>(StringComparer.CurrentCultureIgnoreCase)) : entry.Name.Trim() into name
			where name.Length > 0
			select name).Distinct<string>(StringComparer.CurrentCultureIgnoreCase).ToList();
	}
}
