using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// What a fixture's world lends its ending: the driver it bound, the runners it closes, the frame it pumps, the
    /// dispatcher it stops, and -- last, and only once everything has come back -- what it releases.
    /// </summary>
    internal interface ICutFixtureWorld
    {
        /// <summary>The driver, or null: a test may have destroyed it and cleared the field itself.</summary>
        ProvisionalCutDriver Driver { get; }

        /// <summary>The DAG, where the fixture has one; null otherwise.</summary>
        CutDag Dag { get; }

        PhysicsOwnerRegistry Registry { get; }

        PhysicsCutCook Cook { get; }

        SharedWorkFrame Frame { get; }

        SharedWorkDispatcher Dispatcher { get; }

        /// <summary>Lets every held work go, at the fixture's own destinations, so that it can be collected.</summary>
        void ReleaseHeldWork();

        /// <summary>Pumps the runners directly, for a world whose driver never added them to the frame.</summary>
        void PumpDirectly();

        /// <summary>
        /// What a worker may still be reading, in the order it is released: storage, the input harness, the pools, a
        /// display -- each as a name and the call that releases it, listed only where the member exists. Nothing here
        /// is released until the ending has been confirmed.
        /// </summary>
        IReadOnlyList<KeyValuePair<string, Action>> Tail { get; }
    }

    internal enum CutFixtureEndingState
    {
        NotStarted = 0,
        InProgress = 1,
        ReleasedSafely = 2,
        NotConfirmed = 3,
    }

    /// <summary>
    /// Whether the driver's recovery and its record list were registered with the ending. A world whose driver was
    /// never bound has no recovery and can have accepted no cut; one whose bind was attempted but not confirmed is
    /// of unknown state, which is not the same thing.
    /// </summary>
    internal enum CutFixtureRegistration
    {
        NotBound = 0,
        BindAttempted = 1,
        Held = 2,
    }

    /// <summary>
    /// The ending of one fixture world, in the order the product's own ending takes (<see cref="CutWorldRoot"/>):
    /// every accepted cut is asked to end, the runners are closed, the frame is pumped until nothing of the world is
    /// still with a worker and no record is still waiting, the dispatcher is stopped and confirmed, and only then is
    /// what a worker could have been reading released.
    /// <para>
    /// **This is an EditMode fixture's ending, not the product's.** The product pumps one turn per real frame on
    /// `Time.frameCount`; this pumps turns on a counter of its own inside one call, so each turn has a fresh budget.
    /// What is the same is the end state it insists on before releasing anything, and that it never releases by
    /// force: when the ending cannot be confirmed within its deadline, or anything of it threw, **nothing** of the
    /// tail is released, and the world is kept back for the fixture to report.
    /// </para>
    /// <para>
    /// It runs once. A second call -- a fixture disposes a world both at the end of a `using` and from its own list
    /// -- does nothing, whether the first ended safely or not: no retry, no release.
    /// </para>
    /// </summary>
    internal sealed class CutFixtureEnding
    {
        /// <summary>
        /// Where the ending's own frame ids start. The dispatcher refills its budget for any id other than the one
        /// that is open, so these need only differ from one another; a collision with the test's last id would cost
        /// one turn without a refill, and the next turn is a different id.
        /// </summary>
        private const int FirstEndingFrameId = 1000000;

        private bool _entered;
        private ProvisionalCutRecovery _recovery;
        private IReadOnlyList<ProvisionalCutTransaction> _transactions;
        private readonly List<ProvisionalCutTransaction> _records = new List<ProvisionalCutTransaction>();
        private readonly List<PhysicsCutRequest> _requests = new List<PhysicsCutRequest>();
        private readonly List<string> _reasons = new List<string>();
        private readonly List<string> _released = new List<string>();
        private readonly List<string> _facts = new List<string>();

        public CutFixtureEndingState State { get; private set; } = CutFixtureEndingState.NotStarted;

        public CutFixtureRegistration Registration { get; private set; } = CutFixtureRegistration.NotBound;

        /// <summary>Whether anything of the ending failed. Once true it stays true: a later step never clears it.</summary>
        public bool Failed => _reasons.Count > 0;

        /// <summary>Said just before the driver is bound, so that a bind that throws is not mistaken for one never tried.</summary>
        public void BindAttempted()
        {
            Registration = CutFixtureRegistration.BindAttempted;
        }

        /// <summary>
        /// Said right after the driver is bound: the recovery the bind made and the driver's own (live) record list
        /// are held from here on, so that a test destroying the driver later does not take them with it.
        /// </summary>
        public void Bound(ProvisionalCutDriver driver)
        {
            _recovery = driver != null ? driver.Recovery : null;
            _transactions = driver != null ? driver.Transactions : null;
            Registration = _recovery != null && _transactions != null
                ? CutFixtureRegistration.Held
                : CutFixtureRegistration.BindAttempted;
        }

        public void Run(ICutFixtureWorld world, int deadlineMilliseconds)
        {
            if (_entered)
            {
                return;
            }

            _entered = true;
            State = CutFixtureEndingState.InProgress;
            string step = "S1";
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                int frameId = FirstEndingFrameId;
                ProvisionalCutDriver driver = world.Driver;
                bool driverAlive = Registration != CutFixtureRegistration.NotBound && driver != null;

                // S1. What the driver still lists is noted now, so that each record's own ending can be read after.
                if (_transactions != null)
                {
                    for (int i = 0; i < _transactions.Count; i++)
                    {
                        _records.Add(_transactions[i]);
                        _requests.Add(_transactions[i].Cut);
                    }
                }

                _facts.Add("registration=" + Registration + " driverAlive=" + driverAlive
                           + " recordsListed=" + _records.Count);

                // S2-S6. The explicit ending, the driver's destruction and the closes. Each is tried on its own: one
                // throwing does not stop the next from being tried, but it does stop this ending from ever releasing.
                step = "S2";
                if (driverAlive)
                {
                    Attempt(step, "EndEveryCut", () => driver.EndEveryCut());
                }

                step = "S3";
                if (driverAlive)
                {
                    Attempt(step, "DestroyImmediate(driver)", () => UnityEngine.Object.DestroyImmediate(driver.gameObject));
                }

                step = "S4";
                Attempt(step, "dag.Dispose", () => world.Dag?.Dispose());
                step = "S5";
                Attempt(step, "registry.Dispose", () => world.Registry?.Dispose());
                step = "S6";
                Attempt(step, "cook.Dispose", () => world.Cook?.Dispose());

                // S7. One loop: what is held is let go, the frame is pumped -- the cook, the DAG and the recovery are
                // its participants -- and the collectable conditions are read. An exception here ends the ending.
                step = "S7";
                while (clock.ElapsedMilliseconds < deadlineMilliseconds && !Collectable(world))
                {
                    world.ReleaseHeldWork();
                    if (world.Frame != null)
                    {
                        world.Frame.Update(frameId++);
                    }

                    if (Registration != CutFixtureRegistration.Held || world.Frame == null)
                    {
                        // The driver never put its participants on the frame, or there is no frame: the runners are
                        // pumped by hand, the way the fixtures always did.
                        if (world.Frame == null && world.Dispatcher != null)
                        {
                            world.Dispatcher.BeginFrame(frameId++);
                            world.Dispatcher.Dispatch();
                        }

                        world.PumpDirectly();
                    }
                }

                // S8. Read once more, and written down whatever it says.
                step = "S8";
                bool collected = Collected(world, "S8");
                bool closed = RecordsClosed("S8");
                if (!collected)
                {
                    Fail(step, "not collected within " + deadlineMilliseconds + " ms");
                    return;
                }

                if (!closed)
                {
                    Fail(step, "the records this driver listed did not all close");
                }

                // S9. The stop. It is asked once, and only of a drained world, as the product asks it; an earlier
                // failure still allows this attempt, and nothing after it.
                step = "S9";
                bool stopped;
                try
                {
                    int remaining = (int)Math.Max(0, deadlineMilliseconds - clock.ElapsedMilliseconds);
                    stopped = world.Dispatcher == null || world.Dispatcher.Shutdown(remaining).workersStopped;
                }
                catch (Exception stopFailure)
                {
                    Fail(step, "Shutdown threw: " + stopFailure);
                    return;
                }

                _facts.Add("workersStopped=" + stopped);
                if (!stopped)
                {
                    Fail(step, "the workers did not confirm that they had stopped");
                    return;
                }

                if (Failed)
                {
                    return;
                }

                // S10-S11. What the stop handed back is taken, once, and everything is read again.
                step = "S10";
                if (world.Frame != null)
                {
                    world.Frame.Update(frameId++);
                }

                step = "S11";
                if (!Collected(world, "S11") || !RecordsClosed("S11"))
                {
                    Fail(step, "not confirmed after the stop");
                    return;
                }

                // S12. Only now. One at a time, in order; one throwing leaves the rest unreleased.
                step = "S12";
                IReadOnlyList<KeyValuePair<string, Action>> tail = world.Tail;
                for (int i = 0; i < tail.Count; i++)
                {
                    tail[i].Value();
                    _released.Add(tail[i].Key);
                }

                step = "S13";
                if (!Failed)
                {
                    State = CutFixtureEndingState.ReleasedSafely;
                }
            }
            catch (Exception failure)
            {
                Fail(step, "threw: " + failure);
            }
            finally
            {
                if (Failed || State == CutFixtureEndingState.InProgress)
                {
                    State = CutFixtureEndingState.NotConfirmed;
                }

                WriteClosingLine();
            }
        }

        /// <summary>
        /// The one information line this ending leaves in the log. Both making the text and writing it are inside
        /// their own boundary: a Dispose runs inside a `using`, and nothing of a report may replace the exception
        /// the test itself is carrying. A failure to describe is recorded as a reason -- directly, so that the
        /// recording does not come back through here -- and a failure to write is not recorded anywhere but here.
        /// </summary>
        private void WriteClosingLine()
        {
            string line;
            try
            {
                line = "CutFixtureEnding " + Describe();
            }
            catch (Exception describing)
            {
                _reasons.Add("report: describing the ending threw: " + describing.GetType().Name);
                line = "CutFixtureEnding state=" + State + " (describing it threw: " + describing.GetType().Name + ")";
            }

            try
            {
                UnityEngine.Debug.Log(line);
            }
            catch (Exception)
            {
                // Not written, and not recorded: recording would be another report, and this is the end of it.
            }
        }

        public string Describe()
        {
            var text = new StringBuilder();
            text.Append("state=").Append(State).Append(" registration=").Append(Registration);
            for (int i = 0; i < _facts.Count; i++)
            {
                text.Append(" | ").Append(_facts[i]);
            }

            text.Append(" | released=[").Append(string.Join(",", _released)).Append(']');
            for (int i = 0; i < _reasons.Count; i++)
            {
                text.Append(" | FAIL ").Append(_reasons[i]);
            }

            return text.ToString();
        }

        private void Attempt(string step, string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception failure)
            {
                Fail(step, what + " threw: " + failure);
            }
        }

        private void Fail(string step, string reason)
        {
            _reasons.Add(step + ": " + reason);
        }

        /// <summary>
        /// The conditions that pumping can still change: the runners drained, nothing submitted, and -- for a
        /// registered recovery -- nothing waiting there. An unconfirmed registration is not asked about here.
        /// </summary>
        private bool Collectable(ICutFixtureWorld world)
        {
            bool cook = world.Cook == null || world.Cook.IsDrained;
            bool dag = world.Dag == null || world.Dag.IsDrained;
            bool submitted = world.Dispatcher == null || world.Dispatcher.SubmittedCount == 0;
            bool recovery = Registration != CutFixtureRegistration.Held || _recovery.Count == 0;
            return cook && dag && submitted && recovery;
        }

        /// <summary>
        /// Conditions 1-4, read and written down under <paramref name="at"/>. A registration that was attempted but
        /// never confirmed is unknown, and unknown is not collected.
        /// </summary>
        private bool Collected(ICutFixtureWorld world, string at)
        {
            bool cook = world.Cook == null || world.Cook.IsDrained;
            bool dag = world.Dag == null || world.Dag.IsDrained;
            bool submitted = world.Dispatcher == null || world.Dispatcher.SubmittedCount == 0;
            string recoveryText;
            bool recovery;
            switch (Registration)
            {
                case CutFixtureRegistration.NotBound:
                    recovery = true;
                    recoveryText = "n/a(not bound)";
                    break;
                case CutFixtureRegistration.Held:
                    recovery = _recovery.Count == 0;
                    recoveryText = _recovery.Count.ToString();
                    break;
                default:
                    recovery = false;
                    recoveryText = "unknown(bind attempted)";
                    break;
            }

            _facts.Add(at + ": cookDrained=" + cook + " dagDrained=" + dag
                       + " submitted=" + (world.Dispatcher == null ? "n/a" : world.Dispatcher.SubmittedCount.ToString())
                       + " recoveryCount=" + recoveryText);
            return cook && dag && submitted && recovery;
        }

        /// <summary>
        /// Conditions 6-8: the driver lists no record any more, every record it listed at S1 has closed -- recovered
        /// or handed off, holding no input and no products -- and every request those records had is over.
        /// </summary>
        private bool RecordsClosed(string at)
        {
            if (Registration == CutFixtureRegistration.BindAttempted)
            {
                _facts.Add(at + ": records=unknown(bind attempted)");
                return false;
            }

            int listed = _transactions != null ? _transactions.Count : 0;
            int open = 0;
            int requestsRunning = 0;
            for (int i = 0; i < _records.Count; i++)
            {
                ProvisionalCutTransaction record = _records[i];
                bool ended = record.Phase == ProvisionalCutPhase.Recovered || record.Phase == ProvisionalCutPhase.HandedOff;
                if (!ended || record.HoldsInput || record.Products != null)
                {
                    open++;
                }
            }

            for (int i = 0; i < _requests.Count; i++)
            {
                if (_requests[i] != null && !_requests[i].IsOver)
                {
                    requestsRunning++;
                }
            }

            _facts.Add(at + ": stillListed=" + listed + " recordsOpen=" + open + "/" + _records.Count
                       + " requestsRunning=" + requestsRunning + "/" + _requests.Count);
            return listed == 0 && open == 0 && requestsRunning == 0;
        }
    }

    /// <summary>
    /// A fixture's own clean-up after its worlds have ended: destroying what it tracked one by one, and reporting an
    /// ending that was not confirmed without hiding a failure the test itself already had.
    /// </summary>
    internal static class CutFixtureTeardown
    {
        /// <summary>
        /// Destroys the tracked lists in the order given, each entry in turn, removing each from its list as it goes.
        /// At the first exception it stops: the entry that threw and everything after it stay in that list, **and no
        /// later list is touched at all**, so that what was destroyed and what was not are told apart, and false is
        /// returned. The lists are the fixture's own, kept whole.
        /// </summary>
        public static bool DestroyInOrder(List<string> failures, params KeyValuePair<string, IList>[] lists)
        {
            for (int i = 0; i < lists.Length; i++)
            {
                if (!DestroyEach(lists[i].Value, lists[i].Key, failures))
                {
                    if (i + 1 < lists.Length)
                    {
                        failures.Add("the lists after '" + lists[i].Key + "' were not touched: "
                                     + string.Join(", ", Names(lists, i + 1)));
                    }

                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<string> Names(KeyValuePair<string, IList>[] lists, int from)
        {
            for (int i = from; i < lists.Length; i++)
            {
                yield return lists[i].Key;
            }
        }

        /// <summary>
        /// Destroys one list's entries in order, removing each as it goes. On an exception the entry that threw and
        /// everything after it stay in the list, and false is returned. An entry that is not a Unity object cannot be
        /// destroyed and counts as such an exception.
        /// </summary>
        public static bool DestroyEach(IList tracked, string what, List<string> failures)
        {
            int done = 0;
            while (tracked.Count > 0)
            {
                try
                {
                    object entry = tracked[0];
                    if (entry != null)
                    {
                        UnityEngine.Object.DestroyImmediate((UnityEngine.Object)entry);
                    }
                }
                catch (Exception failure)
                {
                    failures.Add(what + ": destroying entry " + done + " threw; it and " + (tracked.Count - 1)
                                 + " after it are kept back: " + failure);
                    return false;
                }

                tracked.RemoveAt(0);
                done++;
            }

            return true;
        }

        /// <summary>
        /// The fixture's last word. A teardown that itself threw is rethrown after the report is written; an ending
        /// that was not confirmed fails a test that had passed, and is written beside the failure of one that had not.
        /// </summary>
        public static void Report(List<string> report, bool notConfirmed, Exception teardownError)
        {
            string text = report.Count == 0 ? "(no reasons recorded)" : string.Join("\n", report);
            if (teardownError != null)
            {
                TestContext.WriteLine("fixture ending: " + text);
                ExceptionDispatchInfo.Capture(teardownError).Throw();
                return;
            }

            if (!notConfirmed)
            {
                return;
            }

            if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Passed)
            {
                Assert.Fail("the fixture's ending was not confirmed, so what a worker could still read was kept "
                            + "back, not released:\n" + text);
            }

            TestContext.WriteLine("fixture ending (beside the test's own failure): " + text);
        }
    }

    /// <summary>
    /// What one test left unconfirmed, kept for the rest of the fixture's life and never touched again: its worlds
    /// (with their endings' states and reasons), the objects, meshes and materials it tracked, and the report. The
    /// lists themselves are moved here; the fixture goes on with fresh ones.
    /// </summary>
    internal sealed class CutFixtureHeldBack
    {
        public CutFixtureHeldBack(
            string test, IList worlds, IList objects, IList meshes, IList materials, IReadOnlyList<string> report)
        {
            Test = test;
            Worlds = worlds;
            Objects = objects;
            Meshes = meshes;
            Materials = materials;
            Report = report;
        }

        public string Test { get; }

        public IList Worlds { get; }

        public IList Objects { get; }

        public IList Meshes { get; }

        public IList Materials { get; }

        public IReadOnlyList<string> Report { get; }
    }
}
