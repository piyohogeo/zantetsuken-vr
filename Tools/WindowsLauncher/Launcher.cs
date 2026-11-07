using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Zantetsu.Core;

namespace Zantetsu.WindowsLauncher
{
    // This executable never loads Unity or OpenXR. It chooses one child process, before its graphics device exists.
    internal static class Launcher
    {
        [STAThread]
        private static int Main(string[] arguments)
        {
            return Run(arguments);
        }

        internal static int Run(string[] arguments)
        {
            try
            {
                LaunchRequest request = LaunchRequest.Parse(arguments);
                string preference = WindowsGraphicsPreference.Auto;
                string loadFailure = null;
                // An explicit API remains usable even when a saved preference is damaged.
                if (request.ShowSettings || request.ExplicitApi == null)
                {
                    try { preference = WindowsGraphicsPreference.Load(); }
                    catch (Exception e)
                    {
                        if (!request.ShowSettings) throw;
                        loadFailure = "保存した設定を読み込めません。選び直して保存してください。\r\n" + e.Message;
                    }
                }

                if (request.ShowSettings)
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new SettingsForm(preference, request.ExplicitApi, loadFailure))
                    {
                        if (form.ShowDialog() != DialogResult.OK) return 0;
                        preference = form.Preference;
                    }
                }

                Detection detection = request.ExplicitApi == null && preference == WindowsGraphicsPreference.Auto
                    ? MachineDetection.ReadBounded(1500) : new Detection(false, "hardware probe not needed");
                LaunchDecision decision = LaunchDecision.Select(request, preference, detection);
                string directory = AppDomain.CurrentDomain.BaseDirectory;
                string player = Path.Combine(directory, "CutWorldSandbox.exe");
                if (!File.Exists(player)) throw new FileNotFoundException("同じフォルダーに CutWorldSandbox.exe が見つかりません。", player);

                var start = new ProcessStartInfo(player, CommandLine.Join(decision.Arguments));
                start.WorkingDirectory = directory;
                start.UseShellExecute = false;
                start.EnvironmentVariables[WindowsGraphicsPreference.SourceEnvironment] = decision.Source;
                start.EnvironmentVariables[WindowsGraphicsPreference.ReasonEnvironment] = decision.Reason;
                WriteLog("launch api=" + decision.Api + "; source=" + decision.Source + "; reason=" + decision.Reason);
                using (Process child = Process.Start(start))
                {
                    if (child == null) throw new IOException("Player を起動できませんでした。");
                    // No restart, no watchdog: the child owns its lifetime, including manual play and shutdown.
                    child.WaitForExit();
                    int exit = child.ExitCode;
                    WriteLog("child exit=" + exit);
                    return exit;
                }
            }
            catch (Exception e)
            {
                WriteLog("launch refused: " + e.Message);
                MessageBox.Show(e.Message, "起動できません", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 22;
            }
        }

        private static void WriteLog(string message)
        {
            try
            {
                string folder = Path.GetDirectoryName(WindowsGraphicsPreference.SettingsPath);
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "launcher.log"), DateTimeOffset.Now.ToString("o") + " " + message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception) { /* A log failure does not prevent a user from launching. The child receives the reason too. */ }
        }
    }

    internal sealed class LaunchRequest
    {
        internal string[] Arguments;
        internal string ExplicitApi;
        internal bool ShowSettings;
        internal bool NativeAsked;
        internal bool UnityAsked;

        internal static LaunchRequest Parse(string[] arguments)
        {
            var result = new LaunchRequest();
            var kept = new List<string>();
            bool d11 = false, d12 = false;
            foreach (string argument in arguments)
            {
                if (argument == "--settings") { result.ShowSettings = true; continue; }
                if (IsOtherGraphicsApi(argument))
                    throw new ArgumentException("この Player が対応する描画方式は Direct3D 11 と Direct3D 12 です。別の描画方式の起動引数を外してください: " + argument);
                kept.Add(argument);
                d11 |= argument == "-force-d3d11";
                d12 |= argument == "-force-d3d12";
                // Match VpNativeDrawSetup.ReadRequest exactly: Unity's explicit route wins over native.
                result.NativeAsked |= argument == "-zantetsuVp3cNative";
                result.UnityAsked |= argument == "-zantetsuVp3cUnityApi";
            }
            if (d11 && d12) throw new ArgumentException("Direct3D 11 と Direct3D 12 が両方指定されています。どちらか一方にしてください。");
            result.ExplicitApi = d11 ? WindowsGraphicsPreference.Direct3D11 : d12 ? WindowsGraphicsPreference.Direct3D12 : null;
            result.Arguments = kept.ToArray();
            return result;
        }

        private static bool IsOtherGraphicsApi(string argument)
        {
            return string.Equals(argument, "-force-vulkan", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("-force-glcore", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("-force-gles", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "-force-opengl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "-force-metal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "-force-d3d9", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "-force-d3d10", StringComparison.OrdinalIgnoreCase);
        }
    }

    // A second entry point, compiled from the same source. Unlike an absolute .lnk it survives moving the Player folder.
    internal static class SettingsLauncher
    {
        [STAThread]
        private static int Main(string[] arguments)
        {
            return Launcher.Run(WithSettings(arguments));
        }

        internal static string[] WithSettings(string[] arguments)
        {
            var result = new string[arguments.Length + 1];
            result[0] = "--settings";
            Array.Copy(arguments, 0, result, 1, arguments.Length);
            return result;
        }
    }

    internal sealed class LaunchDecision
    {
        internal string Api;
        internal string Source;
        internal string Reason;
        internal string[] Arguments;

        internal static LaunchDecision Select(LaunchRequest request, string preference, Detection detection)
        {
            if (!WindowsGraphicsPreference.IsValid(preference)) throw new ArgumentException("保存された描画設定が不正です。");
            var result = new LaunchDecision();
            if (request.ExplicitApi != null)
            {
                result.Api = request.ExplicitApi;
                result.Source = "command-line";
                result.Reason = "explicit -force-" + result.Api;
            }
            else if (preference != WindowsGraphicsPreference.Auto)
            {
                result.Api = preference;
                result.Source = "saved";
                result.Reason = "saved graphics preference " + preference;
            }
            else
            {
                result.Api = detection.Compatibility ? WindowsGraphicsPreference.Direct3D11 : WindowsGraphicsPreference.Direct3D12;
                result.Source = "auto";
                result.Reason = detection.Reason;
            }

            if (result.Api == WindowsGraphicsPreference.Direct3D11 && request.NativeAsked && !request.UnityAsked)
                throw new ArgumentException("互換 Direct3D 11 は native 描画指定と併用できません。-zantetsuVp3cNative を外すか、-zantetsuVp3cUnityApi を明示してください。");
            var args = new List<string>(request.Arguments);
            if (request.ExplicitApi == null) args.Add("-force-" + result.Api);
            if (result.Api == WindowsGraphicsPreference.Direct3D11 && !request.UnityAsked) args.Add("-zantetsuVp3cUnityApi");
            result.Arguments = args.ToArray();
            return result;
        }
    }

    internal sealed class Detection
    {
        internal readonly bool Compatibility;
        internal readonly string Reason;
        internal Detection(bool compatibility, string reason) { Compatibility = compatibility; Reason = reason; }
    }

    internal sealed class Adapter
    {
        internal string Name;
        internal string PnpId;
        internal string Driver;
    }

    internal static class MachineDetection
    {
        internal static Detection ReadBounded(int milliseconds)
        {
            Detection result = null;
            var thread = new Thread(delegate()
            {
                try
                {
                    string runtime = null;
                    using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (RegistryKey key = machine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1", false))
                        runtime = key == null ? null : key.GetValue("ActiveRuntime") as string;
                    var adapters = new List<Adapter>();
                    var options = new EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromMilliseconds(milliseconds) };
                    using (var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController", options))
                    using (ManagementObjectCollection records = searcher.Get())
                    {
                        foreach (ManagementObject record in records)
                        {
                            using (record)
                                adapters.Add(new Adapter { Name = record["Name"] as string, PnpId = record["PNPDeviceID"] as string, Driver = record["DriverVersion"] as string });
                            if (adapters.Count > 1) break;
                        }
                    }
                    result = Evaluate(adapters, runtime, Environment.GetEnvironmentVariable("XR_RUNTIME_JSON"));
                }
                catch (Exception) { result = new Detection(false, "standard: graphics environment probe unavailable"); }
            });
            thread.IsBackground = true;
            thread.Name = "Graphics compatibility probe";
            thread.Start();
            // EnumerationOptions.Timeout alone is not a total WMI timeout. Never block launch beyond this join.
            return thread.Join(milliseconds) && result != null ? result : new Detection(false, "standard: graphics environment probe timed out");
        }

        internal static Detection Evaluate(IList<Adapter> adapters, string runtime, string runtimeOverride)
        {
            if (!string.IsNullOrEmpty(runtimeOverride)) return new Detection(false, "standard: explicit XR runtime override; no automatic compatibility match");
            if (adapters == null || adapters.Count != 1) return new Detection(false, "standard: graphics adapter is unknown or ambiguous");
            Adapter adapter = adapters[0];
            bool name = adapter.Name != null && Regex.IsMatch(adapter.Name, @"\bRX\s*570\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool ids = adapter.PnpId != null && Regex.IsMatch(adapter.PnpId, @"^PCI\\VEN_1002&DEV_67DF(?:&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool driver = adapter.Driver == "31.0.21925.1001";
            string runtimeDirectory = runtime == null ? null : Path.GetDirectoryName(runtime);
            bool meta = runtimeDirectory != null && Regex.IsMatch(runtime, @"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)
                && string.Equals(Path.GetFileName(runtime), "oculus_openxr_64.json", StringComparison.OrdinalIgnoreCase)
                && (runtimeDirectory.IndexOf("Oculus", StringComparison.OrdinalIgnoreCase) >= 0 || runtimeDirectory.IndexOf("Meta Horizon", StringComparison.OrdinalIgnoreCase) >= 0);
            if (name && ids && driver && meta)
                return new Detection(true, "compatibility: single AMD RX 570 (1002:67DF), driver 31.0.21925.1001, registered Meta OpenXR runtime");
            return new Detection(false, "standard: no known RX 570 / driver / Meta runtime compatibility match");
        }
    }

    internal static class CommandLine
    {
        internal static string Join(IList<string> arguments)
        {
            var result = new StringBuilder();
            for (int i = 0; i < arguments.Count; i++)
            {
                if (i != 0) result.Append(' ');
                result.Append('"');
                int slashes = 0;
                foreach (char character in arguments[i])
                {
                    if (character == '\\') { slashes++; continue; }
                    if (character == '"') result.Append('\\', slashes * 2 + 1);
                    else result.Append('\\', slashes);
                    slashes = 0;
                    result.Append(character);
                }
                result.Append('\\', slashes * 2);
                result.Append('"');
            }
            return result.ToString();
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly ComboBox choice;
        internal string Preference { get; private set; }

        internal SettingsForm(string current, string explicitApi, string loadFailure)
        {
            Text = "起動設定";
            Font = new Font("Yu Gothic UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 295);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            var title = new Label { Text = "描画方式", AutoSize = true, Location = new Point(22, 20) };
            choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(22, 50), Width = 510 };
            choice.Items.AddRange(new object[] { "自動（推奨）", "標準 Direct3D 12", "互換 Direct3D 11" });
            choice.SelectedIndex = current == WindowsGraphicsPreference.Direct3D11 ? 2 : current == WindowsGraphicsPreference.Direct3D12 ? 1 : 0;
            var explanation = new Label { AutoSize = false, Location = new Point(22, 91), Size = new Size(510, 100), Text =
                "自動は通常 Direct3D 12 を使い、確認済みの RX 570・ドライバーと Meta の設定では Direct3D 11 を使います。\r\n画面が黒くなる場合は「互換 Direct3D 11」を選んでください。\r\n画質設定は変更しません。変更は次の起動から反映されます。" };
            var note = new Label { AutoSize = false, Location = new Point(22, 191), Size = new Size(510, 45), ForeColor = loadFailure != null || explicitApi != null ? Color.DarkRed : SystemColors.ControlText,
                Text = loadFailure ?? (explicitApi != null ? "今回は起動引数の " + explicitApi.ToUpperInvariant() + " が、保存する設定より優先されます。" : "この画面はヘッドセットを使わず操作できます。") };
            var launch = new Button { Text = "保存して起動", Location = new Point(290, 245), Size = new Size(130, 32) };
            var cancel = new Button { Text = "キャンセル", Location = new Point(430, 245), Size = new Size(102, 32), DialogResult = DialogResult.Cancel };
            launch.Click += delegate
            {
                launch.Enabled = false;
                string selected = choice.SelectedIndex == 2 ? WindowsGraphicsPreference.Direct3D11 : choice.SelectedIndex == 1 ? WindowsGraphicsPreference.Direct3D12 : WindowsGraphicsPreference.Auto;
                try { WindowsGraphicsPreference.Save(selected); Preference = selected; DialogResult = DialogResult.OK; Close(); }
                catch (Exception e) { launch.Enabled = true; MessageBox.Show(this, "設定を保存できませんでした。\r\n" + e.Message, "起動設定", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            Controls.AddRange(new Control[] { title, choice, explanation, note, launch, cancel });
            AcceptButton = launch; CancelButton = cancel;
        }
    }
}
