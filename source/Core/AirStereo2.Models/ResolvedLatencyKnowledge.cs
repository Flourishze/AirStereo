namespace AirStereo2.Models;

public sealed record ResolvedLatencyKnowledge(int LatencyMs, KnowledgeSourcePriority Priority, string SourceDescription, bool RequiresLocalFinalVerification);
