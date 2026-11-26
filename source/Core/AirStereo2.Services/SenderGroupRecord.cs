using System.Collections.Generic;

namespace AirStereo2.Services;

public sealed class SenderGroupRecord
{
	public string Name { get; set; } = string.Empty;

	public bool IsAppleSeeded { get; set; }

	public List<string> MemberStableIds { get; set; } = new List<string>();
}
