using System;
using System.IO;
using NUnit.Framework;

namespace Zantetsu.Core.Tests
{
    public class WindowsGraphicsPreferenceTests
    {
        private string folder;
        private string path;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "ZantetsuLaunchTest-" + Guid.NewGuid().ToString("N"));
            path = Path.Combine(folder, "settings", "graphics-api.txt");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        [Test]
        public void MissingPreferenceUsesAutoWithoutCreatingAFile()
        {
            Assert.That(WindowsGraphicsPreference.Load(path), Is.EqualTo(WindowsGraphicsPreference.Auto));
            Assert.That(Directory.Exists(folder), Is.False);
        }

        [Test]
        public void SaveAndReplacePersistTheNextLaunchChoice()
        {
            WindowsGraphicsPreference.Save(path, WindowsGraphicsPreference.Direct3D11);
            Assert.That(WindowsGraphicsPreference.Load(path), Is.EqualTo("d3d11"));
            WindowsGraphicsPreference.Save(path, WindowsGraphicsPreference.Direct3D12);
            Assert.That(WindowsGraphicsPreference.Load(path), Is.EqualTo("d3d12"));
            WindowsGraphicsPreference.Save(path, WindowsGraphicsPreference.Auto);
            Assert.That(WindowsGraphicsPreference.Load(path), Is.EqualTo("auto"));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)), Has.Length.EqualTo(1));
        }

        [Test]
        public void CorruptionIsReportedAndCanBeReplacedFromSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "not-an-api");
            Assert.Throws<InvalidDataException>(() => WindowsGraphicsPreference.Load(path));
            WindowsGraphicsPreference.Save(path, "d3d11");
            Assert.That(WindowsGraphicsPreference.Load(path), Is.EqualTo("d3d11"));
        }

        [Test]
        public void InvalidSaveDoesNotDestroyThePreviousChoice()
        {
            WindowsGraphicsPreference.Save(path, "d3d11");
            Assert.Throws<ArgumentException>(() => WindowsGraphicsPreference.Save(path, "vulkan"));
            Assert.That(WindowsGraphicsPreference.Load(path), Is.EqualTo("d3d11"));
        }

        [Test]
        public void ReadFailureDoesNotBecomeAuto()
        {
            WindowsGraphicsPreference.Save(path, "d3d11");
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => WindowsGraphicsPreference.Load(path));
        }
    }
}
