using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AirStereo.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirStereo;

/// <summary>Isolated, repeatable UI allocation benchmark; no receiver connection or preferences writes.</summary>
internal static class WinUiMemoryTest
{
    private static int result = 1;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> samples = new();
    private static bool expectOptimization;
    private static void Trace(string stage) => System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "memory-test-trace.txt"), stage + Environment.NewLine);
    internal static int Run(bool verifyOptimization = false)
    {
        expectOptimization = verifyOptimization;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(queueController =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new WinUiApplication(false, 0, true, Session.SilenceFrameMode.RepeatLast, memorySelfTest: true);
        });
        return result;
    }

    internal static async void RunOnUiThread()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AirStereo-memory-" + Guid.NewGuid().ToString("N"));
        WinUiMainWindow? main = null;
        WinUiSettingsWindow? settings = null;
        try
        {
            Trace("fixture");
            var store = new FaultStore(directory, directory);
            for (int i = 0; i < 240; i++) store.Activity("Synthetic diagnostic " + i + " " + new string('x', 180));
            for (int i = 0; i < 24; i++) store.Record("Synthetic", "Record " + i, "", "Offline fixture");
            var controller = new WinUiController(action => DispatcherQueue.GetForCurrentThread().TryEnqueue(() => action()), 0,
                true, Session.SilenceFrameMode.RepeatLast, isolatedTest: true, diagnosticsStore: store);
            var groups = (IList)Get(controller, "groups");
            for (int i = 0; i < 6; i++) groups.Add(new ReceiverGroup { Name = "Offline speaker " + i,
                Members = new List<Receiver> { new() { ServiceDeviceId = "TEST" + i, Instance = "Test", Address = "192.0.2." + (i + 1), Port = 7000 } } });
            main = new WinUiMainWindow(false, 0, true, Session.SilenceFrameMode.RepeatLast, controller);
            Trace("main");
            ((DispatcherQueueTimer)Get(main, "localMuteTimer")).Stop();
            // Exercise the real hidden-window path without network discovery.
            // An already-scanning isolated controller makes StartScan a no-op.
            controller.SetSilentStartup(true);
            Set(controller, "<Scanning>k__BackingField", true);
            main.InitializeLaunch(startupLaunch: true);
            if (main.AppWindow.IsVisible || (bool)Get(main, "popupVisible") || !(bool)Get(main, "startupScanStarted"))
                throw new InvalidOperationException("Silent startup must stay hidden and initialize startup scanning.");
            Set(controller, "<Scanning>k__BackingField", false);
            Set(main, "popupVisible", true);
            Invoke(main, "RefreshOnUiThread");
            var view = (WinUiMainView)Get(main, "view");
            object firstRow = view.DeviceList.Children[0];
            Measure("visible_500_refreshes", () => { for (int i = 0; i < 500; i++) Invoke(main, "RefreshOnUiThread"); });
            bool rowReused = ReferenceEquals(firstRow, view.DeviceList.Children[0]);
            Trace("visible complete");
            Set(main, "popupVisible", false);
            Measure("hidden_500_refreshes", () => { for (int i = 0; i < 500; i++) Invoke(main, "RefreshOnUiThread"); });

            settings = new WinUiSettingsWindow(controller);
            Trace("settings");
            var settingsView = (WinUiSettingsView)Get(settings, "view");
            await MeasureAsync("general_100_notices", () => { for (int i = 0; i < 100; i++) Invoke(controller, "Log", "Offline notice " + i); });
            settingsView.Navigation.SelectedItem = settingsView.Navigation.MenuItems.Cast<NavigationViewItem>().First(item => (string)item.Tag == "notice");
            await Task.Delay(300);
            Trace("logs");
            await MeasureAsync("logs_100_notices", () => { for (int i = 0; i < 100; i++) Invoke(controller, "Log", "Offline log " + i); });
            bool latestLogVisible = settingsView.RecentActivityText.Text.Contains("Offline log 99", StringComparison.Ordinal);
            settingsView.Navigation.SelectedItem = settingsView.Navigation.MenuItems.Cast<NavigationViewItem>().First(item => (string)item.Tag == "diagnostics");
            await Task.Delay(400);
            await MeasureAsync("faults_100_notices", () => { for (int i = 0; i < 100; i++) Invoke(controller, "Log", "Offline fault-page notice " + i); });
            bool faultsVisible = settingsView.DiagnosticsText.Text.Contains("Record ", StringComparison.Ordinal);
            Trace("faults complete");
            var weakWindows = new List<WeakReference>();
            settings.Close(); settings = null;
            await Task.Delay(100);
            long openingAllocations = GC.GetTotalAllocatedBytes(true);
            for (int i = 0; i < 12; i++)
            {
                weakWindows.Add(OpenAndCloseSettings(controller));
                await Task.Delay(80);
            }
            Sample("12_settings_open_close", GC.GetTotalAllocatedBytes(true) - openingAllocations);
            await Task.Delay(300);
            // Diagnostic-only reachability check, outside allocation measurements.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            // WinRT release notifications drain on the UI queue after finalizers.
            await Task.Delay(200);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int retained = weakWindows.Count(reference => reference.IsAlive);
            Sample("settled_after_close", 0);
            if (expectOptimization)
            {
                if (!rowReused || retained != 0) throw new InvalidOperationException($"DeviceRowReused={rowReused}; ClosedWindowsRetained={retained}.");
                CheckRowLifecycle(main, controller, groups, view);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { Samples = samples, DeviceRowReused = rowReused,
                LatestLogVisible = latestLogVisible, FaultsVisible = faultsVisible, ClosedWindowsRetained = retained,
                Scope = "Offline XAML windows, synthetic six-device list and local diagnostics. No playback or visible-window rendering." }, new JsonSerializerOptions { WriteIndented = true }));
            if (!latestLogVisible || !faultsVisible) throw new InvalidOperationException("Diagnostic page content did not refresh.");
            result = 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); }
        finally
        {
            settings?.Close();
            if (main != null) Invoke(main, "ExitApplication");
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
            Application.Current.Exit();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndCloseSettings(WinUiController controller)
    {
        var window = new WinUiSettingsWindow(controller);
        var reference = new WeakReference(window);
        window.Close();
        return reference;
    }
    private static void Measure(string stage, Action action)
    {
        long allocated = GC.GetTotalAllocatedBytes(true);
        action();
        Sample(stage, GC.GetTotalAllocatedBytes(true) - allocated);
    }
    private static async Task MeasureAsync(string stage, Action action)
    {
        long allocated = GC.GetTotalAllocatedBytes(true);
        action();
        // Include the deferred, coalesced UI work in both versions' totals.
        await Task.Delay(300);
        Sample(stage, GC.GetTotalAllocatedBytes(true) - allocated);
    }
    private static void CheckRowLifecycle(WinUiMainWindow main, WinUiController controller, IList groups, WinUiMainView view)
    {
        Set(main, "popupVisible", true);
        Invoke(main, "RefreshOnUiThread");
        object card = view.DeviceList.Children[0];
        var first = (ReceiverGroup)groups[0]!;
        var second = (ReceiverGroup)groups[1]!;
        controller.Select(first, true);
        controller.Select(second, true);
        Invoke(main, "RefreshOnUiThread");
        if (!ReferenceEquals(card, view.DeviceList.Children[0]) || controller.Role(first) != "L" || controller.Role(second) != "R")
            throw new InvalidOperationException("Selection must reuse cards and preserve L/R selection order.");
        var renamed = new ReceiverGroup { Name = "Renamed offline speaker", Members = first.Members };
        groups[0] = renamed;
        Invoke(main, "RefreshOnUiThread");
        var text = (StackPanel)((Grid)((Border)view.DeviceList.Children[0]).Child).Children[2];
        if (!ReferenceEquals(card, view.DeviceList.Children[0]) || ((TextBlock)text.Children[0]).Text != renamed.Name || !controller.IsSelected(renamed))
            throw new InvalidOperationException("Refresh must reuse stable IDs, update the label and retain selection.");
        groups.RemoveAt(0);
        Invoke(main, "RefreshOnUiThread");
        if (view.DeviceList.Children.Count != groups.Count || ((IDictionary)Get(main, "deviceRows")).Count != groups.Count)
            throw new InvalidOperationException("Removed devices must be released from the visual cache.");
        Set(main, "popupVisible", false);
        object remaining = view.DeviceList.Children[0];
        groups.Clear();
        Invoke(main, "RefreshOnUiThread");
        if (!ReferenceEquals(remaining, view.DeviceList.Children[0])) throw new InvalidOperationException("Hidden popup performed visual work.");
        Set(main, "popupVisible", true);
        Invoke(main, "RefreshOnUiThread");
        if (view.DeviceList.Children.Count != 1 || ((IDictionary)Get(main, "deviceRows")).Count != 0)
            throw new InvalidOperationException("Reopening must catch up with discovery changes.");
        Console.Error.WriteLine("Memory UI regression passed: row reuse, stable-ID rename/selection, L/R, cache removal, hidden/reopen catch-up and closed-window collection.");
    }
    private static void Sample(string stage, long allocated)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        samples.Add(new { Stage = stage, AllocatedBytes = allocated, WorkingSetBytes = process.WorkingSet64,
            PrivateBytes = process.PrivateMemorySize64, ManagedBytes = GC.GetTotalMemory(false), Gen0 = GC.CollectionCount(0), Gen2 = GC.CollectionCount(2) });
    }
    private static object Get(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
}
