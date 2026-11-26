namespace AirStereo2.Models;

public sealed record DeviceClampDisclosureInfo(bool HasSelfReportedLatency, string? SelfReportedLatencyText, bool HasClampNotice, string? ClampNoticeText)
{
	public bool IsVisible
	{
		get
		{
			if (!HasSelfReportedLatency)
			{
				return HasClampNotice;
			}
			return true;
		}
	}
}
