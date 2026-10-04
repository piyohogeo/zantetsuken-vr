using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // Measurement (the playable city unit, Assets/Licensed/PlayableCity/PlayableCity.unity): in the MobPlan mode, when the
    // city also holds placed cuttables (PlayableCityCuttable: a building and an anchored prop, in the same cut world as the
    // crowd), what the Slashes of the saved input did to each kind -- NPC, building, prop -- and what became of the pieces.
    // Only reads; nothing is cut, moved or retired from here.
    //   [scenario] each kind cut, published and committed from a Slash; one Slash accepted on two kinds or more; a piece cut
    //              again; the anchored pieces fixed where they were cut, the free ones dynamic; no building or prop piece gone
    //              other than by its own cut; the crowd's plan cycles going on across a building or prop cut (the plan
    //              cycles either side of each cut window no further apart than 3 s: a cycle is published about once a second,
    //              and a cut window is often shorter than that).
    //   The pieces' motion (how far and how fast the free ones went) is written down, not judged: that the cuts were all
    //   committed is not that they look or move right.
    public static partial class SandboxPropSlashPlayerCheck
    {
        /// <summary>
        /// One CSV field (RFC 4180): as it is, unless it holds a comma, a double quote, a carriage return or a line feed,
        /// when it is enclosed in double quotes with each double quote doubled (2026-10-01: an outcome with a comma moved a
        /// row's columns).
        /// </summary>
        public static string CsvField(string value)
        {
            if (value == null) return "";
            if (value.IndexOfAny(s_csvSpecial) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static readonly char[] s_csvSpecial = { ',', '"', '\r', '\n' };

        /// <summary>The display's GPU copy in words: capacities, strides, bytes, the first buffers' time, growth, the release queue, the range used.</summary>
        internal static string DescribeGpuCopy(VpLogicalCutDisplay d) =>
            "vertex buffer " + d.GpuVertexCapacity + " x " + d.GpuVertexStride + " B = " + d.GpuVertexBytes + " B, index buffer " + d.GpuIndexCapacity + " x " + d.GpuIndexStride + " B = " + d.GpuIndexBytes
            + " B, in all " + (d.GpuVertexBytes + d.GpuIndexBytes) + " B (" + ((d.GpuVertexBytes + d.GpuIndexBytes) / (1024.0 * 1024.0)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MiB); first buffers made in "
            + (d.GpuCreationSeconds * 1000).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " ms; grown: vertex " + d.GpuVertexGrowthCount + ", index " + d.GpuIndexGrowthCount + " (" + d.GpuGrowthCount + " growths); replaced buffers awaiting release now "
            + d.GpuRetiredNow + " (most at once " + d.GpuMaxRetired + "); the range used at most: vertices " + d.GpuVertexHighWater + ", indices " + d.GpuIndexHighWater;
        /// <summary>
        /// Whether an owner stands fixed by its anchors, on the body that carries it (TL, 2026-09-30): its own body when
        /// it has one; the group's when it is fused (a fused member has no body of its own, by design) -- the owner
        /// fixed by anchors, the body kinematic (and a fused group resting and anchored), and every enabled collider
        /// under the owner's Root attached to that body. Not a looser judgement: the same three facts, read where
        /// they are.
        /// </summary>
        /// <summary>What <see cref="AnalyseCycleWindows"/> found: the widest measured gap, the longest end without a cycle, the ends missing a cycle, and a line a window (the first twelve and the last).</summary>
        public struct CycleWindows
        {
            public double widest, widestOpenEnd;
            public int missingStarts, missingEnds;
            public List<string> lines;
        }

        /// <summary>
        /// The crowd's plan cycles over windows of time (TL, 2026-09-30): for each window, the widest gap between consecutive
        /// cycles from the last before it to the first after it -- measured where both ends are cycles -- and, where an end has
        /// no cycle (the run began or ended inside the window: a missing measurement), the time from that end of the window to
        /// the nearest cycle, the least a stop there could have been. The last window may be open at the run's end.
        /// </summary>
        public static CycleWindows AnalyseCycleWindows(IReadOnlyList<(double from, double to)> windows, IReadOnlyList<double> cycleTimes, bool openAtEnd)
        {
            var result = new CycleWindows { lines = new List<string>() };
            for (int wi = 0; wi < windows.Count; wi++)
            {
                (double from, double to) w = windows[wi];
                double before = cycleTimes.Where(c => c <= w.from).DefaultIfEmpty(double.NaN).Max();
                double after = cycleTimes.Where(c => c >= w.to).DefaultIfEmpty(double.NaN).Min();
                List<double> inside = cycleTimes.Where(c => c > w.from && c < w.to).OrderBy(c => c).ToList();
                var points = new List<double>();
                if (!double.IsNaN(before)) points.Add(before); else result.missingStarts++;
                points.AddRange(inside);
                if (!double.IsNaN(after)) points.Add(after); else result.missingEnds++;
                double measured = 0;
                for (int i = 1; i < points.Count; i++) measured = System.Math.Max(measured, points[i] - points[i - 1]);
                double headOpen = double.IsNaN(before) ? (points.Count > 0 ? points[0] - w.from : w.to - w.from) : 0;
                double tailOpen = double.IsNaN(after) ? (points.Count > 0 ? w.to - points[points.Count - 1] : w.to - w.from) : 0;
                result.widest = System.Math.Max(result.widest, measured);
                result.widestOpenEnd = System.Math.Max(result.widestOpenEnd, System.Math.Max(headOpen, tailOpen));
                if (result.lines.Count < 12 || wi == windows.Count - 1)
                    result.lines.Add("window " + wi + " " + (w.to - w.from).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " s: cycles inside " + inside.Count + ", before " + (double.IsNaN(before) ? "none (missing)" : (w.from - before).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " s earlier")
                        + ", after " + (double.IsNaN(after) ? "none (missing" + (openAtEnd && wi == windows.Count - 1 ? ": open at the run's end" : "") + ")" : (after - w.to).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " s later")
                        + ", widest measured gap " + measured.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " s, open end at least " + System.Math.Max(headOpen, tailOpen).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " s");
            }

            return result;
        }

        public static bool FixedByItsAnchors(PhysicsFragmentOwner owner, out string how)
        {
            Rigidbody body = owner.IsFused ? owner.Group.Body : owner.Body;
            bool groupHeld = !owner.IsFused || (owner.Group.Kinematic && owner.Group.Anchored);
            int colliders = 0, onBody = 0;
            if (owner.Root != null)
            {
                foreach (Collider collider in owner.Root.GetComponentsInChildren<Collider>(true))
                {
                    if (!collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                    colliders++;
                    if (body != null && collider.attachedRigidbody == body) onBody++;
                }
            }

            how = (owner.IsFused ? "fused, group body " : "own body ") + (body != null ? (body.isKinematic ? "kinematic" : "dynamic") : "none")
                + (owner.IsFused ? ", group resting " + owner.Group.Kinematic + " anchored " + owner.Group.Anchored : "")
                + ", fixedByAnchors " + owner.FixedByAnchors + ", colliders on that body " + onBody + " of " + colliders;
            return owner.FixedByAnchors && body != null && body.isKinematic && groupHeld && colliders > 0 && onBody == colliders;
        }

        private sealed partial class Walk
        {
            private sealed class PlayablePiece
            {
                public string kind, birthOverlap, fastContacts;
                public int fastFrame = -1;
                public bool anchored, kinematicAlways = true;
                public Vector3 firstCentre;
                public float maxDisplacement, maxSpeed;
                public int firstFrame, lastFrame;
            }

            private PlayableCityCuttable[] _pcCuttables = new PlayableCityCuttable[0];
            private readonly Dictionary<LogicalFragmentId, PlayablePiece> _pcPieces = new Dictionary<LogicalFragmentId, PlayablePiece>();
            private readonly List<LogicalFragmentId> _pcFragments = new List<LogicalFragmentId>();
            private int _pcWindowFrames, _pcWindowCycles, _pcWindowRetired, _pcWindowReplacements, _pcRetired;
            private int _pcLastCycles, _pcLastReplacements, _pcLastRetired;
            private readonly List<double> _pcCycleTimes = new List<double>();
            private readonly List<(double from, double to)> _pcWindows = new List<(double, double)>();
            private double _pcWindowFrom = -1;
            private const double PlayableCycleGapSeconds = 3.0;
            // The storage's fixed tables and the system constraints: the most used at once (read every 30 frames from the
            // storage's own room description), against their capacity -- written down, not judged.
            private int _pcMaxBlocks, _pcMaxSubmeshes, _pcMaxDescriptors, _pcMaxConstraints, _pcBlockCapacity, _pcSubmeshCapacity, _pcDescriptorCapacity;
            private static readonly System.Text.RegularExpressions.Regex s_pcRoom = new System.Text.RegularExpressions.Regex(
                @"descriptors reserved (\d+) published (\d+) retiring (\d+) free \d+ of (\d+); submeshes used (\d+) of (\d+); vertex blocks used (\d+) of (\d+)");
            private bool PlayableCity => _pcCuttables.Length > 0;
            private int _pcDeferredAtStart, _pcDeferredLogged;
            private readonly List<string> _pcDeferredNotTarget = new List<string>();

            private void PlayableCityBegin()
            {
                _pcCuttables = Object.FindObjectsByType<PlayableCityCuttable>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (!PlayableCity) return;
                if (_world.Display != null) Log("display GPU copy at the start: " + DescribeGpuCopy(_world.Display) + "; the world's profile asked for " + _world.Profile.GpuVertexInitialCapacity + " vertices and " + _world.Profile.GpuIndexInitialCapacity + " indices");
                // DIAGNOSIS ONLY (2026-10-01): "-zantetsuPlacePhased" makes each Place pass time its queries, checks and the rest as three blocks.
                VpMultiCutSnapshot.PlacePhasedDiagnosis = System.Environment.GetCommandLineArgs().Contains("-zantetsuPlacePhased");
                Log("place diagnosis (queries, checks and the rest as three timed blocks): " + (VpMultiCutSnapshot.PlacePhasedDiagnosis ? "ON (-zantetsuPlacePhased)" : "off"));
                foreach (PlayableCityCuttable c in _pcCuttables)
                {
                    if (c != null && c.IsRegistered) _pcNames[c.Registration.Fragment] = PlayableCityName(c);
                }

                foreach (PlayableCityCuttable c in _pcCuttables)
                {
                    // Deferred (TL, 2026-10-03): a cut target only, the instance the scene's own until its first cut.
                    if (c.Candidate != null && !c.IsRegistered)
                    {
                        int renderersOn = c.instanceRenderers.Count(r => r != null && r.enabled), collidersOn = c.instanceColliders.Count(k => k != null && k.enabled);
                        if (_pcDeferredLogged < 8)
                        {
                            _pcDeferredLogged++;
                            Log("playable city: " + (c.building ? "building " : "prop ") + c.gameObject.name + " a cut target, drawn and colliding as the scene placed it until its first cut (renderers on "
                                + renderersOn + " of " + c.instanceRenderers.Length + ", colliders on " + collidersOn + " of " + c.instanceColliders.Length + ")");
                        }

                        _pcDeferredAtStart++;
                        if (!c.Candidate.IsHitTarget || c.Candidate.IsWithdrawn) _pcDeferredNotTarget.Add(c.gameObject.name);
                        continue;
                    }

                    if (c.building && c.IsRegistered && c.Registration.Group != null && _world.Hulls != null)
                    {
                        // The building in the hull trial (2026-09-30): one kinematic group, one body, one collider, one hull, its
                        // root fragment display-only; its anchors are the group's, so the owner has no body to judge.
                        Log("playable city: building (hull trial) " + c.Registration.Description);
                        // Checked as registered before the section's cuts (TL, 2026-10-01): here its current state; otherwise as registered, as before.
                        if (_bhRegistrationChecked.ContainsKey(c.Registration.Group.Building)) BuildingHullCurrent(c.Registration.Group);
                        else BuildingHullBegin(c.Registration.Group, c.Registration.Fragment);
                        continue;
                    }

                    PhysicsFragmentOwner owner = null;
                    bool fixedAtStart = c.IsRegistered && _world.Owners.TryGet(c.Registration.Fragment, out owner) && FixedByItsAnchors(owner, out string how);
                    Log("playable city: " + (c.building ? "building " : "prop ") + (c.IsRegistered ? c.Registration.Description : "NOT registered")
                        + (owner != null ? "; anchors judged " + (FixedByItsAnchors(owner, out string judged) ? "fixed" : "NOT fixed") + " (" + judged + ")" : ""));
                    Expect(fixedAtStart, "[scenario] the " + (c.building ? "building" : "prop") + " is registered in the crowd's cut world, fixed by its anchors");
                }

                if (_pcDeferredAtStart > 0)
                {
                    // Before any cut (TL, 2026-10-03): every placed cuttable a candidate only -- no fragment, no registration
                    // (no VP geometry, no display, no owner, no hull group), no body of the cut world; the instances as placed.
                    int candidates = 0, targets = 0, withFragment = 0, registered = 0, instanceBodies = 0;
                    foreach (PlayableCityCuttable c in _pcCuttables)
                    {
                        if (c == null || c.Candidate == null) continue;
                        candidates++;
                        if (c.Candidate.IsHitTarget) targets++;
                        if (c.Candidate.Source.IsSet) withFragment++;
                        if (c.Registration != null) registered++;
                        if (c.target != null) instanceBodies += c.target.GetComponentsInChildren<Rigidbody>(true).Length;
                    }

                    int groups = _world.Hulls != null ? _world.Hulls.GroupsMade : 0;
                    Log("playable city before any cut: " + candidates + " candidates of " + _pcCuttables.Length + " placed cuttables, hit targets " + targets + ", with a fragment " + withFragment
                        + ", registered " + registered + "; the cut world: owners " + _world.Owners.Count + ", hull groups made " + groups + ", storage vertices " + _world.Storage.VertexCount
                        + " (the crowd's prepared characters included); Rigidbodies on the placed instances " + instanceBodies);
                    Expect(candidates == _pcCuttables.Length && targets == candidates, "[scenario] every placed cuttable is prepared as a candidate and a hit target (" + targets + " of " + _pcCuttables.Length + ")");
                    Expect(withFragment == 0 && registered == 0 && groups == 0, "[scenario] before any cut no placed cuttable has VP geometry or a cut-world body (fragments " + withFragment
                        + ", registrations " + registered + ", hull groups " + groups + ")");
                    var scenario = Object.FindFirstObjectByType<ScenarioRenderPipeline>();
                    string inEffect = ScenarioRenderPipeline.Describe(UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline);
                    Log("rendering in effect at the walk's start: " + inEffect + "; the scene's scenario pipeline " + (scenario == null ? "none" : (scenario.pipeline != null ? scenario.pipeline.name : "unset") + (scenario.Applied ? " applied" : " NOT applied")));
                    if (scenario != null)
                    {
                        Expect(scenario.Applied && UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == scenario.pipeline && inEffect.Contains("ScreenSpaceAmbientOcclusion=inactive")
                            && !inEffect.Contains("ScreenSpaceAmbientOcclusion=active"), "[scenario] the scenario's render pipeline is in effect with its SSAO inactive");
                    }

                    Expect(_pcDeferredNotTarget.Count == 0, "[scenario] every deferred placed cuttable stands as the scene placed it, a cut target until its first cut (" + _pcDeferredAtStart
                        + " deferred; not a target " + _pcDeferredNotTarget.Count + (_pcDeferredNotTarget.Count > 0 ? ": " + string.Join(" ", _pcDeferredNotTarget.Take(8)) : "") + ")");
                }

                _crowd.ActorRetired += PlayableCityRetired;
                _pcLastCycles = _crowd.PublishedCycles;
                _pcLastReplacements = _crowd.ReplacementsAdded;
            }

            private void PlayableCityRetired(int id, SandboxNpcCharacter c) => _pcRetired++;

            private const float PlayableFastSpeed = 8f;
            private readonly Collider[] _pcNear = new Collider[32];

            // What this owner's colliders overlap now, with the depth (Physics.ComputePenetration): a static collider of the
            // city (no Rigidbody), another piece (a Rigidbody), or the floor. Only statics when asked. Empty when nothing.
            private string PlayableContacts(PhysicsFragmentOwner owner, bool staticOnly)
            {
                if (owner.Root == null) return "";
                var found = new List<string>();
                foreach (Collider own in owner.Root.GetComponentsInChildren<Collider>())
                {
                    if (!own.enabled) continue;
                    Bounds b = own.bounds;
                    int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * 0.01f, _pcNear, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n; i++)
                    {
                        Collider other = _pcNear[i];
                        if (other == null || other.transform.IsChildOf(owner.Root.transform)) continue;
                        bool isStatic = other.attachedRigidbody == null;
                        if (staticOnly && !isStatic) continue;
                        if (!Physics.ComputePenetration(own, own.transform.position, own.transform.rotation, other, other.transform.position, other.transform.rotation, out _, out float depth)) continue;
                        string what = isStatic ? (other.name.Contains("Floor") ? "floor " : "static ") : "piece ";
                        found.Add(what + other.name + " " + depth.ToString("F3", Inv) + " m");
                    }
                }

                return string.Join("; ", found.Distinct());
            }

            // The kind of a lineage root that is one of the placed cuttables, or null. The names are made once a cuttable is
            // seen registered (the city walk, 2026-10-03: hundreds of them, many of one asset, so the name of each is its
            // placement's own -- the scene builder's "building-<asset>#<k>@<x>,<z>" -- where it has one).
            private readonly Dictionary<LogicalFragmentId, string> _pcNames = new Dictionary<LogicalFragmentId, string>();

            private static string PlayableCityName(PlayableCityCuttable c)
            {
                string kind = c.building ? "building-" : "prop-";
                return c.gameObject.name.StartsWith(kind, System.StringComparison.Ordinal) ? c.gameObject.name : kind + (c.Registration != null ? c.Registration.Name : c.Candidate.Name);
            }

            private string PlayableCityRootName(LogicalFragmentId root)
            {
                if (_pcNames.TryGetValue(root, out string named)) return named;
                foreach (PlayableCityCuttable c in _pcCuttables)
                {
                    if (c != null && c.IsRegistered && c.Registration.Fragment == root)
                    {
                        return _pcNames[root] = PlayableCityName(c);
                    }

                    // A deferred one: the fragment a hit identified it as.
                    if (c != null && c.Candidate != null && c.Candidate.Source.IsSet && c.Candidate.Source == root)
                    {
                        return _pcNames[root] = PlayableCityName(c);
                    }
                }

                return null;
            }

            private static string KindOf(string lineage) =>
                lineage.StartsWith("npc-") ? "npc" : lineage.StartsWith("building-") ? "building" : lineage.StartsWith("prop-") ? "prop" : "other";

            private void PlayableCityFrame(int frame)
            {
                if (!PlayableCity) return;
                BuildingFusionFrame(frame);   // the building's fusion, frame by frame, as the building mode records it (nothing without a fusion)
                if (_world.Hulls != null && !_world.IsEnding) BuildingHullFrame(frame);   // the hull trial's building, frame by frame (building-hull.csv): one body, collider and hull each

                // The building and prop pieces live now: where they are, whether they are fixed.
                _pcFragments.Clear();
                _world.Owners.CopyFragmentsTo(_pcFragments);
                foreach (LogicalFragmentId fragment in _pcFragments)
                {
                    if (!_world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.Body == null || owner.IsWithdrawn) continue;
                    string kind = KindOf(LineageOf(fragment));
                    if (kind != "building" && kind != "prop") continue;
                    Vector3 centre = owner.Body.worldCenterOfMass;
                    if (!_pcPieces.TryGetValue(fragment, out PlayablePiece piece))
                    {
                        _pcPieces[fragment] = piece = new PlayablePiece { kind = kind, anchored = owner.FixedByAnchors, firstCentre = centre, firstFrame = frame };
                        piece.birthOverlap = PlayableContacts(owner, staticOnly: true);
                    }

                    // The first time a free piece goes fast: what it touches then (city statics, other pieces, the floor).
                    if (piece.fastFrame < 0 && !owner.Body.isKinematic && owner.Body.linearVelocity.magnitude > PlayableFastSpeed)
                    {
                        piece.fastFrame = frame;
                        piece.fastContacts = "speed " + owner.Body.linearVelocity.magnitude.ToString("F1", Inv) + " m/s at " + centre.ToString("F2") + ": " + PlayableContacts(owner, staticOnly: false);
                    }

                    piece.lastFrame = frame;
                    piece.kinematicAlways &= owner.Body.isKinematic;
                    piece.maxDisplacement = Mathf.Max(piece.maxDisplacement, (centre - piece.firstCentre).magnitude);
                    if (!owner.Body.isKinematic) piece.maxSpeed = Mathf.Max(piece.maxSpeed, owner.Body.linearVelocity.magnitude);
                }

                PlayableDiagFrame(frame, 0.1f);
                _pcMaxConstraints = Mathf.Max(_pcMaxConstraints, _world.Owners.SystemConstraintCount);
                if (frame % 30 == 0) PlayableCityRoom();

                // While a building or prop cut is accepted and not yet committed -- or, for the fused building, while its
                // fusion has anything waiting -- what the crowd did meanwhile.
                bool window = _accepted.Any(a => !a.fusion && a.committedFrame < 0 && KindOf(LineageOf(a.fragment)) is string k && (k == "building" || k == "prop"))
                    || (_world.Fusion != null && !_world.Fusion.IsSettled)
                    || (_world.Hulls != null && !_world.Hulls.IsSettled);
                double now = Time.realtimeSinceStartupAsDouble;
                if (_crowd.PublishedCycles != _pcLastCycles) _pcCycleTimes.Add(now);
                if (window && _pcWindowFrom < 0) _pcWindowFrom = now;
                if (!window && _pcWindowFrom >= 0) { _pcWindows.Add((_pcWindowFrom, now)); _pcWindowFrom = -1; }
                if (window)
                {
                    _pcWindowFrames++;
                    _pcWindowCycles += _crowd.PublishedCycles - _pcLastCycles;
                    _pcWindowReplacements += _crowd.ReplacementsAdded - _pcLastReplacements;
                    _pcWindowRetired += _pcRetired - _pcLastRetired;
                }

                _pcLastCycles = _crowd.PublishedCycles;
                _pcLastReplacements = _crowd.ReplacementsAdded;
                _pcLastRetired = _pcRetired;
            }

            private void PlayableCityRoom()
            {
                System.Text.RegularExpressions.Match m = s_pcRoom.Match(_world.Storage.DescribeRoom());
                if (!m.Success) return;
                int Group(int i) => int.Parse(m.Groups[i].Value, Inv);
                _pcMaxDescriptors = Mathf.Max(_pcMaxDescriptors, Group(1) + Group(2) + Group(3));
                _pcDescriptorCapacity = Group(4);
                _pcMaxSubmeshes = Mathf.Max(_pcMaxSubmeshes, Group(5));
                _pcSubmeshCapacity = Group(6);
                _pcMaxBlocks = Mathf.Max(_pcMaxBlocks, Group(7));
                _pcBlockCapacity = Group(8);
            }

            private void PlayableCitySummary()
            {
                if (!PlayableCity) return;
                PlayableCityRoom();
                Log("playable city storage tables at most: descriptors " + _pcMaxDescriptors + " of " + _pcDescriptorCapacity + ", submeshes " + _pcMaxSubmeshes
                    + " of " + _pcSubmeshCapacity + ", vertex blocks " + _pcMaxBlocks + " of " + _pcBlockCapacity + " (every 30 frames and at the end); system constraints at most "
                    + _pcMaxConstraints + " of " + _world.Profile.SystemConstraintCapacity + "; storage now: " + _world.Storage.DescribeRoom());
                if (_crowd != null) _crowd.ActorRetired -= PlayableCityRetired;
                Guarded("playable diag summary", PlayableDiagSummary);

                // Each kind's cuts: roots and children, accepted, published, committed; Pending ones and how they ended.
                var kinds = new[] { "npc", "building", "prop" };
                foreach (string kind in kinds)
                {
                    List<Accepted> of = _accepted.Where(a => KindOf(LineageOf(a.fragment)) == kind).ToList();
                    int roots = of.Count(a => !a.child), children = of.Count(a => a.child);
                    Log("playable city " + kind + ": root cuts accepted " + roots + " published " + of.Count(a => !a.child && a.publishedFrame >= 0)
                        + " committed " + of.Count(a => !a.child && a.committedFrame >= 0) + "; child cuts accepted " + children
                        + " published " + of.Count(a => a.child && a.publishedFrame >= 0) + " committed " + of.Count(a => a.child && a.committedFrame >= 0)
                        + "; accepted as Pending " + of.Count(a => a.pending) + " (committed " + of.Count(a => a.pending && a.committedFrame >= 0) + ")");
                    if (kind == "building" && _world.Hulls != null)
                    {
                        // The hull trial's building: its hits are the trial's (they issue no operation to this check), judged
                        // in the hull summary from its own record to the display operations at the ledger's end.
                        Log("playable city building (hull trial): hits " + _world.Hulls.Hits.Count + ", published " + _world.Hulls.HitsPublished + ", display cuts " + _world.Hulls.DisplayCuts);
                        continue;
                    }

                    if (kind == "building" && _world.Fusion != null && of.Any(a => a.fusion))
                    {
                        // The fused building: its hits are the fusion's, and a success is the whole way from a hit to its
                        // members' operations at the ledger's end with their geometry committed (TL, 2026-09-30).
                        Guarded("building fusion chains", () => BuildingFusionChains(of.Count(a => a.fusion)));
                        continue;
                    }

                    Expect(of.Any(a => !a.child && a.committedFrame >= 0), "[scenario] a " + kind + " was cut from a Slash, published and committed");
                }

                // One Slash on more than one kind.
                var slashKinds = _accepted.Select(a => (slash: a.slash, kind: KindOf(LineageOf(a.fragment)))).ToList();
                if (_world.Hulls != null)
                {
                    // A hit the hull trial published is the building's acceptance of that Slash.
                    foreach (BuildingHullFusion.HullHit hit in _world.Hulls.Hits) if (hit.outcome == "Published") slashKinds.Add((hit.slashId, "building"));
                }

                var bySlash = slashKinds.GroupBy(a => a.slash).Select(g => (slash: g.Key, kinds: g.Select(a => a.kind).Distinct().OrderBy(k => k).ToList())).ToList();
                var mixed = bySlash.Where(s => s.kinds.Count > 1).ToList();
                foreach ((long slash, List<string> k) in mixed) Log("playable city slash " + slash + " accepted on " + string.Join("+", k));
                // The city walk aims no Slash at two kinds at once (its route stands before one target at a time): written down, not judged.
                if (cityWalk) Log("playable city: one Slash accepted on two kinds of target or more: " + mixed.Count + " such Slashes (not judged in the city walk)");
                else Expect(mixed.Count > 0, "[scenario] one Slash was accepted on two kinds of target or more (" + mixed.Count + " such Slashes)");
                Expect(_accepted.Any(a => a.child && a.committedFrame >= 0), "[scenario] a piece made by a cut was cut again and committed");

                // The pieces: anchored ones stayed fixed where they were cut; free ones were dynamic. Gone ones: only by a cut.
                var cutSources = new HashSet<LogicalFragmentId>(_accepted.Select(a => a.fragment));
                var live = new HashSet<LogicalFragmentId>(_pcFragments);
                foreach (string kind in new[] { "building", "prop" })
                {
                    if (kind == "building" && _world.Hulls != null)
                    {
                        // Its pieces are display members with no body of their own: these judgements do not apply. The hull
                        // trial's own ([hull kinematic]: one body, one collider and one hull a building at every frame, kinematic
                        // throughout, the display's moves in their cut planes) take their place.
                        Log("playable city building pieces: NOT APPLICABLE to the hull trial's building (display members without bodies of their own); replaced by the [hull kinematic] judgements");
                        continue;
                    }

                    List<KeyValuePair<LogicalFragmentId, PlayablePiece>> pieces = _pcPieces.Where(p => p.Value.kind == kind).ToList();
                    List<PlayablePiece> anchored = pieces.Where(p => p.Value.anchored).Select(p => p.Value).ToList();
                    List<PlayablePiece> free = pieces.Where(p => !p.Value.anchored).Select(p => p.Value).ToList();
                    int gone = pieces.Count(p => !live.Contains(p.Key) && !cutSources.Contains(p.Key));
                    Log("playable city " + kind + " pieces: seen " + pieces.Count + ", anchored " + anchored.Count + " (all kinematic "
                        + anchored.All(p => p.kinematicAlways) + ", max displacement " + (anchored.Count > 0 ? anchored.Max(p => p.maxDisplacement) : 0f).ToString("F4", Inv)
                        + " m), free " + free.Count + " (dynamic " + free.Count(p => !p.kinematicAlways) + ", max displacement "
                        + (free.Count > 0 ? free.Max(p => p.maxDisplacement) : 0f).ToString("F2", Inv) + " m, max speed "
                        + (free.Count > 0 ? free.Max(p => p.maxSpeed) : 0f).ToString("F2", Inv) + " m/s), live at the end " + pieces.Count(p => live.Contains(p.Key))
                        + ", gone other than by a cut " + gone);
                    Expect(anchored.All(p => p.kinematicAlways && p.maxDisplacement < 0.001f), "[scenario] every anchored " + kind + " piece stayed kinematic where it was made");
                    Expect(free.Count == 0 || free.All(p => !p.kinematicAlways), "[scenario] every free " + kind + " piece is dynamic");
                    Expect(gone == 0, "[scenario] no " + kind + " piece went other than by its own cut (" + gone + ")");
                }

                // Each window's plan cycles (TL, 2026-09-30: a window open to the run's end had no cycle after it, and its gap was
                // Infinity): the widest gap between consecutive cycles from the last before the window to the first after it,
                // measured where both ends are cycles; where an end has no cycle (the run began or ended inside the window), the
                // time from that end of the window to the nearest cycle is the least the stop could have been -- a stop when it
                // passes the limit, and otherwise a missing end, recorded as such.
                bool openAtEnd = _pcWindowFrom >= 0;
                if (openAtEnd) _pcWindows.Add((_pcWindowFrom, Time.realtimeSinceStartupAsDouble));
                CycleWindows cw = AnalyseCycleWindows(_pcWindows, _pcCycleTimes, openAtEnd);
                double widest = cw.widest, widestOpenEnd = cw.widestOpenEnd;
                int missingStarts = cw.missingStarts, missingEnds = cw.missingEnds;
                List<string> windowLines = cw.lines;
                foreach (string l in windowLines) Log("playable city crowd " + l);

                // The pieces born overlapping a city static (the floor apart), and the fast ones with what they touched then.
                int born = 0, fast = 0, fastWithStatic = 0, fastWithPiece = 0, fastWithFloorOnly = 0, logged = 0;
                foreach (KeyValuePair<LogicalFragmentId, PlayablePiece> p in _pcPieces)
                {
                    PlayablePiece piece = p.Value;
                    bool cityStatic = piece.birthOverlap != null && piece.birthOverlap.Split(';').Any(s => s.Trim().StartsWith("static"));
                    if (cityStatic) born++;
                    if (piece.fastFrame < 0) continue;
                    fast++;
                    bool withStatic = piece.fastContacts.Contains(": static") || piece.fastContacts.Contains("; static");
                    bool withPiece = piece.fastContacts.Contains("piece ");
                    if (withStatic) fastWithStatic++;
                    if (withPiece) fastWithPiece++;
                    if (!withStatic && !withPiece && piece.fastContacts.Contains("floor")) fastWithFloorOnly++;
                    if (logged++ < 40)
                        Log("playable city fast " + piece.kind + " piece " + p.Key.value + " (anchored " + piece.anchored + ") frame " + piece.fastFrame + " " + piece.fastContacts
                            + (string.IsNullOrEmpty(piece.birthOverlap) ? "" : " | at birth: " + piece.birthOverlap));
                }

                Log("playable city piece contacts: born overlapping a city static " + born + "; fast (over " + PlayableFastSpeed + " m/s) " + fast
                    + ", touching a city static then " + fastWithStatic + ", another piece " + fastWithPiece + ", the floor only " + fastWithFloorOnly
                    + ", nothing " + (fast - fastWithStatic - fastWithPiece - fastWithFloorOnly));
                Log("playable city crowd across the building and prop cuts: windows " + _pcWindows.Count + " (" + _pcWindowFrames + " frames), plan cycles published inside "
                    + _pcWindowCycles + ", widest measured gap between consecutive plan cycles over a window " + widest.ToString("F2", Inv) + " s; ends without a cycle (missing) "
                    + missingStarts + " before and " + missingEnds + " after, the longest such open end at least " + widestOpenEnd.ToString("F2", Inv) + " s; individuals retired inside "
                    + _pcWindowRetired + ", replacements inside " + _pcWindowReplacements + "; over the run cycles " + _pcCycleTimes.Count);
                Expect(_pcWindows.Count == 0 || widest <= PlayableCycleGapSeconds,
                    "[scenario] the crowd's plan cycles went on across every building or prop cut (widest measured gap " + widest.ToString("F2", Inv) + " s, at most " + PlayableCycleGapSeconds + ")");
                Expect(widestOpenEnd <= PlayableCycleGapSeconds,
                    "[scenario] no window end without a plan cycle stood longer than a stop would (" + missingStarts + "+" + missingEnds + " ends missing a cycle, the longest open " + widestOpenEnd.ToString("F2", Inv) + " s, at most " + PlayableCycleGapSeconds + ")");
            }
        }
    }
}
