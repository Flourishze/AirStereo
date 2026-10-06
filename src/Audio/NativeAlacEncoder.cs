using System;
using System.Runtime.InteropServices;

namespace AirStereo.Audio
{
    /// <summary>
    /// Direct x64 P/Invoke wrapper for LibALAC. The managed buffers are owned by this
    /// instance and reused for every block; the native call pins them for its duration.
    /// </summary>
    internal sealed class NativeAlacEncoder : IDisposable
    {
        public const int FramesPerPacket = AudioPacketizer.FramesPerPacket;
        public const int Channels = AudioPacketizer.Channels;
        public const int BitsPerSample = 16;
        public const int PcmBytes = AudioPacketizer.PcmBytes;
        // ALAC can expand incompressible PCM slightly. This is deliberately generous and
        // is also the capacity used by AudioPacketizer's pooled media buffers.
        public const int MaxEncodedBytes = 4096;

        private const string DllName = "LibALAC64.dll";
        private IntPtr handle;
        private readonly byte[] nativePcm = new byte[PcmBytes];
        private readonly byte[] outputBuffer = new byte[MaxEncodedBytes];
        private readonly byte[] cookie;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr InitializeEncoder(int sampleRate, int channels, int bitsPerSample,
            int framesPerPacket, [MarshalAs(UnmanagedType.I1)] bool useFastMode);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int GetMagicCookieSize(IntPtr encoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int GetMagicCookie(IntPtr encoder, IntPtr outputCookie);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int Encode(IntPtr encoder, byte[] readBuffer, byte[] writeBuffer,
            ref int ioNumBytes);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FinishEncoder(IntPtr encoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ParseMagicCookie(byte[] magicCookie, int cookieSize,
            out int sampleRate, out int channels, out int bitsPerSample, out int framesPerPacket);

        public NativeAlacEncoder(int sampleRate, bool useFastMode = false)
        {
            if (sampleRate != AudioPacketizer.SupportedRate44100 &&
                sampleRate != AudioPacketizer.SupportedRate48000)
            {
                throw new ArgumentException("sample rate must be 44100 or 48000", nameof(sampleRate));
            }

            try
            {
                handle = InitializeEncoder(sampleRate, Channels, BitsPerSample, FramesPerPacket, useFastMode);
            }
            catch (DllNotFoundException error)
            {
                throw new InvalidOperationException(
                    "ALAC 编码器缺少 LibALAC64.dll；请将 x64 native DLL 放在应用输出目录。", error);
            }
            catch (BadImageFormatException error)
            {
                throw new InvalidOperationException(
                    "LibALAC64.dll 不是当前 x64 进程可加载的 native DLL。", error);
            }

            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("LibALAC64.dll 无法初始化 ALAC encoder。");

            int cookieSize = GetMagicCookieSize(handle);
            if (cookieSize <= 0 || cookieSize > 1024)
            {
                FinishEncoder(handle);
                handle = IntPtr.Zero;
                throw new InvalidOperationException("LibALAC64.dll 返回了无效的 magic cookie 长度：" + cookieSize);
            }

            cookie = new byte[cookieSize];
            // Use an explicit unmanaged output pointer here. The native API accepts a
            // plain unsigned-char pointer; explicit allocation avoids any ambiguity in
            // the CLR array marshaller for this legacy native ABI.
            IntPtr cookieBuffer = Marshal.AllocHGlobal(cookieSize);
            try
            {
                int copied = GetMagicCookie(handle, cookieBuffer);
                if (copied != cookieSize)
                {
                    throw new InvalidOperationException(
                        "LibALAC64.dll 获取 magic cookie 失败：expected=" + cookieSize + ", actual=" + copied);
                }
                Marshal.Copy(cookieBuffer, cookie, 0, cookieSize);
            }
            catch
            {
                FinishEncoder(handle);
                handle = IntPtr.Zero;
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(cookieBuffer);
            }
        }

        public byte[] MagicCookie { get { return cookie; } }
        public byte[] OutputBuffer { get { return outputBuffer; } }

        public int Encode(byte[] pcm)
        {
            if (handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeAlacEncoder));
            if (pcm == null || pcm.Length != PcmBytes)
                throw new ArgumentException("PCM block must be " + PcmBytes + " bytes", nameof(pcm));

            // ioNumBytes is an in/out field: on entry it is the valid PCM input length,
            // not the capacity of the output scratch buffer.
            int ioNumBytes = PcmBytes;
            // Packetizer accepts network-order PCM for the original PCM fallback. LibALAC's
            // inputFormat is NativeEndian: Windows x64 requires little-endian samples.
            // A round-trip of arbitrary bytes alone would NOT detect a swapped sample value.
            for (int i = 0; i < pcm.Length; i += 2)
            {
                nativePcm[i] = pcm[i + 1];
                nativePcm[i + 1] = pcm[i];
            }
            int status = Encode(handle, nativePcm, outputBuffer, ref ioNumBytes);
            if (status != 0)
                throw new InvalidOperationException("LibALAC Encode failed with status " + status + ".");
            if (ioNumBytes <= 0 || ioNumBytes > outputBuffer.Length)
                throw new InvalidOperationException("LibALAC returned an invalid encoded length: " + ioNumBytes);
            return ioNumBytes;
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            FinishEncoder(handle);
            handle = IntPtr.Zero;
        }
    }
}

