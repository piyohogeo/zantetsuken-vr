using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The sandbox scene, played (DESIGN 4.5.6, 5.6, 7.2): the scene's own root builds the world, its own probe
    /// registers a body and asks for cuts, its own drawing component draws what the display settles for the scene's
    /// camera, and **the pixels that camera produces are read back**.
    /// <para>
    /// **Nothing of the product is replaced.** The scene is loaded as it is on disk; no private field is written, no
    /// driver entrance is called by hand, and the only thing this does besides looking is press the probe's own
    /// buttons — through the same public calls the keys make. What it looks at is of two kinds, kept apart in the
    /// report: **what the display's records say**, and **what the camera really drew**, read from a render texture.
    /// </para>
    /// <para>
    /// **What is not looked at here**: shadows, the depth buffer, the stencil buffer's own contents, XR, and anything
    /// about performance. The colours below are read from the camera's colour target only.
    /// </para>
    /// </summary>
    public class CutWorldSandboxScenePlayModeTests
    {
        private const string ScenePath = "Assets/Scenes/CutWorldSandbox.unity";
        private const float DeadlineSeconds = 90f;
        private const int Width = 320;
        private const int Height = 200;

        private CutWorldRoot _world;
        private SandboxCutWorldProbe _probe;
        private Camera _camera;
        private RenderTexture _target;
        private Texture2D _readback;
        private VpCutSurfaceColour.State _colours;
        private bool _coloursTaken;
        private readonly List<HoldingExecutor> _holding = new List<HoldingExecutor>();

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            // Nothing may be left held: an ending waits for its destinations to confirm they have stopped.
            foreach (HoldingExecutor held in _holding)
            {
                held.HoldEverything = false;
            }

            _holding.Clear();
            if (_world != null && !_world.IsReleased)
            {
                _world.Shutdown();
                float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
                while (!_world.IsReleased && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }
            }

            if (_camera != null)
            {
                _camera.targetTexture = null;
            }

            if (_target != null)
            {
                _target.Release();
                UnityEngine.Object.DestroyImmediate(_target);
                _target = null;
            }

            if (_readback != null)
            {
                UnityEngine.Object.DestroyImmediate(_readback);
                _readback = null;
            }

            if (_coloursTaken)
            {
                VpCutSurfaceColour.Restore(_colours);
                _coloursTaken = false;
            }

            CutWorldRoot.nextWorldExecutors = null;
            _world = null;
            _probe = null;
            _camera = null;
            yield return null;
        }

        // ----- a destination whose collection a case holds ---------------------------------------------------------

        /// <summary>
        /// One of the product's destinations, wrapped: the work runs as usual and is handed back only once the case
        /// lets it go. It holds the **collection**, never the running. Holding every destination is how a case keeps
        /// a cut provisional for as long as it wants to look at it.
        /// </summary>
        private sealed class HoldingExecutor : IWorkExecutor
        {
            private readonly IWorkExecutor _inner;
            private readonly List<IDispatchWork> _held = new List<IDispatchWork>();

            internal HoldingExecutor(IWorkExecutor inner)
            {
                _inner = inner;
            }

            internal bool HoldEverything { get; set; } = true;

            public WorkDestination Destination => _inner.Destination;

            public int Capacity => _inner.Capacity;

            public int Held => _inner.Held;

            public bool CanAccept => _inner.CanAccept;

            public bool TryAccept(IDispatchWork work)
            {
                return _inner.TryAccept(work);
            }

            public void BeginAccepted(IDispatchWork work)
            {
                _inner.BeginAccepted(work);
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                if (!HoldEverything && _held.Count > 0)
                {
                    work = _held[0];
                    _held.RemoveAt(0);
                    completion = WorkCompletion.Finished;
                    return true;
                }

                if (!_inner.TryTakeFinished(out work, out completion))
                {
                    return false;
                }

                if (!HoldEverything)
                {
                    return true;
                }

                _held.Add(work);
                work = null;
                completion = default;
                return false;
            }

            public void CloseForNewWork()
            {
                _inner.CloseForNewWork();
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                return _inner.StopAndConfirm(timeoutMilliseconds) && _held.Count == 0;
            }
        }

        /// <summary>
        /// What one frame's own state was, written down in that frame's late update -- after the scene's collection
        /// and before its picture is drawn. A coroutine cannot see it: it resumes between the updates and the late
        /// updates, so by the time it looks, the frame it wanted has moved on.
        /// <para>
        /// **This is what makes a state and a picture the same frame's.** A case reads these in the resume after the
        /// frame they were written in, which is the first moment that frame's picture exists, and checks the frame
        /// numbers say so.
        /// </para>
        /// <para>
        /// It only reads. Nothing of the world is driven from here.
        /// </para>
        /// </summary>
        [DefaultExecutionOrder(300)]
        private sealed class FrameWatcher : MonoBehaviour
        {
            internal CutWorldRoot world;
            internal LogicalFragmentId body;

            /// <summary>The cut to follow, once the case knows which it is. Unset until then.</summary>
            internal CutOperationId operation;

            /// <summary>The two children to follow, once they are published.</summary>
            internal LogicalFragmentId positive;

            internal LogicalFragmentId negative;

            internal int Frame { get; private set; } = -1;

            internal bool PairStood { get; private set; }

            internal int DrawnForBody { get; private set; }

            internal int DrawnForPositive { get; private set; }

            internal int DrawnForNegative { get; private set; }

            internal Vector3 PositiveAt { get; private set; }

            internal Vector3 NegativeAt { get; private set; }

            /// <summary>The stage the followed cut's geometry was at in this frame. Only read when <see
            /// cref="Following"/> says a cut was being followed.</summary>
            internal CutGeometryStage Stage { get; private set; }

            /// <summary>Whether a cut was named to follow when this frame was written down.</summary>
            internal bool Following { get; private set; }

            /// <summary>How many collections had settled by this frame: it grows in the frame one settles in.</summary>
            internal int SettledCollections { get; private set; }

            private void LateUpdate()
            {
                if (world == null || !world.IsReady || world.Display == null || world.Display.IsDisposed)
                {
                    return;
                }

                Frame = Time.frameCount;
                SettledCollections = world.Display.SettledCollections;
                Following = operation.IsSet;
                if (Following)
                {
                    Stage = world.Geometry.StageOf(operation);
                }
                PairStood = world.Owners.TryGetProvisionalOf(body, out ProvisionalOwnerPair pair) && !pair.IsEnded;
                if (PairStood)
                {
                    PositiveAt = pair.Positive.Root.transform.position;
                    NegativeAt = pair.Negative.Root.transform.position;
                }
                else if (positive.IsSet
                         && world.Owners.TryGet(positive, out PhysicsFragmentOwner positiveOwner)
                         && world.Owners.TryGet(negative, out PhysicsFragmentOwner negativeOwner))
                {
                    PositiveAt = positiveOwner.Root.transform.position;
                    NegativeAt = negativeOwner.Root.transform.position;
                }

                DrawnForBody = 0;
                DrawnForPositive = 0;
                DrawnForNegative = 0;
                for (int r = 0; r < world.Display.RenderFragmentCount; r++)
                {
                    if (!world.Display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf))
                    {
                        continue;
                    }

                    if (rf.root == body)
                    {
                        DrawnForBody++;
                    }
                    else if (positive.IsSet && rf.root == positive)
                    {
                        DrawnForPositive++;
                    }
                    else if (negative.IsSet && rf.root == negative)
                    {
                        DrawnForNegative++;
                    }
                }
            }
        }

        /// <summary>
        /// Loads the scene as it is on disk and takes hold of what it built. With <paramref name="holdCollection"/>
        /// every destination's collection is held from the moment the world is built, through the seam the root
        /// already has: the scene's own world runs, and only when a finished work is handed back is the case's.
        /// </summary>
        private IEnumerator LoadScene(bool holdCollection = false)
        {
            // The cut surface in the display's own debug colours, so that a cap and a provisional face can be told
            // apart from a side by the colour they are drawn in.
            _colours = VpCutSurfaceColour.Capture();
            _coloursTaken = true;
            VpCutSurfaceColour.SetDebugEnabled(true);

            if (holdCollection)
            {
                CutWorldRoot.nextWorldExecutors = destination =>
                {
                    IWorkExecutor inner = destination == WorkDestination.UnityJob
                        ? new UnityJobWorkExecutor(4)
                        : destination == WorkDestination.GeometryPool
                            ? WorkerPoolExecutor.GeometryPool(1)
                            : WorkerPoolExecutor.BackgroundPool(1);
                    var held = new HoldingExecutor(inner);
                    _holding.Add(held);
                    return held;
                };
            }

            SceneManager.LoadScene(ScenePath, LoadSceneMode.Single);
            yield return null;

            _world = UnityEngine.Object.FindFirstObjectByType<CutWorldRoot>();
            Assert.That(_world, Is.Not.Null, "the scene has a cut world");
            Assert.That(_world.IsReady, Is.True, "which built itself when the scene began");
            _probe = UnityEngine.Object.FindFirstObjectByType<SandboxCutWorldProbe>();
            Assert.That(_probe, Is.Not.Null, "and the body this scene is about");
            _camera = Camera.main;
            Assert.That(_camera, Is.Not.Null, "and a camera to draw with");

            // The camera draws into a texture this test can read. It is the scene's camera, its scene's arrangement
            // and the scene's drawing component; only where the result lands is the test's.
            _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                name = "Sandbox readback",
            };
            _target.Create();
            _camera.targetTexture = _target;
            _readback = new Texture2D(Width, Height, TextureFormat.RGBA32, false);

            // One frame with the body registered, so that a collection has settled before anything is asked for.
            yield return null;
            yield return null;
        }

        /// <summary>What the camera really drew this frame, read back from its colour target.</summary>
        private Color[] ReadPixels()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _target;
            _readback.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
            _readback.Apply(false);
            RenderTexture.active = previous;
            return _readback.GetPixels();
        }

        // ----- telling the target's own pixels from the scene's ----------------------------------------------------

        /// <summary>
        /// **The target is known by its own colour, not by being different from the background.** Everything in this
        /// scene that is not drawn by the display is grey: the floor is a grey lit surface and the camera clears to a
        /// near-black grey. The display's own materials are strongly coloured -- the sides blue, the ends orange --
        /// and the cut surface takes the display's debug colours, green for a cap it really has and red for the face
        /// of a provisional side. So a pixel of the target can be named, and counting pixels that merely differ from
        /// the clear colour -- which the floor alone satisfies -- is never done here.
        /// </summary>
        private static bool IsSide(Color pixel)
        {
            return pixel.b - pixel.r > 0.3f && pixel.b - pixel.g > 0.2f;
        }

        /// <summary>The end material: orange, and never confused with the provisional face, which has no green.</summary>
        private static bool IsEnd(Color pixel)
        {
            return pixel.r - pixel.b > 0.3f && pixel.g > 0.25f;
        }

        /// <summary>A cap the display really has, in the debug cut-surface colour.</summary>
        private static bool IsCap(Color pixel)
        {
            return pixel.g - pixel.r > 0.15f && pixel.g - pixel.b > 0.15f;
        }

        /// <summary>The face of a provisional side, in the debug provisional colour.</summary>
        private static bool IsProvisionalFace(Color pixel)
        {
            return pixel.r > 0.5f && pixel.g < 0.2f && pixel.b < 0.2f;
        }

        /// <summary>Any pixel the display drew: a side, an end or a cut surface of either kind.</summary>
        private static bool IsTarget(Color pixel)
        {
            return IsSide(pixel) || IsEnd(pixel) || IsCap(pixel) || IsProvisionalFace(pixel);
        }

        /// <summary>How many pixels of the whole image the display drew.</summary>
        private int TargetPixels()
        {
            return Count(ReadPixels(), IsTarget, new RectInt(0, 0, Width, Height));
        }

        /// <summary>How many pixels of a box on the screen the display drew.</summary>
        private int TargetPixelsIn(RectInt box)
        {
            return Count(ReadPixels(), IsTarget, box);
        }

        /// <summary>How many pixels of the whole image are of one kind.</summary>
        private int PixelsOf(Func<Color, bool> kind)
        {
            return Count(ReadPixels(), kind, new RectInt(0, 0, Width, Height));
        }

        /// <summary>How many pixels of a box are of one kind.</summary>
        private int PixelsOfIn(Func<Color, bool> kind, RectInt box)
        {
            return Count(ReadPixels(), kind, box);
        }

        private static int Count(Color[] pixels, Func<Color, bool> is_, RectInt box)
        {
            int found = 0;
            for (int y = Mathf.Max(0, box.yMin); y < Mathf.Min(Height, box.yMax); y++)
            {
                for (int x = Mathf.Max(0, box.xMin); x < Mathf.Min(Width, box.xMax); x++)
                {
                    if (is_(pixels[(y * Width) + x]))
                    {
                        found++;
                    }
                }
            }

            return found;
        }

        /// <summary>The screen box one world point falls in, with a margin around it.</summary>
        private RectInt BoxAround(Vector3 world, int margin)
        {
            Vector3 point = _camera.WorldToScreenPoint(world);
            var centre = new Vector2Int(Mathf.RoundToInt(point.x), Mathf.RoundToInt(point.y));
            return new RectInt(centre.x - margin, centre.y - margin, margin * 2, margin * 2);
        }

        private int DrawnFragmentsOf(LogicalFragmentId fragment)
        {
            int found = 0;
            for (int r = 0; r < _world.Display.RenderFragmentCount; r++)
            {
                Assert.That(_world.Display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                if (rf.root == fragment)
                {
                    found++;
                }
            }

            return found;
        }

        private IEnumerator Until(Func<bool> condition, string what)
        {
            float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, what + ": it had not happened within the deadline");
        }

        // ----- 1. what the scene shows before anything is cut ------------------------------------------------------

        /// <summary>
        /// **The body is really drawn, in its own colours, where its owner stands.** The pixels are read from the
        /// camera and counted by the display's own materials, so the floor -- which is grey, and lit -- cannot stand
        /// in for the body. Nothing is cut yet, so no cut surface of either kind is on the screen.
        /// </summary>
        [UnityTest]
        public IEnumerator TheScenePlayed_DrawsTheBodyInItsOwnColours_WhereItsOwnerStands()
        {
            yield return LoadScene();

            Assert.That(_probe.Body.IsSet, Is.True, "the body was registered with the world");
            Assert.That(DrawnFragmentsOf(_probe.Body), Is.EqualTo(1), "and the collection draws it as one body");

            int drawn = TargetPixels();
            Assert.That(
                drawn, Is.GreaterThan(800),
                "the camera really drew the body: " + drawn + " pixels are of the display's own materials");

            Vector3 at = _probe.Actor.transform.position;
            int onTheBody = TargetPixelsIn(BoxAround(at, 12));
            Assert.That(
                onTheBody, Is.GreaterThan(300),
                "the body is drawn where its own actor stands: " + onTheBody + " of its pixels are there");
            Assert.That(
                TargetPixelsIn(new RectInt(4, Height - 30, 24, 24)), Is.Zero,
                "and nothing of it is drawn where nothing stands");

            Assert.That(PixelsOf(IsCap), Is.Zero, "nothing is cut, so there is no cap on the screen");
            Assert.That(PixelsOf(IsProvisionalFace), Is.Zero, "and no provisional face either");
        }

        // ----- 2. the Provisional pair, drawn before the Final is done ----------------------------------------------

        /// <summary>
        /// **The cut is published and drawn as two sides without waiting for the Final.** The scene's own key is
        /// pressed, and in the frame that takes it up the pair stands in the scene, the body is not replaced yet, and
        /// the collection draws it as its two sides, each following its own actor.
        /// <para>
        /// **The state and the picture are of the same frame.** The frame's own state is written down in that frame's
        /// late update, by a watcher that runs after the scene's collection; the picture of that frame is read at the
        /// next resume, which is the first moment it exists. Both are checked to be that one frame's.
        /// </para>
        /// </summary>
        [UnityTest]
        public IEnumerator PressingTheCutKey_PublishesAndDrawsTwoSides_BeforeTheFinalIsDone()
        {
            yield return LoadScene();
            LogicalFragmentId body = _probe.Body;

            Assert.That(_probe.AskCut(body, new Vector4(0f, 1f, 0f, 0f)), Is.True, "the scene asked for a cut");
            yield return null;

            Assert.That(
                _world.Owners.ProvisionalPairCount, Is.EqualTo(1),
                "the pair is in the scene in the update the ask was taken up in");
            Assert.That(
                _world.Ledger.TryGetReplacingOperation(body, out CutOperationId _), Is.False,
                "and nothing has replaced the body yet: the Final is not waited for");

            var watcher = new GameObject("Frame watcher").AddComponent<FrameWatcher>();
            watcher.world = _world;
            watcher.body = body;

            Assert.That(
                _world.Owners.TryGetProvisionalOf(body, out ProvisionalOwnerPair pair) && !pair.IsEnded, Is.True,
                "the pair is what stands for the body");
            int watchedFrame = Time.frameCount;

            yield return null;

            Assert.That(watcher.Frame, Is.EqualTo(watchedFrame), "the watcher wrote down that very frame");
            Assert.That(
                Time.frameCount, Is.EqualTo(watchedFrame + 1),
                "and the picture read now is that frame's, the first moment it exists");
            Assert.That(watcher.PairStood, Is.True, "in which the pair was still what stood for the body");
            Assert.That(
                watcher.DrawnForBody, Is.EqualTo(2),
                "and the collection drew the body as its two sides, each at the actor it follows");

            int onThePair = TargetPixelsIn(BoxAround(watcher.NegativeAt, 14));
            Assert.That(
                onThePair, Is.GreaterThan(100),
                "the camera drew the cut body where its actors are: " + onThePair + " of its own pixels");
            Assert.That(
                TargetPixelsIn(new RectInt(4, Height - 30, 24, 24)), Is.Zero,
                "with nothing drawn where nothing stands");
        }

        // ----- 3. the handoff and the commit, seen from the screen ---------------------------------------------------

        /// <summary>
        /// **The picture survives the handoff and the geometry commit, and each state is checked against its own
        /// frame's picture.** The pair is taken hold of while the Final is held back, so which actors it is does not
        /// depend on how quickly the work finished. Then the hold is let go and every frame on the way is looked at
        /// twice: the state a watcher wrote down in that frame's late update, and the picture that frame drew, read
        /// at the next resume. The case goes on until it has seen a frame whose **collection settled after the
        /// commit**, and checks that frame's own picture.
        /// </summary>
        [UnityTest]
        public IEnumerator TheHandoffAndTheCommit_KeepThePictureOnTheSameActors()
        {
            yield return LoadScene(true);
            LogicalFragmentId body = _probe.Body;

            var watcher = new GameObject("Frame watcher").AddComponent<FrameWatcher>();
            watcher.world = _world;
            watcher.body = body;

            Assert.That(_probe.AskCut(body, new Vector4(0f, 1f, 0f, 0f)), Is.True);
            yield return null;

            // The Final is held, so the pair stands until this case lets it go: which actors it is does not depend
            // on the work having been slow enough to look at.
            Assert.That(_world.Owners.TryGetProvisionalOf(body, out ProvisionalOwnerPair pair), Is.True);
            Assert.That(pair.IsEnded, Is.False, "and it has not ended while the Final is held");
            GameObject positiveActor = pair.Positive.Root;
            GameObject negativeActor = pair.Negative.Root;

            foreach (HoldingExecutor held in _holding)
            {
                held.HoldEverything = false;
            }

            // Every frame from here: the state of the frame just gone, and that same frame's picture.
            int lowest = int.MaxValue;
            int commitFrame = -1;
            int settledAtCommit = -1;
            int checkedFrame = -1;
            float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                if (watcher.Frame < 0)
                {
                    continue;
                }

                Assert.That(
                    Time.frameCount, Is.EqualTo(watcher.Frame + 1),
                    "the picture read now is the picture of the frame the watcher wrote down");
                lowest = Mathf.Min(lowest, TargetPixels());

                if (!watcher.Following)
                {
                    // Name the cut to follow as soon as the ledger has one; the frames before that are the frames
                    // before there was anything to follow.
                    if (_world.Ledger.TryGetReplacingOperation(body, out CutOperationId found))
                    {
                        watcher.operation = found;
                    }

                    continue;
                }

                if (watcher.Stage != CutGeometryStage.Committed)
                {
                    continue;
                }

                if (commitFrame < 0)
                {
                    commitFrame = watcher.Frame;
                    settledAtCommit = watcher.SettledCollections;
                    Assert.That(_world.Ledger.TryGetOperation(watcher.operation, out LogicalCutOperation named), Is.True);
                    watcher.positive = named.positive;
                    watcher.negative = named.negative;
                    continue;
                }

                // A collection that settled after the commit: what is drawn now was collected from the committed
                // geometry, not from what stood before it.
                if (watcher.SettledCollections > settledAtCommit)
                {
                    checkedFrame = watcher.Frame;
                    break;
                }
            }

            Assert.That(commitFrame, Is.GreaterThan(0), "the cut committed while the scene played");
            Assert.That(
                checkedFrame, Is.GreaterThan(commitFrame),
                "and a collection settled after the commit, in a frame of its own");
            Assert.That(
                lowest, Is.GreaterThan(400),
                "the target was on the screen in every frame on the way: the fewest of its pixels in any of them was "
                + lowest);

            // This frame's own state, and this frame's own picture.
            Assert.That(watcher.Frame, Is.EqualTo(checkedFrame));
            Assert.That(Time.frameCount, Is.EqualTo(checkedFrame + 1));
            Assert.That(watcher.Stage, Is.EqualTo(CutGeometryStage.Committed), "in that frame the cut had committed");
            Assert.That(watcher.PairStood, Is.False, "the provisional pair no longer stood for the body");
            Assert.That(watcher.DrawnForBody, Is.Zero, "the body they replaced was not drawn in it");
            Assert.That(watcher.DrawnForPositive, Is.EqualTo(1), "and each child was drawn for itself");
            Assert.That(watcher.DrawnForNegative, Is.EqualTo(1));

            Assert.That(_world.Ledger.TryGetOperation(watcher.operation, out LogicalCutOperation record), Is.True);
            Assert.That(_world.Owners.TryGet(record.positive, out PhysicsFragmentOwner positive), Is.True);
            Assert.That(_world.Owners.TryGet(record.negative, out PhysicsFragmentOwner negative), Is.True);
            Assert.That(
                ReferenceEquals(positive.Root, positiveActor), Is.True,
                "the positive child is the very actor its provisional side was");
            Assert.That(ReferenceEquals(negative.Root, negativeActor), Is.True);

            // The picture of that frame, at the places that frame's state says the children were.
            int onPositive = TargetPixelsIn(BoxAround(watcher.PositiveAt, 10));
            int onNegative = TargetPixelsIn(BoxAround(watcher.NegativeAt, 10));
            Assert.That(onPositive, Is.GreaterThan(100), "the positive child is on the screen where it was: " + onPositive);
            Assert.That(onNegative, Is.GreaterThan(100), "and so is the negative: " + onNegative);
            Assert.That(
                TargetPixelsIn(new RectInt(4, Height - 30, 24, 24)), Is.Zero,
                "with nothing drawn where nothing stands");
        }

        // ----- 4. the cut face, once the children can be moved apart -------------------------------------------------

        /// <summary>
        /// **The cut face is shown on each child, and not outside it.** Once the geometry has committed, one child is
        /// carried away from the other -- the actors are moved, not the display -- so that each face is turned towards
        /// the camera. **Each child's own face is checked, and just outside each face is checked**: to the side of the
        /// body at the height of the face, on the child's far face, and in the gap the two left between them.
        /// </summary>
        [UnityTest]
        public IEnumerator LiftingOneChild_ShowsTheCutFaceOnBoth_AndNotOutsideThem()
        {
            yield return LoadScene();
            LogicalFragmentId body = _probe.Body;
            Assert.That(_probe.AskCut(body, new Vector4(0f, 1f, 0f, 0f)), Is.True);

            CutOperationId operation = default;
            yield return Until(
                () => _world.Ledger.TryGetReplacingOperation(body, out operation)
                      && _world.Geometry.StageOf(operation) == CutGeometryStage.Committed,
                "the cut finished");

            Assert.That(_world.Ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
            Assert.That(_world.Owners.TryGet(record.positive, out PhysicsFragmentOwner positive), Is.True);
            Assert.That(_world.Owners.TryGet(record.negative, out PhysicsFragmentOwner negative), Is.True);

            yield return CarryApart(positive.Root, negative.Root);
            AssertCutFaceIsOnEachSideOnly(IsCap, "the cap");
        }

        // ----- 5. the provisional face, with the Final held --------------------------------------------------------

        /// <summary>
        /// **A cut that is still provisional shows its face too, on each side and not outside it.** Every
        /// destination's collection is held, so the Final never comes back and the pair stands for as long as the
        /// case looks at it; the two provisional sides are carried apart and judged exactly as the committed children
        /// are. **The window is made to last by holding the product's own collection** -- no frame is invented and
        /// nothing of the display is driven by hand.
        /// </summary>
        [UnityTest]
        public IEnumerator HoldingTheFinal_KeepsTheProvisionalPair_AndShowsItsCutFace()
        {
            yield return LoadScene(true);
            LogicalFragmentId body = _probe.Body;
            Assert.That(_probe.AskCut(body, new Vector4(0f, 1f, 0f, 0f)), Is.True);
            yield return null;

            Assert.That(_world.Owners.ProvisionalPairCount, Is.EqualTo(1), "the pair stands");
            Assert.That(
                _world.Owners.TryGetProvisionalOf(body, out ProvisionalOwnerPair pair) && !pair.IsEnded, Is.True);

            yield return CarryApart(pair.Positive.Root, pair.Negative.Root);

            Assert.That(
                _world.Ledger.TryGetReplacingOperation(body, out CutOperationId _), Is.False,
                "the Final has not come back, so what is on the screen is still the provisional pair");
            Assert.That(
                _world.Owners.TryGetProvisionalOf(body, out ProvisionalOwnerPair still) && !still.IsEnded, Is.True,
                "and the pair is still what stands for the body");

            AssertCutFaceIsOnEachSideOnly(IsProvisionalFace, "the provisional face");

            foreach (HoldingExecutor held in _holding)
            {
                held.HoldEverything = false;
            }
        }

        // ----- what a parted pair must look like ---------------------------------------------------------------------

        /// <summary>The distance the upper side is carried, in metres.</summary>
        private const float CarryDistance = 1.1f;

        private Vector3 _upperFaceAt;
        private Vector3 _lowerFaceAt;
        private Vector3 _upperFarFaceAt;
        private Vector3 _lowerFarFaceAt;
        private Vector3 _gapAt;

        /// <summary>
        /// Stops both sides' physics and carries the upper one away, writing down where the two faces they were cut
        /// on now are: they touched at the midpoint between them, so that point is the lower side's face and that
        /// point carried up is the upper side's.
        /// </summary>
        private IEnumerator CarryApart(GameObject upper, GameObject lower)
        {
            Hold(upper);
            Hold(lower);
            Vector3 touchedAt = 0.5f * (upper.transform.position + lower.transform.position);
            var carry = new Vector3(0f, CarryDistance, 0f);
            upper.transform.position += carry;

            // Several frames, so that what is on the screen is collected from where they are now.
            for (int i = 0; i < 4; i++)
            {
                yield return null;
            }

            _lowerFaceAt = touchedAt;
            _upperFaceAt = touchedAt + carry;
            _gapAt = touchedAt + (0.5f * carry);

            // The far end of each side: the end it was **not** cut on. The body is a metre cube halved, so each
            // side's far end is half a metre from its own face -- measured from the face, not from where an actor's
            // origin happens to sit.
            var toFarEnd = new Vector3(0f, 0.45f, 0f);
            _upperFarFaceAt = _upperFaceAt + toFarEnd;
            _lowerFarFaceAt = _lowerFaceAt - toFarEnd;
        }

        /// <summary>
        /// **Each side's own face, and just outside each face.** The face is looked for in a small box on the face
        /// itself; it must not be found beside the body at that same height, on either side's far end, or in the gap
        /// the two left between them.
        /// </summary>
        private void AssertCutFaceIsOnEachSideOnly(Func<Color, bool> face, string what)
        {
            int onUpper = PixelsOfIn(face, BoxAround(_upperFaceAt, 7));
            int onLower = PixelsOfIn(face, BoxAround(_lowerFaceAt, 7));
            Assert.That(onUpper, Is.GreaterThan(20), what + " is on the side that was carried away: " + onUpper);
            Assert.That(onLower, Is.GreaterThan(20), what + " is on the side it left behind: " + onLower);

            var beside = new Vector3(0.8f, 0f, 0f);
            Assert.That(
                PixelsOfIn(face, BoxAround(_upperFaceAt + beside, 7)), Is.Zero,
                what + " is not beside the upper side, at the height of its own face");
            Assert.That(
                PixelsOfIn(face, BoxAround(_upperFaceAt - beside, 7)), Is.Zero,
                what + " is not on the other side of it either");
            Assert.That(
                PixelsOfIn(face, BoxAround(_lowerFaceAt + beside, 7)), Is.Zero,
                what + " is not beside the lower side, at the height of its own face");
            Assert.That(
                PixelsOfIn(face, BoxAround(_lowerFaceAt - beside, 7)), Is.Zero,
                what + " is not on the other side of it either");

            Assert.That(
                PixelsOfIn(face, BoxAround(_upperFarFaceAt, 7)), Is.Zero,
                what + " is not on the far end of the side that was carried away");
            Assert.That(
                PixelsOfIn(face, BoxAround(_lowerFarFaceAt, 7)), Is.Zero,
                what + " is not on the far end of the other side");

            Assert.That(
                TargetPixelsIn(BoxAround(_gapAt, 8)), Is.Zero,
                "and nothing at all is drawn in the gap they left: no leaked face, and no picture of the body that "
                + "was there");
        }

        // ----- 6. cutting a child from the scene's own entrance ------------------------------------------------------

        /// <summary>
        /// **A child can be cut from the scene, and the result reaches the screen.** The probe's second key asks for
        /// a cut of one child; its physics is published and its geometry committed while the scene plays, and the
        /// pieces are drawn in the display's own colours.
        /// </summary>
        [UnityTest]
        public IEnumerator PressingTheChildKey_CutsAChild_AndTheResultIsDrawn()
        {
            yield return LoadScene();
            LogicalFragmentId body = _probe.Body;
            Assert.That(_probe.AskCut(body, new Vector4(0f, 1f, 0f, 0f)), Is.True);

            CutOperationId first = default;
            yield return Until(
                () => _world.Ledger.TryGetReplacingOperation(body, out first)
                      && _world.Geometry.StageOf(first) == CutGeometryStage.Committed,
                "the first cut finished");

            Assert.That(_world.Ledger.TryGetOperation(first, out LogicalCutOperation firstRecord), Is.True);
            LogicalFragmentId child = firstRecord.positive;

            Assert.That(_probe.AskChildCut(), Is.True, "the scene asked for a cut of that child");
            yield return null;
            Assert.That(
                _world.Owners.ProvisionalPairCount, Is.EqualTo(1),
                "the child's own pair is in the scene in that update");

            CutOperationId second = default;
            yield return Until(
                () => _world.Ledger.TryGetReplacingOperation(child, out second)
                      && _world.Geometry.StageOf(second) == CutGeometryStage.Committed,
                "and the child's cut finished on both sides");

            Assert.That(_world.Ledger.TryGetOperation(second, out LogicalCutOperation secondRecord), Is.True);
            yield return null;
            Assert.That(
                DrawnFragmentsOf(secondRecord.positive), Is.EqualTo(1),
                "the child's own children are drawn for themselves");
            Assert.That(DrawnFragmentsOf(secondRecord.negative), Is.EqualTo(1));
            Assert.That(_world.GeometryFaults, Is.Zero, "and nothing failed");

            int drawn = TargetPixels();
            Assert.That(drawn, Is.GreaterThan(400), "the screen still shows the pieces: " + drawn + " of their pixels");
        }

        // ----- 7. ending the world from the scene --------------------------------------------------------------------

        /// <summary>
        /// **The scene's own ending closes the drawing and gives everything back.** The probe's ending key is pressed
        /// while a cut is still running; the ordinary frames finish the ending, nothing of the display is on the
        /// screen after it, and nothing that was given up is touched.
        /// </summary>
        [UnityTest]
        public IEnumerator EndingTheWorldFromTheScene_StopsTheDrawing_AndGivesEverythingBack()
        {
            yield return LoadScene();
            Assert.That(_probe.AskCut(_probe.Body, new Vector4(0f, 1f, 0f, 0f)), Is.True);
            yield return null;
            Assert.That(_world.Owners.ProvisionalPairCount, Is.EqualTo(1), "a cut is under way");

            _world.Shutdown();
            Assert.That(_world.IsReady, Is.False, "nothing is accepted any more");

            yield return Until(() => _world.IsReleased, "the ending finished on the ordinary frames");
            Assert.That(_world.IsDrained(), Is.True, "and nothing of it is out any more");

            yield return null;
            yield return null;
            Assert.That(_world.Display.IsDisposed, Is.True, "the display was given up");
            Assert.That(_world.IsReleased, Is.True);
            Assert.That(TargetPixels(), Is.Zero, "and nothing of it is on the screen any more");
        }

        /// <summary>Stops an actor's physics so that a case can put it where it wants to look at it.</summary>
        private static void Hold(GameObject actor)
        {
            var actorBody = actor.GetComponent<Rigidbody>();
            if (actorBody != null)
            {
                actorBody.isKinematic = true;
            }
        }
    }
}
