using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 Run chunk artifact descriptor
    /// factory and the fixed chunk identity boundary: the
    /// <see cref="CaptureArtifactKind.FrameSequence"/> kind, the descriptor
    /// that permits the same normalized relative path for staging and final,
    /// and the stateless <see cref="NvencRunChunkArtifactDescriptorFactory"/>
    /// that fixes kind, format, version, and paths while delegating the
    /// confirmed byte length, content hash, and artifact id to the existing
    /// descriptor validation.
    /// </summary>
    public class NvencRunChunkArtifactDescriptorFactoryContractTests
    {
        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ChunkPath = "chunks/chunk-0.nvenc-idr-chunk-v1.h264";

        [Test]
        public void ArtifactKind_ExistingValuesUnchanged_FrameSequenceAppended()
        {
            Assert.That((int)CaptureArtifactKind.None, Is.EqualTo(0));
            Assert.That((int)CaptureArtifactKind.FrameImage, Is.EqualTo(1));
            Assert.That((int)CaptureArtifactKind.FrameMetadata, Is.EqualTo(2));
            Assert.That((int)CaptureArtifactKind.RunManifest, Is.EqualTo(3));
            Assert.That((int)CaptureArtifactKind.FrameIndex, Is.EqualTo(4));
            Assert.That((int)CaptureArtifactKind.TraceBundle, Is.EqualTo(5));
            Assert.That((int)CaptureArtifactKind.FrameSequence, Is.EqualTo(6));
        }

        [Test]
        public void Descriptor_SameStagingFinalRelativePath_IsValid()
        {
            CaptureArtifactDescriptor descriptor = new CaptureArtifactDescriptor(
                "chunk/0",
                CaptureArtifactKind.FrameSequence,
                "NvencH264IdrChunk",
                1,
                ChunkPath,
                ChunkPath,
                64,
                Hash64);

            Assert.That(descriptor.IsValid, Is.True);
            Assert.That(descriptor.StagingRelativePath, Is.EqualTo(ChunkPath));
            Assert.That(descriptor.FinalRelativePath, Is.EqualTo(ChunkPath));
            Assert.That(descriptor.StagingRelativePath, Is.EqualTo(descriptor.FinalRelativePath));
        }

        [Test]
        public void Descriptor_InvalidPathsStillRejected()
        {
            string[] invalid =
            {
                null,
                "",
                "/absolute",
                "C:/absolute",
                "a\\b",
                "a/../b",
                "../a",
                "a/..",
                "a/./b",
                "./a",
                "a//b",
                "a:b",
            };

            foreach (string path in invalid)
            {
                Assert.That(MakeDescriptorException(path, "frames/7.png"), Is.TypeOf<ArgumentException>(),
                    "staging path must be rejected: " + (path ?? "<null>"));
                Assert.That(MakeDescriptorException("frames/7.png", path), Is.TypeOf<ArgumentException>(),
                    "final path must be rejected: " + (path ?? "<null>"));
            }
        }

        [Test]
        public void Factory_SetsKindFormatVersionPathsIdLengthHashExactly()
        {
            CaptureArtifactDescriptor descriptor =
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64);

            Assert.That(descriptor.IsValid, Is.True);
            Assert.That(descriptor.ArtifactId, Is.EqualTo("chunk/0"));
            Assert.That(descriptor.ArtifactKind, Is.EqualTo(CaptureArtifactKind.FrameSequence));
            Assert.That(descriptor.FormatId, Is.EqualTo("NvencH264IdrChunk"));
            Assert.That(descriptor.FormatVersion, Is.EqualTo(1));
            Assert.That(descriptor.StagingRelativePath, Is.EqualTo(ChunkPath));
            Assert.That(descriptor.FinalRelativePath, Is.EqualTo(ChunkPath));
            Assert.That(descriptor.ByteLength, Is.EqualTo(64));
            Assert.That(descriptor.ContentHash, Is.EqualTo(Hash64));
        }

        [Test]
        public void Factory_PendingPathNeverEntersDescriptor()
        {
            CaptureArtifactDescriptor descriptor =
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64);

            StringAssert.DoesNotContain(".partial", descriptor.StagingRelativePath);
            StringAssert.DoesNotContain(".partial", descriptor.FinalRelativePath);
            StringAssert.EndsWith(".partial", NvencRunChunkArtifactDescriptorFactory.PendingRelativePath);
        }

        [Test]
        public void Factory_RejectsNonPositiveLengthInvalidHashInvalidId()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 0, Hash64));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", -1, Hash64));

            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, "zz"));
            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, null));
            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64.ToUpperInvariant()));

            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkArtifactDescriptorFactory.Create(null, 64, Hash64));
            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkArtifactDescriptorFactory.Create("", 64, Hash64));
            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkArtifactDescriptorFactory.Create(new string('a', 513), 64, Hash64));
        }

        [Test]
        public void Factory_StatelessAndNotDisposable()
        {
            Type type = typeof(NvencRunChunkArtifactDescriptorFactory);

            Assert.That(type.IsAbstract && type.IsSealed, Is.True, "factory must be a static class.");
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] instanceFields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(instanceFields, Is.Empty);
        }

        [Test]
        public void FactorySource_NoFilesystemStreamThreadTaskTimeRandomOrHash()
        {
            string source = File.ReadAllText(
                Path.Combine(RuntimeDirectory(), "NvencRunChunkArtifactDescriptorFactory.cs"));

            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "Stream", "new Thread", "ThreadPool",
                "Task", "DateTime", "DateTimeOffset", "Stopwatch", "Random", "Guid.NewGuid",
                "ComputeHash", "HashAlgorithm", "IncrementalHash", "SHA256", "SHA384",
                "SHA512", "MD5", "System.Security.Cryptography", "UnityEngine", "Application.",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "factory source must not reference: " + word);
            }
        }

        [Test]
        public void PngJsonDescriptor_StillValidWithDifferentPaths()
        {
            CaptureArtifactDescriptor image = new CaptureArtifactDescriptor(
                "frame/7/image",
                CaptureArtifactKind.FrameImage,
                "image/png",
                1,
                "frames/7.png.stage",
                "frames/7.png",
                123,
                Hash64);

            Assert.That(image.IsValid, Is.True);
            Assert.That(image.StagingRelativePath, Is.EqualTo("frames/7.png.stage"));
            Assert.That(image.FinalRelativePath, Is.EqualTo("frames/7.png"));
        }

        private static Exception MakeDescriptorException(string stagingRelativePath, string finalRelativePath)
        {
            try
            {
                new CaptureArtifactDescriptor(
                    "chunk/0",
                    CaptureArtifactKind.FrameSequence,
                    "NvencH264IdrChunk",
                    1,
                    stagingRelativePath,
                    finalRelativePath,
                    64,
                    Hash64);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }
    }
}
