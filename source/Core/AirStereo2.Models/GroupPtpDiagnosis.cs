using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using AirStereo2.Services;

namespace AirStereo2.Models;

public static class GroupPtpDiagnosis
{
	public static GroupPtpDiagnosisResult Diagnose(IReadOnlyList<CalibrationDeviceIdentity>? devices, SnapshotDelta? delta)
	{
		Dictionary<string, double> sigmas = new Dictionary<string, double>(StringComparer.Ordinal);
		if (devices == null || devices.Count <= 1)
		{
			return new GroupPtpDiagnosisResult(GroupPtpStatus.NotApplicable, IsVisible: false, null, string.Empty, null, sigmas);
		}
		if (delta == null || !delta.IsValid || delta.Targets.Count == 0 || (from t in delta.Targets
			group t by t.TargetId).Any((IGrouping<ulong, TargetHealth> g) => g.Count() != 1) || devices.GroupBy<CalibrationDeviceIdentity, string>((CalibrationDeviceIdentity d) => d.StableId, StringComparer.OrdinalIgnoreCase).Any((IGrouping<string, CalibrationDeviceIdentity> g) => g.Count() != 1))
		{
			return Insufficient();
		}
		HashSet<ulong> hashSet = new HashSet<ulong>();
		List<(CalibrationDeviceIdentity, double)> list = new List<(CalibrationDeviceIdentity, double)>();
		foreach (CalibrationDeviceIdentity device in devices)
		{
			TargetHealth[] array = delta.Targets.Where((TargetHealth t) => t.Active && Matches(device, t)).ToArray();
			if (array.Length != 1 || !hashSet.Add(array[0].TargetId))
			{
				return Insufficient();
			}
			TargetHealth targetHealth = array[0];
			LatencyHistogram ptpOffset = targetHealth.PtpOffset;
			if (!targetHealth.IsValid || ptpOffset == null || ptpOffset.Metric != HistogramMetric.PtpClockOffsetProxy || targetHealth.PtpOffsetCount < 30 || ptpOffset.Total != targetHealth.PtpOffsetCount || ptpOffset.Overflow || !double.IsFinite(ptpOffset.Sigma))
			{
				return Insufficient();
			}
			list.Add((device, ptpOffset.Sigma));
		}
		foreach (var member in list)
		{
			string key = ((devices.Count((CalibrationDeviceIdentity d) => d.Name == member.Item1.Name) == 1) ? member.Item1.Name : (member.Item1.Name + " [" + member.Item1.StableId + "]"));
			if (!sigmas.TryAdd(key, member.Item2))
			{
				return Insufficient();
			}
		}
		double num = list.Min<(CalibrationDeviceIdentity, double)>(((CalibrationDeviceIdentity Device, double Sigma) m) => m.Sigma);
		if (list.Max<(CalibrationDeviceIdentity, double)>(((CalibrationDeviceIdentity Device, double Sigma) m) => m.Sigma) < 0.3)
		{
			return new GroupPtpDiagnosisResult(GroupPtpStatus.Normal, IsVisible: true, null, LocalizationManager.Instance.GetString("PtpDiagnosis_Normal", "传输侧时钟偏移波动较小；这不代表已测量实际播放同步。"), null, sigmas);
		}
		double threshold = Math.Max(0.5, 5.0 * num);
		(CalibrationDeviceIdentity, double)[] array2 = (from m in list
			where m.Item2 > threshold
			orderby m.Item2 descending
			select m).ToArray();
		if (array2.Length != 0)
		{
			string name = array2[0].Item1.Name;
			string format = LocalizationManager.Instance.GetString("PtpDiagnosis_UnstableDevice", "「{0}」的传输侧时钟偏移波动明显高于其他设备。可检查 Wi-Fi 或 Mesh 节点连接；这不代表已测量实际播放同步。");
			return new GroupPtpDiagnosisResult(GroupPtpStatus.UnstableDevice, IsVisible: true, name, string.Format(format, name), LocalizationManager.Instance.GetString("PtpDiagnosis_CheckNetwork", "建议检查设备的无线信号和路由节点连接，再重新测试。"), sigmas);
		}
		return new GroupPtpDiagnosisResult(GroupPtpStatus.ElevatedGroup, IsVisible: true, null, LocalizationManager.Instance.GetString("PtpDiagnosis_ElevatedGroup", "组内传输侧时钟偏移波动偏高，但没有可明确归因的单个异常设备。这不代表已测量实际播放同步。"), LocalizationManager.Instance.GetString("PtpDiagnosis_CheckNetwork", "建议检查设备的无线信号和路由节点连接，再重新测试。"), sigmas);
		GroupPtpDiagnosisResult Insufficient()
		{
			return new GroupPtpDiagnosisResult(GroupPtpStatus.InsufficientData, IsVisible: true, null, LocalizationManager.Instance.GetString("PtpDiagnosis_InsufficientData", "对时信息有限或设备映射不明确，暂时无法判断传输侧时钟偏移稳定性；这不代表实际播放同步测量。"), null, sigmas);
		}
	}

	internal static bool Matches(CalibrationDeviceIdentity device, TargetHealth target)
	{
		if (!string.IsNullOrWhiteSpace(device.TargetAddress))
		{
			return SameAddress(device.TargetAddress, target.Ip);
		}
		if (ulong.TryParse(device.StableId, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
		{
			return result == target.TargetId;
		}
		if (IPAddress.TryParse(device.StableId, out IPAddress _))
		{
			return SameAddress(device.StableId, target.Ip);
		}
		return false;
	}

	private static bool SameAddress(string left, string right)
	{
		if (IPAddress.TryParse(left, out IPAddress address) && IPAddress.TryParse(right, out IPAddress address2))
		{
			return address.Equals(address2);
		}
		return false;
	}
}
