using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AirStereo2.Services;

public static class BackendEventText
{
	private enum Field
	{
		Text,
		Int,
		Number,
		RttMs,
		Rest,
		RestOrNull,
		Json
	}

	private static readonly JsonElement EmptyObject = ParseElement("{}");

	private static readonly Dictionary<string, (string Name, Field Type)[]> Schemas = new Dictionary<string, (string, Field)[]>(StringComparer.Ordinal)
	{
		["Status CONNECTING"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Status CONNECTED"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Status CALIBRATING"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Status CALIBRATION_SLOW"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Status DISCONNECTED"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Status RECONNECTING"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Status PREEMPTED"] = new(string, Field)[3]
		{
			("ip", Field.Text),
			("takeover", Field.Int),
			("limit", Field.Int)
		},
		["Status FAILED"] = new(string, Field)[2]
		{
			("ip", Field.Text),
			("reason", Field.Rest)
		},
		["Status BACKEND_EXITED"] = new(string, Field)[1] { ("code", Field.Int) },
		["Status BACKEND_RECOVERED"] = new(string, Field)[1] { ("attempt", Field.Int) },
		["EndpointStatus CONNECTING"] = EndpointFields,
		["EndpointStatus CALIBRATING"] = EndpointFields,
		["EndpointStatus CONNECTED"] = EndpointFields,
		["EndpointStatus DISCONNECTED"] = EndpointFields,
		["EndpointStatus FAILED"] = EndpointFields,
		["EndpointStatus RECONNECTING"] = EndpointFields,
		["Auth PIN_REQUIRED"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Auth PIN_FAILED"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Auth PASSWORD_REQUIRED"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Auth PASSWORD_FAILED"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Volume CURRENT"] = new(string, Field)[2]
		{
			("ip", Field.Text),
			("percent", Field.Int)
		},
		["Latency RTT"] = new(string, Field)[3]
		{
			("ip", Field.Text),
			("ms", Field.RttMs),
			("proc_ms", Field.Int)
		},
		["Latency PLAYBACK"] = new(string, Field)[3]
		{
			("ip", Field.Text),
			("ms", Field.Int),
			("rtt_ms", Field.Int)
		},
		["Latency PLAYOUT"] = new(string, Field)[3]
		{
			("ip", Field.Text),
			("ms", Field.Int),
			("rtt_ms", Field.Int)
		},
		["Latency OVERRIDE_APPLIED"] = new(string, Field)[2]
		{
			("mode", Field.Text),
			("ms", Field.Int)
		},
		["Latency RTX"] = new(string, Field)[4]
		{
			("ip", Field.Text),
			("requests", Field.Int),
			("requested", Field.Int),
			("resent", Field.Int)
		},
		["Latency RTX_REQ"] = new(string, Field)[3]
		{
			("ip", Field.Text),
			("first_seq", Field.Int),
			("count", Field.Int)
		},
		["Latency DRIFT"] = new(string, Field)[3]
		{
			("ip", Field.Text),
			("drift_ppb", Field.Int),
			("samples", Field.Int)
		},
		["Latency RESYNC"] = new(string, Field)[2]
		{
			("ip", Field.Text),
			("reason", Field.Rest)
		},
		["Codec REQUESTED"] = new(string, Field)[1] { ("codec", Field.Text) },
		["Codec ACTIVE"] = new(string, Field)[1] { ("codec", Field.Text) },
		["Codec ENDPOINT_ACTIVE"] = new(string, Field)[2]
		{
			("ip", Field.Text),
			("codec", Field.Text)
		},
		["Codec EXACT_ENDPOINT_ACTIVE"] = new(string, Field)[3]
		{
			("endpoint_id", Field.Text),
			("ip", Field.Text),
			("codec", Field.Text)
		},
		["Codec CONFIRM_REQUIRED"] = new(string, Field)[4]
		{
			("ip", Field.Text),
			("requested", Field.Text),
			("suggested", Field.Text),
			("reason", Field.Text)
		},
		["Codec AIRPORT_PCM_WARNING"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Capture FAILED"] = new(string, Field)[2]
		{
			("source", Field.Text),
			("reason", Field.Rest)
		},
		["Capture PROCESS_EXITED"] = new(string, Field)[1] { ("pid", Field.Int) },
		["Calibration LATENCY_APPLIED"] = new(string, Field)[2]
		{
			("ms", Field.Int),
			("mode", Field.Text)
		},
		["Calibration LATENCY_BELOW_FLOOR"] = new(string, Field)[2]
		{
			("requested_ms", Field.Int),
			("floor_ms", Field.Int)
		},
		["Calibration LATENCY_UNAVAILABLE"] = new(string, Field)[1] { ("reason", Field.Rest) },
		["Calibration SESSION_FALLBACK"] = new(string, Field)[2]
		{
			("ip", Field.Text),
			("max_ms", Field.Int)
		},
		["Calibration TONE"] = new(string, Field)[1] { ("state", Field.Text) },
		["MediaCommand PLAY"] = new(string, Field)[1] { ("ip", Field.Text) },
		["MediaCommand PAUSE"] = new(string, Field)[1] { ("ip", Field.Text) },
		["MediaCommand TOGGLE"] = new(string, Field)[1] { ("ip", Field.Text) },
		["MediaCommand NEXT"] = new(string, Field)[1] { ("ip", Field.Text) },
		["MediaCommand PREV"] = new(string, Field)[1] { ("ip", Field.Text) },
		["MediaCommand STOP"] = new(string, Field)[1] { ("ip", Field.Text) },
		["Engine PROTOCOL"] = new(string, Field)[1] { ("version", Field.Int) },
		["Info LATENCY"] = new(string, Field)[2]
		{
			("ip", Field.Text),
			("latency", Field.Json)
		}
	};

	private static (string, Field)[] EndpointFields => new(string, Field)[3]
	{
		("endpoint_id", Field.Text),
		("ip", Field.Text),
		("reason", Field.RestOrNull)
	};

	public static bool TryParse(string? line, long? timestampNs, [NotNullWhen(true)] out BackendEvent? evt)
	{
		evt = null;
		if (string.IsNullOrWhiteSpace(line) || line[0] != '[')
		{
			return false;
		}
		int num = line.IndexOf(']');
		if (num <= 1)
		{
			return false;
		}
		string text = line.Substring(1, num - 1);
		string text2 = line.Substring(num + 1).TrimStart();
		if (text2.Length == 0 || text.Contains(' '))
		{
			return false;
		}
		int num2 = text2.IndexOf(' ');
		string text3 = ((num2 < 0) ? text2 : text2.Substring(0, num2));
		string args = ((num2 < 0) ? string.Empty : text2.Substring(num2 + 1));
		(string, Field)[] value;
		JsonElement data = (Schemas.TryGetValue(text + " " + text3, out value) ? BuildData(value, args) : EmptyObject);
		evt = new BackendEvent(3, timestampNs, text, text3, data, line);
		return true;
	}

	private static JsonElement BuildData((string Name, Field Type)[] schema, string args)
	{
		using MemoryStream memoryStream = new MemoryStream();
		using (Utf8JsonWriter utf8JsonWriter = new Utf8JsonWriter(memoryStream))
		{
			utf8JsonWriter.WriteStartObject();
			string remaining = args;
			for (int i = 0; i < schema.Length; i++)
			{
				var (propertyName, field) = schema[i];
				bool flag;
				switch (field)
				{
				case Field.Json:
					try
					{
						using JsonDocument jsonDocument = JsonDocument.Parse(remaining.Trim());
						utf8JsonWriter.WritePropertyName(propertyName);
						jsonDocument.RootElement.WriteTo(utf8JsonWriter);
					}
					catch (JsonException)
					{
						utf8JsonWriter.WriteNull(propertyName);
					}
					remaining = string.Empty;
					continue;
				case Field.Rest:
				case Field.RestOrNull:
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				if (flag)
				{
					string text = remaining.Trim();
					if (field == Field.RestOrNull && text.Length == 0)
					{
						utf8JsonWriter.WriteNull(propertyName);
					}
					else
					{
						utf8JsonWriter.WriteString(propertyName, text);
					}
					remaining = string.Empty;
					continue;
				}
				string text2 = NextToken(ref remaining);
				switch (field)
				{
				case Field.Text:
					if (text2.Length > 0)
					{
						utf8JsonWriter.WriteString(propertyName, text2);
						continue;
					}
					break;
				case Field.Int:
				{
					if (long.TryParse(text2, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2))
					{
						utf8JsonWriter.WriteNumber(propertyName, result2);
						continue;
					}
					break;
				}
				case Field.Number:
				{
					if (double.TryParse(text2, NumberStyles.Float, CultureInfo.InvariantCulture, out var result3))
					{
						utf8JsonWriter.WriteNumber(propertyName, result3);
						continue;
					}
					break;
				}
				case Field.RttMs:
				{
					if (double.TryParse(text2, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result >= 0.0)
					{
						utf8JsonWriter.WriteNumber(propertyName, result);
						continue;
					}
					break;
				}
				}
				utf8JsonWriter.WriteNull(propertyName);
			}
			utf8JsonWriter.WriteEndObject();
		}
		using JsonDocument jsonDocument2 = JsonDocument.Parse(memoryStream.ToArray());
		return jsonDocument2.RootElement.Clone();
	}

	private static string NextToken(ref string remaining)
	{
		remaining = remaining.TrimStart();
		int num = remaining.IndexOf(' ');
		string result = ((num < 0) ? remaining : remaining.Substring(0, num));
		remaining = ((num < 0) ? string.Empty : remaining.Substring(num + 1));
		return result;
	}

	private static JsonElement ParseElement(string json)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(json);
		return jsonDocument.RootElement.Clone();
	}
}
