using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The world's ending in a frame whose display has draws registered through the real registration path
    /// (<see cref="CutWorldCameraDrawing"/> at the camera's beginCameraRendering, the host's report at its
    /// endCameraRendering) -- unit 10-A, 2026-10-18. On the Quest (v27) the app's quit ran the world's OnDestroy in
    /// the frame whose draws were already rendered, and the display refused its disposal by the frame count alone:
    /// an InvalidOperationException out of OnDestroy, with the owners already disposed and the display, the storage
    /// and the pools not. The display now refuses only while a camera's draws of this frame are not yet rendered,
    /// and the world's release frees nothing at all when refused, finishing in a later turn.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static CutWorldCameraDrawing DrawingFor(CutWorldRoot root, Camera camera)
        {
            var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
            typeof(CutWorldCameraDrawing).GetField("world", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(drawing, root);
            typeof(CutWorldCameraDrawing).GetField("cameras", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(drawing, new[] { camera });
            return drawing;
        }

        // A camera that renders every frame in the Editor's batch mode too: into a target of its own, not the screen.
        private Camera EndingCamera()
        {
            var cameraObject = Track(new GameObject("Ending Test Camera"));
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(new Vector3(0f, 1.5f, -4f), Quaternion.LookRotation(Vector3.forward));
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = Track(new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32));
            return camera;
        }

        /// <summary>Errors, exceptions and assertions logged while it is held (ordinary log lines are the world's own and expected).</summary>
        private sealed class ErrorLogCount : System.IDisposable
        {
            public readonly System.Collections.Generic.List<string> Errors = new System.Collections.Generic.List<string>();
            public ErrorLogCount() { Application.logMessageReceived += OnLog; }
            private void OnLog(string message, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Errors.Add(type + ": " + message);
            }
            public void Dispose() { Application.logMessageReceived -= OnLog; }
        }

        private IEnumerator UntilDrawn(CutWorldRoot root, CutWorldCameraDrawing drawing)
        {
            yield return Until(() => root.Display != null && root.Display.ShownCount > 0, "the body is shown by the display");
            yield return Until(() => drawing.DrawnFrames > 0, "the camera drawing registered the body's draws (frames with nothing to draw: " + drawing.FramesWithNothingToDraw + ", last refused frame " + drawing.LastRefusedFrame + ")");
        }

        /// <summary>
        /// **The ending asked for after this frame's draws were registered and rendered -- as an app's quit runs the
        /// world's OnDestroy -- finishes in that very frame, with no exception.** A body shown and drawn by the camera
        /// drawing; at the end of the frame (after rendering) the display reports no undrawn draws, Shutdown returns
        /// true, the world is released and the display disposed; a second Shutdown is true as well; the ending
        /// log holds no error.
        /// </summary>
        [UnityTest]
        public IEnumerator Ending_InTheFrameWhoseDrawsWereRendered_Finishes_WithoutAnException()
        {
            using var errors = new ErrorLogCount();
            CutWorldRoot root = NewWorld(out Shader _);
            Camera camera = EndingCamera();
            CutWorldCameraDrawing drawing = DrawingFor(root, camera);
            AddBody(root, Vector3.zero);
            yield return UntilDrawn(root, drawing);
            Assert.That(root.Display.TryGetCameraStencil(camera, out _, out _), Is.True, "the camera is registered with the display");

            // After this camera's rendering, in this very frame (the Editor's batch mode evokes no WaitForEndOfFrame):
            // from an endCameraRendering handler that runs after the camera drawing's own, which reported the camera
            // rendered. It is what an app's quit does to the world, in the frame whose draws were rendered.
            bool asked = false, drawnThisFrame = false, undrawnWhenAsked = true, shutdownAnswer = false, releasedWhenAsked = false, disposedWhenAsked = false, secondAnswer = false;
            int askedFrame = -1;
            void OnEnd(ScriptableRenderContext context, Camera rendered)
            {
                if (asked || rendered != camera || root.Display == null) return;
                asked = true;
                askedFrame = Time.frameCount;
                drawnThisFrame = drawing.DrawnFrames > 0;
                undrawnWhenAsked = root.Display.HasUndrawnDraws;
                shutdownAnswer = root.Shutdown();
                releasedWhenAsked = root.IsReleased;
                disposedWhenAsked = root.Display.IsDisposed;
                secondAnswer = root.Shutdown();
            }

            RenderPipelineManager.endCameraRendering += OnEnd;
            try
            {
                yield return Until(() => asked, "a frame in which the camera finished rendering");
            }
            finally
            {
                RenderPipelineManager.endCameraRendering -= OnEnd;
            }

            Assert.That(drawnThisFrame, Is.True);
            Assert.That(undrawnWhenAsked, Is.False, "the camera rendered this frame's draws, and said so");
            Assert.That(shutdownAnswer, Is.True, "the ending finishes in the frame whose draws were rendered");
            Assert.That(releasedWhenAsked, Is.True, "released in that very frame");
            Assert.That(disposedWhenAsked, Is.True, "the display was disposed with the world");
            Assert.That(secondAnswer, Is.True, "asked again in the same frame: released already");
            Assert.That(root.IsReleased, Is.True);
            Assert.That(askedFrame, Is.GreaterThan(0));
            yield return null;
            Assert.That(root.Shutdown(), Is.True, "asked again in a later frame: still released");
            Assert.That(errors.Errors, Is.Empty, "no error or exception was logged by the ending");
        }

        /// <summary>
        /// **The ending asked for while this frame's draws are registered but not yet rendered frees nothing, and the
        /// frame draws as it would have; the next frame's turn releases.** From a beginCameraRendering handler that
        /// runs after the camera drawing's, with the draws just registered: Shutdown is false, nothing is disposed,
        /// no exception; that frame's camera still renders (the drawing's frame count grows); the ordinary late
        /// update of a later frame releases the world, once.
        /// </summary>
        [UnityTest]
        public IEnumerator Ending_WhileThisFramesDrawsAreNotRenderedYet_FreesNothing_AndALaterTurnReleases()
        {
            using var errors = new ErrorLogCount();
            CutWorldRoot root = NewWorld(out Shader _);
            Camera camera = EndingCamera();
            CutWorldCameraDrawing drawing = DrawingFor(root, camera);
            AddBody(root, Vector3.zero);
            yield return UntilDrawn(root, drawing);

            bool asked = false, undrawnWhenAsked = false, shutdownAnswer = true, releasedWhenAsked = true, disposedWhenAsked = true;
            int askedFrame = -1, drawnFramesWhenAsked = -1;
            void OnBegin(ScriptableRenderContext context, Camera rendering)
            {
                if (asked || rendering != camera || root.Display == null) return;
                asked = true;
                askedFrame = Time.frameCount;
                drawnFramesWhenAsked = drawing.DrawnFrames;
                undrawnWhenAsked = root.Display.HasUndrawnDraws;
                shutdownAnswer = root.Shutdown();
                releasedWhenAsked = root.IsReleased;
                disposedWhenAsked = root.Display.IsDisposed;
            }

            // Subscribed after the drawing's own handler: called after it in the same camera's begin, with the draws registered.
            RenderPipelineManager.beginCameraRendering += OnBegin;
            try
            {
                yield return Until(() => asked, "a frame in which the camera began rendering with the draws registered");
            }
            finally
            {
                RenderPipelineManager.beginCameraRendering -= OnBegin;
            }

            Assert.That(undrawnWhenAsked, Is.True, "asked with this frame's draws registered and not rendered");
            Assert.That(shutdownAnswer, Is.False, "the ending did not finish in that frame");
            Assert.That(releasedWhenAsked, Is.False, "nothing was freed");
            Assert.That(disposedWhenAsked, Is.False, "the display was not disposed under the registered draws");
            Assert.That(root.IsEnding || root.IsReleased, Is.True, "the ending began");

            // The frame drew as it would have, and a later turn of the ending releases.
            yield return Until(() => root.IsReleased, "the ending's later turn released the world");
            Assert.That(drawing.DrawnFrames, Is.GreaterThanOrEqualTo(drawnFramesWhenAsked), "that frame's drawing was not undone");
            Assert.That(root.Display.IsDisposed, Is.True);
            Assert.That(Time.frameCount, Is.GreaterThan(askedFrame), "released in a later frame");
            Assert.That(errors.Errors, Is.Empty, "no error or exception was logged by the ending");
        }
    }
}
