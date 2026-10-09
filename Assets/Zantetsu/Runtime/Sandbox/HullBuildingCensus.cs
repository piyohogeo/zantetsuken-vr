using System;
using System.Collections.Generic;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The city check's count of the hull trial's buildings in the always-kinematic mode, a frame at a time
    /// (2026-10-10; DESIGN 7.10). Read once a frame, before the frame's collections:
    /// <list type="bullet">
    /// <item>**a cut building that stands** has exactly one group, with a body, one enabled collider and one hull --
    /// as it was judged before buildings could end;</item>
    /// <item>**a building that no longer stands** was ended by the group collection, and no other way: the trial
    /// said its end (<see cref="BuildingHullFusion.BuildingEnded"/>) while the collection was retiring that very
    /// building's group (<see cref="CutFragmentGroupCollection.RetiringRoot"/>). One gone with no end said, or ended
    /// while no collection of its own group ran, is **lost**: a fault at that frame and at every frame after, as a
    /// missing building always was. The count is not relaxed: standing and ended by a collection make every group
    /// the trial made;</item>
    /// <item>**of one ended by a collection nothing shared is left** at the first frame it is missing: the actor,
    /// the body and the hull collider the census saw are destroyed, the group holds nothing and is not listed, and
    /// no cut fragment of its family is live;</item>
    /// <item>**its original comes back as it stood**: not while the display still holds anything of the family, and
    /// then with every renderer and collider of the instance on or off as the census read them itself before the
    /// cut took anything away, where it stood then. The census reads the instance's own components; it does not ask
    /// the candidate what it recorded.</item>
    /// </list>
    /// What is wrong at a frame is counted as that frame (<see cref="FramesNotOneEach"/>); what is wrong about an
    /// ended building is listed once (<see cref="Faults"/>).
    /// </summary>
    internal sealed class HullBuildingCensus : IDisposable
    {
        private sealed class AsPlaced
        {
            internal bool[] renderers, colliders;
            internal Vector3 position;
            internal Quaternion rotation;
        }

        private enum State { Standing, Ended, Lost }

        private sealed class Seen
        {
            internal int building, groupId, seenFrame, endedFrame = -1, missingFrame = -1, backFrame = -1;
            internal HullGroup group;
            internal GameObject root;
            internal Rigidbody body;
            internal Collider collider;
            internal LogicalFragmentId family;
            internal PlacedCuttableCandidate candidate;
            internal AsPlaced asPlaced;
            internal State state;
            internal bool formal, sharedJudged, backJudged;
            internal string lostWhy;
        }

        private readonly CutWorldRoot _world;
        private readonly BuildingHullFusion _hulls;
        private readonly List<PlacedCuttableCandidate> _candidates = new List<PlacedCuttableCandidate>();
        private readonly Dictionary<PlacedCuttableCandidate, AsPlaced> _asAdded = new Dictionary<PlacedCuttableCandidate, AsPlaced>();
        private readonly Dictionary<int, Seen> _known = new Dictionary<int, Seen>();
        private readonly List<Seen> _all = new List<Seen>();
        private readonly List<Collider> _colliders = new List<Collider>();
        private readonly List<string> _faults = new List<string>();
        private bool _disposed;
        private int _observedFrame = -1;

        internal HullBuildingCensus(CutWorldRoot world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _hulls = world.Hulls ?? throw new InvalidOperationException("the world runs no hull trial");
            _hulls.BuildingEnded += OnBuildingEnded;
        }

        /// <summary>Frames at which a standing building was not one group, body, enabled collider and hull, or a building was lost.</summary>
        internal int FramesNotOneEach { get; private set; }
        internal int FirstNotOneEachFrame { get; private set; } = -1;
        internal string NotOneEachWhy { get; private set; }

        /// <summary>Buildings seen standing, and of them now: ended by a group collection, lost, and back as originals.</summary>
        internal int BuildingsSeen => _all.Count;
        internal int EndedByCollection { get; private set; }
        internal int Lost { get; private set; }
        internal int OriginalsBack { get; private set; }

        /// <summary>What was wrong about a building ended by a collection: something shared left, or its original's return.</summary>
        internal IReadOnlyList<string> Faults => _faults;

        /// <summary>
        /// A placed building that a hit may register into the trial: its instance's renderers, colliders and placement
        /// are read here, now, when it is still as the scene placed it (not cut, not left).
        /// </summary>
        internal void Add(PlacedCuttableCandidate candidate)
        {
            if (candidate == null || !candidate.IsBuilding || _asAdded.ContainsKey(candidate))
            {
                return;
            }

            _candidates.Add(candidate);
            bool asTheScenePlacedIt = candidate.State == PlacedCuttableCandidate.CandidateState.Candidate && !candidate.IsWithdrawn;
            _asAdded.Add(candidate, asTheScenePlacedIt ? Read(candidate) : null);
        }

        /// <summary>One frame's reading; <paramref name="frame"/> is the frame it is read in.</summary>
        internal void Observe(int frame)
        {
            if (frame == _observedFrame)
            {
                return;   // a frame is read once
            }

            _observedFrame = frame;
            string why = null;
            int standing = 0;
            foreach (HullGroup g in _hulls.Groups)
            {
                if (g.State == HullGroupState.Gone)
                {
                    continue;
                }

                if (!_known.TryGetValue(g.Building, out Seen s))
                {
                    s = FirstSight(g);
                }

                if (s.seenFrame == frame)
                {
                    why = "building " + g.Building + ": more than one group";
                }
                else
                {
                    standing++;
                }

                s.seenFrame = frame;
                if (ReferenceEquals(s.group, g))
                {
                    // What its pieces share, as it is now: read again when it is gone.
                    s.root = g.Root;
                    s.body = g.Body;
                    s.collider = g.Collider;
                    if (!s.family.IsSet)
                    {
                        s.family = FamilyOf(g, s.candidate);
                    }
                }

                int enabled = 0;
                if (g.Root != null)
                {
                    g.Root.GetComponentsInChildren(true, _colliders);
                    foreach (Collider c in _colliders)
                    {
                        if (c.enabled) enabled++;
                    }
                }

                if (g.Body == null || enabled != 1 || g.HullCount != 1 || g.Shape == null)
                {
                    why = "group " + g.Id + ": body " + (g.Body != null) + ", enabled colliders " + enabled + ", hulls " + g.HullCount;
                }
            }

            for (int i = 0; i < _all.Count; i++)
            {
                Seen s = _all[i];
                if (s.seenFrame == frame)
                {
                    continue;
                }

                if (s.state == State.Standing)
                {
                    // The first frame it is not there.
                    s.missingFrame = frame;
                    if (s.endedFrame >= 0 && s.formal)
                    {
                        s.state = State.Ended;
                        EndedByCollection++;
                    }
                    else
                    {
                        s.state = State.Lost;
                        Lost++;
                        s.lostWhy = s.endedFrame >= 0
                            ? "was ended at frame " + s.endedFrame + " while no group collection was retiring its own group"
                            : "is gone and the trial said no end of it";
                    }
                }

                if (s.state == State.Lost)
                {
                    why = "building " + s.building + " (group " + s.groupId + ") " + s.lostWhy;
                    continue;
                }

                // Its end was in an earlier frame's turn after the collections: what was destroyed then is gone by now.
                if (!s.sharedJudged && frame > s.endedFrame)
                {
                    s.sharedJudged = true;
                    JudgeShared(s, frame);
                }

                if (!s.backJudged)
                {
                    JudgeBack(s, frame);
                }
            }

            // Every group the trial made is accounted for: standing, ended by a collection, or lost (a fault above, by
            // its own reason). A difference left is a building made that was never read at all.
            if (standing + EndedByCollection + Lost != _hulls.GroupsMade)
            {
                why = "buildings standing " + standing + (EndedByCollection > 0 ? ", ended by a group collection " + EndedByCollection : "") + (Lost > 0 ? ", lost " + Lost : "") + " of " + _hulls.GroupsMade;
            }

            if (why != null)
            {
                FramesNotOneEach++;
                if (FirstNotOneEachFrame < 0)
                {
                    FirstNotOneEachFrame = frame;
                    NotOneEachWhy = why;
                }
            }
        }

        /// <summary>
        /// At the run's end: every building ended by a collection has been read back as an original, but one ended in
        /// the last frames (its return waits for a collection the display adopts); and the trial's and the collection's
        /// own counts of ended buildings are the census's.
        /// </summary>
        internal void AtEnd(int frame)
        {
            for (int i = 0; i < _all.Count; i++)
            {
                Seen s = _all[i];
                if (s.state == State.Ended && !s.backJudged && frame - s.endedFrame > FramesToComeBackAtTheEnd)
                {
                    s.backJudged = true;
                    _faults.Add("building " + s.building + " (group " + s.groupId + "), ended at frame " + s.endedFrame + ": its original was not back at the end (frame " + frame + ")");
                }
            }

            // (The census stands from before the first cut: the trial's and the collection's counts are of the same span.)
            int collected = _world.Groups != null ? _world.Groups.BuildingsRetired : 0;
            int byCollection = EndedByCollection + EndedNotYetMissing(), outside = EndedOutsideACollection();
            if (_hulls.BuildingsEnded != byCollection + outside || collected != byCollection)
            {
                _faults.Add("the counts of ended buildings differ: the trial ended " + _hulls.BuildingsEnded + ", the group collection retired " + collected
                    + ", the census heard " + byCollection + " ended by a collection (" + EndedByCollection + " of them read as missing since) and " + outside + " ended outside one");
            }
        }

        /// <summary>An original is allowed this many frames at the run's end to come back (the adoption after its pieces' retirement, and the reading).</summary>
        internal const int FramesToComeBackAtTheEnd = 3;

        internal string Describe()
        {
            return "buildings seen standing " + _all.Count + ": ended by a group collection " + EndedByCollection + " (originals read back " + OriginalsBack + "), lost " + Lost
                + "; frames not one each " + FramesNotOneEach + (FirstNotOneEachFrame >= 0 ? " (first at frame " + FirstNotOneEachFrame + ": " + NotOneEachWhy + ")" : "")
                + "; faults of the ended ones " + _faults.Count + (_faults.Count > 0 ? ": " + _faults[0] : "");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hulls.BuildingEnded -= OnBuildingEnded;
        }

        // The trial's own word, inside the call that ended the building: formal only while the group collection is
        // retiring the family this building's pieces are of.
        private void OnBuildingEnded(HullGroup group)
        {
            if (group == null)
            {
                return;
            }

            if (!_known.TryGetValue(group.Building, out Seen s))
            {
                // Never read standing (made and ended between two readings): nothing of it was seen to be judged by.
                s = new Seen { building = group.Building, groupId = group.Id, group = group, seenFrame = -1, state = State.Standing, candidate = CandidateOf(group) };
                _known.Add(s.building, s);
                _all.Add(s);
            }

            LogicalFragmentId retiring = _world.Groups != null ? _world.Groups.RetiringRoot : default;
            s.endedFrame = Time.frameCount;
            s.formal = retiring.IsSet && s.family.IsSet && retiring == s.family && ReferenceEquals(s.group, group);
        }

        private int EndedOutsideACollection()
        {
            int n = 0;
            foreach (Seen s in _all)
            {
                if (s.endedFrame >= 0 && !s.formal) n++;
            }

            return n;
        }

        private int EndedNotYetMissing()
        {
            int n = 0;
            foreach (Seen s in _all)
            {
                if (s.endedFrame >= 0 && s.formal && s.state == State.Standing) n++;
            }

            return n;
        }

        private Seen FirstSight(HullGroup g)
        {
            var s = new Seen { building = g.Building, groupId = g.Id, group = g, seenFrame = -1, state = State.Standing, candidate = CandidateOf(g) };
            if (s.candidate != null)
            {
                // Its first cut not published yet: the instance is still the scene's own, so it is read now, at this
                // cut. Seen only after the hand-over, it is judged by what was read when it was added.
                s.asPlaced = g.AwaitsFirstCut ? Read(s.candidate) : (_asAdded.TryGetValue(s.candidate, out AsPlaced added) ? added : null);
            }

            s.family = FamilyOf(g, s.candidate);
            _known.Add(s.building, s);
            _all.Add(s);
            return s;
        }

        private PlacedCuttableCandidate CandidateOf(HullGroup g)
        {
            foreach (PlacedCuttableCandidate c in _candidates)
            {
                if (c.Registration != null && ReferenceEquals(c.Registration.Group, g))
                {
                    return c;
                }
            }

            return null;
        }

        // The family the building's pieces are of: asked of the ledger for a member, else the fragment its candidate was identified as.
        private LogicalFragmentId FamilyOf(HullGroup g, PlacedCuttableCandidate candidate)
        {
            foreach (LogicalFragmentId member in g.Fragments)
            {
                if (_world.Ledger.TryGetRoot(member, out LogicalFragmentId root))
                {
                    return root;
                }
            }

            return candidate != null ? candidate.Source : default;
        }

        private static AsPlaced Read(PlacedCuttableCandidate candidate)
        {
            IReadOnlyList<Renderer> renderers = candidate.InstanceRenderers;
            IReadOnlyList<Collider> colliders = candidate.InstanceColliders;
            var read = new AsPlaced { renderers = new bool[renderers.Count], colliders = new bool[colliders.Count] };
            for (int i = 0; i < renderers.Count; i++) read.renderers[i] = renderers[i] != null && renderers[i].enabled;
            for (int i = 0; i < colliders.Count; i++) read.colliders[i] = colliders[i] != null && colliders[i].enabled;
            if (candidate.InstanceTransform != null)
            {
                candidate.InstanceTransform.GetPositionAndRotation(out read.position, out read.rotation);
            }

            return read;
        }

        private void JudgeShared(Seen s, int frame)
        {
            var left = new List<string>();
            if (s.root != null) left.Add("its actor");
            if (s.body != null) left.Add("its body");
            if (s.collider != null) left.Add("its hull collider");
            HullGroup g = s.group;
            if (g.State != HullGroupState.Gone || g.Root != null || g.Body != null || g.Collider != null || g.Shape != null || g.MemberCount != 0)
            {
                left.Add("the group's own (state " + g.State + ", members " + g.MemberCount + ")");
            }

            if (_hulls.TryGetGroup(s.groupId, out _)) left.Add("its listing in the trial");
            int live = s.family.IsSet ? _world.Ledger.LiveCutCountOf(s.family) : 0;
            if (live != 0) left.Add(live + " live cut fragments of its family");
            if (left.Count > 0)
            {
                _faults.Add("building " + s.building + " (group " + s.groupId + "), ended at frame " + s.endedFrame + ": at frame " + frame + " there is still " + string.Join(", ", left));
            }
        }

        private void JudgeBack(Seen s, int frame)
        {
            string who = "building " + s.building + " (group " + s.groupId + "), ended at frame " + s.endedFrame;
            if (s.candidate == null || s.asPlaced == null)
            {
                s.backJudged = true;
                _faults.Add(who + ": " + (s.candidate == null ? "no placed instance is known for it" : "its instance was not read before its cut took anything away") + ", so its return cannot be judged");
                return;
            }

            IReadOnlyList<Renderer> renderers = s.candidate.InstanceRenderers;
            IReadOnlyList<Collider> colliders = s.candidate.InstanceColliders;
            bool anyWasOn = false, anyOn = false;
            for (int i = 0; i < renderers.Count && i < s.asPlaced.renderers.Length; i++)
            {
                if (!s.asPlaced.renderers[i]) continue;
                anyWasOn = true;
                anyOn |= renderers[i] != null && renderers[i].enabled;
            }

            for (int i = 0; i < colliders.Count && i < s.asPlaced.colliders.Length; i++)
            {
                if (!s.asPlaced.colliders[i]) continue;
                anyWasOn = true;
                anyOn |= colliders[i] != null && colliders[i].enabled;
            }

            // Back: something of it that was on is on again (an instance with nothing on is back when it is no longer withdrawn).
            if (!(anyWasOn ? anyOn : !s.candidate.IsWithdrawn))
            {
                return;
            }

            s.backJudged = true;
            s.backFrame = frame;
            OriginalsBack++;
            if (_world.Display != null && s.family.IsSet && _world.Display.HoldsAnythingOf(s.family))
            {
                _faults.Add(who + ": its original is back at frame " + frame + " while the display still holds something of its pieces");
            }

            string differs = Differs(s.candidate, s.asPlaced);
            if (differs != null)
            {
                _faults.Add(who + ": its original is back at frame " + frame + " otherwise than it stood: " + differs);
            }
        }

        private static string Differs(PlacedCuttableCandidate candidate, AsPlaced was)
        {
            AsPlaced now = Read(candidate);
            if (now.renderers.Length != was.renderers.Length || now.colliders.Length != was.colliders.Length)
            {
                return "its renderers or colliders are not the ones read";
            }

            for (int i = 0; i < now.renderers.Length; i++)
            {
                if (now.renderers[i] != was.renderers[i]) return "renderer " + i + " is " + (now.renderers[i] ? "on" : "off") + ", was " + (was.renderers[i] ? "on" : "off");
            }

            for (int i = 0; i < now.colliders.Length; i++)
            {
                if (now.colliders[i] != was.colliders[i]) return "collider " + i + " is " + (now.colliders[i] ? "on" : "off") + ", was " + (was.colliders[i] ? "on" : "off");
            }

            if ((now.position - was.position).sqrMagnitude > 1e-8f || Quaternion.Angle(now.rotation, was.rotation) > 0.01f)
            {
                return "it stands at " + now.position.ToString("F4") + ", stood at " + was.position.ToString("F4");
            }

            return null;
        }
    }
}
