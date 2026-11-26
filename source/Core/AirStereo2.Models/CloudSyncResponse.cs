using System;
using System.Collections.Generic;

namespace AirStereo2.Models;

public sealed class CloudSyncResponse
{
	public int SchemaVersion { get; set; } = 1;

	public string Etag { get; set; } = string.Empty;

	public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;

	public int? TtlSeconds { get; set; }

	public List<LatencyKnowledgeEntry> Entries { get; set; } = new List<LatencyKnowledgeEntry>();
}
