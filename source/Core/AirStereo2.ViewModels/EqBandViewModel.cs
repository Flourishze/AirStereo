using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using AirStereo2.Models;
using AirStereo2.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel.__Internals;

namespace AirStereo2.ViewModels;

public class EqBandViewModel : ObservableObject
{
	private readonly Action<EqBandViewModel>? _onGainChanged;

	private bool _suppressNotify;

	[ObservableProperty]
	private double _gainDb;

	public int Index { get; }

	public string FrequencyLabel { get; }

	public string FrequencyUnitLabel { get; }

	public string DisplayFrequency { get; }

	public string ZoneLabel { get; }

	public string GainText => EqLayout.FormatGainDb(GainDb);

	public string GainTooltip
	{
		get
		{
			LocalizationManager instance = LocalizationManager.Instance;
			double num = Math.Round(GainDb, 1);
			return string.Concat(str2: (Math.Abs(num) < 0.05) ? instance.GetString("Eq_GainZero", "0 dB") : ((!(num > 0.0)) ? instance.Format("Eq_GainNegative", "{0} dB", num.ToString("0.#")) : instance.Format("Eq_GainPositive", "+{0} dB", num.ToString("0.#"))), str0: DisplayFrequency, str1: ": ");
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public double GainDb
	{
		get
		{
			return _gainDb;
		}
		set
		{
			if (!EqualityComparer<double>.Default.Equals(_gainDb, value))
			{
				this.OnPropertyChanging(__KnownINotifyPropertyChangingArgs.GainDb);
				_gainDb = value;
				OnGainDbChanged(value);
				this.OnPropertyChanged(__KnownINotifyPropertyChangedArgs.GainDb);
			}
		}
	}

	public EqBandViewModel(int index, double gainDb, Action<EqBandViewModel>? onGainChanged = null)
	{
		Index = index;
		FrequencyLabel = EqLayout.FrequencyLabels[index];
		FrequencyUnitLabel = ((index < 5) ? "Hz" : "");
		DisplayFrequency = ((index < 5) ? (EqLayout.FrequencyLabels[index] + " Hz") : (EqLayout.FrequencyLabels[index] + "Hz"));
		ZoneLabel = EqLayout.ZoneLabels[index];
		_onGainChanged = onGainChanged;
		_suppressNotify = true;
		GainDb = Math.Clamp(gainDb, -12.0, 12.0);
		_suppressNotify = false;
	}

	public void SetGainSilent(double gainDb)
	{
		_suppressNotify = true;
		try
		{
			GainDb = Math.Clamp(gainDb, -12.0, 12.0);
		}
		finally
		{
			_suppressNotify = false;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnGainDbChanged(double value)
	{
		double num = Math.Clamp(value, -12.0, 12.0);
		if (Math.Abs(num - value) > 0.0001)
		{
			GainDb = num;
			return;
		}
		this.OnPropertyChanged("GainText");
		this.OnPropertyChanged("GainTooltip");
		if (!_suppressNotify)
		{
			_onGainChanged?.Invoke(this);
		}
	}
}
