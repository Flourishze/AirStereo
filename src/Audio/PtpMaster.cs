using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace AirStereo.Audio
{
    /// <summary>
    /// Minimal unicast PTPv2 master for the AirPlay timing profile. The sender owns the
    /// clock: it announces itself, publishes Sync/FollowUp pairs to every member of the
    /// group, and answers the delay requests the receivers send back. Nothing about the
    /// system clock is modified.
    /// </summary>
    public sealed class PtpMaster : IDisposable
    {
        public const int EventPort = 319;
        public const int GeneralPort = 320;
        private const int SyncIntervalMs = 125;

        private readonly MediaClock clock;
        private readonly List<IPAddress> peers;
        private readonly Socket eventSocket;
        private readonly Socket generalSocket;
        private readonly Thread worker;
        private readonly ManualResetEventSlim stop = new ManualResetEventSlim(false);
        private long receivedPackets;
        private readonly ConcurrentDictionary<string, long> responsesByPeer =
            new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public ulong ClockId { get; private set; }

        public long ReceivedPackets { get { return Interlocked.Read(ref receivedPackets); } }

        public long ResponsesFrom(string address)
        {
            return responsesByPeer.TryGetValue(address, out long count) ? count : 0;
        }

        public PtpMaster(IEnumerable<IPAddress> peerAddresses, MediaClock mediaClock)
        {
            clock = mediaClock;
            peers = new List<IPAddress>(peerAddresses);

            eventSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            generalSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            eventSocket.Bind(new IPEndPoint(IPAddress.Any, EventPort));
            generalSocket.Bind(new IPEndPoint(IPAddress.Any, GeneralPort));
            eventSocket.ReceiveTimeout = 50;
            generalSocket.ReceiveTimeout = 50;

            byte[] identity = new byte[8];
            System.Security.Cryptography.RandomNumberGenerator.Fill(identity);
            ClockId = BinaryPrimitives.ReadUInt64BigEndian(identity) & 0x7fff_ffff_ffff_ffffUL;

            worker = new Thread(Run) { IsBackground = true, Name = "AirStereo PTP" };
            worker.Start();
        }

        public static void EnsurePortsAvailable()
        {
            using (Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                probe.Bind(new IPEndPoint(IPAddress.Any, EventPort));
            }
            using (Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                probe.Bind(new IPEndPoint(IPAddress.Any, GeneralPort));
            }
        }

        private static byte[] Header(byte kind, int length, ulong clockIdentity, ushort sequence, ushort flags, int interval)
        {
            byte[] packet = new byte[length];
            packet[0] = (byte)(0x10 | kind);
            packet[1] = 2;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)length);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), flags);
            BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(20), clockIdentity);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(28), 0x8005);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(30), sequence);
            packet[33] = (byte)interval;
            return packet;
        }

        private static void WriteTimestamp(byte[] packet, int offset, ulong nanoseconds)
        {
            ulong seconds = nanoseconds / 1_000_000_000UL;
            uint fraction = (uint)(nanoseconds % 1_000_000_000UL);
            packet[offset] = (byte)(seconds >> 40);
            packet[offset + 1] = (byte)(seconds >> 32);
            packet[offset + 2] = (byte)(seconds >> 24);
            packet[offset + 3] = (byte)(seconds >> 16);
            packet[offset + 4] = (byte)(seconds >> 8);
            packet[offset + 5] = (byte)seconds;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(offset + 6), fraction);
        }

        private static byte[] Tlv(byte[] data)
        {
            byte[] output = new byte[4 + data.Length];
            output[1] = 3;
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), (ushort)data.Length);
            Buffer.BlockCopy(data, 0, output, 4, data.Length);
            return output;
        }

        private byte[] Announce(ushort sequence, ulong now)
        {
            byte[] packet = Header(11, 76, ClockId, sequence, 0x0408, 0);
            WriteTimestamp(packet, 34, now);
            packet[47] = 96;                       // local oscillator, not a GPS claim
            packet[48] = 248;
            packet[49] = 0xfe;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(50), 0xffff);
            packet[52] = 128;
            BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(53), ClockId);
            packet[63] = 0xa0;
            Buffer.BlockCopy(new byte[] { 0, 8, 0, 8 }, 0, packet, 64, 4);
            BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(68), ClockId);
            return packet;
        }

        private void SyncPair(ushort sequence, ulong now, out byte[] sync, out byte[] followUp)
        {
            sync = Header(0, 44, ClockId, sequence, 0x0608, -3);
            WriteTimestamp(sync, 34, now);

            followUp = Header(8, 96, ClockId, sequence, 0x0408, -3);
            WriteTimestamp(followUp, 34, now);

            byte[] rate = new byte[28];
            Buffer.BlockCopy(new byte[] { 0, 0x80, 0xc2, 0, 0, 1 }, 0, rate, 0, 6);
            byte[] identity = new byte[16];
            Buffer.BlockCopy(new byte[] { 0, 0x0d, 0x93, 0, 0, 4 }, 0, identity, 0, 6);
            BinaryPrimitives.WriteUInt64BigEndian(identity.AsSpan(6), ClockId);

            Buffer.BlockCopy(Tlv(rate), 0, followUp, 44, 32);
            Buffer.BlockCopy(Tlv(identity), 0, followUp, 76, 20);
        }

        private void Run()
        {
            // The receivers track this thread's timestamps, so it must not be the one that
            // loses its slice to a background task.
            AudioThread.Raise();
            ushort sequence = 0;
            DateTime lastAnnounce = DateTime.UtcNow.AddSeconds(-2);
            DateTime nextSync = DateTime.UtcNow;
            byte[] buffer = new byte[2048];

            while (!stop.IsSet)
            {
                DateTime now = DateTime.UtcNow;
                if (now >= nextSync)
                {
                    SyncPair(sequence, clock.NowNanoseconds, out byte[] sync, out byte[] followUp);
                    bool announce = (now - lastAnnounce).TotalSeconds >= 1;
                    byte[] announcePacket = announce ? Announce(sequence, clock.NowNanoseconds) : null;
                    foreach (IPAddress peer in peers)
                    {
                        TrySend(eventSocket, sync, peer, EventPort);
                        TrySend(generalSocket, followUp, peer, GeneralPort);
                        if (announcePacket != null) TrySend(generalSocket, announcePacket, peer, GeneralPort);
                    }
                    if (announce) lastAnnounce = now;
                    sequence++;
                    nextSync = now.AddMilliseconds(SyncIntervalMs);
                }

                Drain(eventSocket, buffer);
                Drain(generalSocket, buffer);
                stop.Wait(2);
            }
        }

        private void Drain(Socket socket, byte[] buffer)
        {
            try
            {
                while (socket.Poll(0, SelectMode.SelectRead))
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int count = socket.ReceiveFrom(buffer, ref from);
                    if (count < 34 || buffer[1] != 2) continue;
                    IPAddress source = ((IPEndPoint)from).Address;
                    if (!peers.Contains(source)) continue;
                    int length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2));
                    if (length > count || length < 34) continue;

                    Interlocked.Increment(ref receivedPackets);
                    responsesByPeer.AddOrUpdate(source.ToString(), 1, (_, count) => count + 1);
                    byte kind = (byte)(buffer[0] & 0x0f);
                    ushort requestSequence = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(30));
                    if ((kind == 1 || kind == 2) && count >= 44) Respond(kind, requestSequence, buffer, source);
                }
            }
            catch (SocketException)
            {
                // transient socket condition; the loop retries on the next pass
            }
        }

        private void Respond(byte requestKind, ushort requestSequence, byte[] request, IPAddress source)
        {
            bool delayRequest = requestKind == 1;
            byte[] response = Header((byte)(delayRequest ? 9 : 3), 54, ClockId, requestSequence,
                delayRequest ? (ushort)0x0408 : (ushort)0x0608, -3);
            WriteTimestamp(response, 34, clock.NowNanoseconds);
            Buffer.BlockCopy(request, 20, response, 44, 10);

            if (delayRequest)
            {
                TrySend(generalSocket, response, source, GeneralPort);
                return;
            }

            TrySend(eventSocket, response, source, EventPort);
            byte[] followUp = Header(10, 54, ClockId, requestSequence, 0x0408, -3);
            WriteTimestamp(followUp, 34, clock.NowNanoseconds);
            Buffer.BlockCopy(request, 20, followUp, 44, 10);
            TrySend(generalSocket, followUp, source, GeneralPort);
        }

        private static void TrySend(Socket socket, byte[] data, IPAddress address, int port)
        {
            try
            {
                socket.SendTo(data, new IPEndPoint(address, port));
            }
            catch (SocketException)
            {
                // a dropped timing datagram is harmless, the next Sync follows in 125 ms
            }
        }

        public void Dispose()
        {
            stop.Set();
            if (worker.IsAlive) worker.Join(500);
            eventSocket.Dispose();
            generalSocket.Dispose();
            stop.Dispose();
        }
    }
}
