using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AirStereo.Audio;

namespace AirStereo.Ui
{
    internal static partial class UiRegressionTests
    {
        private static void VerifyAppearance(Action<string, bool, string> check)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "selftest-artifacts", Guid.NewGuid().ToString("N"), "appearance.txt");
            check("appearance missing preference follows system", AppearanceSettings.Read(path) == AppearanceMode.System, null);
            foreach (AppearanceMode mode in Enum.GetValues(typeof(AppearanceMode)))
            {
                AppearanceSettings.Write(path, mode);
                check("appearance preference roundtrip " + mode, AppearanceSettings.Read(path) == mode, null);
            }
            File.WriteAllText(path, "99");
            check("appearance corrupt preference falls back to system", AppearanceSettings.Read(path) == AppearanceMode.System, null);
            check("appearance system mode resolves both Windows app colours",
                !DesktopTheme.ResolveDark(AppearanceMode.System, true) && DesktopTheme.ResolveDark(AppearanceMode.System, false) &&
                !DesktopTheme.ResolveDark(AppearanceMode.Light, false) && DesktopTheme.ResolveDark(AppearanceMode.Dark, true), null);
            using (ValueSlider vertical = new ValueSlider { Orientation = Orientation.Vertical,
                Minimum = -12, Maximum = 12, Height = 180, Width = 80 })
            {
                typeof(ValueSlider).GetMethod("OnKeyDown", Private).Invoke(vertical, new object[] { new KeyEventArgs(Keys.Up) });
                check("appearance EQ slider up increases gain and exposes accessible value",
                    vertical.Value == 1 && vertical.AccessibilityObject.Value == "1", null);
                typeof(ValueSlider).GetMethod("SetFromPointer", Private).Invoke(vertical, new object[] { 40, 0 });
                check("appearance EQ pointer top is maximum gain", vertical.Value == 12, null);
                typeof(ValueSlider).GetMethod("SetFromPointer", Private).Invoke(vertical, new object[] { 40, 180 });
                check("appearance EQ pointer bottom is minimum gain", vertical.Value == -12, null);
            }
            AudioProfile applied = null;
            using (CalibrationForm eq = new CalibrationForm(new AudioProfileController(), AudioProfile.Flat, p => applied = p))
            {
                ((CheckBox)Field(eq, "enabledBox")).Checked = true;
                ((ValueSlider[])Field(eq, "bands"))[2].Value = 4;
                ((ValueSlider)Field(eq, "balanceBar")).Value = 3;
                check("appearance EQ controls retain existing DSP gain and trim mapping", applied.Enabled &&
                    applied.BandGainDb(2) == 4 && applied.LeftGainDb == -3 && applied.RightGainDb == 0, null);
            }
            try
            {
                foreach (float scale in new[] { 1F, 1.25F, 1.5F, 1.75F, 2F })
                using (MainForm form = new MainForm { OfflinePreview = true })
                {
                    Populate(form, new List<Receiver> { Speaker("客厅音响", 1), Speaker("卧室音响", 2) });
                    CheckAt(form, 0).Checked = true;
                    CheckAt(form, 1).Checked = true;
                    Form settings = (Form)Field(form, "settingsForm");
                    CreateHandles(form);
                    CreateHandles(settings);
                    form.Scale(new SizeF(scale, scale));
                    settings.Scale(new SizeF(scale, scale));
                    settings.Font = new Font(settings.Font.FontFamily, 9F * scale);
                    var tabs = (SettingsTabs)((DarkSettingsForm)settings).ContentHost.Controls[0];
                    var choice = (ComboBox)Field(form, "appearanceChoice");
                    var volume = (ValueSlider)Field(form, "volumeBar");
                    var balance = (ValueSlider)Field(form, "stereoBalance");
                    var delay = (ValueSlider)Field(form, "latencyBar");
                    int volumeChanges = 0, balanceChanges = 0, delayChanges = 0;
                    volume.ValueChanged += delegate { volumeChanges++; };
                    balance.ValueChanged += delegate { balanceChanges++; };
                    delay.ValueChanged += delegate { delayChanges++; };
                    CheckBox first = CheckAt(form, 0), second = CheckAt(form, 1);
                    foreach (AppearanceMode mode in new[] { AppearanceMode.Light, AppearanceMode.Dark, AppearanceMode.Light })
                    {
                        choice.SelectedIndex = (int)mode;
                        bool light = mode == AppearanceMode.Light;
                        check("appearance popup palette " + mode + " " + scale,
                            form.BackColor == DesktopTheme.Canvas && (form.BackColor.GetBrightness() > .5F) == light, null);
                        string detail = "";
                        check("appearance popup geometry " + mode + " " + scale, Fits(form, ref detail), detail);
                        for (int index = 0; index < tabs.PageCount; index++)
                        {
                            tabs.SelectedIndex = index;
                            settings.PerformLayout();
                            detail = "";
                            check("appearance settings geometry " + mode + " page " + index + " " + scale, Fits(settings, ref detail), detail);
                            check("appearance no opposite-theme surface " + mode + " page " + index + " " + scale,
                                HasThemeBackground(settings, light), null);
                            if (scale == 1F || scale == 2F)
                                RenderPreview(settings, "fluent-" + mode + "-page-" + index + "-" + (int)(scale * 100));
                        }
                        RenderPreview(form, "fluent-" + mode + "-popup-" + (int)(scale * 100));
                    }
                    Set(form, "playing", true);
                    Set(form, "streamReady", true);
                    var live = new LivePlaybackControl(true);
                    Set(form, "livePlayback", live);
                    Call(form, "UpdateButtons");
                    foreach (AppearanceMode mode in new[] { AppearanceMode.Dark, AppearanceMode.Light })
                    {
                        choice.SelectedIndex = (int)mode;
                        check("appearance retains active session and locked delay " + mode,
                            ReferenceEquals(Field(form, "livePlayback"), live) && (bool)Field(form, "playing") &&
                            delay.InputLocked && !((Button)Field(form, "playButton")).Enabled, null);
                    }
                    check("appearance does not emit audio value events or rebuild selected targets " + scale,
                        volumeChanges == 0 && balanceChanges == 0 && delayChanges == 0 &&
                        ReferenceEquals(first, CheckAt(form, 0)) && ReferenceEquals(second, CheckAt(form, 1)) &&
                        first.Checked && second.Checked && RoleAt(form, 0) == "L" && RoleAt(form, 1) == "R", null);
                    Set(form, "playing", false);
                    Set(form, "livePlayback", null);
                }
                using (MainForm form = new MainForm { OfflinePreview = true })
                using (CalibrationForm eq = new CalibrationForm(new AudioProfileController(), AudioProfile.Flat,
                    _ => throw new InvalidOperationException("Theme changed the DSP profile")))
                {
                    CreateHandles(form); CreateHandles(eq);
                    foreach (AppearanceMode mode in new[] { AppearanceMode.Dark, AppearanceMode.Light })
                    {
                        DesktopTheme.SetMode(mode, true, false);
                        check("appearance EQ inherits live palette " + mode, HasThemeBackground(eq, mode == AppearanceMode.Light), null);
                        RenderPreview(eq, "fluent-" + mode + "-eq");
                        DesktopTheme.SetMode(mode, true, true);
                        check("appearance high contrast uses system colours " + mode,
                            form.BackColor == SystemColors.Window && form.ForeColor == SystemColors.WindowText, null);
                        DesktopTheme.SetMode(mode, true, false);
                        check("appearance can leave high contrast without losing semantic colours " + mode,
                            form.BackColor == DesktopTheme.Canvas && eq.BackColor == DesktopTheme.Canvas, null);
                    }
                }
                foreach (AppearanceMode mode in new[] { AppearanceMode.Dark, AppearanceMode.Light })
                foreach (float scale in new[] { 1F, 1.25F, 1.5F, 1.75F, 2F })
                foreach (float fontSize in new[] { 9F, 14F })
                {
                    DesktopTheme.SetMode(mode, true, false);
                    using (MainForm form = new MainForm { OfflinePreview = true })
                    using (CalibrationForm eq = new CalibrationForm(new AudioProfileController(), AudioProfile.Flat, _ => { }))
                    {
                        Form settings = (Form)Field(form, "settingsForm");
                        CreateHandles(settings); CreateHandles(eq);
                        settings.Scale(new SizeF(scale, scale));
                        settings.Font = new Font(settings.Font.FontFamily, fontSize * scale);
                        settings.ClientSize = new Size((int)(760 * scale), (int)(620 * scale));
                        settings.Opacity = 0;
                        settings.Show();
                        var tabs = (SettingsTabs)((DarkSettingsForm)settings).ContentHost.Controls[0];
                        for (int i = 0; i < tabs.PageCount; i++)
                        {
                            tabs.SelectedIndex = i;
                            string detail = "";
                            check("appearance large-text settings " + mode + " " + scale + " " + fontSize + " page " + i,
                                Fits(settings, ref detail), detail);
                        }
                        eq.Font = new Font(eq.Font.FontFamily, fontSize * scale);
                        eq.ClientSize = new Size((int)(660 * scale), (int)(540 * scale));
                        eq.PerformLayout();
                        string eqDetail = "";
                        check("appearance EQ font/DPI geometry " + mode + " " + scale + " " + fontSize, Fits(eq, ref eqDetail), eqDetail);
                        if (scale == 2F && fontSize == 14F)
                        {
                            RenderPreview(eq, "fluent-" + mode + "-eq-large-text-200");
                            tabs.SelectedIndex = 1;
                            RenderPreview(settings, "fluent-" + mode + "-general-large-text-200");
                        }
                    }
                }
            }
            finally { DesktopTheme.SetMode(AppearanceMode.Dark, false, false); }
        }

        private static bool HasThemeBackground(Control control, bool light)
        {
            if ((control.BackColor.GetBrightness() > .5F) != light) return false;
            foreach (Control child in control.Controls) if (!HasThemeBackground(child, light)) return false;
            return true;
        }
    }
}
