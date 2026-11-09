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

                // A compiled fake child validates Windows' real argv parsing and exit propagation, without Unity/XR.
                string record = Path.Combine(folder, "fake output.txt");
                string[] forwarded = { "-force-d3d11", "-zantetsuVp3cUnityApi", "plain", "with space", "", "quote\"inside", @"C:\space path\", "\\\"", "\\\\\"", "日本語", "& %PATH% $() ;", "with\ttab", "-zantetsuScenario", "play" };
                var start = new ProcessStartInfo(Path.Combine(folder, "Zantetsu.exe"), CommandLine.Join(forwarded));
                start.UseShellExecute = false;
                start.WorkingDirectory = folder;
                start.EnvironmentVariables["ZANTETSU_LAUNCHER_TEST_OUTPUT"] = record;
                using (Process launcher = Process.Start(start))
                {
                    if (!launcher.WaitForExit(10000)) { launcher.Kill(); throw new Exception("fake-child launcher timed out"); }
                    Check(launcher.ExitCode == 37, "child exit code not propagated");
                }
                string[] lines = File.ReadAllLines(record, Encoding.UTF8);
                Check(File.ReadAllLines(record + ".launches").Length == 1, "child launched more than once");
                Check(lines[0] == "command-line" && lines[1] == "explicit -force-d3d11", "child environment reason");
                Check(lines.Length == forwarded.Length + 2, "argv count changed");
                for (int i = 0; i < forwarded.Length; i++) Check(Encoding.UTF8.GetString(Convert.FromBase64String(lines[i + 2])) == forwarded[i], "argv differs at " + i);

                string moved = Path.Combine(folder, "copied folder 日本語");
                Directory.CreateDirectory(moved);
                foreach (string name in new[] { "Zantetsu.exe", "起動設定.exe", "CutWorldSandbox.exe" }) File.Copy(Path.Combine(folder, name), Path.Combine(moved, name));
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
                Console.WriteLine("PASS " + checks + " checks; fake child only; no Unity, XR or real Player was started.");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
