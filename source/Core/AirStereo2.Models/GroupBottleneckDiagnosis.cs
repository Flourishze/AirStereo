using System;
using System.Collections.Generic;
using System.Linq;

namespace AirStereo2.Models;

public static class GroupBottleneckDiagnosis
{
	public static GroupBottleneckResult Diagnose(IReadOnlyList<CalibrationDeviceIdentity> devices, SnapshotDelta delta, IReadOnlyDictionary<string, NetworkStats>? memberStats = null)
	{
		try
		{
			if (devices == null || devices.Count <= 1)
			{
				return new GroupBottleneckResult(IsVisible: false, string.Empty, null, Array.Empty<MemberLatencyEstimate>(), IsTie: false);
			}
			List<MemberLatencyEstimate> list = new List<MemberLatencyEstimate>();
			foreach (CalibrationDeviceIdentity d in devices)
			{
				double num = (LatencyCalibrationPolicy.LooksLikeHomePod(d.Model, d.Name) ? 10.0 : 5.0);
				double num2 = 5.0;
				double num3 = 4.0;
				double num4 = 1.0;
				if (memberStats != null && memberStats.TryGetValue(d.StableId, out NetworkStats value))
				{
					num2 = value.DPath;
					num3 = value.JNet;
					num4 = value.Sigma;
				}
				else if (delta.Targets.Count > 0)
				{
					TargetHealth targetHealth = delta.Targets.FirstOrDefault((TargetHealth t) => t.Ip == d.StableId);
					if (targetHealth != null && targetHealth.RtxRequested != 0)
					{
						num3 += Math.Min(20.0, (double)targetHealth.RtxRequested * 2.0);
					}
				}
				double lrecMember = num + num2 + num3 + 3.5 * num4;
				list.Add(new MemberLatencyEstimate(d.Name, d.Model, d.StableId, num, num2, num3, num4, lrecMember));
			}
			List<MemberLatencyEstimate> list2 = list.OrderByDescending((MemberLatencyEstimate e) => e.LrecMember).ToList();
			MemberLatencyEstimate memberLatencyEstimate = list2[0];
			MemberLatencyEstimate memberLatencyEstimate2 = list2[1];
			double num5 = memberLatencyEstimate.LrecMember - memberLatencyEstimate2.LrecMember;
			if (num5 < 10.0)
			{
				return new GroupBottleneckResult(IsVisible: true, "各音箱网络与处理条件接近，共同决定当前延迟。", null, list2, IsTie: true);
			}
			string suggestionText = (LatencyCalibrationPolicy.LooksLikeAppleTv(memberLatencyEstimate.Model, memberLatencyEstimate.DeviceName) ? ("建议将「" + memberLatencyEstimate.DeviceName + "」连接至有线网络，或移近 Wi-Fi 路由器。") : ("建议将「" + memberLatencyEstimate.DeviceName + "」移近 Wi-Fi 路由器，或避免微波炉、隔墙等信号遮挡。"));
			string summaryText = $"「{memberLatencyEstimate.DeviceName}」决定了当前的延迟（高出约 {Math.Round(num5)} ms）。";
			return new GroupBottleneckResult(IsVisible: true, summaryText, suggestionText, list2, IsTie: false);
		}
		catch
		{
			return new GroupBottleneckResult(IsVisible: false, string.Empty, null, Array.Empty<MemberLatencyEstimate>(), IsTie: false);
		}
	}
}
