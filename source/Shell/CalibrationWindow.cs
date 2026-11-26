using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AirStereo2;

internal sealed class CalibrationWindow : Window
{
    readonly BridgeClient client;
    readonly StackPanel body = new() { Spacing = 12, Padding = new Thickness(24) };
    readonly Dictionary<string, FrameworkElement> controls = new();
    bool updating, finished, seenOpen;
    string layout = "";
    internal CalibrationWindow(BridgeClient bridge)
    {
        client = bridge; Title = "AirStereo2 · " + WinUiLanguage.T("运行延迟校准");
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        WinUiTheme.Apply(body); WinUiTheme.Changed += Theme;
        AppWindow.Resize(new SizeInt32(680, 760));
        client.Changed += Refresh;
        Closed += (_, _) => { client.Changed -= Refresh; WinUiTheme.Changed -= Theme; if (!finished) _ = client.Send("calibration", name: "close"); };
        Refresh();
    }
    void Theme(WinUiAppearanceMode _) => WinUiTheme.Apply(body);
    void Refresh()
    {
        var state = client.Snapshot.Field("calibration");
        if (!state.Flag("open")) { if (seenOpen) { finished = true; Close(); } return; }
        seenOpen = true;
        var fields = state.List("fields");
        string nextLayout = string.Join("|", fields.Select(x => x.Text("type") + x.Text("id") + (x.Text("type") == "list" ? x.Field("items").GetRawText() : "")));
        updating = true;
        try {
            if (layout != nextLayout) {
                layout = nextLayout; controls.Clear(); body.Children.Clear();
                var title = ShellView.Text("运行延迟校准"); title.Style = ShellView.Style("AirStereo2SectionTitleStyle"); body.Children.Add(title);
                foreach (var field in fields) {
                    string id = field.Text("id");
                    FrameworkElement? control = null;
                    switch (field.Text("type")) {
                        case "text": control = ShellView.Text(field.Text("text")); break;
                        case "input":
                            var input = new TextBox { MaxLength = (int)field.Number("maxLength") };
                            input.TextChanged += async (_, _) => { if (!updating) await client.Send("calibration", name: id, value: input.Text); };
                            control = input; break;
                        case "check": case "radio":
                            var check = new CheckBox();
                            check.Click += async (_, _) => { if (!updating) await client.Send("calibration", name: id, value: check.IsChecked == true); };
                            control = check; break;
                        case "button":
                            var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch };
                            button.Click += async (_, _) => { button.IsEnabled = false; await client.Send("calibration", name: id); };
                            control = button; break;
                        case "progress": control = new ProgressBar(); break;
                        case "list":
                            var list = new StackPanel { Spacing = 8 };
                            foreach (var item in field.List("items")) {
                                int index = (int)item.Number("index");
                                var select = new CheckBox { Content = item.Text("text"), IsChecked = item.Flag("selected"), IsEnabled = field.Flag("enabled") };
                                select.Click += async (_, _) => await client.Send("calibration", name: id, value: new { index, selected = select.IsChecked == true });
                                list.Children.Add(select);
                            }
                            control = list; break;
                    }
                    if (control != null) { controls[id] = control; body.Children.Add(control); }
                }
            }
            foreach (var field in fields) {
                if (!controls.TryGetValue(field.Text("id"), out var control)) continue;
                if (control is Control interactive) interactive.IsEnabled = field.Field("enabled").ValueKind != JsonValueKind.False;
                if (control is TextBlock text) text.Text = field.Text("text");
                else if (control is TextBox input && input.FocusState == FocusState.Unfocused) input.Text = field.Text("text");
                else if (control is CheckBox check) { check.Content = field.Text("text"); check.IsChecked = field.Flag("value"); }
                else if (control is Button button) button.Content = field.Text("text");
                else if (control is ProgressBar progress) {
                    progress.Minimum = field.Number("minimum"); progress.Maximum = field.Number("maximum", 100);
                    progress.Value = field.Number("value"); progress.IsIndeterminate = field.Flag("indeterminate");
                }
            }
        } finally { updating = false; }
    }
}
