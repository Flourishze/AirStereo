using System.Linq;
using System.Text;

namespace AirStereo2.Services;

public static class ProblemReportText
{
	public const int MinDescriptionLength = 1;

	public const int MaxDescriptionLength = 4000;

	public const int MaxContactLength = 200;

	public static bool IsDescriptionAcceptable(string? value, int minLength = 1)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Count((char c) => !char.IsWhiteSpace(c)) >= minLength;
		}
		return false;
	}

	public static string Normalize(string? value, int maxLength)
	{
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}
		string text = value.Replace("\r\n", "\n").Replace('\r', '\n');
		StringBuilder stringBuilder = new StringBuilder(text.Length);
		string text2 = text;
		foreach (char c in text2)
		{
			if (c == '\n' || c == '\t' || !char.IsControl(c))
			{
				stringBuilder.Append(c);
			}
		}
		string text3 = stringBuilder.ToString().Trim();
		if (text3.Length <= maxLength)
		{
			return text3;
		}
		int num = maxLength;
		if (num > 0 && char.IsHighSurrogate(text3[num - 1]))
		{
			num--;
		}
		return text3.Substring(0, num).TrimEnd();
	}
}
