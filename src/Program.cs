using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using AirStereo.Audio;
using AirStereo.Protocol;
using AirStereo.Session;

namespace AirStereo
{
    /// <summary>
    /// AirStereo - a group aware AirPlay sender for Windows. It treats a HomePod stereo
    /// pair as one target and drives both halves from a single clock.
    /// </summary>
    internal static class Program
    {
        private const string InstanceMutexName = "Local\\AirStereo.SingleInstance.1";
        private const string WindowTitle = "AirStereo · 立体声 AirPlay 发送器";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        private static bool IsGuiRequest(string[] args)
        {
            return args.Length == 0 || args[0].Equals("gui", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("window", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ActivateExistingWindow()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                IntPtr window = FindWindow(null, WindowTitle);
                if (window != IntPtr.Zero)
                {
                    if (IsIconic(window)) ShowWindowAsync(window, 9);
                    ShowWindowAsync(window, 5);
                    SetForegroundWindow(window);
                    return true;
                }
                Thread.Sleep(50);
            }
            return false;
        }

        private static int Main(string[] args)
        {
            using (Mutex singleInstance = new Mutex(true, InstanceMutexName, out bool created))
            {
                if (IsGuiRequest(args) && !created)
                {
                    ActivateExistingWindow();
                    return 0;
                }

                return Run(args);
            }
        }

        private static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                // Double-clicked, or started with no command: show the window.
                return Ui.MainForm.Run();
            }

            try
            {
                SessionOptions media = ParseMediaArguments(args, out args);
                if (args.Length == 0)
                    throw new ProtocolException("media options need an explicit gui or play command");
                switch (args[0].ToLowerInvariant())
                {
                    case "gui":
                    case "window":
                        return Ui.MainForm.Run(Array.Exists(args, value => value.Equals("--tray", StringComparison.OrdinalIgnoreCase)),
                            media.SourceSilenceDisconnectMilliseconds, media.UseAlac, media.SilenceMode);
                    case "discover":
                        return Discover(Options(args, 1));
                    case "selftest":
                        return SelfTest.Run();
                    case "info":
                        return Info(Options(args, 1));
                    case "play":
                        return Play(Options(args, 1), media);
                    case "volume":
                        return Volume(Options(args, 1));
                    case "devices":
                        return Devices();
                    case "help":
                    case "--help":
                    case "-h":
                        Usage();
                        return 0;
                    default:
                        Console.Error.WriteLine("unknown command: " + args[0]);
                        Usage();
                        return 2;
                }
            }
            catch (ProtocolException error)
            {
                Console.Error.WriteLine("error: " + error.Message);
                return 1;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("error: " + error.GetType().Name + ": " + error.Message);
                return 1;
            }
        }

        private static void Usage()
        {
            Console.WriteLine("AirStereo - stereo aware AirPlay sender for Windows");
            Console.WriteLine();
            Console.WriteLine("  AirStereo gui");
            Console.WriteLine("      Open the window (also what happens when the app is started bare).");
            Console.WriteLine("  AirStereo discover [--seconds N] [--verbose] [--json]");
            Console.WriteLine("      List receivers, grouping HomePod stereo pairs into one target.");
            Console.WriteLine("  AirStereo selftest");
            Console.WriteLine("      Run the protocol and crypto self tests (no network access).");
            Console.WriteLine("  AirStereo info --target <name|index|ip> [--seconds N]");
            Console.WriteLine("      Pair with a target and print what the receiver reports.");
            Console.WriteLine("  AirStereo play --target <name|index|ip> [--duration MS]");
            Console.WriteLine("      Stream to every member of the target from one shared clock.");
            Console.WriteLine("      Sources: --wav FILE | --tone | --pattern | --sweep | system");
            Console.WriteLine("      loopback (the default). --sweep sends one slow sweep through");
            Console.WriteLine("      both speakers: a pair that is not aligned turns it into a warble.");
            Console.WriteLine("  AirStereo volume --target <name|index|ip> [--level <0..100>]");
            Console.WriteLine("      Show the volume the speakers hold, or set it. An absolute volume");
            Console.WriteLine("      only applies inside an RTSP session, which is why a set takes a");
            Console.WriteLine("      moment and is verified on a second connection.");
            Console.WriteLine("  AirStereo devices");
            Console.WriteLine("      List active playback endpoints and mark the loopback default.");
            Console.WriteLine();
            Console.WriteLine("Buffer gears and latency:");
            Console.WriteLine("  --mode realtime|normal|buffered|stable|custom");
            Console.WriteLine("      Receiver buffer presets: 120 / 200 / 500 / 1000 ms.");
            Console.WriteLine("  --latency MS");
            Console.WriteLine("      A custom buffer, 20 to 3000 ms. Smaller is more responsive and");
            Console.WriteLine("      less forgiving of a busy network; the window has a slider for it.");
            Console.WriteLine("  --sync MS|auto");
            Console.WriteLine("      How often the receiver is told where the timeline is (default 100,");
            Console.WriteLine("      the cadence every working sender uses; auto scales with the buffer).");
            Console.WriteLine();
            Console.WriteLine("Options: --rate 44100|48000   --seconds N   --verbose");
            Console.WriteLine("         --pin CODE           --no-ptp       --group-id ID");
            Console.WriteLine("         --gain 0.0..4.0      --handshake-only");
            Console.WriteLine("         --pcm               Experimental PCM fallback; not recommended for multi-device");
            Console.WriteLine("GUI/play experiments: --pcm, --silence-mode=repeat-last|zero");
            Console.WriteLine("      --silence-disconnect-ms=5400000 (90 minutes, source-confirmed silence only)");
            Console.WriteLine("      ALAC continuous media; HomePod acceptance pending. H2 --standby-ms retired.");
        }

        /// <summary>Runtime-only experiment options for both GUI and CLI. Never persisted.</summary>
        internal static SessionOptions ParseMediaArguments(string[] args, out string[] remaining)
        {
            SessionOptions media = new SessionOptions();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> kept = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                int equals = argument.IndexOf('=');
                string key = equals >= 0 ? argument.Substring(0, equals) : argument;
                if (key.Equals("--standby-ms", StringComparison.OrdinalIgnoreCase))
                    throw new ProtocolException("--standby-ms belongs to retired H2: continuous media no longer enters standby");
                if (!key.Equals("--silence-disconnect-ms", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("--silence-mode", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("--pcm", StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add(argument);
                    continue;
                }
                if (!seen.Add(key)) throw new ProtocolException(key + " must be specified only once");
                if (key.Equals("--pcm", StringComparison.OrdinalIgnoreCase))
                {
                    if (equals >= 0) throw new ProtocolException("--pcm takes no value");
                    media.UseAlac = false;
                    continue;
                }
                if (equals < 0 && i + 1 >= args.Length) throw new ProtocolException(key + " needs a value");
                string value = equals >= 0 ? argument.Substring(equals + 1) : args[++i];
                if (key.Equals("--silence-disconnect-ms", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int milliseconds) || milliseconds <= 0)
                        throw new ProtocolException(key + " must be a positive integer of milliseconds");
                    media.SourceSilenceDisconnectMilliseconds = milliseconds;
                }
                else if (value.Equals("repeat-last", StringComparison.OrdinalIgnoreCase)) media.SilenceMode = SilenceFrameMode.RepeatLast;
                else if (value.Equals("zero", StringComparison.OrdinalIgnoreCase)) media.SilenceMode = SilenceFrameMode.Zero;
                else throw new ProtocolException("--silence-mode must be repeat-last or zero");
            }
            remaining = kept.ToArray();
            return media;
        }

        private static Dictionary<string, string> Options(string[] args, int start)
        {
            Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = start; i < args.Length; i++)
            {
                string argument = args[i];
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ProtocolException("unexpected argument: " + argument);
                }
                string key = argument.Substring(2);
                bool takesValue = key != "verbose" && key != "json" && key != "tone" &&
                    key != "pattern" && key != "no-ptp" && key != "handshake-only" &&
                    key != "pcm";
                if (takesValue)
                {
                    if (i + 1 >= args.Length) throw new ProtocolException("--" + key + " needs a value");
                    options[key] = args[++i];
                }
                else
                {
                    options[key] = "true";
                }
            }
            return options;
        }

        private static bool Flag(Dictionary<string, string> options, string name)
        {
            return options.ContainsKey(name);
        }

        private static string Value(Dictionary<string, string> options, string name, string fallback)
        {
            return options.TryGetValue(name, out string value) ? value : fallback;
        }

        private static int IntValue(Dictionary<string, string> options, string name, int fallback)
        {
            if (!options.TryGetValue(name, out string value)) return fallback;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                throw new ProtocolException("--" + name + " must be a number");
            }
            return parsed;
        }

        private static int Discover(Dictionary<string, string> options)
        {
            int seconds = IntValue(options, "seconds", 6);
            Discovery found = AirStereoApi.Discover(seconds);
            MdnsBrowser.Result result = found.Last;
            if (result.InterfacesJoined == 0)
            {
                Console.Error.WriteLine("No usable IPv4 network interface was found; discovery cannot run.");
                return 1;
            }

            List<Receiver> receivers = found.Receivers;
            List<ReceiverGroup> groups = found.Groups;
            foreach (string warning in result.Warnings) Console.Error.WriteLine("warning: " + warning);

            if (Flag(options, "json"))
            {
                Console.WriteLine(ToJson(groups, result, receivers));
                return groups.Count > 0 ? 0 : 3;
            }

            Console.WriteLine("AirStereo · discovery");
            Console.WriteLine("  mDNS packets: " + result.PacketsReceived +
                "   records: " + result.Records.Count +
                "   endpoints: " + receivers.Count +
                "   targets: " + groups.Count);
            if (found.Rounds > 1)
            {
                Console.WriteLine("  browse rounds: " + found.Rounds +
                    " (a receiver announced a group it had not finished describing)");
            }
            Console.WriteLine();

            if (groups.Count == 0)
            {
                Console.WriteLine("No AirPlay receivers were announced on this network.");
                Console.WriteLine("Check that this PC and the speakers are on the same reachable LAN.");
                return 3;
            }

            int index = 1;
            foreach (ReceiverGroup group in groups)
            {
                Console.WriteLine("[" + index + "] " + group.Describe());
                if (group.Inferred)
                {
                    Console.WriteLine("    note: name-only suspected pair; this receiver remains independently selectable.");
                }
                Receiver sample = group.Members.Count > 0 ? group.Members[0] : null;
                if (sample != null)
                {
                    Console.WriteLine("    model " + (sample.Model ?? "unknown") +
                        "   HomePod software " + (sample.OsVersion ?? "unknown") +
                        "   AirTunes " + (sample.SourceVersion ?? "unknown"));
                }
                foreach (Receiver member in group.Members)
                {
                    string role = group.IsGroup
                        ? (ReferenceEquals(member, group.Leader) ? "leader" : "member")
                        : "standalone";
                    Console.WriteLine("      - " + Pad(member.Instance, 18) + " " +
                        Pad(member.Address + ":" + member.Port, 22) + " " + role);
                }
                if (Flag(options, "verbose"))
                {
                    foreach (Receiver member in group.Members)
                    {
                        Console.WriteLine("      " + member.Instance + " service " + member.ServiceType);
                        foreach (KeyValuePair<string, string> pair in member.Txt.Items)
                        {
                            Console.WriteLine("        " + pair.Key + " = " + pair.Value);
                        }
                    }
                }
                index++;
            }

            Console.WriteLine();
            Console.WriteLine("Play to a target with: AirStereo play --target " + groups[0].Name);
            return 0;
        }

        private static List<ReceiverGroup> Resolve(Dictionary<string, string> options, out string selector)
        {
            selector = Value(options, "target", null);
            if (string.IsNullOrEmpty(selector))
            {
                throw new ProtocolException("--target is required (name, index or IP address)");
            }

            int seconds = IntValue(options, "seconds", 5);
            List<ReceiverGroup> groups = AirStereoApi.Discover(seconds).Groups;

            if (groups.Count == 0) throw new ProtocolException("no AirPlay receivers were found on this network");

            if (int.TryParse(selector, out int index) && index >= 1 && index <= groups.Count)
            {
                return new List<ReceiverGroup> { groups[index - 1] };
            }

            List<ReceiverGroup> matches = new List<ReceiverGroup>();
            foreach (ReceiverGroup group in groups)
            {
                if (string.Equals(group.Name, selector, StringComparison.OrdinalIgnoreCase) ||
                    ContainsMember(group, selector))
                {
                    matches.Add(group);
                }
            }
            if (matches.Count == 0)
            {
                StringBuilder names = new StringBuilder();
                foreach (ReceiverGroup group in groups)
                {
                    if (names.Length > 0) names.Append(", ");
                    names.Append(group.Describe());
                }
                throw new ProtocolException("no receiver matched '" + selector + "'. Found: " + names);
            }
            // A name can legitimately appear on more than one candidate. Prefer the target
            // that covers the most physical speakers so a pair never resolves to one half.
            string wanted = selector;
            matches.Sort(delegate (ReceiverGroup left, ReceiverGroup right)
            {
                int byName = ExactName(right, wanted).CompareTo(ExactName(left, wanted));
                if (byName != 0) return byName;
                return right.Members.Count.CompareTo(left.Members.Count);
            });
            return matches;
        }

        private static int ExactName(ReceiverGroup group, string selector)
        {
            return string.Equals(group.Name, selector, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        private static bool ContainsMember(ReceiverGroup group, string selector)
        {
            foreach (Receiver member in group.Members)
            {
                if (string.Equals(member.Instance, selector, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(member.Address, selector, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static int Info(Dictionary<string, string> options)
        {
            List<ReceiverGroup> groups = Resolve(options, out string selector);
            Console.WriteLine("target: " + groups[0].Describe() + "  (matched '" + selector + "')");
            foreach (Receiver member in groups[0].Members)
            {
                Console.WriteLine();
                Console.WriteLine(member.Instance + "  " + member.Address + ":" + member.Port);
                using (RtspConnection connection = new RtspConnection(
                    System.Net.IPAddress.Parse(member.Address), member.Port))
                {
                    RtspMessage reply = connection.Get("/info");
                    Dictionary<string, object> info = Plist.AsDictionary(Plist.Read(reply.Body));
                    Console.WriteLine("  " + Plist.Describe(info));

                    try
                    {
                        Pairing.Transient(connection, Value(options, "pin", Srp.DefaultPin),
                            message => Console.WriteLine("  " + message));
                        Console.WriteLine("  pairing: ok, control channel encrypted");
                    }
                    catch (ProtocolException error)
                    {
                        Console.WriteLine("  pairing failed: " + error.Message);
                    }
                }
            }
            return 0;
        }

        private static int Play(Dictionary<string, string> options, SessionOptions media)
        {
            List<ReceiverGroup> groups = Resolve(options, out string selector);
            ReceiverGroup group = groups[0];

            PlayRequest request = new PlayRequest();
            request.SourceSilenceDisconnectMilliseconds = media.SourceSilenceDisconnectMilliseconds;
            request.SilenceMode = media.SilenceMode;
            request.UseAlac = media.UseAlac;
            request.WavPath = Value(options, "wav", null);
            if (!string.IsNullOrEmpty(request.WavPath)) request.Kind = "wav";
            else if (Flag(options, "tone")) request.Kind = "tone";
            else if (Flag(options, "pattern")) request.Kind = "pattern";
            else if (Flag(options, "mono") || Flag(options, "sweep")) request.Kind = "sweep";
            request.SampleRate = IntValue(options, "rate", 0);
            string modeName = Value(options, "mode", null);
            if (!string.IsNullOrEmpty(modeName))
            {
                if (!LatencyProfile.TryParse(modeName, out LatencyMode mode))
                {
                    throw new ProtocolException(
                        "--mode must be realtime, normal, buffered, stable or custom");
                }
                request.Mode = mode;
            }
            string latencyValue = Value(options, "latency", null);
            if (!string.IsNullOrEmpty(latencyValue))
            {
                if (!int.TryParse(latencyValue, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int latency))
                {
                    throw new ProtocolException("--latency needs a number of milliseconds");
                }
                request.CustomLatencyMs = latency;
                // A bare --latency is a custom buffer unless a gear was named as well.
                if (string.IsNullOrEmpty(modeName)) request.Mode = LatencyMode.Custom;
            }
            request.UsePtp = !Flag(options, "no-ptp");
            string syncValue = Value(options, "sync", null);
            if (!string.IsNullOrEmpty(syncValue))
            {
                if (syncValue.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    request.SyncIntervalMs = 0;
                }
                else if (int.TryParse(syncValue, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int syncMs) && syncMs >= 25 && syncMs <= 1000)
                {
                    request.SyncIntervalMs = syncMs;
                }
                else
                {
                    throw new ProtocolException("--sync must be auto or a number of milliseconds (25..1000)");
                }
            }
            request.HandshakeOnly = Flag(options, "handshake-only");
            request.DurationMs = IntValue(options, "duration", 0);
            request.Pin = Value(options, "pin", Srp.DefaultPin);
            if (request.SampleRate != 0 && request.SampleRate != 44100 && request.SampleRate != 48000)
            {
                throw new ProtocolException("--rate must be 44100 or 48000");
            }

            Console.WriteLine("target: " + group.Describe() + "  (" + group.Members.Count + " member(s))");
            if (group.IsStereoPair)
            {
                Console.WriteLine("stereo pair: both members share one clock and one sample timeline");
                Console.WriteLine("group id:   " + (group.GroupId.Length > 0 ? group.GroupId : "(none announced)"));
            }
            else if (group.Members.Count == 1)
            {
                Console.WriteLine("note: only one physical speaker was found for this target.");
                Console.WriteLine("      if this should be a stereo pair, run discover again; a lost");
                Console.WriteLine("      multicast packet can hide the second speaker.");
            }

            string gainText = Value(options, "gain", null);
            if (!string.IsNullOrEmpty(gainText) &&
                !double.TryParse(gainText, NumberStyles.Float, CultureInfo.InvariantCulture, out request.Gain))
            {
                throw new ProtocolException("--gain must be a number");
            }

            using (ManualResetEventSlim stop = new ManualResetEventSlim(false))
            {
                Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs eventArgs)
                {
                    eventArgs.Cancel = true;
                    stop.Set();
                };

                PlayResult result = AirStereoApi.Play(group, request, stop, Log);
                Console.WriteLine("source: " + result.Source);
                Console.WriteLine("buffer: " + result.LatencyMs + " ms (" +
                    LatencyProfile.ModeName(request.Mode) + ")");
                Console.WriteLine("streamed " + result.Packets + " packets, " +
                    result.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                if (result.LateRecoveries > 0)
                {
                    Console.WriteLine("late recoveries " + result.LateRecoveries +
                        ", skipped slots " + result.SkippedPackets + ", worst lateness " +
                        result.WorstLatenessMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
                }
                if (result.RetransmitRequests > 0)
                {
                    Console.WriteLine("receivers asked for " + result.RetransmitRequests +
                        " retransmission(s), " + result.RetransmittedPackets + " recovered, " +
                        result.LostPackets + " lost");
                }
                if (result.SourceStats.Length > 0) Console.WriteLine("capture: " + result.SourceStats);
                Console.WriteLine("pacing: worst lateness " +
                    result.PacingLatenessMs.ToString("0.0", CultureInfo.InvariantCulture) +
                    " ms, worst gap deviation " +
                    result.PacingJitterMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
                if (result.PtpPackets > 0)
                {
                    Console.WriteLine("timing: the receivers sent " + result.PtpPackets +
                        " datagram(s) back to the sender's clock");
                }
                else
                {
                    Console.WriteLine("timing: the receivers never answered the PTP master; they " +
                        "may be following their own clock instead");
                }
                return 0;
            }
        }

        private static int Devices()
        {
            List<string> devices = LoopbackSource.ListDevices();
            if (devices.Count == 0)
            {
                Console.WriteLine("No active playback endpoints were found.");
                return 1;
            }
            Console.WriteLine("Playback endpoints (loopback captures the [default] one):");
            foreach (string device in devices) Console.WriteLine("  " + device);
            return 0;
        }

        private static int Volume(Dictionary<string, string> options)
        {
            List<ReceiverGroup> groups = Resolve(options, out string selector);
            int percent = IntValue(options, "level", -1);
            if (percent > 100) throw new ProtocolException("--level must be between 0 and 100");
            string pin = Value(options, "pin", Srp.DefaultPin);

            // Without --level this is a pure read, which is the only way to see what the speakers
            // really hold: a percentage read on a connection that just wrote one is unreliable.
            if (percent < 0)
            {
                foreach (Receiver member in groups[0].Members)
                {
                    int now = AirStereoApi.ReadVolume(member, pin, Log);
                    Console.WriteLine("  " + member.Instance + ": " + AirStereoApi.DescribeVolume(now));
                }
                Console.WriteLine("target: " + groups[0].Describe() + "  (matched '" + selector + "')");
                return 0;
            }

            List<string> results = AirStereoApi.SetVolume(groups[0], percent,
                pin, null);
            foreach (string line in results) Console.WriteLine(line);
            Console.WriteLine("target: " + groups[0].Describe() + "  (matched '" + selector + "')");
            return 0;
        }

        private static void Log(string message)
        {
            Console.WriteLine("  " + message);
        }

        private static string Pad(string value, int width)
        {
            if (value == null) value = "";
            if (value.Length >= width) return value;
            return value + new string(' ', width - value.Length);
        }

        private static string ToJson(List<ReceiverGroup> groups, MdnsBrowser.Result result, List<Receiver> receivers)
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\n");
            json.Append("  \"packets\": ").Append(result.PacketsReceived).Append(",\n");
            json.Append("  \"endpoints\": ").Append(receivers.Count).Append(",\n");
            json.Append("  \"targets\": [\n");
            for (int i = 0; i < groups.Count; i++)
            {
                ReceiverGroup group = groups[i];
                json.Append("    {\n");
                json.Append("      \"name\": ").Append(Quote(group.Name)).Append(",\n");
                json.Append("      \"groupId\": ").Append(Quote(group.GroupId)).Append(",\n");
                json.Append("      \"stereoPair\": ").Append(group.IsStereoPair ? "true" : "false").Append(",\n");
                json.Append("      \"members\": [\n");
                for (int m = 0; m < group.Members.Count; m++)
                {
                    Receiver member = group.Members[m];
                    json.Append("        { \"name\": ").Append(Quote(member.Instance));
                    json.Append(", \"address\": ").Append(Quote(member.Address));
                    json.Append(", \"port\": ").Append(member.Port.ToString(CultureInfo.InvariantCulture));
                    json.Append(", \"model\": ").Append(Quote(member.Model));
                    json.Append(", \"os\": ").Append(Quote(member.OsVersion));
                    json.Append(", \"role\": ").Append(Quote(
                        group.Members.Count > 1
                            ? (ReferenceEquals(member, group.Leader) ? "leader" : "member")
                            : "standalone"));
                    json.Append(" }");
                    if (m < group.Members.Count - 1) json.Append(",");
                    json.Append("\n");
                }
                json.Append("      ]\n");
                json.Append("    }");
                if (i < groups.Count - 1) json.Append(",");
                json.Append("\n");
            }
            json.Append("  ]\n}");
            return json.ToString();
        }

        private static string Quote(string value)
        {
            if (value == null) return "null";
            StringBuilder quoted = new StringBuilder("\"");
            foreach (char character in value)
            {
                if (character == '"' || character == '\\') quoted.Append('\\').Append(character);
                else if (character < ' ') quoted.Append("\\u").Append(((int)character).ToString("x4"));
                else quoted.Append(character);
            }
            quoted.Append('"');
            return quoted.ToString();
        }
    }
}
