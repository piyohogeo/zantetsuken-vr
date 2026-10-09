using System;
using Unity.Profiling;

namespace Zantetsu.PhysicsCut
{
    /// <summary>What a frame has had that optional Main work does not start beside (<see cref="CutWorldRoot.FrameEventsHold"/>).</summary>
    internal enum CutFrameHold
    {
        None = 0,
        HeavyWork,
        GarbageCollection,
        CutInFlight,
    }

    /// <summary>
    /// The delayed collection of physics objects with nothing to show (DESIGN 7.9.3, Phase 5.7): made with the world,
    /// given one turn a frame after the frame's collections, and let go with the world.
    /// </summary>
    public partial class CutWorldRoot
    {
        private static readonly ProfilerMarker s_emptyOwnersMarker = new ProfilerMarker("Zantetsu.EmptyOwnerCollection.Step");

        private Action _stepEmptyOwners;

        /// <summary>The optional collection of live fragments whose geometry is settled as empty (DESIGN 7.9.3).</summary>
        public CutEmptyOwnerCollection EmptyOwners { get; private set; }

        private void MakeEmptyOwners(CutWorldProfile profile)
        {
            EmptyOwners = new CutEmptyOwnerCollection(this, profile.EmptyOwnerCollection);

            // After everything a frame collects: what the frame published, grew and compacted is known by then.
            _stepEmptyOwners = StepEmptyOwners;
            CutPhysicsStep.JoinAfterCollect(_stepEmptyOwners);
        }

        private void StepEmptyOwners()
        {
            if (this == null)
            {
                // Destroyed without its ending having finished: nothing of it takes a turn any more.
                ReleaseEmptyOwners();
                return;
            }

            if (EmptyOwners == null || !IsReady || _ending || TerminationRequested)
            {
                return;
            }

            if (!EmptyOwners.Settings.enabled && EmptyOwners.ActorsLeaving == 0 && EmptyOwners.ShapesLeaving == 0)
            {
                // Off, and nothing it started is still on its way out: nothing at all.
                return;
            }

            using (s_emptyOwnersMarker.Auto())
            {
                EmptyOwners.Step();
            }
        }

        private void ReleaseEmptyOwners()
        {
            if (_stepEmptyOwners != null)
            {
                CutPhysicsStep.LeaveAfterCollect(_stepEmptyOwners);
                _stepEmptyOwners = null;
            }

            EmptyOwners = null;
        }
    }
}
