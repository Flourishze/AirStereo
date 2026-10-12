using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirStereo;

internal sealed partial class WinUiMainView : UserControl
{
    internal Grid Root => LayoutRoot;
    internal Button RefreshButtonControl => RefreshButton;
    internal FontIcon RefreshIconControl => RefreshIcon;
    internal Grid ApplyVolumeIconControl => ApplyVolumeIcon;
    internal Button SettingsButton => Settings;
    internal Button HideButton => Hide;
    internal TextBlock StatusText => Status;
    internal StackPanel DeviceList => Devices;
    internal Button PlayButton => Play;
    internal FontIcon PlayIconControl => PlayIcon;
    internal TextBlock PlayTextControl => PlayText;
    internal Slider VolumeSlider => Volume;
    internal CheckBox RealtimeVolumeCheckBox => RealtimeVolume;
    internal TextBlock VolumeText => VolumeValue;
    internal TextBlock FormatText => Format;
    internal Button ApplyVolumeButton => ApplyVolume;

    internal WinUiMainView() => InitializeComponent();
}
