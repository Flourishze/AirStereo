namespace AirStereo2.Services;

public static class UiLayout
{
	public const double HomeMinContentWidth = 480.0;

	public const double SettingsMinContentWidth = 560.0;

	public const double MinContentWidth = 480.0;

	public static double CoerceContentWidth(double availableWidth, double minContentWidth)
	{
		if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0.0)
		{
			return minContentWidth;
		}
		if (!(availableWidth < minContentWidth))
		{
			return availableWidth;
		}
		return minContentWidth;
	}

	public static bool ShouldClampToMinWidth(double availableWidth, double minContentWidth)
	{
		if (!double.IsNaN(availableWidth) && !double.IsInfinity(availableWidth) && availableWidth > 0.0)
		{
			return availableWidth < minContentWidth;
		}
		return false;
	}
}
