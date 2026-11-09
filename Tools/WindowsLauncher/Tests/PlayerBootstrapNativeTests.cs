using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

internal static class PlayerBootstrapNativeTests
{
    private static int checks;
    private static int cases;
    private static string production;
    private static string errorFixture;
    private static string fixtures;
    private static string root;

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    private static string ReadText(BinaryReader reader)
    {
        int chars = reader.ReadInt32();
        if (chars < 0 || chars > 65536) throw new InvalidDataException("Invalid fixture text length.");
        byte[] bytes = reader.ReadBytes(chars * 2);
        if (bytes.Length != chars * 2) throw new EndOfStreamException();
        return Encoding.Unicode.GetString(bytes);
    }

    private sealed class Run
    {
        internal string Folder;
        internal string Result;
        internal int ProcessId;
        internal int Exit;
        internal double Seconds;
    }

    private static Run Execute(string mode, string[] arguments, bool errorDialogFixture,
        bool helperPresent = true, bool unityPresent = true, bool entryPresent = true, string source = "saved")
    {
        cases++;
        string folder = Path.Combine(root, cases.ToString("00") + " " + mode + " \u65e5\u672c\u8a9e");
        Directory.CreateDirectory(folder);
        File.Copy(errorDialogFixture ? errorFixture : production, Path.Combine(folder, "CutWorldSandbox.exe"));
        if (helperPresent) File.Copy(Path.Combine(fixtures, "Zantetsu.Graphics.exe"), Path.Combine(folder, "Zantetsu.Graphics.exe"));
        if (unityPresent) File.Copy(Path.Combine(fixtures, entryPresent ? "UnityPlayer.dll" : "UnityPlayer.NoEntry.dll"), Path.Combine(folder, "UnityPlayer.dll"));
        string result = Path.Combine(folder, "observed.bin");
        string working = Path.Combine(root, "different working directory");
        Directory.CreateDirectory(working);
        var start = new ProcessStartInfo(Path.Combine(folder, "CutWorldSandbox.exe"), PlayerBootstrapResolverFixture.Quote(arguments));
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WorkingDirectory = working;
        start.EnvironmentVariables["ZANTETSU_NATIVE_TEST_RESULT"] = result;
        start.EnvironmentVariables["ZANTETSU_NATIVE_HELPER_MODE"] = mode;
        start.EnvironmentVariables["ZANTETSU_NATIVE_TEST_SOURCE"] = source;
        start.EnvironmentVariables["ZANTETSU_GRAPHICS_SOURCE"] = "old inherited provenance";
        start.EnvironmentVariables["ZANTETSU_GRAPHICS_REASON"] = "old inherited reason";
        var elapsed = Stopwatch.StartNew();
        int id, exit;
        using (Process process = Process.Start(start))
        {
            if (process == null) throw new Exception("Fixture did not start.");
            id = process.Id;
            if (!process.WaitForExit(25000))
            {
                process.Kill();
                process.WaitForExit();
                throw new Exception("The fixture did not exit within 25 seconds.");
            }
            exit = process.ExitCode;
        }
        return new Run { Folder = folder, Result = result, ProcessId = id, Exit = exit, Seconds = elapsed.Elapsed.TotalSeconds };
    }

    private static void HelperWasReaped(Run run)
    {
        string path = run.Result + ".helper";
        if (!File.Exists(path)) return;
        int id;
        using (var reader = new BinaryReader(File.OpenRead(path))) id = reader.ReadInt32();
        bool alive = false;
        try { using (Process helper = Process.GetProcessById(id)) alive = !helper.HasExited; }
        catch (ArgumentException) { }
        Check(!alive, "An owned resolver remained alive after bootstrap exit.");
    }

    private static void Success(string mode, string[] arguments, string source = "saved")
    {
        Run run = Execute(mode, arguments, false, source: source);
        Check(run.Exit == 7, "Unity's return code was not preserved.");
        Check(File.Exists(run.Result + ".loaded"), "The fake engine was not loaded.");
        using (var reader = new BinaryReader(File.OpenRead(run.Result)))
        {
            Check(reader.ReadUInt32() == 0x3154555a, "Invalid fake engine record.");
            Check(reader.ReadInt32() == run.ProcessId, "Unity ran in a different process.");
            Check(reader.ReadUInt32() == 1, "UnityMain2 was called more than once.");
            Check(reader.ReadUInt32() == 1, "UnityMain2 did not receive the real module instance.");
            Check(reader.ReadUInt32() == 1, "A custom data folder was passed.");
            Check(reader.ReadUInt32() == 1, "The bootstrap loaded CLR into the Unity process.");
            Check(ReadText(reader) == Path.Combine(run.Folder, "CutWorldSandbox.exe"), "The actual process image name changed.");
            string osCommand = ReadText(reader);
            string unityCommand = ReadText(reader);
            Check(osCommand.Contains("CutWorldSandbox.exe"), "OS command did not retain the Player image.");
            Check(!osCommand.Contains("-force-d3d11") && !osCommand.Contains("-zantetsuVp3cUnityApi"), "Synthetic API arguments reached the OS command line.");
            string expected = mode == "large" ? new string('x', 32766)
                : PlayerBootstrapResolverFixture.Quote(arguments) + " \"-force-d3d11\" \"-zantetsuVp3cUnityApi\"";
            Check(unityCommand == expected, "UnityMain2 arguments differ from the resolver output.");
            Check(ReadText(reader) == source, "The bootstrap trusted inherited graphics provenance.");
            Check(ReadText(reader) == (mode == "large" ? new string('r', 2048) : "fixture selected graphics"), "The graphics reason was not replaced.");
            Check(reader.ReadUInt32() == 1, "NvOptimusEnablement was not preserved.");
            Check(reader.ReadUInt32() == 1, "AmdPowerXpressRequestHighPerformance was not preserved.");
            Check(reader.ReadUInt32() == 618, "The Unity Agility SDK version changed.");
            Check(ReadText(reader) == ".\\D3D12\\", "The Unity Agility SDK path changed.");
            Check(reader.BaseStream.Position == reader.BaseStream.Length, "Unexpected fake engine record tail.");
        }
        using (var reader = new BinaryReader(File.OpenRead(run.Result + ".helper")))
        {
            reader.ReadInt32();
            Check(reader.ReadInt32() == arguments.Length, "The resolver did not receive every original argument.");
            foreach (string argument in arguments) Check(ReadText(reader) == argument, "Quoted or Unicode arguments changed before resolution.");
            Check(reader.BaseStream.Position == reader.BaseStream.Length, "Unexpected resolver fixture record tail.");
        }
        Check(!File.Exists(run.Result + ".error"), "A successful start displayed an error.");
        HelperWasReaped(run);
    }

    private static void Refusal(string mode, bool helperPresent = true, bool unityPresent = true, bool entryPresent = true)
    {
        Run run = Execute(mode, new[] { "original argument" }, true, helperPresent, unityPresent, entryPresent);
        Check(run.Exit == 22, "Invalid resolver output did not refuse startup.");
        Check(!File.Exists(run.Result), "A rejected start entered UnityMain2.");
        Check(File.Exists(run.Result + ".loaded") == (unityPresent && !entryPresent), "A rejected resolver output loaded Unity.");
        using (var reader = new BinaryReader(File.OpenRead(run.Result + ".error")))
        {
            Check(reader.ReadUInt32() == 1, "The bootstrap did not report exactly one error.");
            Check(ReadText(reader).Length > 0, "The error dialog was empty.");
        }
        HelperWasReaped(run);
        if (mode == "timeout" || mode == "closed-pipe-timeout")
            Check(run.Seconds >= 14 && run.Seconds < 23, "The resolver's 15-second deadline was not bounded.");
    }

    private static int Main(string[] arguments)
    {
        try
        {
            production = Path.GetFullPath(arguments[0]);
            errorFixture = Path.GetFullPath(arguments[1]);
            fixtures = Path.GetFullPath(arguments[2]);
            root = Path.GetFullPath(arguments[3]);
            Directory.CreateDirectory(root);
            var original = FileVersionInfo.GetVersionInfo(arguments[4]);
            var copied = FileVersionInfo.GetVersionInfo(production);
            Check(copied.FileVersion == original.FileVersion && copied.ProductName == original.ProductName,
                "The Player's version resources changed.");
            Success("success", new string[0]);
            Success("success", new[] { "space argument", "", "quote\"inside", "slash\\", "\u65e5\u672c\u8a9e", "line\nbreak", "end\\\\", "--unrelated" }, "auto");
            Success("success", new[] { "another start" }, "command-line");
            Success("large", new[] { "large pipe response" });
            foreach (string mode in new[]
            {
                "failure", "empty-command", "bad-magic", "bad-source", "nul", "invalid-unicode", "success-error",
                "bad-status", "success-exit", "failure-exit", "failure-fields", "overlong",
                "truncated-header", "truncated-payload", "extra", "oversized"
            }) Refusal(mode);
            Refusal("success", helperPresent: false);
            Refusal("success", unityPresent: false);
            Refusal("success", entryPresent: false);
            Refusal("timeout");
            Refusal("closed-pipe-timeout");
            File.WriteAllText(Path.Combine(root, "result.json"),
                "{\"passed\":true,\"checks\":" + checks + ",\"cases\":" + cases + "}", new UTF8Encoding(false));
            Console.WriteLine("Native bootstrap tests passed " + checks + " checks across " + cases + " cases.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
