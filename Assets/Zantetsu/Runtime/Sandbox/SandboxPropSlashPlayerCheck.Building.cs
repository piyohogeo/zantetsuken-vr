using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // Measurement (the college_001 Slash E2E, Assets/Licensed/BuildingSlashE2E/BuildingSlashCity.unity): one building of
    // the city, registered by BuildingSlashE2E with its author's convex and anchors, cut again and again by replayed
    // katana input. With BuildingArgument the check does not ask for the box; it waits for the building's registration,
    // follows every Slash (hits, misses), every accepted cut to its Provisional publication, its Final/Logical
    // publication and its geometry commit (the "op" records of the hit record, CheckHits), and every building piece frame by frame (building-pieces.csv).
    // It only reads: nothing is cut, hit, published, placed or completed from here.
    //
    // What is judged, in three kinds, each written with its own prefix:
    // - [scenario] the structure the product sets up: the registration, fixed pieces (kinematic, no D6, not moving),
    //   the depth rule, every free piece's World D6 (to the World, Y free, the others limited, the distance limit L(d)),
    //   no joint left after a Final publication, every accepted cut through to its commit.
    // - [quality] how the pieces move in this scenario: no instantaneous move of a piece's centre of mass or of its
    //   rotation beyond what its velocities explain, no large excursion from its D6's reference, no large back-and-forth
    //   motion. DESIGN 7.2.2 does not bound a piece's total displacement or rotation, nor promise that a building stands,
    //   so these are this scenario's quality conditions, not product guarantees.
    // - [readback] the D6's angle limits as the joint reports them against A(d): written as a table, not judged here.
    // The total rotation to the reference (Quaternion.Angle) is kept apart from the per-axis angles (twist about the
    // joint's X, swing about Y and Z), which are what each D6 limit applies to.
    public static partial class SandboxPropSlashPlayerCheck
    {
        public const string BuildingArgument = "-zantetsuBuildingSlash";

        private sealed partial class Walk
        {
            internal bool building;

            // The physics time observed after the last cut's geometry commit (TL: at least 5 s), and its real-time cap.
            private const float BuildingObservationPhysicsSeconds = 5f;
            private const float BuildingObservationRealCap = 30f;

            // "-zantetsuBuildingObserveSeconds s": a longer observation window for this run (physics seconds, at least 5).
            // A window to look through, not a time the product promises anything by.
            private float BuildingObserveSeconds =>
                float.TryParse(Value("-zantetsuBuildingObserveSeconds"), System.Globalization.NumberStyles.Float, Inv, out float s) && s > BuildingObservationPhysicsSeconds
                    ? s : BuildingObservationPhysicsSeconds;
            private long _observeFromStep = -1;

            // [quality] thresholds, written into the log with every result.
            private const float TeleportComMetres = 0.10f;    // COM displacement in a read beyond what the linear velocity explains
            private const float TeleportRotDegrees = 5f;      // rotation in a read beyond what the angular velocity explains
            private const float LargeAngleDegrees = 10f;      // total rotation from the D6's reference
            private const float LargeOffsetBeyond = 0.25f;    // metres of horizontal offset beyond the joint's distance limit
            private const float ReversalDegrees = 2f;         // a change in the total angle that counts as a swing ...
            private const int ReversalCount = 4;              // ... and how many reversals of such swings make back-and-forth
            private const float FixedDriftTolerance = 0.001f; // metres a fixed piece may be read to have moved

            // The records kept around a large motion (building-events.csv): the steps before it and after it, per piece.
            private const int EventStepsBefore = 30;
            private const int EventStepsAfter = 60;

            private BuildingSlashE2E _building;
            private Quaternion _buildingRotation;
            private StreamWriter _buildingRows, _eventRows;
            private readonly Dictionary<int, BuildingTrack> _buildingTracks = new Dictionary<int, BuildingTrack>();
            private readonly Dictionary<long, BuildingOp> _buildingOps = new Dictionary<long, BuildingOp>();
            private readonly List<string> _buildingViolations = new List<string>();
            private int _buildingViolationsDropped;
            private readonly SortedDictionary<string, int> _angleReadback = new SortedDictionary<string, int>();
            // [readback] the reads the engine's linear minimum applied to (DESIGN 7.2.2), by depth: the fragments, the reads, the
            // request, and the smallest and largest value read (a light run writes no per-piece rows, so what was read is kept here).
            private readonly SortedDictionary<int, (HashSet<int> fragments, int reads, float requested, float readMin, float readMax)> _linearRaised
                = new SortedDictionary<int, (HashSet<int>, int, float, float, float)>();
            private int _events;
            private readonly List<string> _eventLines = new List<string>();
            private readonly Dictionary<Rigidbody, int> _bodyFragment = new Dictionary<Rigidbody, int>();
            private readonly Collider[] _overlaps = new Collider[64];
            private Collider _buildingFloor;

            private sealed class BuildingTrack
            {
                public int firstFrame;
                public long firstStep;
                public long originOp;
                public int depth;
                public bool derived;
                public int anchors;
                public bool fixedOwner;
                public Vector3 firstPosition;
                public Vector3 lastCom, lastV, lastW;
                public Quaternion lastRot;
                public long lastStep = -1;
                public int lastFrame;
                public float lastTotal = float.NaN, lastDTotal;
                public int reversals;
                public float maxFixedDrift, maxOffset, maxTotal, maxTwist, maxSwingY, maxSwingZ, maxSpeed, maxComJump, maxRotJump, maxStepAngle;
                public float maxTwistOver, maxSwingYOver, maxSwingZOver;
                public float comJumpAtFinal = -1f, rotJumpAtFinal = -1f;
                public string maxComJumpAt, maxRotJumpAt, maxTotalAt;
                public float limit = float.NaN, readX = float.NaN, readY = float.NaN, readZ = float.NaN;
                public bool endedLive;
                public string end;
                public int staleFrames;

                // Sleep in the observation period after the last cut: the step it first slept, how many times it went to
                // sleep, how many times it woke after having slept, and whether it sleeps at the end. Recorded, not required.
                public long firstSleepStep = -1;
                public int sleeps, wakes;
                public bool sleeping, observedAny;
                public readonly List<long> wakeSteps = new List<long>();
                public PhysicsFragmentOwner owner;
                public readonly Queue<string> recent = new Queue<string>();
                public int after;
                public int eventId;
                public readonly List<string> triggers = new List<string>();
            }

            private sealed class BuildingOp
            {
                public long slash;
                public LogicalFragmentId source;
                public int sourceDepth = -1;
                public int sourceAnchors = -1;
                public bool sourceFixed;
                public string kind;
                public Vector3 worldNormal;
            }

            private bool BuildingReady() => _building != null && _building.IsRegistered;

            // A piece with its own body is kinematic by that body; a fused member (no body of its own, the fusion's trial) by its group.
            private static bool IsKinematicOrFusedResting(PhysicsFragmentOwner owner) =>
                owner.Body != null ? owner.Body.isKinematic : owner.IsFused && owner.Group != null && owner.Group.Kinematic;

            private void Violation(string what)
            {
                if (_buildingViolations.Count < 200) _buildingViolations.Add(what);
                else _buildingViolationsDropped++;
            }

            // "-zantetsuBuildingStepBack distance,height" in metres (from the front face, above the base); 22, 7 if not given.
            public const string StepBackArgument = "-zantetsuBuildingStepBack";
            private float StepBackDistance = 22f;
            private float StepBackHeight = 7f;

            // The front view of the whole building: StepBackDistance in front of its front face, StepBackHeight above its base,
            // looking at its middle. The instance's own mesh gives its extent (its renderers are off once it is registered).
            private bool FrontView(out Vector3 eye, out Quaternion rotation, out string what)
            {
                string[] given = (Value(StepBackArgument) ?? "").Split(',');
                if (given.Length == 2)
                {
                    StepBackDistance = float.Parse(given[0], Inv);
                    StepBackHeight = float.Parse(given[1], Inv);
                }

                eye = default;
                rotation = Quaternion.identity;
                what = "no building";
                if (_building == null || _building.target == null)
                {
                    return false;
                }

                Vector3 at = _building.target.position;
                Bounds drawn = default;
                bool any = false;
                foreach (Renderer r in _building.instanceRenderers)
                {
                    MeshFilter filter = r != null ? r.GetComponent<MeshFilter>() : null;
                    if (filter == null || filter.sharedMesh == null) continue;
                    Bounds local = filter.sharedMesh.bounds;
                    for (int c = 0; c < 8; c++)
                    {
                        Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                        Vector3 world = r.transform.TransformPoint(corner);
                        if (any) drawn.Encapsulate(world); else { drawn = new Bounds(world, Vector3.zero); any = true; }
                    }
                }

                if (!any) drawn = new Bounds(at, Vector3.zero);
                eye = new Vector3(drawn.center.x, at.y + StepBackHeight, drawn.min.z - StepBackDistance);
                Vector3 look = new Vector3(drawn.center.x, at.y + 0.45f * drawn.size.y, drawn.center.z);
                rotation = Quaternion.LookRotation(look - eye, Vector3.up);
                what = StepBackDistance.ToString("R", Inv) + " m from the face, " + StepBackHeight.ToString("R", Inv) + " m up, looking at "
                    + look.ToString("F2") + " (building " + drawn.min.ToString("F1") + ".." + drawn.max.ToString("F1") + ")";
                return true;
            }

            // Before the replay: the movie camera and the check's view both on the front view, so the pictures and the
            // movie of every condition are taken from one place. The player's own view is not moved until the cuts end.
            private void BuildingPlaceViews()
            {
                if (!FrontView(out Vector3 eye, out Quaternion rotation, out string what))
                {
                    Log("front view: " + what);
                    return;
                }

                _view.transform.SetPositionAndRotation(eye, rotation);
                _view.fieldOfView = 60f;
                _view.farClipPlane = 500f;
                if (_building.movieCamera != null)
                {
                    _building.movieCamera.transform.SetPositionAndRotation(eye, rotation);
                    _building.movieCamera.fieldOfView = 60f;
                }

                Log("front view for the check's pictures and the movie: " + what + " fov 60");
            }

            // At the replay's start: the registration as the scenario made it.
            private void BuildingBegin()
            {
                Log("building registration: " + _building.Registration);
                GameObject actor = _building.Actor;
                _buildingRotation = actor != null ? actor.transform.rotation : Quaternion.identity;
                bool rootOwned = _world.Owners.TryGet(_building.Fragment, out PhysicsFragmentOwner root);
                _world.Ledger.TryGetAnchorCount(_building.Fragment, out int anchors);
                Log("building root: fragment=" + _building.Fragment.value + " owner=" + rootOwned + " anchors(ledger)=" + anchors
                    + " fixedByAnchors=" + (rootOwned && root.FixedByAnchors) + " kinematic=" + (rootOwned && IsKinematicOrFusedResting(root)) + " fused=" + (rootOwned && root.IsFused)
                    + " lineage=" + (rootOwned ? root.Building.ToString() : "none") + " mass=" + (rootOwned ? root.Mass.ToString("R", Inv) : "none")
                    + " position=" + (actor != null ? actor.transform.position.ToString("F3") : "none")
                    + " yaw=" + _buildingRotation.eulerAngles.y.ToString("F1", Inv));
                BuildingWorldD6Settings d6 = _world.Profile.BuildingWorld;
                Log("building D6 settings from the world's profile (" + _world.Profile.name + "): L1=" + d6.firstLimitMetres.ToString("R", Inv) + " m A1="
                    + d6.firstAngleDegrees.ToString("R", Inv) + " deg r=" + d6.ratio.ToString("R", Inv)
                    + "; [quality] thresholds: instantaneous move COM " + TeleportComMetres.ToString("R", Inv) + " m / rotation " + TeleportRotDegrees.ToString("R", Inv)
                    + " deg beyond the velocities, large excursion " + LargeAngleDegrees.ToString("R", Inv) + " deg total or " + LargeOffsetBeyond.ToString("R", Inv)
                    + " m beyond the distance limit, back-and-forth " + ReversalCount + " reversals of " + ReversalDegrees.ToString("R", Inv)
                    + " deg; a fixed piece may drift " + FixedDriftTolerance.ToString("R", Inv) + " m");
                if (_world.Hulls != null)
                {
                    BuildingHullBegin();   // the hull trial: one group, its anchors the group's, its root fragment display-only
                }
                else
                {
                    Expect(rootOwned && anchors == _building.AnchorCount && anchors > 0 && root.FixedByAnchors && IsKinematicOrFusedResting(root)
                            && root.Building.IsBuildingDerived && root.Building.SplitDepth == 0,
                        "[scenario] the building is registered as a building at depth 0, holding its " + _building.AnchorCount + " anchors, fixed and kinematic");
                }

                GameObject floor = GameObject.Find("Building Slash Floor");
                _buildingFloor = floor != null ? floor.GetComponent<Collider>() : null;
                HitLogOpen("building");
                if (!light)
                {
                    _buildingRows = new StreamWriter(Path.Combine(directory, "building-pieces.csv"));
                    _buildingRows.WriteLine("frame,phase,stepId,fragment,originOp,depth,derived,anchors,fixedByAnchors,ledgerFixed,kinematic,joints,d6,d6Connected,"
                        + "d6XMotion,d6YMotion,limit,expectedLimit,expectedRead,lowX,highX,limitY,limitZ,expectedAngle,offset,total,twist,swingY,swingZ,"
                        + "comX,comY,comZ,speed,angularDegPerS,comJump,rotJump,posX,posY,posZ,lowestVertexY");
                    _eventRows = new StreamWriter(Path.Combine(directory, "building-events.csv"));
                    _eventRows.WriteLine("event,trigger," + EventHeader);
                }

                MultiStorage("before replay");
                BuildingDiagBegin();
            }

            private const string EventHeader = "frame,phase,stepId,fragment,originOp,opPhase,opLedger,opGeometry,depth,mass,inertiaX,inertiaY,inertiaZ,"
                + "inertiaRotX,inertiaRotY,inertiaRotZ,inertiaRotW,comLocalX,comLocalY,comLocalZ,comX,comY,comZ,vX,vY,vZ,rotX,rotY,rotZ,rotW,wX,wY,wZ,"
                + "anchorWorldX,anchorWorldY,anchorWorldZ,connectedX,connectedY,connectedZ,axisX,axisY,axisZ,secondaryX,secondaryY,secondaryZ,"
                + "limit,lowX,highX,limitY,limitZ,offset,total,twist,swingY,swingZ,comJump,rotJump,sleeping,contacts";

            // At acceptance: what the source was, and which way the plane lies in the world (the lineage keeps the root's
            // geometry frame, which stands at the registration pose, a D6 child within its angle of it).
            private void BuildingOnHit(in SlashHitConfirmed hit)
            {
                if (hit.Acceptance != ProvisionalCutAcceptance.Published && hit.Acceptance != ProvisionalCutAcceptance.Pending)
                {
                    return;
                }

                if (_world.Hulls != null)
                {
                    return;   // a hull group's hit issues no operation to this check: its record is the trial's own (the hull summary)
                }

                var op = new BuildingOp { slash = hit.SlashId, source = hit.Fragment };
                if (_world.Owners.TryGet(hit.Fragment, out PhysicsFragmentOwner owner))
                {
                    op.sourceDepth = owner.Building.SplitDepth;
                    op.sourceFixed = owner.FixedByAnchors;
                }

                _world.Ledger.TryGetAnchorCount(hit.Fragment, out op.sourceAnchors);
                if (_world.Ledger.TryGetOperation(hit.Operation, out LogicalCutOperation cut))
                {
                    op.worldNormal = (_buildingRotation * new Vector3(cut.plane.x, cut.plane.y, cut.plane.z)).normalized;
                    float up = Mathf.Abs(op.worldNormal.y);
                    op.kind = up > 0.9f ? "horizontal" : up < 0.1f ? "vertical" : "diagonal";
                }
                else
                {
                    op.kind = "unknown";
                }

                _buildingOps[hit.Operation.value] = op;
                Log("building op" + hit.Operation.value + ": slash=" + hit.SlashId + " source=" + hit.Fragment.value + " sourceDepth=" + op.sourceDepth
                    + " sourceAnchors=" + op.sourceAnchors + " sourceFixed=" + op.sourceFixed + " cut=" + op.kind
                    + " worldNormal=" + op.worldNormal.ToString("F3") + " acceptance=" + hit.Acceptance);
            }

            // The rotation from the D6's reference, in the joint's frame at the reference (which is the world's: Place set the
            // joint's axes to the world's right and up), split as swing * twist with the twist about X. Degrees, signed.
            private static void SwingTwist(Quaternion d, out float twist, out float swingY, out float swingZ)
            {
                if (d.w < 0f) d = new Quaternion(-d.x, -d.y, -d.z, -d.w);
                float norm = Mathf.Sqrt(d.w * d.w + d.x * d.x);
                Quaternion tw = norm > 1e-9f ? new Quaternion(d.x / norm, 0f, 0f, d.w / norm) : Quaternion.identity;
                Quaternion sw = d * Quaternion.Inverse(tw);
                if (sw.w < 0f) sw = new Quaternion(-sw.x, -sw.y, -sw.z, -sw.w);
                twist = 2f * Mathf.Atan2(tw.x, tw.w) * Mathf.Rad2Deg;
                swingY = 2f * Mathf.Atan2(sw.y, sw.w) * Mathf.Rad2Deg;
                swingZ = 2f * Mathf.Atan2(sw.z, sw.w) * Mathf.Rad2Deg;
            }

            // What a piece's colliders touch now: every other collider within their bounds, with its penetration (0 when only
            // near). By fragment where the other is a piece's, by name otherwise.
            private string Contacts(PhysicsFragmentOwner owner, int self)
            {
                var parts = new List<string>();
                foreach (Collider mine in owner.Root.GetComponentsInChildren<Collider>())
                {
                    if (!mine.enabled) continue;
                    Bounds b = mine.bounds;
                    b.Expand(4f * Physics.defaultContactOffset);
                    int n = Physics.OverlapBoxNonAlloc(b.center, b.extents, _overlaps, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n; i++)
                    {
                        Collider other = _overlaps[i];
                        if (other == null || other.attachedRigidbody == owner.Body) continue;
                        string name = other == _buildingFloor ? "floor"
                            : other.attachedRigidbody != null && _bodyFragment.TryGetValue(other.attachedRigidbody, out int f) ? "f" + f
                            : other.name.Replace(",", " ").Replace("|", " ");
                        float depth = Physics.ComputePenetration(mine, mine.transform.position, mine.transform.rotation,
                            other, other.transform.position, other.transform.rotation, out Vector3 _, out float d) ? d : 0f;
                        parts.Add(name + ":" + depth.ToString("F4", Inv));
                    }
                }

                return string.Join("|", parts.Distinct());
            }

            // Every frame of the replay and the observation: each building piece's constraints and motion.
            private void BuildingFrame(int frame)
            {
                if (_world == null || _world.IsEnding)
                {
                    return;
                }

                BuildingFusionFrame(frame);
                BuildingHullFrame(frame);
                bool d6On = _world.Profile.BuildingWorld.enabled;   // with the World D6 off (the fusion's runs) nothing about a D6 is judged
                ManualPhysicsClock clock = CutPhysicsStep.Clock;
                long step = clock != null ? clock.StepId : -1;
                float h = clock != null ? 1f / clock.FrequencyHz : 0f;
                BuildingWorldD6Settings d6 = _world.Profile.BuildingWorld;
                var live = new HashSet<int>();
                _bodyFragment.Clear();
                for (int id = 1; id < 256; id++)
                {
                    var fragment = new LogicalFragmentId(id);
                    if (!_world.Ledger.TryGetFragmentState(fragment, out LogicalFragmentState s)) break;
                    if (s == LogicalFragmentState.Live && _world.Owners.TryGet(fragment, out PhysicsFragmentOwner o) && !o.IsWithdrawn && o.Body != null)
                    {
                        _bodyFragment[o.Body] = id;
                    }
                }

                for (int id = 1; id < 256; id++)
                {
                    var fragment = new LogicalFragmentId(id);
                    if (!_world.Ledger.TryGetFragmentState(fragment, out LogicalFragmentState state))
                    {
                        break;
                    }

                    if (state != LogicalFragmentState.Live || !_world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner)
                        || owner.IsWithdrawn || owner.Body == null)
                    {
                        continue;
                    }

                    live.Add(id);
                    Rigidbody body = owner.Body;
                    Transform root = owner.Root.transform;
                    bool hasOrigin = _world.Ledger.TryGetOrigin(fragment, out CutOperationId origin, out _);
                    LogicalCutOperation made = default;
                    bool finalised = hasOrigin && _world.Ledger.TryGetOperation(origin, out made) && made.state != LogicalCutOperationState.Admitted;
                    _world.Ledger.TryGetAnchorCount(fragment, out int anchors);
                    bool ledgerFixed = _world.Ledger.IsFixedOwner(fragment);
                    int depth = owner.Building.SplitDepth;
                    ConfigurableJoint joint = owner.BuildingWorldConstraint;
                    int joints = owner.Root.GetComponents<Joint>().Length;
                    Vector3 position = root.position;
                    Vector3 com = body.worldCenterOfMass;
                    Vector3 v = body.linearVelocity;
                    Vector3 w = body.angularVelocity;
                    Quaternion rot = root.rotation;

                    if (!_buildingTracks.TryGetValue(id, out BuildingTrack t))
                    {
                        _buildingTracks[id] = t = new BuildingTrack
                        {
                            firstFrame = frame, firstStep = step, originOp = hasOrigin ? origin.value : 0, depth = depth,
                            derived = owner.Building.IsBuildingDerived, anchors = anchors, fixedOwner = owner.FixedByAnchors,
                            firstPosition = position, lastCom = com, lastV = v, lastW = w, lastRot = rot, lastStep = step, lastFrame = frame,
                        };
                        if (hasOrigin && _buildingOps.TryGetValue(origin.value, out BuildingOp parent) && parent.sourceDepth >= 0
                            && depth != parent.sourceDepth + 1)
                        {
                            Violation("fragment " + id + " at depth " + depth + " from op" + origin.value + " whose source was at depth " + parent.sourceDepth);
                        }

                        BuildingDiagFirstSight(frame, step, id, owner);
                    }

                    // [scenario] The constraints this piece carries now.
                    string of = LineageOf(fragment);
                    if (of != "building") Violation("fragment " + id + " of " + of + " (not of the building) at frame " + frame);
                    if (!owner.Building.IsBuildingDerived) Violation("fragment " + id + " not building-derived at frame " + frame);
                    if (owner.FixedByAnchors != ledgerFixed) Violation("fragment " + id + " fixedByAnchors " + owner.FixedByAnchors + " but the ledger's anchors say " + ledgerFixed + " at frame " + frame);
                    if (owner.FixedByAnchors && (!body.isKinematic || joint != null))
                    {
                        Violation("fixed fragment " + id + " kinematic=" + body.isKinematic + " d6=" + (joint != null) + " at frame " + frame);
                    }

                    float expectedLimit = float.NaN, expectedRead = float.NaN, expectedAngle = float.NaN, offset = float.NaN, total = float.NaN, twist = float.NaN, swingY = float.NaN, swingZ = float.NaN;
                    Vector3 anchorWorld = position;
                    if (d6On && !owner.FixedByAnchors && depth >= 1)
                    {
                        expectedLimit = d6.LimitMetres(depth);   // the request L(d)
                        expectedRead = ExpectedLinearReadback(expectedLimit);   // what the joint is expected to report (DESIGN 7.2.2)
                        expectedAngle = d6.AngleDegrees(depth);
                        if (joint == null || joint.gameObject != owner.Root)
                        {
                            Violation("free fragment " + id + " at depth " + depth + " has no World D6 on its actor at frame " + frame);
                        }
                        else
                        {
                            bool shape = joint.connectedBody == null && joint.xMotion == ConfigurableJointMotion.Limited && joint.yMotion == ConfigurableJointMotion.Free
                                && joint.zMotion == ConfigurableJointMotion.Limited && joint.angularXMotion == ConfigurableJointMotion.Limited
                                && joint.angularYMotion == ConfigurableJointMotion.Limited && joint.angularZMotion == ConfigurableJointMotion.Limited;
                            if (!shape || !LinearReadbackMatches(joint.linearLimit.limit, expectedLimit))
                            {
                                Violation("fragment " + id + " D6 at depth " + depth + " shape=" + shape + " limit read " + joint.linearLimit.limit.ToString("R", Inv)
                                    + " (requested L(d) " + expectedLimit.ToString("R", Inv) + ", expected read " + expectedRead.ToString("R", Inv)
                                    + ", tolerance " + LinearLimitTolerance.ToString("R", Inv) + ") at frame " + frame);
                            }

                            if (LinearMinimumApplies(expectedLimit))
                            {
                                float read = joint.linearLimit.limit;
                                bool raisedBefore = _linearRaised.TryGetValue(depth, out (HashSet<int> fragments, int reads, float requested, float readMin, float readMax) raised);
                                raised.fragments ??= new HashSet<int>();
                                raised.fragments.Add(id);
                                _linearRaised[depth] = (raised.fragments, raised.reads + 1, expectedLimit, raisedBefore ? Mathf.Min(raised.readMin, read) : read,
                                    raisedBefore ? Mathf.Max(raised.readMax, read) : read);
                            }

                            // [readback] the angle limits as the joint reports them, beside A(d), tallied per depth.
                            string key = "depth " + depth + " A(d)=" + expectedAngle.ToString("R", Inv) + " lowX=" + joint.lowAngularXLimit.limit.ToString("R", Inv)
                                + " highX=" + joint.highAngularXLimit.limit.ToString("R", Inv) + " Y=" + joint.angularYLimit.limit.ToString("R", Inv)
                                + " Z=" + joint.angularZLimit.limit.ToString("R", Inv);
                            _angleReadback.TryGetValue(key, out int seen);
                            _angleReadback[key] = seen + 1;

                            t.limit = joint.linearLimit.limit;
                            t.readX = joint.highAngularXLimit.limit;
                            t.readY = joint.angularYLimit.limit;
                            t.readZ = joint.angularZLimit.limit;
                            anchorWorld = root.TransformPoint(joint.anchor);
                            Vector3 away = anchorWorld - joint.connectedAnchor;
                            offset = new Vector2(away.x, away.z).magnitude;
                            // The reference: the rotation that took the world's right and up to the joint's axes.
                            Vector3 axis = joint.axis.normalized, secondary = joint.secondaryAxis.normalized;
                            Quaternion toReference = Quaternion.LookRotation(Vector3.Cross(axis, secondary), secondary);
                            Quaternion relative = rot * toReference;
                            total = Quaternion.Angle(relative, Quaternion.identity);
                            SwingTwist(relative, out twist, out swingY, out swingZ);
                            t.maxOffset = Mathf.Max(t.maxOffset, offset);
                            if (total > t.maxTotal) { t.maxTotal = total; t.maxTotalAt = "frame " + frame + " step " + step; }
                            t.maxTwist = Mathf.Max(t.maxTwist, Mathf.Abs(twist));
                            t.maxSwingY = Mathf.Max(t.maxSwingY, Mathf.Abs(swingY));
                            t.maxSwingZ = Mathf.Max(t.maxSwingZ, Mathf.Abs(swingZ));
                            t.maxTwistOver = Mathf.Max(t.maxTwistOver, twist > 0f ? twist - joint.highAngularXLimit.limit : joint.lowAngularXLimit.limit - twist);
                            t.maxSwingYOver = Mathf.Max(t.maxSwingYOver, Mathf.Abs(swingY) - joint.angularYLimit.limit);
                            t.maxSwingZOver = Mathf.Max(t.maxSwingZOver, Mathf.Abs(swingZ) - joint.angularZLimit.limit);
                        }
                    }

                    // [scenario] After the Final handoff the sibling constraint has ended: the actor carries its D6 or
                    // nothing. Two frames in a row, so a handoff between this read and the last is not taken for a stale one.
                    int expectedJoints = joint != null ? 1 : 0;
                    if (finalised && joints != expectedJoints)
                    {
                        if (++t.staleFrames == 2) Violation("fragment " + id + " carries " + joints + " joints (expected " + expectedJoints + ") after its op" + origin.value + "'s Final publication, frame " + frame);
                    }
                    else
                    {
                        t.staleFrames = 0;
                    }

                    if (owner.FixedByAnchors)
                    {
                        t.maxFixedDrift = Mathf.Max(t.maxFixedDrift, Vector3.Distance(position, t.firstPosition));
                    }

                    // [quality] Motion, read once per physics step: the COM against its linear velocity and the rotation
                    // against the angular velocity, so a rotation that swings the body's origin (the building's origin, far
                    // from the piece) is not taken for a move. A read with no Step since the last shows nothing new.
                    float comJump = 0f, rotJump = 0f;
                    bool stepped = step != t.lastStep;
                    if (stepped)
                    {
                        float dt = Mathf.Max(0L, step - t.lastStep) * h;
                        comJump = Mathf.Max(0f, Vector3.Distance(com, t.lastCom) - Mathf.Max(v.magnitude, t.lastV.magnitude) * dt);
                        rotJump = Mathf.Max(0f, Quaternion.Angle(rot, t.lastRot) - Mathf.Max(w.magnitude, t.lastW.magnitude) * dt * Mathf.Rad2Deg);
                        if (comJump > t.maxComJump) { t.maxComJump = comJump; t.maxComJumpAt = "frame " + frame + " step " + step; }
                        if (rotJump > t.maxRotJump) { t.maxRotJump = rotJump; t.maxRotJumpAt = "frame " + frame + " step " + step; }
                        if (hasOrigin && _accepted.Exists(a => a.operation.value == origin.value && a.publishedFrame == frame))
                        {
                            t.comJumpAtFinal = Mathf.Max(t.comJumpAtFinal, comJump);
                            t.rotJumpAtFinal = Mathf.Max(t.rotJumpAtFinal, rotJump);
                        }

                        if (!float.IsNaN(total) && !float.IsNaN(t.lastTotal))
                        {
                            float dTotal = total - t.lastTotal;
                            t.maxStepAngle = Mathf.Max(t.maxStepAngle, Mathf.Abs(dTotal));
                            if (Mathf.Abs(dTotal) >= ReversalDegrees && Mathf.Abs(t.lastDTotal) >= ReversalDegrees && Mathf.Sign(dTotal) != Mathf.Sign(t.lastDTotal))
                            {
                                t.reversals++;
                            }

                            t.lastDTotal = dTotal;
                        }

                        t.lastTotal = total;
                    }

                    t.maxSpeed = Mathf.Max(t.maxSpeed, v.magnitude);
                    t.owner = owner;
                    if (_observing && _observeFromStep < 0) _observeFromStep = step;
                    if (_observing && stepped)
                    {
                        bool asleep = body.IsSleeping();
                        if (!t.observedAny)
                        {
                            t.observedAny = true;
                            t.sleeping = asleep;
                            if (asleep) { t.firstSleepStep = step; t.sleeps = 1; }
                        }
                        else if (asleep != t.sleeping)
                        {
                            if (asleep) { t.sleeps++; if (t.firstSleepStep < 0) t.firstSleepStep = step; }
                            else { t.wakes++; t.wakeSteps.Add(step); }
                            t.sleeping = asleep;
                        }
                    }
                    t.end = "com=" + com.ToString("F3") + " speed=" + v.magnitude.ToString("F3", Inv) + " kinematic=" + body.isKinematic + " sleeping=" + body.IsSleeping()
                        + (float.IsNaN(offset) ? "" : " offset=" + offset.ToString("F4", Inv) + " total=" + total.ToString("F2", Inv) + " twist=" + twist.ToString("F2", Inv)
                            + " swingY=" + swingY.ToString("F2", Inv) + " swingZ=" + swingZ.ToString("F2", Inv));

                    _buildingRows?.WriteLine(string.Join(",", frame, _observing ? "observe" : "cuts", step, id, t.originOp, depth, owner.Building.IsBuildingDerived,
                        anchors, owner.FixedByAnchors, ledgerFixed, body.isKinematic, joints, joint != null, joint != null && joint.connectedBody == null,
                        joint != null ? joint.xMotion.ToString() : "", joint != null ? joint.yMotion.ToString() : "",
                        joint != null ? F(joint.linearLimit.limit) : "", F(expectedLimit), F(expectedRead), joint != null ? F(joint.lowAngularXLimit.limit) : "",
                        joint != null ? F(joint.highAngularXLimit.limit) : "", joint != null ? F(joint.angularYLimit.limit) : "", joint != null ? F(joint.angularZLimit.limit) : "",
                        F(expectedAngle), F(offset), F(total), F(twist), F(swingY), F(swingZ), F(com.x), F(com.y), F(com.z), F(v.magnitude),
                        F(w.magnitude * Mathf.Rad2Deg), F(comJump), F(rotJump), F(position.x), F(position.y), F(position.z), !light ? F(LowestVertexY(owner)) : ""));

                    // [quality] The steps around a large motion of a free piece, kept apart (building-events.csv).
                    if (_eventRows != null && !owner.FixedByAnchors && stepped)
                    {
                        ProvisionalCutTransaction x = hasOrigin ? _world.Driver.TransactionOf(origin) : null;
                        string row = string.Join(",", frame, _observing ? "observe" : "cuts", step, id, t.originOp, x != null ? x.Phase.ToString() : "none",
                            hasOrigin ? made.state.ToString() : "", hasOrigin ? _world.Geometry.StageOf(origin).ToString() : "", depth,
                            F(body.mass), F(body.inertiaTensor.x), F(body.inertiaTensor.y), F(body.inertiaTensor.z),
                            F(body.inertiaTensorRotation.x), F(body.inertiaTensorRotation.y), F(body.inertiaTensorRotation.z), F(body.inertiaTensorRotation.w),
                            F(body.centerOfMass.x), F(body.centerOfMass.y), F(body.centerOfMass.z), F(com.x), F(com.y), F(com.z), F(v.x), F(v.y), F(v.z),
                            F(rot.x), F(rot.y), F(rot.z), F(rot.w), F(w.x), F(w.y), F(w.z), F(anchorWorld.x), F(anchorWorld.y), F(anchorWorld.z),
                            joint != null ? F(joint.connectedAnchor.x) : "", joint != null ? F(joint.connectedAnchor.y) : "", joint != null ? F(joint.connectedAnchor.z) : "",
                            joint != null ? F(joint.axis.x) : "", joint != null ? F(joint.axis.y) : "", joint != null ? F(joint.axis.z) : "",
                            joint != null ? F(joint.secondaryAxis.x) : "", joint != null ? F(joint.secondaryAxis.y) : "", joint != null ? F(joint.secondaryAxis.z) : "",
                            joint != null ? F(joint.linearLimit.limit) : "", joint != null ? F(joint.lowAngularXLimit.limit) : "", joint != null ? F(joint.highAngularXLimit.limit) : "",
                            joint != null ? F(joint.angularYLimit.limit) : "", joint != null ? F(joint.angularZLimit.limit) : "",
                            F(offset), F(total), F(twist), F(swingY), F(swingZ), F(comJump), F(rotJump), body.IsSleeping(), Contacts(owner, id));
                        string trigger = comJump > TeleportComMetres ? "comJump" : rotJump > TeleportRotDegrees ? "rotJump"
                            : !float.IsNaN(total) && total > LargeAngleDegrees ? "largeAngle"
                            : !float.IsNaN(offset) && offset > t.limit + LargeOffsetBeyond ? "largeOffset"
                            : t.maxStepAngle >= 5f && Mathf.Abs(t.lastDTotal) >= 5f ? "stepAngle5" : null;
                        if (t.after > 0)
                        {
                            _eventRows.WriteLine(t.eventId + ",after," + row);
                            t.after--;
                            if (trigger != null) t.after = EventStepsAfter;
                        }
                        else if (trigger != null)
                        {
                            t.eventId = ++_events;
                            t.triggers.Add("event " + t.eventId + " " + trigger + " at frame " + frame + " step " + step);
                            _eventLines.Add("event " + t.eventId + ": fragment " + id + " " + trigger + " at frame " + frame + " step " + step
                                + " (total " + F(total) + " deg, offset " + F(offset) + " m, comJump " + F(comJump) + " m, rotJump " + F(rotJump) + " deg)");
                            foreach (string before in t.recent) _eventRows.WriteLine(t.eventId + ",before," + before);
                            _eventRows.WriteLine(t.eventId + "," + trigger + "," + row);
                            t.recent.Clear();
                            t.after = EventStepsAfter;
                        }

                        if (t.after == 0)
                        {
                            t.recent.Enqueue(row);
                            if (t.recent.Count > EventStepsBefore) t.recent.Dequeue();
                        }
                    }

                    t.lastCom = com;
                    t.lastV = v;
                    t.lastW = w;
                    t.lastRot = rot;
                    t.lastStep = step;
                    t.lastFrame = frame;
                }

                foreach (KeyValuePair<int, BuildingTrack> k in _buildingTracks)
                {
                    k.Value.endedLive = live.Contains(k.Key);
                }
            }

            // Called from MultiClose, which has ended the hit record this mode shares (CheckHits).
            private void BuildingClose()
            {
                BuildingFusionClose();
                BuildingHullClose();
                _buildingRows?.Dispose();
                _buildingRows = null;
                _eventRows?.Dispose();
                _eventRows = null;
            }

            // The unit's records and judgements: per Slash, per operation, per piece.
            private void BuildingSummarise()
            {
                MultiStorage("at the end");
                _buildingRows?.Flush();
                _eventRows?.Flush();
                // The cuts' stages by operation: one "op" record each in the hit record (formerly building-ops.csv).
                HitLogOpsPlanned(_accepted.Count);
                foreach (Accepted a in _accepted)
                {
                    _buildingOps.TryGetValue(a.operation.value, out BuildingOp b);
                    bool known = _world.Ledger.TryGetOperation(a.operation, out LogicalCutOperation op);
                    HitLogOp(new OpRecord
                    {
                        name = a.name, operation = a.operation.value, slash = a.slash, source = a.fragment.value, acceptance = a.pending ? "Pending" : "Published",
                        acceptedFrame = a.acceptedFrame, provisionalFrame = a.provisionalFrame, finalFrame = a.publishedFrame, committedFrame = a.committedFrame,
                        acceptedTime = a.acceptedTime, provisionalTime = a.provisionalTime, finalTime = a.finalTime, committedTime = a.committedTime,
                        ledgerState = known ? op.state.ToString() : "none", pendingEnd = a.pendingEnd, hull = a.hull, fusion = a.fusion,
                        sourceDepth = b?.sourceDepth ?? -1, sourceAnchors = b?.sourceAnchors ?? -1, sourceFixed = b?.sourceFixed ?? false, cut = b?.kind,
                        normalX = b?.worldNormal.x, normalY = b?.worldNormal.y, normalZ = b?.worldNormal.z,
                        positive = known ? op.positive.value : 0, negative = known ? op.negative.value : 0,
                    });
                }

                // Per Slash: latched, hit or missed, what each hit's acceptance said. A Slash latched with no hit is a miss.
                int misses = 0;
                foreach (KeyValuePair<long, SlashTally> s in _multiSlashes)
                {
                    SlashTally t = s.Value;
                    int accepted = _accepted.Count(a => a.slash == s.Key);
                    if (t.hits == 0) misses++;
                    Log("building slash " + s.Key + ": latch frame=" + t.latchFrame + (t.hits == 0 ? " MISSED (no hit)" : "")
                        + " hits=" + t.hits + " accepted=" + accepted + " published=" + t.published + " pending=" + t.pending + " held=" + t.held
                        + " noOp(emptySide)=" + t.emptySide + " notAccepted=" + t.notAccepted + " anchorsRefused=" + t.anchorsRefused + " aborted=" + t.aborted
                        + " stale=" + t.stale + " invalid=" + t.invalid + " admissions=[" + string.Join(",", t.admissions.Select(k => k.Key + ":" + k.Value)) + "]"
                        + " rootHits=" + (t.hits - t.childHits) + " childHits=" + t.childHits + " childAccepted=" + t.childAccepted
                        + " cuts=[" + string.Join(",", _accepted.Where(a => a.slash == s.Key).Select(a => "op" + a.operation.value + ":"
                            + (_buildingOps.TryGetValue(a.operation.value, out BuildingOp b) ? b.kind + "@d" + b.sourceDepth : "?"))) + "]");
                }

                // Per piece: as it was when last seen.
                int finalPieces = 0, maxDepth = 0, fixedPieces = 0, freePieces = 0;
                foreach (KeyValuePair<int, BuildingTrack> k in _buildingTracks.OrderBy(k => k.Key))
                {
                    BuildingTrack t = k.Value;
                    maxDepth = Mathf.Max(maxDepth, t.depth);
                    if (t.endedLive)
                    {
                        finalPieces++;
                        if (t.fixedOwner) fixedPieces++; else freePieces++;
                    }

                    Log("building piece " + k.Key + (t.endedLive ? " (live at the end)" : " (cut again)") + ": from op" + t.originOp + " depth=" + t.depth
                        + " anchors=" + t.anchors + " fixed=" + t.fixedOwner
                        + (t.fixedOwner ? " fixedDrift=" + t.maxFixedDrift.ToString("F5", Inv)
                            : " limit=" + F(t.limit) + " m readback X=" + F(t.readX) + " Y=" + F(t.readY) + " Z=" + F(t.readZ) + " deg"
                              + " maxOffset=" + t.maxOffset.ToString("F4", Inv) + " maxTotal=" + t.maxTotal.ToString("F2", Inv) + (t.maxTotalAt != null ? " (" + t.maxTotalAt + ")" : "")
                              + " maxTwist=" + t.maxTwist.ToString("F2", Inv) + " maxSwingY=" + t.maxSwingY.ToString("F2", Inv) + " maxSwingZ=" + t.maxSwingZ.ToString("F2", Inv)
                              + " beyondReadback X/Y/Z=" + t.maxTwistOver.ToString("F2", Inv) + "/" + t.maxSwingYOver.ToString("F2", Inv) + "/" + t.maxSwingZOver.ToString("F2", Inv)
                              + " maxStepAngle=" + t.maxStepAngle.ToString("F2", Inv) + " reversals=" + t.reversals)
                        + " first frame " + t.firstFrame + " step " + t.firstStep + " last frame " + t.lastFrame + " maxSpeed=" + t.maxSpeed.ToString("F3", Inv)
                        + " maxComJump=" + t.maxComJump.ToString("F4", Inv) + (t.maxComJumpAt != null ? " (" + t.maxComJumpAt + ")" : "")
                        + " maxRotJump=" + t.maxRotJump.ToString("F2", Inv) + (t.maxRotJumpAt != null ? " (" + t.maxRotJumpAt + ")" : "")
                        + " atFinal com/rot=" + (t.comJumpAtFinal < 0f ? "not seen" : t.comJumpAtFinal.ToString("F4", Inv) + "/" + t.rotJumpAtFinal.ToString("F2", Inv))
                        + (t.triggers.Count > 0 ? " events=[" + string.Join("; ", t.triggers) + "]" : "")
                        + " sleepAfterCuts=" + (t.observedAny ? (t.firstSleepStep >= 0 ? "first at step " + t.firstSleepStep + ", slept " + t.sleeps + "x, woke " + t.wakes + "x, " + (t.sleeping ? "asleep" : "awake") + " at the end" : "never slept") : "not observed")
                        + "; last " + t.end);
                }

                var kinds = new HashSet<string>(_accepted.Where(a => _buildingOps.ContainsKey(a.operation.value)).Select(a => _buildingOps[a.operation.value].kind));
                List<Accepted> pend = _accepted.Where(a => a.pending).ToList();
                int held = _multiSlashes.Values.Sum(t => t.held);
                Log("building: slashes latched=" + _multiSlashes.Count + " missed=" + misses + " cuts accepted=" + _accepted.Count
                    + " (root " + _accepted.Count(a => !a.child) + ", child " + _accepted.Count(a => a.child) + ") kinds=[" + string.Join(",", kinds.OrderBy(x => x)) + "]"
                    + " pieces at the end=" + finalPieces + " (fixed " + fixedPieces + ", free " + freePieces + ") max depth=" + maxDepth
                    + " pending=" + (pend.Count > 0 ? pend.Count + ", Provisional published after " + string.Join(",", pend.Select(a => (a.provisionalFrame - a.acceptedFrame) + "f")) : "未観測")
                    + " held=" + (held > 0 ? held.ToString() : "未観測")
                    + " max live waves=" + _multiMaxWaves + " profile=" + _world.Profile.name);
                Log("provisional budget overruns (DESIGN 7.1.1): " + _world.Driver.BudgetOverrunCount
                    + (_world.Driver.BudgetOverrunCount > 0 ? ", last " + _world.Driver.LastBudgetOverrun : ""));
                List<BuildingTrack> liveFree = _buildingTracks.Values.Where(t => t.endedLive && !t.fixedOwner && t.observedAny).ToList();
                // Per free piece live at the end: whether its body's inertia was raised, when it first slept and woke in the
                // observation (physics seconds after it began), and, for one still awake, its motion and what it touches.
                ManualPhysicsClock sleepClock = CutPhysicsStep.Clock;
                float hz = sleepClock != null ? sleepClock.FrequencyHz : 45f;
                string S(long stepAt) => stepAt < 0 ? "" : ((stepAt - _observeFromStep) / hz).ToString("F2", Inv);
                using (var sleep = new StreamWriter(Path.Combine(directory, "building-sleep.csv")))
                {
                    sleep.WriteLine("fragment,depth,mass,firstSleepSeconds,sleeps,wakes,wakeSeconds,asleepAtEnd,endSpeed,endDegPerS,normalizedEnergy,sleepThreshold,contacts");
                    foreach (KeyValuePair<int, BuildingTrack> k in _buildingTracks.Where(k => k.Value.endedLive && !k.Value.fixedOwner && k.Value.observedAny).OrderBy(k => k.Key))
                    {
                        BuildingTrack t = k.Value;
                        Rigidbody b = t.owner != null ? t.owner.Body : null;
                        string contacts = "";
                        if (t.owner != null && !t.owner.IsWithdrawn)
                        {
                            // Each partner with whether it sleeps.
                            contacts = string.Join("|", Contacts(t.owner, k.Key).Split('|').Where(c => c.Length > 0).Select(c =>
                            {
                                string name = c.Split(':')[0];
                                if (name.StartsWith("f") && int.TryParse(name.Substring(1), out int other) && _buildingTracks.TryGetValue(other, out BuildingTrack o) && o.owner != null && o.owner.Body != null)
                                    return c + (o.owner.Body.IsSleeping() ? ":asleep" : ":awake");
                                return c;
                            }));
                        }

                        // The mass-normalised kinetic energy the engine compares with a body's sleep threshold: 0.5 v^2 plus
                        // 0.5 w.(I w) / m, w in the principal frame of the inertia the body carries now.
                        float energy = float.NaN;
                        if (b != null && b.mass > 0f)
                        {
                            Vector3 w = Quaternion.Inverse(b.rotation * b.inertiaTensorRotation) * b.angularVelocity;
                            Vector3 i = b.inertiaTensor;
                            energy = 0.5f * b.linearVelocity.sqrMagnitude + 0.5f * (i.x * w.x * w.x + i.y * w.y * w.y + i.z * w.z * w.z) / b.mass;
                        }

                        sleep.WriteLine(string.Join(",", k.Key, t.depth, b != null ? F(b.mass) : "", S(t.firstSleepStep), t.sleeps, t.wakes,
                            string.Join("|", t.wakeSteps.Select(S)), t.sleeping, b != null ? F(b.linearVelocity.magnitude) : "",
                            b != null ? F(b.angularVelocity.magnitude * Mathf.Rad2Deg) : "", F(energy), b != null ? F(b.sleepThreshold) : "", contacts));
                    }
                }

                Log("sleep window: " + BuildingObserveSeconds.ToString("R", Inv) + " s of physics from step " + _observeFromStep
                    + "; free pieces live at the end: " + liveFree.Count + " (asleep " + liveFree.Count(t => t.sleeping) + ")");
                Log("sleep after the last cut (free pieces live at the end, recorded not required): " + liveFree.Count + " observed, "
                    + liveFree.Count(t => t.sleeping) + " asleep at the end, " + liveFree.Count(t => t.firstSleepStep < 0) + " never slept, "
                    + liveFree.Count(t => t.wakes > 0) + " woke again after sleeping (wakes " + liveFree.Sum(t => t.wakes) + ")");
                foreach (string v in _buildingViolations) Log("building violation: " + v);
                if (_buildingViolationsDropped > 0) Log("building violations not written: " + _buildingViolationsDropped);
                foreach (KeyValuePair<string, int> r in _angleReadback) Log("[readback] " + r.Key + " (" + r.Value + " reads)");
                if (_linearRaised.Count == 0) Log("[readback] linear limit raised by the engine's minimum " + EngineLinearLimitMinimum.ToString("R", Inv) + " m (DESIGN 7.2.2): none");
                foreach (KeyValuePair<int, (HashSet<int> fragments, int reads, float requested, float readMin, float readMax)> r in _linearRaised)
                {
                    Log("[readback] linear limit raised by the engine's minimum (DESIGN 7.2.2): depth " + r.Key + ": " + r.Value.fragments.Count + " fragments, "
                        + r.Value.reads + " reads, requested L(d) " + r.Value.requested.ToString("R", Inv) + ", expected read "
                        + ExpectedLinearReadback(r.Value.requested).ToString("R", Inv) + ", read " + r.Value.readMin.ToString("R", Inv) + ".." + r.Value.readMax.ToString("R", Inv));
                }
                foreach (string e in _eventLines) Log("[quality] " + e);

                // [scenario] The unit's conditions. Vertical sinking and rest are not judged.
                if (_world.Hulls != null)
                {
                    Log("[scenario] the hull trial is on: a hull cut publishes no operation to this check (the display members' operations are the trial's), so the operation-based conditions are judged in the hull summary");
                }
                else
                {
                    Expect(_accepted.Count(a => !a.child) == 1 && _accepted.Any(a => a.child), "[scenario] the building was cut by a real hit, then its children again by later Slashes");
                }

                if (_world.Fusion == null && _world.Hulls == null)
                {
                    Expect(maxDepth >= 2, "[scenario] a child of a child was cut: multi-level re-cutting (max depth " + maxDepth + ")");
                    Expect(kinds.Contains("horizontal") && kinds.Contains("vertical") && kinds.Contains("diagonal"),
                        "[scenario] the input's accepted cuts include horizontal, vertical and diagonal planes ([" + string.Join(",", kinds.OrderBy(x => x)) + "])");
                    Expect(_multiAcceptedOnSource.Values.All(n => n == 1), "[scenario] no fragment was accepted twice (no duplicate acceptance)");
                    Expect(pend.All(a => a.provisionalFrame > a.acceptedFrame && a.publishedFrame >= 0 && a.committedFrame >= 0 && a.pendingEnd == null),
                        "[scenario] every Pending cut was published later as the same operation, then Final/Logical published and committed");
                    Expect(_accepted.All(a => _world.Ledger.TryGetOperation(a.operation, out LogicalCutOperation op) && op.state == LogicalCutOperationState.Completed),
                        "[scenario] every accepted operation completed (none lost or aborted)");
                }
                else
                {
                    Log("[scenario] the fusion is on: a group cut publishes no operation, depth or plane kind to this check (the crossed members' operations are the fusion's), so the operation-based conditions are not judged here");
                }
                Expect(_buildingViolations.Count == 0 && _buildingViolationsDropped == 0,
                    "[scenario] every building piece, every frame: of the building, its depth one more than its source's, fixed exactly when it holds anchors, a fixed piece kinematic without a D6, "
                    + "a free piece with its World D6 (to the World, Y free, the others limited, distance limit L(d)), no joint left over after its Final publication ("
                    + (_buildingViolations.Count + _buildingViolationsDropped) + " violations)");
                Expect(_buildingTracks.Values.Where(t => t.fixedOwner).All(t => t.maxFixedDrift <= FixedDriftTolerance), "[scenario] no fixed piece moved");

                // [quality] How the free pieces moved in this scenario.
                List<BuildingTrack> free = _buildingTracks.Values.Where(t => !t.fixedOwner && t.depth >= 1).ToList();
                Expect(_buildingTracks.Values.All(t => t.maxComJump <= TeleportComMetres && t.maxRotJump <= TeleportRotDegrees),
                    "[quality] no instantaneous move: no piece's COM or rotation changed in a read beyond what its velocities explain");
                Expect(_buildingTracks.Values.All(t => t.comJumpAtFinal <= TeleportComMetres && t.rotJumpAtFinal <= TeleportRotDegrees),
                    "[quality] no piece was set back or moved instantaneously at its Final handoff");
                bool d6 = _world.Profile.BuildingWorld.enabled;
                if (d6)
                {
                    Expect(free.All(t => t.maxTotal <= LargeAngleDegrees && t.maxOffset <= t.limit + LargeOffsetBeyond),
                        "[quality] no free piece made a large excursion from its D6's reference (" + LargeAngleDegrees.ToString("R", Inv) + " deg total, "
                        + LargeOffsetBeyond.ToString("R", Inv) + " m beyond its distance limit)");
                }
                else
                {
                    Log("[quality] the World D6 is off in this run: the excursion from a D6's reference is not judged");
                }

                Guarded("building fusion summary", BuildingFusionSummarise);
                Guarded("building hull summary", BuildingHullSummarise);
                Expect(free.All(t => t.reversals < ReversalCount), "[quality] no free piece moved back and forth (" + ReversalCount + " reversals of "
                    + ReversalDegrees.ToString("R", Inv) + " deg or more)");
                Expect(_fastSeen.Count == 0, "[quality] no piece flew faster than 20 m/s (no abnormal scatter)");
                Log("hit lists: " + _hitListRereads.Count + " frame(s) still showed an evaluation already read, not read again");
            }

            // Once every cut has committed: the player steps back to the front view. The XR Origin carries the head camera
            // (and the katana, which has no collider), so the view shown in the window moves with it; the check's own view is
            // already there. Nothing of the building or the physics is touched.
            private void BuildingStepBack()
            {
                GameObject origin = GameObject.Find("XR Origin");
                Camera head = Camera.main;
                if (origin == null || head == null || !FrontView(out Vector3 eye, out Quaternion want, out string what))
                {
                    Log("step back: skipped (origin " + (origin != null) + ", head " + (head != null) + ")");
                    return;
                }

                Transform o = origin.transform;
                Vector3 before = head.transform.position;
                // The head keeps its pose in the origin; the origin is turned and moved so the head ends at eye.
                Quaternion headInOrigin = Quaternion.Inverse(o.rotation) * head.transform.rotation;
                o.rotation = want * Quaternion.Inverse(headInOrigin);
                o.position += eye - head.transform.position;
                _view.transform.SetPositionAndRotation(head.transform.position, head.transform.rotation);
                _view.fieldOfView = head.fieldOfView;
                Log("step back: " + what + "; frame " + Time.frameCount + " head " + before.ToString("F2") + " -> " + head.transform.position.ToString("F2")
                    + " fov " + head.fieldOfView.ToString("F1", Inv));
            }

            private string BuildingLineageOf(LogicalFragmentId root) => BuildingReady() && root == _building.Fragment ? "building" : "other";
        }
    }
}
