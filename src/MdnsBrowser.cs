using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AirStereo
{
    /// <summary>
    /// Minimal multicast DNS browser. Deliberately dependency free: no Bonjour install,
    /// no background service, no third party mDNS library.
    /// </summary>
    public sealed class MdnsBrowser
    {
        private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");
        private const int MdnsPort = 5353;

        public sealed class Result
        {
            public List<MdnsRecord> Records = new List<MdnsRecord>();
            public int PacketsReceived;
            public int InterfacesJoined;
            public List<string> Warnings = new List<string>();
        }

        public Result Browse(string[] serviceTypes, TimeSpan timeout)
        {
            Result result = new Result();
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                JoinMulticastGroups(socket, result);

                SendQueries(socket, serviceTypes, result);

                socket.ReceiveTimeout = 1000;
                DateTime deadline = DateTime.UtcNow + timeout;
                DateTime secondRound = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeout.TotalMilliseconds * 0.4);
                bool secondRoundSent = false;

                while (DateTime.UtcNow < deadline)
                {
                    if (!secondRoundSent && DateTime.UtcNow >= secondRound)
                    {
                        SendQueries(socket, serviceTypes, result);
                        secondRoundSent = true;
                    }

                    byte[] buffer = new byte[9000];
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    try
                    {
                        int count = socket.ReceiveFrom(buffer, ref from);
                        result.PacketsReceived++;
                        Parse(buffer, count, ((IPEndPoint)from).Address.ToString(), result.Records);
                    }
                    catch (SocketException)
                    {
                        Thread.Sleep(100);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                try { socket.Close(); }
                catch (Exception) { }
            }
            return result;
        }

        private static void JoinMulticastGroups(Socket socket, Result result)
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface nic in interfaces)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    try
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                            new MulticastOption(MulticastGroup, address.Address));
                        result.InterfacesJoined++;
                    }
                    catch (SocketException error)
                    {
                        result.Warnings.Add("multicast join failed on " + address.Address + ": " + error.Message);
                    }
                }
            }
        }

        private static void SendQueries(Socket socket, string[] serviceTypes, Result result)
        {
            foreach (string service in serviceTypes)
            {
                try
                {
                    socket.SendTo(BuildQuery(service), new IPEndPoint(MulticastGroup, MdnsPort));
                }
                catch (SocketException error)
                {
                    result.Warnings.Add("query failed for " + service + ": " + error.Message);
                }
            }
        }

        private static byte[] BuildQuery(string name)
        {
            List<byte> packet = new List<byte>();
            packet.Add(0); packet.Add(0);          // transaction id
            packet.Add(0); packet.Add(0);          // flags: standard query
            packet.Add(0); packet.Add(1);          // questions
            packet.Add(0); packet.Add(0);          // answers
            packet.Add(0); packet.Add(0);          // authority
            packet.Add(0); packet.Add(0);          // additional
            foreach (string label in name.Split('.'))
            {
                if (label.Length == 0) continue;
                packet.Add((byte)label.Length);
                packet.AddRange(Encoding.ASCII.GetBytes(label));
            }
            packet.Add(0);
            packet.Add(0); packet.Add((byte)MdnsRecord.TypePtr);
            // Plain IN question. Asking with the unicast-response bit instead makes the
            // HomePod firmware stay silent, so answers are collected from multicast and a
            // missing record is handled by browsing again.
            packet.Add(0); packet.Add(1);          // IN, no cache-flush on a question
            return packet.ToArray();
        }

        private static void Parse(byte[] packet, int length, string source, List<MdnsRecord> records)
        {
            if (length < 12) return;
            int questions = (packet[4] << 8) | packet[5];
            int answers = (packet[6] << 8) | packet[7];
            int authority = (packet[8] << 8) | packet[9];
            int additional = (packet[10] << 8) | packet[11];
            int position = 12;

            for (int i = 0; i < questions; i++)
            {
                ReadName(packet, length, ref position);
                position += 4;
            }

            int total = answers + authority + additional;
            for (int i = 0; i < total; i++)
            {
                MdnsRecord record = ReadRecord(packet, length, ref position, source);
                if (record == null) break;
                records.Add(record);
            }
        }

        private static MdnsRecord ReadRecord(byte[] packet, int length, ref int position, string source)
        {
            string name = ReadName(packet, length, ref position);
            if (position + 10 > length) return null;

            ushort type = (ushort)((packet[position] << 8) | packet[position + 1]);
            position += 4;                                   // type + class
            position += 4;                                   // ttl
            ushort dataLength = (ushort)((packet[position] << 8) | packet[position + 1]);
            position += 2;

            int dataStart = position;
            int dataEnd = dataStart + dataLength;
            if (dataEnd > length) return null;

            MdnsRecord record = new MdnsRecord();
            record.Name = name;
            record.Type = type;
            record.SourceAddress = source;

            if (type == MdnsRecord.TypeA && dataLength == 4)
            {
                record.Text = new IPAddress(new byte[]
                {
                    packet[position], packet[position + 1],
                    packet[position + 2], packet[position + 3]
                }).ToString();
            }
            else if (type == MdnsRecord.TypeAaaa && dataLength == 16)
            {
                byte[] address = new byte[16];
                Array.Copy(packet, position, address, 0, 16);
                record.Text = new IPAddress(address).ToString();
            }
            else if (type == MdnsRecord.TypePtr)
            {
                record.Text = ReadName(packet, length, ref position);
            }
            else if (type == MdnsRecord.TypeSrv && dataLength >= 7)
            {
                int port = (packet[position + 4] << 8) | packet[position + 5];
                int cursor = position + 6;
                string target = ReadName(packet, length, ref cursor);
                record.Text = target + ":" + port;
            }
            else if (type == MdnsRecord.TypeTxt)
            {
                List<string> parts = new List<string>();
                int cursor = dataStart;
                while (cursor < dataEnd)
                {
                    int span = packet[cursor];
                    cursor++;
                    if (span == 0) continue;
                    int available = dataEnd - cursor;
                    if (available <= 0) break;
                    int take = span <= available ? span : available;
                    parts.Add(Encoding.UTF8.GetString(packet, cursor, take));
                    cursor += span;
                }
                record.Text = string.Join(" ", parts.ToArray());
            }
            else
            {
                record.Text = "(" + dataLength + " bytes)";
            }

            position = dataEnd;
            return record;
        }

        private static string ReadName(byte[] packet, int length, ref int position)
        {
            StringBuilder name = new StringBuilder();
            int jumpTarget = -1;
            int guard = 0;

            while (true)
            {
                if (guard++ > 200) break;
                if (position < 0 || position >= length) break;
                byte value = packet[position];
                if (value == 0)
                {
                    position++;
                    break;
                }
                if ((value & 0xC0) == 0xC0)
                {
                    if (position + 1 >= length) break;
                    int pointer = ((value & 0x3F) << 8) | packet[position + 1];
                    if (jumpTarget < 0) jumpTarget = position + 2;
                    position = pointer;
                    continue;
                }
                if (position + 1 + value > length) break;
                if (name.Length > 0) name.Append('.');
                name.Append(Encoding.UTF8.GetString(packet, position + 1, value));
                position += value + 1;
            }

            if (jumpTarget >= 0) position = jumpTarget;
            return name.ToString();
        }
    }
}
