using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngArtifactDestinationFactoryTests
    {
        private static CaptureFrameRequest MakeRequest(long testRunId = 1, long captureFrameId = 42)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, captureFrameId, 30, testRunId, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(
                context,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-factory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTempDir(string dir)
        {
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void NullDirectory_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactDestinationFactory(null));
        }

        [Test]
        public void EmptyOrWhitespaceDirectory_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestinationFactory(""));
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestinationFactory("   "));
        }

        [Test]
        public void RelativeDirectory_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestinationFactory("out"));
        }

        [Test]
        public void DriveRelativeAndCurrentDriveRooted_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestinationFactory("C:out"));
            Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifactDestinationFactory("\\out"));
        }

        [Test]
        public void FullyQualifiedDirectory_Accepted()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");
            Assert.That(factory.DirectoryPath, Is.EqualTo("C:\\captures"));
        }

        [Test]
        public void DotDotDirectoryNormalized()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\x\\..\\y");
            Assert.That(factory.DirectoryPath, Is.EqualTo("C:\\y"));
        }

        [Test]
        public void TrailingSeparator_SameDirectoryPath()
        {
            CaptureFramePngArtifactDestinationFactory a = new CaptureFramePngArtifactDestinationFactory("C:\\captures");
            CaptureFramePngArtifactDestinationFactory b = new CaptureFramePngArtifactDestinationFactory("C:\\captures\\");
            Assert.That(a.DirectoryPath, Is.EqualTo(b.DirectoryPath));
        }

        [Test]
        public void DriveRootNotBroken()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\");
            Assert.That(factory.DirectoryPath, Is.EqualTo("C:\\"));
        }

        [Test]
        public void Constructor_DoesNotCreateDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-factory-" + Guid.NewGuid().ToString("N"));
            try
            {
                CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory(dir);

                Assert.That(factory.DirectoryPath, Is.EqualTo(Path.GetFullPath(dir)));
                Assert.That(Directory.Exists(dir), Is.False);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DefaultOrInvalidRequest_Rejected()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");
            Assert.Throws<ArgumentException>(() => factory.Create(default));
        }

        [Test]
        public void TestRunId_ZeroAndNegative_Rejected()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");
            Assert.Throws<ArgumentOutOfRangeException>(() => factory.Create(MakeRequest(testRunId: 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => factory.Create(MakeRequest(testRunId: -1)));
        }

        [Test]
        public void CaptureFrameId_ZeroAndNegative_Rejected()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");
            Assert.Throws<ArgumentOutOfRangeException>(() => factory.Create(MakeRequest(captureFrameId: 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => factory.Create(MakeRequest(captureFrameId: -1)));
        }

        [Test]
        public void ExactBasename()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");

            CaptureFramePngArtifactDestination destination = factory.Create(MakeRequest(1, 42));

            Assert.That(Path.GetFileName(destination.PngDestinationPath), Is.EqualTo("capture-00000000000000000001-00000000000000000042.png"));
            Assert.That(Path.GetFileName(destination.SidecarDestinationPath), Is.EqualTo("capture-00000000000000000001-00000000000000000042.json"));
        }

        [Test]
        public void PngAndSidecar_SameDirectory()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");

            CaptureFramePngArtifactDestination destination = factory.Create(MakeRequest(1, 42));

            Assert.That(Path.GetDirectoryName(destination.PngDestinationPath), Is.EqualTo("C:\\captures"));
            Assert.That(Path.GetDirectoryName(destination.SidecarDestinationPath), Is.EqualTo("C:\\captures"));
        }

        [Test]
        public void PngIsDotPng_SidecarIsDotJson()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");

            CaptureFramePngArtifactDestination destination = factory.Create(MakeRequest(1, 42));

            Assert.That(Path.GetExtension(destination.PngDestinationPath), Is.EqualTo(".png"));
            Assert.That(Path.GetExtension(destination.SidecarDestinationPath), Is.EqualTo(".json"));
        }

        [Test]
        public void DestinationCaptureFrameId_MatchesRequest()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");

            CaptureFramePngArtifactDestination destination = factory.Create(MakeRequest(1, 42));

            Assert.That(destination.CaptureFrameId, Is.EqualTo(42));
        }

        [Test]
        public void SameRequest_ByteForByteSamePaths()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");

            CaptureFramePngArtifactDestination a = factory.Create(MakeRequest(7, 99));
            CaptureFramePngArtifactDestination b = factory.Create(MakeRequest(7, 99));

            Assert.That(a.PngDestinationPath, Is.EqualTo(b.PngDestinationPath));
            Assert.That(a.SidecarDestinationPath, Is.EqualTo(b.SidecarDestinationPath));
        }

        [Test]
        public void DifferentCaptureFrameId_DifferentPaths()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");

            CaptureFramePngArtifactDestination a = factory.Create(MakeRequest(1, 1));
            CaptureFramePngArtifactDestination b = factory.Create(MakeRequest(1, 2));

            Assert.That(a.PngDestinationPath, Is.Not.EqualTo(b.PngDestinationPath));
        }

        [Test]
        public void DifferentTestRunId_DifferentPaths()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");

            CaptureFramePngArtifactDestination a = factory.Create(MakeRequest(1, 1));
            CaptureFramePngArtifactDestination b = factory.Create(MakeRequest(2, 1));

            Assert.That(a.PngDestinationPath, Is.Not.EqualTo(b.PngDestinationPath));
        }

        [Test]
        public void Ids_Fixed20Digits()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");

            Assert.That(Path.GetFileName(factory.Create(MakeRequest(1, 1)).PngDestinationPath), Is.EqualTo("capture-00000000000000000001-00000000000000000001.png"));
            Assert.That(Path.GetFileName(factory.Create(MakeRequest(1, 2)).PngDestinationPath), Is.EqualTo("capture-00000000000000000001-00000000000000000002.png"));
            Assert.That(Path.GetFileName(factory.Create(MakeRequest(1, 10)).PngDestinationPath), Is.EqualTo("capture-00000000000000000001-00000000000000000010.png"));
            Assert.That(Path.GetFileName(factory.Create(MakeRequest(1, long.MaxValue)).PngDestinationPath), Is.EqualTo("capture-00000000000000000001-09223372036854775807.png"));
        }

        [Test]
        public void IdAscending_PathLexicographicOrderMatches()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");

            string f1 = Path.GetFileName(factory.Create(MakeRequest(1, 1)).PngDestinationPath);
            string f2 = Path.GetFileName(factory.Create(MakeRequest(1, 2)).PngDestinationPath);
            string f10 = Path.GetFileName(factory.Create(MakeRequest(1, 10)).PngDestinationPath);

            Assert.That(string.CompareOrdinal(f1, f2), Is.LessThan(0));
            Assert.That(string.CompareOrdinal(f2, f10), Is.LessThan(0));
        }

        [Test]
        public void NonEnglishCulture_ResultUnchanged()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");
            CaptureFramePngArtifactDestination expected = factory.Create(MakeRequest(1, 42));
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

                CaptureFramePngArtifactDestination actual = factory.Create(MakeRequest(1, 42));

                Assert.That(actual.PngDestinationPath, Is.EqualTo(expected.PngDestinationPath));
                Assert.That(actual.SidecarDestinationPath, Is.EqualTo(expected.SidecarDestinationPath));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Test]
        public void CultureRestoredAfterTest()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
                Assert.That(CultureInfo.CurrentCulture.Name, Is.EqualTo("fr-FR"));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }

            Assert.That(CultureInfo.CurrentCulture, Is.EqualTo(original));
        }

        [Test]
        public void ExistingFile_FactoryReturnsSamePath_DoesNotModify()
        {
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory(dir);
                string pngPath = Path.Combine(dir, "capture-00000000000000000001-00000000000000000042.png");
                File.WriteAllBytes(pngPath, new byte[] { 1, 2, 3 });

                CaptureFramePngArtifactDestination destination = factory.Create(MakeRequest(1, 42));

                Assert.That(destination.PngDestinationPath, Is.EqualTo(Path.GetFullPath(pngPath)));
                Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(new byte[] { 1, 2, 3 }));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void RequestFieldsUnchangedAfterCreate()
        {
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");

            CaptureFrameRequest request = MakeRequest(1, 42);
            long testRunIdBefore = request.TraceContext.TestRunId;
            long captureFrameIdBefore = request.TraceContext.CaptureFrameId;
            CaptureSource sourceBefore = request.Source;
            CaptureEye eyeBefore = request.Eye;
            int arrayIndexBefore = request.ArrayIndex;

            factory.Create(request);

            Assert.That(request.TraceContext.TestRunId, Is.EqualTo(testRunIdBefore));
            Assert.That(request.TraceContext.CaptureFrameId, Is.EqualTo(captureFrameIdBefore));
            Assert.That(request.Source, Is.EqualTo(sourceBefore));
            Assert.That(request.Eye, Is.EqualTo(eyeBefore));
            Assert.That(request.ArrayIndex, Is.EqualTo(arrayIndexBefore));
        }

        [Test]
        public void NotIDisposable()
        {
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFramePngArtifactDestinationFactory)), Is.False);

            Assert.That(typeof(CaptureFramePngArtifactDestinationFactory).GetField(nameof(CaptureFramePngArtifactDestinationFactory.FileNamePrefix), BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
        }
    }
}
