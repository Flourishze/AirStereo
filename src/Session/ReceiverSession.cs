using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using AirStereo.Audio;
using AirStereo.Protocol;

namespace AirStereo.Session
{
    public sealed class SessionOptions
    {
        /// <summary>
        /// The timing packet cadence the receiver is tracked at. Ten a second is what every
        /// working AirPlay 2 sender uses, and 0 asks the streamer to derive one instead.
        /// </summary>
        public const int DefaultSyncMs = 100;

        public string SenderName = "AirStereo";
        public string SenderId = "02:57:32:41:50:01";
        public int SampleRate = 44100;
        public int LatencyMs = 250;
        public int SyncMs = DefaultSyncMs;
        public bool UsePtp = true;
        public string GroupId;
        public bool SplitStereo;
        public int Balance;
        public LivePlaybackControl LiveControl;
        public bool SwapChannels;
        public string Pin = Srp.DefaultPin;
        /// <summary>Every receiver in this group, announced to each member through SETPEERS.</summary>
        public List<string> PeerAddresses = new List<string>();
    }

    /// <summary>
    /// One RTSP session with one physical receiver: pair, describe, set up the audio stream,
    /// record, and then carry encrypted RTP audio on a fixed sample timeline.
    /// </summary>
    public sealed class ReceiverSession : IDisposable
    {
        private static readonly ConcurrentDictionary<string, ReceiverSession> activeSessions =
            new ConcurrentDictionary<string, ReceiverSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Receiver receiver;
        private readonly SessionOptions options;
        private readonly Action<string> log;

        private RtspConnection control;
        private RtspConnection events;
        private Thread eventWorker;
        private ManualResetEventSlim eventStop;
        private Socket audioSocket;
        private Socket controlSocket;
        private IPEndPoint audioTarget;
        private IPEndPoint timingTarget;
        private AudioPacketizer packetizer;
        /// <summary>Guards the RTSP control connection against the feedback thread.</summary>
        private readonly object controlLock = new object();
        private Thread feedbackWorker;
        private ManualResetEventSlim feedbackStop;
        /// <summary>Reused for incoming retransmission requests on the audio thread.</summary>
        private byte[] requestBuffer;
        private volatile bool disposed;
        private volatile bool closing;

        public Dictionary<string, object> Info { get; private set; }
        /// <summary>Samples of latency the timing packets carry: what this sender asked for.</summary>
        public int RequestedLatencySamples { get; private set; }
        public int NegotiatedLatencySamples { get; private set; }
        public string NegotiatedLatencySource { get; private set; }
        public long EventMessages { get; private set; }
        public long FeedbackFailures { get; private set; }
        public int ConsecutiveMediaSendFailures { get; private set; }
        /// <summary>Retransmission requests the receiver sent: a direct measure of packet loss.</summary>
        public long RetransmitRequests { get; private set; }
        /// <summary>Packets the receiver asked for again and got.</summary>
        public long RetransmittedPackets { get; private set; }
        /// <summary>Packets the receiver asked for again after this sender had dropped them.</summary>
        public long LostPackets { get; private set; }
        public string Uri { get; private set; }

        private ReceiverSession(Receiver receiver, SessionOptions options, Action<string> log)
        {
            this.receiver = receiver;
            this.options = options;
            this.log = log ?? delegate { };
        }

        public static ReceiverSession Connect(Receiver receiver, SessionOptions options, MediaClock clock,
            ulong? clockId, int ntpPort, uint initialRtpTimestamp, Action<string> log)
        {
            ReceiverSession session = new ReceiverSession(receiver, options, log);
            try
            {
                session.Open(clock, clockId, ntpPort, initialRtpTimestamp);
            }
            catch (Exception error)
            {
                session.Dispose();
                throw new ProtocolException("音响「" + receiver.Instance + "」（" + receiver.Address + ":" + receiver.Port +
                    "）连接失败：" + error.Message);
            }
            return session;
        }

        private void Open(MediaClock clock, ulong? clockId, int ntpPort, uint initialRtpTimestamp)
        {
            IPAddress address = IPAddress.Parse(receiver.Address);
            log("control connection to " + address + ":" + receiver.Port);
            control = new RtspConnection(address, receiver.Port);
            IPAddress local = control.LocalAddress;

            log("reading receiver capabilities");
            Info = Plist.AsDictionary(Plist.Read(control.Get("/info").Body));

            byte[] sessionKey = Pairing.Transient(control, options.Pin, log);

            controlSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            controlSocket.Bind(new IPEndPoint(local, 0));
            audioSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            audioSocket.Bind(new IPEndPoint(local, 0));
            audioSocket.ReceiveTimeout = 1;

            byte[] audioKey = HapCrypto.Derive(sessionKey, "Events-Salt", "Events-Write-Encryption-Key");
            uint ssrc = BinaryPrimitivesNext();
            byte[] sequenceBytes = new byte[2];
            RandomNumberGenerator.Fill(sequenceBytes);
            ushort sequence = (ushort)(sequenceBytes[0] | (sequenceBytes[1] << 8));
            packetizer = new AudioPacketizer(audioKey, sequence, initialRtpTimestamp, ssrc, options.SampleRate);
            Uri = string.Format(CultureInfo.InvariantCulture, "rtsp://{0}/{1}", local, ssrc);

            long eventPort = SetupSession(control, Uri, options, clockId, ntpPort, log);

            if (clockId.HasValue)
            {
                List<object> peerList = new List<object>();
                foreach (string peer in options.PeerAddresses)
                {
                    if (!string.IsNullOrEmpty(peer) && peerList.Count < 8) peerList.Add(peer);
                }
                if (!peerList.Contains(local.ToString())) peerList.Add(local.ToString());
                log("announcing timing peers");
                control.Send("SETPEERS", Uri, PlistHeaders(), Plist.Write(peerList));
            }

            int latencySamples = (int)AudioPacketizer.LatencySamples(options.LatencyMs, options.SampleRate);
            RequestedLatencySamples = latencySamples;
            Dictionary<string, object> stream = new Dictionary<string, object>
            {
                ["audioFormat"] = (long)AudioPacketizer.AudioFormat(false, options.SampleRate),
                ["audioMode"] = "default",
                ["controlPort"] = (long)((IPEndPoint)controlSocket.LocalEndPoint).Port,
                ["ct"] = 1L,
                ["isMedia"] = true,
                ["latencyMin"] = (long)latencySamples,
                ["latencyMax"] = (long)latencySamples,
                ["shk"] = audioKey,
                ["spf"] = (long)AudioPacketizer.FramesPerPacket,
                ["sr"] = (long)options.SampleRate,
                ["type"] = 96L,
                ["supportsDynamicStreamID"] = false,
                ["streamConnectionID"] = (long)ssrc
            };
            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["streams"] = new List<object> { stream }
            };

            log("audio stream setup (buffer " + options.LatencyMs + " ms)");
            Dictionary<string, object> streamReply = Plist.AsDictionary(
                Plist.Read(control.Send("SETUP", Uri, PlistHeaders(), Plist.Write(request)).Body));
            List<object> streams = Plist.AsList(streamReply["streams"]);
            if (streams.Count == 0) throw new ProtocolException("receiver returned no stream");
            Dictionary<string, object> negotiated = Plist.AsDictionary(streams[0]);

            long remoteControlPort = Plist.Integer(negotiated, "controlPort") ?? 0;
            long dataPort = Plist.Integer(negotiated, "dataPort") ?? 0;
            if (remoteControlPort <= 0 || dataPort <= 0)
            {
                throw new ProtocolException("receiver did not return usable media ports");
            }

            audioTarget = new IPEndPoint(address, (int)dataPort);
            timingTarget = new IPEndPoint(address, (int)remoteControlPort);
            audioSocket.Connect(audioTarget);

            long? receiverLatency = Plist.Integer(negotiated, "latencyMin");
            if (receiverLatency.HasValue && receiverLatency.Value > 0 && receiverLatency.Value <= options.SampleRate * 10L)
            {
                NegotiatedLatencySamples = (int)receiverLatency.Value;
                NegotiatedLatencySource = "receiver";
                log("receiver reports latencyMin " + NegotiatedLatencySamples + " samples");
            }
            else
            {
                NegotiatedLatencySamples = latencySamples;
                NegotiatedLatencySource = "requested";
            }
            packetizer.ConfigureRetention(TimeSpan.FromMilliseconds(
                NegotiatedLatencySamples * 1000.0 / options.SampleRate));

            StartEventChannel(address, (int)eventPort, sessionKey);

            byte[][] recordHeaders = new byte[][]
            {
                Encoding.ASCII.GetBytes("Range: npt=0-"),
                Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture,
                    "RTP-Info: seq={0};rtptime={1}", packetizer.Sequence, packetizer.Timestamp))
            };
            log("record");
            control.Send("RECORD", Uri, recordHeaders, null);
            control.Send("FLUSH", Uri, recordHeaders, null);

            feedbackStop = new ManualResetEventSlim(false);
            feedbackWorker = new Thread(() => FeedbackLoop(feedbackStop))
            {
                IsBackground = true,
                Name = "AirStereo feedback"
            };
            feedbackWorker.Start();
            activeSessions[receiver.Address] = this;
        }

        public static bool TrySetVolume(IList<Receiver> members, double decibels, Action<string> log)
        {
            if (members == null || members.Count == 0) return false;
            List<ReceiverSession> sessions = new List<ReceiverSession>();
            List<Receiver> matched = new List<Receiver>();
            foreach (Receiver member in members)
            {
                ReceiverSession session;
                if (activeSessions.TryGetValue(member.Address, out session) && !session.disposed)
                {
                    sessions.Add(session);
                    matched.Add(member);
                }
            }

            // Never fall back to a second RTSP setup while playback is active. A stereo pair can
            // register its two sessions a few milliseconds apart; opening a temporary session
            // for the missing half can make the receiver tear down the media session that is
            // already carrying audio. The caller waits for the pair to become complete.
            if (sessions.Count != members.Count) return false;

            byte[] body = Encoding.ASCII.GetBytes(
                "volume: " + decibels.ToString("0.000000", CultureInfo.InvariantCulture) + "\r\n");
            for (int i = 0; i < sessions.Count; i++)
            {
                sessions[i].SendVolume(body);
                if (log != null) log("  " + matched[i].Instance + " SET_PARAMETER RTSP/1.0 200 OK (播放会话)");
            }
            return true;
        }

        public static bool HasActiveSession(IList<Receiver> members)
        {
            if (members == null) return false;
            foreach (Receiver member in members)
            {
                ReceiverSession session;
                if (activeSessions.TryGetValue(member.Address, out session) && !session.disposed)
                {
                    return true;
                }
            }
            return false;
        }

        private static uint BinaryPrimitivesNext()
        {
            byte[] bytes = new byte[4];
            RandomNumberGenerator.Fill(bytes);
            return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        }

        /// <summary>
        /// The session level SETUP that opens every AirPlay 2 control connection, and the only
        /// way to get a request URI a receiver will apply a <c>SET_PARAMETER</c> to. Returns the
        /// event port the receiver announced; a caller that only wants to change a setting can
        /// ignore it and tear the session down again.
        /// </summary>
        public static long SetupSession(RtspConnection control, string uri, SessionOptions options,
            ulong? clockId, int ntpPort, Action<string> log)
        {
            IPAddress local = control.LocalAddress;
            string sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
            Dictionary<string, object> setup = new Dictionary<string, object>
            {
                ["deviceID"] = options.SenderId,
                ["macAddress"] = options.SenderId,
                ["name"] = options.SenderName,
                ["sessionUUID"] = sessionUuid,
                ["timingProtocol"] = clockId.HasValue ? "PTP" : "NTP",
                ["isMultiSelectAirPlay"] = true,
                ["groupContainsGroupLeader"] = false,
                ["senderSupportsRelay"] = false
            };
            if (!string.IsNullOrEmpty(options.GroupId)) setup["groupUUID"] = options.GroupId;

            if (clockId.HasValue)
            {
                Dictionary<string, object> peerInfo = new Dictionary<string, object>
                {
                    ["ID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                    ["DeviceType"] = 0L,
                    ["ClockID"] = (long)clockId.Value,
                    ["SupportsClockPortMatchingOverride"] = false,
                    ["Addresses"] = new List<object> { local.ToString() }
                };
                setup["timingPeerInfo"] = peerInfo;
                setup["timingPeerList"] = new List<object> { peerInfo };
            }
            else
            {
                setup["timingPort"] = (long)ntpPort;
            }

            log("session setup");
            Dictionary<string, object> setupReply = Plist.AsDictionary(
                Plist.Read(control.Send("SETUP", uri, PlistHeaders(), Plist.Write(setup)).Body));
            long eventPort = Plist.Integer(setupReply, "eventPort") ?? 0;
            if (eventPort <= 0) throw new ProtocolException("receiver did not announce an event port");
            return eventPort;
        }

        private static byte[][] PlistHeaders()
        {
            return new byte[][]
            {
                Encoding.ASCII.GetBytes("Content-Type: application/x-apple-binary-plist")
            };
        }

        private void StartEventChannel(IPAddress address, int port, byte[] sessionKey)
        {
            events = new RtspConnection(address, port);
            events.EnableEncryption(
                HapCrypto.Derive(sessionKey, "Events-Salt", "Events-Read-Encryption-Key"),
                HapCrypto.Derive(sessionKey, "Events-Salt", "Events-Write-Encryption-Key"));
            eventStop = new ManualResetEventSlim(false);
            eventWorker = new Thread(() => EventLoop(eventStop)) { IsBackground = true, Name = "AirStereo events" };
            eventWorker.Start();
        }

        private void EventLoop(ManualResetEventSlim stop)
        {
            while (!stop.IsSet && !closing)
            {
                try
                {
                    RtspMessage message = events.TryReceive(20000);
                    if (message == null) continue;
                    string[] parts = message.FirstLine.Split(' ');
                    string protocol = parts.Length > 2 ? parts[2] : "RTSP/1.0";
                    string cseq = message.Header("CSeq");
                    StringBuilder reply = new StringBuilder();
                    reply.Append(protocol).Append(" 200 OK\r\n");
                    if (cseq != null) reply.Append("CSeq: ").Append(cseq).Append("\r\n");
                    reply.Append("Content-Length: 0\r\nAudio-Latency: 0\r\n\r\n");
                    events.WriteRaw(Encoding.ASCII.GetBytes(reply.ToString()));
                    EventMessages++;
                }
                catch (Exception error)
                {
                    if (!closing && !stop.IsSet)
                        log("event channel closed: 音响「" + receiver.Instance + "」（" +
                            receiver.Address + ":" + receiver.Port + "）: " + error.Message);
                    return;
                }
            }
        }

        public void SendVolume(byte[] body)
        {
            lock (controlLock)
            {
                control.Send("SET_PARAMETER", Uri,
                    new byte[][] { Encoding.ASCII.GetBytes("Content-Type: text/parameters") }, body);
            }
        }

        public void Feedback()
        {
            try
            {
                RtspMessage reply = control.Send("POST", "/feedback", null, null);
                if (!reply.IsSuccess) FeedbackFailures++;
            }
            catch (ProtocolException)
            {
                FeedbackFailures++;
            }
        }

        /// <summary>
        /// Emits one RTP block. Every member is given its media before any member services
        /// retransmissions, so a busy receiver cannot delay another member's audio.
        /// </summary>
        public void SendMedia(byte[] pcm, bool first, ulong nowNanoseconds, ulong? clockId, bool sendTiming)
        {
            // The timing packet maps the timestamp the next audio block carries, so it is
            // emitted first and reads the packetizer before the block advances the timeline.
            if (sendTiming) SendTiming(nowNanoseconds, clockId, first);
            SendAudio(packetizer.Packet(pcm, first));
        }

        /// <summary>Answers retransmission requests. Called on the sending thread.</summary>
        public void Service()
        {
            ServiceRetransmits();
        }

        public void SendTiming(ulong nowNanoseconds, ulong? clockId, bool first)
        {
            byte[] timing = AudioPacketizer.TimingPacket(packetizer.Timestamp,
                (uint)RequestedLatencySamples, nowNanoseconds, clockId, first);
            try
            {
                controlSocket.SendTo(timing, timingTarget);
            }
            catch (SocketException)
            {
                // timing datagrams are best effort
            }
        }

        private void SendAudio(byte[] packet)
        {
            try
            {
                audioSocket.Send(packet);
                ConsecutiveMediaSendFailures = 0;
            }
            catch (SocketException)
            {
                // One UDP send can fail transiently; persistent failures terminate the pair.
                ConsecutiveMediaSendFailures++;
            }
        }

        public void Skip(int packets)
        {
            packetizer.SkipPackets(packets);
        }

        private void ServiceRetransmits()
        {
            try
            {
                // One buffer for the lifetime of the session: this runs on the audio thread,
                // and a few kilobytes of garbage every eight milliseconds is a collection the
                // send cadence eventually has to pay for.
                if (requestBuffer == null) requestBuffer = new byte[256];
                byte[] buffer = requestBuffer;
                while (controlSocket.Poll(0, SelectMode.SelectRead))
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int count = controlSocket.ReceiveFrom(buffer, ref from);
                    // Only the receiver this session belongs to may ask for a retransmission.
                    if (!((IPEndPoint)from).Address.Equals(audioTarget.Address)) continue;
                    if (!AudioPacketizer.ParseRetransmitRequest(buffer, count, out ushort start, out ushort limit))
                    {
                        continue;
                    }
                    RetransmitRequests++;
                    for (int i = 0; i < limit && i < 64; i++)
                    {
                        AudioPacketizer.RetransmitResult result =
                            packetizer.Retransmit((ushort)(start + i), DateTime.UtcNow, out byte[] reply);
                        if (result != AudioPacketizer.RetransmitResult.Packet)
                        {
                            // The receiver noticed a gap and this sender can no longer fill it,
                            // which is exactly the case that is audible as a click.
                            LostPackets++;
                            continue;
                        }
                        try
                        {
                            controlSocket.SendTo(reply, from);
                            RetransmittedPackets++;
                        }
                        catch (SocketException)
                        {
                            break;
                        }
                    }
                }
            }
            catch (SocketException)
            {
                // ignore and try again on the next media tick
            }
        }

        /// <summary>
        /// The receiver wants to hear from the sender every couple of seconds. That request is
        /// a TCP round trip, and running it on the thread that sends audio stalls the media
        /// cadence for however long the receiver takes to answer. At a large buffer nobody
        /// notices; at a small one the receivers see the timeline jump and correct for it, which
        /// is exactly the kind of thing a listener calls noise. So it runs here, on its own
        /// thread, and the audio loop never waits for a control message again.
        /// </summary>
        private void FeedbackLoop(ManualResetEventSlim stop)
        {
            AudioThread.Raise();
            while (!stop.Wait(2000))
            {
                lock (controlLock)
                {
                    if (disposed || closing) return;
                    try
                    {
                        Feedback();
                    }
                    catch (Exception)
                    {
                        if (!closing) FeedbackFailures++;
                    }
                }
            }
        }

        public string Describe()
        {
            string model = Plist.Text(Info, "model") ?? receiver.Model ?? "unknown";
            string source = Plist.Text(Info, "sourceVersion") ?? receiver.SourceVersion ?? "unknown";
            return string.Format(CultureInfo.InvariantCulture,
                "{0} ({1}, AirTunes {2}) -> {3}", receiver.Instance, model, source, audioTarget);
        }

        /// <summary>Mark every member before any TEARDOWN can close its peer's event channel.</summary>
        internal void BeginShutdown()
        {
            closing = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            BeginShutdown();
            disposed = true;
            // Signal the event reader before TEARDOWN, not after the receiver closes it.
            eventStop?.Set();
            activeSessions.TryRemove(receiver.Address, out ReceiverSession ignored);

            feedbackStop?.Set();
            if (feedbackWorker != null && feedbackWorker.IsAlive) feedbackWorker.Join(300);

            try
            {
                lock (controlLock)
                {
                    if (control != null) control.SetTimeout(300);
                    control?.Send("TEARDOWN", Uri, null, null);
                }
            }
            catch (Exception)
            {
                // the receiver may already have dropped the session
            }

            if (eventWorker != null && eventWorker.IsAlive) eventWorker.Join(300);
            events?.Dispose();
            feedbackStop?.Dispose();
            control?.Dispose();
            packetizer?.Dispose();
            try { audioSocket?.Dispose(); } catch (Exception) { }
            try { controlSocket?.Dispose(); } catch (Exception) { }
            eventStop?.Dispose();
        }
    }
}

