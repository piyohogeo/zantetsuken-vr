using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Core;
using Zantetsu.Rendering;

namespace Zantetsu.Core.Tests
{
    /// <summary>The upload meter (unit 8-C): bytes by the source elements, every overload, nothing without the argument, every site in a snapshot; and the session restarts counted at the source.</summary>
    public sealed class VpUploadMeterTests
    {
        [TearDown]
        public void TearDown() => VpUploadMeter.SetEnabledForTest(false);

        [Test]
        public void TheBytes_AreTheSourceElements_NotTheBuffersStride()
        {
            VpUploadMeter.SetEnabledForTest(true);
            // 5 uints (4 bytes each) into a 20-byte-stride argument buffer: 20 bytes transferred, not 100.
            using (var arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 8, 20))
            {
                arguments.SetData(new uint[] { 1, 2, 3, 4, 5 }, "site-a");
                Assert.That(VpUploadMeter.Get("site-a").Bytes, Is.EqualTo(20));
                arguments.SetData(new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 2, 0, 5, "site-a");
                Assert.That(VpUploadMeter.Get("site-a").Bytes, Is.EqualTo(40));
                Assert.That(VpUploadMeter.Get("site-a").MaxBytes, Is.EqualTo(20));
                Assert.That(VpUploadMeter.Get("site-a").Calls, Is.EqualTo(2));
            }
        }

        [Test]
        public void EveryOverload_Counts_TheElementsItSends()
        {
            VpUploadMeter.SetEnabledForTest(true);
            using (var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 64, 16))
            {
                var native = new NativeArray<Vector4>(16, Allocator.Temp);
                try
                {
                    buffer.SetData(new Vector4[8], "array");                         // 8 x 16
                    buffer.SetData(new Vector4[8], 0, 0, 3, "array-range");          // 3 x 16
                    buffer.SetData(native, "native");                                // 16 x 16
                    buffer.SetData(native, 4, 4, 6, "native-range");                 // 6 x 16
                    buffer.SetData(new List<Vector4>(new Vector4[5]), "list");       // 5 x 16
                }
                finally
                {
                    native.Dispose();
                }

                Assert.That(VpUploadMeter.Get("array").Bytes, Is.EqualTo(128));
                Assert.That(VpUploadMeter.Get("array-range").Bytes, Is.EqualTo(48));
                Assert.That(VpUploadMeter.Get("native").Bytes, Is.EqualTo(256));
                Assert.That(VpUploadMeter.Get("native-range").Bytes, Is.EqualTo(96));
                Assert.That(VpUploadMeter.Get("list").Bytes, Is.EqualTo(80));
                Assert.That(VpUploadMeter.TotalBytes, Is.EqualTo(128 + 48 + 256 + 96 + 80));
                Assert.That(VpUploadMeter.TotalCalls, Is.EqualTo(5));
            }
        }

        [Test]
        public void WithoutTheArgument_NothingIsCounted_AndTheUploadStillHappens()
        {
            VpUploadMeter.SetEnabledForTest(false);
            Assert.That(VpUploadMeter.AskedFor(new[] { "-zantetsuScenario", "play" }), Is.False);
            Assert.That(VpUploadMeter.AskedFor(new[] { VpUploadMeter.Argument }), Is.True);
            using (var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4))
            {
                buffer.SetData(new uint[] { 7, 7, 7, 7 }, "quiet");
                var back = new uint[4];
                buffer.GetData(back);
                Assert.That(back, Is.EqualTo(new uint[] { 7, 7, 7, 7 }), "the upload itself is unchanged");
            }

            Assert.That(VpUploadMeter.SiteCount, Is.Zero);
            Assert.That(VpUploadMeter.TotalCalls, Is.Zero);
            VpUploadMeter.Note("quiet", 100);
            Assert.That(VpUploadMeter.SiteCount, Is.Zero, "Note counts nothing while off");
        }

        [Test]
        public void TheSnapshot_ListsEverySiteThatUploadedInTheWindow_AndResetsTheWindowsOnly()
        {
            VpUploadMeter.SetEnabledForTest(true);
            for (int i = 0; i < 20; i++) VpUploadMeter.Note("site-" + i, 1000 + i);
            VpUploadMeter.Note("site-big-once", 50 * 1024 * 1024);
            List<string> lines = VpUploadMeter.Snapshot();
            Assert.That(lines.Count, Is.EqualTo(22), "a header and all 21 sites, none dropped");
            Assert.That(lines[0], Does.StartWith("sites=21"));
            Assert.That(lines[1], Does.StartWith("site-big-once"), "the largest window first");

            VpUploadMeter.Note("site-3", 5);
            lines = VpUploadMeter.Snapshot();
            Assert.That(lines.Count, Is.EqualTo(2), "only the site that uploaded in this window");
            Assert.That(VpUploadMeter.Get("site-big-once").Bytes, Is.EqualTo(50 * 1024 * 1024), "the totals stay");
            Assert.That(VpUploadMeter.Get("site-3").Calls, Is.EqualTo(2));
        }

        [Test]
        public void TheSessionRestarts_AreCountedAtTheSource_FromTheFirstEvent()
        {
            XrSessionStateReport.Reset();
            try
            {
                XrSessionStateReport.Set(0, 1, 0);   // UNKNOWN -> IDLE
                XrSessionStateReport.Set(1, 2, 0);   // IDLE -> READY: the launch
                XrSessionStateReport.Set(2, 5, 2);   // -> FOCUSED
                Assert.That(XrSessionStateReport.ReadyCount, Is.EqualTo(1));
                Assert.That(XrSessionStateReport.Restarts, Is.Zero, "the launch is no restart");
                // Nobody listened so far (the readout subscribes after the scene): the count is the source's.
                XrSessionStateReport.Set(5, 6, 100); XrSessionStateReport.Set(6, 1, 100);   // STOPPING -> IDLE
                XrSessionStateReport.Set(1, 2, 101);                                        // IDLE -> READY: a restart
                XrSessionStateReport.Set(2, 5, 102);
                XrSessionStateReport.Set(5, 6, 103); XrSessionStateReport.Set(6, 1, 103);
                XrSessionStateReport.Set(1, 2, 104);                                        // the second of the wake
                Assert.That(XrSessionStateReport.ReadyCount, Is.EqualTo(3));
                Assert.That(XrSessionStateReport.Restarts, Is.EqualTo(2));
                Assert.That(XrSessionStateReport.Changes, Is.EqualTo(10));
            }
            finally
            {
                XrSessionStateReport.Reset();
            }
        }
    }
}
