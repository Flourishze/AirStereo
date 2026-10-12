using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace AirStereo;

internal sealed partial class WinUiSettingsView : UserControl
{
    internal NavigationView Navigation => SettingsNavigation;
    internal Grid Root => SettingsRoot;
    internal ScrollViewer GeneralPageView => GeneralPage;
    internal ScrollViewer AudioPageView => AudioPage;
    internal Grid RecentPageView => RecentPage;
    internal ScrollViewer DiagnosticsPageView => DiagnosticsPage;
    internal BottomComboBox LatencyBox => Latency;
    internal BottomComboBox AppearanceBox => Appearance;
    internal BottomComboBox LanguageBox => LanguageChoice;
    internal TextBlock LanguageStatusText => LanguageStatus;
    internal TextBlock AppearanceStatusText => AppearanceStatus;
    internal Slider CustomLatencySlider => CustomLatency;
    internal TextBlock CustomLatencyText => CustomLatencyValue;
    internal BottomComboBox CodecBox => Codec;
    internal TextBlock CodecDescriptionText => CodecDescription;
    internal BottomComboBox RateBox => Rate;
    internal CheckBox ReconnectCheckBox => Reconnect;
    internal NumberBox ReconnectAttemptsBox => ReconnectAttempts;
    internal CheckBox StartupCheckBox => Startup;
    internal CheckBox AutoConnectCheckBox => AutoConnect;
    internal CheckBox SilentStartupCheckBox => SilentStartup;
    internal CheckBox MuteLocalCheckBox => MuteLocal;
    internal CheckBox EqualizerEnabledCheckBox => EqualizerEnabled;
    internal BottomComboBox EqualizerPresetBox => EqualizerPreset;
    internal Grid EqualizerBandPanel => EqualizerBands;
    internal Slider BalanceSlider => Balance;
    internal TextBlock BalanceText => BalanceValue;
    internal TextBlock DiagnosticsText => Diagnostics;
    internal TextBlock LatestNoticeText => LatestNotice;
    internal TextBlock RecentActivityText => RecentActivity;
    internal Button OpenDiagnosticsButton => OpenDiagnostics;
    internal Button ReloadDiagnosticsButton => ReloadDiagnostics;
    internal Button UpdateButton => CheckUpdates;
    internal Button UpdateDownloadButton => UpdateDownload;
    internal TextBlock UpdateStatusText => UpdateStatus;

    internal WinUiSettingsView() => InitializeComponent();

    internal void SetConnectionControlsEnabled(bool canEdit, bool customLatency)
    {
        Latency.IsEnabled = canEdit;
        Codec.IsEnabled = canEdit;
        Rate.IsEnabled = canEdit;
        CustomLatency.IsEnabled = canEdit && customLatency;
    }
}
