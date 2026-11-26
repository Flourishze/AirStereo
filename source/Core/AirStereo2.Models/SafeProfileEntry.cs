namespace AirStereo2.Models;

public sealed class SafeProfileEntry
{
	public NetworkKey NetworkKey { get; set; } = new NetworkKey();

	public int VerifiedSafeLatencyMs { get; set; }

	public string Source { get; set; } = "probe_pass_only";

	public bool ExpiresAfterNetworkChange { get; set; } = true;
}
