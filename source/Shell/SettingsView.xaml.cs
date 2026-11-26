using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirStereo2;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    internal T Control<T>(string name) where T : DependencyObject => (T)FindName(name);
}
