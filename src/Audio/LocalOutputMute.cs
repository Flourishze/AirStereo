using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AirStereo.Audio
{
    internal interface IOutputMuteEndpoint : IDisposable
    {
        string Id { get; }
        bool Muted { get; set; }
    }
    internal interface IOutputMuteBackend
    {
        IOutputMuteEndpoint OpenDefault();
        IOutputMuteEndpoint Open(string id);
    }
    internal sealed class MuteRecoveryRecord
    {
        internal string EndpointId;
        internal bool WasMuted;
    }
    internal interface IMuteRecoveryStore
    {
        MuteRecoveryRecord Read();
        void Write(MuteRecoveryRecord record);
        void Clear();
    }
    // Write the original state BEFORE muting. A failed restore keeps the recovery record.
    internal sealed class LocalOutputMuteController
    {
        private readonly IOutputMuteBackend backend;
        private readonly IMuteRecoveryStore store;
        private readonly Action<string> log;
        private MuteRecoveryRecord owned;
        private IOutputMuteEndpoint ownedEndpoint;
        private string lastError;
        internal bool OwnsEndpoint => owned != null;
        internal LocalOutputMuteController(IOutputMuteBackend backend, IMuteRecoveryStore store, Action<string> log)
        { this.backend = backend; this.store = store; this.log = log; }
        private void Failure(Exception error)
        {
            if (lastError != error.Message) log("本机静音降级（不影响串流）：" + error.Message);
            lastError = error.Message;
        }
        internal bool Restore()
        {
            try
            {
                MuteRecoveryRecord record = owned ?? store.Read();
                if (record == null) return true;
                IOutputMuteEndpoint endpoint = ownedEndpoint;
                if (endpoint == null || !string.Equals(endpoint.Id, record.EndpointId, StringComparison.Ordinal))
                    endpoint = backend.Open(record.EndpointId);
                endpoint.Muted = record.WasMuted;
                store.Clear();
                owned = null;
                if (ReferenceEquals(endpoint, ownedEndpoint)) ownedEndpoint = null;
                endpoint.Dispose();
                lastError = null;
                log("本机输出已恢复原状态");
                return true;
            }
            catch (Exception error) { Failure(error); return false; }
        }
        internal void Begin()
        {
            if (owned != null) return;
            // Never overwrite an unresolved record from a previous process/session.
            if (!Restore()) return;
            IOutputMuteEndpoint endpoint = null;
            try
            {
                endpoint = backend.OpenDefault();
                MuteRecoveryRecord record = new MuteRecoveryRecord { EndpointId = endpoint.Id, WasMuted = endpoint.Muted };
                store.Write(record);
                owned = record;
                ownedEndpoint = endpoint;
                endpoint.Muted = true;
                if (!endpoint.Muted)
                    throw new InvalidOperationException("Windows did not confirm mute on the selected render endpoint");
                lastError = null;
                log("串流时静音本机输出：已启用（role=Console，endpointId=" + record.EndpointId +
                    "，readbackMuted=true，原静音=" + record.WasMuted + "）");
            }
            catch (Exception error)
            {
                Failure(error);
                if (ownedEndpoint == null && endpoint != null) endpoint.Dispose();
                Restore();
            }
        }
        // On a default-device switch, restore the ORIGINAL endpoint and abandon auto-mute
        // for this session. Otherwise keep the same endpoint object and force mute back on
        // if another process or the system volume UI has cleared it.
        internal bool CheckDefault()
        {
            if (owned == null) return true;
            try
            {
                using (IOutputMuteEndpoint endpoint = backend.OpenDefault())
                {
                    if (!string.Equals(endpoint.Id, owned.EndpointId, StringComparison.Ordinal))
                    {
                        log("默认音频端点已切换：本次会话停止本机自动静音，恢复原端点");
                        Restore();
                        return false;
                    }
                }
                if (ownedEndpoint != null && !ownedEndpoint.Muted)
                {
                    ownedEndpoint.Muted = true;
                    if (!ownedEndpoint.Muted)
                        throw new InvalidOperationException("Windows did not confirm re-mute on the selected render endpoint");
                    log("串流时静音本机输出：检测到外部解除静音，已重新静音");
                }
                return true;
            }
            catch (Exception error)
            {
                Failure(error);
                // Keep ownership on transient endpoint/readback failures; the next
                // 500 ms poll can retry without losing the user's original state.
                return true;
            }
        }
    }    internal sealed class FileMuteRecoveryStore : IMuteRecoveryStore
    {
        private readonly string path;
        internal FileMuteRecoveryStore(string path) { this.path = path; }
        public MuteRecoveryRecord Read()
        {
            if (!File.Exists(path)) return null;
            string[] fields = File.ReadAllLines(path);
            if (fields.Length != 2 || (fields[1] != "0" && fields[1] != "1"))
                throw new InvalidDataException("本机静音恢复记录无效，保留记录且不再自动静音");
            string id = Encoding.UTF8.GetString(Convert.FromBase64String(fields[0]));
            if (id.Length == 0) throw new InvalidDataException("本机静音恢复端点为空");
            return new MuteRecoveryRecord { EndpointId = id, WasMuted = fields[1] == "1" };
        }
        public void Write(MuteRecoveryRecord record)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(record.EndpointId)) + "\n" + (record.WasMuted ? "1" : "0") + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        public void Clear() { if (File.Exists(path)) File.Delete(path); }
    }
    internal sealed class WindowsOutputMuteBackend : IOutputMuteBackend
    {
        public IOutputMuteEndpoint OpenDefault() => OpenCore(null);
        public IOutputMuteEndpoint Open(string id) => OpenCore(id);
        private static IOutputMuteEndpoint OpenCore(string id)
        {
            AudioInterop.IMMDeviceEnumerator enumerator = null;
            AudioInterop.IMMDevice device = null;
            object instance = null;
            try
            {
                enumerator = AudioInterop.CreateEnumerator();
                Marshal.ThrowExceptionForHR(id == null
                    ? enumerator.GetDefaultAudioEndpoint(AudioInterop.EDATAFLOW_RENDER, AudioInterop.EROLE_CONSOLE, out device)
                    : enumerator.GetDevice(id, out device));
                Marshal.ThrowExceptionForHR(device.GetId(out string actual));
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, AudioInterop.CLSCTX_ALL, IntPtr.Zero, out instance));
                var endpoint = new Endpoint(actual, (IAudioEndpointVolume)instance);
                instance = null;
                return endpoint;
            }
            finally { AudioInterop.Release(instance); AudioInterop.Release(device); AudioInterop.Release(enumerator); }
        }
        private sealed class Endpoint : IOutputMuteEndpoint
        {
            private IAudioEndpointVolume volume;
            public string Id { get; }
            internal Endpoint(string id, IAudioEndpointVolume volume) { Id = id; this.volume = volume; }
            public bool Muted
            {
                get { Marshal.ThrowExceptionForHR(volume.GetMute(out bool muted)); return muted; }
                set { Guid context = Guid.Empty; Marshal.ThrowExceptionForHR(volume.SetMute(value, ref context)); }
            }
            public void Dispose() { AudioInterop.Release(volume); volume = null; }
        }
        // Exact Windows SDK endpointvolume.h vtable order through GetMute (BOOL is 4 bytes).
        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint channels);
            [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
    }
}
