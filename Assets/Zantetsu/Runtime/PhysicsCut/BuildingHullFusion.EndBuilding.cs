#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Collections.Generic;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The end of one building that was cut (DESIGN 7.10, Phase 7.1): the entrance a group lifetime uses to end a whole
    /// building at one boundary. It is not <see cref="Unregister"/> (a registration that did not complete) nor
    /// <see cref="TakeBackUncut"/> (a first cut that ended unpublished): those end a building nothing of which was cut,
    /// and neither looks at what a cut building has under way.
    /// <para>
    /// **The always-kinematic mode only** -- one group a building, never split, its members display fragments with no
    /// physics of their own. A building of the other mode (several groups, sibling constraints, staged stops) is
    /// answered as not quiet and left as it is.
    /// </para>
    /// </summary>
    public sealed partial class BuildingHullFusion
    {
        /// <summary>Buildings ended whole by <see cref="EndBuilding"/>.</summary>
        public int BuildingsEnded { get; private set; }

        /// <summary>
        /// Said when <see cref="EndBuilding"/> has ended a building, with the group that was its (gone by then, its
        /// <see cref="HullGroup.Building"/> and <see cref="HullGroup.Id"/> still its own). For a check that counts the
        /// buildings: nothing of the product listens.
        /// </summary>
        public event Action<HullGroup> BuildingEnded;

        /// <summary>
        /// Whether nothing of <paramref name="group"/>'s building is under way, so that it can be ended whole: the group
        /// idle and handed over, and no cut in preparation, held hit, unanswered hit, hull update, drop, candidate,
        /// waiting request, open display operation or cut-limit wait of it. Reads only; the reason when it is not.
        /// </summary>
        public bool IsBuildingQuiet(HullGroup group, out string why)
        {
            why = null;
            if (group == null || group.State == HullGroupState.Gone || !ReferenceEquals(group.owner, this) || group.Root == null)
            {
                why = "the group is gone";
                return false;
            }

            if (!_settings.kinematicDisplay)
            {
                why = "not the always-kinematic mode";
                return false;
            }

            if (group.Handover != null)
            {
                why = "its first cut is not published";
                return false;
            }

            int building = group.Building;
            foreach (HullGroup g in _groups)
            {
                if (g.Building == building && !ReferenceEquals(g, group) && g.State != HullGroupState.Gone)
                {
                    why = "the building has more than one group";
                    return false;
                }
            }

            if (BuildingBusy(building))
            {
                why = "the building is cutting, awaits an aggregation or has display operations open";
                return false;
            }

            foreach (PendingCut c in _cuts) if (ReferenceEquals(c.group, group)) { why = "a cut of it is in preparation"; return false; }
            foreach (HeldHit h in _held) if (ReferenceEquals(h.group, group)) { why = "a hit of it is held"; return false; }
            foreach (HullHit h in _hits) if (h.group == group.Id && h.IsPending) { why = "a hit of it is unanswered"; return false; }
            foreach (DisplayAnimation a in _animations) if (ReferenceEquals(a.group, group)) { why = "a drop of it is running"; return false; }
            foreach (HullUpdateWork w in _hullWorks) if (ReferenceEquals(w.group, group)) { why = "a hull update of it is in flight"; return false; }
            if (_waitingRequest.ContainsKey(group)) { why = "a hull request of it waits"; return false; }
            foreach ((HullGroup g, string _) in _takeBack) if (ReferenceEquals(g, group)) { why = "it is being taken back out"; return false; }
            foreach (SiblingPair p in _pairs) if (p.building == building) { why = "a sibling pair of it stands"; return false; }
            if (_limitAwaiting.ContainsKey(building)) { why = "a hit of it awaits the cut limit"; return false; }
            return true;
        }

        /// <summary>
        /// Ends one cut building whose members' fragments and display owners the caller has just retired by the common
        /// retirement, in the same call: the group leaves the trial, its actor -- the body, the hull collider and every
        /// member root under it -- is destroyed, its hull, mesh and shape go back, and what the trial kept of the
        /// building is forgotten. Nothing of another building is touched. The caller asked
        /// <see cref="IsBuildingQuiet"/> right before; a group that is gone already is left as it is.
        /// </summary>
        internal void EndBuilding(HullGroup group, string why)
        {
            if (group == null || group.State == HullGroupState.Gone)
            {
                return;
            }

            int building = group.Building;
            group.State = HullGroupState.Gone;
            _groups.Remove(group);
            _byId.Remove(group.Id);
            if (group.Body != null) _rest.Untrack(group.Body, why);
            group.Members.Clear();
            group.Reserved.Clear();
            PhysicsOwnerBuilder.DestroyObject(group.Root);
            group.Root = null; group.Body = null; group.Collider = null;
            group.Shape?.Dispose();
            group.OwnedBrep?.Dispose();
            if (group.OwnedMesh != null) PhysicsCutCook.DestroyMesh(group.OwnedMesh);
            group.Shape = null; group.OwnedBrep = null; group.OwnedMesh = null;

            // What the trial kept by the building's number.
            _cutStopped.Remove(building);
            _displayOpenOf.Remove(building);
            _limitAwaiting.Remove(building);
            _stopRecords.Remove(building);
            _stopAt.Remove(building);
            _lastStopAt.Remove(building);
            _aggregations.Remove(building);
            _waits.RemoveAll(w => w.building == building);
            List<CutOperationId> operations = null;
            foreach (KeyValuePair<CutOperationId, int> pair in _displayOpBuilding)
            {
                if (pair.Value == building) (operations ?? (operations = new List<CutOperationId>())).Add(pair.Key);
            }

            if (operations != null)
            {
                foreach (CutOperationId operation in operations) _displayOpBuilding.Remove(operation);
            }

            BuildingsEnded++;
            NoteLiveHulls();
            Record("group " + group.Id + " building " + building + " ended whole (" + why + "): its members retired, its actor, hull and tracking gone");
            BuildingEnded?.Invoke(group);
        }
    }
}
