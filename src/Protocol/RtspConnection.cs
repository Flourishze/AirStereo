using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirStereo.Protocol
{
    public sealed class RtspMessage
    {
        public string FirstLine = "";
        public Dictionary<string, string> Headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();

        public int Status
        {
            get
            {
                string[] parts = FirstLine.Split(' ');
                if (parts.Length < 2 || !int.TryParse(parts[1], out int status))
                {
                    throw new ProtocolException("malformed RTSP status line: " + FirstLine);
                }
                return status;
            }
        }

        public bool IsSuccess { get { return Status == 200; } }

        public string Header(string name)
        {
            return Headers.TryGetValue(name, out string value) ? value : null;
        }
    }

    /// <summary>
    /// RTSP/HTTP control connection to one AirPlay accessory. Requests and responses are
    /// plaintext until pairing enables the HAP record layer, after which every byte on the
    /// wire is a sealed record and the header block itself is encrypted too.
    /// </summary>
    public sealed class RtspConnection : IDisposable
    {
        private const int MaxHeaderBytes = 16 * 1024;
        private const int MaxBodyBytes = 1024 * 1024;

        private readonly Socket socket;
        private readonly List<byte> plaintext = new List<byte>();
        private readonly List<byte> encrypted = new List<byte>();
        private RecordCipher transmit;
        private RecordCipher receive;
        private int sequence;
        private int timeoutMs = 4000;
        private bool disposed;

        public RtspConnection(IPAddress address, int port, int connectTimeoutMs = 3000)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            socket.Blocking = false;

            Stopwatch watch = Stopwatch.StartNew();
            IAsyncResult pending = socket.BeginConnect(address, port, null, null);
            while (!pending.IsCompleted)
            {
                if (watch.ElapsedMilliseconds > connectTimeoutMs)
                {
                    socket.Dispose();
                    throw new ProtocolException("timed out connecting to " + address + ":" + port);
                }
                System.Threading.Thread.Sleep(10);
            }
            try
            {
                socket.EndConnect(pending);
            }
            catch (SocketException error)
            {
                socket.Dispose();
                throw new ProtocolException("cannot reach " + address + ":" + port + " (" + error.SocketErrorCode + ")");
            }
        }

        public IPAddress LocalAddress
        {
            get { return ((IPEndPoint)socket.LocalEndPoint).Address; }
        }

        public IPAddress RemoteAddress
        {
            get { return ((IPEndPoint)socket.RemoteEndPoint).Address; }
        }

        /// <summary>Headers and bodies are sealed from this point on.</summary>
        public void EnableEncryption(byte[] transmitKey, byte[] receiveKey)
        {
            transmit = new RecordCipher(transmitKey);
            receive = new RecordCipher(receiveKey);
        }

        public void SetTimeout(int milliseconds)
        {
            timeoutMs = milliseconds;
        }

        public RtspMessage Send(string method, string path, byte[][] headers, byte[] body)
        {
            byte[] request = BuildRequest(method, path, headers, body);
            Write(request);

            Stopwatch watch = Stopwatch.StartNew();
            while (true)
            {
                RtspMessage message = TryReadMessage();
                if (message != null)
                {
                    if (StaleResponse(message)) continue;
                    return message;
                }
                if (watch.ElapsedMilliseconds > timeoutMs)
                {
                    throw new ProtocolException("no reply to " + method + " " + path + " within " + timeoutMs + "ms");
                }
                Pump(20);
            }
        }

        public RtspMessage Get(string path)
        {
            return Send("GET", path, null, null);
        }

        /// <summary>
        /// Returns the next complete message the peer volunteered, or null. Used by the
        /// event channel, where the accessory replies to requests it makes itself.
        /// </summary>
        public RtspMessage TryReceive(int waitMicroseconds)
        {
            RtspMessage message = TryReadMessage();
            if (message != null) return message;
            Pump(waitMicroseconds);
            return TryReadMessage();
        }

        /// <summary>Writes a bare reply (event acknowledgements) honouring the record layer.</summary>
        public void WriteRaw(byte[] data)
        {
            Write(data);
        }

        public RtspMessage Options(string path)
        {
            return Send("OPTIONS", path, null, null);
        }

        public byte[] BuildRequest(string method, string path, byte[][] headers, byte[] body)
        {
            string protocol = path.StartsWith("/pair-", StringComparison.Ordinal) ? "HTTP/1.1" : "RTSP/1.0";
            StringBuilder text = new StringBuilder();
            sequence++;
            text.Append(method).Append(' ').Append(path).Append(' ').Append(protocol).Append("\r\n");
            text.Append("CSeq: ").Append(sequence).Append("\r\n");
            text.Append("User-Agent: AirPlay/550.10\r\n");
            text.Append("Content-Length: ").Append(body == null ? 0 : body.Length).Append("\r\n");
            if (headers != null)
            {
                foreach (byte[] header in headers)
                {
                    string line = Encoding.ASCII.GetString(header);
                    if (line.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    {
                        throw new ProtocolException("header values cannot contain line breaks");
                    }
                    text.Append(line).Append("\r\n");
                }
            }
            text.Append("\r\n");

            byte[] head = Encoding.ASCII.GetBytes(text.ToString());
            if (body == null || body.Length == 0) return head;
            return HapCrypto.Concat(head, body);
        }

        private void Write(byte[] data)
        {
            byte[] wire = transmit == null ? data : transmit.Frame(data);
            int offset = 0;
            Stopwatch watch = Stopwatch.StartNew();
            while (offset < wire.Length)
            {
                try
                {
                    offset += socket.Send(wire, offset, wire.Length - offset, SocketFlags.None);
                }
                catch (SocketException error) when (
                    error.SocketErrorCode == SocketError.WouldBlock ||
                    error.SocketErrorCode == SocketError.IOPending)
                {
                    if (watch.ElapsedMilliseconds > timeoutMs) throw new ProtocolException("control write timed out");
                    socket.Poll(20000, SelectMode.SelectWrite);
                }
            }
        }

        /// <summary>Reads whatever the socket has and appends it to the plaintext buffer.</summary>
        private void Pump(int waitMicroseconds)
        {
            if (disposed) throw new ProtocolException("control connection is closed");
            bool readable;
            try
            {
                readable = socket.Poll(waitMicroseconds, SelectMode.SelectRead);
            }
            catch (SocketException)
            {
                throw new ProtocolException("control connection was reset");
            }

            if (!readable)
            {
                if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                {
                    throw new ProtocolException("receiver closed the control connection");
                }
                return;
            }

            byte[] buffer = new byte[8192];
            int count;
            try
            {
                count = socket.Receive(buffer);
            }
            catch (SocketException error) when (
                error.SocketErrorCode == SocketError.WouldBlock ||
                error.SocketErrorCode == SocketError.IOPending)
            {
                return;
            }
            catch (SocketException error)
            {
                throw new ProtocolException("control read failed (" + error.SocketErrorCode + ")");
            }

            if (count == 0) throw new ProtocolException("receiver closed the control connection");

            if (receive == null)
            {
                for (int i = 0; i < count; i++) plaintext.Add(buffer[i]);
                return;
            }

            for (int i = 0; i < count; i++) encrypted.Add(buffer[i]);
            while (encrypted.Count >= 2)
            {
                int length = encrypted[0] | (encrypted[1] << 8);
                if (length <= 0 || length > RecordCipher.MaxRecord)
                {
                    throw new ProtocolException("invalid encrypted record length " + length);
                }
                int end = 2 + length + 16;
                if (encrypted.Count < end) break;

                byte[] record = new byte[length + 16];
                encrypted.CopyTo(2, record, 0, record.Length);
                byte[] header = new byte[2] { encrypted[0], encrypted[1] };
                byte[] decoded = receive.Open(record, header);
                plaintext.AddRange(decoded);
                encrypted.RemoveRange(0, end);
            }
        }

        private RtspMessage TryReadMessage()
        {
            int headerEnd = IndexOf(plaintext, 0, "\r\n\r\n");
            if (headerEnd < 0)
            {
                if (plaintext.Count > MaxHeaderBytes) throw new ProtocolException("RTSP header block is too large");
                return null;
            }
            if (headerEnd + 4 > MaxHeaderBytes) throw new ProtocolException("RTSP header block is too large");

            string text = Encoding.ASCII.GetString(plaintext.ToArray(), 0, headerEnd + 4);
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            RtspMessage message = new RtspMessage();
            message.FirstLine = lines[0];
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) throw new ProtocolException("malformed RTSP header: " + lines[i]);
                string name = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                message.Headers[name] = value;
            }

            int length = 0;
            string contentLength = message.Header("Content-Length");
            if (contentLength != null && !int.TryParse(contentLength, out length)) length = 0;
            if (length < 0 || length > MaxBodyBytes) throw new ProtocolException("RTSP body is too large");

            int total = headerEnd + 4 + length;
            if (plaintext.Count < total) return null;

            if (length > 0)
            {
                message.Body = new byte[length];
                plaintext.CopyTo(headerEnd + 4, message.Body, 0, length);
            }
            plaintext.RemoveRange(0, total);
            return message;
        }

        private bool StaleResponse(RtspMessage message)
        {
            string cseq = message.Header("CSeq");
            return cseq != null && int.TryParse(cseq, out int value) && value < sequence;
        }

        private static int IndexOf(List<byte> buffer, int start, string needle)
        {
            byte[] pattern = Encoding.ASCII.GetBytes(needle);
            for (int i = start; i <= buffer.Count - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            transmit?.Dispose();
            receive?.Dispose();
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
                // the peer may already be gone
            }
            socket.Dispose();
        }
    }
}
