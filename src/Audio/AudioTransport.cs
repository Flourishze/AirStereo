using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using AirStereo.Protocol;

namespace AirStereo.Audio
{
    /// <summary>
    /// RTP audio packetization for AirPlay 2. The payload is sealed with the stream key the
    /// receiver is told about during SETUP; the associated data is the RTP timestamp and
    /// SSRC, and the counter that produced the nonce travels in the trailing eight bytes so
    /// the receiver can authenticate a packet even after losing earlier ones.
    /// </summary>
    public sealed class AudioPacketizer : IDisposable
    {
        public const int FramesPerPacket = 352;
        public const int Channels = 2;
        public const int PcmBytes = FramesPerPacket * Channels * 2;
        public const int MaxHistory = 512;

        private sealed class CachedPacket
        {
            public ushort Sequence;
            public byte[] Bytes = Array.Empty<byte>();
            public DateTime SentAt;
            public DateTime Deadline;
        }

        public const int SupportedRate44100 = 44100;
        public const int SupportedRate48000 = 48000;

        private readonly ChaCha20Poly1305Compat aead;
        private readonly LinkedList<CachedPacket> history = new LinkedList<CachedPacket>();
        /// <summary>
        /// Sealed packets and their history records are recycled instead of collected: this runs
        /// on the thread that has to emit a packet every eight milliseconds, and a garbage
        /// collection there arrives as a stall the receivers would have to correct for. Only
        /// this thread touches the pools and the history.
        /// </summary>
        private readonly Stack<byte[]> sparePackets = new Stack<byte[]>();
        private readonly Stack<CachedPacket> spareRecords = new Stack<CachedPacket>();
        private ulong counter;
        private TimeSpan retention = TimeSpan.FromSeconds(1);
        /// <summary>
        /// How long a sent packet stays worth resending: the audio it carries is played
        /// <see cref="playWindow"/> after it leaves this process, so a receiver asking for it
        /// any later than that is asking for something that has already gone past its ears.
        /// </summary>
        private TimeSpan playWindow = TimeSpan.FromMilliseconds(200);

        public ushort Sequence { get; private set; }
        public uint Timestamp { get; private set; }
        public uint Ssrc { get; }
        public int Rate { get; }
        public long PacketsSent { get; private set; }

        public AudioPacketizer(byte[] key, ushort sequence, uint timestamp, uint ssrc, int rate)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("audio key must be 32 bytes");
            if (rate != SupportedRate44100 && rate != SupportedRate48000)
            {
                throw new ArgumentException("sample rate must be 44100 or 48000");
            }
            aead = new ChaCha20Poly1305Compat(key);
            Sequence = sequence;
            Timestamp = timestamp;
            Ssrc = ssrc;
            Rate = rate;
        }

        public static ulong AudioFormat(bool alac, int rate)
        {
            if (alac) return rate == SupportedRate48000 ? 1UL << 20 : 1UL << 18;
            return rate == SupportedRate48000 ? 1UL << 15 : 1UL << 11;
        }

        /// <summary>Samples of receiver latency, the unit SETUP and the timing packets use.</summary>
        public static uint LatencySamples(int latencyMs, int rate)
        {
            return (uint)((long)latencyMs * rate / 1000);
        }

        public byte[] Packet(byte[] pcm, bool first)
        {
            if (pcm.Length != PcmBytes) throw new ArgumentException("PCM block must be " + PcmBytes + " bytes");
            DateTime now = DateTime.UtcNow;
            return Packet(pcm, first, now, now + playWindow);
        }

        public byte[] Packet(byte[] pcm, bool first, DateTime now, DateTime deadline)
        {
            if (pcm.Length != PcmBytes) throw new ArgumentException("PCM block must be " + PcmBytes + " bytes");

            int length = 12 + pcm.Length + 16 + 8;
            byte[] packet = sparePackets.Count > 0 ? sparePackets.Pop() : new byte[length];
            packet[0] = 0x80;
            packet[1] = first ? (byte)0xE0 : (byte)0x60;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), Sequence);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), Timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), Ssrc);

            ulong nonceCounter = counter;
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4), nonceCounter);
            aead.Encrypt(nonce, pcm, packet.AsSpan(12, pcm.Length),
                packet.AsSpan(12 + pcm.Length, 16), packet.AsSpan(4, 8));
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(12 + pcm.Length + 16), nonceCounter);
            counter++;

            CachedPacket record = spareRecords.Count > 0 ? spareRecords.Pop() : new CachedPacket();
            record.Sequence = Sequence;
            record.Bytes = packet;
            record.SentAt = now;
            record.Deadline = deadline;
            Retain(record);
            Sequence++;
            Timestamp += FramesPerPacket;
            PacketsSent++;
            return packet;
        }

        private void Retain(CachedPacket packet)
        {
            DateTime now = packet.SentAt;
            while (history.First != null &&
                   (history.First.Value.SentAt + retention <= now || history.Count >= MaxHistory))
            {
                Recycle(history.First.Value);
                history.RemoveFirst();
            }
            history.AddLast(packet);
        }

        private void Recycle(CachedPacket cached)
        {
            if (cached.Bytes != null && cached.Bytes.Length == 12 + PcmBytes + 16 + 8 &&
                sparePackets.Count < MaxHistory)
            {
                sparePackets.Push(cached.Bytes);
            }
            cached.Bytes = null;
            if (spareRecords.Count < MaxHistory) spareRecords.Push(cached);
        }

        /// <summary>Advances the RTP timeline for packets the sender had to drop.</summary>
        public void SkipPackets(int count)
        {
            Sequence = (ushort)(Sequence + count);
            Timestamp += (uint)count * FramesPerPacket;
        }

        public void ConfigureRetention(TimeSpan latency)
        {
            retention = latency < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : latency + TimeSpan.FromMilliseconds(250);
            playWindow = latency;
        }

        public enum RetransmitResult
        {
            Packet,
            Missing,
            Expired
        }

        public RetransmitResult Retransmit(ushort sequence, DateTime now, out byte[] reply)
        {
            reply = null;
            LinkedListNode<CachedPacket> node = history.Last;
            while (node != null && node.Value.Sequence != sequence) node = node.Previous;
            if (node == null) return RetransmitResult.Missing;

            CachedPacket cached = node.Value;
            if (now >= cached.Deadline || now - cached.SentAt >= retention) return RetransmitResult.Expired;

            reply = new byte[4 + cached.Bytes.Length];
            reply[0] = 0x80;
            reply[1] = 0xD6;
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), sequence);
            Buffer.BlockCopy(cached.Bytes, 0, reply, 4, cached.Bytes.Length);
            return RetransmitResult.Packet;
        }

        /// <summary>Parses a receiver retransmission request: 0x80, 0x55, then seq and count.</summary>
        public static bool ParseRetransmitRequest(byte[] request, out ushort start, out ushort count)
        {
            return ParseRetransmitRequest(request, request == null ? 0 : request.Length, out start, out count);
        }

        /// <summary>
        /// Same, for a caller reading into a buffer it reuses: the request is the first eight
        /// bytes, and the rest of the datagram is ignored.
        /// </summary>
        public static bool ParseRetransmitRequest(byte[] request, int length, out ushort start, out ushort count)
        {
            start = 0;
            count = 0;
            if (request == null || length != 8) return false;
            if ((request[0] & 0xc0) != 0x80) return false;
            if ((request[1] & 0x7f) != 0x55) return false;
            start = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4));
            count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6));
            return true;
        }

        public static byte[] TimingPacket(uint rtpTimestamp, uint latencySamples, ulong nowNanoseconds,
            ulong? clockId, bool first)
        {
            bool ptp = clockId.HasValue;
            // PTP form is 4 + 4 + 8 + 4 + 8 bytes; the NTP form drops the trailing clock id.
            byte[] packet = new byte[ptp ? 28 : 20];
            packet[0] = first ? (byte)0x90 : (byte)0x80;
            packet[1] = ptp ? (byte)0xd7 : (byte)0xd4;
            packet[2] = 0;
            packet[3] = (byte)(ptp ? 6 : 7);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), rtpTimestamp);
            if (ptp)
            {
                BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), nowNanoseconds);
            }
            else
            {
                Buffer.BlockCopy(NtpTimestamp(nowNanoseconds), 0, packet, 8, 8);
            }
            // latencyMin is applied by the receiver; shifting this mapping as well doubles it.
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16),
                ptp ? rtpTimestamp - latencySamples : rtpTimestamp);
            if (ptp) BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(20), clockId.Value);
            return packet;
        }

        public static byte[] NtpTimestamp(ulong unixNanoseconds)
        {
            byte[] output = new byte[8];
            uint seconds = (uint)(unixNanoseconds / 1_000_000_000UL + 2_208_988_800UL);
            ulong fraction = (unixNanoseconds % 1_000_000_000UL) * (1UL << 32) / 1_000_000_000UL;
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0), seconds);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), (uint)fraction);
            return output;
        }

        public void Dispose()
        {
            aead.Dispose();
        }
    }

    /// <summary>
    /// NTP responder used only when the PTP ports cannot be bound. Receivers fall back to
    /// NTP timing when SETUP announces timingProtocol NTP and a timingPort.
    /// </summary>
    public sealed class NtpResponder : IDisposable
    {
        private readonly MediaClock clock;
        private readonly Socket socket;
        private readonly Thread worker;
        private readonly ManualResetEventSlim stop = new ManualResetEventSlim(false);
        private readonly List<IPAddress> peers;

        public int Port { get { return ((IPEndPoint)socket.LocalEndPoint).Port; } }

        public NtpResponder(IEnumerable<IPAddress> peerAddresses, MediaClock mediaClock)
        {
            clock = mediaClock;
            peers = new List<IPAddress>(peerAddresses);
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.ReceiveTimeout = 100;
            worker = new Thread(Run) { IsBackground = true, Name = "AirStereo NTP" };
            worker.Start();
        }

        private void Run()
        {
            byte[] buffer = new byte[256];
            while (!stop.IsSet)
            {
                try
                {
                    if (!socket.Poll(100000, SelectMode.SelectRead)) continue;
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int count = socket.ReceiveFrom(buffer, ref from);
                    if (count != 32 || (buffer[1] & 0x7f) != 0x52) continue;
                    if (!peers.Contains(((IPEndPoint)from).Address)) continue;

                    ulong received = clock.NowNanoseconds;
                    byte[] response = new byte[0x30];
                    byte[] head = new byte[] { 0x80, 0xd3, 0, 7, 0, 0, 0, 0 };
                    Buffer.BlockCopy(head, 0, response, 0, 8);
                    Buffer.BlockCopy(buffer, 24, response, 8, 8);
                    Buffer.BlockCopy(AudioPacketizer.NtpTimestamp(received), 0, response, 16, 8);
                    Buffer.BlockCopy(AudioPacketizer.NtpTimestamp(clock.NowNanoseconds), 0, response, 24, 8);
                    socket.SendTo(response, from);
                }
                catch (SocketException)
                {
                    // retry on the next poll
                }
            }
        }

        public void Dispose()
        {
            stop.Set();
            if (worker.IsAlive) worker.Join(300);
            socket.Dispose();
            stop.Dispose();
        }
    }
}
