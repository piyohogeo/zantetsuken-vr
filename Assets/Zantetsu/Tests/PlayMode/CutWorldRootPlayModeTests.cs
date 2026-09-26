using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A cut world put together by the product's own composition root, carried by **Unity's own update loop**
    /// (DESIGN 4.5.6, 7.1, 7.2): a body is registered, a cut is asked for, and everything after that — the Provisional
    /// pair, the cook, the Final handoff, the display geometry commit, and a cut of one of the children — happens
    /// because the driver's <c>Update</c> and <c>LateUpdate</c> run, not because this test called them.
    /// <para>
    /// **Nothing here drives.** <c>Advance</c>, <c>DriveUpdate</c> and <c>DriveLateUpdate</c> are never called from a
    /// test; each case yields frames and watches. What a case does control, where the order has to be fixed, is when a
    /// finished work is handed back -- at the destination, which is a joint the product already has -- and never what
    /// the root or the driver do with it.
    /// </para>
    /// <para>
    /// **Not here**: hit detection, the impulse of a cut (the two values a case gives are a test's input and not a
    /// product value), the building constraint, Character, XR, drawing and any measurement. The simulation runs as
    /// Unity runs it; nothing is stepped by hand.
    /// </para>
    /// </summary>
    public unsafe class CutWorldRootPlayModeTests
    {
        private const double ParentMass = 12.0;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const float DeadlineSeconds = 60f;

        // ----- what a case makes, held from the moment it is made ------------------------------------------------------

        // The lists are the fixture's own and are **not** exchanged when something is held back: a case that cannot
        // end or collect stops the cases after it, so nothing else will read them, and an exchange in the middle of an
        // interrupted teardown is exactly how a later step came to work on an empty list.
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();

        /// <summary>Every actor a case made, and the ones the world took. A registered actor is the world's to destroy.</summary>
        private readonly List<GameObject> _actors = new List<GameObject>();

        private readonly List<GameObject> _registered = new List<GameObject>();

        /// <summary>Shapes a case made. A shape the world took is disposed by the world too; a second Dispose does nothing.</summary>
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        /// <summary>
        /// Destinations a case is holding the collection of, tracked where they are made. The fixture lets go of them
        /// **however the case ends**, so an assertion that throws does not leave one holding.
        /// </summary>
        private readonly List<HoldingExecutor> _holding = new List<HoldingExecutor>();

        private CutWorldProfile _profile;

        /// <summary>The root, held from the moment its component exists -- before it is activated and builds.</summary>
        private CutWorldRoot _root;

        /// <summary>Whether a root was ever made by this case. A case that makes none has nothing to end.</summary>
        private bool _rootMade;

        /// <summary>Whether this case means to raise the Player's termination (DESIGN 4). Said by the case itself.</summary>
        private bool _terminationExpected;

        /// <summary>The world's ending, as far as this fixture saw it.</summary>
        private enum WorldEnding
        {
            NotStarted = 0,

            /// <summary>No world was made: there is nothing to end, and nothing of a world to keep.</summary>
            NoWorld = 1,

            /// <summary><c>IsReleased</c> was seen within the deadline.</summary>
            Confirmed = 2,

            /// <summary>Not seen: a deadline passed, the request threw, the root went missing, or a wait was left.</summary>
            NotConfirmed = 3,

            /// <summary>The latch was fixed: DESIGN 4 promises no collection and no release, and none is added here.</summary>
            TerminatedByContract = 4,
        }

        /// <summary>The fixture's own collection, which is a separate thing from the world's ending.</summary>
        private enum Collection
        {
            NotStarted = 0,
            Done = 1,
            HeldBack = 2,
        }

        private WorldEnding _ending;
        private Collection _collection;

        /// <summary>
        /// Whether the ending and the collection have been entered, and whether either was **left** before it
        /// concluded. A leaving is final: neither a second call nor a resumption of the iterator that was left may
        /// start the ending again, touch a resource, or make a state say that something succeeded.
        /// </summary>
        private bool _endingEntered;
        private bool _collectionEntered;
        private bool _abandoned;

        private Func<bool> _releaseProbe;

        /// <summary>The one deadline of the ending's wait. A guard case may shorten it to observe a timeout.</summary>
        private float _endingDeadlineSeconds = DeadlineSeconds;

        private readonly List<string> _report = new List<string>();

        /// <summary>
        /// What a case could not end or collect: the reasons, and the fact that everything the case made is still
        /// held by the fields above. The cases after it are not started, so nothing moves out of those fields.
        /// </summary>
        private sealed class HeldBack
        {
            public HeldBack(string test, IReadOnlyList<string> reasons)
            {
                Test = test;
                Reasons = reasons;
            }

            public string Test { get; }

            public IReadOnlyList<string> Reasons { get; }
        }

        private readonly List<HeldBack> _heldBack = new List<HeldBack>();

        /// <summary>
        /// Once a case's world could not be ended or its resources given back, that world may still be live in the
        /// scene, and keeping a reference does not stop the scene from being unloaded. The cases after it in this
        /// fixture are not started: what they would see is not a fresh scene, and nothing here can make it one.
        /// </summary>
        private bool _laterCasesBlocked;

        [SetUp]
        public void Fresh()
        {
            if (_laterCasesBlocked)
            {
                Assert.Inconclusive(
                    "an earlier case of this fixture could not end or collect its world, which may still be live; "
                    + "this case is not started (" + _heldBack.Count + " held back)");
            }

            // Nothing is cleared here: a case that ran to its end left these empty, and a case that did not stops
            // every case after it, so an empty check is what the assertions below are for.
            Assert.That(_objects, Is.Empty, "the fixture starts with nothing of an earlier case");
            Assert.That(_actors, Is.Empty);
            Assert.That(_disposables, Is.Empty);
            Assert.That(_holding, Is.Empty);
            Assert.That(_profile == null, Is.True);
            _registered.Clear();
            _root = null;
            _rootMade = false;
            _terminationExpected = false;
            _ending = WorldEnding.NotStarted;
            _collection = Collection.NotStarted;
            _endingEntered = false;
            _collectionEntered = false;
            _abandoned = false;
            _releaseProbe = null;
            _endingDeadlineSeconds = DeadlineSeconds;
            _report.Clear();
        }

        private T Track<T>(T tracked)
            where T : UnityEngine.Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        /// <summary>An actor, tracked as it is made and before anything can go wrong with it.</summary>
        private GameObject TrackActor(GameObject actor)
        {
            _actors.Add(actor);
            return actor;
        }

        /// <summary>A held destination, tracked where it is made -- inside the world's build, before it returns.</summary>
        private HoldingExecutor Held(HoldingExecutor held)
        {
            _holding.Add(held);
            return held;
        }

        /// <summary>Said by a case that means to raise the Player's termination, before it does anything.</summary>
        private void ExpectTermination()
        {
            _terminationExpected = true;
        }

        // ----- the ending: asked for once, carried by the ordinary frames ----------------------------------------------

        /// <summary>
        /// The world's ending, once, however it is reached -- from the case's own <see cref="EndWorld"/> or from the
        /// teardown. The holds are let go; then: a case that made no world has nothing to end; a root that was made
        /// and is now missing is not confirmed; a fixed latch is left as DESIGN 4 leaves it; a released world is
        /// confirmed; an ending already going on is waited for and not asked for again; otherwise the ending is asked
        /// for once. The wait is on the ordinary frames -- nothing is pumped, no frame id is made up, nothing is
        /// completed by force -- to one deadline, and a wait left before it concluded is not a confirmation.
        /// </summary>
        private IEnumerator EndWorldOnce()
        {
            if (_endingEntered || _abandoned)
            {
                yield break;
            }

            _endingEntered = true;
            foreach (HoldingExecutor held in _holding)
            {
                held.ReleaseEverything();
            }

            if (_releaseProbe == null)
            {
                if (!_rootMade)
                {
                    _ending = WorldEnding.NoWorld;
                    yield break;
                }

                if (_root == null)
                {
                    _ending = WorldEnding.NotConfirmed;
                    _report.Add("ending: the root this case made is missing, so its ending cannot be confirmed");
                    yield break;
                }

                if (_root.TerminationRequested)
                {
                    _ending = WorldEnding.TerminatedByContract;
                    if (!_terminationExpected)
                    {
                        _report.Add("ending: the Player's termination was requested, which this case did not mean");
                    }

                    yield break;
                }

                if (_root.IsReleased)
                {
                    _ending = WorldEnding.Confirmed;
                    yield break;
                }

                if (!_root.IsEnding && !TryRequestEnding())
                {
                    _ending = WorldEnding.NotConfirmed;
                    yield break;
                }
            }

            bool concluded = false;
            try
            {
                float deadline = Time.realtimeSinceStartup + _endingDeadlineSeconds;
                while (!Released() && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    if (_abandoned)
                    {
                        // Resumed after this teardown was left: nothing is concluded from here.
                        yield break;
                    }
                }

                if (Released())
                {
                    _ending = WorldEnding.Confirmed;
                }
                else
                {
                    _ending = WorldEnding.NotConfirmed;
                    _report.Add("ending: the world was not released within " + _endingDeadlineSeconds + " s"
                                + (_root != null ? " (IsEnding=" + _root.IsEnding + ", IsDrained=" + _root.IsDrained() + ")" : ""));
                }

                concluded = true;
            }
            finally
            {
                if (!concluded)
                {
                    Abandon("ending: the wait for the world's release was left before it concluded");
                }
            }
        }

        private bool Released()
        {
            return _releaseProbe != null ? _releaseProbe() : _root != null && _root.IsReleased;
        }

        private bool TryRequestEnding()
        {
            try
            {
                _root.Shutdown();
                return true;
            }
            catch (Exception failure)
            {
                _report.Add("ending: Shutdown threw: " + failure);
                return false;
            }
        }

        /// <summary>
        /// Said once, when the ending or the collection was left before it concluded: from here nothing of this case
        /// is ended, destroyed or declared done, whether by a second call or by an iterator resuming.
        /// </summary>
        private void Abandon(string reason)
        {
            if (_abandoned)
            {
                return;
            }

            _abandoned = true;
            if (_ending == WorldEnding.NotStarted)
            {
                _ending = WorldEnding.NotConfirmed;
            }

            HoldBack(reason);
        }

        // ----- the collection: the fixture's own, after the world's ending -----------------------------------------------

        /// <summary>
        /// Gives the fixture's own resources back, once, and only where their use has really ended: after a confirmed
        /// ending, or where no world was ever made. First the actors the world never took -- their colliders point at
        /// the fixture's meshes -- then a frame for the destroys the world deferred, then every actor is confirmed
        /// gone, registered or not; a registered actor still there is never destroyed by force. Only then the shapes,
        /// the objects (meshes among them) and the profile, one list at a time; the first failure stops it and what
        /// remains is held.
        /// <para>
        /// After a termination (DESIGN 4) **nothing is given back**: the contract promises no collection, so whether
        /// a worker is still reading the body's meshes, or a shape's bank, is not something this fixture can know.
        /// The case keeps everything it made, and the cases after it are not started.
        /// </para>
        /// </summary>
        private IEnumerator CollectOnce()
        {
            if (_collectionEntered || _abandoned || _collection != Collection.NotStarted)
            {
                yield break;
            }

            _collectionEntered = true;
            if (_ending == WorldEnding.TerminatedByContract)
            {
                HoldBack(
                    _terminationExpected
                        ? "collection: the Player's termination was requested as this case meant, and DESIGN 4 promises "
                          + "no collection: everything this case made is kept, its use never having been confirmed over"
                        : "collection: nothing is collected after a termination this case did not mean");
                yield break;
            }

            if (_ending != WorldEnding.Confirmed && _ending != WorldEnding.NoWorld)
            {
                HoldBack("collection: the world's ending was not confirmed, so nothing of this case was given back");
                yield break;
            }

            for (int i = 0; i < _actors.Count; i++)
            {
                GameObject actor = _actors[i];
                if (actor == null || _registered.Contains(actor))
                {
                    continue;
                }

                if (!TryDestroyNow(actor, "an unregistered actor"))
                {
                    HoldBack("collection: destroying an unregistered actor threw, so the meshes it points at were kept");
                    yield break;
                }
            }

            if (_actors.Count > 0)
            {
                // The frame the world's deferred destroys take effect in.
                yield return null;
                if (_abandoned)
                {
                    yield break;
                }
            }

            for (int i = 0; i < _actors.Count; i++)
            {
                if (_actors[i] != null)
                {
                    HoldBack("collection: an actor is still alive after the ending (registered="
                             + _registered.Contains(_actors[i]) + "), so the meshes its colliders point at were kept");
                    yield break;
                }
            }

            _actors.Clear();
            if (!DisposeEach(_disposables, "shapes") || !DestroyEach(_objects, "objects") || !DestroyProfile())
            {
                HoldBack("collection: giving the fixture's resources back stopped at the first failure; the rest were kept");
                yield break;
            }

            // Everything this case made has gone back, so the destinations it held are of no more use to anyone: the
            // references go too, and the next case starts with a fixture that holds nothing. A hold-back keeps them,
            // which is what its own reading of them is for.
            _registered.Clear();
            _holding.Clear();
            _collection = Collection.Done;
        }

        private bool TryDestroyNow(UnityEngine.Object target, string what)
        {
            try
            {
                UnityEngine.Object.DestroyImmediate(target);
                return true;
            }
            catch (Exception failure)
            {
                _report.Add("collection: destroying " + what + " threw: " + failure);
                return false;
            }
        }

        /// <summary>
        /// Disposes in order, removing each as it goes. At the first failure the one that threw and everything after
        /// it stay in the list, and false is returned -- and no later list is touched, because the caller stops.
        /// </summary>
        private bool DisposeEach(List<IDisposable> list, string what)
        {
            while (list.Count > 0)
            {
                try
                {
                    list[0].Dispose();
                }
                catch (Exception failure)
                {
                    _report.Add("collection: " + what + ": Dispose threw; it and " + (list.Count - 1)
                                + " after it are kept, and no later list was touched: " + failure);
                    return false;
                }

                list.RemoveAt(0);
            }

            return true;
        }

        private bool DestroyEach<T>(List<T> list, string what)
            where T : UnityEngine.Object
        {
            while (list.Count > 0)
            {
                T item = list[0];
                if (item != null && !TryDestroyNow(item, what))
                {
                    _report.Add("collection: " + what + ": " + list.Count
                                + " entries are kept from the failure on, and no later list was touched");
                    return false;
                }

                list.RemoveAt(0);
            }

            return true;
        }

        private bool DestroyProfile()
        {
            if (_profile == null)
            {
                return true;
            }

            if (!TryDestroyNow(_profile, "the profile"))
            {
                return false;
            }

            _profile = null;
            return true;
        }

        /// <summary>
        /// What this case could not end or collect. The fields keep everything; this records why, and stops the cases
        /// after it. Said once per teardown.
        /// </summary>
        private void HoldBack(string reason)
        {
            _report.Add(reason);
            if (_collection == Collection.HeldBack)
            {
                return;
            }

            _collection = Collection.HeldBack;
            _laterCasesBlocked = true;
            _heldBack.Add(new HeldBack(TestContext.CurrentContext.Test.FullName, new List<string>(_report)));
        }

        /// <summary>
        /// The teardown: the ending if the case did not get to it, the collection, and the report. Whatever is left
        /// mid-way leaves its states behind -- read from the states, not from the inner iterators' own finally blocks
        /// having run -- and from then on nothing of this case is ended, destroyed or declared done.
        /// </summary>
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            try
            {
                yield return EndWorldOnce();
                yield return CollectOnce();
            }
            finally
            {
                if (_ending == WorldEnding.NotStarted)
                {
                    Abandon("teardown: the ending had not concluded when the teardown was left");
                }
                else if (_collection == Collection.NotStarted)
                {
                    Abandon("teardown: left before the collection concluded; nothing of this case was given back");
                }

                WriteClosingLine();
            }

            Report();
        }

        private void WriteClosingLine()
        {
            string line;
            try
            {
                line = "CutWorldRootFixture ending: world=" + _ending + " collection=" + _collection
                       + " terminationExpected=" + _terminationExpected + " abandoned=" + _abandoned
                       + " heldBack=" + _heldBack.Count + " kept=[objects " + _objects.Count + ", actors "
                       + _actors.Count + ", shapes " + _disposables.Count + ", holding " + _holding.Count
                       + ", profile " + (_profile != null) + "] report=[" + string.Join(" | ", _report) + "]";
            }
            catch (Exception describing)
            {
                line = "CutWorldRootFixture ending: (describing it threw: " + describing.GetType().Name + ")";
            }

            try
            {
                UnityEngine.Debug.Log(line);
            }
            catch (Exception)
            {
                // Not written, and not recorded: this is the end of it.
            }
        }

        /// <summary>
        /// Fails a case that had passed when its world was not ended or its resources not given back. **A termination
        /// the case meant is the one ending that keeps everything**: DESIGN 4 promises no collection, so a hold-back
        /// is what this fixture must do, and it is not a failure -- as long as nothing was left mid-way. Everything
        /// else that did not end and collect is a failure of a case that had passed: an unexpected termination, a
        /// timeout, a root that went missing, an interrupted teardown, a disposal that threw.
        /// </summary>
        private void Report()
        {
            bool endedAndCollected = (_ending == WorldEnding.Confirmed || _ending == WorldEnding.NoWorld)
                                     && _collection == Collection.Done;
            bool terminatedAsMeant = _ending == WorldEnding.TerminatedByContract && _terminationExpected
                                     && !_abandoned && _collection == Collection.HeldBack;
            if (endedAndCollected || terminatedAsMeant)
            {
                return;
            }

            string text = string.Join("\n", _report);
            if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed)
            {
                Assert.Fail("the world's ending or the fixture's collection was not confirmed:\n" + text);
            }

            TestContext.WriteLine("fixture ending (beside the case's own failure): " + text);
        }

        // ----- for the guard cases only: the few operations and readings they need -------------------------------------

        internal void HoldRootForTest(CutWorldRoot root)
        {
            _root = root;
            _rootMade = true;
        }

        internal void TrackActorForTest(GameObject actor, bool registered)
        {
            _actors.Add(actor);
            if (registered)
            {
                _registered.Add(actor);
            }
        }

        internal void TrackObjectForTest(UnityEngine.Object tracked)
        {
            _objects.Add(tracked);
        }

        internal void TrackShapeForTest(IDisposable shape)
        {
            _disposables.Add(shape);
        }

        internal void AddHoldingForTest()
        {
            _holding.Add(new HoldingExecutor(null));
        }

        internal void ExpectTerminationForTest()
        {
            _terminationExpected = true;
        }

        /// <summary>Stands in the world's release for the ending's wait; the ending is not requested of any root.</summary>
        internal void UseReleaseProbeForTest(Func<bool> released)
        {
            _releaseProbe = released;
            _rootMade = true;
        }

        /// <summary>Shortens the ending's one deadline, so that a timeout can be observed without waiting a minute.</summary>
        internal void UseEndingDeadlineForTest(float seconds)
        {
            _endingDeadlineSeconds = seconds;
        }

        internal IEnumerator EndWorldOnceForTest()
        {
            return EndWorldOnce();
        }

        internal IEnumerator CollectOnceForTest()
        {
            return CollectOnce();
        }

        internal bool EndingIsNotStarted => _ending == WorldEnding.NotStarted;

        internal bool EndingIsNoWorld => _ending == WorldEnding.NoWorld;

        internal bool EndingIsConfirmed => _ending == WorldEnding.Confirmed;

        internal bool EndingIsNotConfirmed => _ending == WorldEnding.NotConfirmed;

        internal bool EndingIsTerminatedByContract => _ending == WorldEnding.TerminatedByContract;

        internal bool WasAbandoned => _abandoned;

        internal bool CollectionWasEntered => _collectionEntered;

        internal bool CollectionIsNotStarted => _collection == Collection.NotStarted;

        internal bool CollectionIsDone => _collection == Collection.Done;

        internal bool CollectionIsHeldBack => _collection == Collection.HeldBack;

        internal int KeptObjectCount => _objects.Count;

        internal int KeptActorCount => _actors.Count;

        internal int KeptShapeCount => _disposables.Count;

        internal int HoldingCount => _holding.Count;

        internal bool KeptProfile => _profile != null;

        internal int HeldBackCount => _heldBack.Count;

        internal bool LaterCasesBlocked => _laterCasesBlocked;

        internal IReadOnlyList<string> ReportForTest => _report;

        // ----- a destination whose collection a case controls -----------------------------------------------------------

        /// <summary>
        /// One of the product's destinations, wrapped: the work runs as usual and is handed back only once the case
        /// lets it. It holds the **collection**, never the running, and it is the seam the product already has.
        /// </summary>
        internal sealed class HoldingExecutor : IWorkExecutor
        {
            private readonly IWorkExecutor _inner;

            // **Each work with the completion its destination gave it.** What is changed here is when the collection
            // sees a work, never what it ended as: a failure or a cancellation that goes through the hold comes out
            // of it as a failure or a cancellation.
            private readonly List<(IDispatchWork work, WorkCompletion completion)> _held =
                new List<(IDispatchWork, WorkCompletion)>();

            private readonly List<(IDispatchWork work, WorkCompletion completion)> _letThrough =
                new List<(IDispatchWork, WorkCompletion)>();

            internal HoldingExecutor(IWorkExecutor inner)
            {
                _inner = inner;
            }

            internal bool HoldEverything { get; set; }

            /// <summary>
            /// Whether work that finishes from now on is handed on as it comes, while what is already held stays held:
            /// how one request's collection is kept back while another's goes through.
            /// </summary>
            internal bool LetNewWorkThrough { get; set; }

            /// <summary>How many it is holding back from the collection now.</summary>
            internal int HoldingCount => _held.Count;

            /// <summary>One of the works it is holding back, oldest first.</summary>
            internal IDispatchWork HeldWorkAt(int index)
            {
                return _held[index].work;
            }

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
                // One that the case let through by itself, while the rest stay held. Given back as the destination
                // returned it, work and completion together.
                if (_letThrough.Count > 0)
                {
                    (work, completion) = _letThrough[0];
                    _letThrough.RemoveAt(0);
                    return true;
                }

                // What was held back is handed on first, once the case has let it go -- again as it was taken.
                if (!HoldEverything && _held.Count > 0)
                {
                    (work, completion) = _held[0];
                    _held.RemoveAt(0);
                    return true;
                }

                if (!_inner.TryTakeFinished(out work, out completion))
                {
                    return false;
                }

                if (!HoldEverything || LetNewWorkThrough)
                {
                    return true;
                }

                // Finished and taken from the destination, but not handed on: the case decides when the collection
                // sees it. The work itself ran to the end -- what is held is its collection, with the completion it
                // came back with.
                _held.Add((work, completion));
                work = null;
                completion = default;
                return false;
            }

            /// <summary>
            /// Makes what is held collectable. It says **when**, and nothing else: no completion is rewritten here.
            /// </summary>
            internal void ReleaseEverything()
            {
                HoldEverything = false;
            }

            /// <summary>
            /// Lets the oldest held work through, leaving the rest held. A request goes through several stages at one
            /// destination, and holding every one of them stops it at the first: this is how a case carries one to the
            /// stage it wants and holds it there.
            /// </summary>
            internal bool TryLetOneThrough()
            {
                if (_held.Count == 0)
                {
                    return false;
                }

                _letThrough.Add(_held[0]);
                _held.RemoveAt(0);
                return true;
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
        /// A destination that ends work exactly as a case says, so that what the hold does to a completion can be
        /// asked of the wrapper itself rather than inferred from a cut.
        /// </summary>
        private sealed class ScriptedExecutor : IWorkExecutor
        {
            private readonly Queue<(IDispatchWork work, WorkCompletion completion)> _finished =
                new Queue<(IDispatchWork, WorkCompletion)>();

            public WorkDestination Destination => WorkDestination.UnityJob;

            public int Capacity => 8;

            public int Held => _finished.Count;

            public bool CanAccept => true;

            /// <summary>Says that this work has ended, and how.</summary>
            internal void Ended(IDispatchWork work, WorkCompletion completion)
            {
                _finished.Enqueue((work, completion));
            }

            public bool TryAccept(IDispatchWork work)
            {
                return true;
            }

            public void BeginAccepted(IDispatchWork work)
            {
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                if (_finished.Count == 0)
                {
                    work = null;
                    completion = default;
                    return false;
                }

                (work, completion) = _finished.Dequeue();
                return true;
            }

            public void CloseForNewWork()
            {
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                return _finished.Count == 0;
            }
        }

        /// <summary>A piece of work that does nothing and can be told apart from the others by name.</summary>
        private sealed class NamedWork : IDispatchWork
        {
            internal NamedWork(string name)
            {
                Name = name;
            }

            internal string Name { get; }

            public bool IsComplete => true;

            public void Begin()
            {
            }

            public void Collect(WorkCompletion completion)
            {
            }

            public override string ToString()
            {
                return Name;
            }
        }

        /// <summary>
        /// **The hold changes when the collection sees a work, and nothing about how it ended.** Three works end at
        /// the destination as finished, failed and cancelled while the hold is on; each comes out of the hold as what
        /// it was, the failure with its own exception, and **no work comes back twice**.
        /// </summary>
        [Test]
        public void TheHoldGivesBackTheCompletionItWasGiven_AndEachWorkOnlyOnce()
        {
            var inner = new ScriptedExecutor();
            var holding = new HoldingExecutor(inner) { HoldEverything = true };
            var finished = new NamedWork("finished");
            var failed = new NamedWork("failed");
            var cancelled = new NamedWork("cancelled");
            var failure = new InvalidOperationException("what the work threw");
            inner.Ended(finished, WorkCompletion.Finished);
            inner.Ended(failed, WorkCompletion.Failed(failure));
            inner.Ended(cancelled, WorkCompletion.Cancelled);

            // Held: the collection is given nothing at all, not an empty-handed success.
            for (int i = 0; i < 3; i++)
            {
                Assert.That(
                    holding.TryTakeFinished(out IDispatchWork none, out WorkCompletion _), Is.False,
                    "while the hold is on, the collection is handed nothing");
                Assert.That(none, Is.Null, "and nothing is named as taken");
            }

            Assert.That(holding.HoldingCount, Is.EqualTo(3), "all three are held back from the collection");

            holding.ReleaseEverything();

            var taken = new List<IDispatchWork>();
            Assert.That(holding.TryTakeFinished(out IDispatchWork first, out WorkCompletion firstEnd), Is.True);
            taken.Add(first);
            Assert.That(first, Is.SameAs(finished), "the works come back in the order the destination ended them");
            Assert.That(firstEnd.outcome, Is.EqualTo(WorkOutcome.Finished), "and the first one ran to the end");

            Assert.That(holding.TryTakeFinished(out IDispatchWork second, out WorkCompletion secondEnd), Is.True);
            taken.Add(second);
            Assert.That(second, Is.SameAs(failed));
            Assert.That(
                secondEnd.outcome, Is.EqualTo(WorkOutcome.Failed),
                "**a failure is still a failure after the hold**, not a success");
            Assert.That(secondEnd.failure, Is.SameAs(failure), "and it carries the same exception");

            Assert.That(holding.TryTakeFinished(out IDispatchWork third, out WorkCompletion thirdEnd), Is.True);
            taken.Add(third);
            Assert.That(third, Is.SameAs(cancelled));
            Assert.That(
                thirdEnd.outcome, Is.EqualTo(WorkOutcome.Cancelled),
                "**a cancellation is still a cancellation after the hold**");

            Assert.That(taken, Is.Unique, "no work is handed to the collection twice");
            Assert.That(
                holding.TryTakeFinished(out IDispatchWork more, out WorkCompletion _), Is.False,
                "and once each has been handed on, there is nothing left to hand on");
            Assert.That(more, Is.Null);
            Assert.That(holding.HoldingCount, Is.Zero, "nothing is still held");
        }

        /// <summary>
        /// The same of letting **one** through while the rest stay held: the one let through keeps what it ended as,
        /// and the others are still held.
        /// </summary>
        [Test]
        public void LettingOneThrough_KeepsWhatThatWorkEndedAs()
        {
            var inner = new ScriptedExecutor();
            var holding = new HoldingExecutor(inner) { HoldEverything = true };
            var cancelled = new NamedWork("cancelled");
            var stillHeld = new NamedWork("still held");
            inner.Ended(cancelled, WorkCompletion.Cancelled);
            inner.Ended(stillHeld, WorkCompletion.Finished);
            Assert.That(holding.TryTakeFinished(out IDispatchWork _, out WorkCompletion _), Is.False);
            Assert.That(holding.TryTakeFinished(out IDispatchWork _, out WorkCompletion _), Is.False);
            Assert.That(holding.HoldingCount, Is.EqualTo(2));

            Assert.That(holding.TryLetOneThrough(), Is.True, "the oldest of the two is let through");
            Assert.That(holding.HoldingCount, Is.EqualTo(1), "the other stays held");

            Assert.That(holding.TryTakeFinished(out IDispatchWork through, out WorkCompletion end), Is.True);
            Assert.That(through, Is.SameAs(cancelled));
            Assert.That(
                end.outcome, Is.EqualTo(WorkOutcome.Cancelled),
                "what was let through ended as the destination said it did");
            Assert.That(
                holding.TryTakeFinished(out IDispatchWork _, out WorkCompletion _), Is.False,
                "and the hold is still on for the rest");
        }

        // ----- the world, built by the product's own root ---------------------------------------------------------------

        /// <summary>
        /// The profile a case runs with. It is the product's own type with its own defaults; only the two epsilons and
        /// the sizes a case needs are named here, and what is not named is the profile's default.
        /// </summary>
        private CutWorldProfile NewProfile()
        {
            _profile = ScriptableObject.CreateInstance<CutWorldProfile>();
            return _profile;
        }

        private CutWorldRoot NewWorld(out Shader shader)
        {
            return NewWorld(out shader, null, null);
        }

        private CutWorldRoot NewWorld(
            out Shader shader, Func<WorkDestination, IWorkExecutor> executors, Action terminatePlayer)
        {
            shader = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
            Assert.That(shader, Is.Not.Null, "the display's shader is in this project");

            // Inactive first: a component added to an active object runs its Awake at once, and this one builds the
            // world there -- so the profile and the materials have to be on it before that.
            var rootObject = Track(new GameObject("Cut World"));
            rootObject.SetActive(false);
            CutWorldRoot root = rootObject.AddComponent<CutWorldRoot>();
            _root = root;
            _rootMade = true;
            var profile = NewProfile();
            var materials = new[]
            {
                new CutWorldRoot.MaterialBinding
                {
                    sourceIndex = SideMaterial,
                    material = Track(new Material(shader) { name = "side" }),
                },
                new CutWorldRoot.MaterialBinding
                {
                    sourceIndex = EndMaterial,
                    material = Track(new Material(shader) { name = "end" }),
                },
            };

            // The two things a scene would carry on the component, given here because there is no scene asset.
            SetPrivate(root, "profile", profile);
            SetPrivate(root, "materials", materials);
            if (executors != null)
            {
                SetPrivate(root, "executors", executors);
            }

            if (terminatePlayer != null)
            {
                SetPrivate(root, "terminatePlayer", terminatePlayer);
            }

            // And now Awake runs, with everything it needs already there.
            rootObject.SetActive(true);
            Assert.That(root.IsReady, Is.True, "the root built the world in Awake");
            return root;
        }

        /// <summary>
        /// Reads what one frame's collection settled, **in that frame**: a coroutine that yields resumes after the
        /// updates and before the late updates, so what it sees is the frame before's. This runs after the driver's
        /// own late update -- by its execution order -- and writes down what was settled and when.
        /// <para>
        /// It drives nothing: it calls nothing of the driver or the root, and only reads.
        /// </para>
        /// </summary>
        [DefaultExecutionOrder(1000)]
        private sealed class LateFrameObserver : MonoBehaviour
        {
            internal CutWorldRoot world;
            internal LogicalFragmentId watched;

            /// <summary>The frame the last reading is of.</summary>
            internal int Frame { get; private set; } = -1;

            /// <summary>How many render fragments were drawn for the watched branch in that frame.</summary>
            internal int Drawn { get; private set; }

            /// <summary>How many Provisional pairs stood in the scene in that frame.</summary>
            internal int Pairs { get; private set; }

            private void LateUpdate()
            {
                if (world == null || !world.IsReady)
                {
                    return;
                }

                Frame = Time.frameCount;
                Drawn = DrawnFragmentsOf(world, watched);
                Pairs = world.Owners.ProvisionalPairCount;
            }
        }

        private static void SetPrivate(object target, string field, object value)
        {
            System.Reflection.FieldInfo info = target.GetType().GetField(
                field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, "the root has a " + field + " field");
            info.SetValue(target, value);
        }

        // ----- one authored body: a physics shape and a display geometry of the same box -------------------------------

        private static readonly float3[] k_corners =
        {
            new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, -1f, 1f), new float3(-1f, -1f, 1f),
            new float3(-1f, 1f, -1f), new float3(1f, 1f, -1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
        };

        private static readonly (int[] cycle, int submesh)[] k_faces =
        {
            (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0), (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
            (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
        };

        /// <summary>The B-rep of one box, with the edge table built from its own faces.</summary>
        private PhysicsOwnerShape NewBoxShape(out Mesh colliderMesh)
        {
            var faceOffsets = new[] { 0, 4, 8, 12, 16, 20, 24 };
            var faceIndices = new List<int>();
            foreach ((int[] cycle, int _) in k_faces)
            {
                faceIndices.AddRange(cycle);
            }

            BuildEdges(faceOffsets, faceIndices.ToArray(), out int[] faceEdges, out BrepEdge[] edges);

            // The input arrays are read by Authored, which copies every convex into a block the shape owns; nothing
            // reads them after it, so they live only through this call, whether it returns or throws. The collider
            // mesh is borrowed by the shape and stays tracked with the fixture's objects.
            var vertices = default(NativeArray<float3>);
            var offsets = default(NativeArray<int>);
            var indices = default(NativeArray<int>);
            var edgesOfFaces = default(NativeArray<int>);
            var edgeTable = default(NativeArray<BrepEdge>);
            try
            {
                vertices = new NativeArray<float3>(k_corners, Allocator.Persistent);
                offsets = new NativeArray<int>(faceOffsets, Allocator.Persistent);
                indices = new NativeArray<int>(faceIndices.ToArray(), Allocator.Persistent);
                edgesOfFaces = new NativeArray<int>(faceEdges, Allocator.Persistent);
                edgeTable = new NativeArray<BrepEdge>(edges, Allocator.Persistent);

                var bank = new ConvexBrepBank
                {
                    vertices = (float3*)vertices.GetUnsafePtr(),
                    faceOffsets = (int*)offsets.GetUnsafePtr(),
                    faceIndices = (int*)indices.GetUnsafePtr(),
                    faceEdges = (int*)edgesOfFaces.GetUnsafePtr(),
                    edges = (BrepEdge*)edgeTable.GetUnsafePtr(),
                };

                var range = new ConvexBrepRange
                {
                    vertexBase = 0, vertexCount = k_corners.Length,
                    faceBase = 0, faceCount = k_faces.Length,
                    faceIndexBase = 0, faceIndexCount = faceIndices.Count,
                    edgeBase = 0, edgeCount = edges.Length,
                    maxFaceLoop = 4,
                };

                colliderMesh = NewColliderMesh();
                var source = PhysicsShapeSource.External();
                return PhysicsOwnerShape.Authored(
                    bank, new[] { range }, new List<Mesh> { colliderMesh }, source, float4x4.identity);
            }
            finally
            {
                if (edgeTable.IsCreated) edgeTable.Dispose();
                if (edgesOfFaces.IsCreated) edgesOfFaces.Dispose();
                if (indices.IsCreated) indices.Dispose();
                if (offsets.IsCreated) offsets.Dispose();
                if (vertices.IsCreated) vertices.Dispose();
            }
        }

        private Mesh NewColliderMesh()
        {
            var mesh = Track(new Mesh { name = "Authored collider", hideFlags = HideFlags.HideAndDontSave });
            var vertices = new Vector3[k_corners.Length];
            for (int i = 0; i < k_corners.Length; i++)
            {
                vertices[i] = k_corners[i];
            }

            mesh.vertices = vertices;
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
            };
            UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
            return mesh;
        }

        private static void BuildEdges(int[] faceOffsets, int[] faceIndices, out int[] faceEdges, out BrepEdge[] edges)
        {
            var found = new Dictionary<long, int>();
            var table = new List<BrepEdge>();
            faceEdges = new int[faceIndices.Length];
            for (int f = 0; f + 1 < faceOffsets.Length; f++)
            {
                int from = faceOffsets[f];
                int count = faceOffsets[f + 1] - from;
                for (int k = 0; k < count; k++)
                {
                    int a = faceIndices[from + k];
                    int b = faceIndices[from + ((k + 1) % count)];
                    int lo = math.min(a, b);
                    int hi = math.max(a, b);
                    long key = ((long)lo << 32) | (uint)hi;
                    if (!found.TryGetValue(key, out int at))
                    {
                        at = table.Count;
                        found.Add(key, at);
                        table.Add(new BrepEdge { v0 = lo, v1 = hi, f0 = -1, f1 = -1 });
                    }

                    BrepEdge edge = table[at];
                    if (a == lo)
                    {
                        if (edge.f0 < 0)
                        {
                            edge.f0 = f;
                        }
                    }
                    else if (edge.f1 < 0)
                    {
                        edge.f1 = f;
                    }

                    table[at] = edge;
                    faceEdges[from + k] = at;
                }
            }

            edges = table.ToArray();
        }

        /// <summary>The display geometry of that same box, appended to the world's storage as a cut input.</summary>
        private static VpStoredGeometry AppendBoxGeometry(VpCpuGeometryStorage storage, Vector3 offset)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int face = 0; face < k_faces.Length; face++)
                {
                    if (k_faces[face].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[face].cycle;
                    float3 normal = math.normalize(math.cross(
                        k_corners[c[1]] - k_corners[c[0]], k_corners[c[2]] - k_corners[c[0]]));
                    uint b = (uint)vertices.Count;
                    var uv = new[]
                    {
                        new float2(0.05f, 0.1f), new float2(0.95f, 0.1f),
                        new float2(0.95f, 0.9f), new float2(0.05f, 0.9f),
                    };
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex
                        {
                            position = k_corners[c[k]] + (float3)(Vector3)offset,
                            normal = normal,
                            uv0 = uv[k],
                        });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(
                    start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            Assert.That(
                storage.TryAppendCuttable(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_corners.Length, submeshes.ToArray(),
                    out VpStoredGeometry geometry, out _),
                Is.True,
                "the display geometry of the body is appended");
            return geometry;
        }

        /// <summary>One body in the world: its actor, its physics shape and its display geometry, all tied together.</summary>
        private LogicalFragmentId AddBody(CutWorldRoot root, Vector3 at)
        {
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxGeometry(root.Storage, Vector3.zero);

            var actor = TrackActor(new GameObject("Body"));
            actor.transform.position = at;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);

            bool added = root.TryAddBody(
                actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, null, out LogicalFragmentId fragment);
            if (added)
            {
                _registered.Add(actor);
            }

            Assert.That(added, Is.True, "the body was taken into the world");
            return fragment;
        }

        private static ProvisionalCutAsk Ask(LogicalFragmentId source, float4 plane)
        {
            // The two impulses are this test's input, not a product value: DESIGN 7.2 leaves them to the caller.
            return new ProvisionalCutAsk
            {
                source = source,
                plane = plane,
                positiveSeparationImpulse = 0f,
                negativeSeparationImpulse = 0f,
                renderAnchor = float3.zero,
            };
        }

        /// <summary>
        /// Ends one world the way a caller should: the ending is asked for once and the **ordinary frames** carry it,
        /// and nothing is destroyed until it has finished. A world left unended would be destroyed mid-ending, which
        /// the root reports as the error it is.
        /// </summary>
        private IEnumerator EndWorld(CutWorldRoot root)
        {
            Assert.That(ReferenceEquals(root, _root), Is.True, "the case ends the world the fixture holds");
            yield return EndWorldOnce();
            Assert.That(_ending, Is.EqualTo(WorldEnding.Confirmed), "the world's ending finished on the ordinary frames");
        }

        private static IEnumerator Until(Func<bool> condition, string what)
        {
            float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, what + ": it had not happened within the deadline");
        }

        private static LogicalCutOperation OperationOf(CutWorldRoot root, CutOperationId operation)
        {
            Assert.That(root.Ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
            return record;
        }

        private static bool IsDrawn(CutWorldRoot root, LogicalFragmentId fragment)
        {
            return DrawnFragmentsOf(root, fragment) > 0;
        }

        /// <summary>How many render fragments the settled collection draws for one branch root.</summary>
        private static int DrawnFragmentsOf(CutWorldRoot root, LogicalFragmentId fragment)
        {
            int found = 0;
            for (int r = 0; r < root.Display.RenderFragmentCount; r++)
            {
                Assert.That(root.Display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                if (rf.root == fragment)
                {
                    found++;
                }
            }

            return found;
        }

        // ----- 1. the frame the cut was taken up in --------------------------------------------------------------------

        /// <summary>
        /// **The pair is published in the update the ask was taken up in, and both sides are drawn in that same
        /// frame's collection.** Nothing of the final cut is waited for: the cut is still Provisional when the display
        /// has already settled two sides.
        /// <para>
        /// **The cut is kept in its state before the handoff, because this case holds the collection of the finished
        /// result.** The product may hand a cut over in the very update its products come back in, and the handoff
        /// takes the pair out of the registry -- so a cut whose cook happened to be collected inside this frame would
        /// leave no pair here to read, through no fault of the publication. What is held is the **collection** of a
        /// result that has finished, which says nothing about whether a worker is still running, and the hold goes as
        /// soon as the reading below is done.
        /// </para>
        /// </summary>
        [UnityTest]
        public IEnumerator AnAskTakenUpByTheUpdateLoop_IsPublishedAndDrawnInThatFrame()
        {
            HoldingExecutor unityJob = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.UnityJob
                    ? unityJob = Held(new HoldingExecutor(new UnityJobWorkExecutor(8)))
                    : null,
                null);
            Assert.That(unityJob, Is.Not.Null, "the cut's physics work goes to the destination this case holds");
            unityJob.HoldEverything = true;
            LogicalFragmentId body = AddBody(root, Vector3.zero);

            // One collection with the body itself, so that what follows is a change and not the first sight of it.
            yield return null;
            Assert.That(IsDrawn(root, body), Is.True, "the body is drawn before the cut");

            // What that frame's collection settled is read here, after the driver's own late update.
            LateFrameObserver observer = Track(new GameObject("Observer")).AddComponent<LateFrameObserver>();
            observer.world = root;
            observer.watched = body;

            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True, "the ask is noted for the driver's next update");

            // The very next frame: the driver's Update takes the ask up and publishes, its LateUpdate collects. A
            // coroutine resumes between the two, so this is where the publication is read and the observer is what
            // reads the collection.
            yield return null;
            int askedUpIn = Time.frameCount;

            Assert.That(
                root.Owners.ProvisionalPairCount, Is.EqualTo(1),
                "the Provisional pair was published in the update that took the ask up");
            Assert.That(
                root.Ledger.TryGetFragmentState(body, out LogicalFragmentState state)
                && state == LogicalFragmentState.Live,
                Is.True,
                "and the fragment is still the one live fragment: a Provisional publishes no logical child");

            ProvisionalCutTransaction transaction = null;
            foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
            {
                transaction = candidate;
            }

            Assert.That(transaction, Is.Not.Null, "the driver holds the record of that cut");
            LogicalCutOperation record = OperationOf(root, transaction.Operation);
            Assert.That(
                record.state, Is.EqualTo(LogicalCutOperationState.Admitted),
                "the cut is accepted and not published yet: its cook is still running");
            Assert.That(
                transaction.Phase, Is.Not.EqualTo(ProvisionalCutPhase.HandedOff),
                "the Final handoff is not a condition of what follows");

            // **Both sides are drawn in that same frame.** The observer's reading is of the frame the ask was taken
            // up in, after that frame's collection: two render fragments for the one body, one per side of the cut.
            yield return null;
            Assert.That(
                observer.Frame, Is.EqualTo(askedUpIn),
                "the reading is of the frame the cut was taken up in");
            Assert.That(
                observer.Pairs, Is.EqualTo(1),
                "the pair stood in the scene in that frame (asked up in " + askedUpIn + ", published in " + transaction.PublishedFrame
                + ", handed off in " + transaction.HandedOffFrame + ", observer read frame " + observer.Frame
                + ", phase now " + transaction.Phase + ")");
            Assert.That(
                observer.Drawn, Is.EqualTo(2),
                "and the body was drawn as the two sides of the cut in that frame's collection");

            // The situation this case means really was in force while it read: the cut was still Provisional, and
            // the handoff -- which is what takes the pair out of the registry -- had not happened in the frame that
            // was read. (What the children become the root of is the display's own, later change, made at the
            // geometry commit; it is not this, and this case does not reach it.)
            Assert.That(
                transaction.Phase, Is.Not.EqualTo(ProvisionalCutPhase.HandedOff),
                "the cut was still Provisional throughout the frame that was read (handed off in "
                + transaction.HandedOffFrame + ")");
            Assert.That(
                transaction.HandedOffFrame, Is.Not.EqualTo(askedUpIn),
                "so nothing that was read depended on when the cook's result happened to be collected");

            unityJob.ReleaseEverything();
            yield return EndWorld(root);
        }

        // ----- 2. the whole way, and a cut of a child -------------------------------------------------------------------

        /// <summary>
        /// **The update loop carries a cut to its end by itself**: the Final handoff happens, the display geometry is
        /// really committed, and one of the two children can then be cut in its turn — all without a test calling the
        /// driver.
        /// </summary>
        [UnityTest]
        public IEnumerator TheUpdateLoopCarriesACutToItsCommit_AndAChildCanBeCutAgain()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;

            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;

            CutOperationId operation = default;
            foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
            {
                operation = candidate.Operation;
            }

            Assert.That(operation.IsSet, Is.True, "the cut was accepted");
            yield return Until(
                () => root.Geometry.StageOf(operation) == CutGeometryStage.Committed,
                "the cut finished on both sides, driven by the update loop");

            LogicalCutOperation record = OperationOf(root, operation);
            Assert.That(root.Owners.TryGet(record.positive, out PhysicsFragmentOwner positive), Is.True);
            Assert.That(root.Owners.TryGet(record.negative, out PhysicsFragmentOwner _), Is.True);
            Assert.That(
                root.Geometry.TryGetGeometry(record.positive, out VpStoredGeometry _), Is.True,
                "each child is its own display geometry now");
            Assert.That(root.Owners.ProvisionalPairCount, Is.Zero, "and no Provisional pair is left");
            Assert.That(root.GeometryFaults, Is.Zero, "nothing failed");

            // The child, cut in its turn, through the same entrance.
            ProvisionalCutAsk again = Ask(record.positive, new float4(1f, 0f, 0f, 0f));
            Assert.That(root.TryAsk(in again), Is.True);
            yield return null;

            Assert.That(
                root.Owners.ProvisionalPairCount, Is.EqualTo(1),
                "the child's own pair was published in the update that took its ask up");

            CutOperationId second = default;
            foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
            {
                second = candidate.Operation;
            }

            yield return Until(
                () => root.Geometry.StageOf(second) == CutGeometryStage.Committed,
                "and the child's cut finished on both sides as well");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(
                positive.IsWithdrawn || positive.IsReleased, Is.True,
                "the child that was cut has been replaced by its own two children");

            yield return EndWorld(root);
        }

        // ----- 3. two owners at once ------------------------------------------------------------------------------------

        /// <summary>
        /// **Two bodies cut in the same frame make progress side by side.** Both cuts are really submitted to a
        /// destination — the work is with an executor, not merely recorded somewhere — and one of them finishing is
        /// not what the other waits for.
        /// </summary>
        [UnityTest]
        public IEnumerator TwoOwnersCutAtOnce_BothReachTheirDestination_AndNeitherWaitsForTheOther()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId first = AddBody(root, new Vector3(-4f, 0f, 0f));
            LogicalFragmentId second = AddBody(root, new Vector3(4f, 0f, 0f));
            yield return null;

            ProvisionalCutAsk askFirst = Ask(first, new float4(0f, 1f, 0f, 0f));
            ProvisionalCutAsk askSecond = Ask(second, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in askFirst), Is.True);
            Assert.That(root.TryAsk(in askSecond), Is.True);
            yield return null;

            Assert.That(
                root.Owners.ProvisionalPairCount, Is.EqualTo(2),
                "both cuts were published in the one update that took both asks up");
            Assert.That(root.Driver.Transactions.Count, Is.EqualTo(2), "and both are the driver's records");

            // **Two numeric works of two different owners, with a destination at the same moment.** The count is
            // taken per destination: the physics of a cut goes to the Unity job destination and its display geometry
            // to the geometry pool, so the whole count could reach two with one owner's two duties. Two there means
            // two owners' physics.
            yield return Until(
                () => root.Dispatcher.SubmittedCountFor(WorkDestination.UnityJob) >= 2,
                "both owners' physics work was with the Unity job destination at the same time");

            yield return Until(
                () => root.Driver.Transactions.Count == 0,
                "and both were carried to their end");

            Assert.That(root.Owners.ProvisionalPairCount, Is.Zero, "neither pair is left standing");
            Assert.That(root.GeometryFaults, Is.Zero);

            yield return EndWorld(root);
        }

        // ----- 4. an ending with a work really held -------------------------------------------------------------------

        /// <summary>
        /// **An ending frees nothing while a work is out, and needs no hand driving afterwards.** The geometry work of
        /// a cut is fixed as finished-but-uncollected at its destination; the world is then ended. What that work
        /// holds — its reservation of the storage's room, and its read hold on the body's geometry — is still held
        /// while it is out, and the ending is only finished once the collection has really happened, which the
        /// ordinary frames do by themselves.
        /// </summary>
        [UnityTest]
        public IEnumerator EndingTheWorldWithAWorkHeld_FreesNothingEarly_AndFinishesOnTheOrdinaryFrames()
        {
            HoldingExecutor geometryPool = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination =>
                {
                    if (destination != WorkDestination.GeometryPool)
                    {
                        return null;
                    }

                    geometryPool = Held(new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2)));
                    geometryPool.HoldEverything = true;
                    return geometryPool;
                },
                null);

            Assert.That(geometryPool, Is.Not.Null, "the geometry destination is this case's to hold");
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            VpStoredGeometry geometry = root.Geometry.TryGetGeometry(body, out VpStoredGeometry found)
                ? found
                : default;
            Assert.That(
                root.Storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out _),
                Is.True);
            Assert.That(state, Is.EqualTo(VpIndexRangeState.Published), "the body's geometry is published");
            yield return null;

            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);

            CutOperationId operation = default;
            yield return Until(
                () =>
                {
                    foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
                    {
                        operation = candidate.Operation;
                    }

                    return operation.IsSet && root.Geometry.StageOf(operation) == CutGeometryStage.Running;
                },
                "the geometry work of the cut is running");

            // Fixed: finished at its destination and held back from the collection.
            yield return Until(
                () => geometryPool.HoldingCount > 0,
                "its work finished and is held back from the collection");
            VpStorageCutRequest running = root.Geometry.RequestOf(operation);
            Assert.That(running, Is.Not.Null, "the DAG still holds that cut");
            Assert.That(running.HoldsReservation, Is.True, "which holds a reservation of the storage's room");
            int freeWhileOut = root.Storage.FreeIndexRoom;

            // The ending is asked for while that work is out.
            Assert.That(root.Shutdown(), Is.False, "the ending cannot finish while a work is out");
            Assert.That(root.IsEnding, Is.True);
            Assert.That(root.IsReleased, Is.False, "so nothing has been freed");
            Assert.That(root.TryAsk(in ask), Is.False, "and nothing is accepted any more");

            // Frames pass, and still nothing is freed: the work is still out.
            yield return null;
            yield return null;
            Assert.That(root.IsReleased, Is.False, "nothing was freed while the work was still out");
            Assert.That(running.HoldsReservation, Is.True, "its reservation is still held");
            Assert.That(
                root.Storage.FreeIndexRoom, Is.EqualTo(freeWhileOut), "and the room it reserved is still taken");
            Assert.That(
                root.Storage.TryGetIndexState(geometry.indexRange, out state, out _, out _), Is.True,
                "the body's geometry is still there to be read");

            // Let it be collected. From here the ordinary frames finish the ending: nothing of the driver or the
            // frame is called by this case.
            geometryPool.ReleaseEverything();

            // The one ending of this fixture carries the rest: the ending was already asked for above, so it waits
            // for the release on the ordinary frames -- to the same single deadline, not to a new one.
            yield return EndWorld(root);

            Assert.That(running.HoldsReservation, Is.False, "the reservation went back with the work");
            Assert.That(root.IsDrained(), Is.True, "and nothing of the world is out any more");
        }

        /// <summary>
        /// **A case that never reaches its own ending is still ended, by the teardown.** A cut's physics work is held
        /// at its destination and the case stops there: it asks for nothing, ends nothing and destroys nothing. The
        /// teardown lets the hold go, asks for the ending once, waits for it on the ordinary frames, and only then
        /// gives the fixture's own resources back -- which is what the closing line of this case says.
        /// </summary>
        [UnityTest]
        public IEnumerator ACaseThatNeverEndsItsWorld_IsEndedAndCollectedByTheTeardown()
        {
            HoldingExecutor unityJob = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.UnityJob
                    ? unityJob = Held(new HoldingExecutor(new UnityJobWorkExecutor(8)))
                    : null,
                null);
            Assert.That(unityJob, Is.Not.Null);
            unityJob.HoldEverything = true;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;

            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return Until(() => unityJob.HoldingCount > 0, "the cut's physics work is held at its destination");

            Assert.That(root.IsEnding, Is.False, "this case asks for no ending");
            Assert.That(root.IsReleased, Is.False, "and nothing of the world has been freed");

            // And that is where this case stops. What follows is the teardown's.
        }

        // ----- 5. the termination request of DESIGN 4 --------------------------------------------------------------------

        /// <summary>
        /// **A shared geometry that cannot be cut ends the Player.** The body is registered with a display geometry
        /// that was never accepted as a cut input, so the cut of it fails in the geometry route; that failure is a
        /// cause of the common termination of DESIGN 4. The latch is fixed, new cuts are not accepted any more, no
        /// unpublished product is committed, and the Player's termination API — faked here — is called once, however
        /// many causes arrive.
        /// </summary>
        [UnityTest]
        public IEnumerator AGeometryThatCannotBeCut_RequestsThePlayersTermination_Once()
        {
            ExpectTermination();
            int terminations = 0;
            CutWorldRoot root = NewWorld(out Shader _, null, () => terminations++);

            // A geometry appended as an ordinary one, not as a cut input: the cut route refuses to read it.
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendPlainBoxGeometry(root.Storage);

            var actor = TrackActor(new GameObject("Body"));
            var rigidbody = actor.AddComponent<Rigidbody>();
            rigidbody.useGravity = false;
            rigidbody.automaticCenterOfMass = false;
            rigidbody.automaticInertiaTensor = false;
            rigidbody.mass = (float)ParentMass;
            rigidbody.centerOfMass = Vector3.zero;
            rigidbody.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);
            bool added = root.TryAddBody(
                actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, null, out LogicalFragmentId body);
            if (added)
            {
                _registered.Add(actor);
            }

            Assert.That(added, Is.True);
            yield return null;

            Assert.That(root.TerminationRequested, Is.False, "nothing has gone wrong yet");

            // The termination is logged once, best effort, before the Player's API is called: that log is part of the
            // contract, so it is expected here rather than being a failure of this case.
            LogAssert.Expect(LogType.Error, new Regex("the Player is being ended"));
            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);

            yield return Until(() => root.TerminationRequested, "the geometry failure requested the termination");

            Assert.That(root.GeometryFaults, Is.GreaterThan(0), "the failure was reported once, where it happened");
            Assert.That(terminations, Is.EqualTo(1), "and the Player's termination API was called once");
            Assert.That(root.TerminationCalls, Is.EqualTo(1));

            // Acceptance is closed from the moment the latch was fixed.
            Assert.That(root.TryAsk(in ask), Is.False, "no cut is accepted after the termination request");
            Assert.That(
                root.Driver.enabled, Is.False, "and the asks noted before it are not taken up either");

            // A second cause changes nothing: the latch is one way and the API is called once.
            yield return null;
            Assert.That(terminations, Is.EqualTo(1), "the termination API is not called again");
            Assert.That(root.TerminationCalls, Is.EqualTo(1));

            // **A termination is not an ending.** The ordinary ending -- ending every cut, collecting, confirming the
            // workers -- is not started by it and cannot be started after it.
            Assert.That(root.Shutdown(), Is.False, "the ordinary ending is not started after a termination request");
            Assert.That(root.IsEnding, Is.False);
            Assert.That(root.IsReleased, Is.False, "and nothing was freed by it");
        }

        /// <summary>
        /// **Nothing unpublished is published after the termination request, including later in the very update it was
        /// made in.** One cut's cook is finished and waiting to be collected; another body's geometry cannot be cut.
        /// The second one's failure fixes the latch, and only then is the first one's finished bake let go — so the
        /// collection that follows, in that same carry, does not hand the first cut over and publishes no logical
        /// child for it.
        /// <para>
        /// **The situation is made at a seam inside the carry, not at a frame boundary.** The hold is released from
        /// the termination call itself: that is after the latch is fixed and before the call that is carrying the
        /// frame returns, so the collection really happens with the latch in force. Releasing it earlier would let
        /// the cut be handed over before there is any latch — which says nothing about this contract — and releasing
        /// it later would leave the work uncollected, because the latch stops the driver, and a case that never
        /// collects would pass whatever the product did.
        /// </para>
        /// </summary>
        [UnityTest]
        public IEnumerator AfterTheTerminationRequest_AFinishedCutIsNotPublished_EvenLaterInThatUpdate()
        {
            ExpectTermination();
            HoldingExecutor physics = null;
            int terminations = 0;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination =>
                {
                    if (destination != WorkDestination.UnityJob)
                    {
                        return null;
                    }

                    physics = Held(new HoldingExecutor(new UnityJobWorkExecutor(8)));
                    physics.HoldEverything = true;
                    return physics;
                },
                () =>
                {
                    terminations++;

                    // **The seam.** The latch is fixed by now -- this is what being called means -- and the call
                    // carrying the frame has not returned, so what is let go here is collected inside that same
                    // carry, with the latch in force.
                    physics?.ReleaseEverything();
                });

            Assert.That(physics, Is.Not.Null);
            LogicalFragmentId cuttable = AddBody(root, new Vector3(-4f, 0f, 0f));
            LogicalFragmentId unusable = AddPlainBody(root, new Vector3(4f, 0f, 0f));
            yield return null;

            // The first cut, carried to "finished, waiting to be collected": its bake is with the destination and
            // held there, so one collection would finish its cook and hand it over.
            ProvisionalCutAsk first = Ask(cuttable, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in first), Is.True);

            // Its earlier stages are let through one at a time; the one that appears when it reaches the bake stays
            // held, which is what "finished, waiting to be collected" means here.
            ProvisionalCutTransaction held = null;
            float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
                {
                    if (candidate.Source.Equals(cuttable))
                    {
                        held = candidate;
                    }
                }

                if (held != null && held.Cut != null && held.Cut.Stage == PhysicsCutStage.Baking
                    && physics.HoldingCount > 0)
                {
                    break;
                }

                physics.TryLetOneThrough();
                yield return null;
            }

            Assert.That(held, Is.Not.Null, "the driver holds the first cut");
            Assert.That(held.Cut, Is.Not.Null);
            Assert.That(
                held.Cut.Stage, Is.EqualTo(PhysicsCutStage.Baking),
                "the first cut's bake is with the destination");
            Assert.That(physics.HoldingCount, Is.GreaterThan(0), "and it is held there, not collected");

            CutOperationId operation = held.Operation;
            Assert.That(
                OperationOf(root, operation).positive.IsSet, Is.False, "no child of it is published yet");

            // The bake is **finished and not collected**: the destination handed it back and the hold kept it here.
            Assert.That(physics.HoldEverything, Is.True, "the bake is still held, not let through");
            Assert.That(
                physics.HoldingCount, Is.GreaterThan(0),
                "and it is finished work waiting to be collected, not work still running");
            for (int i = 0; i < physics.HoldingCount; i++)
            {
                IDispatchWork heldWork = physics.HeldWorkAt(i);
                // **Asked of the work itself.** That the destination handed it back already means it finished --
                // the executor only gives back work whose IsComplete is true -- but the case says so directly, so
                // that it does not rest on how the executor happens to decide that.
                Assert.That(
                    heldWork.IsComplete, Is.True,
                    "the held work reports that it has finished, so this really is finished-and-not-collected");
            }

            // Now ask for the cut that cannot be done. Its geometry fails while the frame is carried, which fixes
            // the latch and calls the termination API -- and the hold is released from there, not from here.
            LogAssert.Expect(LogType.Error, new Regex("the Player is being ended"));
            ProvisionalCutAsk fails = Ask(unusable, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in fails), Is.True);

            yield return Until(() => root.TerminationRequested, "the geometry failure requested the termination");
            Assert.That(terminations, Is.EqualTo(1));

            // From here the first cut's products may be collected at any moment -- and are not published.
            yield return null;
            yield return null;

            // **The products really came back, and were then not published.** Without these, a cut whose cook had
            // never been collected would pass this case just as well.
            Assert.That(held.Cut, Is.Null, "the first cut's request ended: its result was collected");
            Assert.That(
                held.Phase, Is.EqualTo(ProvisionalCutPhase.FinalHeld),
                "and its record holds the final products");
            Assert.That(held.Products, Is.Not.Null, "which it still has, because nothing was handed over");
            Assert.That(
                held.Phase, Is.Not.EqualTo(ProvisionalCutPhase.HandedOff),
                "the finished cut was not handed over after the termination request");
            Assert.That(
                OperationOf(root, operation).positive.IsSet, Is.False,
                "and no logical child was published for it");
            Assert.That(
                root.Owners.ProvisionalPairCount, Is.GreaterThan(0),
                "its Provisional pair is where it was: nothing was published or ended for it");
        }

        /// <summary>One body whose display geometry was never accepted as a cut input.</summary>
        private LogicalFragmentId AddPlainBody(CutWorldRoot root, Vector3 at)
        {
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendPlainBoxGeometry(root.Storage);

            var actor = TrackActor(new GameObject("Body without a cuttable geometry"));
            actor.transform.position = at;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);

            bool added = root.TryAddBody(
                actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, null, out LogicalFragmentId fragment);
            if (added)
            {
                _registered.Add(actor);
            }

            Assert.That(added, Is.True, "the body was taken into the world");
            return fragment;
        }

        /// <summary>A box appended as an ordinary geometry: shown, but never accepted as a cut input.</summary>
        private static VpStoredGeometry AppendPlainBoxGeometry(VpCpuGeometryStorage storage)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int face = 0; face < k_faces.Length; face++)
                {
                    if (k_faces[face].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[face].cycle;
                    float3 normal = math.normalize(math.cross(
                        k_corners[c[1]] - k_corners[c[0]], k_corners[c[2]] - k_corners[c[0]]));
                    uint b = (uint)vertices.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex
                        {
                            position = k_corners[c[k]],
                            normal = normal,
                            uv0 = new float2(0.5f, 0.5f),
                        });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(
                    start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            Assert.That(
                storage.TryAppendPrepared(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_corners.Length, submeshes.ToArray(),
                    out VpStoredGeometry geometry),
                Is.True,
                "the geometry is appended, but not as a cut input");
            return geometry;
        }
    }
}
