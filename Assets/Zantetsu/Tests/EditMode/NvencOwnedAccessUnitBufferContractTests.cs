using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the single fixed Access Unit region (D-147) and its
    /// one-way Free → CollectorOwned → SinkOwned → Free ownership boundary.
    /// Uses a compact fixture: the 16 MiB storage is allocated once per buffer,
    /// no GPU, NVENC, or native resource is touched.
    /// </summary>
    public class NvencOwnedAccessUnitBufferContractTests
    {
        [Test]
        public void Capacity_EqualsProfileMaxAccessUnitLength()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(NvencBringUpProfileV1.MaxAccessUnitByteLength, Is.EqualTo(16L * 1024L * 1024L));
            Assert.That(buffer.Capacity, Is.EqualTo((int)NvencBringUpProfileV1.MaxAccessUnitByteLength));
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void BeginWrite_OnlySucceedsWhenFree_OneOutstandingAccessUnit()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(write.IsValid, Is.True);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));

            Assert.That(buffer.TryBeginWrite(MakeToken(2), out NvencAccessUnitWriteLease second), Is.False);
            Assert.That(second.IsValid, Is.False);
        }

        [Test]
        public void CollectorCopyAndSinkConsume_RoundTripContent()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            byte[] content = new byte[1024];
            content[0] = 0xAB;
            content[1023] = 0xCD;

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryCopyCollectorContent(write, content, 0, 1024), Is.True);
            Assert.That(buffer.TryTransferToSink(write, 1024, out NvencOwnedAccessUnitLease owned), Is.True);

            byte[] destination = new byte[1024];
            Assert.That(buffer.TryConsumeSinkContent(owned, destination, 0, out int consumedLength), Is.True);
            Assert.That(consumedLength, Is.EqualTo(1024));
            Assert.That(destination[0], Is.EqualTo((byte)0xAB));
            Assert.That(destination[1023], Is.EqualTo((byte)0xCD));
        }

        [Test]
        public void CollectorSourceArray_IsCopiedNotAliased()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            byte[] content = new byte[1024];
            content[0] = 0xAB;

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryCopyCollectorContent(write, content, 0, 1024), Is.True);

            // Mutating the collector's source after the copy must not reach the
            // region: the buffer copied, it did not alias.
            content[0] = 0x00;

            Assert.That(buffer.TryTransferToSink(write, 1024, out NvencOwnedAccessUnitLease owned), Is.True);
            byte[] destination = new byte[1024];
            Assert.That(buffer.TryConsumeSinkContent(owned, destination, 0, out int validLength), Is.True);
            Assert.That(validLength, Is.EqualTo(1024));
            Assert.That(destination[0], Is.EqualTo((byte)0xAB));
        }

        [Test]
        public void CollectorCannotMutateRegionAfterTransfer()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            byte[] content = new byte[1024];
            content[0] = 0xAB;
            content[1023] = 0xCD;

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryCopyCollectorContent(write, content, 0, 1024), Is.True);
            Assert.That(buffer.TryTransferToSink(write, 1024, out NvencOwnedAccessUnitLease owned), Is.True);

            // The transferred write lease is stale: it cannot mutate the region
            // the sink now owns.
            byte[] mutation = new byte[1024];
            mutation[0] = 0x00;
            Assert.That(buffer.TryCopyCollectorContent(write, mutation, 0, 1024), Is.False);

            byte[] destination = new byte[1024];
            Assert.That(buffer.TryConsumeSinkContent(owned, destination, 0, out int consumed), Is.True);
            Assert.That(consumed, Is.EqualTo(1024));
            Assert.That(destination[0], Is.EqualTo((byte)0xAB));
            Assert.That(destination[1023], Is.EqualTo((byte)0xCD));
        }

        [Test]
        public void SinkCannotMutateOrReadAfterReturnOrReuse()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            byte[] content = new byte[1024];
            content[0] = 0xAB;

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryCopyCollectorContent(write, content, 0, 1024), Is.True);
            Assert.That(buffer.TryTransferToSink(write, 1024, out NvencOwnedAccessUnitLease owned), Is.True);
            Assert.That(buffer.Return(owned), Is.True);

            // After return the owned lease is stale: it has no path to mutate
            // or read the region it no longer owns.
            byte[] destination = new byte[1024];
            Assert.That(buffer.TryConsumeSinkContent(owned, destination, 0, out _), Is.False);

            // A later generation writes different content; the stale lease
            // still cannot observe or mutate it.
            byte[] content2 = new byte[1024];
            content2[0] = 0x22;
            Assert.That(buffer.TryBeginWrite(MakeToken(2), out NvencAccessUnitWriteLease write2), Is.True);
            Assert.That(buffer.TryCopyCollectorContent(write2, content2, 0, 1024), Is.True);
            Assert.That(buffer.TryTransferToSink(write2, 1024, out NvencOwnedAccessUnitLease owned2), Is.True);
            Assert.That(buffer.TryConsumeSinkContent(owned2, destination, 0, out int consumed2), Is.True);
            Assert.That(destination[0], Is.EqualTo((byte)0x22));

            Assert.That(buffer.TryConsumeSinkContent(owned, destination, 0, out _), Is.False);
        }

        [Test]
        public void ValidLength_BoundaryOneAndMax_Accepted()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write1), Is.True);
            Assert.That(buffer.TryTransferToSink(write1, 1, out NvencOwnedAccessUnitLease owned1), Is.True);
            byte[] dest1 = new byte[1];
            Assert.That(buffer.TryConsumeSinkContent(owned1, dest1, 0, out int minLength), Is.True);
            Assert.That(minLength, Is.EqualTo(1));
            Assert.That(buffer.Return(owned1), Is.True);

            Assert.That(buffer.TryBeginWrite(MakeToken(2), out NvencAccessUnitWriteLease write2), Is.True);
            Assert.That(buffer.TryTransferToSink(write2, buffer.Capacity, out NvencOwnedAccessUnitLease owned2), Is.True);
            byte[] dest2 = new byte[buffer.Capacity];
            Assert.That(buffer.TryConsumeSinkContent(owned2, dest2, 0, out int maxLength), Is.True);
            Assert.That(maxLength, Is.EqualTo(buffer.Capacity));
        }

        [Test]
        public void ValidLength_ZeroNegativeAndOverMax_RejectedWithoutStateChange()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryTransferToSink(write, 0, out _), Is.False);
            Assert.That(buffer.TryTransferToSink(write, -1, out _), Is.False);
            Assert.That(buffer.TryTransferToSink(write, buffer.Capacity + 1, out _), Is.False);

            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));

            // The untouched write lease still transfers.
            Assert.That(buffer.TryTransferToSink(write, 1024, out NvencOwnedAccessUnitLease owned), Is.True);
            Assert.That(owned.IsValid, Is.True);
        }

        [Test]
        public void WorkToken_ForwardedExactlyThroughWriteAndOwnedLeases()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            CaptureFrameWorkToken token = MakeToken(7);

            Assert.That(buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(write.WorkToken.IdenticalTo(token), Is.True);

            Assert.That(buffer.TryTransferToSink(write, 1024, out NvencOwnedAccessUnitLease owned), Is.True);
            Assert.That(owned.WorkToken.IdenticalTo(token), Is.True);
        }

        [Test]
        public void ForeignBufferLease_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer a = new NvencOwnedAccessUnitBuffer(state);
            NvencOwnedAccessUnitBuffer b = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(a.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease writeA), Is.True);

            byte[] content = new byte[1024];
            Assert.That(b.TryCopyCollectorContent(writeA, content, 0, 1024), Is.False);
            Assert.That(b.TryTransferToSink(writeA, 1024, out _), Is.False);
            Assert.That(b.CancelWrite(writeA), Is.False);

            Assert.That(a.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));
        }

        [Test]
        public void ForeignWorkTokenLease_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);

            // Forge a lease with the same buffer identity and generation but a
            // different exact work token; the buffer must reject it.
            NvencAccessUnitWriteLease forged = new NvencAccessUnitWriteLease(
                write.OwnerToken, write.Generation, MakeToken(2));

            Assert.That(buffer.TryTransferToSink(forged, 1024, out _), Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));

            // The genuine lease still transfers.
            Assert.That(buffer.TryTransferToSink(write, 1024, out _), Is.True);
        }

        [Test]
        public void StaleGenerationLease_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.CancelWrite(write), Is.True);

            // The cancelled lease is now stale for every later use.
            Assert.That(buffer.CancelWrite(write), Is.False);
            Assert.That(buffer.TryTransferToSink(write, 1024, out _), Is.False);
            byte[] content = new byte[1024];
            Assert.That(buffer.TryCopyCollectorContent(write, content, 0, 1024), Is.False);
        }

        [Test]
        public void WriteLease_DoubleTransferAndCancel_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryTransferToSink(write, 1024, out NvencOwnedAccessUnitLease owned), Is.True);

            Assert.That(buffer.TryTransferToSink(write, 1024, out _), Is.False);
            Assert.That(buffer.CancelWrite(write), Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));

            Assert.That(buffer.Return(owned), Is.True);
        }

        [Test]
        public void OwnedLease_DoubleReturn_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryTransferToSink(write, 1024, out NvencOwnedAccessUnitLease owned), Is.True);

            Assert.That(buffer.Return(owned), Is.True);
            Assert.That(buffer.Return(owned), Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void ControlledCancel_ReusableWithNextGeneration()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease first), Is.True);
            long firstGeneration = first.Generation;

            Assert.That(buffer.CancelWrite(first), Is.True);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));

            Assert.That(buffer.TryBeginWrite(MakeToken(2), out NvencAccessUnitWriteLease second), Is.True);
            Assert.That(second.Generation, Is.EqualTo(firstGeneration + 1));
        }

        [Test]
        public void SinkReturn_ReusableWithNextGeneration()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease first), Is.True);
            long firstGeneration = first.Generation;
            Assert.That(buffer.TryTransferToSink(first, 1024, out NvencOwnedAccessUnitLease owned), Is.True);

            Assert.That(buffer.Return(owned), Is.True);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));

            Assert.That(buffer.TryBeginWrite(MakeToken(2), out NvencAccessUnitWriteLease second), Is.True);
            Assert.That(second.Generation, Is.EqualTo(firstGeneration + 1));
        }

        [Test]
        public void RunningAndDraining_AllowAcceptedWork()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write1), Is.True);
            Assert.That(buffer.TryTransferToSink(write1, 1024, out NvencOwnedAccessUnitLease owned1), Is.True);

            Assert.That(state.TryBeginDrain(), Is.True);

            Assert.That(buffer.Return(owned1), Is.True);
            Assert.That(buffer.TryBeginWrite(MakeToken(2), out NvencAccessUnitWriteLease write2), Is.True);
            Assert.That(write2.IsValid, Is.True);
        }

        [Test]
        public void Poisoned_BlocksNewAcquire()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(state.TryPoison(), Is.True);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.False);
            Assert.That(write.IsValid, Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void CollectorOwnedThenPoison_HoldsRegionNoReturnNoReuse()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(state.TryPoison(), Is.True);

            Assert.That(buffer.CancelWrite(write), Is.False);
            Assert.That(buffer.TryTransferToSink(write, 1024, out _), Is.False);
            Assert.That(buffer.TryBeginWrite(MakeToken(2), out _), Is.False);

            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));
        }

        [Test]
        public void SinkOwnedThenPoison_HoldsRegionNoReturnNoReuse()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryTransferToSink(write, 1024, out NvencOwnedAccessUnitLease owned), Is.True);
            Assert.That(state.TryPoison(), Is.True);

            Assert.That(buffer.Return(owned), Is.False);
            Assert.That(buffer.TryBeginWrite(MakeToken(2), out _), Is.False);

            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
        }

        [Test]
        public void PoisonInsideResourceResolutionGuard_RejectsLaterReleaseAndReuse()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryTransferToSink(write, 1024, out NvencOwnedAccessUnitLease owned), Is.True);

            // Hold the resource-resolution gate and poison inside it: the poison
            // transition is ordered against the region state transitions.
            Assert.That(state.TryBeginResourceResolution(), Is.True);
            Assert.That(state.TryPoison(), Is.True);
            state.EndResourceResolution();

            Assert.That(buffer.Return(owned), Is.False);
            Assert.That(buffer.TryBeginWrite(MakeToken(2), out _), Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
        }

        [Test]
        public void GenerationOverflow_RetiresWithoutWrap()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            FieldInfo generationField = typeof(NvencOwnedAccessUnitBuffer).GetField(
                "_generation", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(generationField, Is.Not.Null);
            generationField.SetValue(buffer, long.MaxValue);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(write.Generation, Is.EqualTo(long.MaxValue));
            Assert.That(buffer.TryTransferToSink(write, 1024, out NvencOwnedAccessUnitLease owned), Is.True);
            Assert.That(buffer.Return(owned), Is.True);

            // Retired: the single slot must not wrap back to a small generation.
            Assert.That(buffer.TryBeginWrite(MakeToken(2), out _), Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void LeaseShape_ValueType_NoPublicConstructor_NoDispose_ReadonlyFields()
        {
            Type[] leaseTypes =
            {
                typeof(NvencAccessUnitWriteLease),
                typeof(NvencOwnedAccessUnitLease),
            };

            foreach (Type leaseType in leaseTypes)
            {
                Assert.That(leaseType.IsValueType, Is.True, leaseType.Name + " must be a value type.");
                Assert.That(typeof(IDisposable).IsAssignableFrom(leaseType), Is.False, leaseType.Name + " must not be disposable.");
                Assert.That(leaseType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty,
                    leaseType.Name + " must not expose a public constructor.");

                foreach (FieldInfo field in leaseType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(field.IsInitOnly, Is.True, leaseType.Name + "." + field.Name + " must be readonly.");
                }
            }
        }

        [Test]
        public void BufferShape_Sealed_NoPublicConstructor_NoDispose()
        {
            Type bufferType = typeof(NvencOwnedAccessUnitBuffer);

            Assert.That(bufferType.IsSealed, Is.True, "The buffer must be sealed.");
            Assert.That(typeof(IDisposable).IsAssignableFrom(bufferType), Is.False, "The buffer must not be disposable.");
            Assert.That(bufferType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty,
                "The buffer must not expose a public constructor.");
        }

        [Test]
        public void RunPath_NoLargeArrayNoResizeNoPoolNoQueueNoThreadNoIoNoHashNoParser()
        {
            string directory = RuntimeDirectory();
            string bufferSource = File.ReadAllText(Path.Combine(directory, "NvencOwnedAccessUnitBuffer.cs"));
            string leaseSource =
                File.ReadAllText(Path.Combine(directory, "NvencAccessUnitWriteLease.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencOwnedAccessUnitLease.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencAccessUnitPhase.cs"));

            // Exactly one fixed backing allocation, in the constructor.
            Assert.That(CountOccurrences(bufferSource, "new byte["), Is.EqualTo(1),
                "The buffer must allocate its storage exactly once.");

            string[] methodBodies =
            {
                ExtractMethodBody(bufferSource, "TryBeginWrite"),
                ExtractMethodBody(bufferSource, "TryCopyCollectorContent"),
                ExtractMethodBody(bufferSource, "TryTransferToSink"),
                ExtractMethodBody(bufferSource, "TryConsumeSinkContent"),
                ExtractMethodBody(bufferSource, "CancelWrite"),
                ExtractMethodBody(bufferSource, "Return"),
            };

            string[] allocationWords =
            {
                "new byte[", "new long[", "new int[", "new []",
                "new List", "new Dictionary", "new Queue", "new Stack", "new HashSet",
                "Array.", "ArrayPool", ".ToArray(", ".ToList(", "Enumerable.", "Allocate",
            };

            foreach (string body in methodBodies)
            {
                foreach (string word in allocationWords)
                {
                    Assert.That(body, Does.Not.Contain(word), "Run path must not allocate: " + word);
                }
            }

            string allSource = bufferSource + leaseSource;

            string[] forbidden =
            {
                "lock (", "Monitor", "ManualResetEvent", "AutoResetEvent", "WaitHandle", "SpinWait",
                "new Thread", "ThreadPool", "Task", "File.", "Directory.", "FileStream", "DllImport",
                "UnityEngine", "Application.", "SystemInfo", "GraphicsDevice", "IntPtr", "SafeHandle",
                "NvEnc", "RenderTexture", "SHA", "MD5", "Hash", "H.264", "H264", "ArrayPool",
            };

            foreach (string word in forbidden)
            {
                Assert.That(allSource, Does.Not.Contain(word), "Production source must not reference: " + word);
            }
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), "Method signature not found: " + methodName);

            int open = source.IndexOf('{', signature);
            Assert.That(open, Is.GreaterThanOrEqualTo(0), "Method body not found: " + methodName);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail("Method body not terminated: " + methodName);
            return null;
        }
    }
}
