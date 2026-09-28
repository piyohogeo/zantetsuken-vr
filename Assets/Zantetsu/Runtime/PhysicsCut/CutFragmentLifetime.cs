using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// How <see cref="CutFragmentLifetime"/> chooses and paces its retirements. Every value is the caller's; the profile
    /// holds the provisional ones (DESIGN 7.10 leaves the method, the thresholds and the pace to the implementation).
    /// </summary>
    public readonly struct CutFragmentLifetimeSettings
    {
        public CutFragmentLifetimeSettings(
            bool enabled, int threshold, float minDistance, int examinePerFrame, int retirePerFrame,
            double minRemainingMainSeconds, double maxStepSeconds)
        {
            this.enabled = enabled;
            this.threshold = threshold;
            this.minDistance = minDistance;
            this.examinePerFrame = examinePerFrame;
            this.retirePerFrame = retirePerFrame;
            this.minRemainingMainSeconds = minRemainingMainSeconds;
            this.maxStepSeconds = maxStepSeconds;
        }

        /// <summary>Off: no candidate is looked at and no retirement is started. What was started goes on regardless.</summary>
        public readonly bool enabled;

        /// <summary>Retirements start only while more live target pieces than this remain.</summary>
        public readonly int threshold;

        /// <summary>A candidate's bounds are at least this far, in metres, from the viewer.</summary>
        public readonly float minDistance;

        /// <summary>Candidates looked at per frame, at most.</summary>
        public readonly int examinePerFrame;

        /// <summary>Retirements started per frame, at most.</summary>
        public readonly int retirePerFrame;

        /// <summary>A frame with less of its Main budget left than this looks at nothing.</summary>
        public readonly double minRemainingMainSeconds;

        /// <summary>A frame's step stops looking once it has taken this long.</summary>
        public readonly double maxStepSeconds;

        public bool IsValid =>
            threshold >= 0 && minDistance >= 0f && !float.IsNaN(minDistance) && !float.IsInfinity(minDistance)
            && examinePerFrame > 0 && retirePerFrame > 0 && minRemainingMainSeconds >= 0.0 && maxStepSeconds > 0.0;
    }

    /// <summary>
    /// DESIGN 7.10's optional lifetime of live logical fragments, for the pieces a character's cuts leave behind: once
    /// more of them are alive than <see cref="CutFragmentLifetimeSettings.threshold"/>, pieces far from the viewer and
    /// outside both eyes' views are retired -- from the display, the physics and the hit targets alike -- a few a frame,
    /// and only while the frame's Main budget has room.
    /// <para>
    /// **Which pieces.** Only a fragment a cut produced (it has an origin) whose lineage goes back to a root marked with
    /// <see cref="MarkLineage"/> -- a character's. An uncut character, a building and a prop are never looked at.
    /// Distance and view are judged on the piece's bounds -- its physics shape's box, placed where its owner is -- never
    /// on its actor's origin alone; the view is the two eyes' frustums of the view camera, or the one of a mono camera.
    /// </para>
    /// <para>
    /// **How it retires.** The existing single retirement, in the order the abort already uses: the ledger first (a
    /// retired fragment is no longer a target, and any result that arrives for it later is stale), then its physics
    /// owner, which takes the actor, its colliders and its hit shapes away and gives back only what nothing else still
    /// uses. The display lets the piece go at its next collection and gives back the geometry references then, which
    /// retires the index range; the geometry DAG forgets what the piece would have been cut from. Nothing is made for
    /// it: no operation, no pending cut, no transaction. Right before, it checks that the piece is live, has no cut of
    /// its own under way, still has its owner here, and is not drawn inside an aggregate -- where retiring it would
    /// stop the display. Another fragment's cut, or this one's geometry not being finished, is no reason to wait.
    /// </para>
    /// <para>
    /// **How much.** Nothing is looked at while the world holds no more owners than the threshold, or while the frame's
    /// Main budget is short. Otherwise it walks one round of the owned fragments, a few per frame, counting the target
    /// pieces as it goes and -- while the last finished round counted more than the threshold -- retiring candidates on
    /// the way; a round's count is settled when it ends and the next round begins. A step stops at its per-frame counts
    /// or its time, whichever comes first. It never runs
    /// to make room, never waits for anything, and no cut waits for it.
    /// </para>
    /// </summary>
    public sealed class CutFragmentLifetime
    {
        private readonly CutWorldRoot _world;
        private readonly HashSet<LogicalFragmentId> _lineageRoots = new HashSet<LogicalFragmentId>();
        private readonly List<LogicalFragmentId> _round = new List<LogicalFragmentId>();
        private readonly Plane[] _eyePlanes = new Plane[2 * VpInstanceCulling.EyePlaneCount];
        private int _cursor;
        private int _roundTargets;

        internal CutFragmentLifetime(CutWorldRoot world, in CutFragmentLifetimeSettings settings)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            Settings = settings;
            RemainingMainSeconds = () => CutPhysicsStep.RemainingMainSeconds;
        }

        public CutFragmentLifetimeSettings Settings { get; set; }

        /// <summary>What is left of this frame's Main budget. The product's frame clock; a test gives its own.</summary>
        public Func<double> RemainingMainSeconds { get; set; }

        /// <summary>The camera whose eyes and position the pieces are judged against; null means <c>Camera.main</c>.</summary>
        public Camera ViewCamera { get; set; }

        /// <summary>Live target pieces as the last finished round counted them, less those retired since.</summary>
        public int TargetCount { get; private set; }

        public int Steps { get; private set; }
        public int BudgetSkips { get; private set; }
        /// <summary>Rounds finished: each walked every fragment that had an owner when it began.</summary>
        public int Rounds { get; private set; }
        public int Examined { get; private set; }
        public int Candidates { get; private set; }
        public int Retired { get; private set; }
        public int Refused { get; private set; }

        /// <summary>Pieces looked at and passed over, by why: no longer a live, owned piece with a box; too near; seen.</summary>
        public int PassedUnusable { get; private set; }
        public int PassedNear { get; private set; }
        public int PassedSeen { get; private set; }

        /// <summary>The farthest a looked-at piece's bounds were from the viewer, in metres, since the start.</summary>
        public float FarthestExamined { get; private set; }

        /// <summary>Main time spent in steps that looked at something, and the longest one.</summary>
        public double StepSeconds { get; private set; }
        public double MaxStepSeconds { get; private set; }

        /// <summary>Main time spent in the retirements themselves, a part of <see cref="StepSeconds"/>.</summary>
        public double RetireSeconds { get; private set; }

        /// <summary>Why the last refused retirement was refused. Log text only.</summary>
        public string LastRefusal { get; private set; }

        /// <summary>Makes the pieces of <paramref name="root"/>'s lineage targets. The root itself never is.</summary>
        public void MarkLineage(LogicalFragmentId root)
        {
            if (root.IsSet)
            {
                _lineageRoots.Add(root);
            }
        }

        /// <summary>
        /// Whether this fragment is a target: produced by a cut, and of a marked lineage. Reads the ledger's origins
        /// only, changing nothing.
        /// </summary>
        public bool IsTarget(LogicalFragmentId fragment)
        {
            LogicalCutLedger ledger = _world.Ledger;
            if (ledger == null || !ledger.TryGetOrigin(fragment, out _, out _))
            {
                return false;
            }

            LogicalFragmentId at = fragment;
            int steps = ledger.OperationCount + 1;
            for (int step = 0; step <= steps; step++)
            {
                if (!ledger.TryGetOrigin(at, out CutOperationId origin, out _))
                {
                    return _lineageRoots.Contains(at);
                }

                if (!ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                {
                    return false;
                }

                at = cut.source;
            }

            return false;
        }

        /// <summary>One frame's turn: see the class notes for what it looks at and when.</summary>
        public void Step()
        {
            CutFragmentLifetimeSettings settings = Settings;
            if (!settings.enabled || !settings.IsValid || !_world.IsReady || _world.IsEnding || _world.TerminationRequested)
            {
                return;
            }

            Steps++;

            // No more owners than the threshold: no more targets either, and nothing is looked at.
            if (_world.Owners.Count <= settings.threshold)
            {
                return;
            }

            if (RemainingMainSeconds() < settings.minRemainingMainSeconds)
            {
                BudgetSkips++;
                return;
            }

            long begin = Stopwatch.GetTimestamp();
            if (_cursor >= _round.Count)
            {
                StartRound();
            }

            // One walk does both: every piece looked at is counted for this round, and while the last round's count is
            // over the threshold it is judged as a candidate too. The count is settled when the round ends, so it keeps
            // moving whatever it came to -- a round is never left unfinished because the count was low.
            Camera camera = ViewCamera != null ? ViewCamera : Camera.main;
            bool retiring = TargetCount > settings.threshold && camera != null;
            int eyes = retiring ? VpInstanceCulling.GetEyePlanes(camera, _eyePlanes) : 0;
            Vector3 viewer = retiring ? camera.transform.position : default;
            int examined = 0;
            int retired = 0;
            while (_cursor < _round.Count && examined < settings.examinePerFrame && Since(begin) < settings.maxStepSeconds)
            {
                LogicalFragmentId fragment = _round[_cursor++];
                examined++;
                Examined++;
                if (!_world.Ledger.IsCurrentTarget(fragment) || !IsTarget(fragment))
                {
                    continue;
                }

                _roundTargets++;
                if (!retiring || retired >= settings.retirePerFrame || TargetCount <= settings.threshold
                    || !IsCandidate(fragment, viewer, eyes, settings.minDistance))
                {
                    continue;
                }

                Candidates++;
                if (TryRetire(fragment, out _))
                {
                    retired++;
                    _roundTargets--;
                }
            }

            if (_cursor >= _round.Count)
            {
                TargetCount = _roundTargets;
                Rounds++;
            }

            double spent = Since(begin);
            StepSeconds += spent;
            MaxStepSeconds = Math.Max(MaxStepSeconds, spent);
        }

        /// <summary>
        /// Retires one live target piece by the existing single retirement (see the class notes), after checking that it
        /// is live, has no cut of its own under way, still has its owner here and is not drawn inside an aggregate. False,
        /// changing nothing, with the reason, otherwise.
        /// </summary>
        public bool TryRetire(LogicalFragmentId fragment, out string refusal)
        {
            refusal = Refusal(fragment);
            if (refusal != null)
            {
                Refused++;
                LastRefusal = refusal;
                return false;
            }

            long begin = Stopwatch.GetTimestamp();
            if (!_world.Ledger.Retire(fragment))
            {
                refusal = "the ledger did not retire it";
                Refused++;
                LastRefusal = refusal;
                return false;
            }

            _world.Owners.Retire(fragment);
            _world.Geometry?.Forget(fragment);
            Retired++;
            TargetCount = Math.Max(0, TargetCount - 1);
            RetireSeconds += Since(begin);
            return true;
        }

        private string Refusal(LogicalFragmentId fragment)
        {
            if (!_world.IsReady || _world.IsEnding || _world.TerminationRequested)
            {
                return "the world is not taking changes";
            }

            LogicalCutLedger ledger = _world.Ledger;
            if (!ledger.IsCurrentTarget(fragment))
            {
                return "not live";
            }

            if (!IsTarget(fragment))
            {
                return "not a piece of a marked lineage";
            }

            if (ledger.TryGetActiveOperation(fragment, out _) || _world.Owners.TryGetProvisionalOf(fragment, out _))
            {
                return "a cut of it is under way";
            }

            // The authority to retire it is this world's: its owner is here, is the one its body resolves to, and is
            // still in the scene.
            if (!_world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.IsWithdrawn || owner.IsReleased
                || owner.Body == null || !_world.Owners.TryResolveFragment(owner.Body, out LogicalFragmentId resolved, out _)
                || resolved != fragment)
            {
                return "no owner of its own here";
            }

            if (_world.Display != null && _world.Display.IsDrawnInsideAggregate(fragment))
            {
                return "drawn inside an aggregate";
            }

            return null;
        }

        private bool IsCandidate(LogicalFragmentId fragment, Vector3 viewer, int eyes, float minDistance)
        {
            if (!_world.Ledger.IsCurrentTarget(fragment) || _world.Ledger.TryGetActiveOperation(fragment, out _)
                || !_world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.IsWithdrawn
                || owner.Root == null || owner.Shape == null || !owner.Shape.TryLocalBounds(out float3 lo, out float3 hi))
            {
                PassedUnusable++;
                return false;
            }

            Bounds world = WorldBounds(owner.Root.transform.localToWorldMatrix * (Matrix4x4)owner.Shape.LocalToOwner, lo, hi);
            float squared = world.SqrDistance(viewer);
            FarthestExamined = Mathf.Max(FarthestExamined, Mathf.Sqrt(squared));
            if (squared < minDistance * minDistance)
            {
                PassedNear++;
                return false;
            }

            if (VpInstanceCulling.MayIntersectAnyEye(world, _eyePlanes, eyes))
            {
                PassedSeen++;
                return false;
            }

            return true;
        }

        /// <summary>A new round: the fragments the owner registry has now, walked a few a frame and counted as it goes.</summary>
        private void StartRound()
        {
            _round.Clear();
            _world.Owners.CopyFragmentsTo(_round);
            _cursor = 0;
            _roundTargets = 0;
        }

        private static Bounds WorldBounds(Matrix4x4 localToWorld, float3 lo, float3 hi)
        {
            Vector3 centre = localToWorld.MultiplyPoint3x4((Vector3)((lo + hi) * 0.5f));
            Vector3 half = (Vector3)((hi - lo) * 0.5f);
            var extents = new Vector3(
                Mathf.Abs(localToWorld.m00) * half.x + Mathf.Abs(localToWorld.m01) * half.y + Mathf.Abs(localToWorld.m02) * half.z,
                Mathf.Abs(localToWorld.m10) * half.x + Mathf.Abs(localToWorld.m11) * half.y + Mathf.Abs(localToWorld.m12) * half.z,
                Mathf.Abs(localToWorld.m20) * half.x + Mathf.Abs(localToWorld.m21) * half.y + Mathf.Abs(localToWorld.m22) * half.z);
            return new Bounds(centre, extents * 2f);
        }

        private static double Since(long begin)
        {
            return (double)(Stopwatch.GetTimestamp() - begin) / Stopwatch.Frequency;
        }
    }
}
