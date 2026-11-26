using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using AirStereo2.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AirStereo2.ViewModels;

public sealed class DefaultOutputEntry : ObservableObject
{
	private readonly DeviceViewModel? _device;

	private readonly DeviceGroupViewModel? _group;

	private bool? _isSelected;

	public object Output { get; }

	public Geometry? IconData
	{
		get
		{
			object obj = _device?.IconData;
			if (obj == null)
			{
				DeviceGroupViewModel? deviceGroupViewModel = _group;
				if (deviceGroupViewModel == null)
				{
					return null;
				}
				obj = deviceGroupViewModel.IconData;
			}
			return (Geometry?)obj;
		}
	}

	public string KindText
	{
		get
		{
			if (_group == null)
			{
				return string.Empty;
			}
			if (_group.IsNativeStereoPair)
			{
				return LocalizationManager.Instance.GetString("Device_DefaultOutputStereoPair", "Stereo pair");
			}
			return string.Format(LocalizationManager.Instance.GetString("Device_DefaultOutputGroup", "Group · {0} speakers"), _group.Members.Count);
		}
	}

	public Visibility KindVisibility
	{
		get
		{
			if (_group != null)
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public bool? IsSelected
	{
		get
		{
			return _isSelected;
		}
		set
		{
			bool isDefaultOutput = DefaultOutputSelection.FlagAfterClick(_isSelected, value);
			foreach (DeviceViewModel item in Members())
			{
				item.IsDefaultOutput = isDefaultOutput;
			}
			Refresh();
		}
	}

	public DefaultOutputEntry(DeviceViewModel device)
	{
		_device = device;
		Output = device;
		_isSelected = ReadState();
	}

	public DefaultOutputEntry(DeviceGroupViewModel group)
	{
		_group = group;
		Output = group;
		_isSelected = ReadState();
	}

	internal bool Represents(object output)
	{
		return Output == output;
	}

	public void Refresh()
	{
		this.SetProperty<bool?>(ref _isSelected, ReadState(), "IsSelected");
		this.OnPropertyChanged("KindText");
		this.OnPropertyChanged("IconData");
	}

	private IReadOnlyList<DeviceViewModel> Members()
	{
		if (_device == null)
		{
			return _group?.Members.ToList() ?? new List<DeviceViewModel>();
		}
		return new DeviceViewModel[1] { _device };
	}

	private bool? ReadState()
	{
		return DefaultOutputSelection.EntryState((from member in Members()
			select member.IsDefaultOutput).ToList(), _group?.IsNativeStereoPair ?? false);
	}
}
