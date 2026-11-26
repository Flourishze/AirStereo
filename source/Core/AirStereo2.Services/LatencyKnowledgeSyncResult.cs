namespace AirStereo2.Services;

public sealed class LatencyKnowledgeSyncResult
{
	public bool Success { get; set; }

	public bool NotModified { get; set; }

	public string? Etag { get; set; }

	public int EntryCount { get; set; }

	public string? ErrorMessage { get; set; }
}
