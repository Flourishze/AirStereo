using System.Collections.Generic;
using System.Linq;

namespace AirStereo2.Models;

public sealed class EqUserPresetConfig
{
	public string Name { get; set; } = string.Empty;

	public List<double> Gains { get; set; } = EqLayout.NormalizeGains(null).ToList();
}
