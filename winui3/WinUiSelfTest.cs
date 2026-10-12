using AirStereo.Audio;

namespace AirStereo;

internal static class SelfTest
{
    private static int languageUiResult = 1;

    internal static int RunLanguageUi()
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start(queueController =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                _ = new WinUiApplication(false, Session.SessionOptions.DefaultSourceSilenceDisconnectMilliseconds,
                    true, Session.SilenceFrameMode.RepeatLast, languageSelfTest: true);
            });
        }
        catch (Exception error) { return Fail(error.ToString()); }
        return languageUiResult;
    }

    internal static void CheckLanguageViews()
    {
        bool originalLanguage = WinUiLanguage.English;
        try
        {
            WinUiLanguage.ApplyPreference(false);
            var main = new WinUiMainView();
            using (var motion = new WinUiMotion())
            {
                var icon = main.ApplyVolumeIconControl;
                motion.Pulse(icon);
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
                // Simulate the new placement assigned by layout after the first click.
                var arrangedOffset = new System.Numerics.Vector3(8, 8, 0);
                visual.Offset = arrangedOffset;
                for (int click = 0; click < 30; click++)
                {
                    main.ApplyVolumeButton.IsEnabled = click % 2 == 0;
                    motion.Pulse(icon);
                    if (!Verify(visual.Offset == arrangedOffset,
                        "Repeated volume pulses must preserve the current layout position.")) return;
                }
                main.ApplyVolumeButton.IsEnabled = true;
            }
            var settings = new WinUiSettingsView();
            var navItem = (Microsoft.UI.Xaml.Controls.NavigationViewItem)settings.Navigation.MenuItems[0];
            settings.LatencyBox.ItemsSource = new[] { "实时 · 120 ms", "标准 · 200 ms" };
            settings.LatencyBox.SelectedIndex = 1;
            settings.ReconnectCheckBox.IsChecked = true;
            settings.ReconnectAttemptsBox.Value = 7;
            settings.SilentStartupCheckBox.IsChecked = true;
            var message = new Microsoft.UI.Xaml.Controls.TextBlock();
            WinUiLanguage.Message(message, "当前 {0}，远程 {1}，无需更新。", "1.0.6", "1.0.6");
            if (!Verify(main.PlayTextControl.Text == "播放电脑声音" && (string)navItem.Content == "常规", "Chinese XAML initialization failed.")) return;
            WinUiLanguage.ApplyPreference(true);
            if (!Verify(main.PlayTextControl.Text == "Play PC audio" && (string)navItem.Content == "General" &&
                settings.LatencyBox.Header == "Latency buffer" && settings.LanguageBox.Header == "Language" &&
                settings.CodecBox.Header == "Audio encoding" && settings.RateBox.Header == "Sample rate" &&
                settings.CodecDescriptionText.Text.StartsWith("Both provide identical audio quality.", StringComparison.Ordinal) &&
                (string)settings.SilentStartupCheckBox.Content == "Start silently (show tray icon only)" &&
                settings.SilentStartupCheckBox.IsChecked == true &&
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(main.ApplyVolumeButton) == "Apply volume",
                "English live switch did not update XAML text, headers and accessibility labels.")) return;
            if (!Verify(message.Text == "Installed: 1.0.6. Latest: 1.0.6. No update needed.", "Live switch lost formatted message arguments.")) return;
            if (!Verify(settings.LatencyBox.SelectedIndex == 1 && settings.ReconnectCheckBox.IsChecked == true &&
                settings.ReconnectAttemptsBox.Value == 7, "Language switch changed a setting value.")) return;
            WinUiLanguage.ApplyPreference(false);
            if (!Verify(main.PlayTextControl.Text == "播放电脑声音" && (string)navItem.Content == "常规" &&
                settings.LatencyBox.Header == "延迟缓冲" && settings.LanguageBox.Header == "语言" &&
                settings.CodecBox.Header == "音频编码" && settings.RateBox.Header == "采样率" &&
                settings.CodecDescriptionText.Text == "两者音质完全相同。ALAC 压缩后占用带宽约为 PCM 的一半；PCM 无需编码，占用带宽更高",
                "Switching back to Chinese failed.")) return;
            if (!Verify((string)settings.SilentStartupCheckBox.Content == "开机静默启动（仅显示托盘图标）" &&
                settings.SilentStartupCheckBox.IsChecked == true, "Silent startup label/value did not survive language switching.")) return;
            var codecHeader = (Microsoft.UI.Xaml.Controls.TextBlock)((Microsoft.UI.Xaml.Controls.StackPanel)settings.CodecBox.Content).Children[0];
            if (!Verify(codecHeader.Text == "音频编码" && codecHeader.ReadLocalValue(Microsoft.UI.Xaml.Controls.TextBlock.ForegroundProperty) == Microsoft.UI.Xaml.DependencyProperty.UnsetValue,
                "Dropdown header must use the theme text style instead of a transparent local foreground.")) return;
            settings.SetConnectionControlsEnabled(false, true);
            if (!Verify(!settings.LatencyBox.IsEnabled && !settings.CodecBox.IsEnabled && !settings.RateBox.IsEnabled &&
                !settings.CustomLatencySlider.IsEnabled && settings.LanguageBox.IsEnabled && settings.AppearanceBox.IsEnabled,
                "Active connection must lock stream format controls while keeping language and appearance editable.")) return;
            settings.SetConnectionControlsEnabled(true, true);
            if (!Verify(settings.LatencyBox.IsEnabled && settings.CodecBox.IsEnabled && settings.RateBox.IsEnabled &&
                settings.CustomLatencySlider.IsEnabled && settings.LatencyBox.SelectedIndex == 1,
                "Stopping must unlock stream format controls without changing settings.")) return;
            settings.SetConnectionControlsEnabled(true, false);
            if (!Verify(!settings.CustomLatencySlider.IsEnabled, "Custom latency must stay disabled for a preset.")) return;
            using (var tray = new WinUiTrayIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "AirStereo.ico")))
            {
                bool exitRequested = false;
                int stopRequests = 0;
                tray.StopPlayback += () => stopRequests++;
                tray.Exit += () => exitRequested = true;
                tray.HandleMenuCommand(3);
                tray.HandleMenuCommand(3);
                if (!Verify(stopRequests == 2 && !exitRequested && tray.Registered,
                    "Tray Stop playback must request playback stop without exiting or removing the tray icon.")) return;
                tray.HandleMenuCommand(0);
                if (!Verify(stopRequests == 2 && !exitRequested, "Canceled tray menu must do nothing.")) return;
                tray.HandleMenuCommand(4);
                if (!Verify(exitRequested && stopRequests == 2, "Tray Exit must remain separate from Stop playback.")) return;
                exitRequested = false;
                var hwnd = (nint)typeof(WinUiTrayIcon).GetField("hwnd", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tray)!;
                if (!Verify(SendMessageW(hwnd, 0x0011, 0, 1) == 1 && !exitRequested, "Tray must accept installer shutdown without exiting during the query.")) return;
                SendMessageW(hwnd, 0x0016, 0, 1);
                if (!Verify(!exitRequested, "Canceled shutdown must keep the app running.")) return;
                SendMessageW(hwnd, 0x0016, 1, 1);
                if (!Verify(exitRequested, "Installer end-session must invoke the existing exit path.")) return;
            }
            Console.WriteLine("AirStereo language UI self-test passed: compiled views, live Chinese/English switch, formatted messages, settings preservation, tray stop/exit separation and installer shutdown.");
            languageUiResult = 0;
        }
        catch (Exception error) { Fail(error.ToString()); }
        finally { WinUiLanguage.ApplyPreference(originalLanguage); }
    }

    internal static int Run()
    {
        int checks = 0;
        if (!Verify(AudioProfile.BandCount == 10, "EQ band count is not ten.")) return 1;
        checks++;

        double[] expected = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        for (int i = 0; i < expected.Length; i++)
        {
            if (!Verify(AudioProfile.Frequency(i) == expected[i], "EQ frequency table mismatch at band " + i + ".")) return 1;
            if (!Verify(AudioProfile.Q(i) > 0, "EQ Q must be positive at band " + i + ".")) return 1;
        }
        checks++;

        double[] expanded = AudioProfile.ExpandLegacyFiveBandGains(new double[] { 1, 2, 3, 4, 5 });
        int[] targetBands = { 1, 3, 5, 7, 9 };
        for (int i = 0; i < targetBands.Length; i++)
        {
            if (!Verify(expanded[targetBands[i]] == i + 1, "Legacy five-band EQ migration failed.")) return 1;
        }
        if (expanded.Where((gain, index) => !targetBands.Contains(index)).Any(gain => gain != 0))
            return Fail("Legacy EQ migration changed an unrelated new band.");
        checks++;

        double[] gains = Enumerable.Range(0, AudioProfile.BandCount).Select(index => (double)index - 4).ToArray();
        AudioProfile profile = new AudioProfile(true, gains, 0, 0);
        for (int i = 0; i < gains.Length; i++)
        {
            if (!Verify(profile.BandGainDb(i) == gains[i], "Ten-band EQ profile lost a band value.")) return 1;
        }
        checks++;

        if (!VerifyStartupRoutes(out int startupChecks)) return 1;
        checks += startupChecks;
        if (!VerifySilentStartup(out int silentChecks)) return 1;
        checks += silentChecks;
        string[] uiKeys = { "设置", "诊断日志", "故障信息", "播放电脑声音", "实时音量调整", "扫描中…", "连接中…",
            "播放中", "流行", "平坦", "自定义", "跟随系统", "原生立体声对 · {0} 只" };
        foreach (string key in uiKeys)
        {
            if (!Verify(WinUiTextCatalog.Translate(key, false) == key && WinUiTextCatalog.Translate(key, true) != key,
                "Chinese/English translation missing: " + key)) return 1;
        }
        checks++;
        if (!Verify(string.Format(WinUiTextCatalog.Translate("当前 {0}，远程 {1}，无需更新。", true), "1.0.6", "1.0.6") ==
            "Installed: 1.0.6. Latest: 1.0.6. No update needed.", "Localized version placeholders changed.")) return 1;
        checks++;
        string speakerName = "卧室 · HomePod";
        if (!Verify(WinUiTextCatalog.Translate(speakerName, true) == speakerName,
            "Unknown or user-provided text must not be rewritten.")) return 1;
        checks++;
        if (!Verify(!WinUiMainWindow.ShouldDismissPopup(true, false, true, false, 1, 2, 2) &&
            WinUiMainWindow.ShouldDismissPopup(true, false, false, false, 1, 2, 2),
            "Closing settings after desktop focus must resume popup dismissal.")) return 1;
        checks++;
        if (!Verify(!WinUiMainWindow.ShouldDismissPopup(true, false, false, false, 1, 1, 1) &&
            !WinUiMainWindow.ShouldDismissPopup(true, false, false, false, 1, 2, 1) &&
            !WinUiMainWindow.ShouldDismissPopup(true, false, false, true, 1, 2, 2) &&
            !WinUiMainWindow.ShouldDismissPopup(true, false, false, false, 1, 0, 0),
            "Popup focus, owned flyouts, tray menus and transient missing foreground must not dismiss.")) return 1;
        checks++;
        if (!Verify(!WinUiMainWindow.ShouldDismissPopup(false, false, false, false, 1, 2, 2) &&
            !WinUiMainWindow.ShouldDismissPopup(true, true, false, false, 1, 2, 2),
            "Hidden or exiting popup must not schedule dismissal.")) return 1;
        checks++;
        Console.WriteLine("AirStereo WinUI self-test passed: " + checks + " checks (10-band EQ, startup routes, Chinese/English text).");
        return 0;
    }

    private static bool VerifyStartupRoutes(out int checks)
    {
        int count = 0;
        bool Check(bool condition, string message)
        {
            count++;
            return Verify(condition, message);
        }
        ReceiverGroup Speaker(string id, string name = "Speaker", string address = "192.0.2.1") => new()
        {
            Name = name,
            Members = new List<Receiver> { new() { ServiceDeviceId = id, Instance = name, Address = address, Port = 7000 } }
        };

        ReceiverGroup left = Speaker("AA", "Left");
        ReceiverGroup right = Speaker("BB", "Right");
        ReceiverGroup[] available = { right, left };
        PlaybackRoute? single = WinUiController.ResolveStartupRoute(available, new[] { "device:AA" });
        bool passed = Check(single?.Target == left && !single.SplitStereo && !single.NativePair,
            "Startup single speaker must preserve full stereo.");
        PlaybackRoute? dual = WinUiController.ResolveStartupRoute(available, new[] { "device:AA", "device:BB" });
        passed &= Check(dual?.SplitStereo == true && dual.Target.Members[0] == left.Members[0] &&
            dual.Target.Members[1] == right.Members[0], "Startup must preserve saved L/R order, not scan order.");
        passed &= Check(WinUiController.ResolveStartupRoute(new[] { left }, new[] { "device:AA", "device:BB" }) == null,
            "Startup with a missing speaker must not downgrade to a single speaker.");
        ReceiverGroup renamed = Speaker("AA", "Renamed", "192.0.2.99");
        passed &= Check(WinUiController.ResolveStartupRoute(new[] { renamed }, new[] { "device:aa" })?.Target == renamed,
            "Startup stable identity must survive name/IP changes and casing.");
        ReceiverGroup native = new() { Name = "Native pair", GroupId = "pair", StereoPairId = "pair",
            Members = new List<Receiver> { right.Members[0], left.Members[0] } };
        PlaybackRoute? pair = WinUiController.ResolveStartupRoute(new[] { native }, new[] { "group:device:AA,device:BB" });
        passed &= Check(pair?.NativePair == true && !pair.SplitStereo,
            "Startup native pair must remain one complete stereo target.");
        ReceiverGroup incomplete = Speaker("AA");
        incomplete.GroupId = incomplete.StereoPairId = "pair";
        passed &= Check(WinUiController.ResolveStartupRoute(new[] { incomplete }, new[] { "device:AA" }) == null &&
            WinUiController.ResolveStartupRoute(new[] { incomplete }, new[] { "group:device:AA,device:BB" }) == null,
            "Startup incomplete native pair must not connect.");
        passed &= Check(WinUiController.ResolveStartupRoute(available, Array.Empty<string>()) == null,
            "Startup without saved targets must not connect.");
        passed &= Check(WinUiController.ResolveStartupRoute(available, new[] { "device:AA", "device:BB", "device:CC" }) == null,
            "Startup must reject more than two targets.");
        passed &= Check(WinUiController.ResolveStartupRoute(new[] { left, renamed }, new[] { "device:AA" }) == null &&
            WinUiController.ResolveStartupRoute(available, new[] { "device:AA", "device:AA" }) == null,
            "Startup must reject ambiguous or repeated identities.");
        ReceiverGroup anonymous = Speaker("");
        passed &= Check(WinUiController.ResolveStartupRoute(new[] { anonymous },
            new[] { "device:" + anonymous.Members[0].Identity }) == null,
            "Startup must reject name/IP-only identities.");
        ReceiverGroup hosted = Speaker("");
        hosted.Members[0].Host = "Speaker.local";
        passed &= Check(WinUiController.ResolveStartupRoute(new[] { hosted }, new[] { "device:host:speaker.local" }) != null,
            "Startup must accept a stable advertised hostname.");
        passed &= Check(WinUiController.ResolveStartupRoute(new[] { native, left },
            new[] { "group:device:AA,device:BB", "device:AA" }) == null,
            "Startup must reject native pair plus an independent target.");
        checks = count;
        return passed;
    }

    private static bool VerifySilentStartup(out int checks)
    {
        checks = 0;
        if (!Verify(Program.IsStartupLaunch(new[] { "gui", "--tray" }) &&
            Program.IsStartupLaunch(new[] { "GUI", "--TRAY" }) &&
            !Program.IsStartupLaunch(Array.Empty<string>()) && !Program.IsStartupLaunch(new[] { "gui" }) &&
            !Program.IsStartupLaunch(new[] { "discover", "--tray" }),
            "Startup command detection must distinguish login/Store launches from manual opens.")) return false;
        checks++;
        if (!Verify(WinUiMainWindow.ShouldStartHidden(true, true, true) &&
            !WinUiMainWindow.ShouldStartHidden(false, true, true) &&
            !WinUiMainWindow.ShouldStartHidden(true, false, true) &&
            !WinUiMainWindow.ShouldStartHidden(true, true, false),
            "Manual opens and missing tray icons must remain visible; only opted-in startup is hidden.")) return false;
        checks++;
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AirStereo-silent-startup-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var store = new Ui.FaultStore(directory, directory);
        WinUiController Controller() => new(_ => { }, 0, true, Session.SilenceFrameMode.RepeatLast,
            isolatedTest: true, diagnosticsStore: store, isolatedSettingsDirectory: directory);
        try
        {
            using (var initial = Controller())
            {
                if (!Verify(!initial.SilentStartup, "Silent startup must default off for a fresh install.")) return false;
                initial.SetAutoConnect(true);
                initial.SetSilentStartup(true);
            }
            checks++;
            using (var restored = Controller())
            {
                if (!Verify(restored.SilentStartup && restored.AutoConnect, "Silent startup must persist independently of auto-connect.")) return false;
                restored.SetSilentStartup(false);
            }
            checks++;
            using (var disabled = Controller())
                if (!Verify(!disabled.SilentStartup && disabled.AutoConnect, "Disabling silent startup must persist without changing auto-connect.")) return false;
            checks++;
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "settings.txt"), "autoConnect=1\nvolume=60\n");
            using (var legacy = Controller())
                if (!Verify(!legacy.SilentStartup && legacy.AutoConnect && legacy.Volume == 60,
                    "Existing settings without the new key must retain previous startup and volume behavior.")) return false;
            checks++;
            return true;
        }
        finally
        {
            System.IO.File.Delete(System.IO.Path.Combine(directory, "settings.txt"));
            System.IO.Directory.Delete(directory);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("AirStereo WinUI self-test failed: " + message);
        return 1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    private static bool Verify(bool condition, string message)
    {
        if (!condition) Fail(message);
        return condition;
    }
}
