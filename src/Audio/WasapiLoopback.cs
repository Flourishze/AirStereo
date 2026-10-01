using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace AirStereo.Audio
{
    /// <summary>
    /// The subset of the WASAPI COM surface this sender needs: enumerate render endpoints,
    /// open the default one in shared loopback mode, and read the system mix as float.
    /// </summary>
    internal static class AudioInterop
    {
        public const int EDATAFLOW_RENDER = 0;
        public const int EROLE_CONSOLE = 0;
        public const int DEVICE_STATE_ACTIVE = 0x1;
        public const int CLSCTX_ALL = 0x17;

        public const int SHAREMODE_SHARED = 0;
        public const int STREAMFLAGS_LOOPBACK = 0x00020000;
        public const int STREAMFLAGS_EVENTCALLBACK = 0x00040000;

        public const int BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
        public const int BUFFERFLAGS_SILENT = 0x2;

        public const ushort FORMAT_PCM = 1;
        public const ushort FORMAT_IEEE_FLOAT = 3;
        public const ushort FORMAT_EXTENSIBLE = 0xFFFE;

        public static readonly Guid ClassEnumerator =
            new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WaveFormatEx
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSecond;
            public uint AverageBytesPerSecond;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort ExtraSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WaveFormatExtensible
        {
            public WaveFormatEx Format;
            public ushort ValidBitsPerSample;
            public uint ChannelMask;
            public Guid SubFormat;
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid interfaceId, int classContext, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;

            public PropertyKey(Guid formatId, uint propertyId)
            {
                FormatId = formatId;
                PropertyId = propertyId;
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct PropVariant
        {
            [FieldOffset(0)] public ushort ValueType;
            [FieldOffset(8)] public IntPtr PointerValue;
            [FieldOffset(8)] public int Int32Value;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags,
                long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid interfaceId,
                [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out int flags,
                out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant value);

        public static readonly PropertyKey FriendlyName =
            new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

        public static IMMDeviceEnumerator CreateEnumerator()
        {
            Type type = Type.GetTypeFromCLSID(ClassEnumerator, true);
            object instance = Activator.CreateInstance(type);
            IMMDeviceEnumerator enumerator = instance as IMMDeviceEnumerator;
            if (enumerator == null)
            {
                throw new InvalidOperationException("the WASAPI device enumerator is unavailable");
            }
            return enumerator;
        }

        public static string DeviceName(IMMDevice device)
        {
            IPropertyStore store = null;
            try
            {
                if (device.OpenPropertyStore(0, out store) < 0) return null;
                PropertyKey key = FriendlyName;
                PropVariant value;
                if (store.GetValue(ref key, out value) < 0) return null;
                try
                {
                    return value.ValueType == 31 ? Marshal.PtrToStringUni(value.PointerValue) : null;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                Release(store);
            }
        }

        public static void Release(object comObject)
        {
            if (comObject == null) return;
            try
            {
                if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
            }
            catch (Exception)
            {
                // reference counting is best effort during teardown
            }
        }
    }

    /// <summary>
    /// Captures whatever Windows is playing on a render endpoint through WASAPI loopback and
    /// presents it as interleaved 16 bit stereo at the stream sample rate.
    /// </summary>
    public sealed class LoopbackSource : AudioSource, IDisposable
    {
        private readonly string deviceName;
        private readonly int captureRate;
        private readonly int captureChannels;
        private readonly bool captureIsFloat;
        private readonly int captureBytesPerSample;
        private readonly SincResampler resampler;
        private readonly Queue<short> output = new Queue<short>();
        /// <summary>Capture timestamp of every frame in <see cref="output"/>, oldest first.</summary>
        private readonly Queue<long> stamps = new Queue<long>();
        /// <summary>Age of the freshest frame of the last few thousand blocks, in milliseconds.</summary>
        private readonly Queue<double> ages = new Queue<double>();
        private float[] staging = new float[0];
        /// <summary>Queue depth the capture path aims to hold, in output frames.</summary>
        private readonly long targetFrames;

        private object enumeratorObject;
        private object deviceObject;
        private object clientObject;
        private object captureObject;
        private AudioInterop.IAudioClient client;
        private AudioInterop.IAudioCaptureClient capture;
        private AutoResetEvent bufferEvent;
        /// <summary>Signalled by the capture pump as soon as it has queued audio.</summary>
        private AutoResetEvent dataReady;
        private Thread worker;
        private volatile bool stopping;
        private long capturedFrames;
        private long silentPackets;
        /// <summary>Reads that found the queue empty: the sender was about to run dry.</summary>
        private long starvedReads;
        /// <summary>Milliseconds spent waiting for the device inside Read.</summary>
        private double waitedMilliseconds;
        private double worstWaitMilliseconds;
        private long queueCeiling;
        /// <summary>Frames the endpoint never handed over, judged from its position counter.</summary>
        private long lostFrames;
        private long lastDevicePosition = -1;
        /// <summary>Rate trims applied by the drift loop, in parts per million.</summary>
        private double trimPpm;
        private double maxAgeMilliseconds;
        private string lastError;

        /// <summary>How many block ages the p95 is taken over: about half a minute of audio.</summary>
        private const int AgeWindow = 4000;
        /// <summary>The queue depth the capture path holds; the reference engine uses 20 ms.</summary>
        private const double TargetQueueMilliseconds = 20.0;
        /// <summary>Above this the depth stops being a cushion and the drift loop drains it.</summary>
        private const double QueueCeilingMilliseconds = 40.0;
        private const double QueueFloorMilliseconds = 10.0;

        public sealed class Description
        {
            public string Name;
            public int SampleRate;
            public int Channels;
            public bool IsFloat;
        }

        public static Description DescribeDefault()
        {
            AudioInterop.IMMDeviceEnumerator enumerator = AudioInterop.CreateEnumerator();
            object deviceObject = null;
            object clientObject = null;
            try
            {
                AudioInterop.IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(
                    AudioInterop.EDATAFLOW_RENDER, AudioInterop.EROLE_CONSOLE, out device) < 0)
                {
                    throw new InvalidOperationException("no default playback device is available");
                }
                deviceObject = device;

                Guid clientId = typeof(AudioInterop.IAudioClient).GUID;
                object instance;
                if (device.Activate(ref clientId, AudioInterop.CLSCTX_ALL, IntPtr.Zero, out instance) < 0)
                {
                    throw new InvalidOperationException("the audio engine refused an audio client");
                }
                clientObject = instance;

                IntPtr formatPointer;
                if (((AudioInterop.IAudioClient)instance).GetMixFormat(out formatPointer) < 0 ||
                    formatPointer == IntPtr.Zero)
                {
                    throw new InvalidOperationException("the audio engine did not report a mix format");
                }
                try
                {
                    Description description = new Description();
                    description.Name = AudioInterop.DeviceName(device) ?? "default playback device";
                    ParseFormat(formatPointer, out description.SampleRate, out description.Channels,
                        out description.IsFloat, out int unused);
                    return description;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(formatPointer);
                }
            }
            finally
            {
                AudioInterop.Release(clientObject);
                AudioInterop.Release(deviceObject);
                AudioInterop.Release(enumerator);
            }
        }

        /// <summary>Reads the fields of a WAVEFORMATEX that may actually be WAVEFORMATEXTENSIBLE.</summary>
        private static void ParseFormat(IntPtr pointer, out int rate, out int channels,
            out bool isFloat, out int bytesPerSample)
        {
            AudioInterop.WaveFormatEx format =
                (AudioInterop.WaveFormatEx)Marshal.PtrToStructure(pointer, typeof(AudioInterop.WaveFormatEx));
            rate = (int)format.SamplesPerSecond;
            channels = format.Channels;
            isFloat = format.FormatTag == AudioInterop.FORMAT_IEEE_FLOAT;
            if (format.FormatTag == AudioInterop.FORMAT_EXTENSIBLE && format.ExtraSize >= 22)
            {
                AudioInterop.WaveFormatExtensible extensible =
                    (AudioInterop.WaveFormatExtensible)Marshal.PtrToStructure(
                        pointer, typeof(AudioInterop.WaveFormatExtensible));
                isFloat = extensible.SubFormat == new Guid("00000003-0000-0010-8000-00AA00389B71");
            }
            bytesPerSample = format.BitsPerSample / 8;
        }

        public static List<string> ListDevices()
        {
            List<string> names = new List<string>();
            AudioInterop.IMMDeviceEnumerator enumerator = AudioInterop.CreateEnumerator();
            object defaultDevice = null;
            string defaultId = null;
            try
            {
                AudioInterop.IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(
                    AudioInterop.EDATAFLOW_RENDER, AudioInterop.EROLE_CONSOLE, out device) >= 0)
                {
                    defaultDevice = device;
                    device.GetId(out defaultId);
                }

                AudioInterop.IMMDeviceCollection collection;
                if (enumerator.EnumAudioEndpoints(AudioInterop.EDATAFLOW_RENDER,
                    AudioInterop.DEVICE_STATE_ACTIVE, out collection) < 0)
                {
                    return names;
                }
                int count;
                collection.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    AudioInterop.IMMDevice candidate;
                    if (collection.Item(i, out candidate) < 0) continue;
                    string id;
                    candidate.GetId(out id);
                    string name = AudioInterop.DeviceName(candidate) ?? "(unnamed endpoint)";
                    names.Add(name + (id == defaultId ? "  [default]" : ""));
                    AudioInterop.Release(candidate);
                }
                AudioInterop.Release(collection);
            }
            finally
            {
                AudioInterop.Release(defaultDevice);
                AudioInterop.Release(enumerator);
            }
            return names;
        }

        public LoopbackSource(int sampleRate)
        {
            SampleRate = sampleRate;

            AudioInterop.IMMDeviceEnumerator enumerator = AudioInterop.CreateEnumerator();
            enumeratorObject = enumerator;

            AudioInterop.IMMDevice device;
            int hr = enumerator.GetDefaultAudioEndpoint(
                AudioInterop.EDATAFLOW_RENDER, AudioInterop.EROLE_CONSOLE, out device);
            if (hr < 0) throw new InvalidOperationException("no default playback device is available");
            deviceObject = device;
            deviceName = AudioInterop.DeviceName(device) ?? "default playback device";

            Guid clientId = typeof(AudioInterop.IAudioClient).GUID;
            object instance;
            hr = device.Activate(ref clientId, AudioInterop.CLSCTX_ALL, IntPtr.Zero, out instance);
            if (hr < 0) throw new InvalidOperationException("the audio engine refused an audio client");
            clientObject = instance;
            client = (AudioInterop.IAudioClient)instance;

            IntPtr formatPointer;
            hr = client.GetMixFormat(out formatPointer);
            if (hr < 0 || formatPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("the audio engine did not report a mix format");
            }

            try
            {
                ParseFormat(formatPointer, out captureRate, out captureChannels,
                    out captureIsFloat, out captureBytesPerSample);

                bufferEvent = new AutoResetEvent(false);
                dataReady = new AutoResetEvent(false);
                // The event callback flag is only accepted by Initialize, and the handle can
                // only be handed over once initialization has succeeded.
                hr = client.Initialize(AudioInterop.SHAREMODE_SHARED,
                    AudioInterop.STREAMFLAGS_LOOPBACK | AudioInterop.STREAMFLAGS_EVENTCALLBACK,
                    2000000, 0, formatPointer, IntPtr.Zero);
                if (hr < 0)
                {
                    throw new InvalidOperationException(
                        "loopback capture could not be started (0x" + hr.ToString("x8") + ")");
                }

                hr = client.SetEventHandle(bufferEvent.SafeWaitHandle.DangerousGetHandle());
                if (hr < 0)
                {
                    throw new InvalidOperationException(
                        "the audio engine rejected the buffer event (0x" + hr.ToString("x8") + ")");
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(formatPointer);
            }

            resampler = new SincResampler(captureRate, sampleRate);
            targetFrames = (long)Math.Round(TargetQueueMilliseconds * sampleRate / 1000.0);

            Guid captureId = typeof(AudioInterop.IAudioCaptureClient).GUID;
            object captureInstance;
            hr = client.GetService(ref captureId, out captureInstance);
            if (hr < 0) throw new InvalidOperationException("the audio engine exposed no capture client");
            captureObject = captureInstance;
            capture = (AudioInterop.IAudioCaptureClient)captureInstance;

            hr = client.Start();
            if (hr < 0) throw new InvalidOperationException("the audio engine refused to start capture");

            worker = new Thread(Pump) { IsBackground = true, Name = "AirStereo loopback" };
            worker.Start();
        }

        public override int SampleRate { get; }

        public string DeviceName { get { return deviceName; } }
        public int CaptureRate { get { return captureRate; } }
        public int CaptureChannels { get { return captureChannels; } }
        public long CapturedFrames { get { return Interlocked.Read(ref capturedFrames); } }
        public long SilentPackets { get { return Interlocked.Read(ref silentPackets); } }
        public string LastError { get { return lastError; } }

        /// <summary>Reads that arrived at an empty queue: each one is a sender side stall.</summary>
        public long StarvedReads { get { return Interlocked.Read(ref starvedReads); } }
        /// <summary>Frames the endpoint lost before this client saw them.</summary>
        public long LostFrames { get { return Interlocked.Read(ref lostFrames); } }
        public double WorstWaitMilliseconds { get { return worstWaitMilliseconds; } }
        /// <summary>The deepest the queue has been, in milliseconds of audio.</summary>
        public double PeakQueueMilliseconds { get { return queueCeiling * 500.0 / SampleRate; } }
        public double TrimPpm { get { return trimPpm; } }
        /// <summary>95th percentile of the age of the audio handed to the sender, in milliseconds.</summary>
        public double AgeP95Milliseconds
        {
            get
            {
                lock (output)
                {
                    if (ages.Count == 0) return 0.0;
                    double[] sorted = ages.ToArray();
                    Array.Sort(sorted);
                    return sorted[(sorted.Length - 1) * 95 / 100];
                }
            }
        }
        /// <summary>The oldest audio the sender has been handed, in milliseconds.</summary>
        public double WorstAgeMilliseconds { get { return maxAgeMilliseconds; } }

        /// <summary>Queue depth and capture health, in the shape the stream log wants it.</summary>
        public override string Stats()
        {
            string text = "queue " + (output.Count / 2 * 1000.0 / SampleRate).ToString("0.0",
                System.Globalization.CultureInfo.InvariantCulture) + " ms, " +
                "age " + AgeP95Milliseconds.ToString("0.0",
                    System.Globalization.CultureInfo.InvariantCulture) +
                "/" + WorstAgeMilliseconds.ToString("0.0",
                    System.Globalization.CultureInfo.InvariantCulture) + " ms, " +
                "peak " + PeakQueueMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
                " ms, starved " + StarvedReads +
                ", worst wait " + WorstWaitMilliseconds.ToString("0.0",
                    System.Globalization.CultureInfo.InvariantCulture) + " ms";
            if (LostFrames > 0) text += ", endpoint lost " + LostFrames + " frames";
            if (Math.Abs(TrimPpm) >= 1.0)
            {
                text += ", trim " + TrimPpm.ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
                    " ppm";
            }
            return text;
        }

        /// <summary>
        /// Brings the capture queue to its working depth before the first packet goes out.
        /// The endpoint keeps filling while the handshake runs, so the queue can hold a second
        /// of audio by the time streaming starts: that audio is stale, and sending it would
        /// only add a second of delay the receivers cannot see, so it is dropped.
        /// </summary>
        public override void Prepare()
        {
            lock (output)
            {
                int keep = (int)(targetFrames * 2);
                while (output.Count > keep)
                {
                    output.Dequeue();
                    output.Dequeue();
                    stamps.Dequeue();
                }
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(500);
            while (!stopping && DateTime.UtcNow < deadline)
            {
                lock (output)
                {
                    if (output.Count >= targetFrames * 2) break;
                }
                dataReady.WaitOne(5);
            }
        }

        /// <summary>Blocks until the requested number of frames has been captured and converted.</summary>
        public override int Read(short[] buffer, int frames)
        {
            int needed = frames * 2;
            int written = 0;
            bool starved = false;
            long waitingSince = 0;
            long newestStamp = 0;
            while (written < needed && !stopping)
            {
                lock (output)
                {
                    while (written < needed && output.Count > 0)
                    {
                        newestStamp = stamps.Dequeue();
                        buffer[written++] = output.Dequeue();
                        buffer[written++] = output.Dequeue();
                    }
                    if (written < needed && output.Count == 0 && lastError != null) break;
                    if (written < needed && output.Count == 0 && !starved)
                    {
                        starved = true;
                        waitingSince = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    // The freshest frame of the block is the one that says how old the audio we
                    // are about to send is; the ones before it only differ by the block itself.
                    if (newestStamp > 0)
                    {
                        RecordAge(System.Diagnostics.Stopwatch.GetTimestamp() - newestStamp);
                        newestStamp = 0;
                    }
                }
                if (written < needed)
                {
                    // Waking on the pump's signal beats polling Thread.Sleep(1): the sleep is
                    // bound to the 15.6 ms system tick, and at a small buffer that delay is the
                    // whole margin.
                    dataReady.WaitOne(2);
                }
            }
            if (starved)
            {
                Interlocked.Increment(ref starvedReads);
                double waited = (System.Diagnostics.Stopwatch.GetTimestamp() - waitingSince) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency;
                lock (output)
                {
                    waitedMilliseconds += waited;
                    if (waited > worstWaitMilliseconds) worstWaitMilliseconds = waited;
                }
            }
            for (int i = written; i < needed; i++) buffer[i] = 0;
            return frames;
        }

        private void Pump()
        {
            AudioThread.Raise();
            try
            {
                while (!stopping)
                {
                    uint available;
                    if (capture.GetNextPacketSize(out available) < 0) break;
                    if (available == 0)
                    {
                        bufferEvent.WaitOne(20);
                        continue;
                    }

                    IntPtr data;
                    uint frames;
                    int flags;
                    ulong devicePosition;
                    ulong qpcPosition;
                    if (capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpcPosition) < 0)
                    {
                        break;
                    }
                    if (frames == 0)
                    {
                        capture.ReleaseBuffer(frames);
                        continue;
                    }

                    try
                    {
                        CountLostFrames(devicePosition, frames);
                        if ((flags & AudioInterop.BUFFERFLAGS_SILENT) != 0)
                        {
                            silentPackets++;
                            Convert(frames, IntPtr.Zero, 0);
                        }
                        else
                        {
                            Convert(frames, data, captureChannels * captureBytesPerSample);
                        }
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                    Interlocked.Add(ref capturedFrames, frames);
                }
            }
            catch (Exception error)
            {
                lastError = error.GetType().Name + ": " + error.Message;
            }
        }

        /// <summary>
        /// WASAPI loopback counts frames for the endpoint, so a jump larger than the packet
        /// just read means the client did not collect everything the device produced. That is
        /// invisible from the audio itself - a lost chunk simply never existed as far as this
        /// process is concerned - and it is the one capture failure the receivers cannot hide.
        /// </summary>
        private void CountLostFrames(ulong devicePosition, uint frames)
        {
            long position = (long)devicePosition;
            if (lastDevicePosition >= 0)
            {
                long expected = lastDevicePosition + frames;
                long gap = position - expected;
                if (gap > 0 && gap < (long)captureRate * 5)
                {
                    Interlocked.Add(ref lostFrames, gap);
                }
            }
            lastDevicePosition = position;
        }

        /// <summary>Turns one captured packet into output rate stereo 16 bit frames.</summary>
        private void Convert(uint frames, IntPtr data, int stride)
        {
            bool silence = data == IntPtr.Zero;
            int count = (int)frames;
            if (staging.Length < count * 2) staging = new float[count * 2];
            for (int frame = 0; frame < count; frame++)
            {
                float left;
                float right;
                ReadFrame(silence ? IntPtr.Zero : data, (uint)frame, stride, out left, out right);
                staging[frame * 2] = left;
                staging[frame * 2 + 1] = right;
            }

            resampler.Push(staging, count);
            float[] produced = resampler.Produced;
            int producedFrames = resampler.ProducedFrames;

            long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            // One timestamp per frame, walked backwards from the newest: the queue then knows
            // how long each frame has been waiting, which is what the age p95 is taken from.
            long frameTicks = Math.Max(1L, System.Diagnostics.Stopwatch.Frequency / SampleRate);
            long depth;
            lock (output)
            {
                for (int i = 0; i < producedFrames; i++)
                {
                    stamps.Enqueue(stamp - (producedFrames - 1 - i) * frameTicks);
                    output.Enqueue(ToSample(produced[i * 2]));
                    output.Enqueue(ToSample(produced[i * 2 + 1]));
                }
                depth = output.Count;
                if (depth > queueCeiling) queueCeiling = depth;
            }
            Steer(depth / 2);
            // Convert runs once per captured packet, so this is also the natural heartbeat for
            // a reader that is waiting for the next block.
            dataReady.Set();
        }

        /// <summary>
        /// Holds the queue depth inside a band instead of at a point. Inside the band nothing
        /// happens at all, which is the important part: a controller that always corrects would
        /// leave a permanent fraction of a percent of pitch shift on the music. Outside it the
        /// resampler ratio is trimmed by up to 1500 ppm - inaudible while it works, and enough
        /// to walk a few milliseconds of drift back within a minute.
        /// </summary>
        private void Steer(long queuedFrames)
        {
            double floor = QueueFloorMilliseconds * SampleRate / 1000.0;
            double ceiling = QueueCeilingMilliseconds * SampleRate / 1000.0;
            double span = TargetQueueMilliseconds * SampleRate / 1000.0;
            double trim;
            if (queuedFrames > ceiling) trim = (queuedFrames - ceiling) / span * 0.05;
            else if (queuedFrames < floor) trim = -(floor - queuedFrames) / span * 0.05;
            else trim = 0.0;
            trimPpm = trim * 1e6;
            resampler.SetTrim(trim);
        }

        /// <summary>
        /// Records how long the frame just handed over waited in the capture queue. This is the
        /// one number that says whether the local pipeline is tight: audio that leaves 20 ms
        /// after it was played by the desktop keeps up with the music, while audio that leaves
        /// 200 ms late is being held back, and no buffer setting on the receiver can hide that.
        /// Callers hold <see cref="output"/>.
        /// </summary>
        private void RecordAge(long ticks)
        {
            if (ticks < 0) ticks = 0;
            double milliseconds = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            ages.Enqueue(milliseconds);
            while (ages.Count > AgeWindow) ages.Dequeue();
            if (milliseconds > maxAgeMilliseconds) maxAgeMilliseconds = milliseconds;
        }

        private void ReadFrame(IntPtr data, uint frame, int stride, out float left, out float right)
        {
            left = 0f;
            right = 0f;
            if (data == IntPtr.Zero) return;

            IntPtr start = IntPtr.Add(data, (int)frame * stride);
            if (captureIsFloat)
            {
                left = Marshal.PtrToStructure<float>(start);
                if (captureChannels > 1) right = Marshal.PtrToStructure<float>(IntPtr.Add(start, 4));
            }
            else if (captureBytesPerSample == 2)
            {
                left = Marshal.PtrToStructure<short>(start) / 32768f;
                if (captureChannels > 1) right = Marshal.PtrToStructure<short>(IntPtr.Add(start, 2)) / 32768f;
            }
            else if (captureBytesPerSample == 4)
            {
                left = Marshal.PtrToStructure<int>(start) / 2147483648f;
                if (captureChannels > 1) right = Marshal.PtrToStructure<int>(IntPtr.Add(start, 4)) / 2147483648f;
            }
            else if (captureBytesPerSample == 3)
            {
                left = Read24(start);
                if (captureChannels > 1) right = Read24(IntPtr.Add(start, 3));
            }
            if (!captureIsFloat && captureChannels == 1) right = left;
        }

        private static float Read24(IntPtr pointer)
        {
            byte[] bytes = new byte[3];
            Marshal.Copy(pointer, bytes, 0, 3);
            int value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
            if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
            return value / 8388608f;
        }

        private static short ToSample(float value)
        {
            if (value > 1f) value = 1f;
            else if (value < -1f) value = -1f;
            // Rounding rather than truncating: truncation biases every sample towards zero and
            // leaves a correlated -90 dB layer on quiet passages, which is audible as a faint
            // grain on a speaker pair that is otherwise silent.
            return (short)Math.Round(value * 32767f);
        }

        public override void Stop()
        {
            stopping = true;
            try
            {
                if (client != null) client.Stop();
            }
            catch (Exception)
            {
                // the endpoint may already have gone away
            }
            try { bufferEvent?.Set(); } catch (ObjectDisposedException) { }
            try { dataReady?.Set(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            Stop();
            if (worker != null && worker.IsAlive) worker.Join(500);
            bufferEvent?.Dispose();
            dataReady?.Dispose();
            AudioInterop.Release(captureObject);
            AudioInterop.Release(clientObject);
            AudioInterop.Release(deviceObject);
            AudioInterop.Release(enumeratorObject);
        }
    }
}
