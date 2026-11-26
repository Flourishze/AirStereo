using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AirStereo2.Services;

public static class CloudQuirksVerifier
{
	public const string DefaultPublicKeyPem = "";

	public const string CompromisedPublicKeySha256 = "38e5dcec51cb4cda4ec8ce8768a76482ab7a021d5a3483017af74c3e7196fe4d";

	private static readonly HashSet<string> AllowedRootProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "schema_version", "generated_at_utc", "signature_algorithm", "signature", "devices" };

	private static readonly HashSet<string> AllowedDeviceProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"fingerprint", "model", "manufacturer", "srcvers_major_minor", "t_dev_ms", "confidence", "confidence_weight", "sample_count", "p10_ms", "p90_ms",
		"spread_ms", "clamp_ms", "notes"
	};

	private static readonly string[] ForbiddenKeywords = new string[8] { "mac", "ip", "ssid", "bssid", "device_name", "hostname", "username", "email" };

	public static CloudQuirksVerificationResult VerifyAndParse(string json, string? publicKeyPem = null)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return CloudQuirksVerificationResult.Failed("Empty or null JSON payload");
		}
		string text = publicKeyPem ?? "";
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			JsonElement rootElement = jsonDocument.RootElement;
			string text2 = AuditElementPrivacy(rootElement, isRoot: true);
			if (text2 != null)
			{
				return CloudQuirksVerificationResult.Failed(text2);
			}
			if (!rootElement.TryGetProperty("signature_algorithm", out var value) || !string.Equals(value.GetString(), "ECDSA-P256-SHA256", StringComparison.OrdinalIgnoreCase))
			{
				return CloudQuirksVerificationResult.Failed("Unsupported signature algorithm. Expected ECDSA-P256-SHA256");
			}
			if (!rootElement.TryGetProperty("signature", out var value2) || string.IsNullOrEmpty(value2.GetString()))
			{
				return CloudQuirksVerificationResult.Failed("Missing signature");
			}
			string s = value2.GetString();
			if (!rootElement.TryGetProperty("devices", out var value3))
			{
				return CloudQuirksVerificationResult.Failed("Missing 'devices' field in payload");
			}
			string s2 = CanonicalizeJsonElement(value3);
			byte[] bytes = Encoding.UTF8.GetBytes(s2);
			byte[] signature;
			try
			{
				signature = Convert.FromHexString(s);
			}
			catch
			{
				return CloudQuirksVerificationResult.Failed("Invalid hex format for signature");
			}
			using ECDsa eCDsa = ECDsa.Create();
			if (string.IsNullOrWhiteSpace(text))
			{
				return CloudQuirksVerificationResult.Failed("SEC-1: trusted public key is not configured");
			}
			eCDsa.ImportFromPem(text);
			if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(eCDsa.ExportSubjectPublicKeyInfo()), Convert.FromHexString("38e5dcec51cb4cda4ec8ce8768a76482ab7a021d5a3483017af74c3e7196fe4d")))
			{
				return CloudQuirksVerificationResult.Failed("SEC-1: compromised public key is revoked");
			}
			if (eCDsa.ExportParameters(includePrivateParameters: false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
			{
				return CloudQuirksVerificationResult.Failed("Signing requires an ECDSA P-256 key");
			}
			if (!eCDsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
			{
				return CloudQuirksVerificationResult.Failed("Signature verification failed: Invalid or unauthorized key signature");
			}
			JsonSerializerOptions options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true,
				PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
			};
			VerifiedCloudQuirksDatabase verifiedCloudQuirksDatabase = JsonSerializer.Deserialize<VerifiedCloudQuirksDatabase>(json, options);
			if (verifiedCloudQuirksDatabase == null)
			{
				return CloudQuirksVerificationResult.Failed("Deserialization produced null database");
			}
			return CloudQuirksVerificationResult.Success(verifiedCloudQuirksDatabase);
		}
		catch (Exception ex)
		{
			return CloudQuirksVerificationResult.Failed("Verification error: " + ex.Message);
		}
	}

	public static string? AuditElementPrivacy(JsonElement element, bool isRoot = false)
	{
		switch (element.ValueKind)
		{
		case JsonValueKind.Object:
			foreach (JsonProperty item in element.EnumerateObject())
			{
				string name = item.Name;
				string text2 = name.ToLowerInvariant();
				string[] forbiddenKeywords = ForbiddenKeywords;
				foreach (string value in forbiddenKeywords)
				{
					if (text2.Contains(value))
					{
						return $"PRIVACY VIOLATION: Forbidden keyword '{value}' found in property name '{name}'";
					}
				}
				if (isRoot)
				{
					if (!AllowedRootProperties.Contains(name))
					{
						return "PRIVACY VIOLATION: Unknown or unauthorized root property '" + name + "' not in allowlist";
					}
				}
				else if (item.Value.ValueKind != JsonValueKind.Object && !AllowedDeviceProperties.Contains(name))
				{
					return "PRIVACY VIOLATION: Unknown device property '" + name + "' not in allowlist";
				}
				string text3 = AuditElementPrivacy(item.Value);
				if (text3 != null)
				{
					return text3;
				}
			}
			break;
		case JsonValueKind.Array:
			foreach (JsonElement item2 in element.EnumerateArray())
			{
				string text4 = AuditElementPrivacy(item2);
				if (text4 != null)
				{
					return text4;
				}
			}
			break;
		case JsonValueKind.String:
		{
			string text = element.GetString()?.ToLowerInvariant() ?? "";
			if (text.Contains("ssid=") || text.Contains("ip=") || text.Contains("mac="))
			{
				return "PRIVACY VIOLATION: Substring leak detected in string value";
			}
			break;
		}
		}
		return null;
	}

	public static string CanonicalizeJsonElement(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return element.GetRawText();
		}
		List<JsonProperty> list = element.EnumerateObject().OrderBy<JsonProperty, string>((JsonProperty p) => p.Name, StringComparer.Ordinal).ToList();
		StringBuilder stringBuilder = new StringBuilder("{");
		for (int num = 0; num < list.Count; num++)
		{
			if (num > 0)
			{
				stringBuilder.Append(",");
			}
			stringBuilder.Append("\"").Append(list[num].Name).Append("\":");
			if (list[num].Value.ValueKind == JsonValueKind.Object)
			{
				stringBuilder.Append(CanonicalizeJsonElement(list[num].Value));
			}
			else
			{
				stringBuilder.Append(list[num].Value.GetRawText());
			}
		}
		stringBuilder.Append("}");
		return stringBuilder.ToString();
	}
}
