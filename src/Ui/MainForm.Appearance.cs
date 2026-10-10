using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    public sealed partial class MainForm
    {
        private ComboBox appearanceChoice;
        private Label appearanceHint;
        private TableLayoutPanel appearanceOptions;
        private bool appearanceSyncing;

        private Control BuildAppearanceOptions()
        {
            TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 2, Margin = new Padding(0), BackColor = CanvasColor };
            appearanceOptions = row;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label caption = new Label { Text = "外观", TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = InkColor, Margin = new Padding(0) };
            appearanceChoice = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Standard,
                BackColor = PanelColor, ForeColor = InkColor, AccessibleName = "外观主题",
                Margin = new Padding(0, 4, 0, 4) };
            appearanceChoice.Items.AddRange(new object[] { "跟随系统", "浅色", "深色" });
            appearanceChoice.SelectedIndex = (int)DesktopTheme.Mode;
            appearanceHint = new Label { Text = "即时生效，跟随 Windows 应用颜色模式", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = MutedColor, AutoEllipsis = true,
                Margin = new Padding(0) };
            row.Controls.Add(new AppearanceSelectionRow(caption, appearanceChoice) {
                Dock = DockStyle.Fill, Margin = new Padding(0) }, 0, 0);
            row.Controls.Add(appearanceHint, 0, 1);
            appearanceChoice.SelectedIndexChanged += delegate
            {
                if (appearanceSyncing || appearanceChoice.SelectedIndex < 0) return;
                DesktopTheme.SetMode((AppearanceMode)appearanceChoice.SelectedIndex);
                if (OfflinePreview) return;
                try
                {
                    AppearanceSettings.Write(AppearancePath(), DesktopTheme.Mode);
                    appearanceHint.Text = "外观已保存，下次启动自动应用";
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { appearanceHint.Text = "外观已应用，但无法保存；下次启动将恢复原设置"; }
            };
            return row;
        }

        // EDIT/ComboBox native height updates after font changes. Keep both
        // controls in one measured row rather than relying on table anchoring.
        private sealed class AppearanceSelectionRow : Panel
        {
            private readonly Label caption;
            private readonly ComboBox choice;
            internal AppearanceSelectionRow(Label caption, ComboBox choice)
            {
                this.caption = caption;
                this.choice = choice;
                Controls.Add(caption);
                Controls.Add(choice);
                choice.SizeChanged += delegate { PerformLayout(); };
            }
            protected override void OnLayout(LayoutEventArgs args)
            {
                base.OnLayout(args);
                if (caption == null || choice == null) return;
                SuspendLayout();
                try
                {
                    int gap = Math.Max(16, DeviceDpi * 16 / 96);
                    int width = TextRenderer.MeasureText(caption.Text, caption.Font).Width + gap;
                    int available = Math.Max(1, ClientSize.Width - width);
                    int choiceWidth = Math.Min(available, Math.Max(180 * Math.Max(96, DeviceDpi) / 96,
                        TextRenderer.MeasureText("跟随系统", choice.Font).Width + gap * 3));
                    choice.SetBounds(ClientSize.Width - choiceWidth, 0, choiceWidth, choice.PreferredHeight);
                    choice.Top = Math.Max(0, (ClientSize.Height - choice.Height) / 2);
                    caption.SetBounds(0, choice.Top, width - gap, choice.Height);
                }
                finally { ResumeLayout(false); }
            }
        }

        private static string AppearancePath() => Path.Combine(PackageEnvironment.DataDirectory, "appearance.txt");

        private void InitializeAppearance()
        {
            DesktopTheme.Attach(this);
            DesktopTheme.Changed += OnAppearanceChanged;
            if (trayIcon?.ContextMenuStrip != null)
            {
                trayIcon.ContextMenuStrip.Renderer = new ToolStripProfessionalRenderer(new DesktopTheme.MenuColors());
                trayIcon.ContextMenuStrip.Opening += delegate { ApplyMenuAppearance(); };
            }
        }

        private void LoadAppearance()
        {
            DesktopTheme.SetMode(AppearanceSettings.Read(AppearancePath()));
            OnAppearanceChanged(DesktopTheme.Current);
        }

        private void OnAppearanceChanged(DesktopTheme.Palette previous)
        {
            if (IsDisposed) return;
            appearanceSyncing = true;
            try { appearanceChoice.SelectedIndex = (int)DesktopTheme.Mode; }
            finally { appearanceSyncing = false; }
            // Recolour existing rows, without rebuilding them, changing selection,
            // changing any value or calling playback/volume/update handlers.
            foreach (DeviceRow row in deviceRows)
            {
                bool selected = selectedKeys.Contains(row.SelectionKey);
                ApplyRowBackground(row.Root, selected ? DesktopTheme.Selection : DesktopTheme.Surface);
                row.Role.ForeColor = selected && DesktopTheme.Current.HighContrast ? SystemColors.HighlightText :
                    row.Group.IsStereoPair ? AccentDarkColor : AccentColor;
            }
            uiTips.BackColor = PanelColor;
            uiTips.ForeColor = InkColor;
            ApplyMenuAppearance();
        }

        private void ApplyMenuAppearance()
        {
            if (trayIcon?.ContextMenuStrip == null) return;
            ContextMenuStrip menu = trayIcon.ContextMenuStrip;
            menu.BackColor = PanelColor;
            menu.ForeColor = InkColor;
            foreach (ToolStripItem item in menu.Items) item.ForeColor = InkColor;
            menu.Invalidate();
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            // Windows sends these to top-level windows when application colours or
            // accessibility settings change. No polling timer or global event leak.
            if ((message.Msg == 0x001A || message.Msg == 0x031A) && !OfflinePreview && appearanceChoice != null)
            {
                DesktopTheme.SetMode(DesktopTheme.Mode);
                UpdateNotificationIcon();
            }
        }
    }
}
