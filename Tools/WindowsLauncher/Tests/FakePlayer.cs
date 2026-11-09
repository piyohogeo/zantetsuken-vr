using System;
using System.IO;
using System.Text;

internal static class FakePlayer
{
    private static int Main(string[] args)
    {
        string output = Environment.GetEnvironmentVariable("ZANTETSU_LAUNCHER_TEST_OUTPUT");
        if (string.IsNullOrEmpty(output)) return 91;
        File.AppendAllText(output + ".launches", "launched\n");
        File.WriteAllText(output + ".commandline", Environment.CommandLine, new UTF8Encoding(false));
        using (var writer = new StreamWriter(output, false, new UTF8Encoding(false)))
        {
            writer.WriteLine(Environment.GetEnvironmentVariable("ZANTETSU_GRAPHICS_SOURCE"));
            writer.WriteLine(Environment.GetEnvironmentVariable("ZANTETSU_GRAPHICS_REASON"));
            foreach (string arg in args) writer.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(arg)));
        }
        return 37;
    }
}
