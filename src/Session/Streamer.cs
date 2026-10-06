using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading;
using AirStereo.Audio;
using AirStereo.Protocol;

namespace AirStereo.Session
{
    /// <summary>
    /// Drives every member of one logical target (a single speaker, or both halves of a
    /// stereo pair) from one shared clock and one shared sample timeline.
    /// </summary>
    public sealed class Streamer
    {
        /// <summary>Timing packets never go out slower than this; the profile assumes ~10 a second.</summary>
        private const double SlowestSyncMilliseconds = 100.0;
        /// <summary>...and never faster than this, however small the buffer gets.</summary>
        private const double FastestSyncMilliseconds = 25.0;

        private readonly List<Receiver> targets;
        private readonly SessionOptions options;
        private readonly Action<string> log;
        private long lateRecoveries;
        private long skippedPackets;
        private double worstLatenessMs;
        private long packets;
        private double secondsSent;
        private long retransmitRequests;
        private long retransmittedPackets;
        private long lostPackets;
        private long ptpReceived;
        private string sourceStats = "";
        private double pacingLatenessMs;
        private double pacingJitterMs;

        public Streamer(List<Receiver> targets, SessionOptions options, Action<string> log)
        {
            if (options.SourceSilenceDisconnectMilliseconds <= 0)
                throw new ProtocolException("source silence disconnect milliseconds must be positive");
            this.targets = targets;
            this.options = options;
            this.log = log ?? delegate { };
        }

        public long LateRecoveries { get { return lateRecoveries; } }
        public long SkippedPackets { get { return skippedPackets; } }
        public double WorstLatenessMilliseconds { get { return worstLatenessMs; } }
        /// <summary>Media packets handed to the receivers.</summary>
        public long Packets { get { return packets; } }
        /// <summary>Audio actually sent, in seconds of the media timeline.</summary>
        public double SecondsSent { get { return secondsSent; } }
        /// <summary>Retransmission requests from every member of the target.</summary>
        public long RetransmitRequests { get { return retransmitRequests; } }
        public long RetransmittedPackets { get { return retransmittedPackets; } }
        /// <summary>Packets a receiver asked for again after this sender had already dropped them.</summary>
        public long LostPackets { get { return lostPackets; } }
        /// <summary>Datagrams the receivers sent back to the PTP master: proof they use our clock.</summary>
        public long PtpReceived { get { return ptpReceived; } }
        /// <summary>What the audio source had to say about its own health at the end.</summary>
        public string SourceStats { get { return sourceStats; } }
        /// <summary>How far behind its schedule the send loop drifted, at worst, in milliseconds.</summary>
        public double PacingLatenessMs { get { return pacingLatenessMs; } }
        /// <summary>Worst deviation of the gap between two packets from the nominal one.</summary>
        public double PacingJitterMs { get { return pacingJitterMs; } }

        /// <summary>Runs the whole session. Returns 0 on success.</summary>
        public int Run(AudioSource source, int durationMs, ManualResetEventSlim stop, bool handshakeOnly)
        {
            MediaClock clock = new MediaClock();
            List<IPAddress> peers = new List<IPAddress>();
            foreach (Receiver target in targets) peers.Add(IPAddress.Parse(target.Address));

            PtpMaster ptp = null;
            NtpResponder ntp = null;
            ulong? clockId = null;
            int ntpPort = 0;

            if (options.UsePtp)
            {
                try
                {
                    ptp = new PtpMaster(peers, clock);
                    clockId = ptp.ClockId;
                    log("PTP master clock 0x" + ptp.ClockId.ToString("x16", CultureInfo.InvariantCulture) +
                        " on UDP 319/320");
                }
                catch (Exception error)
                {
                    if (options.SplitStereo)
                        throw new ProtocolException("two-device stereo needs PTP timing; " + error.Message);
                    log("PTP unavailable (" + error.Message + "), falling back to NTP timing");
                }
            }
            if (ptp == null)
            {
                ntp = new NtpResponder(peers, clock);
                ntpPort = ntp.Port;
                log("NTP timing responder on UDP " + ntpPort);
            }

            uint initialRtp = NextUInt32();
            List<ReceiverSession> sessions = new List<ReceiverSession>();
            // Every member is told about the whole timing domain, not just about this PC.
            options.PeerAddresses = new List<string>();
            foreach (Receiver target in targets) options.PeerAddresses.Add(target.Address);
            try
            {
                foreach (Receiver target in targets)
                {
                    sessions.Add(ReceiverSession.Connect(target, options, clock, clockId, ntpPort, initialRtp, log));
                }

                if (options.SplitStereo)
                {
                    int difference = Math.Abs(sessions[0].NegotiatedLatencySamples -
                        sessions[1].NegotiatedLatencySamples);
                    if (difference > AudioPacketizer.FramesPerPacket)
                        throw new ProtocolException("receivers negotiated different playback buffers (" +
                            difference + " samples); stereo pairing stopped to prevent channel offset");
                    log("stereo timing: common RTP origin and PTP clock; receiver buffer difference " +
                        difference + " sample(s) (hardware acoustic alignment still requires listening test)");
                }
                foreach (ReceiverSession session in sessions) log("connected: " + session.Describe());

                if (handshakeOnly)
                {
                    log("handshake complete; no audio was sent");
                    return 0;
                }

                int code;
                ManualResetEventSlim streamDone = new ManualResetEventSlim(false);
                Thread stopWorker = new Thread(delegate ()
                {
                    while (!streamDone.Wait(25))
                    {
                        if (stop.IsSet)
                        {
                            foreach (ReceiverSession session in sessions) session.BeginShutdown();
                            source.Stop();
                            return;
                        }
                    }
                });
                stopWorker.IsBackground = true;
                stopWorker.Name = "air-stereo-stop-watch";
                stopWorker.Start();
                try
                {
                    code = Stream(sessions, clock, clockId, source, durationMs, stop, ptp);
                }
                finally
                {
                    streamDone.Set();
                    if (stopWorker.IsAlive) stopWorker.Join(500);
                    streamDone.Dispose();
                }

                // A receiver only asks for a retransmission when it noticed a gap, so these
                // counters say more about the network than any sender side timing ever will.
                foreach (ReceiverSession session in sessions)
                {
                    retransmitRequests += session.RetransmitRequests;
                    retransmittedPackets += session.RetransmittedPackets;
                    lostPackets += session.LostPackets;
                }
                if (retransmitRequests > 0)
                {
                    log("retransmissions: " + retransmitRequests + " request(s), " +
                        retransmittedPackets + " recovered, " + lostPackets + " lost");
                    foreach (ReceiverSession session in sessions)
                    {
                        if (session.RetransmitRequests == 0) continue;
                        log("  " + session.Describe() + " asked " + session.RetransmitRequests +
                            " time(s), lost " + session.LostPackets);
                    }
                }
                return code;
            }
            finally
            {
                // Native pairs can close both event channels when the first member is torn down.
                // Mark all members first so normal session cleanup is not recorded as a fault.
                foreach (ReceiverSession session in sessions) session.BeginShutdown();
                foreach (ReceiverSession session in sessions) session.Dispose();
                ptp?.Dispose();
                ntp?.Dispose();
            }
        }

        private int Stream(List<ReceiverSession> sessions, MediaClock clock, ulong? clockId,
            AudioSource source, int durationMs, ManualResetEventSlim stop, PtpMaster ptp)
        {
            // This thread has to wake on an eight millisecond boundary; the ordinary scheduler
            // is happy to let it wait.
            AudioThread.Raise();
            int frames = AudioPacketizer.FramesPerPacket;
            short[] block = new short[frames * 2];
            // Reused by every packet: the send loop must not allocate, because a collection
            // here is a stall the receivers see as the timeline moving under them.
            byte[] pcm = new byte[frames * 2 * 2];
            byte[] leftPcm = options.SplitStereo ? new byte[pcm.Length] : null;
            byte[] rightPcm = options.SplitStereo ? new byte[pcm.Length] : null;
            ContinuousSilencePolicy silencePolicy = new ContinuousSilencePolicy(
                options.SourceSilenceDisconnectMilliseconds, options.SilenceMode, block.Length);
            Stopwatch clockWatch = Stopwatch.StartNew();
            double durationMilliseconds = durationMs > 0 ? durationMs : double.PositiveInfinity;
            long sentFrames = 0;
            long packets = 0;
            double nextSyncMilliseconds = 0.0;
            double nextReportMilliseconds = 1000.0;
            bool first = true;
            // Two different things a listener can hear. Lateness is how far behind its own
            // schedule the sender has drifted; interval deviation is the short term jitter
            // between one packet and the next, which is what a receiver tracking our clock
            // actually has to smooth out.
            double lastSendMilliseconds = -1.0;
            double nominalInterval = frames * 1000.0 / source.SampleRate;
            double latenessPeak = 0.0;
            double intervalPeak = 0.0;
            long lateSlots = 0;
            long stalledSlots = 0;
            double worstGapMilliseconds = 0.0;
            long slowWaits = 0;
            double worstWaitOvershoot = 0.0;
            long slowSends = 0;
            double worstSendMilliseconds = 0.0;
            HashSet<ReceiverSession> warnedFeedback = new HashSet<ReceiverSession>();
            bool warnedTiming = false;

            // What the receiver settled on is what actually protects the audio, so the recovery
            // threshold follows the negotiated buffer rather than the requested one.
            double bufferMilliseconds = sessions[0].NegotiatedLatencySamples * 1000.0 / source.SampleRate;
            double lateLimitMilliseconds = LatencyProfile.LateLimit((int)Math.Round(bufferMilliseconds));

            // Each timing packet re-defines where the receiver should be playing, so a steady
            // cadence matters more than a fast one: ten a second is what the reference engine
            // uses at every buffer size, and it gives a receiver that is tracking our clock the
            // same amount of new information whether the buffer is 250 ms or 3 s.
            double syncIntervalMilliseconds = options.SyncMs > 0
                ? options.SyncMs
                : bufferMilliseconds / 5.0;
            if (syncIntervalMilliseconds < FastestSyncMilliseconds) syncIntervalMilliseconds = FastestSyncMilliseconds;
            if (syncIntervalMilliseconds > SlowestSyncMilliseconds) syncIntervalMilliseconds = SlowestSyncMilliseconds;

            // The capture ran all through the handshake, so its queue holds whatever the desktop
            // was playing while the session was set up. Only the newest audio is worth sending.
            source.Prepare();

            log("streaming " + (options.UseAlac ? "ALAC" : "PCM fallback") + " " + source.SampleRate +
                " Hz, continuous media, silence " + options.SilenceMode + ", source-silence timeout " +
                options.SourceSilenceDisconnectMilliseconds + " ms, buffer " +
                bufferMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms (" +
                sessions[0].NegotiatedLatencySource + "), pacing " +
                (PrecisionWait.HighResolution ? "high resolution timer" : "system timer") +
                ", sync every " + syncIntervalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms");

            while (!stop.IsSet && clockWatch.Elapsed.TotalMilliseconds < durationMilliseconds)
            {
                double dueMilliseconds = sentFrames * 1000.0 / source.SampleRate;
                double now = clockWatch.Elapsed.TotalMilliseconds;
                if (now < dueMilliseconds)
                {
                    double remaining = dueMilliseconds - now;
                    if (remaining > 0.1)
                    {
                        // Attribute a stall before fixing it: the wait overshooting and the
                        // socket call blocking look identical from the outside.
                        PrecisionWait.Sleep(remaining);
                        double overshoot = clockWatch.Elapsed.TotalMilliseconds - now - remaining;
                        if (overshoot > 2.0) slowWaits++;
                        if (overshoot > worstWaitOvershoot) worstWaitOvershoot = overshoot;
                    }
                    continue;
                }

                double lateness = now - dueMilliseconds;
                if (lateness > worstLatenessMs) worstLatenessMs = lateness;
                if (lateness > latenessPeak) latenessPeak = lateness;
                if (lateness > 2.0) lateSlots++;
                if (lateness > 8.0) stalledSlots++;
                if (lastSendMilliseconds >= 0.0)
                {
                    double deviation = Math.Abs(now - lastSendMilliseconds - nominalInterval);
                    if (deviation > intervalPeak) intervalPeak = deviation;
                    double gap = now - lastSendMilliseconds;
                    if (gap > worstGapMilliseconds) worstGapMilliseconds = gap;
                }
                lastSendMilliseconds = now;
                // Read before deciding whether to skip. A live loopback source can
                // be Starved when the render endpoint is idle; that is source silence,
                // not a slow sender. The block is already zero-filled by Read(), and
                // must still occupy this media slot so the receiver buffer and RTP
                // timeline continue advancing.
                source.Read(block, frames);
                AudioReadActivity readActivity = source.LastReadActivity;
                bool disconnectForSilence = silencePolicy.Process(block, source.IsRealtime,
                    readActivity, frames, source.SampleRate);

                if (lateness > lateLimitMilliseconds && readActivity != AudioReadActivity.Starved)
                {
                    long behindFrames = (long)((now - dueMilliseconds) * source.SampleRate / 1000.0);
                    int skip = (int)(behindFrames / frames);
                    if (skip > 0)
                    {
                        foreach (ReceiverSession session in sessions) session.Skip(skip);
                        sentFrames += (long)skip * frames;
                        skippedPackets += skip;
                        lateRecoveries++;
                        log("sender fell behind, skipped " + skip + " media slots");
                    }
                    continue;
                }
                if (options.SwapChannels) StereoRouting.SwapInPlace(block);
                if (options.LiveControl != null) options.LiveControl.ApplyBalance(block);
                else if (options.Balance != 0) StereoRouting.ApplyBalanceInPlace(block, options.Balance);

                if (options.SplitStereo) StereoRouting.Split(block, leftPcm, rightPcm, 0);
                else AudioSource.ToBigEndianPcm(block, block.Length, pcm);
                ulong nowNanoseconds = clock.NowNanoseconds;
                bool sync = now >= nextSyncMilliseconds;
                if (sync) nextSyncMilliseconds = now + syncIntervalMilliseconds;
                for (int member = 0; member < sessions.Count; member++)
                {
                    byte[] channel = options.SplitStereo ? (member == 0 ? leftPcm : rightPcm) : pcm;
                    sessions[member].SendMedia(channel, first, nowNanoseconds, clockId, sync);
                    CheckReceiverHealth(targets[member].Instance, sessions[member].ConsecutiveMediaSendFailures, 0, targets.Count > 1);
                }
                foreach (ReceiverSession session in sessions)
                {
                    session.Service();
                    CheckReceiverHealth(session.Describe(), session.ConsecutiveMediaSendFailures, session.FeedbackFailures, targets.Count > 1);
                }
                double sendCost = clockWatch.Elapsed.TotalMilliseconds - now;
                if (sendCost > 2.0) slowSends++;
                if (sendCost > worstSendMilliseconds) worstSendMilliseconds = sendCost;

                sentFrames += frames;
                packets++;
                first = false;
                if (disconnectForSilence)
                {
                    log("source silence confirmed for " + options.SourceSilenceDisconnectMilliseconds +
                        " ms: final media packet sent; graceful TEARDOWN");
                    break;
                }

                if (now >= nextReportMilliseconds)
                {
                    nextReportMilliseconds = now + 1000.0;
                    foreach (ReceiverSession session in sessions)
                    {
                        CheckReceiverHealth(session.Describe(), 0, session.FeedbackFailures, targets.Count > 1);
                        if (session.FeedbackFailures >= 3 && warnedFeedback.Add(session))
                            log("warning: " + session.Describe() +
                                " has repeated control feedback failures; check network/device and stop if unsynchronized");
                    }
                    string health = source.Stats();
                    string timingHealth = "";
                    if (options.SplitStereo && ptp != null)
                    {
                        long firstReplies = ptp.ResponsesFrom(targets[0].Address);
                        long secondReplies = ptp.ResponsesFrom(targets[1].Address);
                        timingHealth = ", PTP replies L/R " + firstReplies + "/" + secondReplies;
                        if (now >= 5000 && (firstReplies == 0 || secondReplies == 0) && !warnedTiming)
                        {
                            warnedTiming = true;
                            log("warning: a receiver has not replied to PTP; shared-clock synchronization cannot be verified");
                        }
                    }
                    log(packets + " packets, " +
                        (sentFrames / (double)source.SampleRate).ToString("0", CultureInfo.InvariantCulture) +
                        " s sent, level " + Level(block).ToString("0.00", CultureInfo.InvariantCulture) +
                        ", pacing " + lateSlots + " late / " + stalledSlots + " stalled slots, " +
                        "worst gap " + worstGapMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms" +
                        ", work " + worstSendMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) +
                        " ms (" + slowSends + " slow), wait overshoot " +
                        worstWaitOvershoot.ToString("0.0", CultureInfo.InvariantCulture) +
                        " ms (" + slowWaits + " slow)" +
                        (health.Length > 0 ? ", " + health : "") + timingHealth);
                    lateSlots = 0;
                    stalledSlots = 0;
                    worstGapMilliseconds = 0.0;
                    slowSends = 0;
                    slowWaits = 0;
                    worstSendMilliseconds = 0.0;
                    worstWaitOvershoot = 0.0;
                }
            }

            log("sent " + packets + " media packets (" +
                (sentFrames / (double)source.SampleRate).ToString("0.0", CultureInfo.InvariantCulture) + " s of audio)");
            pacingLatenessMs = latenessPeak;
            pacingJitterMs = intervalPeak;
            log("pacing: worst lateness " + latenessPeak.ToString("0.0", CultureInfo.InvariantCulture) +
                " ms, worst gap deviation " + intervalPeak.ToString("0.0", CultureInfo.InvariantCulture) +
                " ms (a steady sender gives the receivers nothing to correct)");
            sourceStats = source.Stats();
            if (sourceStats.Length > 0) log("capture: " + sourceStats);
            if (ptp != null)
            {
                ptpReceived = ptp.ReceivedPackets;
                // A receiver that never talks back to the timing master is not using its clock,
                // and that changes what a small buffer can be expected to do.
                log("PTP: " + ptpReceived + " datagram(s) received from the receivers");
            }
            this.packets = packets;
            this.secondsSent = sentFrames / (double)source.SampleRate;
            if (lateRecoveries > 0)
            {
                log("late recoveries " + lateRecoveries + ", skipped slots " + skippedPackets +
                    ", worst lateness " + worstLatenessMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
            }
            return 0;
        }

        internal static void CheckReceiverHealth(string name, int mediaFailures, long feedbackFailures, bool multiple)
        {
            if (mediaFailures >= 10 || (multiple && feedbackFailures >= 3))
                throw new ProtocolException("音响「" + name + "」已失去响应，已停止整个播放会话。请检查电源和网络后重新连接。");
        }

        private static uint NextUInt32()
        {
            byte[] bytes = new byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        }

        /// <summary>
        /// Ignores the small quantization/noise floor produced by WASAPI loopback while
        /// still rejecting an activity-bearing block reported as silence by the source.
        /// </summary>
        internal static bool HasAudioActivity(short[] block)
        {
            if (block == null || block.Length == 0) return false;
            int peak = 0;
            double energy = 0.0;
            for (int i = 0; i < block.Length; i++)
            {
                int sample = Math.Abs((int)block[i]);
                if (sample > peak) peak = sample;
                energy += (double)sample * sample;
            }
            double rms = Math.Sqrt(energy / block.Length);
            return (peak >= 48 && rms >= 8.0) || rms >= 12.0;
        }
        private static double Level(short[] block)
        {
            double sum = 0.0;
            for (int i = 0; i < block.Length; i++)
            {
                double value = block[i] / 32768.0;
                sum += value * value;
            }
            return Math.Sqrt(sum / block.Length);
        }
    }
}

