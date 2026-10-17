using System.Linq;
using NUnit.Framework;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The readout's library check (unit 8-C, 2026-10-17): the plugin's lines in the process's own maps are counted only
    /// from a text that is a whole maps of a Unity process; anything else is "unknown", never 0. A reading only: no call
    /// into the plugin is made (the method takes the text).
    /// </summary>
    public sealed class GpuMemoryReadoutTests
    {
        private static string[] Maps(int lines, int unityLines, int pluginLines)
        {
            var text = Enumerable.Range(0, lines).Select(i => "7f0000" + i.ToString("x4") + "-7f0001 r--p 00000000 fd:00 1  /system/lib64/libc.so").ToList();
            for (int i = 0; i < unityLines; i++) text[i] = "7f1 r-xp 0 fd:00 2  /data/app/x/lib/arm64/libunity.so";
            for (int i = 0; i < pluginLines; i++) text[unityLines + i] = "7f2 r-xp 0 fd:00 3  /data/app/x/lib/arm64/" + GpuMemoryReadout.PluginLibrary;
            return text.ToArray();
        }

        [Test]
        public void TheCount_IsThePluginsLines_OfAWholeUnityMaps()
        {
            Assert.That(GpuMemoryReadout.DescribeMappedLibraries(Maps(400, 6, 3)), Does.StartWith("pluginMap=3 (").And.Contain("read=ok"));
            Assert.That(GpuMemoryReadout.DescribeMappedLibraries(Maps(400, 6, 0)), Does.StartWith("pluginMap=0 (").And.Contain("unityLines=6"));
        }

        [Test]
        public void ATextThatIsNotAUnityProcesssWholeMaps_IsUnknown_NotZero()
        {
            Assert.That(GpuMemoryReadout.DescribeMappedLibraries(Maps(400, 0, 0)), Does.StartWith("pluginMap=unknown"), "no Unity library: not this process's maps");
            Assert.That(GpuMemoryReadout.DescribeMappedLibraries(Maps(40, 2, 0)), Does.StartWith("pluginMap=unknown"), "too few lines for a whole maps");
            Assert.That(GpuMemoryReadout.DescribeMappedLibraries(new string[0]), Does.StartWith("pluginMap=unknown"));
            Assert.That(GpuMemoryReadout.DescribeMappedLibraries(null), Does.StartWith("pluginMap=unknown"));
        }

        [Test]
        public void TheReadOfThisProcess_NeverThrows()
        {
            // On this Editor's platform the file may not exist: then "unknown (read failed: ...)", not an exception.
            string text = GpuMemoryReadout.DescribeMappedLibraries();
            Assert.That(text, Does.StartWith("pluginMap="));
        }
    }
}
