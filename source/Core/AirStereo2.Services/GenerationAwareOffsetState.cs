using System.Threading;

namespace AirStereo2.Services;

public sealed class GenerationAwareOffsetState : IGenerationAwareOffsetTarget
{
	private volatile BindingToken? _boundToken;

	private volatile SessionSnapshot? _boundSnapshot;

	public BindingToken? BoundToken => _boundToken;

	public SessionSnapshot? BoundSnapshot => _boundSnapshot;

	public StreamerSessionContext? BoundSessionContext => _boundSnapshot?.Context;

	public long? AppliedEpoch => _boundSnapshot?.Epoch;

	public int? AppliedOffsetMs
	{
		get
		{
			StreamerPlayoutState playoutState = AirPlayStreamer.Instance.PlayoutState;
			BindingToken boundToken = _boundToken;
			SessionSnapshot boundSnapshot = _boundSnapshot;
			if (boundToken == null || boundSnapshot == null)
			{
				return null;
			}
			if (!playoutState.ActiveBindingToken.IsEmpty && playoutState.ActiveBindingToken.Equals(boundToken) && playoutState.Snapshot.Equals(boundSnapshot))
			{
				return playoutState.AppliedOffsetMs;
			}
			return null;
		}
	}

	public ManualResetEventSlim? PreCommitFenceForTesting
	{
		get
		{
			return AirPlayStreamer.Instance.PreCommitFenceForTesting;
		}
		set
		{
			AirPlayStreamer.Instance.PreCommitFenceForTesting = value;
		}
	}

	public void BindSession(SessionSnapshot snapshot, BindingToken? token = null)
	{
		_boundSnapshot = snapshot;
		_boundToken = token ?? AirPlayStreamer.Instance.PlayoutState.ActiveBindingToken;
	}

	public void BindSession(SessionSnapshot snapshot)
	{
		BindSession(snapshot, null);
	}

	public void BindSession(StreamerSessionContext context)
	{
		BindSession(new SessionSnapshot(context, AirPlayStreamer.Instance.SessionEpoch), null);
	}

	public void InvalidateSession()
	{
		BindingToken boundToken = _boundToken;
		_boundToken = null;
		_boundSnapshot = null;
		if (boundToken != null && !boundToken.IsEmpty)
		{
			AirPlayStreamer.Instance.CancelActiveBinding(boundToken);
		}
	}

	public bool CanApply(AcousticOffsetAppliedEvent evt)
	{
		StreamerPlayoutState playoutState = AirPlayStreamer.Instance.PlayoutState;
		BindingToken boundToken = _boundToken;
		if (boundToken != null && !playoutState.ActiveBindingToken.Equals(boundToken))
		{
			return false;
		}
		if (!playoutState.ActiveBindingToken.IsEmpty)
		{
			return playoutState.Snapshot.Equals(evt.Snapshot);
		}
		return false;
	}

	public bool TryApply(AcousticOffsetAppliedEvent evt)
	{
		return AirPlayStreamer.Instance.TryApplyAcousticOffset(evt, _boundToken);
	}
}
