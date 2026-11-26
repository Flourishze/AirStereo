using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AirStereo2.Services;

public sealed record BackendEvent(int SchemaVersion, long? TimestampNs, string Tag, string Kind, JsonElement Data, string Line)
{
	public const int CurrentSchemaVersion = 3;

	public const int MinimumProtocolVersion = 3;

	public const string OptInCommand = "IPC_FORMAT JSON 3";

	private static readonly char[] LineBreaks = new char[2] { '\r', '\n' };

	private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

	public string ToLegacyRawLine()
	{
		long? timestampNs = TimestampNs;
		if (timestampNs.HasValue)
		{
			long valueOrDefault = timestampNs.GetValueOrDefault();
			return $"ts={valueOrDefault} {Line}";
		}
		return Line;
	}

	public bool Is(string tag, string kind)
	{
		if (string.Equals(Tag, tag, StringComparison.Ordinal))
		{
			return string.Equals(Kind, kind, StringComparison.Ordinal);
		}
		return false;
	}

	public string? GetString(string name)
	{
		if (!TryGet(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}

	public long? GetInt64(string name)
	{
		if (!TryGet(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var value2))
		{
			return null;
		}
		return value2;
	}

	public double? GetDouble(string name)
	{
		if (!TryGet(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var value2))
		{
			return null;
		}
		return value2;
	}

	public bool? GetBoolean(string name)
	{
		JsonElement value;
		bool flag = TryGet(name, out value);
		if (flag)
		{
			JsonValueKind valueKind = value.ValueKind;
			bool flag2 = valueKind - 5 <= JsonValueKind.Object;
			flag = flag2;
		}
		if (!flag)
		{
			return null;
		}
		return value.GetBoolean();
	}

	public bool TryGet(string name, out JsonElement value)
	{
		if (Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(name, out value))
		{
			return true;
		}
		value = default(JsonElement);
		return false;
	}

	public static bool TryParse(string? rawLine, [NotNullWhen(true)] out BackendEvent? evt)
	{
		evt = null;
		if (string.IsNullOrEmpty(rawLine) || rawLine[0] != '{')
		{
			return false;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(rawLine);
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.ValueKind != JsonValueKind.Object)
			{
				return false;
			}
			if (!rootElement.TryGetProperty("v", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var value2) || value2 != 3)
			{
				return false;
			}
			string text = ReadString(rootElement, "tag");
			string text2 = ReadString(rootElement, "kind");
			string text3 = ReadString(rootElement, "line");
			if (string.IsNullOrEmpty(text) || text2 == null || string.IsNullOrEmpty(text3) || text3.IndexOfAny(LineBreaks) >= 0)
			{
				return false;
			}
			long? timestampNs = null;
			if (rootElement.TryGetProperty("ts", out var value3) && value3.ValueKind == JsonValueKind.Number)
			{
				if (!value3.TryGetInt64(out var value4) || value4 < 0)
				{
					return false;
				}
				timestampNs = value4;
			}
			JsonElement value5;
			JsonElement data = ((rootElement.TryGetProperty("data", out value5) && value5.ValueKind == JsonValueKind.Object) ? value5.Clone() : EmptyObject);
			evt = new BackendEvent(value2, timestampNs, text, text2, data, text3);
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static string? ReadString(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}

	[CompilerGenerated]
	private BackendEvent(BackendEvent original)
	{
		SchemaVersion = original.SchemaVersion;
		TimestampNs = original.TimestampNs;
		Tag = original.Tag;
		Kind = original.Kind;
		Data = original.Data;
		Line = original.Line;
	}
}
