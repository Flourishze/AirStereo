using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace AirStereo2.Services;

public sealed record BackendBuildInfo(string Version, string Git, string Mode)
{
	public bool IsDirty => Git.EndsWith("-dirty", StringComparison.Ordinal);

	private const string Prefix = "[Engine] BUILD ";

	public static bool TryParse(string line, [NotNullWhen(true)] out BackendBuildInfo? info)
	{
		info = null;
		if (string.IsNullOrEmpty(line) || !line.StartsWith("[Engine] BUILD ", StringComparison.Ordinal))
		{
			return false;
		}
		string[] array = line.Substring("[Engine] BUILD ".Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (array.Length < 3)
		{
			return false;
		}
		info = new BackendBuildInfo(array[0], array[1], array[2]);
		return true;
	}

	public bool MatchesAppVersion(string? appVersion)
	{
		return string.Equals(Version, appVersion, StringComparison.OrdinalIgnoreCase);
	}

	public override string ToString()
	{
		return $"{Version} (git {Git}, {Mode})";
	}

	[CompilerGenerated]
	private BackendBuildInfo(BackendBuildInfo original)
	{
		Version = original.Version;
		Git = original.Git;
		Mode = original.Mode;
	}
}
