using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The cut scene of the draw comparison (2026-10-09, before the plugin's route is taken as a default, D-215): a
    /// fixed sequence of cut states of two bodies, each held still and placed by this code, so that the same states can
    /// be pictured from the same poses by two routes and the pictures compared -- which a city walk cannot give (its
    /// hits differ from run to run). It is a picture check, never a performance sample.
    /// <para>
    /// **The stages**, each pictured: the two bodies whole; the first body cut level with the cut's geometry held back
    /// (the sides clipped, the cut face drawn as provisional caps, the casters two-sided); its pieces moved and
    /// turned, still held; the geometry committed, and then the second body cut upright, committed and its pieces
    /// put apart; one piece of the first cut again, held; that committed; one piece retired.
    /// </para>
    /// <para>
    /// **The second body is cut only once the first body's cut has committed** (2026-10-09, after the first run in a
    /// Player). A placed prop is taken into the world by its first cut -- its display geometry is appended then -- and
    /// the storage takes no new geometry while any cut's output is outstanding, which the first body's is while its
    /// geometry is held back: the prop's first cut is then not accepted, and the prop stays a candidate until that cut
    /// has committed (until 2026-10-09 it was refused for good, which is how the first run in a Player ended). A piece
    /// already in the world is cut while another cut is outstanding as ever.
    /// </para>
    /// <para>
    /// **Nothing here is left to the simulation.** A cut is asked by one synthetic sweep given to the ordinary hit
    /// detector (the acceptance and the driver are the product's; the plane is the sweep's, in the world); every piece
    /// a cut publishes is made kinematic and put where this code says, from the cut body's own frame -- never where the
    /// frames between the publication and the picture left it. The budget that could defer a cut or a retirement is
    /// taken off for the sequence and put back.
    /// </para>
    /// </summary>
    public static class SandboxDrawCompareCutScene
    {
        /// <summary>After the measurement (mode unity only): the cut scene's stages, pictured. Not a performance sample.</summary>
        public const string Argument = "-zantetsuDrawCompareCutScene";

        /// <summary>The stages' names, in order.</summary>
        public static readonly string[] StageNames =
        {
            "c0-whole", "c1-cut-held", "c2-cut-held-moved", "c3-committed", "c4-recut-held", "c5-recut-committed", "c6-retired",
        };

        /// <summary>A body of the scene: its frame (what its pieces are placed from) and what of the world it fills.</summary>
        public sealed class Body
        {
            public string name;
            public Vector3 position;
            public Quaternion rotation;
            public Bounds bounds;
        }

        /// <summary>What the stages are given by whoever runs them: the world and its detector, the hold, the pictures, the record.</summary>
        public sealed class Io
        {
            public CutWorldRoot world;
            public SlashHitDetector detector;

            /// <summary>Holds the cuts' finished geometry back (true) or lets it through, the held first (false).</summary>
            public Action<bool> hold;

            /// <summary>How many finished geometry works are held now.</summary>
            public Func<int> held;

            /// <summary>Takes the pictures of a stage, under its name.</summary>
            public Func<string, IEnumerator> pictures;

            public Action<string> log;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static long s_slash = 950000;

        // The geometry pool's hold in a Player, as SandboxPropSlashPlayerCheck's: finished work is kept from the
        // collection while holding, then handed on first, as it came back. Installed only when the argument is given
        // (this scene's or the split scene's, which holds a cut's geometry back as this one does).
        private sealed class HeldPool : IWorkExecutor, IDisposable
        {
            private readonly IWorkExecutor _inner;
            private readonly List<(IDispatchWork work, WorkCompletion completion)> _held = new List<(IDispatchWork, WorkCompletion)>();

            internal HeldPool(IWorkExecutor inner) { _inner = inner; }

            internal bool Holding { get; set; }
            internal int HoldingCount => _held.Count;
            public WorkDestination Destination => _inner.Destination;
            public int Capacity => _inner.Capacity;
            public int Held => _inner.Held;
            public bool CanAccept => _inner.CanAccept;
            public bool TryAccept(IDispatchWork work) => _inner.TryAccept(work);
            public void BeginAccepted(IDispatchWork work) => _inner.BeginAccepted(work);

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                if (!Holding && _held.Count > 0)
                {
                    (work, completion) = _held[0];
                    _held.RemoveAt(0);
                    return true;
                }

                if (!_inner.TryTakeFinished(out work, out completion))
                {
                    return false;
                }

                if (!Holding)
                {
                    return true;
                }

                _held.Add((work, completion));
                work = null;
                completion = default;
                return false;
            }

            public void CloseForNewWork() => _inner.CloseForNewWork();
            public bool StopAndConfirm(int timeoutMilliseconds) => _inner.StopAndConfirm(timeoutMilliseconds) && _held.Count == 0;
            public void Dispose() => (_inner as IDisposable)?.Dispose();
        }

        private static HeldPool s_hold;

        /// <summary>Whether this launch's world was given the hold (the argument was there before the scene loaded).</summary>
        public static bool HoldInstalled => s_hold != null;

        /// <summary>The Player's hold, for <see cref="Io.hold"/> and <see cref="Io.held"/>.</summary>
        public static void SetHold(bool holding)
        {
            if (s_hold != null) s_hold.Holding = holding;
        }

        public static int HeldNow => s_hold != null ? s_hold.HoldingCount : 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallHold()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Application.isEditor || (Array.IndexOf(arguments, Argument) < 0 && Array.IndexOf(arguments, SandboxDrawCompareSplitScene.Argument) < 0) || CutWorldRoot.nextWorldExecutors != null)
            {
                return;
            }

            // Not holding until a stage asks: the comparison's own frames before it are as without the argument.
            CutWorldRoot.nextWorldExecutors = destination => destination == WorkDestination.GeometryPool
                ? s_hold = new HeldPool(WorkerPoolExecutor.GeometryPool(4))
                : null;
        }

        /// <summary>
        /// The two props of a city the scene is made of, for a Player: placed props that become cut targets at their
        /// first cut (never buildings), with something to cut -- 0.8 m or more in height and in their narrower
        /// horizontal size, 6 m or less in every size, and their longest size no more than four times their shortest (a
        /// pole with an arm fills little of its box) -- standing 3.5 to 15 m from <paramref name="eye"/> on the ground
        /// plane; the nearest, then the nearest whose box stands 3 m or more clear of the first's (no sweep through one
        /// reaches the other or its pieces). Where none stands in that range, the
        /// nearest of any distance. Ties by name, so that two launches of one Player choose the same two.
        /// </summary>
        public static bool TryChoose(IReadOnlyList<PlayableCityCuttable> all, Vector3 eye, out PlayableCityCuttable first, out PlayableCityCuttable second, out string described)
        {
            first = second = null;
            var fit = new List<(PlayableCityCuttable prop, Bounds bounds, float distance, bool inRange)>();
            int props = 0;
            foreach (PlayableCityCuttable c in all)
            {
                if (c == null || c.building || !c.deferUntilCut || c.target == null || !c.target.gameObject.activeInHierarchy) continue;
                props++;
                if (!TryPlacedBounds(c, out Bounds b)) continue;
                Vector3 size = b.size;
                float longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z)), shortest = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
                if (size.y < 0.8f || Mathf.Min(size.x, size.z) < 0.8f || longest > 6f || longest > 4f * shortest) continue;
                float distance = Flat(b.center - eye).magnitude;
                fit.Add((c, b, distance, distance >= 3.5f && distance <= 15f));
            }

            fit.Sort((x, y) =>
            {
                if (x.inRange != y.inRange) return x.inRange ? -1 : 1;
                int byDistance = x.distance.CompareTo(y.distance);
                return byDistance != 0 ? byDistance : string.CompareOrdinal(x.prop.target.name, y.prop.target.name);
            });
            Bounds firstBounds = default;
            foreach ((PlayableCityCuttable prop, Bounds bounds, float distance, bool inRange) in fit)
            {
                if (first == null)
                {
                    first = prop;
                    firstBounds = bounds;
                }
                else if (FlatGap(bounds, firstBounds) >= 3f)
                {
                    second = prop;
                    break;
                }
            }

            described = "placed props that are cut targets at their first cut " + props + ", with something to cut " + fit.Count
                + (first != null ? "; first " + first.target.name + (second != null ? ", second " + second.target.name : ", NO second 3 m or more clear of it") : "; NONE");
            return first != null && second != null;
        }

        /// <summary>The box of the world a placed prop fills: its own colliders', or its renderers' when it has none.</summary>
        public static bool TryPlacedBounds(PlayableCityCuttable prop, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Collider collider in prop.instanceColliders ?? Array.Empty<Collider>())
            {
                if (collider == null || !collider.enabled) continue;
                if (!any) bounds = collider.bounds; else bounds.Encapsulate(collider.bounds);
                any = true;
            }

            if (any) return true;
            foreach (Renderer renderer in prop.instanceRenderers ?? Array.Empty<Renderer>())
            {
                if (renderer == null) continue;
                if (!any) bounds = renderer.bounds; else bounds.Encapsulate(renderer.bounds);
                any = true;
            }

            return any;
        }

        /// <summary>A prop of the city as a body of the scene: its frame is the instance's as placed.</summary>
        public static Body BodyOf(PlayableCityCuttable prop)
        {
            TryPlacedBounds(prop, out Bounds bounds);
            return new Body { name = prop.target.name, position = prop.target.position, rotation = prop.target.rotation, bounds = bounds };
        }

        /// <summary>
        /// Where the eye stands for a body's view and which way it looks (on the ground plane, at the body's middle):
        /// at <paramref name="eye"/> itself -- a place the path stands at -- when the body is 3.5 to 15 m from it, and
        /// otherwise that far back from the body, towards <paramref name="eye"/>, at the same height.
        /// </summary>
        public static void View(Vector3 eye, Bounds body, out Vector3 from, out float yaw)
        {
            Vector3 to = Flat(body.center - eye);
            float distance = to.magnitude;
            from = eye;
            if (distance < 3.5f || distance > 15f)
            {
                Vector3 back = distance > 1e-3f ? -to / distance : Vector3.back;
                float stand = Mathf.Clamp(1.6f * Mathf.Max(body.size.x, Mathf.Max(body.size.y, body.size.z)), 3.5f, 8f);
                from = new Vector3(body.center.x, eye.y, body.center.z) + back * stand;
                to = Flat(body.center - from);
            }

            yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // How far apart two boxes stand on the ground plane (0 when they overlap there).
        private static float FlatGap(Bounds a, Bounds b)
        {
            float dx = Mathf.Max(0f, Mathf.Abs(a.center.x - b.center.x) - a.extents.x - b.extents.x);
            float dz = Mathf.Max(0f, Mathf.Abs(a.center.z - b.center.z) - a.extents.z - b.extents.z);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// The stages, in order. A stage that cannot be made says why in <paramref name="failures"/> and the sequence
        /// ends there (the pictures taken so far stand); <paramref name="states"/> takes one line a stage -- what the
        /// ledger and the display hold and where each piece was put -- by which two runs are known to have pictured the
        /// same states.
        /// </summary>
        public static IEnumerator Stages(Io io, Body first, Body second, List<string> states, List<string> failures)
        {
            CutWorldRoot world = io.world;
            Func<double> driverBudget = world.Driver.RemainingMainSeconds;
            world.Driver.RemainingMainSeconds = () => 1.0;
            var poses = new Dictionary<LogicalFragmentId, (Vector3 position, Quaternion rotation)>();
            try
            {
                yield return Picture(io, 0, states, poses);

                // ---- the first body's cut, its geometry held back -----------------------------------------------------
                io.hold(true);
                var cutA = new List<LogicalCutOperation>();
                yield return Cut(io, first.bounds, Vector3.up, "the first body, level", cutA, failures);
                if (cutA.Count != 1)
                {
                    failures.Add("the level sweep through " + first.name + " published " + cutA.Count + " cuts, not one");
                    yield break;
                }

                LogicalCutOperation a = cutA[0];
                // Which child is the upper one: by where each stands, read once here.
                Sides(world, a, Vector3.up, out LogicalFragmentId upper, out LogicalFragmentId lower);
                io.log("cut scene: children as published: " + first.name + " upper " + Where(world, upper, first) + ", lower " + Where(world, lower, first)
                    + " (each root's distance from its source's frame, where it is put)");
                // The upper piece is lifted clear of the lower one, by enough that -- turned, in the next stage -- no part
                // of it comes within the reach of the sweep that cuts it again: that sweep must meet it alone.
                float lift = Mathf.Max(0.6f, Mathf.Max(first.bounds.size.y * 0.8f, 0.25f * Mathf.Max(first.bounds.size.x, first.bounds.size.z) + 0.4f));
                Place(io, poses, lower, first.position, first.rotation);
                Place(io, poses, upper, first.position + Vector3.up * lift, first.rotation);
                for (int i = 0; i < 3; i++) yield return null;
                if (io.held() == 0) failures.Add("no finished geometry was held at the first cut: the pictures of the held stages are not of provisional caps");
                if (State(world, a.id) == LogicalCutOperationState.Completed) failures.Add("the first cut was complete while its geometry was to be held");
                if (world.Display.CapRecordCount == 0) failures.Add("the display holds no cap at the held first cut");
                yield return Picture(io, 1, states, poses);

                // ---- its pieces moved and turned, still held -----------------------------------------------------------
                Vector3 pivotA = first.bounds.center + Vector3.up * lift;
                PlaceAbout(io, poses, upper, pivotA, Quaternion.AngleAxis(25f, Vector3.forward), Vector3.up * (lift * 0.4f));
                PlaceAbout(io, poses, lower, first.bounds.center, Quaternion.AngleAxis(15f, Vector3.up), Vector3.zero);
                for (int i = 0; i < 3; i++) yield return null;
                yield return Picture(io, 2, states, poses);

                // ---- the commit; then the second body, cut with nothing outstanding and committed ------------------------
                io.hold(false);
                yield return UntilComplete(io, new[] { a.id }, "the first body's cut", failures);
                if (failures.Count > 0) yield break;
                var cutB = new List<LogicalCutOperation>();
                yield return Cut(io, second.bounds, Vector3.right, "the second body, upright", cutB, failures);
                if (cutB.Count != 1)
                {
                    failures.Add("the upright sweep through " + second.name + " published " + cutB.Count + " cuts, not one");
                    yield break;
                }

                LogicalCutOperation b = cutB[0];
                Sides(world, b, Vector3.right, out LogicalFragmentId right, out LogicalFragmentId left);
                io.log("cut scene: children as published: " + second.name + " right " + Where(world, right, second) + ", left " + Where(world, left, second));
                float aside = Mathf.Max(0.15f, second.bounds.size.x * 0.35f);
                Place(io, poses, left, second.position, second.rotation);
                Place(io, poses, right, second.position + Vector3.right * aside, second.rotation);
                PlaceAbout(io, poses, right, second.bounds.center + Vector3.right * aside, Quaternion.AngleAxis(20f, Vector3.right), Vector3.right * (aside * 0.4f));
                yield return UntilComplete(io, new[] { b.id }, "the second body's cut", failures);
                yield return Picture(io, 3, states, poses);

                // ---- one piece cut again, held ---------------------------------------------------------------------------
                io.hold(true);
                if (!TryWorldBounds(world, upper, out Bounds again))
                {
                    failures.Add("the moved piece of " + first.name + " has no collider to sweep through");
                    yield break;
                }

                var cutC = new List<LogicalCutOperation>();
                yield return Cut(io, again, Vector3.right, "the moved piece, upright", cutC, failures);
                if (cutC.Count != 1)
                {
                    failures.Add("the upright sweep through the moved piece published " + cutC.Count + " cuts, not one");
                    yield break;
                }

                LogicalCutOperation c = cutC[0];
                if (c.source != upper) failures.Add("the added cut's source is not the moved piece");
                (Vector3 position, Quaternion rotation) from = poses[upper];
                Sides(world, c, Vector3.right, out LogicalFragmentId further, out LogicalFragmentId nearer);
                poses.Remove(upper);   // cut: its children stand for it
                Place(io, poses, nearer, from.position, from.rotation);
                Place(io, poses, further, from.position + Vector3.right * Mathf.Max(0.12f, again.size.x * 0.35f), from.rotation);
                for (int i = 0; i < 3; i++) yield return null;
                if (State(world, c.id) == LogicalCutOperationState.Completed) failures.Add("the added cut was complete while its geometry was to be held");
                yield return Picture(io, 4, states, poses);

                io.hold(false);
                yield return UntilComplete(io, new[] { c.id }, "the added cut", failures);
                yield return Picture(io, 5, states, poses);

                // ---- one piece retired -------------------------------------------------------------------------------------
                // The existing single retirement, asked for this one piece. It takes pieces of a marked lineage only (a
                // character's, in the product): the first body's is marked here, for this piece.
                world.Lifetime.MarkLineage(a.source);
                if (!world.Lifetime.TryRetire(further, out string refusal))
                {
                    failures.Add("the piece could not be retired: " + refusal);
                    yield break;
                }

                poses.Remove(further);
                for (int i = 0; i < 4; i++) yield return null;
                yield return Picture(io, 6, states, poses);
            }
            finally
            {
                io.hold(false);
                world.Driver.RemainingMainSeconds = driverBudget;
            }
        }

        // One stage's pictures and its line of state.
        private static IEnumerator Picture(Io io, int stage, List<string> states, Dictionary<LogicalFragmentId, (Vector3 position, Quaternion rotation)> poses)
        {
            string name = StageNames[stage];
            states.Add(Describe(io.world, name, poses));
            io.log("cut scene stage " + states[states.Count - 1]);
            yield return io.pictures(name);
        }

        /// <summary>What the ledger and the display hold, and where each piece was put: the same for two runs of the same stage.</summary>
        internal static string Describe(CutWorldRoot world, string stage, Dictionary<LogicalFragmentId, (Vector3 position, Quaternion rotation)> poses)
        {
            var text = new StringBuilder(stage);
            LogicalCutLedger ledger = world.Ledger;
            int admitted = 0, published = 0, completed = 0, other = 0;
            for (int i = 0; i < ledger.OperationCount; i++)
            {
                if (!ledger.TryGetOperationAtAdmission(i, out LogicalCutOperation operation)) continue;
                switch (operation.state)
                {
                    case LogicalCutOperationState.Completed: completed++; break;
                    case LogicalCutOperationState.Published: published++; break;
                    case LogicalCutOperationState.Admitted: admitted++; break;
                    default: other++; break;
                }
            }

            text.Append(": operations ").Append(ledger.OperationCount).Append(" (complete ").Append(completed).Append(", published with geometry to come ").Append(published)
                .Append(", admitted ").Append(admitted).Append(", ended otherwise ").Append(other).Append("); owners ").Append(world.Owners.Count).Append("; display render fragments ").Append(world.Display.RenderFragmentCount)
                .Append(", commands ").Append(world.Display.CommandCount).Append(", caps ").Append(world.Display.CapRecordCount)
                .Append("; pieces placed ").Append(poses.Count).Append(':');
            var lines = new List<string>();
            foreach (KeyValuePair<LogicalFragmentId, (Vector3 position, Quaternion rotation)> p in poses)
            {
                Vector3 e = p.Value.rotation.eulerAngles;
                lines.Add(" at (" + p.Value.position.x.ToString("F3", Inv) + ", " + p.Value.position.y.ToString("F3", Inv) + ", " + p.Value.position.z.ToString("F3", Inv)
                    + ") turned (" + e.x.ToString("F1", Inv) + ", " + e.y.ToString("F1", Inv) + ", " + e.z.ToString("F1", Inv) + ")");
            }

            lines.Sort(StringComparer.Ordinal);
            foreach (string line in lines) text.Append(line).Append(';');
            return text.ToString();
        }

        internal static LogicalCutOperationState State(CutWorldRoot world, CutOperationId id)
        {
            return world.Ledger.TryGetOperation(id, out LogicalCutOperation operation) ? operation.state : LogicalCutOperationState.Admitted;
        }

        // One cut asked by one synthetic sweep through the middle of a box of the world, given to the ordinary detector:
        // level (the plane's normal up) or upright (the normal right). The cuts it publishes are taken from the ledger.
        // A sweep that admits nothing (an answer that may pass later) is given again, a few times.
        private static IEnumerator Cut(Io io, Bounds box, Vector3 normal, string what, List<LogicalCutOperation> made, List<string> failures)
        {
            LogicalCutLedger ledger = io.world.Ledger;
            int before = ledger.OperationCount;
            Vector3 c = box.center, e = box.extents + new Vector3(0.1f, 0.1f, 0.1f);
            for (int attempt = 0; attempt < 8 && ledger.OperationCount == before; attempt++)
            {
                long slash = ++s_slash;
                SlashSweep sweep = normal == Vector3.up
                    ? new SlashSweep(slash, Time.unscaledTimeAsDouble, false, new Plane(Vector3.up, -c.y), Vector3.right, Vector3.forward,
                        new Vector3(c.x - e.x, c.y, c.z - e.z), new Vector3(c.x - e.x, c.y, c.z + e.z), new Vector3(c.x + e.x, c.y, c.z - e.z), new Vector3(c.x + e.x, c.y, c.z + e.z))
                    : new SlashSweep(slash, Time.unscaledTimeAsDouble, false, new Plane(Vector3.right, -c.x), Vector3.down, Vector3.forward,
                        new Vector3(c.x, c.y + e.y, c.z - e.z), new Vector3(c.x, c.y + e.y, c.z + e.z), new Vector3(c.x, c.y - e.y, c.z - e.z), new Vector3(c.x, c.y - e.y, c.z + e.z));
                io.detector.Evaluate(new[] { sweep }, new[] { slash });
                io.log("cut scene: synthetic sweep " + slash + " through " + what + " (" + (normal == Vector3.up ? "level at y " + c.y.ToString("F3", Inv) : "upright at x " + c.x.ToString("F3", Inv))
                    + "), attempt " + (attempt + 1) + ": hits " + io.detector.HitCount + ", operations " + before + " -> " + ledger.OperationCount);
                for (int i = 0; i < 6 && ledger.OperationCount == before; i++) yield return null;
            }

            if (ledger.OperationCount == before)
            {
                failures.Add("no cut was admitted for " + what);
                yield break;
            }

            // Published: both children are there (the geometry may be held back).
            int count = ledger.OperationCount;
            for (int frame = 0; frame < 600; frame++)
            {
                bool all = true;
                for (int i = before; i < count; i++)
                {
                    all &= ledger.TryGetOperationAtAdmission(i, out LogicalCutOperation operation) && operation.positive.IsSet && operation.negative.IsSet;
                }

                if (all) break;
                yield return null;
            }

            for (int i = before; i < count; i++)
            {
                if (ledger.TryGetOperationAtAdmission(i, out LogicalCutOperation operation) && operation.positive.IsSet && operation.negative.IsSet)
                {
                    made.Add(operation);
                }
                else
                {
                    failures.Add("a cut of " + what + " was not published within 600 frames");
                }
            }
        }

        internal static IEnumerator UntilComplete(Io io, CutOperationId[] operations, string what, List<string> failures)
        {
            for (int frame = 0; frame < 900; frame++)
            {
                bool all = true;
                foreach (CutOperationId id in operations) all &= State(io.world, id) == LogicalCutOperationState.Completed;
                if (all)
                {
                    for (int i = 0; i < 3; i++) yield return null;
                    yield break;
                }

                yield return null;
            }

            failures.Add(what + " did not complete within 900 frames of the hold's release");
        }

        // A piece made kinematic and put at a pose of the world; its pose is kept for the pieces cut from it.
        internal static void Place(Io io, Dictionary<LogicalFragmentId, (Vector3 position, Quaternion rotation)> poses, LogicalFragmentId fragment, Vector3 position, Quaternion rotation)
        {
            poses[fragment] = (position, rotation);
            if (!io.world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.Root == null)
            {
                return;
            }

            if (owner.Body != null)
            {
                owner.Body.isKinematic = true;
                owner.Body.position = position;
                owner.Body.rotation = rotation;
            }

            owner.Root.transform.SetPositionAndRotation(position, rotation);
            Physics.SyncTransforms();
            // Where an owner's root was put outside a physics step is told, for the display's held placements (D-207).
            CutPhysicsStep.NotePlacementInputChanged(fragment);
        }

        // The two children of a cut, by where each stands along an axis of the world: the further one first. By the
        // side's name when either has no collider to read.
        private static void Sides(CutWorldRoot world, in LogicalCutOperation cut, Vector3 axis, out LogicalFragmentId further, out LogicalFragmentId nearer)
        {
            further = cut.positive;
            nearer = cut.negative;
            if (TryWorldBounds(world, cut.positive, out Bounds p) && TryWorldBounds(world, cut.negative, out Bounds n)
                && Vector3.Dot(n.center, axis) > Vector3.Dot(p.center, axis))
            {
                further = cut.negative;
                nearer = cut.positive;
            }
        }

        private static string Where(CutWorldRoot world, LogicalFragmentId fragment, Body source)
        {
            return world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) && owner.Root != null
                ? Vector3.Distance(owner.Root.transform.position, source.position).ToString("F4", Inv) + " m, " + Quaternion.Angle(owner.Root.transform.rotation, source.rotation).ToString("F2", Inv) + " deg"
                : "no owner";
        }

        // The piece turned about a point of the world and moved, from where it was last put.
        internal static void PlaceAbout(Io io, Dictionary<LogicalFragmentId, (Vector3 position, Quaternion rotation)> poses, LogicalFragmentId fragment, Vector3 pivot, Quaternion turn, Vector3 move)
        {
            (Vector3 position, Quaternion rotation) from = poses[fragment];
            Place(io, poses, fragment, pivot + turn * (from.position - pivot) + move, turn * from.rotation);
        }

        /// <summary>The box of the world a fragment's colliders fill now; false when it has none.</summary>
        public static bool TryWorldBounds(CutWorldRoot world, LogicalFragmentId fragment, out Bounds bounds)
        {
            bounds = default;
            if (!world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.Root == null)
            {
                return false;
            }

            Physics.SyncTransforms();
            bool any = false;
            foreach (Collider collider in owner.Root.GetComponentsInChildren<Collider>(false))
            {
                if (!collider.enabled) continue;
                if (!any) bounds = collider.bounds; else bounds.Encapsulate(collider.bounds);
                any = true;
            }

            return any;
        }
    }
}
