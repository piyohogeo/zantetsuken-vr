using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// How <see cref="CutEmptyOwnerCollection"/> walks and paces its retirements. Every value is the caller's; the
    /// profile holds the provisional ones (DESIGN 7.9.3 leaves the walk's order, its frequency and the budget to the
    /// implementation).
    /// </summary>
    public readonly struct CutEmptyOwnerCollectionSettings
    {
        public CutEmptyOwnerCollectionSettings(
            bool enabled, int examinePerFrame, int retirePerFrame, double reserveSeconds, double seedExpectedSeconds)
        {
            this.enabled = enabled;
            this.examinePerFrame = examinePerFrame;
            this.retirePerFrame = retirePerFrame;
            this.reserveSeconds = reserveSeconds;
            this.seedExpectedSeconds = seedExpectedSeconds;
        }

        /// <summary>Off: no fragment is looked at and no retirement is started. What was started goes on regardless.</summary>
        public readonly bool enabled;

        /// <summary>Fragments looked at per frame, at most.</summary>
        public readonly int examinePerFrame;

        /// <summary>Retirements started per frame, at most.</summary>
        public readonly int retirePerFrame;

        /// <summary>The Main budget a turn must leave to the rest of the frame, in seconds, beyond twice its own expected cost.</summary>
        public readonly double reserveSeconds;

        /// <summary>The expected cost of a turn that starts a retirement, in seconds, before any was measured.</summary>
        public readonly double seedExpectedSeconds;

        public bool IsValid =>
            examinePerFrame > 0 && retirePerFrame > 0
            && reserveSeconds >= 0.0 && !double.IsInfinity(reserveSeconds)
            && seedExpectedSeconds >= 0.0 && !double.IsInfinity(seedExpectedSeconds);
    }

    /// <summary>Why a turn of <see cref="CutEmptyOwnerCollection"/> looked at nothing and started nothing.</summary>
    public enum EmptyOwnerHold
    {
        None = 0,

        /// <summary>Heavy work was noted in this frame (<see cref="CutWorldFrameNotes"/>: refill, re-preparation, MobPlan, the static index).</summary>
        HeavyWork,

        /// <summary>A managed garbage collection completed in this frame.</summary>
        GarbageCollection,

        /// <summary>A cut is in flight or held.</summary>
        CutInFlight,

        /// <summary>The ledger changed since the turn before: a cut admitted, published or committed, a fragment added or retired.</summary>
        StructureChanged,

        /// <summary>The display's room, its snapshot, a GPU object or the reference table was grown or replaced since the turn before.</summary>
        DisplayRoomGrown,

        /// <summary>The display compacted its instance regions in this frame: the two do not share a frame.</summary>
        CompactionRan,

        /// <summary>What is left of the frame's Main budget is less than the reserve and twice the expected cost.</summary>
        BudgetShort,

        Count,
    }

    /// <summary>What a fragment looked at turned out to be.</summary>
    public enum EmptyOwnerPass
    {
        /// <summary>A candidate: live, its shared geometry settled as empty, its own final physics with a convex.</summary>
        Candidate = 0,

        /// <summary>Its geometry is not settled as empty: it has faces, it is not committed yet, or it was never cut.</summary>
        NotEmpty,

        /// <summary>No longer a live fragment.</summary>
        NotLive,

        /// <summary>A cut of it is accepted or its Provisional pair stands.</summary>
        CutUnderWay,

        /// <summary>No physics owner of its own here: none, withdrawn, a member of a group, or display only.</summary>
        NoOwnPhysics,

        /// <summary>Its owner has no convex left to read.</summary>
        NoConvex,

        /// <summary>The display draws it inside an aggregate, where retiring it would stop the display.</summary>
        InsideAggregate,

        /// <summary>At the application only: what was chosen is no longer what stands there (another owner or shape).</summary>
        Changed,

        Count,
    }

    /// <summary>
    /// One fragment chosen for collection, with what it was when it was chosen: the owner and the shape it had. An
    /// application compares them with what stands there now; a candidate is never applied to whatever the fragment has
    /// become.
    /// </summary>
    public readonly struct EmptyOwnerCandidate
    {
        internal EmptyOwnerCandidate(LogicalFragmentId fragment, PhysicsFragmentOwner owner, PhysicsOwnerShape shape)
        {
            Fragment = fragment;
            Owner = owner;
            Shape = shape;
        }

        public LogicalFragmentId Fragment { get; }

        internal PhysicsFragmentOwner Owner { get; }

        internal PhysicsOwnerShape Shape { get; }

        public bool IsSet => Fragment.IsSet;
    }

    /// <summary>
    /// DESIGN 7.9.3's delayed collection of physics objects with nothing to show (Phase 5.7): a live logical fragment
    /// whose shared geometry a geometry commit settled as empty, and which still stands in the physics scene with its
    /// own final owner, is retired -- fragment, owner, actor and the building World D6 that actor carries -- a little at
    /// a time, and only in frames that have room. It is a lifetime policy of the game, not a memory collector, and
    /// nothing depends on it: off, late or never, every cut, commit and recut goes on as before.
    /// <para>
    /// **Which fragments.** Only one the cut DAG holds as without geometry (<see cref="CutDag.HasNoGeometry"/>): that
    /// is written by the commit of the cut that produced the fragment, for a side with no triangle at all. A side
    /// with any face -- a real cap, a triangle of no area -- has a geometry and is not one; a geometry still being
    /// computed, a failed one, a fragment never cut are not settled as empty. Nothing of the view is read: not a
    /// camera, not a culling result, not whether anything draws. Besides that the fragment is live, has no cut of its
    /// own under way and no Provisional pair, and has a physics owner of its own in the scene with at least one convex.
    /// A member of a fused or hull group, whose physics is its group's, is passed over. Moving, touching something and
    /// holding anchors are no reasons to wait.
    /// </para>
    /// <para>
    /// **How it retires.** The existing single retirement (DESIGN 7.1.3, as <see cref="CutFragmentLifetime"/> uses it):
    /// the ledger first -- the fragment is no target any more and anything that arrives for it later is stale -- then
    /// its physics owner, which leaves the scene at once with its colliders and its D6 and gives back only what
    /// nothing else still uses, then the DAG forgets it. Nothing is made for it: no operation, no pending cut, no
    /// transaction, no generation of its own. Right before, everything it was chosen for is asked again, and that the
    /// owner and the shape are the ones chosen. Its mass leaves the world; no sibling takes it.
    /// </para>
    /// <para>
    /// **When.** One turn a frame, after the frame's collections (<see cref="CutPhysicsStep.JoinAfterCollect"/>), so
    /// that everything this frame did is known. A turn looks at nothing and starts nothing when heavy work was noted
    /// in the frame, a garbage collection completed in it, a cut is in flight or held, the ledger changed since the
    /// turn before, the display grew room or replaced a GPU object, the display compacted in this frame, or what is
    /// left of the frame's Main budget is less than the reserve and twice this turn's own expected cost. A turn held
    /// leaves everything as it is; the walk goes on where it stopped in a later frame.
    /// </para>
    /// <para>
    /// **How much.** A walk over the fragments the owner registry has, taken as a list once a round and stepped a few
    /// a frame; a turn ends at its count or at the first retirement it starts. The expected cost is of a turn that
    /// starts a retirement: the seed until one was measured, then the median of the last five measured.
    /// </para>
    /// <para>
    /// **What it does not see.** The actor is destroyed by the engine at the end of the frame the retirement starts
    /// in, and what its destruction costs there is not in this turn's time; <see cref="ActorsDestroyed"/> only says,
    /// a turn later, that it happened.
    /// </para>
    /// </summary>
    public sealed class CutEmptyOwnerCollection
    {
        private static readonly ProfilerMarker s_retire = new ProfilerMarker("Zantetsu.EmptyOwnerCollection.Retire");

        private readonly CutWorldRoot _world;
        private readonly List<LogicalFragmentId> _round = new List<LogicalFragmentId>();
        private readonly SimulateCostHistory _costs = new SimulateCostHistory();
        private readonly int[] _holds = new int[(int)EmptyOwnerHold.Count];
        private readonly int[] _passes = new int[(int)EmptyOwnerPass.Count];
        private readonly int[] _refusals = new int[(int)EmptyOwnerPass.Count];
        private readonly List<GameObject> _actorsLeaving = new List<GameObject>(4);
        private readonly List<PhysicsOwnerShape> _shapesLeaving = new List<PhysicsOwnerShape>(4);
        private int _cursor;

        // What the turn before saw of the ledger and of the display's room: a difference is something this frame did.
        private long _revisionSeen = -1;
        private long _roomSeen = -1;

        internal CutEmptyOwnerCollection(CutWorldRoot world, in CutEmptyOwnerCollectionSettings settings)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            Settings = settings;
            RemainingMainSeconds = () => CutPhysicsStep.FrameRemainingMainSeconds;
        }

        public CutEmptyOwnerCollectionSettings Settings { get; set; }

        /// <summary>What is left of this frame's Main budget. The product's frame clock; a test gives its own.</summary>
        public Func<double> RemainingMainSeconds { get; set; }

        /// <summary>Turns taken while on: held ones and walking ones together.</summary>
        public int Turns { get; private set; }

        /// <summary>Turns that walked (not held), and rounds finished: each walked every fragment that had an owner when it began.</summary>
        public int WalkedTurns { get; private set; }
        public int Rounds { get; private set; }

        /// <summary>Fragments looked at, those found candidates, and the retirements started.</summary>
        public int Examined { get; private set; }
        public int Candidates { get; private set; }
        public int Retired { get; private set; }

        // The existing OwnerRetiring notification can identify this policy's retirement precisely, without
        // keeping a second lifetime history or treating other disappearances as collections.
        internal LogicalFragmentId RetiringFragment { get; private set; }

        /// <summary>Candidates that were not applied: what stood there at the application was not what was chosen.</summary>
        public int Refused { get; private set; }

        /// <summary>Of the retirements started: the building World D6 constraints that ended with their actors, and the convexes those owners had.</summary>
        public int ConstraintsRetired { get; private set; }
        public int ConvexesRetired { get; private set; }

        /// <summary>
        /// Of the retirements started: the actors the engine has since destroyed, and the shapes whose bank and mesh
        /// holds have since gone back (a shape work still reads goes back when that work is collected). Read a turn
        /// later or more; what is still on its way is <see cref="ActorsLeaving"/> and <see cref="ShapesLeaving"/>.
        /// </summary>
        public int ActorsDestroyed { get; private set; }
        public int ShapesFreed { get; private set; }
        public int ActorsLeaving => _actorsLeaving.Count;
        public int ShapesLeaving => _shapesLeaving.Count;

        /// <summary>Main time of the turns that walked, the longest of them, and of that the retirements themselves.</summary>
        public double WalkSeconds { get; private set; }
        public double MaxTurnSeconds { get; private set; }
        public double RetireSeconds { get; private set; }

        /// <summary>The frame of the last retirement started, and that turn's time, in seconds; <see cref="int.MinValue"/> before any.</summary>
        public int LastRetiredFrame { get; private set; } = int.MinValue;
        public double LastRetiringTurnSeconds { get; private set; }

        /// <summary>How many turns were held for <paramref name="hold"/>.</summary>
        public int HeldFor(EmptyOwnerHold hold) => _holds[(int)hold];

        /// <summary>How many fragments looked at were passed over as <paramref name="pass"/>.</summary>
        public int PassedAs(EmptyOwnerPass pass) => _passes[(int)pass];

        /// <summary>How many candidates were refused at the application as <paramref name="pass"/>.</summary>
        public int RefusedAs(EmptyOwnerPass pass) => _refusals[(int)pass];

        /// <summary>Why the last turn was held, or <see cref="EmptyOwnerHold.None"/> when it walked.</summary>
        public EmptyOwnerHold LastHold { get; private set; }

        /// <summary>
        /// What a turn that starts a retirement is expected to cost, in seconds: the walk, the checks and the start of
        /// one retirement. The seed until one was measured, then the median of the last five measured.
        /// </summary>
        public double ExpectedSeconds => _costs.Count == 0 ? Settings.seedExpectedSeconds : _costs.ExpectedSeconds;

        /// <summary>One frame's turn: see the class notes for what it looks at and when.</summary>
        public void Step()
        {
            // What was started is followed to its end whatever the settings are now: observation only -- the engine and
            // the shapes' own holds do the releasing.
            FollowLeaving();

            CutEmptyOwnerCollectionSettings settings = Settings;
            if (!settings.enabled || !settings.IsValid || !_world.IsReady || _world.IsEnding || _world.TerminationRequested)
            {
                return;
            }

            Turns++;
            EmptyOwnerHold hold = HoldNow(in settings);
            LastHold = hold;
            if (hold != EmptyOwnerHold.None)
            {
                _holds[(int)hold]++;
                Remember();
                return;
            }

            long begin = Stopwatch.GetTimestamp();
            // Keep the revision before our own retirements. The next collection still has to reflect them, so
            // that next frame is a structural frame too and must not start another retirement beside it.
            Remember();
            WalkedTurns++;
            int examined = 0;
            int retired = 0;
            while (examined < settings.examinePerFrame && retired < settings.retirePerFrame)
            {
                if (_cursor >= _round.Count)
                {
                    if (examined > 0 || !StartRound())
                    {
                        // The round ended in this turn (the next begins in a later one), or there is nothing to walk.
                        break;
                    }
                }

                LogicalFragmentId fragment = _round[_cursor++];
                examined++;
                Examined++;
                EmptyOwnerPass pass = Examine(fragment, out EmptyOwnerCandidate candidate);
                if (pass != EmptyOwnerPass.Candidate)
                {
                    _passes[(int)pass]++;
                }
                else
                {
                    Candidates++;
                    if (TryApply(in candidate, out _))
                    {
                        retired++;
                    }
                }

                if (_cursor >= _round.Count)
                {
                    Rounds++;
                }
            }

            double spent = Since(begin);
            WalkSeconds += spent;
            MaxTurnSeconds = Math.Max(MaxTurnSeconds, spent);
            if (retired > 0)
            {
                // Only a turn that started a retirement is a sample of what such a turn costs.
                _costs.Add(spent);
                LastRetiringTurnSeconds = spent;
            }

        }

        /// <summary>
        /// Looks at one fragment as the walk does, and gives the candidate it is when it is one. Reads only; nothing is
        /// counted. For a caller that chooses now and applies later (<see cref="TryApply"/>).
        /// </summary>
        public bool TryChoose(LogicalFragmentId fragment, out EmptyOwnerCandidate candidate)
        {
            return Examine(fragment, out candidate) == EmptyOwnerPass.Candidate;
        }

        /// <summary>
        /// Retires one chosen fragment by the existing single retirement (see the class notes), after asking again
        /// everything it was chosen for and that its owner and its shape are still the ones chosen. False, changing
        /// nothing, with what it is now, otherwise.
        /// </summary>
        public bool TryApply(in EmptyOwnerCandidate candidate, out EmptyOwnerPass refusal)
        {
            refusal = EmptyOwnerPass.Candidate;
            if (!candidate.IsSet || !_world.IsReady || _world.IsEnding || _world.TerminationRequested)
            {
                refusal = EmptyOwnerPass.NotLive;
            }
            else
            {
                EmptyOwnerPass now = Examine(candidate.Fragment, out EmptyOwnerCandidate standing);
                if (now != EmptyOwnerPass.Candidate)
                {
                    refusal = now;
                }
                else if (!ReferenceEquals(standing.Owner, candidate.Owner) || !ReferenceEquals(standing.Shape, candidate.Shape))
                {
                    refusal = EmptyOwnerPass.Changed;
                }
            }

            if (refusal != EmptyOwnerPass.Candidate)
            {
                Refused++;
                _refusals[(int)refusal]++;
                return false;
            }

            long begin = Stopwatch.GetTimestamp();
            using (s_retire.Auto())
            {
                PhysicsFragmentOwner owner = candidate.Owner;
                bool constraint = owner.BuildingWorldConstraint != null;
                int convexes = candidate.Shape.ConvexCount;
                GameObject actor = owner.Root;
                if (!_world.Ledger.Retire(candidate.Fragment))
                {
                    refusal = EmptyOwnerPass.NotLive;
                    Refused++;
                    _refusals[(int)refusal]++;
                    return false;
                }

                // The owner leaves the scene here, at once, with its colliders and its constraint; its objects are
                // destroyed by the engine at the end of this frame, and its shape goes back when nothing reads it.
                RetiringFragment = candidate.Fragment;
                try
                {
                    _world.Owners.Retire(candidate.Fragment);
                }
                finally
                {
                    RetiringFragment = default;
                }
                _world.Geometry?.Forget(candidate.Fragment);
                Retired++;
                ConstraintsRetired += constraint ? 1 : 0;
                ConvexesRetired += convexes;
                LastRetiredFrame = Time.frameCount;
                if (actor != null)
                {
                    _actorsLeaving.Add(actor);
                }

                _shapesLeaving.Add(candidate.Shape);
            }

            RetireSeconds += Since(begin);
            return true;
        }

        /// <summary>The collection's record, in words. Log text only.</summary>
        public string Describe()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var text = new System.Text.StringBuilder(512);
            text.Append("empty owner collection ").Append(Settings.enabled ? "on" : "off")
                .Append(": turns ").Append(Turns).Append(" (walked ").Append(WalkedTurns).Append(", rounds ").Append(Rounds)
                .Append("), looked at ").Append(Examined).Append(", candidates ").Append(Candidates)
                .Append(", retirements started ").Append(Retired).Append(", candidates not applied ").Append(Refused);
            text.Append("; held:");
            for (int h = 1; h < (int)EmptyOwnerHold.Count; h++)
            {
                text.Append(' ').Append(((EmptyOwnerHold)h).ToString()).Append(' ').Append(_holds[h]);
            }

            text.Append("; passed over:");
            for (int p = 1; p < (int)EmptyOwnerPass.Count; p++)
            {
                text.Append(' ').Append(((EmptyOwnerPass)p).ToString()).Append(' ').Append(_passes[p]);
                if (_refusals[p] > 0)
                {
                    text.Append(" (+").Append(_refusals[p]).Append(" at the application)");
                }
            }

            text.Append("; time: walked turns ").Append((WalkSeconds * 1000.0).ToString("F3", inv)).Append(" ms in all, the longest ")
                .Append((MaxTurnSeconds * 1000.0).ToString("F3", inv)).Append(" ms, of which the retirements ")
                .Append((RetireSeconds * 1000.0).ToString("F3", inv)).Append(" ms; expected now ")
                .Append((ExpectedSeconds * 1000.0).ToString("F3", inv)).Append(" ms (").Append(_costs.Count).Append(" measured)");
            text.Append("; released: actors destroyed ").Append(ActorsDestroyed).Append(" (leaving ").Append(_actorsLeaving.Count)
                .Append("), shapes freed ").Append(ShapesFreed).Append(" (leaving ").Append(_shapesLeaving.Count)
                .Append("), building constraints ").Append(ConstraintsRetired).Append(", convexes ").Append(ConvexesRetired)
                .Append("; the last retirement at frame ").Append(LastRetiredFrame).Append(", its turn ")
                .Append((LastRetiringTurnSeconds * 1000.0).ToString("F3", inv)).Append(" ms");
            return text.ToString();
        }

        // Whether this frame may bear a turn: what the world's own gate asks of any optional work first (the notes, a
        // garbage collection, a cut in flight), then what this frame did to the structure and to the display's room,
        // then the budget -- with this turn's own expected cost, not another work's.
        private EmptyOwnerHold HoldNow(in CutEmptyOwnerCollectionSettings settings)
        {
            switch (_world.FrameEventsHold(out _))
            {
                case CutFrameHold.HeavyWork:
                    return EmptyOwnerHold.HeavyWork;
                case CutFrameHold.GarbageCollection:
                    return EmptyOwnerHold.GarbageCollection;
                case CutFrameHold.CutInFlight:
                    return EmptyOwnerHold.CutInFlight;
            }

            if (_world.Ledger.Revision != _revisionSeen)
            {
                return EmptyOwnerHold.StructureChanged;
            }

            if (RoomNow() != _roomSeen)
            {
                return EmptyOwnerHold.DisplayRoomGrown;
            }

            if (_world.Display != null && _world.Display.LastCompactionFrame == Time.frameCount)
            {
                return EmptyOwnerHold.CompactionRan;
            }

            if (RemainingMainSeconds() < settings.reserveSeconds + (2.0 * ExpectedSeconds))
            {
                return EmptyOwnerHold.BudgetShort;
            }

            return EmptyOwnerHold.None;
        }

        // The display's growths and replacements, as one number that moves when any of them does.
        private long RoomNow()
        {
            VpLogicalCutDisplay display = _world.Display;
            long room = _world.References != null ? _world.References.GrowthCount : 0;
            if (display != null)
            {
                room += (long)display.RoomGrowths + display.SnapshotRegrowths + display.GpuReplacements + display.GpuGrowthCount;
            }

            return room;
        }

        // What this turn saw before it started work: its own retirements also invalidate the next collection.
        private void Remember()
        {
            _revisionSeen = _world.Ledger.Revision;
            _roomSeen = RoomNow();
        }

        private EmptyOwnerPass Examine(LogicalFragmentId fragment, out EmptyOwnerCandidate candidate)
        {
            candidate = default;
            CutDag dag = _world.Geometry;
            LogicalCutLedger ledger = _world.Ledger;
            PhysicsOwnerRegistry owners = _world.Owners;

            // Settled as empty by the commit of the cut that produced it, with the frame it would be cut in: the one
            // evidence. Everything else -- no geometry yet, a failed one, a geometry of any size -- is not that.
            if (dag == null || !dag.HasNoGeometry(fragment) || !dag.TryGetGeometryFrame(fragment, out _))
            {
                return EmptyOwnerPass.NotEmpty;
            }

            if (!ledger.IsCurrentTarget(fragment))
            {
                return EmptyOwnerPass.NotLive;
            }

            if (ledger.TryGetActiveOperation(fragment, out _) || owners.TryGetProvisionalOf(fragment, out _))
            {
                return EmptyOwnerPass.CutUnderWay;
            }

            // A final owner of its own, in the scene, that its body resolves to: not a group's member and not a
            // display-only one, whose physics is somebody else's to end.
            if (!owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.IsWithdrawn || owner.IsReleased
                || owner.IsDisplayOnly || owner.IsFused || owner.Body == null || owner.Root == null
                || !owners.TryResolveFragment(owner.Body, out LogicalFragmentId resolved, out float side)
                || resolved != fragment || side != 0f)
            {
                return EmptyOwnerPass.NoOwnPhysics;
            }

            PhysicsOwnerShape shape = owner.Shape;
            if (shape == null || shape.IsFreed || shape.ConvexCount <= 0)
            {
                return EmptyOwnerPass.NoConvex;
            }

            if (_world.Display != null && _world.Display.IsDrawnInsideAggregate(fragment))
            {
                return EmptyOwnerPass.InsideAggregate;
            }

            candidate = new EmptyOwnerCandidate(fragment, owner, shape);
            return EmptyOwnerPass.Candidate;
        }

        /// <summary>A new round: the fragments the owner registry has now. False when it has none.</summary>
        private bool StartRound()
        {
            _round.Clear();
            _world.Owners.CopyFragmentsTo(_round);
            _cursor = 0;
            return _round.Count > 0;
        }

        // The actors and the shapes of the retirements started, until the engine has destroyed the one and the other
        // has gone back: counted once each, then forgotten.
        private void FollowLeaving()
        {
            for (int i = _actorsLeaving.Count - 1; i >= 0; i--)
            {
                if (_actorsLeaving[i] == null)
                {
                    ActorsDestroyed++;
                    _actorsLeaving.RemoveAt(i);
                }
            }

            for (int i = _shapesLeaving.Count - 1; i >= 0; i--)
            {
                if (_shapesLeaving[i].IsFreed)
                {
                    ShapesFreed++;
                    _shapesLeaving.RemoveAt(i);
                }
            }
        }

        private static double Since(long begin)
        {
            return (double)(Stopwatch.GetTimestamp() - begin) / Stopwatch.Frequency;
        }
    }
}
