namespace AirStereo2.Services;

public interface IGenerationAwareOffsetTarget
{
	StreamerSessionContext? BoundSessionContext { get; }

	int? AppliedOffsetMs { get; }

	void BindSession(StreamerSessionContext context);

	void BindSession(SessionSnapshot snapshot);

	void InvalidateSession();

	bool CanApply(AcousticOffsetAppliedEvent evt);

	bool TryApply(AcousticOffsetAppliedEvent evt);
}
