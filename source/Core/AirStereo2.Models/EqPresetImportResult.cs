namespace AirStereo2.Models;

public sealed class EqPresetImportResult
{
	public bool Success { get; init; }

	public string Message { get; init; } = string.Empty;

	public int ImportedUserCount { get; init; }

	public int SkippedCount { get; init; }

	public int RenamedCount { get; init; }

	public static EqPresetImportResult Fail(string message)
	{
		return new EqPresetImportResult
		{
			Success = false,
			Message = message
		};
	}

	public static EqPresetImportResult Ok(int imported, int skipped, int renamed, string message)
	{
		return new EqPresetImportResult
		{
			Success = true,
			ImportedUserCount = imported,
			SkippedCount = skipped,
			RenamedCount = renamed,
			Message = message
		};
	}
}
