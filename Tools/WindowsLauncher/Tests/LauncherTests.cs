using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Zantetsu.Core;

namespace Zantetsu.WindowsLauncher
{
    internal static class LauncherTests
    {
        private static int checks;
        private static void Check(bool valid, string what) { checks++; if (!valid) throw new Exception(what); }
        private static void Throws(Action action, string what)
        {
            try { action(); } catch (ArgumentException) { checks++; return; }
            throw new Exception(what);
        }

        private static IEnumerable<Adapter> ObserveRecords(IEnumerable<Adapter> records, List<Adapter> visited)
        {
            foreach (Adapter record in records)
            {
                visited.Add(record);
                yield return record;
            }
        }

        private sealed class ResolverReply
        {
            internal uint Status;
            internal string Command, Source, Reason, Error;
        }

        private static ResolverReply ReadReply(byte[] bytes, int exitCode)
        {
            Check(bytes.Length >= 24 && bytes.Length <= 128 * 1024, "resolver response size");
            using (var stream = new MemoryStream(bytes))
            using (var reader = new BinaryReader(stream, Encoding.Unicode))
            {
                Check(reader.ReadUInt32() == 0x3150475a, "resolver magic is not ZGP1");
                uint status = reader.ReadUInt32();
                uint[] lengths = { reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32() };
                Check(status == exitCode && (status == 0 || status == 22), "resolver payload and process status differ");
                Check(lengths[0] <= 32766 && lengths[1] <= 32 && lengths[2] <= 2048 && lengths[3] <= 4096, "resolver field limit");
                string[] fields = new string[4];
                for (int field = 0; field < fields.Length; field++)
                {
                    var characters = new char[(int)lengths[field]];
                    for (int i = 0; i < characters.Length; i++) characters[i] = (char)reader.ReadUInt16();
                    fields[field] = new string(characters);
                    Check(fields[field].IndexOf('\0') < 0, "resolver field contains NUL");
                }
                Check(stream.Position == stream.Length, "resolver has trailing output");
                Check(status == 0 ? fields[3] == "" : fields[0] == "" && fields[1] == "" && fields[2] == "" && fields[3].Length > 0,
                    "resolver success/failure payload shape");
                return new ResolverReply { Status = status, Command = fields[0], Source = fields[1], Reason = fields[2], Error = fields[3] };
            }
        }

        private static ResolverReply Resolve(string[] arguments, Func<string> load, Func<Detection> detect)
        {
            using (var output = new MemoryStream())
            {
                int exit = GraphicsResolver.Run(arguments, output, load, detect);
                return ReadReply(output.ToArray(), exit);
            }
        }

        private static ResolverReply RunResolverProcess(string folder, string[] arguments)
        {
            var start = new ProcessStartInfo(Path.Combine(folder, "Zantetsu.Graphics.exe"), CommandLine.Join(arguments));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = Path.GetTempPath();
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.EnvironmentVariables[WindowsGraphicsPreference.SourceEnvironment] = "saved";
            start.EnvironmentVariables[WindowsGraphicsPreference.ReasonEnvironment] = "stale inherited choice";
            using (var output = new MemoryStream())
            using (Process process = Process.Start(start))
            {
                var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000)) { process.Kill(); throw new Exception("resolver process timed out"); }
                copy.GetAwaiter().GetResult();
                Check(errors.GetAwaiter().GetResult() == "", "resolver wrote non-protocol diagnostics");
                return ReadReply(output.ToArray(), process.ExitCode);
            }
        }

        private static void CheckResolver(string folder, string settings, Detection known, Detection unknown)
        {
            Func<string> load = () => WindowsGraphicsPreference.Load(settings);
            Func<Detection> probe = () => known;
            string[] original = { "plain", "with space", "", "quote\"inside", @"C:\space path\", "日本語", "with\ttab" };
            WindowsGraphicsPreference.Save(settings, "d3d11");
            ResolverReply reply = Resolve(original, load, probe);
            Check(reply.Status == 0 && reply.Source == "saved", "saved resolver selection");
            Check(reply.Command == CommandLine.Join(LaunchDecision.Select(LaunchRequest.Parse(original), "d3d11", known).Arguments),
                "resolver selected command lost quoted/unicode/empty values");
            WindowsGraphicsPreference.Save(settings, "d3d12");
            reply = Resolve(original, load, probe);
            Check(reply.Status == 0 && reply.Source == "saved" && reply.Command.EndsWith("\"-force-d3d12\""), "resolver reused old saved choice");
            WindowsGraphicsPreference.Save(settings, "auto");
            reply = Resolve(new string[0], load, probe);
            Check(reply.Status == 0 && reply.Source == "auto" && reply.Command == "\"-force-d3d11\" \"-zantetsuVp3cUnityApi\"", "resolver Radeon automatic selection");
            reply = Resolve(new string[0], load, () => unknown);
            Check(reply.Status == 0 && reply.Source == "auto" && reply.Command == "\"-force-d3d12\"", "resolver standard automatic selection");
            Func<string> mustNotLoad = delegate { throw new Exception("preference must not be loaded"); };
            Func<Detection> mustNotProbe = delegate { throw new Exception("hardware must not be probed"); };
            reply = Resolve(new[] { "-force-d3d12" }, mustNotLoad, mustNotProbe);
            Check(reply.Status == 0 && reply.Source == "command-line" && reply.Command == "\"-force-d3d12\"", "explicit resolver priority");
            File.WriteAllText(settings, "wrong");
            reply = Resolve(new string[0], load, mustNotProbe);
            Check(reply.Status == 22 && reply.Error.Contains("invalid"), "damaged preference was accepted");
            reply = Resolve(new[] { "-force-d3d11" }, load, mustNotProbe);
            Check(reply.Status == 0 && reply.Source == "command-line", "damaged preference overrode explicit API");
            reply = Resolve(new[] { "--settings" }, mustNotLoad, mustNotProbe);
            Check(reply.Status == 22 && reply.Error.Contains("起動設定.exe"), "resolver tried to show its own settings UI");
            reply = Resolve(new[] { "-force-d3d11", "-force-d3d12" }, mustNotLoad, mustNotProbe);
            Check(reply.Status == 22, "resolver accepted conflicting APIs");
            reply = Resolve(new[] { "-force-d3d11", "-zantetsuVp3cNative" }, mustNotLoad, mustNotProbe);
            Check(reply.Status == 22, "resolver accepted D11 native conflict");
            reply = Resolve(new[] { "-force-vulkan" }, mustNotLoad, mustNotProbe);
            Check(reply.Status == 22, "resolver accepted unsupported graphics API");
            reply = Resolve(new[] { "-force-d3d12", "nul\0inside" }, mustNotLoad, mustNotProbe);
            Check(reply.Status == 22, "resolver accepted NUL argument");
            int overhead = CommandLine.Join(LaunchDecision.Select(LaunchRequest.Parse(new[] { "x" }), "d3d12", unknown).Arguments).Length - 1;
            reply = Resolve(new[] { new string('x', 32766 - overhead) }, () => "d3d12", mustNotProbe);
            Check(reply.Status == 0 && reply.Command.Length == 32766, "resolver command limit rejected boundary");
            reply = Resolve(new[] { new string('x', 32767 - overhead) }, () => "d3d12", mustNotProbe);
            Check(reply.Status == 22, "resolver accepted an overlong command");
            reply = Resolve(new string[0], () => "auto", () => new Detection(true, new string('r', 2049)));
            Check(reply.Status == 22, "resolver accepted an overlong reason");
            reply = Resolve(new string[0], delegate { throw new IOException(new string('e', 5000) + "\0tail"); }, mustNotProbe);
            Check(reply.Status == 22 && reply.Error.Length == 4096, "resolver failed to bound error payload");
            string[] explicitArguments = { "-force-d3d11", "plain", "", "with space", "日本語", "quote\"inside", @"ends\" };
            reply = RunResolverProcess(folder, explicitArguments);
            Check(reply.Source == "command-line" && reply.Command == CommandLine.Join(LaunchDecision.Select(LaunchRequest.Parse(explicitArguments), "auto", unknown).Arguments),
                "actual resolver output changed arguments or trusted stale provenance");
            reply = RunResolverProcess(folder, new[] { "--settings" });
            Check(reply.Status == 22 && reply.Error.Contains("起動設定.exe"), "actual resolver settings refusal");
        }
        private static int Main(string[] args)
        {
            try
            {
                string folder = args[0];
                string settings = Path.Combine(folder, "settings test", "api.txt");
                Check(WindowsGraphicsPreference.Load(settings) == "auto", "absent preference");
                foreach (string mode in new[] { "auto", "d3d12", "d3d11", "auto" })
                {
                    WindowsGraphicsPreference.Save(settings, mode);
                    Check(WindowsGraphicsPreference.Load(settings) == mode, "save/load " + mode);
                }
                Throws(delegate { WindowsGraphicsPreference.Save(settings, "unknown"); }, "invalid save accepted");
                File.WriteAllText(settings, "wrong");
                bool bad = false;
                try { WindowsGraphicsPreference.Load(settings); } catch (InvalidDataException) { bad = true; }
                Check(bad, "corrupt preference silently accepted");

                var known = new Detection(true, "known compatibility");
                var unknown = new Detection(false, "unknown machine");
                Func<string[], string, Detection, LaunchDecision> select = delegate(string[] a, string p, Detection d) { return LaunchDecision.Select(LaunchRequest.Parse(a), p, d); };
                Check(select(new string[0], "auto", known).Api == "d3d11", "auto known");
                Check(select(new string[0], "auto", unknown).Api == "d3d12", "auto unknown");
                Check(select(new string[0], "d3d11", unknown).Source == "saved", "saved compatibility");
                Check(select(new[] { "-force-d3d12" }, "d3d11", known).Api == "d3d12", "explicit D12 wins");
                Check(select(new[] { "-force-d3d11" }, "d3d12", unknown).Source == "command-line", "explicit D11 wins");
                Check(select(new[] { "-force-d3d11", "-force-d3d11" }, "auto", unknown).Api == "d3d11", "repeated same API");
                Throws(delegate { LaunchRequest.Parse(new[] { "-force-d3d11", "-force-d3d12" }); }, "conflicting APIs accepted");
                foreach (string other in new[] { "-force-vulkan", "-force-glcore", "-force-glcore45", "-force-gles30", "-force-opengl", "-force-metal", "-force-d3d9", "-force-d3d10" })
                {
                    string api = other;
                    Throws(delegate { LaunchRequest.Parse(new[] { api }); }, "other API accepted: " + api);
                    Throws(delegate { LaunchRequest.Parse(new[] { "-force-d3d12", api }); }, "D12 plus other API accepted: " + api);
                }
                Throws(delegate { select(new[] { "-zantetsuVp3cNative" }, "auto", known); }, "D11 native conflict accepted");
                Throws(delegate { select(new[] { "-zantetsuVp3cNative", "-zantetsuvp3cunityapi" }, "d3d11", known); }, "case-mismatched route overrides actual native");
                LaunchDecision both = select(new[] { "-zantetsuVp3cNative", "-zantetsuVp3cUnityApi" }, "d3d11", known);
                Check(both.Api == "d3d11" && both.Arguments.Length == 3, "explicit Unity precedence");
                LaunchRequest settingsRequest = LaunchRequest.Parse(new[] { "--settings", "one", "", "--Settings", "--settings" });
                Check(settingsRequest.ShowSettings && settingsRequest.Arguments.Length == 3 && settingsRequest.Arguments[1] == "" && settingsRequest.Arguments[2] == "--Settings", "only exact --settings is consumed");
                LaunchRequest dedicatedSettings = LaunchRequest.Parse(SettingsLauncher.WithSettings(new[] { "-force-d3d11", "one two" }));
                Check(dedicatedSettings.ShowSettings && dedicatedSettings.Arguments.Length == 2 && dedicatedSettings.Arguments[1] == "one two", "settings executable entry forwards all arguments");

                var gpu = new Adapter { Name = "Radeon RX570 Series", PnpId = @"PCI\VEN_1002&DEV_67DF&SUBSYS_00000000" };
                var monitor = new Adapter { Name = "Meta Virtual Monitor", PnpId = @"ROOT\DISPLAY\0000" };
                var rx5500 = new Adapter { Name = "AMD Radeon RX 5500 XT", PnpId = @"PCI\VEN_1002&DEV_7340&SUBSYS_00000000" };
                // Automatic selection uses adapter identity alone, with no driver or XR runtime inputs.
                foreach (Adapter radeon in new[] {
                    gpu,
                    rx5500,
                    new Adapter { Name = "AMD Radeon RX 5700 XT", PnpId = @"PCI\VEN_1002&DEV_731F&SUBSYS_00000000" },
                    new Adapter { Name = "AMD Radeon RX 7900 XTX", PnpId = @"PCI\VEN_1002&DEV_744C&SUBSYS_00000000" },
                    new Adapter { Name = "Radeon Pro WX 7100", PnpId = @"PCI\VEN_1002&DEV_67C4&SUBSYS_00000000" },
                    new Adapter { Name = "AMD Radeon(TM) Graphics", PnpId = @"PCI\VEN_1002&DEV_164E&SUBSYS_00000000" },
                    new Adapter { Name = "ATI Radeon HD 5700 Series", PnpId = @"PCI\VEN_1002&DEV_68B8" },
                    new Adapter { Name = "amd radeon rx 5500 xt", PnpId = @"pci\ven_1002&dev_7340&subsys_00000000" } })
                {
                    Detection detected = MachineDetection.Evaluate(new[] { radeon });
                    Check(detected.Compatibility, "Radeon model did not select compatibility: " + radeon.Name);
                    LaunchDecision automatic = select(new string[0], "auto", detected);
                    Check(automatic.Api == "d3d11" && automatic.Source == "auto" && automatic.Reason == detected.Reason,
                        "Radeon automatic API, source or reason differs: " + radeon.Name);
                    Check(automatic.Arguments.Length == 2 && automatic.Arguments[0] == "-force-d3d11"
                        && automatic.Arguments[1] == "-zantetsuVp3cUnityApi", "Radeon automatic launch omitted D11 or Unity API arguments");
                }
                foreach (Adapter[] records in new[] { new[] { monitor, gpu }, new[] { gpu, monitor }, new[] { monitor, monitor, gpu } })
                {
                    List<Adapter> candidates = MachineDetection.CollectCandidates(records);
                    Check(candidates.Count == 1 && candidates[0] == gpu, "known virtual monitor affected the bounded candidate loop");
                    Detection detected = MachineDetection.Evaluate(candidates);
                    Check(select(new string[0], "auto", detected).Api == "d3d11", "RX570 plus known virtual monitor did not select compatibility");
                    LaunchDecision saved = select(new string[0], "d3d12", detected);
                    Check(saved.Api == "d3d12" && saved.Source == "saved", "saved API lost priority after virtual monitor exclusion");
                    LaunchDecision explicitApi = select(new[] { "-force-d3d12" }, "d3d11", detected);
                    Check(explicitApi.Api == "d3d12" && explicitApi.Source == "command-line", "explicit API lost priority after virtual monitor exclusion");
                }
                var visited = new List<Adapter>();
                List<Adapter> multiple = MachineDetection.CollectCandidates(ObserveRecords(new[] { monitor, gpu, rx5500, monitor }, visited));
                Check(visited.Count == 3 && multiple.Count == 2 && multiple[0] == gpu && multiple[1] == rx5500,
                    "the WMI candidate loop must read the second physical GPU after a virtual-first record");
                Check(!MachineDetection.Evaluate(multiple).Compatibility, "virtual-first enumeration hid a second physical GPU");
                visited.Clear();
                multiple = MachineDetection.CollectCandidates(ObserveRecords(new[] { gpu, monitor, rx5500, monitor }, visited));
                Check(visited.Count == 3 && multiple.Count == 2, "virtual-second enumeration stopped before the second physical GPU");
                visited.Clear();
                multiple = MachineDetection.CollectCandidates(ObserveRecords(new[] { gpu, rx5500, monitor }, visited));
                Check(visited.Count == 2 && multiple.Count == 2, "candidate collection continued after ambiguity was established");
                var lowerMonitor = new Adapter { Name = "meta virtual monitor", PnpId = @"root\display\0001" };
                Check(MachineDetection.CollectCandidates(new[] { lowerMonitor, gpu }).Count == 1, "known virtual monitor comparison is not case-insensitive");
                foreach (Adapter uncertain in new[] {
                    new Adapter { Name = "Unknown adapter", PnpId = null },
                    new Adapter { Name = "Meta Virtual Monitor", PnpId = null },
                    new Adapter { Name = "Meta Virtual Monitor", PnpId = "" },
                    new Adapter { Name = null, PnpId = @"ROOT\DISPLAY\0000" },
                    new Adapter { Name = "Other Virtual Monitor", PnpId = @"ROOT\DISPLAY\0000" },
                    new Adapter { Name = "Meta Virtual Monitor ", PnpId = @"ROOT\DISPLAY\0000" },
                    new Adapter { Name = "Meta Virtual Monitor", PnpId = @"ROOT\OTHER\0000" },
                    new Adapter { Name = "Meta Virtual Monitor", PnpId = @"ROOT\DISPLAYX\0000" },
                    new Adapter { Name = "Meta Virtual Monitor", PnpId = @"PCI\VEN_1002&DEV_67DF" } })
                {
                    List<Adapter> candidates = MachineDetection.CollectCandidates(new[] { gpu, uncertain });
                    Check(candidates.Count == 2 && !MachineDetection.Evaluate(candidates).Compatibility,
                        "unknown or non-matching adapter was discarded from ambiguity");
                }
                Check(!MachineDetection.Evaluate(MachineDetection.CollectCandidates(new[] { monitor })).Compatibility,
                    "a virtual monitor alone was treated as a known GPU");
                Check(select(new string[0], "auto", MachineDetection.Evaluate(MachineDetection.CollectCandidates(new[] { monitor, rx5500 }))).Api == "d3d11",
                    "RX5500 XT plus virtual monitor did not select compatibility");
                Check(!MachineDetection.Evaluate(new[] { gpu, gpu }).Compatibility, "multiple GPUs");
                var nvidia = new Adapter { Name = "NVIDIA GeForce RTX 3090", PnpId = @"PCI\VEN_10DE&DEV_2204" };
                foreach (Adapter[] mixed in new[] { new[] { gpu, nvidia }, new[] { nvidia, gpu } })
                    Check(!MachineDetection.Evaluate(MachineDetection.CollectCandidates(mixed)).Compatibility, "mixed GPU order selected compatibility");
                Check(!MachineDetection.Evaluate(new Adapter[0]).Compatibility, "no GPU");
                Check(!MachineDetection.Evaluate(null).Compatibility, "unknown GPU list");
                foreach (Adapter other in new[] {
                    new Adapter { Name = "AMD Instinct MI100", PnpId = @"PCI\VEN_1002&DEV_738C" },
                    nvidia,
                    new Adapter { Name = "Intel(R) UHD Graphics 630", PnpId = @"PCI\VEN_8086&DEV_3E92" },
                    new Adapter { Name = "Radeon RX 570 Series", PnpId = @"PCI\VEN_10DE&DEV_67DF" },
                    new Adapter { Name = "NotRadeon RX 570", PnpId = @"PCI\VEN_1002&DEV_67DF" },
                    new Adapter { Name = "RadeonExtra RX 570", PnpId = @"PCI\VEN_1002&DEV_67DF" },
                    new Adapter { Name = null, PnpId = @"PCI\VEN_1002&DEV_67DF" } })
                {
                    Detection detected = MachineDetection.Evaluate(new[] { other });
                    Check(!detected.Compatibility && select(new string[0], "auto", detected).Api == "d3d12",
                        "non-Radeon adapter selected compatibility: " + other.Name);
                }
                foreach (string invalidId in new[] {
                    null, "", @"PCI\VEN_1002", @"PCI\VEN_10020&DEV_67DF", @"PCI\VEN_1002&DEV_67DF0",
                    @"PCI\VEN_1002&DEV_67D", @"PCI\VEN_1002&DEV_67DG", @"ROOT\VEN_1002&DEV_67DF",
                    @"PREFIXPCI\VEN_1002&DEV_67DF", @"PCI\VEN_1002&DEV_67DFjunk" })
                {
                    Check(!MachineDetection.Evaluate(new[] { new Adapter { Name = "AMD Radeon RX 570", PnpId = invalidId } }).Compatibility,
                        "malformed PCI identity selected compatibility: " + invalidId);
                }

                CheckResolver(folder, settings, known, unknown);

                // A compiled fake child validates Windows' real argv parsing and exit propagation, without Unity/XR.
                string record = Path.Combine(folder, "fake output.txt");
                string[] forwarded = { "-force-d3d11", "-zantetsuVp3cUnityApi", "plain", "with space", "", "quote\"inside", @"C:\space path\", "\\\"", "\\\\\"", "日本語", "& %PATH% $() ;", "with\ttab", "-zantetsuScenario", "play" };
                var start = new ProcessStartInfo(Path.Combine(folder, "Zantetsu.exe"), CommandLine.Join(forwarded));
                start.UseShellExecute = false;
                start.WorkingDirectory = folder;
                start.EnvironmentVariables["ZANTETSU_LAUNCHER_TEST_OUTPUT"] = record;
                start.EnvironmentVariables[WindowsGraphicsPreference.SourceEnvironment] = "saved";
                start.EnvironmentVariables[WindowsGraphicsPreference.ReasonEnvironment] = "stale inherited choice";
                using (Process launcher = Process.Start(start))
                {
                    if (!launcher.WaitForExit(10000)) { launcher.Kill(); throw new Exception("fake-child launcher timed out"); }
                    Check(launcher.ExitCode == 37, "child exit code not propagated");
                }
                string[] lines = File.ReadAllLines(record, Encoding.UTF8);
                Check(File.ReadAllLines(record + ".launches").Length == 1, "child launched more than once");
                Check(lines[0] == "" && lines[1] == "", "outer launcher carried stale graphics provenance");
                Check(lines.Length == forwarded.Length + 2, "argv count changed");
                for (int i = 0; i < forwarded.Length; i++) Check(Encoding.UTF8.GetString(Convert.FromBase64String(lines[i + 2])) == forwarded[i], "argv differs at " + i);

                string rawRecord = Path.Combine(folder, "raw output.txt");
                string[] rawArguments = { "plain", "with space", "", "日本語", "quote\"inside", @"ends\" };
                var rawStart = new ProcessStartInfo(Path.Combine(folder, "Zantetsu.exe"), CommandLine.Join(rawArguments));
                rawStart.UseShellExecute = false;
                rawStart.EnvironmentVariables["ZANTETSU_LAUNCHER_TEST_OUTPUT"] = rawRecord;
                using (Process launcher = Process.Start(rawStart))
                {
                    if (!launcher.WaitForExit(10000)) { launcher.Kill(); throw new Exception("raw forwarding timed out"); }
                    Check(launcher.ExitCode == 37, "raw forwarding child exit");
                }
                string[] rawLines = File.ReadAllLines(rawRecord, Encoding.UTF8);
                Check(rawLines.Length == rawArguments.Length + 2, "outer launcher injected graphics arguments");
                for (int i = 0; i < rawArguments.Length; i++) Check(Encoding.UTF8.GetString(Convert.FromBase64String(rawLines[i + 2])) == rawArguments[i], "raw argument differs at " + i);
                string rawCommand = File.ReadAllText(rawRecord + ".commandline", Encoding.UTF8);
                Check(!rawCommand.Contains("-force-") && !rawCommand.Contains("-zantetsuVp3cUnityApi"), "OS command line contains generated options");

                string moved = Path.Combine(folder, "copied folder 日本語");
                Directory.CreateDirectory(moved);
                foreach (string name in new[] { "Zantetsu.exe", "起動設定.exe", "Zantetsu.Graphics.exe", "CutWorldSandbox.exe" }) File.Copy(Path.Combine(folder, name), Path.Combine(moved, name));
                // If the launcher remembered its build location this would now fail: only the copied folder has a child.
                File.Move(Path.Combine(folder, "CutWorldSandbox.exe"), Path.Combine(folder, "FakePlayer.original.exe"));
                string movedRecord = Path.Combine(moved, "moved output.txt");
                var movedStart = new ProcessStartInfo(Path.Combine(moved, "Zantetsu.exe"), CommandLine.Join(forwarded));
                movedStart.UseShellExecute = false;
                movedStart.WorkingDirectory = folder;
                movedStart.EnvironmentVariables["ZANTETSU_LAUNCHER_TEST_OUTPUT"] = movedRecord;
                using (Process launcher = Process.Start(movedStart))
                {
                    if (!launcher.WaitForExit(10000)) { launcher.Kill(); throw new Exception("copied-folder launcher timed out"); }
                    Check(launcher.ExitCode == 37, "copied-folder child exit");
                }
                string[] movedLines = File.ReadAllLines(movedRecord, Encoding.UTF8);
                Check(File.ReadAllLines(movedRecord + ".launches").Length == 1, "copied child launched more than once");
                Check(movedLines.Length == forwarded.Length + 2, "copied-folder argv count");
                for (int i = 0; i < forwarded.Length; i++) Check(Encoding.UTF8.GetString(Convert.FromBase64String(movedLines[i + 2])) == forwarded[i], "copied-folder argv differs at " + i);
                ResolverReply movedReply = RunResolverProcess(moved, new[] { "-force-d3d12", "日本語", "" });
                Check(movedReply.Status == 0 && movedReply.Source == "command-line", "copied-folder resolver failed");
                Console.WriteLine("PASS " + checks + " checks; fake child and graphics resolver only; no Unity, XR or real Player was started.");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
