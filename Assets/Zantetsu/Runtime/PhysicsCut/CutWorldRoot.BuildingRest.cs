using System.Collections.Generic;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The trial rest of building pieces (<see cref="BuildingRest"/>) on this world: what the owner correspondence
    /// tells it, and its turn each frame. Off by the profile's default; then nothing here subscribes to anything.
    /// </summary>
    public sealed partial class CutWorldRoot
    {
        /// <summary>The optional trial rest of building pieces (2026-09-29), stepped once a frame after the step decision.</summary>
        public BuildingRest Rest { get; private set; }

        private long _restStepId;
        private readonly List<Collider> _restColliders = new List<Collider>();

        private static readonly Unity.Profiling.ProfilerMarker s_restMarker =
            new Unity.Profiling.ProfilerMarker("Zantetsu.BuildingRest.Step");

        private void MakeRest(CutWorldProfile profile)
        {
            Rest = new BuildingRest(profile.BuildingRest);
            if (!Rest.Enabled)
            {
                return;
            }

            _restStepId = CutPhysicsStep.Clock.StepId;
            Rest.CutInProgress = fragment => Ledger.TryGetActiveOperation(fragment, out _);
            Owners.OwnerAdded += OnRestOwnerAdded;
            Owners.OwnerRetiring += OnRestOwnerRetiring;
            Owners.ProvisionalAdded += OnRestProvisionalAdded;
            Owners.ProvisionalEnding += OnRestProvisionalEnding;
        }

        private void ReleaseRest()
        {
            if (Rest == null)
            {
                return;
            }

            if (Rest.Enabled && Owners != null)
            {
                Owners.OwnerAdded -= OnRestOwnerAdded;
                Owners.OwnerRetiring -= OnRestOwnerRetiring;
                Owners.ProvisionalAdded -= OnRestProvisionalAdded;
                Owners.ProvisionalEnding -= OnRestProvisionalEnding;
            }

            Rest.Dispose();
            Rest = null;
        }

        /// <summary>The rest's turn: before this frame's step, with the steps that actually happened since its last turn.</summary>
        private void StepRest()
        {
            if (Rest == null || !Rest.Enabled)
            {
                return;
            }

            using (s_restMarker.Auto())
            {
                ManualPhysicsClock clock = CutPhysicsStep.Clock;
                int steps = (int)(clock.StepId - _restStepId);
                _restStepId = clock.StepId;
                Rest.Step(steps, clock.StepSeconds, clock.PhysicsSeconds);
            }
        }

        private void OnRestOwnerAdded(LogicalFragmentId fragment, PhysicsFragmentOwner owner)
        {
            if (!owner.Building.IsBuildingDerived || owner.Body == null || owner.Root == null)
            {
                return;
            }

            // The key: a registered building is its own; a child inherits its source's -- through the re-cut's record when
            // the source was already forgotten at the Provisional publication, or from the source itself on the direct
            // Final path, where this registration is the re-cut's physical publication (the same operation releases at
            // most once).
            int key = fragment.value;
            if (owner.Building.SplitDepth > 0 && Ledger.TryGetOrigin(fragment, out CutOperationId operation, out float _))
            {
                if (Rest.TryGetReCutKey(operation.value, out int recorded))
                {
                    key = recorded;
                }
                else if (Ledger.TryGetOperation(operation, out LogicalCutOperation cut) && Rest.TryGetBuildingKey(cut.source, out int inherited))
                {
                    key = inherited;
                    Rest.ReCut(cut.source, operation.value, "re-cut " + operation.value + " of piece " + cut.source.value + " published (Final)");
                }
            }

            _restColliders.Clear();
            owner.Root.GetComponentsInChildren(true, _restColliders);
            Rest.Track(owner.Body, fragment, key, _restColliders, CutPhysicsStep.Clock.PhysicsSeconds, owner.FixedByAnchors);
        }

        private void OnRestOwnerRetiring(LogicalFragmentId fragment, PhysicsFragmentOwner owner)
        {
            if (owner.Body != null)
            {
                Rest.Untrack(owner.Body, "piece " + fragment.value + " retired");
            }
            else
            {
                Rest.Untrack(fragment, "piece " + fragment.value + " retired");
            }
        }

        private void OnRestProvisionalAdded(ProvisionalOwnerPair pair)
        {
            if (!pair.ChildLineage.IsBuildingDerived)
            {
                return;
            }

            // The source has just left the scene: in Kinematic mode what it supported is released locally (the re-cut,
            // which forgets the source); in Sleep mode its group is woken and it is forgotten. Its two actors start their
            // own judgement now, under the same building.
            int key = Rest.TryGetBuildingKey(pair.Source, out int inherited) ? inherited : pair.Source.value;
            Rest.ReCut(pair.Source, pair.Operation.value, "re-cut " + pair.Operation.value + " of piece " + pair.Source.value + " published (Provisional)");
            Rest.Untrack(pair.Source, "re-cut of piece " + pair.Source.value + " published");
            TrackSide(pair.Positive, key);
            TrackSide(pair.Negative, key);
        }

        private void TrackSide(PhysicsOwnerSide side, int key)
        {
            if (side?.Body == null)
            {
                return;
            }

            _restColliders.Clear();
            foreach (MeshCollider collider in side.Colliders)
            {
                _restColliders.Add(collider);
            }

            Rest.Track(side.Body, default, key, _restColliders, CutPhysicsStep.Clock.PhysicsSeconds, side.FixedByAnchors);
        }

        private void OnRestProvisionalEnding(ProvisionalOwnerPair pair)
        {
            if (pair.Positive?.Body != null)
            {
                Rest.Untrack(pair.Positive.Body, "cut " + pair.Operation.value + " ended");
            }

            if (pair.Negative?.Body != null)
            {
                Rest.Untrack(pair.Negative.Body, "cut " + pair.Operation.value + " ended");
            }
        }
    }
}
