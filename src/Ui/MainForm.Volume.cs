using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using AirStereo.Protocol;

namespace AirStereo.Ui
{
    public sealed partial class MainForm
    {
        private CheckBox realtimeVolumeBox;
        private System.Windows.Forms.Timer volumeApplyTimer;
        // UI-thread-owned latest-value slot: no unbounded queue or overlapping RTSP commands.
        private ReceiverGroup pendingVolumeTarget;
        private string pendingVolumeKey;
        private int pendingVolumePercent;
        private bool pendingVolumeDuringPlayback;
        // Offline regression seam. Production always uses the existing SetVolume chain.
        internal Func<ReceiverGroup, int, Action<string>, List<string>> VolumeApplyOverride;

        private void InitializeVolumeAdjustment(TableLayoutPanel volume)
        {
            volumeApplyTimer = new System.Windows.Forms.Timer { Interval = 120 };
            volumeApplyTimer.Tick += delegate { FlushPendingVolume(); };
            realtimeVolumeBox = new FluentCheckBox
            {
                Text = "实时音量调整", Dock = DockStyle.Fill, AutoEllipsis = true,
                ForeColor = InkColor, Margin = new Padding(0),
                AccessibleName = "实时音量调整（默认关闭）"
            };
            uiTips.SetToolTip(realtimeVolumeBox,
                "关闭：拖动后点击右侧 ✓ 应用音量。开启：拖动、方向键或滚轮自动应用音量。");
            realtimeVolumeBox.CheckedChanged += delegate
            {
                ClearPendingVolume();
                UpdateButtons();
                if (!featureSettingsSyncing && !OfflinePreview) SaveSettings();
            };
            volume.RowCount = 2;
            volume.Controls.Add(realtimeVolumeBox, 0, 1);
            volume.SetColumnSpan(realtimeVolumeBox, 4);
        }

        private void LoadVolumeModeSetting(string value)
        {
            // Missing settings retain the original manual-apply default; malformed values are off.
            realtimeVolumeBox.Checked = value == "1";
        }

        private string VolumeModeSettingsLine() =>
            "realtimeVolume=" + (realtimeVolumeBox.Checked ? "1" : "0");

        private void OnVolumeValueChanged(object sender, EventArgs args)
        {
            volumeValue.Text = volumeBar.Value.ToString(CultureInfo.InvariantCulture) + "%";
            if (featureSettingsSyncing || realtimeVolumeBox?.Checked != true || exiting || scanning) return;
            ReceiverGroup target = PlaybackTarget();
            if (target == null) return;
            pendingVolumeTarget = target;
            pendingVolumeKey = SelectionKey(target);
            pendingVolumePercent = volumeBar.Value;
            pendingVolumeDuringPlayback = playing;
            // Throttle rather than debounce: a continuous drag still updates every 120 ms
            // when the previous command has completed, and always retains its final value.
            if (!volumeBusy && !volumeApplyTimer.Enabled) volumeApplyTimer.Start();
        }

        private void ClearPendingVolume()
        {
            volumeApplyTimer?.Stop();
            pendingVolumeTarget = null;
            pendingVolumeKey = null;
        }

        private void UpdateVolumeAdjustmentUi(bool hasTarget)
        {
            if (pendingVolumeTarget != null && (!hasTarget || scanning ||
                (pendingVolumeDuringPlayback && !playing) ||
                !string.Equals(pendingVolumeKey, SelectionKey(PlaybackTarget()), StringComparison.OrdinalIgnoreCase)))
                ClearPendingVolume();
            bool realtime = realtimeVolumeBox?.Checked == true;
            volumeButton.Enabled = hasTarget && !volumeBusy && !realtime;
            uiTips?.SetToolTip(volumeButton, realtime
                ? "实时音量调整已开启：拖动滑条自动应用，无需点击 ✓"
                : "应用音量到当前所选音响（拖动后点击 ✓）");
        }

        private void FlushPendingVolume()
        {
            volumeApplyTimer.Stop();
            ReceiverGroup target = PlaybackTarget();
            if (exiting || IsDisposed || realtimeVolumeBox.Checked != true || scanning || target == null ||
                pendingVolumeTarget == null || (pendingVolumeDuringPlayback && !playing) ||
                !string.Equals(pendingVolumeKey, SelectionKey(target), StringComparison.OrdinalIgnoreCase))
            {
                ClearPendingVolume();
                return;
            }
            if (volumeBusy) return; // Completion restarts the timer for the latest pending value.
            if (playing && !streamReady)
            {
                // Never create a temporary volume session while playback is connecting.
                volumeApplyTimer.Start();
                return;
            }
            ReceiverGroup capturedTarget = pendingVolumeTarget;
            int percent = pendingVolumePercent;
            ClearPendingVolume();
            StartVolumeAdjustment(capturedTarget, percent);
        }

        private void ApplyVolume()
        {
            if (volumeBusy || realtimeVolumeBox.Checked || exiting) return;
            ReceiverGroup group = PlaybackTarget();
            if (group == null)
            {
                if (!OfflinePreview) Log("先选好连接目标再调音量。");
                return;
            }
            ClearPendingVolume();
            StartVolumeAdjustment(group, volumeBar.Value);
        }

        private void StartVolumeAdjustment(ReceiverGroup group, int percent)
        {
            // Synthetic tests must explicitly provide a fake; never connect real speakers.
            if (OfflinePreview && VolumeApplyOverride == null) return;
            volumeBusy = true;
            UpdateButtons();
            var sender = OfflinePreview ? VolumeApplyOverride : null;
            Thread worker = new Thread(delegate ()
            {
                Exception failure = null;
                try
                {
                    List<string> results = sender != null
                        ? sender(group, percent, delegate { })
                        : AirStereoApi.SetVolume(group, percent, Srp.DefaultPin, Log);
                    if (!OfflinePreview)
                    {
                        foreach (string line in results) Log("音量 " + line);
                        Log("音量设置已发送：「" + TargetTitle(group) + "」" +
                            percent.ToString(CultureInfo.InvariantCulture) + "%。");
                    }
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    Post(delegate
                    {
                        if (IsDisposed) return;
                        volumeBusy = false;
                        if (failure != null && !OfflinePreview)
                        {
                            Log("设置音量失败：" + failure.Message);
                            RecordFault("音量设置失败", failure.Message, failure);
                        }
                        UpdateButtons();
                        if (pendingVolumeTarget != null && realtimeVolumeBox.Checked && !exiting)
                            volumeApplyTimer.Start();
                    });
                }
            });
            worker.IsBackground = true;
            worker.Name = "air-stereo-volume";
            worker.Start();
        }

        private void DisposeVolumeAdjustment()
        {
            ClearPendingVolume();
            volumeApplyTimer?.Dispose();
        }
    }
}
