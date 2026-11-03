using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The GPU bake warmed at start-up (SKIN-GPU-FIRST-STARTUP-1, 2026-11-02): the product's Bake16 kernel dispatched once, with
    /// the real shader, kernel and effective keywords, over a prepared skin model's shared input and a PRIVATE identity palette, into
    /// a PRIVATE 16-byte output -- never the display's vertex buffer, no snapshot of the pool, no cut, nothing of the Ledger or the
    /// physics. On the Quest the first dispatch of the kernel cost the first hit's frame about 40 ms; warmed here it does not.
    /// <para>
    /// **Lifetime (TL review, 2026-11-02).** Everything the GPU reads or writes -- the model's shared input (held through
    /// <see cref="VpSkinModel.Hold"/>), the palette and the output -- stays until the completion readback returns, whatever the
    /// coroutine does: a timeout, an <see cref="Abandon"/>, an exception or the owner's end only change what is reported. The
    /// readback's callback is the retirement; nothing is freed before it (<see cref="Retired"/>, <see cref="Pending"/>). The
    /// completion is that readback (the GPU ran the dispatch), never a load, a kernel lookup or a frame count.
    /// </para>
    /// <para>
    /// **Exceptions.** No API exception leaves <see cref="Run"/>: each becomes a failure, reported once, so that a start-up
    /// preparation running this ends exactly once. A failure is a failure, never a completion.
    /// </para>
    /// </summary>
    public sealed class VpSkinBakeWarm
    {
        public const int MaxFrames = 600;

        private readonly VpSkinBakeResources _resources;
        private readonly VpSkinModel _model;
        private GraphicsBuffer _palette, _output;
        private bool _held, _issued, _requested, _done, _error;

        public VpSkinBakeWarm(VpSkinBakeResources resources, VpSkinModel model)
        {
            _resources = resources;
            _model = model;
        }

        /// <summary>Warms whose GPU work is not yet known to be done (their resources still held).</summary>
        public static int Pending { get; private set; }

        /// <summary>For a test only: a message to throw right after the dispatch was recorded (the "issued, no readback" path). Null in the product.</summary>
        public static Func<string> ThrowAfterDispatchForTest;

        public string Failure { get; private set; }
        public bool Finished { get; private set; }
        public bool Completed => Finished && Failure == null;
        /// <summary>Every GPU resource of this warm freed and the model's hold released (after the readback returned, or when nothing was issued).</summary>
        public bool Retired { get; private set; }
        public List<string> Report { get; } = new List<string>();
        public int BeganFrame { get; private set; } = -1;
        public int FinishedFrame { get; private set; } = -1;
        /// <summary>Main-thread milliseconds of the preparation and the dispatch recording (no GPU time).</summary>
        public double MainMs { get; private set; }
        public long OutputBytes { get; private set; }

        public IEnumerator Run()
        {
            if (BeganFrame >= 0) { Fail("a bake warm runs once"); yield break; }
            BeganFrame = Time.frameCount;
            if (!TryIssue(out string why)) { if (!_issued) RetireNow(); Fail(why); yield break; }   // refused before any GPU work: nothing to hold
            int waited = 0;
            while (!_done && !Finished && waited < MaxFrames) { waited++; yield return null; }
            if (Finished) yield break;   // abandoned meanwhile: the retirement stays with the readback
            if (!_done) { Fail("the GPU completion was not observed within " + MaxFrames + " frames (the input, the palette and the output stay held until the readback returns)"); yield break; }
            if (_error) { Fail("the completion readback reported an error (the resources were freed with it)"); yield break; }
            Report.Add("skin bake warm: model " + _model.VertexCount + " vertices, " + _model.BoneCount + " bones; output " + OutputBytes + " bytes (private, freed by the readback); main-thread " + MainMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " ms; readback waited " + waited + " frames");
            Finished = true;
            FinishedFrame = Time.frameCount;
            Report.Add("skin bake warm: completed (frames " + BeganFrame + ".." + FinishedFrame + ")");
        }

        // Everything synchronous of the warm, under one try: a refusal or an exception is a reason, never thrown to the caller.
        private bool TryIssue(out string why)
        {
            why = null;
            try
            {
                if (_resources == null || _resources.IsDisposed) { why = "no skin bake resources"; return false; }
                if (_resources.Bake == null) { why = "the route has no GPU bake"; return false; }
                if (_model == null || _model.IsDisposed) { why = "no skin model"; return false; }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!_model.TryPrepareSharedInput(out string refusal)) { why = "shared input: " + refusal; return false; }
                _model.Hold();   // the shared input lives while the GPU reads it
                _held = true;
                int bones = Math.Max(1, _model.BoneCount);
                _palette = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bones, 64) { name = "Skin bake warm palette" };
                var identity = new float4x4[bones];
                for (int i = 0; i < bones; i++) identity[i] = float4x4.identity;
                _palette.SetData(identity);
                OutputBytes = (long)_model.VertexCount * 16;
                _output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _model.VertexCount, 16) { name = "Skin bake warm output" };
                // Counted as issued BEFORE the call (review 2): whatever leaves Dispatch after the GPU work was recorded -- a diagnosis hook's
                // exception, say -- must find the resources held, never freed under a dispatch that may run. An exception from the dispatch's
                // own argument checks then keeps two small buffers and a hold (reported), which is the safe side.
                _issued = true;
                Pending++;
                _resources.Bake.Dispatch(_model.SharedInput, _palette, bones, _output, 0);
                if (ThrowAfterDispatchForTest != null) throw new InvalidOperationException(ThrowAfterDispatchForTest());
                MainMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                AsyncGPUReadback.Request(_output, 16, 0, OnReadback);
                _requested = true;
                return true;
            }
            catch (Exception e)
            {
                why = "exception: " + e.GetType().Name + ": " + e.Message;
                // Nothing on the GPU yet: freed at once. Dispatched but the readback not requested: no retirement can come; the
                // resources are kept (a leak the report names) rather than freed under a dispatch that may run.
                if (!_issued) RetireNow();
                else if (!_requested) why += " (dispatched without a completion readback: the resources are kept, not freed)";
                return false;
            }
        }

        private void OnReadback(AsyncGPUReadbackRequest request)
        {
            _done = true;
            _error = request.hasError;
            RetireNow();
        }

        private void RetireNow()
        {
            if (Retired) return;
            Retired = true;
            if (_issued) Pending--;
            try { _palette?.Dispose(); } catch (Exception) { }
            try { _output?.Dispose(); } catch (Exception) { }
            _palette = null; _output = null;
            if (_held) { _held = false; try { _model.Release(); } catch (Exception) { } }
        }

        /// <summary>
        /// For a test only: recovers what a failed warm kept (dispatched, no completion readback) the way the product would have -- a
        /// readback of the dispatched output whose callback is the retirement, so nothing is freed before the GPU is done with it.
        /// Returns whether such a readback was requested (false: nothing kept, or already on its way to retirement).
        /// </summary>
        public bool RetireByReadbackForTest()
        {
            if (!_issued || Retired || _requested || _output == null) return false;
            _requested = true;
            AsyncGPUReadback.Request(_output, 16, 0, _ => RetireNow());
            return true;
        }

        /// <summary>
        /// Gives the warm up where it stands (its owner destroyed or the preparation abandoned): reported as a failure once; the GPU
        /// resources stay held until the readback returns (the retirement is the readback's, not the coroutine's). Nothing once finished.
        /// </summary>
        public void Abandon(string why)
        {
            if (Finished) return;
            Failure = "abandoned: " + why;
            Finished = true;
            FinishedFrame = Time.frameCount;
            Report.Add("skin bake warm: " + Failure + (_issued && !Retired ? " (the GPU work is pending: the input, the palette and the output are held until its readback returns)" : ""));
        }

        private void Fail(string why)
        {
            if (Finished) return;
            Failure = why;
            Finished = true;
            FinishedFrame = Time.frameCount;
            Report.Add("skin bake warm: FAILED: " + why);
        }
    }
}
