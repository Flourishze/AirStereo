using AirStereo2.Services;

namespace AirStereo2.Models;

public static class DeviceClampDisclosureHelper
{
	public static DeviceClampDisclosureInfo FormatDisclosure(int? selfReportedMs, int? cloudClampMs)
	{
		if ((!selfReportedMs.HasValue || selfReportedMs.Value <= 0) && (!cloudClampMs.HasValue || cloudClampMs.Value <= 0))
		{
			return new DeviceClampDisclosureInfo(HasSelfReportedLatency: false, null, HasClampNotice: false, null);
		}
		string text = ((selfReportedMs.HasValue && selfReportedMs.Value > 0) ? string.Format(LocalizationManager.Instance.GetString("DeviceDetail_SelfReportedLatency", "该设备自报延迟为 {0} ms（未经独立验证）"), selfReportedMs.Value) : null);
		string text2 = ((cloudClampMs.HasValue && cloudClampMs.Value > 0) ? string.Format(LocalizationManager.Instance.GetString("DeviceDetail_ClampNotice", "云端知识库显示此型号可能存在底层驱动/固件延迟限制（约 {0} ms）。"), cloudClampMs.Value) : null);
		return new DeviceClampDisclosureInfo(text != null, text, text2 != null, text2);
	}
}
