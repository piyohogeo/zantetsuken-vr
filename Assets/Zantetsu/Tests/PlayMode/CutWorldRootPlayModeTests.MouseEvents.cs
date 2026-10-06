using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The cameras of a cut world send no <c>OnMouse*</c> messages (<see cref="CameraMouseEvents"/>, TL 2026-10-06): where
    /// the setting is applied, and what it leaves alone.
    /// <list type="bullet">
    /// <item>A camera given to the world's camera drawing has its event mask zero once the drawing has registered it;
    /// its culling mask is what it was.</item>
    /// <item>A camera made for the world at run time (the check's capture camera) has it zero from the moment it is
    /// made, with the game camera's culling mask.</item>
    /// <item>A camera that was not given keeps its own event mask: the setting reaches named cameras only.</item>
    /// <item>It is written once a camera: the frames that follow write nothing, telling a camera again writes nothing,
    /// and nothing puts the mask back.</item>
    /// </list>
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator MouseEvents_TheWorldsCamerasSendNone_SetOnceWhereTheyAreTakenUp_AndNoOtherCameraIsTouched()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            AddBody(root, new Vector3(0f, 1.5f, 0f));

            // The game's camera, with a culling mask of its own so that a change to it would show.
            var gameObject = TrackActor(new GameObject("Mouse Events Game Camera"));
            gameObject.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, -6f), Quaternion.identity);
            gameObject.tag = "MainCamera";
            Camera game = gameObject.AddComponent<Camera>();
            game.cullingMask = ~(1 << 7);
            game.targetTexture = Track(new RenderTexture(64, 64, 24) { name = "Mouse Events Game Target" });
            int gameCulling = game.cullingMask;

            // A camera nobody gives to the world.
            var otherObject = TrackActor(new GameObject("Mouse Events Other Camera"));
            otherObject.transform.SetPositionAndRotation(new Vector3(8f, 1.6f, -6f), Quaternion.identity);
            Camera other = otherObject.AddComponent<Camera>();
            other.depth = -5f;
            other.targetTexture = Track(new RenderTexture(64, 64, 24) { name = "Mouse Events Other Target" });
            int otherEvents = other.eventMask;
            Assert.That(game.eventMask, Is.Not.Zero, "a camera sends OnMouse* until it is told not to");
            Assert.That(otherEvents, Is.Not.Zero);

            int writesAtStart = CameraMouseEvents.Writes;
            var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
            SetPrivate(drawing, "world", root);
            SetPrivate(drawing, "cameras", new[] { game });
            yield return Until(() => root.Display.TryGetCameraStencil(game, out _, out _), "the drawing registered the game camera, as it began to render");
            Assert.That(CameraMouseEvents.IsOff(game), Is.True, "the camera given to the world sends no OnMouse* messages");
            Assert.That(game.cullingMask, Is.EqualTo(gameCulling), "its culling mask is its own");
            Assert.That(other.eventMask, Is.EqualTo(otherEvents), "a camera that was not given is not touched");
            Assert.That(CameraMouseEvents.Writes, Is.EqualTo(writesAtStart + 1), "written once, for the one camera given");

            // A camera made for the world at run time.
            var capture = new CheckCaptureCamera(root, game, null, 3f, 1f, -10f, 70f, 320, 180);
            Assert.That(CameraMouseEvents.IsOff(capture.Camera), Is.True, "the capture camera sends none from the moment it is made");
            Assert.That(capture.Camera.cullingMask, Is.EqualTo(gameCulling), "and sees what the game camera sees");
            Assert.That(CameraMouseEvents.Writes, Is.EqualTo(writesAtStart + 2), "written once more, for it");

            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            Assert.That(capture.Rendered, Is.GreaterThan(0), "the capture camera was drawn");
            Assert.That(CameraMouseEvents.Writes, Is.EqualTo(writesAtStart + 2), "the frames that followed wrote nothing");
            Assert.That(CameraMouseEvents.IsOff(game) && CameraMouseEvents.IsOff(capture.Camera), Is.True, "and nothing put a mask back");
            Assert.That(other.eventMask, Is.EqualTo(otherEvents));

            // Telling a camera again writes nothing: a second registration of the same camera costs no write.
            CameraMouseEvents.TurnOff(game);
            CameraMouseEvents.TurnOff(capture.Camera);
            CameraMouseEvents.TurnOff(null);
            Assert.That(CameraMouseEvents.Writes, Is.EqualTo(writesAtStart + 2), "telling a camera again writes nothing");
            Assert.That(game.cullingMask, Is.EqualTo(gameCulling));

            capture.Dispose();
            for (int i = 0; i < 30 && CheckCaptureCamera.PendingUnregistrations > 0; i++)
            {
                yield return null;
            }

            Assert.That(CheckCaptureCamera.PendingUnregistrations, Is.Zero, "the capture camera's slot was given back");
            Object.Destroy(drawing);
            yield return null;
            yield return EndWorld(root);
        }
    }
}
