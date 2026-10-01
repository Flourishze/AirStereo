using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using AirStereo.Audio;
using AirStereo.Protocol;
using AirStereo.Session;

namespace AirStereo
{
    /// <summary>One discovery sweep: the raw endpoints, the grouped targets, and the counters behind them.</summary>
    public sealed class Discovery
    {
        public List<ReceiverGroup> Groups = new List<ReceiverGroup>();
        public List<Receiver> Receivers = new List<Receiver>();
        public MdnsBrowser.Result Last;
        public int Rounds;
    }

    /// <summary>
    /// How much audio the receiver is asked to hold before it plays, in the shape the window
    /// offers it: four fixed gears plus a free choice.
    /// </summary>
    public enum LatencyMode
    {
        Realtime = 0,
        Normal = 1,
        Buffered = 2,
        Stable = 3,
        Custom = 4
    }

    /// <summary>
    /// The latency presets and the mapping behind the custom slider. A slider laid out
    /// linearly over the whole range would leave the useful bottom end a few pixels wide, so
    /// the position is logarithmic: every position changes the buffer by the same ratio.
    /// </summary>
    public static class LatencyProfile
    {
        public const int MinimumMs = 20;
        public const int MaximumMs = 3000;
        public const int SliderSteps = 1000;

        /// <summary>
        /// The buffer gears, in the shape a stereo pair can actually be driven in. The earlier
        /// 250 ms floor was the smallest buffer that stayed clean while the send loop was still
        /// sleeping on the 15.6 ms system timer; with the pacer fixed, the sender holds its
        /// schedule to about a millisecond and the pair is limited by the receivers instead, so
        /// 120 ms realtime is reachable - which is the same target a shipping AirPlay 2 sender
        /// uses on this hardware. Below that the two receivers start correcting their clocks
        /// against each other and the image goes hollow, which no sender can fix.
        /// </summary>
        public const int RealtimeMs = 120;
        public const int NormalMs = 200;
        public const int BufferedMs = 500;
        public const int StableMs = 1000;

        /// <summary>The buffer a mode asks for; the custom value is ignored by the fixed gears.</summary>
        public static int Resolve(LatencyMode mode, int customMs)
        {
            switch (mode)
            {
                case LatencyMode.Normal: return NormalMs;
                case LatencyMode.Buffered: return BufferedMs;
                case LatencyMode.Stable: return StableMs;
                case LatencyMode.Custom: return Clamp(customMs);
                default: return RealtimeMs;
            }
        }

        public static int Clamp(int milliseconds)
        {
            if (milliseconds < MinimumMs) return MinimumMs;
            if (milliseconds > MaximumMs) return MaximumMs;
            return milliseconds;
        }

        /// <summary>Slider position (0..SliderSteps) to milliseconds.</summary>
        public static int FromSlider(int position)
        {
            if (position < 0) position = 0;
            if (position > SliderSteps) position = SliderSteps;
            double span = Math.Log((double)MaximumMs / MinimumMs);
            return Round(MinimumMs * Math.Exp(span * position / SliderSteps));
        }

        /// <summary>Milliseconds to the slider position that shows them.</summary>
        public static int ToSlider(int milliseconds)
        {
            double span = Math.Log((double)MaximumMs / MinimumMs);
            double position = Math.Log(Clamp(milliseconds) / (double)MinimumMs) / span * SliderSteps;
            return (int)Math.Round(position);
        }

        /// <summary>Rounds to a step someone would have picked by hand: 5, 10 or 50 ms.</summary>
        private static int Round(double milliseconds)
        {
            int step = milliseconds < 100 ? 5 : (milliseconds < 1000 ? 10 : 50);
            return Clamp((int)Math.Round(milliseconds / step) * step);
        }

        /// <summary>
        /// How late the sender may fall behind before it drops media slots instead of trying to
        /// catch up. A bigger buffer means the receiver can simply play the late audio, so the
        /// sender is allowed to be correspondingly less brutal about recovery.
        /// </summary>
        public static double LateLimit(int latencyMs)
        {
            double half = latencyMs * 0.5;
            if (half < 60.0) return 60.0;
            if (half > 500.0) return 500.0;
            return half;
        }

        public static bool IsFixed(LatencyMode mode)
        {
            return mode != LatencyMode.Custom;
        }

        /// <summary>Mode names as the command line spells them.</summary>
        public static bool TryParse(string name, out LatencyMode mode)
        {
            mode = LatencyMode.Realtime;
            if (string.IsNullOrEmpty(name)) return false;
            switch (name.Trim().ToLowerInvariant())
            {
                case "realtime":
                case "real-time":
                case "live": mode = LatencyMode.Realtime; return true;
                case "normal":
                case "standard": mode = LatencyMode.Normal; return true;
                case "buffered":
                case "buffer": mode = LatencyMode.Buffered; return true;
                case "stable":
                case "safe": mode = LatencyMode.Stable; return true;
                case "custom": mode = LatencyMode.Custom; return true;
                default: return false;
            }
        }

        public static string ModeName(LatencyMode mode)
        {
            switch (mode)
            {
                case LatencyMode.Normal: return "normal";
                case LatencyMode.Buffered: return "buffered";
                case LatencyMode.Stable: return "stable";
                case LatencyMode.Custom: return "custom";
                default: return "realtime";
            }
        }

        public static string DisplayName(LatencyMode mode)
        {
            switch (mode)
            {
                case LatencyMode.Normal: return "标准流";
                case LatencyMode.Buffered: return "缓冲流";
                case LatencyMode.Stable: return "稳定流";
                case LatencyMode.Custom: return "自定义";
                default: return "实时流";
            }
        }
    }

    /// <summary>What to send and how. Kind is one of loopback, tone, pattern or wav.</summary>
    public sealed class PlayRequest
    {
        public string Kind = "loopback";
        public string WavPath;
        /// <summary>0 asks the source for its own native rate.</summary>
        public int SampleRate;
        /// <summary>
        /// Which buffer gear to use. Custom reads CustomLatencyMs. The default is the middle
        /// gear, which is the value a shipping AirPlay 2 sender also defaults to: the realtime
        /// gear is reachable, but it leaves a two device stereo pair only just enough room to
        /// correct its own clocks.
        /// </summary>
        public LatencyMode Mode = LatencyMode.Normal;
        public int CustomLatencyMs = LatencyProfile.NormalMs;
        /// <summary>
        /// How often the receiver is told where the timeline is, in milliseconds, or 0 to let
        /// the streamer choose. Ten a second is the value every working sender uses.
        /// </summary>
        public int SyncIntervalMs = SessionOptions.DefaultSyncMs;
        public double Gain = 1.0;
        /// <summary>Optional live EQ and per-channel calibration; null means untouched audio.</summary>
        public AudioProfileController Calibration;
        /// <summary>Route channel L to member 0 and R to member 1 using the shared clock.</summary>
        public bool SplitStereo;
        public bool SwapChannels;
        /// <summary>Attenuate the opposite side; -100 = left only, +100 = right only.</summary>
        public int Balance;
        public LivePlaybackControl LiveControl;
        /// <summary>0 streams until the stop event is set.</summary>
        public int DurationMs;
        public bool UsePtp = true;
        public bool HandshakeOnly;
        public string Pin = Srp.DefaultPin;

        /// <summary>The receiver buffer this request asks for, in milliseconds.</summary>
        public int LatencyMs
        {
            get { return LatencyProfile.Resolve(Mode, CustomLatencyMs); }
        }
    }

    /// <summary>What a finished (or stopped) stream actually did.</summary>
    public sealed class PlayResult
    {
        public string Source = "";
        public int SampleRate;
        /// <summary>The buffer that was asked for, in milliseconds.</summary>
        public int LatencyMs;
        public long Packets;
        public double Seconds;
        public long LateRecoveries;
        public long SkippedPackets;
        public double WorstLatenessMs;
        public long RetransmitRequests;
        public long RetransmittedPackets;
        public long LostPackets;
        /// <summary>Datagrams the receivers sent back to our PTP master.</summary>
        public long PtpPackets;
        /// <summary>Capture side health: queue depth, stalls, lost frames.</summary>
        public string SourceStats = "";
        /// <summary>Worst drift behind the send schedule, in milliseconds.</summary>
        public double PacingLatenessMs;
        /// <summary>Worst deviation of one packet gap from the nominal gap, in milliseconds.</summary>
        public double PacingJitterMs;

        public string Summary()
        {
            string text = Source + " · " + SampleRate.ToString(CultureInfo.InvariantCulture) + " Hz · " +
                "缓冲 " + LatencyMs.ToString(CultureInfo.InvariantCulture) + " ms · " +
                Packets.ToString(CultureInfo.InvariantCulture) + " packets · " +
                Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            if (RetransmitRequests > 0)
            {
                text += " · 重传 " + RetransmittedPackets.ToString(CultureInfo.InvariantCulture) +
                    "/" + RetransmitRequests.ToString(CultureInfo.InvariantCulture);
                if (LostPackets > 0)
                {
                    text += "（丢 " + LostPackets.ToString(CultureInfo.InvariantCulture) + "）";
                }
            }
            if (SourceStats.Length > 0) text += " · " + SourceStats;
            return text;
        }
    }

    /// <summary>
    /// The usable surface of the sender, shared by the command line and the window.
    /// Everything here is blocking; the caller decides which thread it runs on.
    /// </summary>
    public static class AirStereoApi
    {
        /// <summary>
        /// Browses multicast DNS until the picture looks complete. A receiver that announces a
        /// group id but has no sibling in the record set means the other half of the pair is
        /// still missing, so the browse repeats and the rounds are merged. Without this a lost
        /// packet quietly turns a stereo pair into a target that plays from one speaker.
        /// </summary>
        public static Discovery Discover(int firstSeconds)
        {
            Discovery found = new Discovery();
            List<MdnsRecord> records = new List<MdnsRecord>();

            for (int round = 0; round < 3; round++)
            {
                MdnsBrowser.Result result = new MdnsBrowser().Browse(ReceiverCatalog.ServiceTypes,
                    TimeSpan.FromSeconds(round == 0 ? firstSeconds : 4));
                records.AddRange(result.Records);
                found.Last = result;
                found.Receivers = ReceiverCatalog.Build(records);
                found.Groups = ReceiverCatalog.Group(found.Receivers);
                found.Rounds = round + 1;

                bool incomplete = false;
                foreach (ReceiverGroup group in found.Groups)
                {
                    if (group.Members.Count != 1) continue;
                    Receiver only = group.Members[0];
                    // A receiver that claims a group identity but has no sibling yet is the
                    // signature of a packet that has not arrived.
                    if (group.GroupId.Length > 0 || !string.IsNullOrEmpty(only.GroupName)) incomplete = true;
                }
                if (!incomplete) break;
            }
            return found;
        }

        /// <summary>Receiver volume as a percentage, from the dB value /info reports.</summary>
        public static int PercentFromDecibels(double decibels)
        {
            if (double.IsNaN(decibels) || decibels >= 0.0) return 100;
            if (decibels <= -30.0) return 0;
            return (int)Math.Round((decibels + 30.0) / 0.3);
        }

        /// <summary>The dB value that asks a receiver for a percentage.</summary>
        public static double DecibelsFromPercent(int percent)
        {
            return percent == 0 ? -144.0 : percent * 0.3 - 30.0;
        }

        /// <summary>
        /// Reads the volume a receiver currently holds, in percent, or -1 when it does not
        /// report one. This is the only honest check that a volume write did anything.
        /// </summary>
        public static int ReadVolume(Receiver member, string pin, Action<string> log)
        {
            using (RtspConnection connection = new RtspConnection(
                IPAddress.Parse(member.Address), member.Port))
            {
                connection.Get("/info");
                Pairing.Transient(connection, pin, log);
                return ReadVolume(connection);
            }
        }

        private static int ReadVolume(RtspConnection connection)
        {
            Dictionary<string, object> info = Plist.AsDictionary(Plist.Read(connection.Get("/info").Body));
            object value;
            if (!info.TryGetValue("initialVolume", out value) || value == null) return -1;
            if (value is double) return PercentFromDecibels((double)value);
            if (value is long) return PercentFromDecibels((long)value);
            if (value is float) return PercentFromDecibels((float)value);
            return -1;
        }

        /// <summary>
        /// Sets the volume of every member of one target.
        ///
        /// A receiver only honours <c>SET_PARAMETER volume:</c> inside an RTSP session, which is
        /// why a request on a freshly paired connection is answered politely and then ignored:
        /// the session the request names does not exist, so there is nothing to apply it to.
        /// Each member therefore gets its own short session here - setup, volume, teardown - and
        /// nothing is stored on disk, because pairing is per connection.
        ///
        /// The connection that sent the request also keeps the volume it last reported, so its
        /// own read back lags one command behind. The stored value is only visible on a new
        /// connection, and that is what the result lines report.
        /// </summary>
        public static List<string> SetVolume(ReceiverGroup group, int percent, string pin, Action<string> log)
        {
            if (percent < 0 || percent > 100) throw new ProtocolException("volume must be between 0 and 100");
            if (string.IsNullOrEmpty(pin)) pin = Srp.DefaultPin;
            Action<string> say = log ?? delegate { };
            double decibels = DecibelsFromPercent(percent);
            List<string> results = new List<string>();

            // Reuse the active playback sessions. A second RTSP session can interrupt media.
            // The two halves of a stereo pair register a few milliseconds apart, so wait briefly
            // for the complete pair instead of opening a temporary session during playback.
            for (int attempt = 0; attempt < 12; attempt++)
            {
                if (ReceiverSession.TrySetVolume(group.Members, decibels, say))
                {
                    foreach (Receiver member in group.Members)
                    {
                        results.Add(member.Instance + " -> " + percent.ToString(CultureInfo.InvariantCulture) + "%");
                    }
                    return results;
                }
                if (!ReceiverSession.HasActiveSession(group.Members)) break;
                Thread.Sleep(50);
            }

            if (ReceiverSession.HasActiveSession(group.Members))
            {
                throw new ProtocolException("播放会话尚未完全建立，请稍后再应用音量");
            }

            SessionOptions options = new SessionOptions { Pin = pin, GroupId = group.GroupId };
            foreach (string address in group.Members.ConvertAll(member => member.Address))
            {
                options.PeerAddresses.Add(address);
            }

            // A receiver only applies an absolute volume inside a session, and a session needs a
            // timing peer before it will start one. The responder is a few hundred bytes of state,
            // so it is cheaper to give the receiver what it asks for than to argue with it.
            MediaClock clock = new MediaClock();
            List<IPAddress> peers = new List<IPAddress>();
            foreach (Receiver member in group.Members) peers.Add(IPAddress.Parse(member.Address));
            using (NtpResponder timing = new NtpResponder(peers, clock))
            {
                foreach (Receiver member in group.Members)
                {
                    int before;
                    using (RtspConnection connection = new RtspConnection(
                        IPAddress.Parse(member.Address), member.Port))
                    {
                        connection.Get("/info");
                        Pairing.Transient(connection, pin, say);

                        before = ReadVolume(connection);
                        string uri = "rtsp://" + connection.LocalAddress + "/" + NextSessionId();
                        ReceiverSession.SetupSession(connection, uri, options, null, timing.Port, say);

                        byte[] body = Encoding.ASCII.GetBytes(
                            "volume: " + decibels.ToString("0.000000", CultureInfo.InvariantCulture) + "\r\n");
                        RtspMessage reply = connection.Send("SET_PARAMETER", uri,
                            new byte[][] { Encoding.ASCII.GetBytes("Content-Type: text/parameters") }, body);

                        // Read once on the sending connection to see that the receiver answered,
                        // knowing that the number it shows here is its cache, not the new value.
                        int echoed = ReadVolume(connection);
                        connection.Send("TEARDOWN", uri, null, null);
                        say("  " + member.Instance + " SET_PARAMETER " + reply.FirstLine +
                            " (the sending connection still shows " + DescribeVolume(echoed) + ")");
                    }

                    int after = VerifyVolume(member, pin, percent);
                    say("  " + member.Instance + ": " + DescribeVolume(before) + " -> " +
                        DescribeVolume(after) + ", asked " + percent.ToString(CultureInfo.InvariantCulture) +
                        "% (" + decibels.ToString("0.0", CultureInfo.InvariantCulture) + " dB)" +
                        (after == percent ? "" : "  <- the receiver still reports " + DescribeVolume(after)));
                    results.Add(member.Instance + " " + DescribeVolume(before) + " -> " + DescribeVolume(after));
                }
            }
            return results;
        }

        /// <summary>
        /// Reads the stored volume back on a fresh connection until it matches, or gives up after
        /// about 0.7 s and returns whatever was last seen. A receiver can take a moment to commit
        /// a write, so a single immediate read is not a trustworthy check.
        /// </summary>
        private static int VerifyVolume(Receiver member, string pin, int percent)
        {
            int seen = -1;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Thread.Sleep(attempt == 0 ? 120 : 150);
                seen = ReadVolume(member, pin, null);
                if (seen == percent) return seen;
            }
            return seen;
        }

        /// <summary>A percentage as a person reads it, with "unknown" for a value nobody reported.</summary>
        public static string DescribeVolume(int percent)
        {
            return percent < 0 ? "unknown" : percent.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private static uint NextSessionId()
        {
            byte[] bytes = new byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        }

        /// <summary>
        /// Streams to every member of one target from a single clock of its own. Returns what the
        /// stream did; throws if the handshake or the source fails.
        /// </summary>
        public static PlayResult Play(ReceiverGroup group, PlayRequest request,
            ManualResetEventSlim stop, Action<string> log)
        {
            if (group == null) throw new ArgumentNullException("group");
            if (request == null) throw new ArgumentNullException("request");
            if (stop == null) stop = new ManualResetEventSlim(false);
            Action<string> say = log ?? delegate { };

            int rate = request.SampleRate;
            if (rate == 0)
            {
                rate = 44100;
                if (request.Kind == "loopback")
                {
                    // Capturing at the endpoint's own rate avoids resampling the system mix.
                    LoopbackSource.Description device = LoopbackSource.DescribeDefault();
                    if (device.SampleRate == 44100 || device.SampleRate == 48000) rate = device.SampleRate;
                }
            }
            if (rate != 44100 && rate != 48000) throw new ProtocolException("the sample rate must be 44100 or 48000");
            if (request.Gain < 0.0 || request.Gain > 4.0) throw new ProtocolException("gain must be between 0 and 4");
            if (request.Balance < -100 || request.Balance > 100) throw new ProtocolException("balance must be between -100 and 100");
            if (request.SplitStereo && (group.Members.Count != 2 ||
                string.Equals(group.Members[0].Identity, group.Members[1].Identity, StringComparison.OrdinalIgnoreCase)))
                throw new ProtocolException("stereo routing requires two distinct receivers");
            if (group.StereoPairId.Length > 0 && !group.IsStereoPair)
                throw new ProtocolException("原生立体声对成员信息不完整，请刷新设备后再播放。");
            if (request.SplitStereo && group.Members.Exists(member => member.StereoPairId.Length > 0))
                throw new ProtocolException("原生立体声对成员不能作为两只独立音响拆分连接，请刷新后选择配对目标。");
            if (request.SplitStereo && !request.UsePtp)
                throw new ProtocolException("双设备立体声必须使用共同的 PTP 时钟。");

            SessionOptions session = new SessionOptions
            {
                SampleRate = rate,
                LatencyMs = request.LatencyMs,
                SyncMs = request.SyncIntervalMs,
                UsePtp = request.UsePtp,
                Pin = string.IsNullOrEmpty(request.Pin) ? Srp.DefaultPin : request.Pin,
                GroupId = group.GroupId,
                SplitStereo = request.SplitStereo,
                SwapChannels = request.SwapChannels,
                Balance = group.Members.Count == 1 ? 0 : request.Balance,
                LiveControl = request.LiveControl
            };

            PlayResult result = new PlayResult();
            result.SampleRate = rate;
            result.LatencyMs = session.LatencyMs;
            AudioSource source = null;
            try
            {
                if (request.Kind == "wav")
                {
                    source = WavSource.Open(request.WavPath, session.SampleRate);
                    result.Source = Path.GetFileName(request.WavPath);
                    say("source: " + request.WavPath);
                }
                else if (request.Kind == "tone")
                {
                    source = new ToneSource(session.SampleRate);
                    result.Source = "440 Hz left / 660 Hz right";
                    say("source: 440 Hz left / 660 Hz right");
                }
                else if (request.Kind == "left-check" || request.Kind == "right-check")
                {
                    bool left = request.Kind == "left-check";
                    source = new ToneSource(session.SampleRate, left ? 440.0 : 0.0, left ? 0.0 : 660.0);
                    result.Source = left ? "left channel test" : "right channel test";
                    say("source: " + result.Source);
                }
                else if (request.Kind == "pattern")
                {
                    source = new ChannelCheckSource(session.SampleRate);
                    result.Source = "left only / right only / both, 4 s each";
                    say("source: 4 s left only, 4 s right only, 4 s both, repeating");
                }
                else if (request.Kind == "sweep")
                {
                    source = new MonoSweepSource(session.SampleRate);
                    result.Source = "100 Hz to 4 kHz sweep on both channels";
                    say("source: the same sweep through both speakers; a pair that is not " +
                        "aligned combs it into a warble");
                }
                else
                {
                    LoopbackSource loopback = new LoopbackSource(session.SampleRate);
                    source = loopback;
                    result.Source = loopback.DeviceName;
                    say("capturing: " + loopback.DeviceName + " at " + loopback.CaptureRate +
                        " Hz, " + loopback.CaptureChannels + " channel(s) -> " +
                        session.SampleRate + " Hz stereo");
                }

                if (request.Gain != 1.0) source = new GainSource(source, request.Gain);
                if (request.LiveControl != null) source = new LivePlaybackSource(source, request.LiveControl);
                if (request.Calibration != null) source = new CalibratedAudioSource(source, request.Calibration);

                Streamer streamer = new Streamer(group.Members, session, say);
                streamer.Run(source, request.DurationMs, stop, request.HandshakeOnly);

                result.Packets = streamer.Packets;
                result.Seconds = streamer.SecondsSent;
                result.LateRecoveries = streamer.LateRecoveries;
                result.SkippedPackets = streamer.SkippedPackets;
                result.WorstLatenessMs = streamer.WorstLatenessMilliseconds;
                result.RetransmitRequests = streamer.RetransmitRequests;
                result.RetransmittedPackets = streamer.RetransmittedPackets;
                result.LostPackets = streamer.LostPackets;
                result.PtpPackets = streamer.PtpReceived;
                result.SourceStats = streamer.SourceStats;
                result.PacingLatenessMs = streamer.PacingLatenessMs;
                result.PacingJitterMs = streamer.PacingJitterMs;
                return result;
            }
            finally
            {
                (source as IDisposable)?.Dispose();
            }
        }
    }
}
