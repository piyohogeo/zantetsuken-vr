using NUnit.Framework;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The stop stages (unit 8-C): each stage keeps the earlier ones stopped; frame uploads during the registration are
    /// initial data; the display's frame update is left at stage 2 once the registration is over; stage 4 makes no
    /// buffer; the held renderers stay listed; every decision is tallied; the argument's reading.
    /// </summary>
    public sealed class VpGpuStopTests
    {
        [SetUp]
        public void SetUp() => VpGpuStop.Reset();

        [TearDown]
        public void TearDown() => VpGpuStop.Reset();

        [Test]
        public void Stage0_StopsNothing_AndTallies()
        {
            Assert.That(VpGpuStop.Draw(), Is.True);
            Assert.That(VpGpuStop.Dispatch(), Is.True);
            Assert.That(VpGpuStop.CommandWrite(), Is.True);
            Assert.That(VpGpuStop.DisplayUpdate(), Is.True);
            VpGpuStop.NoteRegistrationDone();
            Assert.That(VpGpuStop.DisplayUpdate(), Is.True);
            Assert.That(VpGpuStop.Upload(VpUploadKind.Frame, 100), Is.True);
            Assert.That(VpGpuStop.Upload(VpUploadKind.Initial, 200), Is.True);
            using (GraphicsBuffer made = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, 1024, 16))
            {
                Assert.That(made, Is.Not.Null);
                Assert.That(made.count, Is.EqualTo(1024));
            }

            Assert.That(VpGpuStop.DrawsIssued, Is.EqualTo(1));
            Assert.That(VpGpuStop.DisplayUpdatesEntered, Is.EqualTo(2));
            Assert.That(VpGpuStop.FrameUploadBytesIssued, Is.EqualTo(100));
            Assert.That(VpGpuStop.InitialUploadBytesIssued, Is.EqualTo(200));
            Assert.That(VpGpuStop.AllocationBytesMade, Is.EqualTo(1024 * 16));
            Assert.That(VpGpuStop.AllocationsWithheld, Is.Zero);
            Assert.That(VpGpuStop.Describe(), Does.StartWith("gpuStop=0 ").And.Contain("draws=1/skipped 0"));
        }

        [Test]
        public void EachStage_KeepsTheEarlierStopped()
        {
            VpGpuStop.NoteRegistrationDone();
            // 1: draws only
            VpGpuStop.Set(1);
            Assert.That(VpGpuStop.Draw(), Is.False);
            Assert.That(VpGpuStop.DisplayUpdate(), Is.True);
            Assert.That(VpGpuStop.Dispatch(), Is.True);
            Assert.That(VpGpuStop.Upload(VpUploadKind.Frame, 1), Is.True);
            Assert.That(VpGpuStop.Upload(VpUploadKind.Initial, 1), Is.True);
            Assert.That(VpGpuStop.WithholdAllocations, Is.False);
            // 2: plus frame work (the display's update, dispatch, command writes, frame uploads after the registration)
            VpGpuStop.Set(2);
            Assert.That(VpGpuStop.Draw(), Is.False);
            Assert.That(VpGpuStop.DisplayUpdate(), Is.False);
            Assert.That(VpGpuStop.Dispatch(), Is.False);
            Assert.That(VpGpuStop.CommandWrite(), Is.False);
            Assert.That(VpGpuStop.Upload(VpUploadKind.Frame, 1), Is.False);
            Assert.That(VpGpuStop.Upload(VpUploadKind.Initial, 1), Is.True, "initial data is kept to stage 2");
            Assert.That(VpGpuStop.WithholdAllocations, Is.False);
            // 3: plus initial uploads
            VpGpuStop.Set(3);
            Assert.That(VpGpuStop.Upload(VpUploadKind.Initial, 1), Is.False);
            Assert.That(VpGpuStop.Upload(VpUploadKind.Frame, 1), Is.False);
            Assert.That(VpGpuStop.WithholdAllocations, Is.False, "the buffers are still made at stage 3");
            // 4: plus allocations
            VpGpuStop.Set(4);
            Assert.That(VpGpuStop.WithholdAllocations, Is.True);
            Assert.That(VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, 64, 4), Is.Null);
            Assert.That(VpGpuStop.AllocationsWithheld, Is.EqualTo(1));
            Assert.That(VpGpuStop.AllocationBytesWithheld, Is.EqualTo(256));
            Assert.That(VpGpuStop.AllocationsMade, Is.Zero);
            Assert.That(VpGpuStop.DrawsSkipped, Is.EqualTo(2));
            Assert.That(VpGpuStop.DisplayUpdatesSkipped, Is.EqualTo(1));
            Assert.That(VpGpuStop.DispatchesSkipped, Is.EqualTo(1));
            Assert.That(VpGpuStop.InitialUploadsSkipped, Is.EqualTo(1));
            Assert.That(VpGpuStop.FrameUploadsSkipped, Is.EqualTo(2));
        }

        [Test]
        public void TheDisplaysUpdate_IsEnteredDuringTheRegistration_AndLeftAfterIt_AtStage2()
        {
            VpGpuStop.Set(2);
            Assert.That(VpGpuStop.DisplayUpdate(), Is.True, "the registration's collections upload the bodies shown");
            Assert.That(VpGpuStop.Upload(VpUploadKind.Frame, 10), Is.True, "a body's first commands while bodies are still shown");
            Assert.That(VpGpuStop.InitialUploadsIssued, Is.EqualTo(1));
            VpGpuStop.NoteRegistrationDone();
            Assert.That(VpGpuStop.DisplayUpdate(), Is.False);
            Assert.That(VpGpuStop.Upload(VpUploadKind.Frame, 10), Is.False);
            Assert.That(VpGpuStop.DisplayUpdatesSkipped, Is.EqualTo(1));
            Assert.That(VpGpuStop.FrameUploadsSkipped, Is.EqualTo(1));
            VpGpuStop.Reset();
            VpGpuStop.Set(3);
            Assert.That(VpGpuStop.DisplayUpdate(), Is.True, "stage 3 still enters the update before the registration is over");
            Assert.That(VpGpuStop.Upload(VpUploadKind.Frame, 10), Is.False, "but skips the registration's data");
            Assert.That(VpGpuStop.InitialUploadsSkipped, Is.EqualTo(1));
        }

        [Test]
        public void HeldRenderers_StayListed_AndAreGivenBackOnReset()
        {
            var one = new GameObject("held one").AddComponent<MeshRenderer>();
            var other = new GameObject("held other").AddComponent<MeshRenderer>();
            try
            {
                Assert.That(VpGpuStop.SuppressRenderers(new Renderer[] { one, other }), Is.Zero, "stage 0 holds nothing");
                Assert.That(one.forceRenderingOff, Is.False);
                VpGpuStop.Set(1);
                Assert.That(VpGpuStop.SuppressRenderers(new Renderer[] { one, other, null }), Is.EqualTo(2));
                Assert.That(one.forceRenderingOff && other.forceRenderingOff, Is.True);
                Assert.That(VpGpuStop.IsSuppressedByStop(one), Is.True, "the inventory keeps it as a target");
                Assert.That(VpGpuStop.SuppressRenderers(new Renderer[] { one }), Is.Zero, "held once");
                Assert.That(VpGpuStop.SuppressedRendererCount, Is.EqualTo(2));
                var already = new GameObject("off by the scene").AddComponent<MeshRenderer>();
                try
                {
                    already.forceRenderingOff = true;
                    Assert.That(VpGpuStop.SuppressRenderers(new Renderer[] { already }), Is.Zero, "a renderer the scene holds off is not the stop's");
                    Assert.That(VpGpuStop.IsSuppressedByStop(already), Is.False);
                }
                finally
                {
                    Object.DestroyImmediate(already.gameObject);
                }

                VpGpuStop.Reset();
                Assert.That(one.forceRenderingOff || other.forceRenderingOff, Is.False, "given back");
                Assert.That(VpGpuStop.SuppressedRendererCount, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(one.gameObject);
                Object.DestroyImmediate(other.gameObject);
            }
        }

        [Test]
        public void TheArgument_IsReadAsAStage_OrNothing()
        {
            Assert.That(VpGpuStop.Read(new[] { "x", VpGpuStop.Argument, "3" }), Is.EqualTo(3));
            Assert.That(VpGpuStop.Read(new[] { VpGpuStop.Argument, "7" }), Is.Zero, "out of range");
            Assert.That(VpGpuStop.Read(new[] { VpGpuStop.Argument }), Is.Zero, "no value");
            Assert.That(VpGpuStop.Read(new[] { VpGpuStop.Argument, "-1" }), Is.Zero);
            Assert.That(VpGpuStop.Read(new[] { "-zantetsuScenario", "play" }), Is.Zero);
            Assert.That(VpGpuStop.Read(null), Is.Zero);
            VpGpuStop.Set(9);
            Assert.That(VpGpuStop.Stage, Is.EqualTo(4), "clamped");
        }

        [Test]
        public void TheMeterExtension_SkipsTheUpload_AtTheStage_AndCountsOnlyWhatWasMade()
        {
            VpUploadMeter.SetEnabledForTest(true);
            try
            {
                using (var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4))
                {
                    buffer.SetData(new uint[] { 1, 1, 1, 1 }, "a", VpUploadKind.Initial);
                    VpGpuStop.Set(3);
                    buffer.SetData(new uint[] { 2, 2, 2, 2 }, "a", VpUploadKind.Initial);
                    var back = new uint[4];
                    buffer.GetData(back);
                    Assert.That(back, Is.EqualTo(new uint[] { 1, 1, 1, 1 }), "the second upload was not made");
                    Assert.That(VpUploadMeter.Get("a").Calls, Is.EqualTo(1), "only the upload made is counted");
                    Assert.That(VpGpuStop.InitialUploadsSkipped, Is.EqualTo(1));
                    Assert.That(VpGpuStop.InitialUploadBytesSkipped, Is.EqualTo(16));
                }

                // A withheld buffer (stage 4) takes the extension without being touched.
                VpGpuStop.Set(4);
                GraphicsBuffer none = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, 4, 4);
                Assert.That(none, Is.Null);
                none.SetData(new uint[] { 3 }, "b", VpUploadKind.Initial);
                Assert.That(VpGpuStop.InitialUploadsSkipped, Is.EqualTo(2));
            }
            finally
            {
                VpUploadMeter.SetEnabledForTest(false);
            }
        }
    }
}
