using System;
using System.Collections.Generic;
using System.Linq;

namespace AirStereo2.Models;

public sealed class EqBuiltInPreset
{
	public string Id { get; }

	public string DisplayName { get; }

	public IReadOnlyList<double> GainsDb { get; }

	public static IReadOnlyList<EqBuiltInPreset> All { get; } = new EqBuiltInPreset[9]
	{
		new EqBuiltInPreset("flat", "Flat", new double[10]),
		new EqBuiltInPreset("bass_boost", "Bass Boost", new double[10] { 6.0, 5.0, 3.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }),
		new EqBuiltInPreset("treble_boost", "Treble Boost", new double[10] { 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 2.0, 4.0, 5.0, 4.0 }),
		new EqBuiltInPreset("v_shape", "V-Shape", new double[10] { 5.0, 4.0, 2.0, 0.0, -2.0, -1.0, 1.0, 3.0, 4.0, 4.0 }),
		new EqBuiltInPreset("vocal", "Vocal", new double[10] { -3.0, -2.0, -1.0, 1.0, 3.0, 4.0, 2.0, 0.0, -1.0, -2.0 }),
		new EqBuiltInPreset("music", "Music", new double[10] { 3.0, 2.0, 1.0, 0.0, 0.0, 0.0, 1.0, 2.0, 3.0, 2.0 }),
		new EqBuiltInPreset("night", "Night", new double[10] { 2.0, 1.0, 0.0, 0.0, -1.0, -1.0, -2.0, -3.0, -4.0, -4.0 }),
		new EqBuiltInPreset("movie", "Movie", new double[10] { 2.0, 1.0, 0.0, 1.0, 3.0, 3.0, 1.0, 0.0, -1.0, -2.0 }),
		new EqBuiltInPreset("podcast", "Podcast", new double[10] { -4.0, -3.0, -1.0, 2.0, 4.0, 4.0, 2.0, 0.0, -1.0, -2.0 })
	};

	public EqBuiltInPreset(string id, string displayName, double[] gainsDb)
	{
		if (gainsDb.Length != 10)
		{
			throw new ArgumentException($"Preset must have {10} bands.", "gainsDb");
		}
		Id = id;
		DisplayName = displayName;
		GainsDb = EqLayout.NormalizeGains(gainsDb);
	}

	public static EqBuiltInPreset? Find(string? id)
	{
		return All.FirstOrDefault((EqBuiltInPreset p) => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
	}
}
