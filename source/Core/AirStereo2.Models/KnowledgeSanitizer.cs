using System;

namespace AirStereo2.Models;

public static class KnowledgeSanitizer
{
	public static LatencyKnowledgeEntry PrepareForUpload(string fingerprint, string model, string manufacturer, string srcvers, int verifiedLatency, NetworkKey? networkKey, string confidence = "observed")
	{
		return new LatencyKnowledgeEntry
		{
			SchemaVersion = 1,
			DeviceFingerprint = fingerprint,
			NetworkScope = ((networkKey != null) ? "network_bound" : "global"),
			Model = model,
			Manufacturer = manufacturer,
			SrcversMajorMinor = DeviceFingerprintHelper.NormalizeSrcVers(srcvers),
			VerifiedSafeLatencyMs = verifiedLatency,
			AcousticOffsetPriorMs = null,
			Confidence = confidence,
			Source = "local_probe",
			SampleCount = 1,
			UpdatedAt = DateTime.UtcNow,
			BoundNetworkKey = ((networkKey != null) ? new NetworkKey
			{
				Transport = networkKey.Transport,
				GatewayMacPrefix = null,
				Band = networkKey.Band,
				Ssid = null,
				BssidPrefix = null,
				ActiveAdapterId = null
			} : null)
		};
	}
}
