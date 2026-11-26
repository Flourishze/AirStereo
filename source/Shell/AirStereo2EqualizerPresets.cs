namespace AirStereo2;

// UI preset data from AirStereo2. Applied through the original engine's existing EQ setters.
internal static class AirStereo2EqualizerPresets
{
    internal static readonly string[] Names = { "流行", "摇滚", "古典", "低音", "人声", "爵士", "电子", "平坦", "自定义" };
    internal static readonly double[][] Curves = {
        new double[] { -1, 1, 3, 3, 1, -1, -1, 1, 2, 2 },
        new double[] { 4, 3, 1, -1, -2, 1, 3, 4, 3, 2 },
        new double[] { 3, 2, 1, 0, -1, -1, 0, 2, 3, 4 },
        new double[] { 5, 4, 3, 2, 1, 0, 0, 0, 0, 0 },
        new double[] { -2, -1, 0, 2, 4, 4, 3, 1, 0, -1 },
        new double[] { 3, 2, 1, 2, -1, -1, 0, 2, 3, 4 },
        new double[] { 4, 3, 1, -1, -2, 1, 0, 2, 3, 4 },
        new double[10]
    };
    internal const int Custom = 8;
    internal static bool Matches(int index, IReadOnlyList<double> gains) =>
        index >= 0 && index < Curves.Length && gains.Count == 10 &&
        Curves[index].Select((value, i) => Math.Abs(value - gains[i]) < 0.000001).All(match => match);
    internal static int Match(IReadOnlyList<double> gains)
    {
        for (int i = 0; i < Curves.Length; i++) if (Matches(i, gains)) return i;
        return Custom;
    }
}
