using System;

namespace AirStereo2.Models;

public sealed class GradeBProbePlanner
{
	public record ProbeDecision(bool ContinueProbe, int NextLatencyMs, bool EnterFinalVerification);

	public const int InitialSamplingBaselineMs = 160;

	public const int Probe1CandidateMs = 100;

	public const int Floor5Ms = 60;

	public const int MaxProbeRounds = 3;

	public const int FinalVerificationSeconds = 8;

	private int _roundIndex;

	private int _currentLatency = 100;

	private int _highestSafeLatency = 160;

	private int? _lowestVerifiedSafeLatency;

	private int? _lowestFailedLatency;

	private bool _completed;

	public int CurrentLatency => _currentLatency;

	public int RoundIndex => _roundIndex;

	public bool IsCompleted => _completed;

	public int? LowestVerifiedSafeLatency => _lowestVerifiedSafeLatency;

	public int? LowestFailedLatency => _lowestFailedLatency;

	public ProbeDecision RecordRoundResult(bool passed)
	{
		_roundIndex++;
		if (passed)
		{
			_lowestVerifiedSafeLatency = _currentLatency;
			_highestSafeLatency = _currentLatency;
			if (_currentLatency <= 60 || _roundIndex >= 3)
			{
				_completed = true;
				return new ProbeDecision(ContinueProbe: false, _lowestVerifiedSafeLatency.Value, EnterFinalVerification: true);
			}
			_currentLatency = _currentLatency switch
			{
				100 => 80, 
				80 => 60, 
				_ => Math.Max(60, _currentLatency - 20), 
			};
			return new ProbeDecision(ContinueProbe: true, _currentLatency, EnterFinalVerification: false);
		}
		_lowestFailedLatency = _currentLatency;
		if (_roundIndex >= 3)
		{
			_completed = true;
			int nextLatencyMs = _lowestVerifiedSafeLatency ?? _highestSafeLatency;
			return new ProbeDecision(ContinueProbe: false, nextLatencyMs, EnterFinalVerification: true);
		}
		int num = (int)(Math.Round((double)(_highestSafeLatency + _currentLatency) / 10.0) * 5.0);
		if (num <= _currentLatency)
		{
			num = _highestSafeLatency;
		}
		_currentLatency = num;
		return new ProbeDecision(ContinueProbe: true, _currentLatency, EnterFinalVerification: false);
	}
}
