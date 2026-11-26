using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace AirStereo2;

/// <summary>
/// Monitors the Windows default playback device while AirPlay streaming is active:
/// 1) Optionally keeps local PC output muted and re-mutes ~500 ms after system volume changes;
/// 2) Optionally syncs Windows system volume adjustments straight to AirStereo2 master volume.
/// </summary>
internal sealed class LocalOutputMuteGuard : IDisposable
{
    private const int ClsCtxAll = 23;
    private const int ERender = 0;
    private const int EMultimedia = 1;
    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid EventContext = new("2B8D6A4E-9C35-44E7-A8D5-6724A1E9C0B2");

    private readonly DispatcherQueueTimer pollTimer;
    private bool polling;
    private bool muteGuarding;
    private bool syncVolumeActive;
    private bool? preStreamMuted;
    private float lastVolumeScalar = -1f;
    private int lastSyncedPercent = -1;
    private DateTime? pendingRemuteAtUtc;
    private bool disposed;

    internal event Action<int>? SystemVolumeChanged;

    internal LocalOutputMuteGuard(DispatcherQueue queue)
    {
        pollTimer = queue.CreateTimer();
        pollTimer.Interval = TimeSpan.FromMilliseconds(120);
        pollTimer.IsRepeating = true;
        pollTimer.Tick += (_, _) => Poll();
    }

    internal void Update(bool muteEnabled, bool syncVolumeEnabled, bool isStreaming)
    {
        if (disposed) return;
        bool wantMute = muteEnabled && isStreaming;
        bool wantSync = syncVolumeEnabled && isStreaming;
        bool wantPoll = wantMute || wantSync;

        syncVolumeActive = wantSync;

        if (wantMute && !muteGuarding)
        {
            muteGuarding = true;
            pendingRemuteAtUtc = null;
            if (TryReadState(out bool currentlyMuted, out float volume))
            {
                preStreamMuted = currentlyMuted;
                lastVolumeScalar = volume;
                if (!currentlyMuted)
                    TrySetMute(true);
            }
            else
            {
                preStreamMuted = null;
            }
        }
        else if (!wantMute && muteGuarding)
        {
            ReleaseMute();
        }

        if (wantPoll && !polling)
        {
            polling = true;
            if (lastVolumeScalar < 0f && TryReadState(out _, out float volume))
                lastVolumeScalar = volume;
            lastSyncedPercent = lastVolumeScalar >= 0f ? (int)Math.Round(Math.Clamp(lastVolumeScalar, 0f, 1f) * 100f) : -1;
            pollTimer.Start();
        }
        else if (!wantPoll && polling)
        {
            pollTimer.Stop();
            polling = false;
            lastVolumeScalar = -1f;
            lastSyncedPercent = -1;
            pendingRemuteAtUtc = null;
        }
    }

    private void Poll()
    {
        if (disposed || !polling) return;
        if (!TryReadState(out bool isMuted, out float volume)) return;

        bool volumeMoved = lastVolumeScalar >= 0f && Math.Abs(volume - lastVolumeScalar) > 0.005f;
        if (volumeMoved)
        {
            lastVolumeScalar = volume;
            if (syncVolumeActive)
            {
                int percent = (int)Math.Round(Math.Clamp(volume, 0f, 1f) * 100f);
                if (percent != lastSyncedPercent)
                {
                    lastSyncedPercent = percent;
                    SystemVolumeChanged?.Invoke(percent);
                }
            }
            if (muteGuarding)
                pendingRemuteAtUtc = DateTime.UtcNow.AddMilliseconds(500);
            return;
        }
        lastVolumeScalar = volume;

        if (!muteGuarding) return;
        if (!isMuted)
        {
            if (pendingRemuteAtUtc == null)
            {
                pendingRemuteAtUtc = DateTime.UtcNow.AddMilliseconds(500);
            }
            else if (DateTime.UtcNow >= pendingRemuteAtUtc.Value)
            {
                TrySetMute(true);
                pendingRemuteAtUtc = null;
            }
        }
        else
        {
            pendingRemuteAtUtc = null;
        }
    }

    private void ReleaseMute()
    {
        muteGuarding = false;
        pendingRemuteAtUtc = null;
        if (preStreamMuted == false)
            TrySetMute(false);
        preStreamMuted = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pollTimer.Stop();
        polling = false;
        syncVolumeActive = false;
        if (muteGuarding) ReleaseMute();
        lastVolumeScalar = -1f;
        lastSyncedPercent = -1;
        SystemVolumeChanged = null;
    }

    private static bool TryReadState(out bool muted, out float volume)
    {
        muted = false;
        volume = -1f;
        object? enumeratorObj = null;
        IMMDevice? device = null;
        object? volumeObj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator);
            if (type == null) return false;
            enumeratorObj = Activator.CreateInstance(type);
            if (enumeratorObj is not IMMDeviceEnumerator enumerator) return false;
            if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device) != 0 || device == null) return false;
            Guid iid = IidAudioEndpointVolume;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out volumeObj) != 0 || volumeObj is not IAudioEndpointVolume ep) return false;
            if (ep.GetMute(out bool m) != 0) return false;
            if (ep.GetMasterVolumeLevelScalar(out float v) != 0) return false;
            muted = m;
            volume = v;
            return true;
        }
        catch { return false; }
        finally
        {
            if (volumeObj != null) Marshal.FinalReleaseComObject(volumeObj);
            if (device != null) Marshal.FinalReleaseComObject(device);
            if (enumeratorObj != null) Marshal.FinalReleaseComObject(enumeratorObj);
        }
    }

    private static bool TrySetMute(bool mute)
    {
        object? enumeratorObj = null;
        IMMDevice? device = null;
        object? volumeObj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator);
            if (type == null) return false;
            enumeratorObj = Activator.CreateInstance(type);
            if (enumeratorObj is not IMMDeviceEnumerator enumerator) return false;
            if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device) != 0 || device == null) return false;
            Guid iid = IidAudioEndpointVolume;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out volumeObj) != 0 || volumeObj is not IAudioEndpointVolume ep) return false;
            Guid ctx = EventContext;
            return ep.SetMute(mute, ref ctx) == 0;
        }
        catch { return false; }
        finally
        {
            if (volumeObj != null) Marshal.FinalReleaseComObject(volumeObj);
            if (device != null) Marshal.FinalReleaseComObject(device);
            if (enumeratorObj != null) Marshal.FinalReleaseComObject(enumeratorObj);
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }
}
