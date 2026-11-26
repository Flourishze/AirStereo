using System.Collections.Generic;
using System.Windows.Media;

namespace AirStereo2.Services;

public class ProcessItem
{
	public int ProcessId { get; set; }

	public int SessionProcessId { get; set; }

	public List<int> CaptureProcessIds { get; set; } = new List<int>();

	public string ProcessName { get; set; } = "";

	public string ApplicationName { get; set; } = "";

	public string ExecutablePath { get; set; } = "";

	public ImageSource? Icon { get; set; }

	public int CaptureProcessId
	{
		get
		{
			if (CaptureProcessIds.Count <= 0)
			{
				if (SessionProcessId <= 0)
				{
					return ProcessId;
				}
				return SessionProcessId;
			}
			return CaptureProcessIds[0];
		}
	}

	public string SecondaryText => ProcessDisplayName.FormatSecondaryText(ProcessName, ProcessId, SessionProcessId);

	public string DisplayName => (string.IsNullOrWhiteSpace(ApplicationName) ? ProcessName : ApplicationName) + " — " + SecondaryText;
}
