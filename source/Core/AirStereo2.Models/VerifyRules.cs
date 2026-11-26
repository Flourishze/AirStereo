using System;

namespace AirStereo2.Models;

public static class VerifyRules
{
	public static VerifyOutcome Evaluate(SnapshotDelta delta, bool statusFailed, string? userFeedback)
	{
		bool flag;
		switch (WindowRules.Judge(delta, 8.0, statusFailed))
		{
		case WindowVerdict.InsufficientEvidence:
			return new VerifyOutcome(VerifyAction.InsufficientEvidence, "遥测不完整，无法确认卡顿来源或自动调整延迟。", ShouldIncreaseLatency: false);
		case WindowVerdict.SourceGlitch:
			return new VerifyOutcome(VerifyAction.SourceGlitchRetry, "延迟测试没有问题，刚才的断续来自播放源。", ShouldIncreaseLatency: false);
		case WindowVerdict.LinkFailure:
		case WindowVerdict.RtxOverBudget:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			return new VerifyOutcome(VerifyAction.NetworkRetryRecompute, "当前链路存在丢包或抖动，系统将微调安全延迟。", ShouldIncreaseLatency: true);
		}
		if (string.Equals(userFeedback, "Choppy", StringComparison.OrdinalIgnoreCase))
		{
			if (delta.CaptureGlitches != 0)
			{
				return new VerifyOutcome(VerifyAction.InsufficientEvidence, "检测到采集异常，但没有足够证据归因卡顿来源；不会自动调整延迟。", ShouldIncreaseLatency: false);
			}
			return new VerifyOutcome(VerifyAction.UserChoppyRecompute, "用户感觉卡顿，系统将微调延迟。", ShouldIncreaseLatency: true);
		}
		if (string.Equals(userFeedback, "Offset", StringComparison.OrdinalIgnoreCase))
		{
			return new VerifyOutcome(VerifyAction.GoPtpDiagnosis, "左右声道或音箱之间不同步，建议检查时钟对齐。", ShouldIncreaseLatency: false);
		}
		if (string.Equals(userFeedback, "TooQuiet", StringComparison.OrdinalIgnoreCase))
		{
			return new VerifyOutcome(VerifyAction.BumpVolumeRetry, "音量微调后重试。", ShouldIncreaseLatency: false);
		}
		return new VerifyOutcome(VerifyAction.Pass, "校准通过，播放顺畅。", ShouldIncreaseLatency: false);
	}
}
