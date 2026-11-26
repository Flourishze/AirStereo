namespace AirStereo2.Services;

public sealed record AcousticOffsetAppliedEvent(SessionSnapshot Snapshot, int OffsetMs, long RequestId = 0L, BindingToken? Token = null)
{
	public StreamerSessionContext SessionContext => Snapshot.Context;

	public long SessionEpoch => Snapshot.Epoch;

	public BindingToken BindingToken => Token ?? AirStereo2.Services.BindingToken.Empty;
}
