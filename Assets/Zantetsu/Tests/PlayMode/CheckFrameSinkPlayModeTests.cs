using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The check's frame sink (Sandbox/CheckFrameSink, docs/diagnostics/check-capture-2026-10-02.md): a known picture saved
    /// and opened again (upright, red as red, mid grey kept), each row's real time the render's own; a slow writer and
    /// full bounds skip, counted by reason, the Main thread's calls staying short and the skipped time kept in the rows;
    /// finish, cancel while writing, a writer's error and a deadline passed each end with everything released and the
    /// thread ended, never a wait on the Main thread. Without a sink nothing is made.
    /// </summary>
    public sealed class CheckFrameSinkPlayModeTests
    {
        /// <summary>A writer of the test's own: slow, failing, or ignoring the cancel, as asked.</summary>
        private sealed class TestWriter : ICheckFrameWriter
        {
            public int delayMs, failOnCall = -1;
            public bool ignoreCancel;
            public int calls, started;
            public readonly ICheckFrameWriter inner = new CheckJpegFrameWriter();

            public void Write(NativeArray<byte> rgba, int width, int height, string path, CancellationToken cancel)
            {
                int call = Interlocked.Increment(ref calls);
                Interlocked.Increment(ref started);
                if (call == failOnCall) throw new IOException("the test's writer failed on call " + call);
                if (delayMs > 0)
                {
                    if (ignoreCancel) Thread.Sleep(delayMs);
                    else if (cancel.WaitHandle.WaitOne(delayMs)) throw new OperationCanceledException(cancel);
                }

                inner.Write(rgba, width, height, path, cancel);
            }
        }

        /// <summary>Whatever a failed test left: its sinks cancelled and let settle, so the next test starts from none.</summary>
        [UnityTearDown]
        public IEnumerator CancelLeftSinks()
        {
            foreach (CheckFrameSink s in new List<CheckFrameSink>(CheckFrameSink.Live)) s.Cancel("the test's teardown");
            float until = Time.realtimeSinceStartup + 5f;
            while (CheckFrameSink.Live.Count > 0 && Time.realtimeSinceStartup < until) yield return null;
            GameObject left = GameObject.Find("Frame Sink Off Camera");
            if (left != null) UnityEngine.Object.Destroy(left);
            yield return null;
        }

        private static string Fresh(string name)
        {
            string dir = Path.Combine(Application.temporaryCachePath, "frame-sink-" + name);
            if (Directory.Exists(dir)) foreach (string f in Directory.GetFiles(dir)) File.Delete(f);
            return dir;
        }

        private static RenderTexture Texture(int w, int h)
        {
            var t = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "Frame Sink Test Texture" };
            t.Create();
            return t;
        }

        private static IEnumerator UntilSettled(CheckFrameSink sink, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!sink.Settled && Time.realtimeSinceStartup < until) yield return null;
        }

        /// <summary>Two offers in one frame: the second sees the first's readback in flight (bound 1) and is skipped.</summary>
        private static IEnumerator OfferFrames(CheckFrameSink sink, RenderTexture t, int frames, int perFrame, List<double> reals)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return null;
                for (int k = 0; k < perFrame; k++)
                {
                    double real = Time.realtimeSinceStartupAsDouble;
                    reals?.Add(real);
                    sink.Offer(t, Time.frameCount, real, "t");
                }
            }
        }

        /// <summary>
        /// **A known picture -- mid grey above, pure red below -- saved through the capture camera and opened again: upright,
        /// red as red, the grey kept as rendered; each row's real time the render's own, the frames paced about 10 ms apart
        /// and the writer slower (25 ms), so some are skipped and their time stays in the rows; finished and released, its
        /// thread ended.** The pictures and frames.csv are left in the cache folder, to be opened outside Unity as well.
        /// </summary>
        [UnityTest]
        public IEnumerator FrameSink_KnownPicture_OpensUpright_RedAsRed_TimesTheRendersOwn()
        {
            const int w = 160, h = 90;
            string dir = Fresh("known");
            int workersBefore = CheckFrameSink.LiveWorkers, sinksBefore = CheckFrameSink.LiveSinks;
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Frame Sink Test Grey";
            quad.transform.SetPositionAndRotation(new Vector3(0f, 1.5f, 0f), Quaternion.identity);
            quad.transform.localScale = new Vector3(20f, 3f, 1f);   // y 0..3: the upper half of the view
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            material.SetColor("_BaseColor", new Color(0.5f, 0.5f, 0.5f, 1f));
            quad.GetComponent<Renderer>().sharedMaterial = material;

            var sink = new CheckFrameSink("known", dir, w, h, 90, new TestWriter { delayMs = 25 });
            var capture = new CheckCaptureCamera(null, null, sink, 0f, 0f, 0f, 60f, w, h,
                new[] { new CheckCaptureCamera.Shot { name = "known", position = new Vector3(0f, 0f, -2f), lookAt = Vector3.zero, fieldOfView = 60f } });
            capture.Camera.clearFlags = CameraClearFlags.SolidColor;
            capture.Camera.backgroundColor = Color.red;
            // The render's own frame and real time, read beside the camera's.
            var renders = new List<(int frame, double real)>();
            Camera cam = capture.Camera;
            Action<ScriptableRenderContext, Camera> seen = (c, camera) => { if (ReferenceEquals(camera, cam)) renders.Add((Time.frameCount, Time.realtimeSinceStartupAsDouble)); };
            RenderPipelineManager.endCameraRendering += seen;
            sink.StartSaving();
            for (int i = 0; i < 120; i++)
            {
                yield return null;
                Thread.Sleep(10);   // the test's own frame time (not the sink's): about 10 ms a frame
            }

            RenderPipelineManager.endCameraRendering -= seen;
            RenderTexture target = capture.Target;
            capture.Dispose();
            sink.BeginFinish("the test's end");
            yield return UntilSettled(sink, 10f);
            TestContext.Out.WriteLine(sink.Describe() + " | the pictures in " + dir);
            Assert.That(sink.Now, Is.EqualTo(CheckFrameSink.State.Finished));
            Assert.That(sink.ledger.Requested, Is.EqualTo(90), "asked up to its limit");
            Assert.That(sink.ledger.AllAccounted && sink.ledger.Errors == 0 && sink.ledger.Cancelled == 0, Is.True);
            Assert.That(sink.ledger.Written, Is.GreaterThan(10).And.LessThan(90), "some written, some skipped by the slower writer");
            Assert.That(sink.Released && !sink.DeadlineExceeded, Is.True);
            Assert.That(CheckFrameSink.LiveWorkers, Is.EqualTo(workersBefore), "its thread ended");
            Assert.That(CheckFrameSink.LiveSinks, Is.EqualTo(sinksBefore), "no sink left live");
            Assert.That(CheckFrameSinkDriver.Exists, Is.EqualTo(sinksBefore > 0), "the driver object gone with the last sink");
            yield return null;
            Assert.That(target == null, Is.True, "the capture texture released");

            // The rows: each real time is the render's own (the same frame, the same real time), and they follow in order.
            string[] rows = File.ReadAllLines(Path.Combine(dir, "frames.csv"));
            Assert.That(rows[0], Is.EqualTo("frame,real,shot,outcome,readbackMs,writeMs"));
            double last = double.MinValue;
            for (int i = 1; i < rows.Length; i++)
            {
                string[] f = rows[i].Split(',');
                int frame = int.Parse(f[0], CultureInfo.InvariantCulture);
                double real = double.Parse(f[1], CultureInfo.InvariantCulture);
                Assert.That(renders.Any(r => r.frame == frame && Math.Abs(r.real - real) < 0.001), Is.True, "row " + rows[i] + " is a render's own frame and time (read at its end, within 1 ms)");
                Assert.That(real, Is.GreaterThan(last), "in order");
                last = real;
            }

            // The pictures, opened again: LoadImage puts the picture's top row last (Unity's rows run bottom-up).
            string[] files = Directory.GetFiles(dir, "*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.That(files.Length, Is.EqualTo(sink.ledger.Written));
            var picture = new Texture2D(2, 2);
            Assert.That(picture.LoadImage(File.ReadAllBytes(files[files.Length - 1])), Is.True);
            Assert.That(picture.width == w && picture.height == h, Is.True, "its size");
            Color32 top = picture.GetPixel(w / 2, h * 3 / 4), bottom = picture.GetPixel(w / 2, h / 4);
            TestContext.Out.WriteLine("opened " + files[files.Length - 1] + ": top " + top + ", bottom " + bottom);
            UnityEngine.Object.Destroy(picture);
            Assert.That(bottom.r > 230 && bottom.g < 30 && bottom.b < 30, Is.True, "red below, as red (not blue): " + bottom);
            Assert.That(Math.Abs(top.r - 128) <= 8 && Math.Abs(top.g - 128) <= 8 && Math.Abs(top.b - 128) <= 8, Is.True, "mid grey above, kept as rendered (sRGB, not linear): " + top);
            UnityEngine.Object.Destroy(quad);
            UnityEngine.Object.Destroy(material);
        }

        /// <summary>
        /// **A slow writer and full bounds: saves skipped and counted by reason (readbacks in flight, the write side), the
        /// Main thread's calls short, every row kept with its time; the skipped time stays between the written rows.**
        /// </summary>
        [UnityTest]
        public IEnumerator FrameSink_SlowWriterAndFullBounds_SkipWithoutWaiting_TimesKept()
        {
            string dir = Fresh("slow");
            RenderTexture t = Texture(64, 36);
            var writer = new TestWriter { delayMs = 150 };
            var sink = new CheckFrameSink("slow", dir, 64, 36, 400, writer, maxReadbacksInFlight: 1, maxWritesWaiting: 1);
            sink.StartSaving();
            var reals = new List<double>();
            float begun = Time.realtimeSinceStartup;
            yield return OfferFrames(sink, t, 60, 2, reals);
            float offering = Time.realtimeSinceStartup - begun;
            sink.BeginFinish("the test's end");
            yield return UntilSettled(sink, 10f);
            TestContext.Out.WriteLine(sink.Describe() + " | 60 frames offered over " + offering.ToString("F2") + " s");
            CheckFrameLedger l = sink.ledger;
            Assert.That(sink.Now, Is.EqualTo(CheckFrameSink.State.Finished));
            Assert.That(l.Requested, Is.EqualTo(120));
            Assert.That(l.AllAccounted && l.Errors == 0 && l.Cancelled == 0, Is.True);
            Assert.That(l.SkippedReadbacks, Is.GreaterThanOrEqualTo(60), "the second offer of each frame met the readback in flight");
            Assert.That(l.SkippedEncodes, Is.GreaterThan(0), "the write side full");
            Assert.That(l.Written, Is.GreaterThan(0).And.LessThan(30), "a few written by the slow writer");
            Assert.That(sink.MostWritesWaiting, Is.LessThanOrEqualTo(1));
            Assert.That(sink.MostReadbacksInFlight, Is.LessThanOrEqualTo(1));
            Assert.That(sink.OfferMsMost, Is.LessThan(5.0), "an offer never waits");
            Assert.That(sink.PumpMsMost, Is.LessThan(5.0), "the Main thread's part never waits");
            // The rows keep every asked time, written or skipped, in order; a skipped row lies between written ones.
            for (int i = 0; i < l.Requested; i++) Assert.That(l[i].real, Is.EqualTo(reals[i]), "row " + i + "'s time is the offer's own");
            int[] written = Enumerable.Range(0, l.Requested).Where(i => l[i].outcome == CheckFrameLedger.Outcome.Written).ToArray();
            Assert.That(written.Length, Is.GreaterThanOrEqualTo(2));
            Assert.That(Enumerable.Range(1, written.Length - 1).Any(k => written[k] > written[k - 1] + 1), Is.True, "skipped rows kept between two written ones (their time not dropped)");
            t.Release();
            UnityEngine.Object.Destroy(t);
        }

        /// <summary>**Cancelled while writing: the write in progress told, what waits dropped and counted, everything released, the thread ended; no wait.**</summary>
        [UnityTest]
        public IEnumerator FrameSink_CancelWhileWriting_ReleasesEverything()
        {
            int workersBefore = CheckFrameSink.LiveWorkers;
            RenderTexture t = Texture(64, 36);
            var writer = new TestWriter { delayMs = 5000 };
            var sink = new CheckFrameSink("cancel", Fresh("cancel"), 64, 36, 100, writer);
            sink.StartSaving();
            float until = Time.realtimeSinceStartup + 5f;
            while (Volatile.Read(ref writer.started) == 0 && Time.realtimeSinceStartup < until) yield return OfferFrames(sink, t, 1, 1, null);
            yield return OfferFrames(sink, t, 4, 1, null);
            Assert.That(writer.started, Is.EqualTo(1), "a write in progress");
            float cancelled = Time.realtimeSinceStartup;
            sink.Cancel("the test");
            yield return UntilSettled(sink, 3f);
            float took = Time.realtimeSinceStartup - cancelled;
            TestContext.Out.WriteLine(sink.Describe() + " | settled " + took.ToString("F3") + " s after the cancel");
            Assert.That(sink.Now, Is.EqualTo(CheckFrameSink.State.Cancelled));
            Assert.That(took, Is.LessThan(1f), "the write in progress heard the cancel");
            Assert.That(sink.ledger.Cancelled, Is.GreaterThan(0));
            Assert.That(sink.ledger.AllAccounted && sink.ledger.Written == 0 && sink.ledger.Errors == 0, Is.True);
            Assert.That(sink.Released && !sink.DeadlineExceeded, Is.True);
            Assert.That(CheckFrameSink.LiveWorkers, Is.EqualTo(workersBefore), "its thread ended");
            Assert.That(sink.PumpMsMost, Is.LessThan(5.0));
            t.Release();
            UnityEngine.Object.Destroy(t);
        }

        /// <summary>**A writer's error: the sink fails at once, cancels the rest, records the error, releases everything; no wait.**</summary>
        [UnityTest]
        public IEnumerator FrameSink_WriterError_FailsAndReleases()
        {
            int workersBefore = CheckFrameSink.LiveWorkers;
            RenderTexture t = Texture(64, 36);
            var writer = new TestWriter { failOnCall = 2, delayMs = 20 };
            var sink = new CheckFrameSink("error", Fresh("error"), 64, 36, 100, writer);
            sink.StartSaving();
            yield return OfferFrames(sink, t, 30, 1, null);
            yield return UntilSettled(sink, 5f);
            TestContext.Out.WriteLine(sink.Describe());
            Assert.That(sink.Now, Is.EqualTo(CheckFrameSink.State.Failed));
            StringAssert.Contains("the test's writer failed on call 2", sink.Why);
            Assert.That(sink.ledger.Errors, Is.EqualTo(1));
            Assert.That(sink.ledger.Written, Is.GreaterThanOrEqualTo(1), "the first written");
            Assert.That(sink.ledger.AllAccounted, Is.True);
            Assert.That(sink.Released, Is.True);
            int asked = sink.ledger.Requested;
            yield return OfferFrames(sink, t, 5, 1, null);
            Assert.That(sink.ledger.Requested, Is.EqualTo(asked), "no more asked after the error");
            Assert.That(CheckFrameSink.LiveWorkers, Is.EqualTo(workersBefore), "its thread ended");
            t.Release();
            UnityEngine.Object.Destroy(t);
        }

        /// <summary>**A write that ignores the cancel past the deadline: the deadline recorded with what was held, the Main thread never waiting; released once the write returns.**</summary>
        [UnityTest]
        public IEnumerator FrameSink_DeadlinePassed_RecordedWithoutWaiting_ReleasedAfter()
        {
            int workersBefore = CheckFrameSink.LiveWorkers;
            RenderTexture t = Texture(64, 36);
            var writer = new TestWriter { delayMs = 1500, ignoreCancel = true };
            var sink = new CheckFrameSink("deadline", Fresh("deadline"), 64, 36, 100, writer, deadlineSeconds: 0.3);
            sink.StartSaving();
            float until = Time.realtimeSinceStartup + 5f;
            while (Volatile.Read(ref writer.started) == 0 && Time.realtimeSinceStartup < until) yield return OfferFrames(sink, t, 1, 1, null);
            sink.BeginFinish("the test's end");
            float begun = Time.realtimeSinceStartup;
            int frames = 0;
            while (!sink.DeadlineExceeded && Time.realtimeSinceStartup < begun + 3f) { frames++; yield return null; }
            float atDeadline = Time.realtimeSinceStartup - begun;
            TestContext.Out.WriteLine("deadline after " + atDeadline.ToString("F3") + " s over " + frames + " frames: " + sink.Describe());
            Assert.That(sink.DeadlineExceeded, Is.True);
            Assert.That(atDeadline, Is.LessThan(1.0f), "recorded at its deadline, not after the write");
            Assert.That(frames, Is.GreaterThan(3), "frames kept running meanwhile");
            StringAssert.Contains("worker running", sink.HeldAtDeadline);
            Assert.That(sink.Settled, Is.False, "the write still running");
            yield return UntilSettled(sink, 5f);
            TestContext.Out.WriteLine("then: " + sink.Describe());
            Assert.That(sink.Now, Is.EqualTo(CheckFrameSink.State.Cancelled));
            Assert.That(sink.Released && sink.ledger.AllAccounted, Is.True);
            Assert.That(CheckFrameSink.LiveWorkers, Is.EqualTo(workersBefore), "its thread ended");
            Assert.That(sink.PumpMsMost, Is.LessThan(5.0), "the Main thread never waited");
            t.Release();
            UnityEngine.Object.Destroy(t);
        }

        /// <summary>**Without a sink (capture off): a camera rendering and the game's view guarded make no sink and no thread.**</summary>
        [UnityTest]
        public IEnumerator FrameSink_Off_NothingMade()
        {
            int sinks = CheckFrameSink.LiveSinks, workers = CheckFrameSink.LiveWorkers;
            var go = new GameObject("Frame Sink Off Camera") { tag = "MainCamera" };
            Camera game = go.AddComponent<Camera>();
            var guard = new GameViewGuard(game);
            for (int i = 0; i < 5; i++)
            {
                yield return null;
                guard.Frame(null, new[] { Camera.main }, Camera.main);
            }

            UnityEngine.Object.Destroy(go);
            Assert.That(CheckFrameSink.LiveSinks, Is.EqualTo(sinks).And.EqualTo(0), "no sink");
            Assert.That(CheckFrameSink.LiveWorkers, Is.EqualTo(workers).And.EqualTo(0), "no writer thread");
            Assert.That(CheckFrameSinkDriver.Exists, Is.False, "no driver object");
            Assert.That(GameObject.Find("Check Capture Camera"), Is.Null, "no capture camera");
        }
    }
}
