using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A placed cuttable before its first cut (TL, 2026-10-03; DESIGN 4.5.1, 4.5.2): it stays the scene's own -- its
    /// renderers, materials, placement and colliders as the scene has them -- and is a cut target through its input's
    /// convexes (<see cref="ISlashPlacedTarget"/>), with nothing of it in the cut world's storage, display or physics. A hit
    /// prepares the cut input then, for this one only, and only as far as the cut goes on:
    /// <list type="bullet">
    /// <item>a prop, in a prepared character's order (VpPreparedCharacterCut): its convexes posed where it stands and the
    /// plane classified with the hit's own lease -- an empty side or a full budget answer there, before anything is made,
    /// and it stays a candidate as it was; its display geometry appended -- room the storage will give back answers as a
    /// full budget does, a geometry the cut input gate refuses (or room that cannot come back) refuses it for good, the
    /// instance as it was; shown; registered as an owner fixed by its anchors on a collider-free body (the instance's own
    /// colliders stand until the cut is published); the cut asked with that lease. The instance leaves when the source
    /// owner is withdrawn at the cut's publication (its renderers and colliders switched off, once), its renderers already
    /// when a Pending cut's display takes the drawing over;</item>
    /// <item>a building, into the hull trial without being shown (<see cref="PlacedCuttableRegistration.RegisterHullDeferred"/>),
    /// and the hit handed to the new group's own acceptance. Pending, its own drawing and colliders stay until the first
    /// cut's publication registers its display and the display cut takes the drawing over; they leave right after it, in
    /// that same call. Answered at once without acceptance, it is taken back out (nothing was shown) and stays a candidate;
    /// a first cut that ends later without a publication takes it back out as well, the instance as it was, refused for
    /// good from then. A building the storage or the trial refuses leaves the instance as it was (for good).</item>
    /// </list>
    /// What it holds before a cut (its hit shape, the prepared convexes and their cooked meshes) is given back by
    /// <see cref="Dispose"/>; a cut's owner and group are the world's from their registration.
    /// </summary>
    public sealed class PlacedCuttableCandidate : ISlashPlacedTarget, IDisposable
    {
        private readonly CutWorldRoot _world;
        private readonly PlacedCuttableInput _data;
        private readonly Transform _target;
        private readonly GameObject _instance;   // the target's GameObject: a Transform's never changes, so it is held, not asked for again
        private readonly Renderer[] _renderers;
        private readonly Collider[] _colliders;
        private readonly float _mass;
        private readonly int _materialIndex;
        private readonly float3[] _anchors;
        private VpCharacterHitShape _hitShape;
        private VpPreparedPhysicsInput _physics;   // a prop's: its convexes, posed for a cut attempt
        private float4x4[] _identity;
        private bool _withdrawn, _disposed;

        public enum CandidateState { Candidate, Cut, Refused, Disposed }

        public CandidateState State { get; private set; } = CandidateState.Candidate;

        /// <summary>Why it was refused for good, or the last refusal it stays a candidate after.</summary>
        public string LastRefusal { get; private set; }

        public bool IsBuilding => _data.isBuilding;
        public string Name => _data.name;

        /// <summary>The building's registration into the hull trial, once a hit made it; null otherwise.</summary>
        public PlacedCuttableRegistration Registration { get; private set; }

        /// <summary>How many hits it answered before its cut went on (observation).</summary>
        public int Refusals { get; private set; }

        public LogicalFragmentId Source { get; private set; }

        public CutOperationId Operation { get; private set; }

        public VpCharacterHitShape HitShape => _hitShape;

        public float4x4 FrameToWorld => float4x4.TRS(_target.position, _target.rotation, new float3(1f));

        // Whether the instance stands and is active is asked of the engine each time; only the way to its GameObject is
        // held. (A Transform and its GameObject are destroyed together, so asking either whether it exists is the same.)
        public bool IsHitTarget => State == CandidateState.Candidate && !_disposed
            && (stateFromHeldObject ? _instance != null && _instance.activeInHierarchy : _target != null && _target.gameObject.activeInHierarchy)
            && _world != null && _world.IsReady && !_world.IsEnding;

        /// <summary>Off, the state is asked through the Transform as before (three engine calls, not two) -- kept for the comparison.</summary>
        internal static bool stateFromHeldObject = true;

        public bool TryGetHitFrame(out float4x4 frameToWorld)
        {
            if (!IsHitTarget)
            {
                frameToWorld = default;
                return false;
            }

            // The instance's position and rotation in one reading, as FrameToWorld reads them one after the other.
            _target.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            frameToWorld = float4x4.TRS(position, rotation, new float3(1f));
            return true;
        }

        public bool TryGetHitPose(out float3 position, out quaternion rotation)
        {
            if (!IsHitTarget)
            {
                position = default;
                rotation = default;
                return false;
            }

            _target.GetPositionAndRotation(out Vector3 p, out Quaternion r);
            position = p;
            rotation = r;
            return true;
        }

        /// <param name="data">The input at the instance's size (a uniform scale already in it); <paramref name="target"/> places it, unscaled.</param>
        public PlacedCuttableCandidate(CutWorldRoot world, PlacedCuttableInput data, Transform target, Renderer[] instanceRenderers, Collider[] instanceColliders,
            float mass, int materialIndex = 0)
        {
            if (world == null || data == null || target == null) throw new ArgumentNullException(world == null ? nameof(world) : data == null ? nameof(data) : nameof(target));
            // Anchors as authored, none included (2026-10-03, TL): what fixes a piece after a cut is the anchors it holds.
            if (!data.isCuttable || data.hulls == null || data.hulls.Length == 0 || (data.isBuilding && data.hulls.Length != 1))
            {
                throw new InvalidOperationException(data.name + ": not a cuttable " + (data.isBuilding ? "building with one convex" : "prop with its convexes"));
            }

            _world = world;
            _data = data;
            _target = target;
            _instance = target.gameObject;
            _renderers = instanceRenderers ?? Array.Empty<Renderer>();
            _colliders = instanceColliders ?? Array.Empty<Collider>();
            _mass = mass;
            _materialIndex = materialIndex;
            _anchors = (data.anchors ?? new Vector3[0]).Select(v => (float3)v).ToArray();
            var natives = new List<IDisposable>();
            try
            {
                ConvexBrepBank bank = PlacedCuttableRegistration.BuildBank(data.hulls, natives, out List<ConvexBrepRange> ranges);
                _hitShape = new VpCharacterHitShape(bank, ranges);   // a copy
                if (!data.isBuilding)
                {
                    _physics = new VpPreparedPhysicsInput(bank, ranges);   // a copy, its convex meshes cooked once here
                    _identity = Enumerable.Repeat(float4x4.identity, ranges.Count).ToArray();
                }
            }
            catch
            {
                _hitShape?.Dispose();
                _physics?.Dispose();
                throw;
            }
            finally
            {
                foreach (IDisposable n in natives) n.Dispose();
            }
        }

        public bool TryIdentify(out LogicalFragmentId fragment)
        {
            fragment = Source;
            if (Source.IsSet) return true;
            if (!IsHitTarget) return false;
            // A prop's fragment carries its anchors, as a registered body's does; a building's anchors are its group's.
            Source = _data.isBuilding || _anchors.Length == 0 ? _world.Ledger.AddFragment() : _world.Ledger.AddFragment(_anchors);
            fragment = Source;
            return true;
        }

        public SlashPlacedCutResult TryCut(in SlashPlacedHit hit)
        {
            if (!IsHitTarget || !Source.IsSet) return Answer(ProvisionalCutAcceptance.NotAccepted, LogicalCutAdmission.SourceNotLive, false, "not a hit target now");
            if (!math.all(math.isfinite(hit.plane)) || math.lengthsq(hit.plane.xyz) <= 0f) return Answer(ProvisionalCutAcceptance.InvalidRequest, LogicalCutAdmission.NoOp, false, "the plane");
            if (!_world.BeginPreparedCharacterCall()) return Answer(ProvisionalCutAcceptance.NotAccepted, LogicalCutAdmission.SourceNotLive, false, "the world is in a prepared cut, ending or terminating");
            try
            {
                return _data.isBuilding ? CutBuilding(in hit) : CutProp(in hit);
            }
            finally
            {
                _world.EndPreparedCharacterCall();
            }
        }

        private SlashPlacedCutResult CutProp(in SlashPlacedHit hit)
        {
            ProvisionalCutDriver.PreparedCutLease lease = null;
            PhysicsOwnerShape taken = null;
            // The instance's own body, when it has one, gives the mass (the lease and the owner take the same) and -- not
            // kinematic, so moving as the scene simulates it -- the motion and the mass distribution its pieces start from.
            // Whether anything is fixed is the anchors' alone (TL, 2026-10-03): the body says how it moves, not what holds it.
            bool free = _anchors.Length == 0;
            Rigidbody own = _target.GetComponent<Rigidbody>();
            bool moving = own != null && !own.isKinematic;
            float mass = own != null ? own.mass : _mass;
            GameObject actor = null;
            VpStoredGeometry geometry = default;
            bool stored = false, shown = false, registered = false;
            try
            {
                // The convexes where it stands (its own frame), and the plane classified once, with the lease the request uses.
                if (!_physics.TryPose(_identity, out _) || !_world.Driver.TryPrepareFreshCut(_physics, hit.plane, mass, out lease))
                {
                    return Refuse(ProvisionalCutAcceptance.InvalidRequest, LogicalCutAdmission.NoOp, "the convexes could not be posed or classified");
                }

                ProvisionalCutDriver.FreshCutEligibility eligible = _world.Driver.AssessFreshCut(lease);
                if (eligible == ProvisionalCutDriver.FreshCutEligibility.EmptySide || eligible == ProvisionalCutDriver.FreshCutEligibility.Full)
                {
                    lease.Dispose();
                    lease = null;
                    if (!_physics.TryRearmAfterRefusal()) return Refuse(ProvisionalCutAcceptance.InvalidRequest, LogicalCutAdmission.NoOp, "the convexes could not be armed again");
                    return eligible == ProvisionalCutDriver.FreshCutEligibility.EmptySide
                        ? Answer(ProvisionalCutAcceptance.EmptySide, LogicalCutAdmission.NoOp, false, "an empty side")
                        : Answer(ProvisionalCutAcceptance.NotAccepted, LogicalCutAdmission.Full, false, "the budget is full");
                }

                if (eligible != ProvisionalCutDriver.FreshCutEligibility.Ready)
                {
                    return Refuse(ProvisionalCutAcceptance.InvalidRequest, LogicalCutAdmission.NoOp, "fresh cut eligibility " + eligible);
                }

                // Its display input, only now.
                VpCpuGeometryStorage storage = _world.Storage;
                int roomRefusals = storage.RoomRefusalCount;
                VpRenderVertex[] vertices;
                vertices = _data.Vertices();
                if (!storage.TryAppendCuttable(vertices, _data.indices, _data.topology, _data.topologyCount,
                        new[] { new VpGeometrySubmesh(0, _data.indices.Length, _materialIndex) }, out geometry, out VpCutInputVerdict verdict))
                {
                    if (storage.RoomRefusalCount != roomRefusals)
                    {
                        if (storage.BackingFailure == null && storage.LastRefusalIsTemporary)
                        {
                            // Room others hold and will give back: answered as a full budget is, a candidate still.
                            lease.Dispose();
                            lease = null;
                            if (!_physics.TryRearmAfterRefusal()) return Refuse(ProvisionalCutAcceptance.InvalidRequest, LogicalCutAdmission.NoOp, "the convexes could not be armed again");
                            return Answer(ProvisionalCutAcceptance.NotAccepted, LogicalCutAdmission.Full, false, "the storage's room is held for now: " + storage.DescribeRoom());
                        }

                        _world.RequestTermination("the display input of a hit placed cuttable could not be given room: " + storage.DescribeRoom());
                    }

                    return Refuse(ProvisionalCutAcceptance.InvalidRequest, LogicalCutAdmission.NoOp, "the geometry was refused: " + verdict);
                }

                stored = true;
                Matrix4x4 frameToWorld = Matrix4x4.TRS(_target.position, _target.rotation, Vector3.one);
                if (!_world.Display.TryShow(Source, geometry, frameToWorld, Matrix4x4.identity, Array.Empty<VpClipBoundary>()))
                {
                    return Refuse(ProvisionalCutAcceptance.InvalidRequest, LogicalCutAdmission.NoOp, "the display refused it");
                }

                shown = true;
                // The source owner: fixed by its anchors, its body without colliders of its own (the instance's stand until the
                // publication). A free body (no anchor) is the motion the cut's sides start from, as a prepared character's
                // source is: not kinematic, no gravity of its own and no collision (it has no collider), its mass and its
                // motion the instance's own body's when it has one -- the pose and velocity it has at the hit -- else at rest.
                actor = new GameObject("Prop " + _data.name);
                actor.transform.SetPositionAndRotation(_target.position, _target.rotation);
                var body = actor.AddComponent<Rigidbody>();
                body.useGravity = !free;
                body.mass = mass;
                body.isKinematic = !free;
                if (free)
                {
                    body.detectCollisions = false;
                    if (moving)
                    {
                        // Its motion at the hit, about its own centre of mass, with its own inertia (the sides' velocities
                        // are worked out from the source's centre of mass, velocity and angular velocity).
                        body.automaticCenterOfMass = false;
                        body.automaticInertiaTensor = false;
                        body.centerOfMass = actor.transform.InverseTransformPoint(own.worldCenterOfMass);
                        body.inertiaTensor = own.inertiaTensor;
                        body.inertiaTensorRotation = Quaternion.Inverse(actor.transform.rotation) * own.rotation * own.inertiaTensorRotation;
                        body.linearVelocity = own.linearVelocity;
                        body.angularVelocity = own.angularVelocity;
                    }
                }

                taken = _physics.TakeShape();
                PhysicsFragmentOwner owner = null;
                ProvisionalCutAcceptance accepted;
                ProvisionalCutTransaction transaction;
                LogicalCutAdmission admission;
                try
                {
                    try
                    {
                        owner = _world.Owners.RegisterAuthored(Source, actor, body, taken, !free, Matrix4x4.identity, false);
                    }
                    finally
                    {
                        // An exception from the owner's registration can come after the owner was added (its listeners):
                        // registered or not is what the registry holds.
                        registered = _world.Owners.TryGet(Source, out PhysicsFragmentOwner found) && ReferenceEquals(found.Shape, taken);
                        if (registered) owner = found;
                    }

                    cutPropHookForTest?.Invoke("after owner");
                    _world.Geometry.RegisterBaseGeometry(Source, geometry, Matrix4x4.identity);
                    cutPropHookForTest?.Invoke("after base geometry");
                    owner.PreparedSourceWithdrawal = new InstanceWithdrawal(this);
                    State = CandidateState.Cut;
                    var ask = new ProvisionalCutAsk
                    {
                        source = Source, plane = hit.plane, renderAnchor = hit.renderAnchor,
                        positiveSeparationImpulse = hit.positiveSeparationImpulse, negativeSeparationImpulse = hit.negativeSeparationImpulse,
                        slashId = hit.slashId, adoptedPlaneId = hit.planeId, adoptedPlaneWorld = hit.planeWorld,
                    };
                    cutPropHookForTest?.Invoke("before request");
                    accepted = _world.Driver.RequestPreparedCut(in ask, lease, out transaction, out admission);
                }
                catch (Exception) when (registered)
                {
                    AfterRegisteredThrew(owner);
                    throw;
                }

                if (transaction != null) Operation = transaction.Operation;
                if (accepted == ProvisionalCutAcceptance.Pending)
                {
                    // The admitted cut's display draws it from here: the instance's drawing goes now; its colliders stand until the publication.
                    SwitchOff(_renderers);
                }
                else if (accepted != ProvisionalCutAcceptance.Published && !_withdrawn)
                {
                    // Not expected after a ready lease. The display draws the whole of it from here, so the instance goes now.
                    LastRefusal = "registered, but the cut answered " + accepted + " (" + admission + "): the display draws it whole";
                    Withdraw();
                }

                return new SlashPlacedCutResult(accepted, admission, Operation, true);
            }
            finally
            {
                lease?.Dispose();
                if (!registered)
                {
                    // Nothing of it became the world's: what this attempt made goes back, the instance as it was.
                    taken?.Dispose();
                    if (actor != null)
                    {
                        actor.SetActive(false);
                        UnityEngine.Object.Destroy(actor);
                    }

                    if (stored && !shown)
                    {
                        // Never shown: given back here (the index range retired, the vertex room released).
                        VpCpuGeometryStorage storage = _world.Storage;
                        bool grouped = storage.TryGetVertexGroup(geometry, out int vertexGroup);
                        if (storage.TryRetireIndices(geometry.indexRange) && grouped) storage.TryReleaseVertexGroup(vertexGroup);
                    }

                    if (shown && State != CandidateState.Refused)
                    {
                        // Shown, then its registration threw: refused for good. The display lets the geometry go at its
                        // collection once the fragment retires below, and the world's reclamation gives its room back.
                        State = CandidateState.Refused;
                        LastRefusal = "its owner's registration failed after the display took it";
                    }

                    if (State == CandidateState.Refused && Source.IsSet && _world.Ledger.IsCurrentTarget(Source)) _world.Ledger.Retire(Source);
                }
            }
        }

        private SlashPlacedCutResult CutBuilding(in SlashPlacedHit hit)
        {
            GameObject standIn = null;
            try
            {
                Transform at = _target;
                if ((_target.lossyScale - Vector3.one).sqrMagnitude > 1e-8f)
                {
                    standIn = new GameObject(_target.name + " (unscaled stand-in)");
                    standIn.transform.SetPositionAndRotation(_target.position, _target.rotation);
                    at = standIn.transform;
                }

                try
                {
                    Registration = PlacedCuttableRegistration.RegisterHullDeferred(_world, _data, at, _mass, _materialIndex, Source, Withdraw, TakenBack);
                }
                catch (InvalidOperationException e)
                {
                    SlashPlacedCutResult refused = Refuse(ProvisionalCutAcceptance.InvalidRequest, LogicalCutAdmission.NoOp, e.Message);
                    if (Source.IsSet && _world.Ledger.IsCurrentTarget(Source)) _world.Ledger.Retire(Source);
                    return refused;
                }

                State = CandidateState.Cut;
                // The group's own acceptance, the plane in its Root's frame. Nothing is shown and the instance stands until a publication.
                HullGroup group = Registration.Group;
                float4x4 rootToWorld = (float4x4)group.Root.transform.localToWorldMatrix;
                float4 planeRoot = math.mul(math.transpose(rootToWorld), hit.planeWorld);
                planeRoot /= math.length(planeRoot.xyz);
                ProvisionalCutAcceptance accepted;
                try
                {
                    accepted = ((ISlashHullTarget)group).TryCut(planeRoot, hit.planeWorld, hit.slashId, hit.planeId, hit.at, hit.travelWorld);
                }
                catch (Exception e)
                {
                    _world.Hulls.TakeBackUncut(group, "its first cut threw: " + e.Message);
                    throw;
                }

                if (accepted == ProvisionalCutAcceptance.Pending || accepted == ProvisionalCutAcceptance.Held)
                {
                    return new SlashPlacedCutResult(accepted, LogicalCutAdmission.NoOp, default, true);
                }

                // Answered at once without acceptance: taken back out (nothing was shown), and a candidate again with a fresh fragment.
                _world.Hulls.TakeBackUncut(group, "its first cut was answered " + accepted);
                State = CandidateState.Candidate;
                Source = default;
                return Answer(accepted, LogicalCutAdmission.NoOp, false, "its first cut was answered " + accepted + "; taken back out, the instance as it was");
            }
            finally
            {
                if (standIn != null) UnityEngine.Object.Destroy(standIn);
            }
        }

        /// <summary>Tests only: called in a prop's first cut with "after owner", "after base geometry" and "before request".</summary>
        internal static Action<string> cutPropHookForTest;

        /// <summary>Tests only: whether it still holds its prepared physics input (a prop's convexes and their cooked meshes).</summary>
        internal bool HoldsPreparedPhysicsForTest => _physics != null;

        // A prop's first cut threw once its owner was registered (2026-10-03). The display has its geometry (shown), so that
        // is the world's in either case, given back by its reclamation once the fragment ends.
        // - The cut not admitted (the fragment still a target, no operation of it): nothing of the attempt stays -- the
        //   fragment retires, its owner (the actor, the convexes taken) is retired and its base geometry forgotten; the
        //   instance was not touched; refused for good.
        // - The cut admitted (an operation of the fragment, or the fragment no longer a target): the driver's from here.
        //   Its record holds the cut and its ending -- the abort, or the world's ending entrance -- is what closes it, so
        //   nothing is given back here. The display draws it from here, so the instance's drawing goes, as on Pending;
        //   its colliders stand until a publication withdraws it.
        private void AfterRegisteredThrew(PhysicsFragmentOwner owner)
        {
            LogicalCutLedger ledger = _world.Ledger;
            bool admitted = ledger.TryGetActiveOperation(Source, out CutOperationId operation) || !ledger.IsCurrentTarget(Source);
            if (admitted)
            {
                State = CandidateState.Cut;
                if (operation.IsSet) Operation = operation;
                if (!_withdrawn) SwitchOff(_renderers);
                LastRefusal = "its cut threw after its admission: the driver's record holds it until it is ended";
                return;
            }

            State = CandidateState.Refused;
            LastRefusal = "its first cut threw before its admission: undone, the instance as it was";
            ledger.Retire(Source);
            if (_world.Owners.TryGet(Source, out PhysicsFragmentOwner found) && ReferenceEquals(found, owner))
            {
                // Its retirement withdraws the owner, which would withdraw the instance too: the instance is not the cut's
                // here, so the owner lets go of it first.
                owner.PreparedSourceWithdrawal = null;
                _world.Owners.Retire(Source);
            }

            _world.Geometry.Forget(Source);
            Debug.Log("PLACED CUTTABLE: " + _data.name + " " + LastRefusal);
        }

        // The hull trial took the building back out: nothing of it was shown, the instance stands as it was. Refused for good
        // when its first cut ended after the hit's answer (a candidate again only when that answer itself refused, above).
        private void TakenBack(string why)
        {
            Registration = null;
            State = CandidateState.Refused;
            LastRefusal = "taken back out before its first cut: " + why;
            Debug.Log("PLACED CUTTABLE: " + _data.name + " " + LastRefusal + " (the instance as it was)");
        }

        private SlashPlacedCutResult Answer(ProvisionalCutAcceptance acceptance, LogicalCutAdmission admission, bool done, string why)
        {
            Refusals++;
            LastRefusal = why;
            return new SlashPlacedCutResult(acceptance, admission, default, done);
        }

        // Refused for good: the instance as it was, no longer a candidate.
        private SlashPlacedCutResult Refuse(ProvisionalCutAcceptance acceptance, LogicalCutAdmission admission, string why)
        {
            State = CandidateState.Refused;
            Debug.Log("PLACED CUTTABLE: " + _data.name + " refused for good at its first cut, the instance as it was: " + why);
            return Answer(acceptance, admission, true, why);
        }

        private static void SwitchOff(Renderer[] renderers)
        {
            foreach (Renderer r in renderers) if (r != null) r.enabled = false;
        }

        // The instance leaves, once: its renderers and colliders switched off (the source owner's withdrawal, or a cut that drew it whole).
        private void Withdraw()
        {
            if (_withdrawn) return;
            _withdrawn = true;
            SwitchOff(_renderers);
            foreach (Collider c in _colliders) if (c != null) c.enabled = false;
            // An instance with a body of its own (a free one, moving) stops with it: its pieces carry its motion on.
            if (_target != null)
            {
                foreach (Rigidbody r in _target.GetComponentsInChildren<Rigidbody>())
                {
                    r.isKinematic = true;
                    r.detectCollisions = false;
                }
            }
        }

        private sealed class InstanceWithdrawal : IPreparedSourceWithdrawal
        {
            private readonly PlacedCuttableCandidate _of;
            public InstanceWithdrawal(PlacedCuttableCandidate of) => _of = of;
            public void Withdraw() => _of.Withdraw();
        }

        /// <summary>Whether the instance has left (its renderers and colliders switched off by its cut).</summary>
        public bool IsWithdrawn => _withdrawn;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (State == CandidateState.Candidate)
            {
                State = CandidateState.Disposed;
                // A fragment a hit identified it as, never cut: retired, as an identified character's is.
                if (Source.IsSet && _world != null && !_world.IsReleased && _world.Ledger != null && _world.Ledger.IsCurrentTarget(Source)) _world.Ledger.Retire(Source);
            }

            _hitShape?.Dispose();
            _hitShape = null;
            _physics?.Dispose();   // nothing after its shape was taken by an owner
            _physics = null;
        }
    }
}
