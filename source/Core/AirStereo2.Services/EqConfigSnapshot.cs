using System.Collections.Generic;
using AirStereo2.Models;

namespace AirStereo2.Services;

public sealed record EqConfigSnapshot(bool Enabled, double PreampDb, string PresetId, IReadOnlyList<double> BandGains, IReadOnlyList<EqUserPresetConfig> UserPresets);
