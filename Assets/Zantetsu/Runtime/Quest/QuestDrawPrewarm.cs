using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering.Urp;
using Zantetsu.Sandbox;

namespace Zantetsu.Quest
{
    /// <summary>
    /// The Quest's drawing preparation before the first cut (2026-10-18): what the scripted NPC slash and city walk did
    /// before their replay (<c>PrewarmBeforeReplay</c>), made one piece so that play prepares the same way. With the
    /// real camera and the display's own materials it asks
    /// <list type="bullet">
    /// <item>the native route (vp3c-native) for its warm request -- the ordinary and clip variants of every body
    /// material, the shadow casters, the motion pass where asked, the GPU cull and the constant copy -- once the route
    /// exists and has enqueued the camera at least once (<see cref="VpNativeDrawRoute.IsCameraEnqueued"/>);</item>
    /// <item>Unity for three frames of the cut's stencil draws (init, volume, cap) and of the katana's and the slash
    /// waves' degenerate copies (<see cref="QuestNpcUnityPrewarm"/>);</item>
    /// </list>
    /// and is complete only when the native warm events were consumed without failure AND the Unity draws were rendered
    /// three times and their GPU fence passed after four more frames. A failure is a failure, never a completion.
    /// <para>
    /// **Resources.** The Unity helper's buffers are freed only when its fence says the GPU is done with them; on a
    /// failure, a timeout or an abandonment they are handed to its own retirement, which waits for that, never freed at
    /// once. What it records, where, and whether a pause holds it are the caller's: the scripted runs write
    /// <see cref="Report"/> to their directory and wait while the game is paused; play keeps preparing through a pause
    /// (the consent dialog, the menu) and only says the outcome.
    /// </para>
    /// </summary>
    internal sealed class QuestDrawPrewarm
    {
        public const double DefaultTimeoutSeconds = 30.0;
        public const int UnityWarmFrames = 3;
        public const int UnityFramesAfter = 4;

        private readonly Camera _camera;
        private readonly CutWorldRoot _world;
        private readonly CutWorldCameraDrawing _drawing;
        private readonly bool _native, _motion, _holdWhilePaused;
        private readonly double _timeoutSeconds;
        private QuestNpcUnityPrewarm _unity;
        private bool _retireFailureLogged;

        public QuestDrawPrewarm(Camera camera, CutWorldRoot world, CutWorldCameraDrawing drawing, bool native, bool motion, bool holdWhilePaused,
            double timeoutSeconds = DefaultTimeoutSeconds)
        {
            _camera = camera;
            _world = world;
            _drawing = drawing;
            _native = native;
            _motion = motion;
            _holdWhilePaused = holdWhilePaused;
            _timeoutSeconds = timeoutSeconds;
        }

        /// <summary>Null while it goes on and after a completion; the first thing that went wrong otherwise.</summary>
        public string Failure { get; private set; }

        /// <summary>Over: completed, failed or abandoned.</summary>
        public bool Finished { get; private set; }

        public bool Completed => Finished && Failure == null;

        /// <summary>What was asked and observed, one line each (the scripted runs' prewarm.txt).</summary>
        public List<string> Report { get; } = new List<string>();

        /// <summary>The frame it began and the frame it finished; -1 before.</summary>
        public int BeganFrame { get; private set; } = -1;
        public int FinishedFrame { get; private set; } = -1;

        // The clock of the deadline: the game's pausable one, so that time paused (a system pause with no frame drawn) is no time spent.
        private static double Clock => Zantetsu.Core.GamePause.UnscaledTimeAsDouble;

        public IEnumerator Run()
        {
            if (BeganFrame >= 0) throw new InvalidOperationException("a drawing preparation runs once");
            BeganFrame = Time.frameCount;
            double deadline = Clock + _timeoutSeconds;
            Report.Add("native requested=" + _native);
            Report.Add("Unity expected rendered warm frames=" + UnityWarmFrames);
            if (_camera == null) Failure = "the real camera is required";
            else if (_world == null || _world.Display == null) Failure = "the prepared world has no display";
            VpNativeDrawRoute nativeRoute = null;
            if (Failure == null && _native)
            {
                if (_drawing == null) Failure = "the prepared world has no camera drawing component";
                // The route made, and the camera enqueued by it at least once: RequestPrewarm's preconditions (its own refusal otherwise).
                while (Failure == null && !Finished && (nativeRoute == null || !nativeRoute.IsCameraEnqueued(_camera)) && Clock < deadline)
                {
                    yield return null;
                    while (_holdWhilePaused && Zantetsu.Core.GamePause.IsPaused) yield return null;
                    nativeRoute = _drawing != null ? _drawing.NativeRoute : null;
                }

                if (Finished) yield break;   // abandoned meanwhile
                if (Failure == null && nativeRoute == null) Failure = "the native route did not become available within " + _timeoutSeconds + " s";
                else if (Failure == null && !nativeRoute.IsCameraEnqueued(_camera)) Failure = "the native route did not enqueue the camera within " + _timeoutSeconds + " s";
                if (Failure == null)
                {
                    try
                    {
                        if (!nativeRoute.RequestPrewarm(_camera, _world.Display, out string failure, _motion)) Failure = failure ?? "native prewarm was not accepted";
                    }
                    catch (Exception e) { Failure = "native prewarm request: " + e.Message; }
                }
            }

            if (Failure == null)
            {
                try { _unity = QuestNpcUnityPrewarm.Create(_camera, _world.Display.PrewarmStencilMaterials, _world.Display.SinglePassInstanced, _world.gameObject.layer, SandboxPropSlashPlayerCheck.ReplayVisualRenderers()); }
                catch (Exception e) { Failure = "Unity prewarm creation: " + e.Message; }
            }

            if (_unity != null)
            {
                QuestNpcUnityPrewarm unity = _unity;
                for (int frame = 0; frame < UnityWarmFrames && Failure == null && !Finished; frame++)
                {
                    try { unity.Issue(); }
                    catch (Exception e) { Failure = "Unity prewarm issue: " + e.Message; }
                    yield return null;
                    while (_holdWhilePaused && Zantetsu.Core.GamePause.IsPaused) yield return null;
                }

                // Allow real camera clears and the last warm frame's GPU work to finish.
                for (int frame = 0; frame < UnityFramesAfter && !Finished; frame++)
                {
                    yield return null;
                    while (_holdWhilePaused && Zantetsu.Core.GamePause.IsPaused) yield return null;
                }

                if (Finished) yield break;   // abandoned meanwhile: the helper went to its retirement
                bool readyKnown = TryUnityReady(unity, out bool ready);
                while (readyKnown && !ready && Clock < deadline && !Finished)
                {
                    yield return null;
                    while (_holdWhilePaused && Zantetsu.Core.GamePause.IsPaused) yield return null;
                    readyKnown = TryUnityReady(unity, out ready);
                }

                if (Finished) yield break;
                Report.Add("Unity rendered warm frames=" + unity.RenderedWarmFrames);
                Report.Add("Unity replay visual renderers=" + unity.ReplayVisualRendererCount + "; material slots=" + unity.ReplayVisualMaterialCount);
                Report.Add("Unity buffer retirement query succeeded=" + readyKnown + "; ready=" + ready);
                if (unity.RenderedWarmFrames != UnityWarmFrames && Failure == null) Failure = "Unity prewarm did not render all " + UnityWarmFrames + " preparation frames";
                if (!ready && Failure == null) Failure = "Unity prewarm GPU completion was not observed within " + _timeoutSeconds + " s";
                if (ready)
                {
                    try { unity.Dispose(); _unity = null; }
                    catch (Exception e) { if (Failure == null) Failure = "Unity prewarm disposal: " + e.Message; }
                }

                RetireUnitySafely();   // never free an in-flight or unconfirmed buffer on failure
            }

            if (_native && nativeRoute != null)
            {
                bool complete = false;
                // Preserve native completion evidence even when Unity's fence query failed.
                try
                {
                    bool requested = nativeRoute.GetPrewarmStatus(_camera, out complete, out string failure);
                    if (Failure == null && (!requested || failure != null)) Failure = failure ?? "native prewarm request disappeared";
                }
                catch (Exception e) { if (Failure == null) Failure = "native prewarm status: " + e.Message; }
                while (Failure == null && !complete && !Finished)
                {
                    try
                    {
                        if (!nativeRoute.GetPrewarmStatus(_camera, out complete, out string failure)) Failure = "native prewarm request disappeared";
                        else if (failure != null) Failure = failure;
                    }
                    catch (Exception e) { Failure = "native prewarm status: " + e.Message; }
                    if (Failure == null && !complete && Clock >= deadline) Failure = "native prewarm did not complete within " + _timeoutSeconds + " s";
                    if (Failure == null && !complete)
                    {
                        yield return null;
                        while (_holdWhilePaused && Zantetsu.Core.GamePause.IsPaused) yield return null;
                    }
                }

                if (Finished) yield break;
                Report.Add("native complete=" + complete);
                Report.Add(nativeRoute.DescribePrewarm(_camera));
            }

            Report.Add("result=" + (Failure ?? "completed"));
            Finished = true;
            FinishedFrame = Time.frameCount;
        }

        /// <summary>
        /// Gives the preparation up where it stands (its owner destroyed): the Unity helper goes to its retirement, which
        /// frees its buffers once the GPU is done with them; not a completion. Nothing happens once it is finished.
        /// </summary>
        public void Abandon(string why)
        {
            RetireUnitySafely();
            if (Finished) return;
            Failure ??= "abandoned: " + why;
            Report.Add("result=" + Failure);
            Finished = true;
            FinishedFrame = Time.frameCount;
        }

        private bool TryUnityReady(QuestNpcUnityPrewarm unity, out bool ready)
        {
            ready = false;
            try { ready = unity.ReadyToDispose; return true; }
            catch (Exception e)
            {
                if (Failure == null) Failure = "Unity prewarm GPU completion query: " + e.Message;
                return false;
            }
        }

        private void RetireUnitySafely()
        {
            if (_unity == null) return;
            try { _unity.RetireWhenReady(); _unity = null; }
            catch (Exception e)
            {
                // Retain the helper if the driver cannot prove completion: buffers the GPU may still read are never
                // force-released, and the caller's coroutine is not aborted.
                if (!_retireFailureLogged)
                {
                    _retireFailureLogged = true;
                    Debug.LogWarning("QUEST PREWARM: prewarm buffers retained; retirement failed: " + e.Message);
                }
            }
        }
    }
}
