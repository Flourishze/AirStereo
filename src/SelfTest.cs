using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using AirStereo.Audio;
using AirStereo.Protocol;

namespace AirStereo
{
    /// <summary>
    /// Offline checks for every primitive and framing rule the protocol depends on. Each
    /// vector is fixed by an external authority (RFC 8439, RFC 5869 style HKDF behaviour as
    /// produced by the platform, or an SRP vector generated with an independent library), and
    /// the composite checks run random inputs through the same portable AEAD used at runtime.
    /// </summary>
    public static class SelfTest
    {
        private static int failures;
        private static int checks;

        public static int Run()
        {
            failures = 0;
            checks = 0;

            Rfc8439BlockFunction();
            Rfc8439Aead();
            PortableAeadCrossCheck();
            HkdfDerivation();
            SrpVector();
            TlvRoundTrip();
            RecordLayer();
            PlistRoundTrip();
            TimingAndAudioLayout();
            SetupAudioFormat();
            ResamplerQuality();
            LatencyProfileMapping();
            ContinuousMediaOptions();
            AlacRoundTripAndContinuity();
            CalibrationDsp();
            StereoRoutingDsp();
            Grouping();
            DiscoveryIdentity();
            NativePairDiscovery();
            SessionFailurePolicy();
            LivePlaybackDsp();
            Ui.FeatureRegressionTests.RunPure(Check);
            Ui.UiRegressionTests.Run(Check);

            Console.WriteLine();
            Console.WriteLine(checks + " checks, " + failures + " failure" + (failures == 1 ? "" : "s"));
            return failures == 0 ? 0 : 1;
        }

        private static void ContinuousMediaOptions()
        {
            var defaults = new Session.SessionOptions();
            Check("default codec is ALAC with 90-minute capture-confirmed timeout",
                defaults.UseAlac && defaults.SourceSilenceDisconnectMilliseconds == 5400000 &&
                defaults.SilenceMode == Session.SilenceFrameMode.RepeatLast);
            Check("API defaults match session", new PlayRequest().UseAlac &&
                new PlayRequest().SourceSilenceDisconnectMilliseconds == 5400000);
            string diag = Session.ReceiverSession.CodecDiagnostics(true,true,44100,24);
            Check("codec diagnostics expose sender format after SETUP without claiming audibility",
                diag.Contains("requested=ALAC actual=ALAC") && diag.Contains("sampleRate=44100 framesPerPacket=352 channels=2") &&
                diag.Contains("ascBytes=24") && diag.Contains("ct=2") && diag.Contains("receiverAudio=not-verified"));
            Check("PCM diagnostic identifies explicit experimental override", Session.ReceiverSession.CodecDiagnostics(false,false,48000,0).Contains("reason=experimental-pcm-override") &&
                Session.ReceiverSession.CodecDiagnostics(false,false,48000,0).Contains("ct=1"));
            string[] original = { "play", "--target", "speaker", "--rate", "48000" };
            var parsed = Program.ParseMediaArguments(original, out string[] remaining);
            Check("media parser preserves original play options", parsed.UseAlac &&
                string.Join("|", remaining) == string.Join("|", original));
            parsed = Program.ParseMediaArguments(new[] { "gui", "--pcm", "--silence-mode=zero",
                "--silence-disconnect-ms", "1000", "--tray" }, out remaining);
            Check("GUI experiment options reach PCM/zero/custom timeout", !parsed.UseAlac &&
                parsed.SilenceMode == Session.SilenceFrameMode.Zero &&
                parsed.SourceSilenceDisconnectMilliseconds == 1000 && string.Join("|", remaining) == "gui|--tray");
            parsed = Program.ParseMediaArguments(new[] { "gui", "--SILENCE-MODE", "repeat-last",
                "--silence-disconnect-ms=2147483647" }, out remaining);
            Check("media options case-insensitive and positive int range", parsed.UseAlac &&
                parsed.SourceSilenceDisconnectMilliseconds == int.MaxValue);
            string[][] invalid = {
                new[] { "gui", "--standby-ms=3000" }, new[] { "gui", "--standby-ms", "20000" },
                new[] { "gui", "--silence-disconnect-ms" }, new[] { "gui", "--silence-disconnect-ms=0" },
                new[] { "gui", "--silence-disconnect-ms=-1" }, new[] { "gui", "--silence-disconnect-ms=2147483648" },
                new[] { "gui", "--silence-disconnect-ms=1.5" }, new[] { "gui", "--silence-disconnect-ms=abc" },
                new[] { "gui", "--silence-mode=unknown" }, new[] { "gui", "--silence-mode" },
                new[] { "gui", "--pcm=true" }, new[] { "gui", "--pcm", "--pcm" },
                new[] { "gui", "--silence-mode=zero", "--silence-mode=repeat-last" },
                new[] { "gui", "--silence-disconnect-ms=100", "--silence-disconnect-ms", "100" }
            };
            for (int i = 0; i < invalid.Length; i++)
            {
                bool rejected = false;
                try { Program.ParseMediaArguments(invalid[i], out remaining); }
                catch (ProtocolException) { rejected = true; }
                Check("media parser rejects invalid/duplicate/retired H2 #" + i, rejected);
            }
            bool sessionRejected = false, requestRejected = false;
            try { new Session.Streamer(new List<Receiver>(),
                new Session.SessionOptions { SourceSilenceDisconnectMilliseconds = 0 }, null); }
            catch (ProtocolException) { sessionRejected = true; }
            try { AirStereoApi.Play(new ReceiverGroup(),
                new PlayRequest { SourceSilenceDisconnectMilliseconds = -1 }, null, null); }
            catch (ProtocolException) { requestRejected = true; }
            Check("bad timeout fails before capture/network", sessionRejected && requestRejected);

            short[] zero = new short[704];
            short[] quiet = new short[704]; Array.Fill(quiet, (short)1);
            short[] boundary = new short[704]; Array.Fill(boundary, (short)12);
            short[] peak = new short[704]; peak[0] = 48;
            short[] music = new short[704]; Array.Fill(music, (short)1000);
            short[] minimum = new short[704]; minimum[0] = short.MinValue;
            Check("activity null/empty/zero inactive", !Session.Streamer.HasAudioActivity(null) &&
                !Session.Streamer.HasAudioActivity(Array.Empty<short>()) && !Session.Streamer.HasAudioActivity(zero));
            Check("activity low-level nonzero stays below detector", !Session.Streamer.HasAudioActivity(quiet));
            Check("activity peak alone below RMS is not enough", !Session.Streamer.HasAudioActivity(peak));
            Check("activity RMS boundary is active", Session.Streamer.HasAudioActivity(boundary));
            Array.Clear(boundary); boundary[0] = 48; boundary[1] = 48;
            Check("activity peak and RMS joint threshold", !Session.Streamer.HasAudioActivity(boundary));
            Array.Fill(boundary, (short)0); for (int i = 0; i < 20; i++) boundary[i] = 48;
            Check("activity peak48 RMS8 crosses threshold", Session.Streamer.HasAudioActivity(boundary));
            Check("activity normal music and short.MinValue safe", Session.Streamer.HasAudioActivity(music) &&
                Session.Streamer.HasAudioActivity(minimum));
            Check("capture aggregate full confirmed silence", LoopbackSource.AggregateReadActivity(704,704,false,false) == AudioReadActivity.ConfirmedSilent);
            Check("capture aggregate active dominates unknown", LoopbackSource.AggregateReadActivity(704,704,true,true) == AudioReadActivity.Active);
            Check("capture aggregate unknown does not count", LoopbackSource.AggregateReadActivity(704,704,false,true) == AudioReadActivity.Unknown);
            Check("capture padded zeros are starved not silence", LoopbackSource.AggregateReadActivity(700,704,false,false) == AudioReadActivity.Starved);
            foreach (Session.SilenceFrameMode mode in Enum.GetValues<Session.SilenceFrameMode>())
            {
                var policy = new Session.ContinuousSilencePolicy(1000, mode, 704);
                Check("silence requires consecutive captured frames " + mode,
                    !policy.Process(zero,true,AudioReadActivity.ConfirmedSilent,352,44100) && policy.SilentMilliseconds > 7);
                Check("quiet nonzero marked active cannot trigger disconnect " + mode,
                    !policy.Process(quiet,true,AudioReadActivity.Active,352,44100) && policy.SilentMilliseconds == 0);
                policy.Process(zero,true,AudioReadActivity.ConfirmedSilent,352,44100);
                Check("unknown resets confirmed timer " + mode,
                    !policy.Process(zero,true,AudioReadActivity.Unknown,352,44100) && policy.SilentMilliseconds == 0);
                policy.Process(zero,true,AudioReadActivity.ConfirmedSilent,352,44100);
                Check("starved resets confirmed timer " + mode,
                    !policy.Process(zero,true,AudioReadActivity.Starved,352,44100) && policy.SilentMilliseconds == 0);
                Check("activity vetoes wrong silent metadata " + mode,
                    !policy.Process(music,true,AudioReadActivity.ConfirmedSilent,352,44100) && music[0] == 1000);
                Check("test/file source does not expire by age " + mode,
                    !policy.Process(zero,false,AudioReadActivity.ConfirmedSilent,352,44100) && policy.SilentMilliseconds == 0);
                short[] silent = new short[704];
                policy.Process(silent,true,AudioReadActivity.ConfirmedSilent,352,44100);
                Check("silence never replays last audible music " + mode, silent[0] == 0);
                int untilDisconnect = 1;
                while (!policy.Process(silent,true,AudioReadActivity.ConfirmedSilent,352,44100) && untilDisconnect < 150) untilDisconnect++;
                Check("continuous confirmed silence expires on frame boundary " + mode, untilDisconnect == 125 && policy.SilentMilliseconds >= 1000);
                Check("real activity resets expired timer " + mode,
                    !policy.Process(music,true,AudioReadActivity.Active,352,44100) && policy.SilentMilliseconds == 0);
            }
        }

        [System.Runtime.InteropServices.DllImport("LibALAC64.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr InitializeDecoderWithCookie(byte[] cookie, int length);
        [System.Runtime.InteropServices.DllImport("LibALAC64.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int Decode(IntPtr decoder, byte[] input, byte[] output, ref int length);
        [System.Runtime.InteropServices.DllImport("LibALAC64.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FinishDecoder(IntPtr decoder);

        private static void AlacRoundTripAndContinuity()
        {
            foreach (int rate in new[] { 44100, 48000 })
            {
                Exception sequentialError = null;
                try
                {
                    for (int round = 0; round < 100; round++)
                    using (var left = new NativeAlacEncoder(rate))
                    using (var right = new NativeAlacEncoder(rate))
                    {
                        foreach (NativeAlacEncoder encoder in new[] { left, right })
                        {
                            int status = NativeAlacEncoder.ParseMagicCookie(encoder.MagicCookie,
                                encoder.MagicCookie.Length, out int sr, out int ch, out int bits, out int frames);
                            if (status != 0 || encoder.MagicCookie.Length != 24 || sr != rate ||
                                ch != 2 || bits != 16 || frames != 352 || encoder.Encode(new byte[1408]) <= 0)
                                throw new InvalidOperationException("invalid ALAC cookie or encode in cycle " + round);
                        }
                    }
                }
                catch (Exception error) { sequentialError = error; }
                Check("ALAC 100 repeated dual-encoder lifecycles " + rate,
                    sequentialError == null, sequentialError?.ToString());

                Exception concurrentError = null;
                object errorGate = new object();
                var workers = new System.Threading.Thread[4];
                for (int index = 0; index < workers.Length; index++)
                {
                    workers[index] = new System.Threading.Thread(() =>
                    {
                        try
                        {
                            for (int cycle = 0; cycle < 100; cycle++)
                            using (var encoder = new NativeAlacEncoder(rate))
                            {
                                int status = NativeAlacEncoder.ParseMagicCookie(encoder.MagicCookie,
                                    encoder.MagicCookie.Length, out int sr, out int ch, out int bits, out int frames);
                                if (status != 0 || encoder.MagicCookie.Length != 24 || sr != rate ||
                                    ch != 2 || bits != 16 || frames != 352 || encoder.Encode(new byte[1408]) <= 0)
                                    throw new InvalidOperationException("invalid concurrent ALAC cookie or encode");
                            }
                        }
                        catch (Exception error) { lock (errorGate) concurrentError = error; }
                    });
                    workers[index].Start();
                }
                foreach (var worker in workers) worker.Join();
                Check("ALAC four concurrent workers 400 lifecycles " + rate,
                    concurrentError == null, concurrentError?.ToString());
            }
            try
            {
                foreach (int rate in new[] { 44100, 48000 })
                using (var encoder = new NativeAlacEncoder(rate))
                {
                    NativeAlacEncoder.ParseMagicCookie(encoder.MagicCookie, encoder.MagicCookie.Length,
                        out int sr, out int ch, out int bits, out int frames);
                    Check("ALAC cookie geometry " + rate, sr == rate && ch == 2 && bits == 16 && frames == 352);
                    IntPtr decoder = InitializeDecoderWithCookie(encoder.MagicCookie, encoder.MagicCookie.Length);
                    Check("native decoder initializes " + rate, decoder != IntPtr.Zero);
                    if (decoder == IntPtr.Zero) continue;
                    try
                    {
                        byte[] networkPcm = new byte[1408], expectedNative = new byte[1408], decoded = new byte[1408];
                        var rng = new Random(352);
                        for (int input = 0; input < 5; input++)
                        {
                            for (int sample = 0; sample < 704; sample++)
                            {
                                // An independent numerical sample vector catches endian swaps that
                                // a same-byte-stream round-trip cannot: +1 must not become +256.
                                short value = input == 0 ? (short)0 : input == 1 ? (short)(sample % 2 == 0 ? 1 : -1234) :
                                    input == 2 ? (short)(sample % 2 == 0 ? short.MinValue : short.MaxValue) :
                                    input == 3 ? (short)(10000 * Math.Sin(sample * 0.17)) : (short)rng.Next(short.MinValue, short.MaxValue + 1);
                                System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(networkPcm.AsSpan(sample * 2), value);
                                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(expectedNative.AsSpan(sample * 2), value);
                            }
                            byte[] original = (byte[])networkPcm.Clone();
                            int length = encoder.Encode(networkPcm);
                            Check("ALAC length bound " + rate + " vector " + input, length > 0 && length <= NativeAlacEncoder.MaxEncodedBytes);
                            int result = Decode(decoder, encoder.OutputBuffer, decoded, ref length);
                            Check("ALAC numeric sample round-trip " + rate + " vector " + input,
                                result == 0 && length == 1408 && expectedNative.AsSpan().SequenceEqual(decoded));
                            Check("ALAC keeps caller's network PCM unchanged " + rate + " vector " + input, original.AsSpan().SequenceEqual(networkPcm));
                        }
                        bool rejected = false;
                        try { encoder.Encode(new byte[1407]); } catch (ArgumentException) { rejected = true; }
                        Check("ALAC rejects incomplete PCM " + rate, rejected);
                    }
                    finally { FinishDecoder(decoder); }
                }
                byte[] key1 = new byte[32], key2 = new byte[32]; key2[0] = 42;
                using (var a = new AudioPacketizer(key1,10,1000,0x12345678,44100,true))
                using (var b = new AudioPacketizer(key2,50,2000,0x87654321,44100,true))
                {
                    byte[] pcm = new byte[1408];
                    DateTime start = new DateTime(2026,10,6,0,0,0,DateTimeKind.Utc);
                    bool continuous = true;
                    foreach (Session.SilenceFrameMode mode in Enum.GetValues<Session.SilenceFrameMode>())
                    {
                        var policy = new Session.ContinuousSilencePolicy(5400000,mode,704);
                        var silent = new short[704];
                        for (int i = 0; i < 1253; i++) // >10s of silence, never suppress a media slot
                        {
                            ushort sequence = a.Sequence; uint timestamp = a.Timestamp;
                            bool expired = policy.Process(silent,true,AudioReadActivity.ConfirmedSilent,352,44100);
                            var packet = a.Packet(pcm,false,start.AddMilliseconds((a.PacketsSent+1)*8), start.AddMinutes(1));
                            continuous &= !expired && a.Sequence == unchecked((ushort)(sequence+1)) &&
                                a.Timestamp == timestamp+352 && System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                                packet.Buffer.AsSpan(packet.Length-8)) == (ulong)(a.PacketsSent-1);
                        }
                    }
                    Check("10s silence both modes retains media/sequence/timestamp/nonce continuity", continuous && a.PacketsSent == 2506);
                    Check("independent ALAC session remains untouched", b.Sequence == 50 && b.Timestamp == 2000 && b.PacketsSent == 0);
                    var pa = a.Packet(pcm,false); var pb = b.Packet(pcm,false);
                    Check("independent ALAC keys/SSRC/sequence/ciphertext", a.Ssrc != b.Ssrc && a.Sequence != b.Sequence &&
                        !pa.Buffer.AsSpan(12,pa.Length-36).SequenceEqual(pb.Buffer.AsSpan(12,pb.Length-36)));
                    DateTime now = DateTime.UtcNow;
                    new Random(55).NextBytes(pcm);
                    var randomPacket = a.Packet(pcm,false,now,now.AddSeconds(1));
                    ushort seq = unchecked((ushort)(a.Sequence-1));
                    var status = a.Retransmit(seq,now.AddMilliseconds(1),out byte[] resend);
                    Check("variable ALAC retransmit contains exact original media bytes", status == AudioPacketizer.RetransmitResult.Packet &&
                        resend.Length == randomPacket.Length+4 && resend.AsSpan(4).SequenceEqual(randomPacket.Buffer.AsSpan(0,randomPacket.Length)));
                }
            }
            catch (Exception error) { Check("ALAC round-trip and continuity no skips on native failure", false,error.ToString()); }
        }

        private static void Check(string name, bool condition, string detail = null)
        {
            checks++;
            if (condition)
            {
                Console.WriteLine("  pass  " + name);
                return;
            }
            failures++;
            Console.WriteLine("  FAIL  " + name + (detail == null ? "" : "  (" + detail + ")"));
        }

        private static void CheckEqual(string name, byte[] expected, byte[] actual)
        {
            Check(name, expected.AsSpan().SequenceEqual(actual),
                "expected " + Hex.ToHex(expected) + ", got " + Hex.ToHex(actual));
        }

        /// <summary>
        /// The capture path resamples the endpoint mix (48000) into the stream rate (44100).
        /// The checks pin the two things a listener would notice: the level must not move, and
        /// a tone above the output Nyquist must not fold back down as aliasing.
        /// </summary>
        private static void ResamplerQuality()
        {
            const int sourceRate = 48000;
            const int targetRate = 44100;
            SincResampler resampler = new SincResampler(sourceRate, targetRate);

            // One second of a quiet sine, pushed in the ten millisecond chunks WASAPI delivers.
            double[] levels = new double[2];
            double[] tones = { 1000.0, 23500.0 };
            int producedTotal = 0;
            for (int tone = 0; tone < tones.Length; tone++)
            {
                resampler.Reset();
                resampler.SetTrim(0.0);
                double phase = 0.0;
                double sum = 0.0;
                long counted = 0;
                for (int chunk = 0; chunk < 100; chunk++)
                {
                    const int frames = 480;
                    float[] input = new float[frames * 2];
                    for (int frame = 0; frame < frames; frame++)
                    {
                        float value = (float)(0.25 * Math.Sin(phase));
                        input[frame * 2] = value;
                        input[frame * 2 + 1] = value;
                        phase += 2.0 * Math.PI * tones[tone] / sourceRate;
                    }
                    resampler.Push(input, frames);

                    // Ignore the first ten milliseconds: the kernel warms up on clamped edges.
                    if (chunk == 0) continue;
                    for (int i = 0; i < resampler.ProducedFrames; i++)
                    {
                        double sample = resampler.Produced[i * 2];
                        sum += sample * sample;
                        counted++;
                    }
                }
                levels[tone] = counted > 0 ? Math.Sqrt(sum / counted) : 0.0;
                if (tone == 0) producedTotal = (int)counted;
            }

            // A quarter amplitude sine has an RMS of 0.25/sqrt(2) = 0.1768, and the conversion
            // must hand back one output frame per output frame of wall clock time.
            Check("resampling keeps the level of an in band tone",
                Math.Abs(levels[0] - 0.17678) < 0.002, "rms=" + levels[0].ToString("0.0000"));
            Check("resampling produces one second of audio for one second of input",
                Math.Abs(producedTotal - (44100 - 441)) < 64, "produced=" + producedTotal);
            Check("a tone above the output Nyquist is filtered, not folded back",
                levels[1] < levels[0] * 0.08, "above=" + levels[1].ToString("0.0000") +
                ", in band=" + levels[0].ToString("0.0000"));

            // The drift loop trims the ratio, and the trim has to stay small enough that nobody
            // hears the pitch move.
            resampler.SetTrim(1.0);
            Check("the rate trim is clamped to 1500 ppm",
                Math.Abs(resampler.TrimPpm - 1500.0) < 0.5, "ppm=" + resampler.TrimPpm);
            resampler.SetTrim(-1.0);
            Check("the rate trim clamps symmetrically",
                Math.Abs(resampler.TrimPpm + 1500.0) < 0.5, "ppm=" + resampler.TrimPpm);

            // Matching rates must be handed through untouched: no filter, no rounding.
            SincResampler passthrough = new SincResampler(44100, 44100);
            float[] block = new float[8];
            for (int i = 0; i < block.Length; i++) block[i] = i + 0.5f;
            passthrough.Push(block, 4);
            bool identical = passthrough.ProducedFrames == 4;
            for (int i = 0; i < 8 && identical; i++) identical = passthrough.Produced[i] == block[i];
            Check("equal rates are passed through without filtering", identical);

            // A delayed or malformed capture callback must not be able to make the live
            // resampler allocate in proportion to the callback size.
            float[] oversized = new float[200000 * 2];
            resampler.Reset();
            resampler.Push(oversized, 200000);
            Check("resampler bounds an oversized capture callback",
                resampler.PendingInputFrames <= 8192 && resampler.ProducedFrames <= 16384,
                "pending=" + resampler.PendingInputFrames + ", produced=" + resampler.ProducedFrames);
        }

        private static void Rfc8439BlockFunction()
        {
            // RFC 8439 section 2.3.2 is a block function vector, so the keystream for the
            // all zero block equals the expected output.
            byte[] key = Hex.FromHex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
            byte[] nonce = Hex.FromHex("000000090000004a00000000");
            byte[] expected = Hex.FromHex(
                "10f1e7e4d13b5915500fdd1fa32071c4c7d1f4c733c068030422aa9ac3d46c4e" +
                "d2826446079faa0914c2d705d98b02a2b5129cd1de164eb9cbd083e8a2503c4e");

            byte[] block;
            using (ChaCha20Poly1305Compat aead = new ChaCha20Poly1305Compat(key))
            {
                byte[] ciphertext = new byte[64];
                byte[] tag = new byte[16];
                // The AEAD uses counter 1 for its payload keystream, which is exactly the
                // block the RFC vector publishes for this key and nonce.
                aead.Encrypt(nonce, new byte[64], ciphertext, tag, ReadOnlySpan<byte>.Empty);
                block = ciphertext;
            }
            CheckEqual("RFC 8439 2.3.2 ChaCha20 block", expected, block);
        }

        private static void Rfc8439Aead()
        {
            byte[] key = Hex.FromHex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
            byte[] nonce = Hex.FromHex("070000004041424344454647");
            byte[] aad = Hex.FromHex("50515253c0c1c2c3c4c5c6c7");
            byte[] plaintext = Encoding.ASCII.GetBytes(
                "Ladies and Gentlemen of the class of '99: If I could offer you only " +
                "one tip for the future, sunscreen would be it.");
            byte[] expectedCiphertext = Hex.FromHex(
                "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6" +
                "3dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b369" +
                "2ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc" +
                "3ff4def08e4b7a9de576d26586cec64b6116");
            byte[] expectedTag = Hex.FromHex("1ae10b594f09e26a7e902ecbd0600691");

            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16];
            using (ChaCha20Poly1305Compat aead = new ChaCha20Poly1305Compat(key))
            {
                aead.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            }
            CheckEqual("RFC 8439 2.8.2 AEAD ciphertext", expectedCiphertext, ciphertext);
            CheckEqual("RFC 8439 2.8.2 AEAD tag", expectedTag, tag);
        }

        private static void PortableAeadCrossCheck()
        {
            // Same key, nonce and data through the runtime AEAD and record layer, plus random
            // round trips at the boundary sizes the control channel uses.
            RandomNumberGenerator random = RandomNumberGenerator.Create();
            bool allMatch = true;
            bool allRoundTrip = true;

            foreach (int size in new[] { 0, 1, 15, 16, 17, 255, 256, 1023, 1024, 1025, 2500 })
            {
                byte[] key = new byte[32];
                byte[] plaintext = new byte[size];
                random.GetBytes(key);
                random.GetBytes(plaintext);

                using (RecordCipher cipher = new RecordCipher(key))
                {
                    byte[] associated = new byte[2] { 0x11, 0x22 };
                    byte[] sealedRecord = cipher.Seal(plaintext, associated);

                    using (ChaCha20Poly1305Compat reference = new ChaCha20Poly1305Compat(key))
                    {
                        byte[] expected = new byte[plaintext.Length];
                        byte[] expectedTag = new byte[16];
                        byte[] nonce = new byte[12];
                        reference.Encrypt(nonce, plaintext, expected, expectedTag, associated);
                        if (!expected.AsSpan().SequenceEqual(sealedRecord.AsSpan(0, plaintext.Length)) ||
                            !expectedTag.AsSpan().SequenceEqual(sealedRecord.AsSpan(plaintext.Length, 16)))
                        {
                            allMatch = false;
                        }
                    }
                }

                using (RecordCipher sender = new RecordCipher(key))
                using (RecordCipher receiver = new RecordCipher(key))
                {
                    byte[] wire = sender.Frame(plaintext);
                    if (!Decode(receiver, wire).AsSpan().SequenceEqual(plaintext)) allRoundTrip = false;
                }
            }

            Check("record layer matches the runtime AEAD", allMatch);
            Check("record framing round trips at every block boundary", allRoundTrip);

            byte[] wrongKey = new byte[32];
            random.GetBytes(wrongKey);
            using (RecordCipher sender = new RecordCipher(new byte[32]))
            using (RecordCipher receiver = new RecordCipher(wrongKey))
            {
                byte[] wire = sender.Frame(new byte[64]);
                bool rejected = false;
                try
                {
                    Decode(receiver, wire);
                }
                catch (ProtocolException)
                {
                    rejected = true;
                }
                Check("wrong key is rejected by the record layer", rejected);
            }
        }

        private static byte[] Decode(RecordCipher cipher, byte[] wire)
        {
            List<byte> output = new List<byte>();
            int position = 0;
            while (position < wire.Length)
            {
                if (position + 2 > wire.Length) throw new ProtocolException("truncated record");
                int length = wire[position] | (wire[position + 1] << 8);
                int end = position + 2 + length + 16;
                if (end > wire.Length) throw new ProtocolException("truncated record");
                byte[] record = new byte[length + 16];
                Buffer.BlockCopy(wire, position + 2, record, 0, record.Length);
                byte[] header = new byte[2] { wire[position], wire[position + 1] };
                output.AddRange(cipher.Open(record, header));
                position = end;
            }
            return output.ToArray();
        }

        private static void HkdfDerivation()
        {
            // The SRP session key of the vector below, run through the two derivations the
            // control channel uses. Values cross-checked against an independent HKDF.
            byte[] secret = Hex.FromHex(
                "bd781e3f038662c8d07a54cf60a0b7304ddd41a6e272b8e8413d6e6198d09803" +
                "c159ed6d15fe72bc73b35da3a89759097a12acd564a63221a4472d19ca30d5cf");

            CheckEqual("HKDF Control-Write-Encryption-Key",
                Hex.FromHex("f8e15c683962345e54fc200c976fb44050393b3f31444db9f6105cd2dbdf8af4"),
                HapCrypto.Derive(secret, "Control-Salt", "Control-Write-Encryption-Key"));
            CheckEqual("HKDF Control-Read-Encryption-Key",
                Hex.FromHex("9447f3bf46fbf0c495fbd141df139195f0301f49dcdaf859beaa77f98bed4579"),
                HapCrypto.Derive(secret, "Control-Salt", "Control-Read-Encryption-Key"));
            CheckEqual("HKDF Events-Write-Encryption-Key",
                Hex.FromHex("d51f0ea2f84c966874899d9fe5c2fe27c26eaf91af4324f45747d754fb9f6b34"),
                HapCrypto.Derive(secret, "Events-Salt", "Events-Write-Encryption-Key"));
            CheckEqual("HKDF Pair-Setup-Encrypt-Info",
                Hex.FromHex("99dd8318b6df627253b3cd438fe9276fa568f20c4805dde4a587a5756b61da99"),
                HapCrypto.Derive(secret, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info"));
        }

        private static void SrpVector()
        {
            // Deterministic vector generated with srptools (no device credentials involved).
            byte[] privateKey = Hex.FromHex("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");
            byte[] salt = Hex.FromHex("0102030405060708090a0b0c0d0e0f10");
            byte[] serverPublic = Hex.FromHex(
                "77becbcf91a15358a56a8dc132063de8d5e09f636f17fd91ea5e07d565e2c14215f872afa5888d3fa613fcee1d104bef" +
                "20b1945cdbb52913aa6870248eb069b39316f0944176311879341bf6589de8b0807b06b445981f3783980b6b11c3831f" +
                "4f433044c33e74186feb1f1d020bf7cdbd2fc4cc74d618ebcd92bf5625e08290e7b8038ac55cb48ef35411f4b93915ca" +
                "814eda8f84b849ca7e0cad42efc4127910b0630525c105b228f0f1adbb8f5277088d0d44907eaf60d1ef5858eaad0c4b" +
                "407018d65deb99293c22c96a2e098e9306980a8557dcb08248e1944e9cc6c2d17615067c6e74251423513ef93f96d7a2a" +
                "511ea41e0a2aaea7198957f75cf8e3d2006cb87f5e906793c30fd6cf7b54dfe6ec5f42cdd4305244edff61b2bd6b962c67" +
                "4d527100fdf0c93e54cf85791ae226363073566c1cc85de330f0e1e9374de9814af809c516f922e123e803f67c05e0aa29a8" +
                "eca43aca400df13fe125f5c95bacbc9192a5b47960a94369a4e925335033e92636a5a1373793fc53335005296");

            Srp.Proof proof = Srp.ClientProof(privateKey, salt, serverPublic, "3939");
            CheckEqual("SRP client public key",
                Hex.FromHex(
                    "bc0e7cf5dc3babf67dcedbb3b140aacc6cac43f4336b43bbd5de48d6ea7c8eda66924e354255225bccad9debe21182e6" +
                    "bb050f3ff3e6cfbb62c229379968c70ca436ad649a0b051373184215eef046f6f1f2256838f958581f6c7b2b85fa4afe3" +
                    "26a0e8a951d4489305331aff88a136fd8d108bcc95fceb7e557c889c828bd23fb0702f053e1ca6470fb3c76bce4843fc0" +
                    "05c7ea675740f8550212656cfc8919d9db805a434a68229e0d9dfe43fc16dc680a5ce74b77cf374353b05759bc1da3a9" +
                    "dabde30a4209381c87ca83d9483abdf66b86f9b1cbda9ad82c62712b87ce6fb7069b8fc8df344261821a06d0dc5106af" +
                    "76d4245f3f7737a94dbc484b415555dc401842d3011204553ba9f611b02bc38de26eba1a76bf8350205a62c436ba1c3c7" +
                    "c69d59318bd107fd1c1f5d846b3142e85a5d49e522655e020ed1bfe1e186cf923bf328f0b9b4c6a8aa3266ed9125bb98" +
                    "d63827110713be7803122ee4603c54ea31863ce4b10aff31f9073cf63b94733b4f066e72d4ec35687047d5d0db160"),
                proof.Public);
            CheckEqual("SRP client proof",
                Hex.FromHex("46dee58787433fe121e697c854832862d8eb9ecec9d8e70aee4d7026d9fb61b9" +
                           "c7b2e0d6cea183be5433cc4e8c51c3d19b09b92bfdb1051eebc746c35ef4871f"),
                proof.ClientProof);
            CheckEqual("SRP expected server proof",
                Hex.FromHex("d13374795ac03f8f1a89ca5431b9d8eabc43a484e79da06915050815531ef9ac" +
                           "08497c758e7b0454a4d32cbcb5a9d55ac2a1887f22e19bc7bf415126a6b31cf1"),
                proof.ExpectedServer);
            CheckEqual("SRP session key",
                Hex.FromHex("bd781e3f038662c8d07a54cf60a0b7304ddd41a6e272b8e8413d6e6198d09803" +
                           "c159ed6d15fe72bc73b35da3a89759097a12acd564a63221a4472d19ca30d5cf"),
                proof.SessionKey);

            bool rejected = false;
            try
            {
                Srp.ClientProof(privateKey, salt, new byte[] { 0 }, "3939");
            }
            catch (ProtocolException)
            {
                rejected = true;
            }
            Check("SRP rejects a zero server public value", rejected);
        }

        private static void TlvRoundTrip()
        {
            byte[] large = new byte[384];
            for (int i = 0; i < large.Length; i++) large[i] = (byte)i;
            byte[] encoded = Tlv.Encode((3, large), (6, new byte[] { 2 }), (0, new byte[0]));
            Dictionary<byte, byte[]> decoded = Tlv.Decode(encoded);
            Check("TLV splits and rejoins values above 255 bytes",
                decoded[3].AsSpan().SequenceEqual(large) && decoded[6][0] == 2 && decoded.ContainsKey(0));

            bool truncated = false;
            try
            {
                Tlv.Decode(new byte[] { 3, 5, 1, 2 });
            }
            catch (ProtocolException)
            {
                truncated = true;
            }
            Check("TLV rejects a truncated value", truncated);

            bool errorTag = false;
            try
            {
                Tlv.Decode(new byte[] { 7, 1, 2 });
            }
            catch (ProtocolException)
            {
                errorTag = true;
            }
            Check("TLV surfaces pairing errors", errorTag);
        }

        private static void RecordLayer()
        {
            // The nonce is the record counter in little endian form after four zero bytes.
            using (RecordCipher cipher = new RecordCipher(new byte[32]))
            {
                byte[] first = cipher.Seal(new byte[] { 1, 2, 3 }, new byte[] { 3, 0 });
                byte[] second = cipher.Seal(new byte[] { 1, 2, 3 }, new byte[] { 3, 0 });
                Check("record nonces advance so ciphertexts differ",
                    !first.AsSpan().SequenceEqual(second));
                Check("record counter advanced twice", cipher.Counter == 2, "counter=" + cipher.Counter);
            }

            using (RecordCipher cipher = new RecordCipher(new byte[32]))
            {
                byte[] framed = cipher.Frame(new byte[2500]);
                int position = 0;
                int records = 0;
                while (position < framed.Length)
                {
                    int length = framed[position] | (framed[position + 1] << 8);
                    Check("record length stays within the 1024 byte limit",
                        length > 0 && length <= RecordCipher.MaxRecord);
                    records++;
                    position += 2 + length + 16;
                }
                Check("2.5 KB is split into three records", records == 3, "records=" + records);
            }
        }

        private static void PlistRoundTrip()
        {
            Dictionary<string, object> source = new Dictionary<string, object>
            {
                ["deviceID"] = "02:57:32:41:50:01",
                ["name"] = "AirStereo",
                ["isMedia"] = true,
                ["sr"] = 44100L,
                ["spf"] = 352L,
                ["shk"] = new byte[] { 1, 2, 3, 4, 5 },
                ["streams"] = new List<object>
                {
                    new Dictionary<string, object> { ["type"] = 96L, ["enabled"] = false }
                }
            };

            Dictionary<string, object> parsed = Plist.AsDictionary(Plist.Read(Plist.Write(source)));
            bool ok = Plist.Text(parsed, "name") == "AirStereo" &&
                      Plist.Text(parsed, "deviceID") == "02:57:32:41:50:01" &&
                      Plist.Integer(parsed, "sr") == 44100 &&
                      parsed["isMedia"] is bool media && media &&
                      ((byte[])parsed["shk"]).Length == 5 &&
                      Plist.Integer(Plist.AsDictionary(Plist.AsList(parsed["streams"])[0]), "type") == 96;
            Check("binary plist round trips dictionaries, arrays, booleans and data", ok);

            // A long string forces the extended length form and a wide offset table.
            Dictionary<string, object> wide = new Dictionary<string, object>
            {
                ["blob"] = new byte[9000],
                ["note"] = new string('x', 300)
            };
            Dictionary<string, object> wideParsed = Plist.AsDictionary(Plist.Read(Plist.Write(wide)));
            Check("binary plist handles extended lengths",
                ((byte[])wideParsed["blob"]).Length == 9000 && ((string)wideParsed["note"]).Length == 300);
        }

        /// <summary>
        /// The buffer gears and the slider mapping the window puts in front of the user. A
        /// slider that cannot reach the ends, or a gear that maps to the wrong number, would
        /// silently send the wrong latency to the receiver.
        /// </summary>
        private static void CalibrationDsp()
        {
            AudioProfile clamped = new AudioProfile(true,
                new double[] { 99, -99, 3, 0, 0 }, 99, -99);
            Check("calibration clamps band and channel gains",
                clamped.BandGainDb(0) == 12 && clamped.BandGainDb(1) == -12 &&
                clamped.LeftGainDb == 12 && clamped.RightGainDb == -12);
            Check("auto preamp offsets the largest positive band",
                Math.Abs(clamped.AutoPreampDb + 12.0) < 0.001);

            AudioProfileController controller = new AudioProfileController();
            controller.Update(AudioProfile.Flat);
            ToneSource raw = new ToneSource(48000, 1000, 1000, 0.25);
            CalibratedAudioSource bypass = new CalibratedAudioSource(raw, controller);
            ToneSource plain = new ToneSource(48000, 1000, 1000, 0.25);
            short[] bypassBlock = new short[352 * 2];
            short[] plainBlock = new short[352 * 2];
            bool exact = true;
            for (int block = 0; block < 8; block++)
            {
                int a = bypass.Read(bypassBlock, 352);
                int b = plain.Read(plainBlock, 352);
                if (a != b || !bypassBlock.AsSpan().SequenceEqual(plainBlock)) exact = false;
            }
            Check("disabled calibration is a bit exact bypass", exact);

            controller.Update(new AudioProfile(true, new double[AudioProfile.BandCount], 0.0, -6.0));
            CalibratedAudioSource balanced = new CalibratedAudioSource(
                new ToneSource(48000, 1000, 1000, 0.25), controller);
            double leftEnergy = 0.0;
            double rightEnergy = 0.0;
            long counted = 0;
            for (int block = 0; block < 32; block++)
            {
                int read = balanced.Read(bypassBlock, 352);
                for (int i = 0; i < read; i++)
                {
                    double left = bypassBlock[i * 2];
                    double right = bypassBlock[i * 2 + 1];
                    leftEnergy += left * left;
                    rightEnergy += right * right;
                }
                counted += read;
            }
            double ratio = Math.Sqrt(rightEnergy / Math.Max(1.0, leftEnergy));
            Check("right channel trim attenuates only the right channel",
                Math.Abs(ratio - 0.501) < 0.03, "ratio=" + ratio.ToString("0.000"));

            controller.Update(new AudioProfile(true,
                new double[] { 0, 0, 6, 0, 0 }, 0.0, 0.0));
            CalibratedAudioSource eq = new CalibratedAudioSource(
                new ToneSource(48000, 1000, 1000, 0.25), controller);
            bool finite = true;
            for (int block = 0; block < 16; block++)
            {
                int read = eq.Read(bypassBlock, 352);
                for (int i = 0; i < read * 2; i++)
                {
                    if (bypassBlock[i] == short.MinValue || bypassBlock[i] == short.MaxValue) finite = false;
                }
            }
            Check("enabled EQ produces bounded audio without hard clipping", finite);

            double flatTone = ToneRms(48000, 910.0, AudioProfile.Flat);
            double cutTone = ToneRms(48000, 910.0,
                new AudioProfile(true, new double[] { 0, 0, -6, 0, 0 }, 0.0, 0.0));
            double cutDb = 20.0 * Math.Log10(cutTone / Math.Max(1.0e-12, flatTone));
            Check("an EQ cut at its centre frequency matches the requested gain",
                Math.Abs(cutDb + 6.0) < 0.6, "gain=" + cutDb.ToString("0.00") + " dB");

            double boostedTone = ToneRms(48000, 910.0,
                new AudioProfile(true, new double[] { 0, 0, 6, 0, 0 }, 0.0, 0.0));
            double boostedDb = 20.0 * Math.Log10(boostedTone / Math.Max(1.0e-12, flatTone));
            Check("auto preamp keeps a boosted band inside the 16-bit range",
                Math.Abs(boostedDb) < 0.6 && boostedTone < 0.99,
                "gain=" + boostedDb.ToString("0.00") + " dB");
        }

        private static double ToneRms(int sampleRate, double frequency, AudioProfile profile)
        {
            AudioProfileController controller = new AudioProfileController();
            controller.Update(profile);
            CalibratedAudioSource source = new CalibratedAudioSource(
                new ToneSource(sampleRate, frequency, frequency, 0.25), controller);
            short[] block = new short[352 * 2];
            double energy = 0.0;
            long counted = 0;
            for (int i = 0; i < 64; i++)
            {
                int read = source.Read(block, 352);
                if (i < 8) continue;
                for (int frame = 0; frame < read; frame++)
                {
                    double left = block[frame * 2] / 32768.0;
                    double right = block[frame * 2 + 1] / 32768.0;
                    energy += left * left + right * right;
                    counted += 2;
                }
            }
            return Math.Sqrt(energy / Math.Max(1, counted));
        }

        private static void LatencyProfileMapping()
        {
            Check("realtime gear is 120 ms", LatencyProfile.Resolve(LatencyMode.Realtime, 600) == 120);
            Check("normal gear is 200 ms", LatencyProfile.Resolve(LatencyMode.Normal, 20) == 200);
            Check("buffered gear is 500 ms", LatencyProfile.Resolve(LatencyMode.Buffered, 20) == 500);
            Check("stable gear is 1000 ms", LatencyProfile.Resolve(LatencyMode.Stable, 20) == 1000);
            // The gears have to climb: a realtime buffer above the normal one would make the
            // window's ordering lie about how much slack each gear buys.
            Check("the gears climb from realtime to stable",
                LatencyProfile.RealtimeMs < LatencyProfile.NormalMs &&
                LatencyProfile.NormalMs < LatencyProfile.BufferedMs &&
                LatencyProfile.BufferedMs < LatencyProfile.StableMs);
            Check("custom gear honours its value", LatencyProfile.Resolve(LatencyMode.Custom, 437) == 437,
                LatencyProfile.Resolve(LatencyMode.Custom, 437).ToString());

            Check("a buffer below the floor is raised", LatencyProfile.Resolve(LatencyMode.Custom, 1) == 20);
            Check("a buffer above the ceiling is lowered",
                LatencyProfile.Resolve(LatencyMode.Custom, 99999) == 3000);

            Check("slider minimum is the floor", LatencyProfile.FromSlider(0) == 20);
            Check("slider maximum is the ceiling",
                LatencyProfile.FromSlider(LatencyProfile.SliderSteps) == 3000);

            bool rising = true;
            int previous = -1;
            for (int position = 0; position <= LatencyProfile.SliderSteps; position += 5)
            {
                int milliseconds = LatencyProfile.FromSlider(position);
                if (milliseconds < LatencyProfile.MinimumMs || milliseconds > LatencyProfile.MaximumMs)
                {
                    rising = false;
                }
                if (milliseconds < previous) rising = false;
                previous = milliseconds;
            }
            Check("the slider rises monotonically inside its range", rising);

            // The whole point of the logarithmic mapping: the bottom of the range must be
            // reachable by hand, not squeezed into a few pixels.
            Check("the first tenth of the slider covers the low end",
                LatencyProfile.FromSlider(LatencyProfile.SliderSteps / 10) < 250,
                LatencyProfile.FromSlider(LatencyProfile.SliderSteps / 10).ToString());

            bool roundTrips = true;
            foreach (int millisecond in new[] { 20, 50, 100, 250, 600, 1500, 3000 })
            {
                int shown = LatencyProfile.FromSlider(LatencyProfile.ToSlider(millisecond));
                if (Math.Abs(shown - millisecond) > millisecond * 0.05 + 5) roundTrips = false;
            }
            Check("a hand picked buffer survives a trip through the slider", roundTrips);

            Check("a small buffer is strict about lateness", LatencyProfile.LateLimit(100) == 60.0);
            Check("a large buffer is lenient about lateness", LatencyProfile.LateLimit(3000) == 500.0);
            Check("recovery never waits more than half the buffer",
                LatencyProfile.LateLimit(600) == 300.0);

            Check("mode names parse", LatencyProfile.TryParse("buffered", out LatencyMode parsed) &&
                parsed == LatencyMode.Buffered && LatencyProfile.ModeName(parsed) == "buffered");
            Check("an unknown mode name is rejected",
                !LatencyProfile.TryParse("turbo", out LatencyMode ignored) &&
                ignored == LatencyMode.Realtime);
        }

        /// <summary>
        /// A stereo pair must survive losing the multicast record that carries the group id.
        /// </summary>
        private static void StereoRoutingDsp()
        {
            short[] samples = { 0x1234, 0x5678, -0x1234, -0x5678 };
            byte[] left = new byte[8], right = new byte[8];
            StereoRouting.Split(samples, left, right, 0);
            CheckEqual("left PCM contains only left samples, duplicated as stereo",
                new byte[] { 0x12, 0x34, 0x12, 0x34, 0xed, 0xcc, 0xed, 0xcc }, left);
            CheckEqual("right PCM contains only right samples, duplicated as stereo",
                new byte[] { 0x56, 0x78, 0x56, 0x78, 0xa9, 0x88, 0xa9, 0x88 }, right);
            StereoRouting.Split(samples, left, right, 100);
            Check("right balance mutes left and preserves right", left.AsSpan().IndexOfAnyExcept((byte)0) < 0 &&
                right[0] == 0x56 && right[1] == 0x78);
            StereoRouting.Split(samples, left, right, -100);
            Check("left balance mutes right and preserves left", right.AsSpan().IndexOfAnyExcept((byte)0) < 0 &&
                left[0] == 0x12 && left[1] == 0x34);
            StereoRouting.SwapInPlace(samples);
            StereoRouting.Split(samples, left, right, 0);
            Check("swap maps original right to first target and left to second",
                left[0] == 0x56 && left[1] == 0x78 && right[0] == 0x12 && right[1] == 0x34);
            short[] paired = { 0x1234, 0x5678 };
            StereoRouting.ApplyBalanceInPlace(paired, 100);
            byte[] pairedPcm = new byte[4];
            AudioSource.ToBigEndianPcm(paired, paired.Length, pairedPcm);
            CheckEqual("native pair keeps full stereo PCM while balance attenuates left",
                new byte[] { 0, 0, 0x56, 0x78 }, pairedPcm);
            paired = new short[] { 0x1234, 0x5678 };
            StereoRouting.SwapInPlace(paired);
            StereoRouting.ApplyBalanceInPlace(paired, -100);
            AudioSource.ToBigEndianPcm(paired, paired.Length, pairedPcm);
            CheckEqual("native pair swaps channels before physical-side balance",
                new byte[] { 0x56, 0x78, 0, 0 }, pairedPcm);
        }

        private static void Grouping()
        {
            List<ReceiverGroup> announced = ReceiverCatalog.Group(new List<Receiver>
            {
                MakeReceiver("卧室", "192.0.2.128", "11111111-2222-5333-8444-555555555555", "卧室"),
                MakeReceiver("卧室 (2)", "192.0.2.111", "11111111-2222-5333-8444-555555555555", "卧室")
            });
            Check("a shared group id becomes one stereo pair target",
                announced.Count == 1 && announced[0].IsStereoPair && !announced[0].Inferred,
                "groups=" + announced.Count);
            Check("the announced group keeps its name", announced.Count == 1 && announced[0].Name == "卧室");

            List<ReceiverGroup> inferred = ReceiverCatalog.Group(new List<Receiver>
            {
                MakeReceiver("卧室", "192.0.2.128", null, null),
                MakeReceiver("卧室 (2)", "192.0.2.111", null, null)
            });
            Check("name-only suspected pairs remain two independent rows",
                inferred.Count == 2 && inferred.TrueForAll(group => !group.IsStereoPair && group.IsSuspectedPair && group.Members.Count == 1),
                "groups=" + inferred.Count);
            Check("suspected rows share only a name hint",
                inferred.Count == 2 && inferred[0].InferredPairName == "卧室" && inferred[1].InferredPairName == "卧室");
            PlaybackRoute inferredTwo = PlaybackRoute.Resolve(inferred);
            Check("two suspected rows can be selected for manual L/R",
                inferredTwo != null && inferredTwo.SplitStereo && !inferredTwo.NativePair);

            List<ReceiverGroup> separate = ReceiverCatalog.Group(new List<Receiver>
            {
                MakeReceiver("厨房", "192.0.2.100", null, null),
                MakeReceiver("客厅", "192.0.2.101", null, null)
            });
            Check("unrelated speakers are never paired up by name",
                separate.Count == 2 && !separate[0].IsGroup && !separate[1].IsGroup,
                "groups=" + separate.Count);
            PlaybackRoute original = PlaybackRoute.Resolve(announced[0], null, false);
            PlaybackRoute native = PlaybackRoute.Resolve(announced[0], null, true);
            PlaybackRoute guessed = PlaybackRoute.Resolve(new List<ReceiverGroup> { inferred[0] });
            PlaybackRoute alone = PlaybackRoute.Resolve(separate[0], null, false);
            PlaybackRoute manual = PlaybackRoute.Resolve(separate[0], separate[1], true);
            Check("one standalone target opens only its own receiver",
                alone != null && alone.Target.Members.Count == 1 && !alone.SplitStereo);
            Check("existing pair stays playable as one logical target",
                original != null && ReferenceEquals(original.Target, announced[0]) && !original.SplitStereo);
            Check("existing pair is directly playable in stereo mode without another selection",
                native != null && native.NativePair && !native.SplitStereo &&
                ReferenceEquals(native.Target, announced[0]));
            Check("one suspected row sends full stereo only to that receiver",
                guessed != null && !guessed.NativePair && !guessed.SplitStereo && guessed.Target.Members.Count == 1);
            Check("two independent speakers receive manual L/R routing",
                manual != null && manual.SplitStereo && !manual.NativePair &&
                ReferenceEquals(manual.Target.Members[0], separate[0].Members[0]) &&
                ReferenceEquals(manual.Target.Members[1], separate[1].Members[0]));
            Check("second speaker is required for manual stereo",
                PlaybackRoute.Resolve(separate[0], null, true) == null);
            Check("the same independent speaker cannot occupy both channels",
                PlaybackRoute.Resolve(separate[0], separate[0], true) == null);
            Check("a configured pair never adds an unrelated third receiver",
                ReferenceEquals(PlaybackRoute.Resolve(announced[0], separate[0], true).Target, announced[0]));

            PlaybackRoute checkedOne = PlaybackRoute.Resolve(new List<ReceiverGroup> { separate[0] });
            PlaybackRoute checkedTwo = PlaybackRoute.Resolve(new List<ReceiverGroup> { separate[0], separate[1] });
            PlaybackRoute checkedNative = PlaybackRoute.Resolve(new List<ReceiverGroup> { announced[0] });
            Check("checked list with one standalone uses full stereo",
                checkedOne != null && !checkedOne.SplitStereo && !checkedOne.NativePair);
            Check("checked list with two standalones assigns L/R",
                checkedTwo != null && checkedTwo.SplitStereo && checkedTwo.Target.Members.Count == 2);
            Check("checked native pair remains a single logical target",
                checkedNative != null && checkedNative.NativePair && !checkedNative.SplitStereo);
            Check("checked list rejects a third target",
                PlaybackRoute.Resolve(new List<ReceiverGroup> { separate[0], separate[1], announced[0] }) == null);
            Check("empty selection cannot play", PlaybackRoute.Resolve(new List<ReceiverGroup>()) == null);
            Check("single device ignores saved balance", !checkedOne.SupportsBalance && checkedOne.EffectiveBalance(100) == 0);
            Check("two devices and native pairs retain balance",
                checkedTwo.SupportsBalance && checkedTwo.EffectiveBalance(-70) == -70 && checkedNative.SupportsBalance);
            Check("native pair cannot be combined with an independent target",
                PlaybackRoute.Resolve(new List<ReceiverGroup> { announced[0], separate[0] }) == null);

            Receiver incompleteReceiver = MakeReceiver("书房", "192.0.2.102",
                "incomplete-group", "书房");
            ReceiverGroup incomplete = ReceiverCatalog.Group(new List<Receiver> { incompleteReceiver })[0];
            PlaybackRoute incompleteRoute = PlaybackRoute.Resolve(new List<ReceiverGroup> { incomplete });
            Check("incomplete native pair is not confirmed",
                incomplete.IsIncompleteGroup && !incomplete.IsStereoPair && incompleteRoute != null &&
                !incompleteRoute.NativePair);

            Check("stable device id is preferred over name and address",
                DevicePreferenceForTest(separate[0].Members[0]) == "AA:BB:CC:DD:EE:64");
        }

        private static string DevicePreferenceForTest(Receiver receiver)
        {
            return !string.IsNullOrEmpty(receiver.DeviceId) ? receiver.DeviceId : receiver.Key;
        }

        private static void LivePlaybackDsp()
        {
            LivePlaybackControl control = new LivePlaybackControl(true);
            short[] samples = { 1000, -2000, 3000, -4000 };
            control.Balance = 100;
            control.PrepareBlock(samples, 44100);
            control.ApplyBalance(samples);
            Check("live balance updates the current block", samples[0] == 0 && samples[1] == -2000);
            foreach (int rate in new[] { 44100, 48000 })
            {
                foreach (ChannelTest channel in new[] { ChannelTest.Left, ChannelTest.Right, ChannelTest.Stereo })
                {
                    short[] test = new short[rate * 2];
                    control.StartTest(channel);
                    control.PrepareBlock(test, rate);
                    control.ApplyBalance(test);
                    long left = 0, right = 0;
                    for (int i = 0; i < test.Length; i += 2) { left += Math.Abs((int)test[i]); right += Math.Abs((int)test[i + 1]); }
                    Check("live " + channel + " test ignores balance at " + rate,
                        (channel == ChannelTest.Right ? left == 0 : left > 0) &&
                        (channel == ChannelTest.Left ? right == 0 : right > 0));
                    control.PrepareBlock(test, rate);
                    control.PrepareBlock(test, rate);
                    short[] restored = { 1000, -2000 };
                    control.PrepareBlock(restored, rate);
                    control.ApplyBalance(restored);
                    Check("test restores source and balance after 3 seconds at " + rate,
                        restored[0] == 0 && restored[1] == -2000 && control.Balance == 100);
                }
            }
            LivePlaybackControl single = new LivePlaybackControl(false);
            single.Balance = -100;
            short[] original = { 1234, -5678 };
            single.PrepareBlock(original, 44100);
            single.ApplyBalance(original);
            Check("single device live processing keeps both channels unchanged",
                single.Balance == 0 && original[0] == 1234 && original[1] == -5678);
        }

        private static void DiscoveryIdentity()
        {
            List<MdnsRecord> records = new List<MdnsRecord>();
            Action<string, string, string, string, int> add = (service, host, ip, txt, port) =>
            {
                records.Add(new MdnsRecord { Type = MdnsRecord.TypeA, Name = host, Text = ip });
                records.Add(new MdnsRecord { Type = MdnsRecord.TypeSrv, Name = service, Text = host + ":" + port });
                records.Add(new MdnsRecord { Type = MdnsRecord.TypeTxt, Name = service, Text = txt });
            };
            add("卧室._airplay._tcp.local", "speaker-a.local", "192.0.2.1", "deviceid=AA:BB:CC:DD:EE:01", 7000);
            add("AABBCCDDEE01@卧室._raop._tcp.local", "speaker-a.local", "192.0.2.1", "am=HomePod", 5000);
            add("AABBCCDDEE02@卧室._raop._tcp.local", "speaker-b.local", "192.0.2.2", "am=HomePod", 5000);
            List<Receiver> found = ReceiverCatalog.Build(records);
            Check("same display names do not merge different physical device IDs", found.Count == 2);
            Receiver first = found.Find(receiver => receiver.DeviceId == "AA:BB:CC:DD:EE:01");
            Check("AirPlay and RAOP advertisements merge by device ID while retaining AirPlay port",
                first != null && first.Port == 7000 && first.Model == "HomePod");
            Check("RAOP MAC identity is available for stable selection",
                found.Exists(receiver => receiver.DeviceId == "AA:BB:CC:DD:EE:02"));
            Receiver duplicate = MakeReceiver("改名", "192.0.2.99", null, null);
            duplicate.Txt = TxtRecord.Parse("deviceid=AA:BB:CC:DD:EE:01");
            List<ReceiverGroup> left = ReceiverCatalog.Group(new List<Receiver> { first });
            List<ReceiverGroup> right = ReceiverCatalog.Group(new List<Receiver> { duplicate });
            Check("same physical ID cannot occupy both L and R despite different names/IPs",
                PlaybackRoute.Resolve(new List<ReceiverGroup> { left[0], right[0] }) == null);
            Check("group catalog deduplicates repeated device IDs",
                ReceiverCatalog.Group(new List<Receiver> { first, duplicate }).Count == 1);
            add("missing-address._airplay._tcp.local", "missing.local", "", "deviceid=AA:BB:CC:DD:EE:03", 7000);
            Check("unresolved mDNS endpoints are not offered as connectable targets", ReceiverCatalog.Build(records).Count == 2);
        }

        private static void SessionFailurePolicy()
        {
            bool belowThreshold = true, feedbackStops = false, mediaStops = false;
            try { Session.Streamer.CheckReceiverHealth("左音响", 9, 2, true); }
            catch (ProtocolException) { belowThreshold = false; }
            try { Session.Streamer.CheckReceiverHealth("右音响", 0, 3, true); }
            catch (ProtocolException error) { feedbackStops = error.Message.Contains("右音响") && error.Message.Contains("整个播放会话"); }
            try { Session.Streamer.CheckReceiverHealth("左音响", 10, 0, true); }
            catch (ProtocolException error) { mediaStops = error.Message.Contains("左音响"); }
            Check("transient receiver errors remain below stop threshold", belowThreshold);
            Check("lost control feedback aborts the dual session and names the receiver", feedbackStops);
            Check("repeated media failures abort playback and name the receiver", mediaStops);
        }

        private static void NativePairDiscovery()
        {
            const string pairId = "22222222-3333-5444-8555-666666666666";
            const string suffix = "+0+33333333-4444-4555-8666-777777777777";
            Receiver a = MakeReceiver("卧室", "192.0.2.1", pairId + "+0", "卧室");
            Receiver b = MakeReceiver("卧室 (2)", "192.0.2.2", pairId + suffix, "卧室");
            a.Txt.AddFrom(TxtRecord.Parse("tsid=" + pairId + " tsm=1 igl=0 pgid=" + pairId + "+0"));
            b.Txt.AddFrom(TxtRecord.Parse("tsid=" + pairId.ToLowerInvariant() + " tsm=1 igl=1"));
            List<ReceiverGroup> groups = ReceiverCatalog.Group(new List<Receiver> { a, b });
            PlaybackRoute route = PlaybackRoute.Resolve(groups);
            Check("real HomePod tsid/tsm metadata groups different suffixed gids into one native pair",
                groups.Count == 1 && groups[0].IsStereoPair && groups[0].StereoPairId == pairId);
            Check("native pair uses the canonical UUID rather than a transient group suffix",
                groups[0].GroupId == pairId && a.GroupId == pairId + "+0" && b.GroupId == pairId + suffix);
            Check("tsid pair keeps a full stereo stream and original EQ/balance route",
                route != null && route.NativePair && !route.SplitStereo && route.SupportsBalance);
            Check("native leader is derived from metadata, not the (2) display suffix",
                ReferenceEquals(groups[0].Leader, b));
            List<ReceiverGroup> partial = ReceiverCatalog.Group(new List<Receiver> { a });
            Check("known native member missing its peer is not an independent output",
                partial[0].IsIncompleteGroup && !PlaybackRoute.Independent(partial[0]) && PlaybackRoute.Resolve(partial) == null);
            Receiver c = MakeReceiver("书房", "192.0.2.3", null, null);
            Check("an incomplete native member cannot be mixed with an independent speaker",
                PlaybackRoute.Resolve(new List<ReceiverGroup> { partial[0], ReceiverCatalog.Group(new List<Receiver> { c })[0] }) == null);

            List<MdnsRecord> records = new List<MdnsRecord>
            {
                new MdnsRecord { Type = MdnsRecord.TypeA, Name = "speaker.local", Text = "192.0.2.1" },
                new MdnsRecord { Type = MdnsRecord.TypeSrv, Name = "speaker._airplay._tcp.local", Text = "speaker.local:7000" },
                new MdnsRecord { Type = MdnsRecord.TypeTxt, Name = "speaker._airplay._tcp.local", Text = "deviceid=AA:BB:CC:DD:EE:01 gid=old igl=0" },
                new MdnsRecord { Type = MdnsRecord.TypeTxt, Name = "speaker._airplay._tcp.local", Text = "deviceid=AA:BB:CC:DD:EE:01 gid=" + pairId + "+0 igl=1 tsid=" + pairId + " tsm=1" }
            };
            Receiver refreshed = ReceiverCatalog.Build(records)[0];
            Check("latest TXT snapshot replaces stale group and leader information",
                refreshed.StereoPairId == pairId && refreshed.IsGroupLeader && refreshed.GroupId == pairId + "+0");
            records.Add(new MdnsRecord { Type = MdnsRecord.TypeTxt, Name = "speaker._airplay._tcp.local", Text = "deviceid=AA:BB:CC:DD:EE:01" });
            refreshed = ReceiverCatalog.Build(records)[0];
            Check("removed TXT membership does not survive from an older scan round",
                refreshed.StereoPairId.Length == 0 && string.IsNullOrEmpty(refreshed.GroupId));
            a.Txt = TxtRecord.Parse("deviceid=AA:BB:CC:DD:EE:01 tsid=" + pairId + " tsm=0");
            b.Txt = TxtRecord.Parse("deviceid=AA:BB:CC:DD:EE:02 tsid=" + pairId + " tsm=0");
            Check("matching tsid without explicit membership is not promoted to a native pair",
                ReceiverCatalog.Group(new List<Receiver> { a, b }).Count == 2);
            a.Txt = TxtRecord.Parse("deviceid=AA:BB:CC:DD:EE:01 tsid=invalid tsm=1");
            Check("malformed tight-sync IDs cannot confirm a native pair", a.StereoPairId.Length == 0);
        }

        private static Receiver MakeReceiver(string instance, string address, string groupId, string groupName)
        {
            Receiver receiver = new Receiver();
            receiver.Instance = instance;
            receiver.Address = address;
            receiver.Port = 7000;
            receiver.Txt = TxtRecord.Parse(
                "deviceid=AA:BB:CC:DD:EE:" + int.Parse(address.Substring(address.LastIndexOf('.') + 1)).ToString("X2") +
                (groupId == null ? "" : " gid=" + groupId) +
                (groupName == null ? "" : " gpn=" + groupName));
            return receiver;
        }

        private static void SetupAudioFormat()
        {
            byte[] key = new byte[32];
            for (int i = 0; i < key.Length; ++i) key[i] = (byte)(i + 1);
            foreach (bool useAlac in new[] { true, false })
            {
                foreach (int rate in new[] { 44100, 48000 })
                {
                    string label = "SETUP " + (useAlac ? "ALAC" : "PCM") + " " + rate;
                    try
                    {
                        using (AudioPacketizer packetizer = new AudioPacketizer(key, 42, 123456,
                            0x12345678U, rate, useAlac))
                        {
                            int latency = (int)AudioPacketizer.LatencySamples(250, rate);
                            var stream = Session.ReceiverSession.CreateAudioStreamDescription(
                                packetizer, key, 6000, latency);
                            var request = new Dictionary<string, object>
                            {
                                ["streams"] = new List<object> { stream }
                            };
                            var decoded = Plist.AsDictionary(Plist.Read(Plist.Write(request)));
                            var streams = Plist.AsList(decoded["streams"]);
                            Check(label + " serializes one stream", streams.Count == 1);
                            var wire = Plist.AsDictionary(streams[0]);
                            Check(label + " declares matching compression type",
                                Plist.Integer(wire, "ct") == (useAlac ? 2L : 1L));
                            long format = useAlac ? (rate == 44100 ? 1L << 18 : 1L << 20)
                                : (rate == 44100 ? 1L << 11 : 1L << 15);
                            Check(label + " declares matching audioFormat",
                                Plist.Integer(wire, "audioFormat") == format);
                            Check(label + " retains sample rate and 352 frames",
                                Plist.Integer(wire, "sr") == rate && Plist.Integer(wire, "spf") == 352);
                            Check(label + " retains encryption key",
                                ((byte[])wire["shk"]).AsSpan().SequenceEqual(key));
                            Check(label + " retains control port and stream ID",
                                Plist.Integer(wire, "controlPort") == 6000 &&
                                Plist.Integer(wire, "streamConnectionID") == 0x12345678L);
                            Check(label + " retains latency bounds",
                                Plist.Integer(wire, "latencyMin") == latency &&
                                Plist.Integer(wire, "latencyMax") == latency);
                            Check(label + " retains existing real-time stream fields",
                                Plist.Integer(wire, "type") == 96 && (bool)wire["isMedia"] &&
                                !(bool)wire["supportsDynamicStreamID"] && (string)wire["audioMode"] == "default");
                            if (useAlac)
                            {
                                byte[] cookie = (byte[])wire["asc"];
                                Check(label + " sends the same encoder's cookie",
                                    cookie.AsSpan().SequenceEqual(packetizer.MagicCookie));
                                int result = NativeAlacEncoder.ParseMagicCookie(cookie, cookie.Length,
                                    out int cookieRate, out int channels, out int bits, out int frames);
                                Check(label + " cookie agrees with stream declaration",
                                    result == 0 && cookieRate == rate && channels == 2 && bits == 16 && frames == 352);
                            }
                            else Check(label + " omits ALAC cookie", !wire.ContainsKey("asc"));
                        }
                    }
                    catch (Exception ex)
                    {
                        Check(label + " constructs and serializes (native dependency required for ALAC)",
                            false, ex.ToString());
                    }
                }
            }
        }

        private static void TimingAndAudioLayout()
        {
            byte[] timing = AudioPacketizer.TimingPacket(20000, 8820, 123456789UL, 456UL, true);
            Check("PTP timing packet is 28 bytes", timing.Length == 28, "length=" + timing.Length);
            Check("timing packet carries the RTP timestamp",
                timing[4] == 0 && timing[5] == 0 && timing[6] == 0x4e && timing[7] == 0x20);
            Check("timing packet carries the master clock id",
                timing[20] == 0 && timing[21] == 0 && timing[22] == 0 && timing[23] == 0 &&
                timing[24] == 0 && timing[25] == 0 && timing[26] == 1 && timing[27] == 0xc8);

            byte[] ntpTiming = AudioPacketizer.TimingPacket(20000, 8820, 123456789UL, null, false);
            Check("NTP timing packet is 20 bytes", ntpTiming.Length == 20, "length=" + ntpTiming.Length);
            Check("NTP timing packet has no clock id tail",
                ntpTiming[1] == 0xd4 && ntpTiming[3] == 7);
            Check("NTP timing packet maps RTP without shifting latency",
                ntpTiming[16] == 0 && ntpTiming[17] == 0 && ntpTiming[18] == 0x4e && ntpTiming[19] == 0x20);

            byte[] ntp = AudioPacketizer.NtpTimestamp(0);
            Check("NTP epoch conversion", Hex.ToHex(ntp) == "83aa7e8000000000", Hex.ToHex(ntp));

            Check("audio format encodes PCM 44.1 kHz as 1<<11",
                AudioPacketizer.AudioFormat(false, 44100) == 1UL << 11);
            Check("audio format encodes ALAC 44.1 kHz as 1<<18",
                AudioPacketizer.AudioFormat(true, 44100) == 1UL << 18);
            Check("352 frames of stereo PCM is 1408 bytes",
                AudioPacketizer.PcmBytes == 1408);

            byte[] key = new byte[32];
            byte[] pcm = new byte[AudioPacketizer.PcmBytes];
            bool alacAvailable = false;
            try
            {
                using (NativeAlacEncoder encoder = new NativeAlacEncoder(44100))
                {
                    alacAvailable = true;
                    Check("native ALAC encoder constructs at 44100 Hz", true);
                    using (NativeAlacEncoder encoder48000 = new NativeAlacEncoder(48000))
                    {
                        Check("native ALAC encoder constructs at 48000 Hz",
                            encoder48000.MagicCookie.Length > 0);
                    }
                    Check("ALAC magic cookie is available", encoder.MagicCookie.Length > 0);
                    Check("ALAC magic cookie parses",
                        NativeAlacEncoder.ParseMagicCookie(encoder.MagicCookie, encoder.MagicCookie.Length,
                            out int cookieRate, out int cookieChannels, out int cookieBits,
                            out int cookieFrames) == 0);
                    NativeAlacEncoder.ParseMagicCookie(encoder.MagicCookie, encoder.MagicCookie.Length,
                        out cookieRate, out cookieChannels, out cookieBits, out cookieFrames);
                    Check("ALAC magic cookie sample rate is 44100", cookieRate == 44100);
                    Check("ALAC magic cookie has two channels", cookieChannels == 2);
                    Check("ALAC magic cookie has 16-bit samples", cookieBits == 16);
                    Check("ALAC magic cookie has 352 frames per packet", cookieFrames == 352);
                    int encodedLength = encoder.Encode(pcm);
                    Check("ALAC encoding succeeds", encodedLength > 0);
                    Check("ALAC encoded length fits the pooled scratch buffer",
                        encodedLength <= NativeAlacEncoder.MaxEncodedBytes,
                        "length=" + encodedLength);
                }
            }
            catch (Exception error)
            {
                Check("native ALAC encoder loads and reports a clear failure when unavailable",
                    false, error.GetType().Name + ": " + error.Message);
            }

            if (alacAvailable)
            {
                using (AudioPacketizer packetizer = new AudioPacketizer(key, 7, 1000, 0x11223344, 44100, true))
                {
                    AudioPacketizer.PacketBuffer packet = packetizer.Packet(pcm, true);
                    int alacPayloadLength = packet.Length - 12 - 16 - 8;
                    Check("ALAC packet uses the actual variable payload length",
                        alacPayloadLength > 0 && packet.Length == 12 + alacPayloadLength + 16 + 8,
                        "length=" + packet.Length);
                    Check("ALAC packet length is inside the pool capacity",
                        packet.Length <= AudioPacketizer.MaxPacketBytes);
                    Check("first audio packet sets the marker bit", (packet.Buffer[1] & 0x80) != 0);
                    Check("sequence and timestamp are big endian",
                        packet.Buffer[2] == 0 && packet.Buffer[3] == 7 && packet.Buffer[4] == 0 &&
                        packet.Buffer[5] == 0 && packet.Buffer[6] == 0x03 && packet.Buffer[7] == 0xe8);

                    // Decrypt the packet the way a receiver would: AAD is timestamp||ssrc, and the
                    // trailing counter names the nonce. The ciphertext span must stop at Length,
                    // never at the pooled buffer capacity.
                    ulong counter = 0;
                    for (int i = 0; i < 8; i++)
                        counter |= (ulong)packet.Buffer[packet.Length - 8 + i] << (8 * i);
                    Check("ALAC nonce counter starts at zero", counter == 0);

                    byte[] plaintext = new byte[alacPayloadLength];
                    byte[] nonce = new byte[12];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
                    using (ChaCha20Poly1305Compat aead = new ChaCha20Poly1305Compat(key))
                    {
                        aead.Decrypt(nonce, packet.Buffer.AsSpan(12, alacPayloadLength),
                            packet.Buffer.AsSpan(12 + alacPayloadLength, 16), plaintext,
                            packet.Buffer.AsSpan(4, 8));
                    }
                    Check("ALAC encrypted payload decrypts using its actual length",
                        plaintext.Length == alacPayloadLength);

                    DateTime sent = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
                    AudioPacketizer.PacketBuffer retransmitSource = packetizer.Packet(
                        pcm, false, sent, sent.AddSeconds(1));
                    AudioPacketizer.RetransmitResult retransmitResult = packetizer.Retransmit(
                        8, sent.AddMilliseconds(1), out byte[] retransmit);
                    Check("variable-length retransmit preserves the actual media length",
                        retransmitResult == AudioPacketizer.RetransmitResult.Packet &&
                        retransmit.Length == 4 + retransmitSource.Length &&
                        retransmit[1] == 0xd6);
                }
            }

            using (AudioPacketizer pcmPacketizer = new AudioPacketizer(key, 7, 1000,
                0x11223344, 44100, false))
            {
                AudioPacketizer.PacketBuffer packet = pcmPacketizer.Packet(pcm, true);
                Check("audio packet length is header + payload + tag + nonce",
                    packet.Length == 12 + AudioPacketizer.PcmBytes + 16 + 8, "length=" + packet.Length);
                Check("PCM fallback packet sets the marker bit", (packet.Buffer[1] & 0x80) != 0);
                Check("sequence and timestamp are big endian",
                    packet.Buffer[2] == 0 && packet.Buffer[3] == 7 && packet.Buffer[4] == 0 &&
                    packet.Buffer[5] == 0 && packet.Buffer[6] == 0x03 && packet.Buffer[7] == 0xe8);

                // Decrypt the packet the way a receiver would: AAD is timestamp||ssrc, and the
                // trailing counter names the nonce.
                ulong counter = 0;
                for (int i = 0; i < 8; i++)
                    counter |= (ulong)packet.Buffer[packet.Length - 8 + i] << (8 * i);
                Check("trailing nonce counter starts at zero", counter == 0);

                byte[] plaintext = new byte[AudioPacketizer.PcmBytes];
                byte[] nonce = new byte[12];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
                using (ChaCha20Poly1305Compat aead = new ChaCha20Poly1305Compat(key))
                {
                    aead.Decrypt(nonce, packet.Buffer.AsSpan(12, AudioPacketizer.PcmBytes),
                        packet.Buffer.AsSpan(12 + AudioPacketizer.PcmBytes, 16), plaintext,
                        packet.Buffer.AsSpan(4, 8));
                }
                Check("audio payload decrypts with the announced associated data",
                    plaintext.Length == AudioPacketizer.PcmBytes);

                byte[] request = new byte[8] { 0x80, 0x55, 0, 0, 0x01, 0x02, 0, 0x03 };
                Check("retransmit requests parse",
                    AudioPacketizer.ParseRetransmitRequest(request, out ushort start, out ushort limit) &&
                    start == 0x0102 && limit == 3);
            }

            byte[] secondKey = new byte[32];
            secondKey[0] = 1;
            using (AudioPacketizer first = new AudioPacketizer(key, 10, 2000, 0x01020304, 44100, false))
            using (AudioPacketizer second = new AudioPacketizer(secondKey, 20, 2000, 0x05060708, 44100, false))
            {
                AudioPacketizer.PacketBuffer firstPacket = first.Packet(pcm, true);
                AudioPacketizer.PacketBuffer secondPacket = second.Packet(pcm, true);
                Check("independent sessions keep independent sequence numbers",
                    firstPacket.Buffer[2] != secondPacket.Buffer[2] || firstPacket.Buffer[3] != secondPacket.Buffer[3]);
                Check("independent sessions keep independent SSRC values",
                    firstPacket.Buffer[8] != secondPacket.Buffer[8]);
                Check("independent sessions do not share ciphertext state",
                    !firstPacket.Buffer.AsSpan(12, AudioPacketizer.PcmBytes).SequenceEqual(
                        secondPacket.Buffer.AsSpan(12, AudioPacketizer.PcmBytes)));
            }
        }
    }
}

