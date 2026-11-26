using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AirStereo2.Models;

public static class EqLayout
{
	public const int BandCount = 10;

	public const double MinGainDb = -12.0;

	public const double MaxGainDb = 12.0;

	public const double MinPreampDb = -12.0;

	public const double MaxPreampDb = 12.0;

	public const int MaxUserPresets = 24;

	public const int MaxUserPresetNameLength = 40;

	public static IReadOnlyList<double> FrequenciesHz { get; } = Array.AsReadOnly(new double[10] { 31.0, 62.0, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0 });

	public static IReadOnlyList<string> FrequencyLabels { get; } = Array.AsReadOnly(new string[10] { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" });

	public static IReadOnlyList<string> ZoneLabels { get; } = Array.AsReadOnly(new string[10] { "Bass", "Bass", "Low", "Low", "Mid", "Mid", "High", "High", "Air", "Air" });

	public static string FormatGainDb(double gainDb)
	{
		if (!double.IsFinite(gainDb))
		{
			return "0";
		}
		double num = Math.Round(gainDb, 1);
		if (Math.Abs(num) < 0.05)
		{
			return "0";
		}
		string text = num.ToString("0.#", CultureInfo.InvariantCulture);
		if (!(num > 0.0))
		{
			return text;
		}
		return "+" + text;
	}

	public static double[] NormalizeGains(IReadOnlyList<double>? gains)
	{
		double[] array = new double[10];
		if (gains == null)
		{
			return array;
		}
		int num = Math.Min(gains.Count, 10);
		for (int i = 0; i < num; i++)
		{
			double num2 = gains[i];
			if (double.IsNaN(num2) || double.IsInfinity(num2))
			{
				num2 = 0.0;
			}
			array[i] = Math.Clamp(num2, -12.0, 12.0);
		}
		return array;
	}

	public static double NormalizePreampDb(double preampDb)
	{
		if (double.IsNaN(preampDb) || double.IsInfinity(preampDb))
		{
			return 0.0;
		}
		return Math.Clamp(preampDb, -12.0, 12.0);
	}

	public static bool GainsEqual(IReadOnlyList<double> a, IReadOnlyList<double> b, double epsilon = 0.15)
	{
		if (a == null || b == null || a.Count != 10 || b.Count != 10)
		{
			return false;
		}
		for (int i = 0; i < 10; i++)
		{
			if (Math.Abs(a[i] - b[i]) > epsilon)
			{
				return false;
			}
		}
		return true;
	}

	public static string SanitizePresetName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return string.Empty;
		}
		string text = new string((from ch in name.Trim()
			where !char.IsControl(ch) && ch != '/' && ch != '\\' && ch != ':' && ch != '"' && ch != '<' && ch != '>' && ch != '|'
			select ch).ToArray());
		if (text.Length > 40)
		{
			text = text.Substring(0, 40).Trim();
		}
		return text;
	}
}
