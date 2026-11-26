using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AirStereo2.Models;

public sealed class LatencyHistogram
{
	public const int Bins = 64;

	public const int BinCount = 64;

	public const double MinMs = 0.05;

	public const double MaxMs = 200.0;

	private static readonly double[] Lower;

	private static readonly double[] Upper;

	private static readonly double[] Mid;

	public ulong[] Counts { get; }

	public HistogramMetric Metric { get; }

	[JsonIgnore]
	public double RangeMinMs
	{
		get
		{
			if (Metric != HistogramMetric.PtpClockOffsetProxy)
			{
				return 0.05;
			}
			return 0.001;
		}
	}

	[JsonIgnore]
	public double RangeMaxMs
	{
		get
		{
			if (Metric != HistogramMetric.PtpClockOffsetProxy)
			{
				return 200.0;
			}
			return 50.0;
		}
	}

	[JsonIgnore]
	public ulong Total => Counts.Aggregate(0uL, (ulong a, ulong b) => a + b);

	[JsonIgnore]
	public bool Overflow => Counts[63] != 0;

	[JsonIgnore]
	public double P50 => Quantile(0.5);

	[JsonIgnore]
	public double P95 => Quantile(0.95);

	[JsonIgnore]
	public double P99 => Quantile(0.99);

	[JsonIgnore]
	public double P99_9 => Quantile(0.999);

	[JsonIgnore]
	public double Max
	{
		get
		{
			for (int num = 63; num >= 0; num--)
			{
				if (Counts[num] != 0)
				{
					return UpperBound(num);
				}
			}
			return 0.0;
		}
	}

	[JsonIgnore]
	public double Sigma
	{
		get
		{
			ulong total = Total;
			if (total < 2)
			{
				if (Metric != HistogramMetric.PtpClockOffsetProxy)
				{
					return 0.0;
				}
				return double.NaN;
			}
			double num = 0.0;
			for (int i = 0; i < 64; i++)
			{
				num += Midpoint(i) * (double)Counts[i];
			}
			num /= (double)total;
			double num2 = 0.0;
			for (int j = 0; j < 64; j++)
			{
				num2 += (double)Counts[j] * (Midpoint(j) - num) * (Midpoint(j) - num);
			}
			return Math.Sqrt(num2 / (double)(total - 1));
		}
	}

	static LatencyHistogram()
	{
		Lower = new double[64];
		Upper = new double[64];
		Mid = new double[64];
		double num = Math.Log(4000.0) / 63.0;
		for (int i = 0; i < 64; i++)
		{
			Lower[i] = ((i == 0) ? 0.0 : (0.05 * Math.Exp(num * (double)i)));
			Upper[i] = ((i == 63) ? 200.0 : (0.05 * Math.Exp(num * (double)(i + 1))));
			Mid[i] = ((i == 0) ? 0.025 : Math.Sqrt(Math.Max(Lower[i], 1E-06) * Upper[i]));
		}
		Mid[63] = 200.0;
	}

	public static double GetLower(int i)
	{
		if (i < 0 || i >= 64)
		{
			return 0.0;
		}
		return Lower[i];
	}

	public static double GetUpper(int i)
	{
		if (i < 0 || i >= 64)
		{
			if (i < 64)
			{
				return 0.0;
			}
			return 200.0;
		}
		return Upper[i];
	}

	public static double GetMid(int i)
	{
		if (i < 0 || i >= 64)
		{
			return 0.0;
		}
		return Mid[i];
	}

	public double LowerBound(int i)
	{
		if (i > 0)
		{
			if (i < 64)
			{
				return RangeMinMs * Math.Exp(Math.Log(RangeMaxMs / RangeMinMs) * (double)i / 63.0);
			}
			return RangeMaxMs;
		}
		return 0.0;
	}

	public double UpperBound(int i)
	{
		if (i >= 0)
		{
			if (i < 63)
			{
				return RangeMinMs * Math.Exp(Math.Log(RangeMaxMs / RangeMinMs) * (double)(i + 1) / 63.0);
			}
			return RangeMaxMs;
		}
		return 0.0;
	}

	public double Midpoint(int i)
	{
		switch (i)
		{
		case 1:
		case 2:
		case 3:
		case 4:
		case 5:
		case 6:
		case 7:
		case 8:
		case 9:
		case 10:
		case 11:
		case 12:
		case 13:
		case 14:
		case 15:
		case 16:
		case 17:
		case 18:
		case 19:
		case 20:
		case 21:
		case 22:
		case 23:
		case 24:
		case 25:
		case 26:
		case 27:
		case 28:
		case 29:
		case 30:
		case 31:
		case 32:
		case 33:
		case 34:
		case 35:
		case 36:
		case 37:
		case 38:
		case 39:
		case 40:
		case 41:
		case 42:
		case 43:
		case 44:
		case 45:
		case 46:
		case 47:
		case 48:
		case 49:
		case 50:
		case 51:
		case 52:
		case 53:
		case 54:
		case 55:
		case 56:
		case 57:
		case 58:
		case 59:
		case 60:
		case 61:
		case 62:
			return Math.Sqrt(LowerBound(i) * UpperBound(i));
		case 63:
			return RangeMaxMs;
		case 0:
			return RangeMinMs / 2.0;
		default:
			return 0.0;
		}
	}

	public LatencyHistogram(ulong[] counts, HistogramMetric metric = HistogramMetric.TransportDelay)
	{
		Metric = metric;
		if (counts.Length != 64)
		{
			ulong[] destinationArray = new ulong[64];
			Array.Copy(counts, destinationArray, Math.Min(counts.Length, 64));
			Counts = destinationArray;
		}
		else
		{
			Counts = (ulong[])counts.Clone();
		}
	}

	public LatencyHistogram(IReadOnlyList<long>? counts, HistogramMetric metric = HistogramMetric.TransportDelay)
	{
		Metric = metric;
		Counts = new ulong[64];
		if (counts != null)
		{
			for (int i = 0; i < 64 && i < counts.Count; i++)
			{
				Counts[i] = (ulong)((counts[i] > 0) ? counts[i] : 0);
			}
		}
	}

	public static LatencyHistogram Delta(ulong[] after, ulong[] before)
	{
		ulong[] array = new ulong[64];
		for (int i = 0; i < 64; i++)
		{
			ulong num = ((i < after.Length) ? after[i] : 0);
			ulong num2 = ((i < before.Length) ? before[i] : 0);
			array[i] = ((num >= num2) ? (num - num2) : 0);
		}
		return new LatencyHistogram(array);
	}

	public static LatencyHistogram Delta(IReadOnlyList<long>? after, IReadOnlyList<long>? before)
	{
		ulong[] array = new ulong[64];
		for (int i = 0; i < 64; i++)
		{
			long num = ((after != null && i < after.Count) ? after[i] : 0);
			long num2 = ((before != null && i < before.Count) ? before[i] : 0);
			array[i] = (ulong)((num >= num2) ? (num - num2) : 0);
		}
		return new LatencyHistogram(array);
	}

	public double Quantile(double q)
	{
		ulong total = Total;
		if (total == 0L)
		{
			return double.NaN;
		}
		ulong num = (ulong)Math.Ceiling(q * (double)total);
		ulong num2 = 0uL;
		for (int i = 0; i < 64; i++)
		{
			num2 += Counts[i];
			if (num2 >= num)
			{
				return Midpoint(i);
			}
		}
		return Midpoint(63);
	}

	public static int BinForMs(double x, HistogramMetric metric = HistogramMetric.TransportDelay)
	{
		double num = ((metric == HistogramMetric.PtpClockOffsetProxy) ? 0.001 : 0.05);
		double num2 = ((metric == HistogramMetric.PtpClockOffsetProxy) ? 50.0 : 200.0);
		if (!(x > num))
		{
			return 0;
		}
		if (x >= num2)
		{
			return 63;
		}
		return Math.Clamp((int)Math.Floor(63.0 * Math.Log(x / num) / Math.Log(num2 / num)), 0, 62);
	}
}
