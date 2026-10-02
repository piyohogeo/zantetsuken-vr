using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// CHECK ONLY (TL, 2026-10-02): that the game reads its own XR camera, untouched by any picture taking. Recorded at the
    /// walk's start: the camera, its parent (the tracking space's Camera Offset), tag, stereo target and target. Every frame
    /// after: Camera.main, the katana's view reference, the pose level of detail's cameras and the pieces' lifetime camera
    /// are that camera, and its tag, stereo target and target are as recorded; its parent is the one expected. The
    /// expected parent changes only at the one sanctioned place -- the check's own eye level (<see cref="NoteEyeLevelReparent"/>,
    /// called right where it hangs the camera under the pivot it made, the pivot's parent being the original Camera
    /// Offset) -- and that change is recorded with its frame and both parents. The eye level takes pitch and roll away, so
    /// the view's orientation does change there, by design: it is the viewing condition the measurement and the pictures
    /// share, not a change made by a capture camera. Nothing observed in a frame ever moves the expectation: any other
    /// parent (unrelated, a pivot of the same name, a return to the Camera Offset after the eye level) is a failure.
    /// </summary>
    public sealed class GameViewGuard
    {
        public Camera Game { get; private set; }
        public Transform OriginalParent { get; private set; }
        public Transform ExpectedParent { get; private set; }
        public int Frames { get; private set; }
        public int MainOther, ParentOther, TagChanged, EyesChanged, TargetChanged, KatanaOther, PoseLodOther, LifetimeOther, BadReparents;
        public int FirstBadFrame { get; private set; } = -1;
        public string FirstBadWhy { get; private set; }
        public readonly List<string> Reparents = new List<string>();

        private string _tag;
        private StereoTargetEyeMask _eyes;
        private RenderTexture _target;

        public GameViewGuard(Camera game)
        {
            Game = game;
            if (game == null) return;
            OriginalParent = game.transform.parent;
            ExpectedParent = OriginalParent;
            _tag = game.tag;
            _eyes = game.stereoTargetEye;
            _target = game.targetTexture;
        }

        private void Bad(string why)
        {
            if (FirstBadFrame < 0) { FirstBadFrame = Time.frameCount; FirstBadWhy = why; }
        }

        /// <summary>
        /// The sanctioned re-parenting: the eye level hung the game camera under <paramref name="pivot"/>, which it made
        /// under the original Camera Offset. Checked by reference; recorded with the frame and both parents; only then is
        /// the pivot the expected parent. Anything else is a bad re-parenting, and the expectation stays.
        /// </summary>
        public void NoteEyeLevelReparent(Transform pivot)
        {
            if (Game == null) return;
            string record = "frame " + Time.frameCount + ": the eye level hung the game camera from " + (ExpectedParent != null ? ExpectedParent.name : "none") + " to " + (pivot != null ? pivot.name : "none");
            if (pivot == null || pivot.parent != OriginalParent || Game.transform.parent != pivot || ExpectedParent != OriginalParent)
            {
                BadReparents++;
                Bad("a re-parenting given as the eye level's was not one (" + record + "; the pivot's parent " + (pivot != null && pivot.parent != null ? pivot.parent.name : "none") + ", the camera's parent " + (Game.transform.parent != null ? Game.transform.parent.name : "none") + ")");
                Reparents.Add(record + " -- REFUSED");
                return;
            }

            Reparents.Add(record + " (the pivot under the original " + (OriginalParent != null ? OriginalParent.name : "none") + ")");
            ExpectedParent = pivot;
        }

        /// <summary>One frame's reading: what the game reads, and the camera as recorded.</summary>
        public void Frame(Transform katanaView, IEnumerable<Camera> poseLodCameras, Camera lifetimeCamera)
        {
            if (Game == null) return;
            Frames++;
            if (!ReferenceEquals(Camera.main, Game)) { MainOther++; Bad("Camera.main is " + (Camera.main != null ? Camera.main.name : "none")); }
            if (!ReferenceEquals(Game.transform.parent, ExpectedParent)) { ParentOther++; Bad("its parent is " + (Game.transform.parent != null ? Game.transform.parent.name : "none") + ", not the expected " + (ExpectedParent != null ? ExpectedParent.name : "none")); }
            if (Game.tag != _tag) { TagChanged++; Bad("its tag changed"); }
            if (Game.stereoTargetEye != _eyes) { EyesChanged++; Bad("its stereo target changed"); }
            if (Game.targetTexture != _target) { TargetChanged++; Bad("its target changed"); }
            if (katanaView != null && !ReferenceEquals(katanaView, Game.transform)) { KatanaOther++; Bad("the katana's view reference is " + katanaView.name); }
            if (poseLodCameras != null) foreach (Camera c in poseLodCameras) if (!ReferenceEquals(c, Game)) { PoseLodOther++; Bad("a pose level of detail reads " + (c != null ? c.name : "none")); }
            if (!ReferenceEquals(lifetimeCamera, Game)) { LifetimeOther++; Bad("the pieces' lifetime reads " + (lifetimeCamera != null ? lifetimeCamera.name : "none")); }
        }

        public bool Passed => Game != null && Frames > 0 && MainOther + ParentOther + TagChanged + EyesChanged + TargetChanged + KatanaOther + PoseLodOther + LifetimeOther + BadReparents == 0;

        public string Describe() =>
            "over " + Frames + " frames: Camera.main another " + MainOther + ", parent not the expected " + ParentOther + ", tag " + TagChanged + ", stereo target " + EyesChanged + ", target " + TargetChanged + ", the katana's view another " + KatanaOther
            + ", a pose level of detail another " + PoseLodOther + ", the lifetime another " + LifetimeOther + ", re-parentings refused " + BadReparents + "; sanctioned re-parentings [" + string.Join("; ", Reparents) + "]"
            + (FirstBadFrame >= 0 ? "; first at frame " + FirstBadFrame + ": " + FirstBadWhy : "");
    }
}
