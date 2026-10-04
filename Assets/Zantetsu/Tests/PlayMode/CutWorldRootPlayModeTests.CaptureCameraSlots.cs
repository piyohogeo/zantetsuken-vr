using System.Collections;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// Capture cameras made and given back one after another, each read and disposed straight after its frame was drawn --
    /// as the city walk's pictures are (2026-10-03: in w2 the walk shots, taken after two such pictures, were not drawn for
    /// by the display). The display refuses a slot back in the frame it drew for the camera; the camera is then kept,
    /// disabled, and given back from a later frame. More pictures than the display has camera slots are each drawn for,
    /// and every slot comes back.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator CaptureCamera_DisposedInTheFrameItWasDrawn_GivesItsSlotBack_SoMorePicturesThanSlotsAreAllDrawnFor()
        {
            CutWorldRoot root = NewKinematicWorld();
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var gameObject = new GameObject("Capture Slots Game Camera");
            gameObject.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, 4f), Quaternion.LookRotation(Vector3.back));
            gameObject.tag = "MainCamera";
            Camera game = gameObject.AddComponent<Camera>();
            // Rendering every frame, as the Player's game camera does (a batch-mode run renders no camera drawing to the screen).
            var gameTarget = new RenderTexture(64, 36, 24);
            game.targetTexture = gameTarget;
            var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
            typeof(CutWorldCameraDrawing).GetField("world", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(drawing, root);
            typeof(CutWorldCameraDrawing).GetField("cameras", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(drawing, new[] { game });
            try
            {
                yield return null;
                yield return null;
                int slots = (int)typeof(CutWorldProfile).GetField("stencilCameraCapacity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(root.Profile);
                int pictures = slots + 2, deferred = 0;
                for (int p = 0; p < pictures; p++)
                {
                    var shot = new CheckCaptureCamera.Shot { name = "slot-" + p, position = new Vector3(0f, 2f, 5f), lookAt = Vector3.zero, fieldOfView = 60f };
                    var capture = new CheckCaptureCamera(root, game, null, 0f, 0f, 0f, 60f, 64, 36, new[] { shot });
                    bool disposed = false, taken = false;
                    int rendered = 0, pendingBefore = CheckCaptureCamera.PendingUnregistrations, pendingAfter = 0;
                    // Disposed at the end of its second render: in the frame the display drew for it, as a picture read at the
                    // frame's end is (batch-mode tests have no end of frame to wait for).
                    System.Action<ScriptableRenderContext, Camera> end = (context, camera) =>
                    {
                        if (disposed || !ReferenceEquals(camera, capture.Camera) || capture.Rendered < 2) return;
                        taken = capture.TakenByDisplay;
                        rendered = capture.Rendered;
                        capture.Dispose();
                        pendingAfter = CheckCaptureCamera.PendingUnregistrations;
                        disposed = true;
                    };
                    RenderPipelineManager.endCameraRendering += end;
                    for (int f = 0; f < 30 && !disposed; f++) yield return null;
                    RenderPipelineManager.endCameraRendering -= end;
                    if (!disposed) capture.Dispose();
                    if (pendingAfter > pendingBefore) deferred++;
                    TestContext.Out.WriteLine("picture " + p + ": rendered " + rendered + ", taken by the display " + taken + ", slots waiting before it " + pendingBefore + ", after its disposal " + pendingAfter);
                    Assert.That(disposed, Is.True, "picture " + p + " rendered twice");
                    Assert.That(taken, Is.True, "picture " + p + " drawn for by the display (" + pictures + " pictures, " + slots + " slots)");
                }

                Assert.That(deferred, Is.GreaterThan(0), "the display refused a slot back in the frame it drew (the case this covers)");

                for (int i = 0; i < 10 && CheckCaptureCamera.PendingUnregistrations > 0; i++) yield return null;
                Assert.That(CheckCaptureCamera.PendingUnregistrations, Is.Zero, "every slot given back");
                yield return null;
                Assert.That(GameObject.Find("Check Capture Camera"), Is.Null, "every capture camera destroyed");
            }
            finally
            {
                // the tagged game camera never outlives this case, whatever it asserted
                Object.DestroyImmediate(gameObject);
                gameTarget.Release();
                Object.Destroy(gameTarget);
                Object.Destroy(drawing);
            }

            yield return EndWorld(root);
        }
    }
}
