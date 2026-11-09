using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

internal static class PlayerBootstrapResolverFixture
{
    internal static string Quote(string[] arguments)
    {
        var result = new StringBuilder();
        foreach (string value in arguments)
        {
            if (result.Length != 0) result.Append(' ');
            result.Append('"');
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
        }
        return result.ToString();
    }

    private static void Text(BinaryWriter writer, string value)
    {
        writer.Write(value.Length);
        writer.Write(Encoding.Unicode.GetBytes(value));
    }

    private static int Main(string[] arguments)
    {
        string result = Environment.GetEnvironmentVariable("ZANTETSU_NATIVE_TEST_RESULT");
        using (var writer = new BinaryWriter(File.Create(result + ".helper")))
        {
            writer.Write(Process.GetCurrentProcess().Id);
            writer.Write(arguments.Length);
            foreach (string argument in arguments) Text(writer, argument);
        }
        string mode = Environment.GetEnvironmentVariable("ZANTETSU_NATIVE_HELPER_MODE") ?? "success";
        if (mode == "timeout" || mode == "closed-pipe-timeout")
        {
            if (mode == "closed-pipe-timeout") Console.OpenStandardOutput().Close();
            Thread.Sleep(30000);
            return 0;
        }
        string command = Quote(arguments) + " \"-force-d3d11\" \"-zantetsuVp3cUnityApi\"";
        string source = Environment.GetEnvironmentVariable("ZANTETSU_NATIVE_TEST_SOURCE") ?? "saved";
        string reason = "fixture selected graphics";
        string error = "";
        uint status = 0;
        uint magic = 0x3150475a;
        int exit = 0;
        if (mode == "failure" || mode == "failure-fields" || mode == "failure-exit")
        {
            command = mode == "failure-fields" ? "unexpected" : "";
            source = reason = "";
            error = "Fixture graphics refusal.";
            status = 22;
            exit = mode == "failure-exit" ? 0 : 22;
        }
        if (mode == "large") { command = new string('x', 32766); reason = new string('r', 2048); }
        if (mode == "bad-magic") magic++;
        if (mode == "bad-source") source = "inherited-old-value";
        if (mode == "empty-command") command = "";
        if (mode == "nul") command += "\0";
        if (mode == "invalid-unicode") command = "\ud800";
        if (mode == "success-error") error = "unexpected";
        if (mode == "bad-status") { status = 3; exit = 3; }
        if (mode == "success-exit") exit = 22;
        using (var memory = new MemoryStream())
        {
            using (var writer = new BinaryWriter(memory, Encoding.Unicode, true))
            {
                writer.Write(magic);
                writer.Write(status);
                writer.Write(mode == "overlong" ? uint.MaxValue : (uint)command.Length);
                writer.Write((uint)source.Length);
                writer.Write((uint)reason.Length);
                writer.Write((uint)error.Length);
                // Preserve malformed UTF16 in the fixture; Encoding.Unicode would replace the surrogate.
                foreach (string value in new[] { command, source, reason, error })
                    foreach (char character in value) writer.Write((ushort)character);
            }
            byte[] response = memory.ToArray();
            if (mode == "truncated-header") Array.Resize(ref response, 19);
            if (mode == "truncated-payload") Array.Resize(ref response, response.Length - 1);
            if (mode == "extra") Array.Resize(ref response, response.Length + 1);
            if (mode == "oversized") response = new byte[128 * 1024 + 1];
            using (Stream output = Console.OpenStandardOutput()) output.Write(response, 0, response.Length);
        }
        return exit;
    }
}
