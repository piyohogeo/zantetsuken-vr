using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngArtifactDestinationTests
    {
        [Test]
        public void CaptureFrameId_ZeroAndNegative_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePngArtifactDestination(0, "C:\\x\\out.png", "C:\\x\\out.json"));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePngArtifactDestination(-1, "C:\\x\\out.png", "C:\\x\\out.json"));
        }

        [Test]
        public void NullPaths_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactDestination(1, null, "C:\\x\\out.json"));
            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactDestination(1, "C:\\x\\out.png", null));
        }

        [Test]
        public void EmptyOrWhitespace_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestination(1, "", "C:\\x\\out.json"));
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestination(1, "   ", "C:\\x\\out.json"));
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestination(1, "C:\\x\\out.png", ""));
        }

        [Test]
        public void RelativePath_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestination(1, "out.png", "C:\\x\\out.json"));
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestination(1, "C:\\x\\out.png", "out.json"));
        }

        [Test]
        public void DriveRelativeAndCurrentDriveRooted_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestination(1, "C:out.png", "C:\\x\\out.json"));
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestination(1, "\\out.png", "C:\\x\\out.json"));
        }

        [Test]
        public void PngExtensionViolation_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestination(1, "C:\\x\\out.txt", "C:\\x\\out.json"));
        }

        [Test]
        public void SidecarExtensionViolation_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestination(1, "C:\\x\\out.png", "C:\\x\\out.txt"));
        }

        [Test]
        public void UppercaseExtensions_Accepted()
        {
            CaptureFramePngArtifactDestination destination = new CaptureFramePngArtifactDestination(1, "C:\\x\\out.PNG", "C:\\x\\out.JSON");

            Assert.That(Path.GetExtension(destination.PngDestinationPath), Is.EqualTo(".PNG").IgnoreCase);
            Assert.That(Path.GetExtension(destination.SidecarDestinationPath), Is.EqualTo(".JSON").IgnoreCase);
        }

        [Test]
        public void DifferentParentDirectory_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestination(1, "C:\\a\\out.png", "C:\\b\\out.json"));
        }

        [Test]
        public void SamePath_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestination(1, "C:\\x\\out.png", "C:\\x\\out.png"));
        }

        [Test]
        public void DotDotNormalized()
        {
            CaptureFramePngArtifactDestination destination = new CaptureFramePngArtifactDestination(1, "C:\\x\\..\\y\\out.png", "C:\\x\\..\\y\\out.json");

            Assert.That(destination.PngDestinationPath, Is.EqualTo("C:\\y\\out.png"));
            Assert.That(destination.SidecarDestinationPath, Is.EqualTo("C:\\y\\out.json"));
            Assert.That(destination.DirectoryPath, Is.EqualTo("C:\\y"));
        }

        [Test]
        public void DirectoryTrailingSeparator_SameResult()
        {
            CaptureFramePngArtifactDestination a = new CaptureFramePngArtifactDestination(1, "C:\\x\\out.png", "C:\\x\\out.json");
            CaptureFramePngArtifactDestination b = new CaptureFramePngArtifactDestination(1, "C:\\x\\\\out.png", "C:\\x\\\\out.json");

            Assert.That(a.PngDestinationPath, Is.EqualTo(b.PngDestinationPath));
            Assert.That(a.SidecarDestinationPath, Is.EqualTo(b.SidecarDestinationPath));
            Assert.That(a.DirectoryPath, Is.EqualTo(b.DirectoryPath));
        }

        [Test]
        public void RootDirectoryNotBroken()
        {
            CaptureFramePngArtifactDestination destination = new CaptureFramePngArtifactDestination(1, "C:\\out.png", "C:\\out.json");

            Assert.That(destination.DirectoryPath, Is.EqualTo("C:\\"));
        }

        [Test]
        public void Constructor_DoesNotCreateFilesystem()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-dest-" + Guid.NewGuid().ToString("N"));

            CaptureFramePngArtifactDestination destination = new CaptureFramePngArtifactDestination(1, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"));

            Assert.That(destination.DirectoryPath, Is.EqualTo(Path.GetFullPath(dir)));
            Assert.That(Directory.Exists(dir), Is.False);
            Assert.That(File.Exists(Path.Combine(dir, "out.png")), Is.False);
            Assert.That(File.Exists(Path.Combine(dir, "out.json")), Is.False);
        }

        [Test]
        public void GetOnlyAndNotIDisposable()
        {
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFramePngArtifactDestination)), Is.False);

            foreach (PropertyInfo property in typeof(CaptureFramePngArtifactDestination).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(property.GetSetMethod(false), Is.Null, typeof(CaptureFramePngArtifactDestination).Name + "." + property.Name + " must not have a public setter.");
            }
        }
    }
}
