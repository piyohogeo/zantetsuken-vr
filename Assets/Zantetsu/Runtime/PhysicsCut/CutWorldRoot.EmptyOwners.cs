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
            Groups = new CutFragmentGroupCollection(this, GroupSettings(profile));

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

            // No turn while the game is paused (DESIGN 4.6): what it started stays on its way until the pause ends.
            if (EmptyOwners == null || !IsReady || _ending || TerminationRequested || Zantetsu.Core.GamePause.IsPaused)
            {
                return;
            }

            // Off, and nothing it started is still on its way out: nothing at all.
            if (EmptyOwners.Settings.enabled || EmptyOwners.ActorsLeaving != 0 || EmptyOwners.ShapesLeaving != 0)
            {
                using (s_emptyOwnersMarker.Auto())
                {
                    EmptyOwners.Step();
                }
            }

            // (The groups' turn is before the frame's collection, in LateUpdate: a retirement there is reflected by this
            // very frame's collection. An empty owner retired here changes the ledger, which holds the groups' next turn.)
        }

        private static readonly ProfilerMarker s_groupsMarker = new ProfilerMarker("Zantetsu.FragmentGroups.Step");

        /// <summary>
        /// The Player argument that switches the fragment group collection on for a run, followed by the maximum of live
        /// cut fragments it starts above (DESIGN 7.10): for looking at it, and for comparing with and without it in one
        /// build, while the profile leaves it off. Every other value is the profile's.
        /// </summary>
        public const string FragmentGroupsArgument = "-zantetsuFragmentGroups";

        // The profile's settings, or -- asked for by the argument -- the same with the collection on and its maximum given.
        private CutFragmentGroupSettings GroupSettings(CutWorldProfile profile)
        {
            CutFragmentGroupSettings settings = profile.FragmentGroupCollection;
            string[] arguments = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(arguments, FragmentGroupsArgument);
            if (at < 0)
            {
                return settings;
            }

            if (at + 1 >= arguments.Length || !int.TryParse(arguments[at + 1], out int maximum) || maximum < 0)
            {
                UnityEngine.Debug.LogError(
                    "FRAGMENT GROUPS: " + FragmentGroupsArgument + " takes the maximum of live cut fragments (0 or more); it was not given one, so the profile's setting stands ("
                    + (settings.enabled ? "on" : "off") + ").", this);
                return settings;
            }

            UnityEngine.Debug.Log(
                "FRAGMENT GROUPS: on -- asked for by " + FragmentGroupsArgument + " " + maximum + ": whole groups are collected above " + maximum
                + " live cut fragments, outside both eyes' views at any distance, the farther of those looked at first; the piece lifetime does not run", this);
            return new CutFragmentGroupSettings(
                true, maximum, settings.examinePerFrame, settings.reserveSeconds,
                settings.seedSecondsPerFragment, settings.seedSecondsPerGroup);
        }

        /// <summary>The optional lifetime of cut fragments by the object they came from (DESIGN 7.10, Phase 7.1).</summary>
        public CutFragmentGroupCollection Groups { get; private set; }

        /// <summary>
        /// Switches the fragment group collection on for this world with the maximum given, every other value the
        /// profile's: what <see cref="FragmentGroupsArgument"/> does, for a host that has no command line (a device
        /// run's configuration file). To be called before the run's cuts; false when the world has no collection.
        /// </summary>
        public bool EnableFragmentGroups(int maximum, string askedBy)
        {
            if (Groups == null || maximum < 0)
            {
                return false;
            }

            CutFragmentGroupSettings settings = Groups.Settings;
            Groups.Settings = new CutFragmentGroupSettings(
                true, maximum, settings.examinePerFrame, settings.reserveSeconds,
                settings.seedSecondsPerFragment, settings.seedSecondsPerGroup);
            UnityEngine.Debug.Log(
                "FRAGMENT GROUPS: on -- asked for by " + askedBy + " " + maximum + ": whole groups are collected above " + maximum
                + " live cut fragments, outside both eyes' views at any distance, the farther of those looked at first; the piece lifetime does not run", this);
            return true;
        }

        /// <summary>
        /// The display's growths and replacements -- its room, its snapshot, a GPU object, the reference table -- as one
        /// number that moves when any of them does: what optional work compares to know a frame grew something.
        /// </summary>
        internal long DisplayRoomSerial()
        {
            long room = References != null ? References.GrowthCount : 0;
            if (Display != null)
            {
                room += (long)Display.RoomGrowths + Display.SnapshotRegrowths + Display.GpuReplacements + Display.GpuGrowthCount;
            }

            return room;
        }

        private void ReleaseEmptyOwners()
        {
            if (_stepEmptyOwners != null)
            {
                CutPhysicsStep.LeaveAfterCollect(_stepEmptyOwners);
                _stepEmptyOwners = null;
            }

            EmptyOwners = null;
            Groups = null;
        }
    }
}
