using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AirStereo;

/// <summary>
/// Native DropDownButton and MenuFlyout keep selected text visible and place
/// the menu below the field without root/screen coordinate conversions.
/// </summary>
public sealed class BottomComboBox : UserControl, IDisposable
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(BottomComboBox), new PropertyMetadata("", ItemsChanged));
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(BottomComboBox), new PropertyMetadata(null, ItemsChanged));
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(BottomComboBox), new PropertyMetadata(-1, SelectionChangedCallback));
    public static readonly DependencyProperty SelectedTextProperty = DependencyProperty.Register(
        nameof(SelectedText), typeof(string), typeof(BottomComboBox), new PropertyMetadata(""));
    public static readonly DependencyProperty MaxDropDownHeightProperty = DependencyProperty.Register(
        nameof(MaxDropDownHeight), typeof(double), typeof(BottomComboBox), new PropertyMetadata(240d));

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public object? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public string SelectedText { get => (string)GetValue(SelectedTextProperty); private set => SetValue(SelectedTextProperty, value); }
    public double MaxDropDownHeight { get => (double)GetValue(MaxDropDownHeightProperty); set => SetValue(MaxDropDownHeightProperty, value); }
    public event EventHandler? SelectionChanged;

    private readonly TextBlock headerText;
    private readonly TextBlock selectedText;
    private readonly DropDownButton field;
    private readonly MenuFlyout menu = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
    private bool disposed;

    public BottomComboBox()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        headerText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        selectedText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        if (Application.Current.Resources.TryGetValue("AirStereoSettingsTitleStyle", out object? style) && style is Style textStyle)
            headerText.Style = textStyle;
        field = new DropDownButton
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = selectedText,
            Flyout = menu
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(headerText);
        panel.Children.Add(field);
        Content = panel;
        menu.Opening += OnMenuOpening;
        IsEnabledChanged += OnEnabledChanged;
        Unloaded += OnUnloaded;
        UpdateText();
    }

    private void OnMenuOpening(object? sender, object args) { if (!disposed) BuildMenu(); }
    private void OnEnabledChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!IsEnabled) menu.Hide(); }
    private void OnUnloaded(object sender, RoutedEventArgs args) => menu.Hide();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        menu.Hide();
        menu.Opening -= OnMenuOpening;
        IsEnabledChanged -= OnEnabledChanged;
        Unloaded -= OnUnloaded;
        menu.Items.Clear();
        menu.MenuFlyoutPresenterStyle = null;
        field.Flyout = null;
        SelectionChanged = null;
    }

    private object[] Choices() => ItemsSource is IEnumerable source ? source.Cast<object>().ToArray() : Array.Empty<object>();

    private static void ItemsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs _) =>
        ((BottomComboBox)sender).UpdateText();

    private static void SelectionChangedCallback(DependencyObject sender, DependencyPropertyChangedEventArgs _)
    {
        var selector = (BottomComboBox)sender;
        selector.UpdateText();
        selector.SelectionChanged?.Invoke(selector, EventArgs.Empty);
    }

    private void UpdateText()
    {
        object[] choices = Choices();
        SelectedText = SelectedIndex >= 0 && SelectedIndex < choices.Length
            ? choices[SelectedIndex]?.ToString() ?? "" : WinUiLanguage.T("请选择");
        headerText.Text = Header;
        AutomationProperties.SetName(field, Header + "：" + SelectedText);
        selectedText.Text = SelectedText;
    }

    private void BuildMenu()
    {
        menu.Items.Clear();
        object[] choices = Choices();
        for (int i = 0; i < choices.Length; i++)
        {
            int index = i;
            var item = new RadioMenuFlyoutItem
            {
                Text = choices[i]?.ToString() ?? "",
                IsChecked = i == SelectedIndex,
                GroupName = Name
            };
            item.Click += (_, _) => { if (IsEnabled) SelectedIndex = index; menu.Hide(); };
            menu.Items.Add(item);
        }
        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, Math.Max(160, ActualWidth)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, Math.Max(96, MaxDropDownHeight)));
        menu.MenuFlyoutPresenterStyle = presenterStyle;
    }
}
