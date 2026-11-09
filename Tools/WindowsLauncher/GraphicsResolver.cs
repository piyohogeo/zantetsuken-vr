using System;
using System.IO;
using System.Text;
using Zantetsu.Core;

namespace Zantetsu.WindowsLauncher
{
    /// <summary>Resolves graphics before the native Player loads Unity. Stdout is the ZGP1 binary response only.</summary>
    internal static class GraphicsResolver
    {
        internal const uint Magic = 0x3150475a;
        internal const int MaximumCommandChars = 32766;
        internal const int MaximumReasonChars = 2048;
        internal const int MaximumErrorChars = 4096;

        private static int Main(string[] arguments)
        {
            using (Stream output = Console.OpenStandardOutput())
                return Run(arguments, output, WindowsGraphicsPreference.Load, () => MachineDetection.ReadBounded(1500));
        }

        internal static int Run(string[] arguments, Stream output, Func<string> load, Func<Detection> detect)
        {
            int status = 0;
            string command = string.Empty, source = string.Empty, reason = string.Empty, error = string.Empty;
            try
            {
                LaunchRequest request = LaunchRequest.Parse(arguments);
                if (request.ShowSettings)
                    throw new ArgumentException("Open 起動設定.exe or Zantetsu.exe --settings to change launch settings.");
                string preference = request.ExplicitApi == null ? load() : WindowsGraphicsPreference.Auto;
                Detection detection = request.ExplicitApi == null && preference == WindowsGraphicsPreference.Auto
                    ? detect() : new Detection(false, "hardware probe not needed");
                LaunchDecision decision = LaunchDecision.Select(request, preference, detection);
                command = CommandLine.Join(decision.Arguments);
                source = decision.Source;
                reason = decision.Reason;
                if (command.Length > MaximumCommandChars || source.Length > 32 || reason.Length > MaximumReasonChars
                    || command.IndexOf('\0') >= 0 || source.IndexOf('\0') >= 0 || reason.IndexOf('\0') >= 0)
                    throw new ArgumentException("The resolved graphics command or explanation exceeds the Player bootstrap limits.");
                Launcher.WriteLog("resolve api=" + decision.Api + "; source=" + source + "; reason=" + reason);
            }
            catch (Exception exception)
            {
                status = 22;
                command = string.Empty;
                source = string.Empty;
                reason = string.Empty;
                error = exception.Message.Replace('\0', ' ');
                if (error.Length > MaximumErrorChars) error = error.Substring(0, MaximumErrorChars);
                if (error.Length == 0) error = "Graphics selection failed.";
                Launcher.WriteLog("resolve refused: " + error);
            }

            // Prepare the full bounded response before touching the pipe. The native parent drains it while we write.
            byte[] response;
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.Unicode, true))
                {
                    writer.Write(Magic);
                    writer.Write((uint)status);
                    writer.Write((uint)command.Length);
                    writer.Write((uint)source.Length);
                    writer.Write((uint)reason.Length);
                    writer.Write((uint)error.Length);
                    foreach (string field in new[] { command, source, reason, error })
                    {
                        foreach (char character in field) writer.Write((ushort)character);
                    }
                }
                response = bytes.ToArray();
            }
            try
            {
                output.Write(response, 0, response.Length);
                output.Flush();
                return status;
            }
            catch (IOException) { return 22; }
        }
    }
}