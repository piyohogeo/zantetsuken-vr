using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Zantetsu.Core.Animation;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // Measurement (the MultiNpcSlash benchmark, Assets/Licensed/MultiNpc/MultiNpcCity.unity): ten NPCs of ten models, no
    // box. With MultiNpcArgument the check does not ask for the box and does not judge the single target; instead it
    // follows every NPC's lineage, every hit's outcome by Slash, and every accepted cut -- Published or Pending -- to its
    // Provisional publication, its Final/Logical publication and its geometry commit, frame by frame (the "frame" records of CheckFrames,
    // the "op" records of the hit record, CheckHits), and judges the benchmark's functional conditions at the end. It only reads: nothing is cut, hit,
    // published, reset or completed from here.
    public static partial class SandboxPropSlashPlayerCheck
    {
        public const string MultiNpcArgument = "-zantetsuMultiNpc";

        private sealed partial class Walk
        {
            internal bool multiNpc;

            private readonly Dictionary<LogicalFragmentId, SandboxNpcCharacter> _multiRoots = new Dictionary<LogicalFragmentId, SandboxNpcCharacter>();
            private readonly SortedDictionary<long, SlashTally> _multiSlashes = new SortedDictionary<long, SlashTally>();
            private readonly Dictionary<LogicalFragmentId, int> _multiAcceptedOnSource = new Dictionary<LogicalFragmentId, int>();
            private readonly HashSet<long> _multiSlashEnded = new HashSet<long>();

            // Hits held before acceptance: the character, the Slash and the frame it was held, until its handle is accepted.
            private readonly List<(SandboxNpcCharacter character, long slash, LogicalFragmentId fragment, int frame)> _multiHeld =
                new List<(SandboxNpcCharacter, long, LogicalFragmentId, int)>();
            private int _multiHeldAccepted, _multiHeldEnded;
            private bool _multiStartChecked;
            private int _multiMaxWaves;
            private int _multiMixedFrames;
            private string _multiFirstMixed;

            private sealed class SlashTally
            {
                public int latchFrame = -1;
                public int hits, published, pending, held, emptySide, notAccepted, anchorsRefused, aborted, stale, invalid;
                public readonly SortedDictionary<string, int> admissions = new SortedDictionary<string, int>();
                public readonly HashSet<string> roots = new HashSet<string>();      // lineages it was accepted on as a root
                public readonly HashSet<string> met = new HashSet<string>();        // lineages it hit at all
                public int childHits, childAccepted;
                public readonly Dictionary<int, int> publishedAtFrame = new Dictionary<int, int>();
            }

            private static string Model(SandboxNpcCharacter c) => s_mobPlanNames.TryGetValue(c, out string named) ? named
                : c.CharacterRoot != null ? c.CharacterRoot.name.Replace("NPC ", "") : c.name;

            private SlashTally Tally(long slash)
            {
                if (!_multiSlashes.TryGetValue(slash, out SlashTally t)) _multiSlashes[slash] = t = new SlashTally();
                return t;
            }

            // At the replay's start: every NPC's lineage root, its Pose Table restarted with the replay (each keeps its own
            // source offset, its walk phase), and what each is.
            private void MultiBegin()
            {
                foreach (SandboxNpcCharacter c in _npcs)
                {
                    PoseTablePlayer pose = c.CharacterRoot != null ? c.CharacterRoot.GetComponent<PoseTablePlayer>() : null;
                    if (pose != null && pose != _npcPose) pose.Restart();
                    Log("multi npc: " + Model(c) + " target=" + c.IsTarget + " failure=" + (c.Failure ?? "none")
                        + " hulls=" + c.HullCount + " renderer=" + (c.Renderer != null ? c.Renderer.name + " vertices=" + c.Renderer.sharedMesh.vertexCount + " bones=" + c.Renderer.bones.Length : "none")
                        + " position=" + (c.CharacterRoot != null ? c.CharacterRoot.transform.position.ToString("F3") : "none")
                        + " yaw=" + (c.CharacterRoot != null ? c.CharacterRoot.transform.eulerAngles.y.ToString("F1", Inv) : "none")
                        + " table=" + (pose != null && pose.Table != null ? pose.Table.ClipName + " resolved=" + pose.ResolvedBoneCount + " unresolved=" + pose.UnresolvedBoneCount + " bonesConfirmed=" + pose.BonesConfirmed : "none")
                        + " lod=" + (c.Lod != null ? "registered" : "none"));
                }

                // A character's lineage root is issued when a hit first identifies it (VpPreparedCharacterCut.TryIdentify),
                // not before: what is judged here is that all ten are prepared targets; their roots are taken as they come.
                Expect(_npcs.Count == 10 && _npcs.All(c => c.IsTarget), "ten NPCs, each a prepared hit target (" + _npcs.Count(c => c.IsTarget) + " of " + _npcs.Count + ")");
                Expect(_npcs.Select(c => c.Renderer != null && c.Renderer.sharedMesh != null ? c.Renderer.sharedMesh.vertexCount + ":" + Model(c) : Model(c)).Distinct().Count() == _npcs.Count,
                    "every NPC has a model of its own");
                HitLogOpen("multiNpc");
                FrameLogOpen("multiNpc");   // the per-frame rows (formerly multi.csv), a recording of their own
                MultiStorage("before replay");
            }

            // The first frame after the replay began, once the Pose Tables have applied: where each NPC stands.
            // Overlap: the smallest distance between vertices of two NPCs' skinned meshes as drawn now (a 5 cm grid);
            // the floor: each mesh's lowest vertex, and the collider under its root.
            private void MultiCheckStart()
            {
                _multiStartChecked = true;
                var baked = new List<(string model, Vector3[] points)>();
                var mesh = new Mesh();
                foreach (SandboxNpcCharacter c in _npcs)
                {
                    SkinnedMeshRenderer r = c.Renderer;
                    if (r == null) continue;
                    r.BakeMesh(mesh, true);
                    Matrix4x4 m = Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
                    Vector3[] v = mesh.vertices;
                    for (int i = 0; i < v.Length; i++) v[i] = m.MultiplyPoint3x4(v[i]);
                    baked.Add((Model(c), v));
                    float lowest = v.Min(p => p.y);
                    Vector3 root = c.CharacterRoot.transform.position;
                    string floor = Physics.Raycast(root + Vector3.up, Vector3.down, out RaycastHit under, 3f, ~0, QueryTriggerInteraction.Ignore)
                        ? under.collider.name + " top y=" + under.point.y.ToString("F3", Inv) : "none";
                    Log("multi start: " + Model(c) + " lowest vertex y=" + lowest.ToString("F4", Inv) + " floor under root=" + floor
                        + " bounds=" + r.bounds.min.ToString("F2") + ".." + r.bounds.max.ToString("F2"));
                    // Diagnostic only (TL, 2026-09-27): how far the walking pose dips into the floor at the replay's start is
                    // written, not judged. Penetration that actually throws pieces or breaks the physics is a separate matter.
                    Log("multi start (diagnostic, not judged): " + Model(c) + " floor penetration " + (lowest < 0f ? (-lowest * 100f).ToString("F2", Inv) + " cm" : "none"));
                    Expect(floor != "none", Model(c) + " stands over the floor");
                }

                Object.Destroy(mesh);
                const float Cell = 0.05f;
                var grid = new Dictionary<Vector3Int, List<(int npc, Vector3 p)>>();
                for (int n = 0; n < baked.Count; n++)
                {
                    foreach (Vector3 p in baked[n].points)
                    {
                        var key = Vector3Int.FloorToInt(p / Cell);
                        if (!grid.TryGetValue(key, out List<(int, Vector3)> list)) grid[key] = list = new List<(int, Vector3)>();
                        list.Add((n, p));
                    }
                }

                var nearest = new Dictionary<(int, int), float>();
                foreach (KeyValuePair<Vector3Int, List<(int npc, Vector3 p)>> cell in grid)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue(cell.Key + new Vector3Int(dx, dy, dz), out List<(int npc, Vector3 p)> other)) continue;
                        foreach ((int a, Vector3 pa) in cell.Value)
                        foreach ((int b, Vector3 pb) in other)
                        {
                            if (a >= b) continue;
                            float d = Vector3.Distance(pa, pb);
                            if (!nearest.TryGetValue((a, b), out float was) || d < was) nearest[(a, b)] = d;
                        }
                    }
                }

                Log("multi start: NPC pairs with vertices within 7.5 cm (grid of 5 cm): "
                    + (nearest.Count == 0 ? "none" : string.Join(", ", nearest.Select(k => baked[k.Key.Item1].model + "-" + baked[k.Key.Item2].model + " " + (k.Value * 100f).ToString("F1", Inv) + " cm"))));
                Expect(nearest.Values.All(d => d > 0.01f), "no two NPCs overlap where they start (no vertices of two NPCs within 1 cm)");
            }

            // One hit, as the check logged it: which Slash, which lineage, what the acceptance said.
            private void MultiOnHit(in SlashHitConfirmed hit, int frame, bool child, int update)
            {
                SlashTally t = Tally(hit.SlashId);
                string of = LineageOf(hit.Fragment);
                t.hits++;
                t.met.Add(of);
                if (child) t.childHits++;
                switch (hit.Acceptance)
                {
                    case ProvisionalCutAcceptance.Published: t.published++; break;
                    case ProvisionalCutAcceptance.Pending: t.pending++; break;
                    case ProvisionalCutAcceptance.Held:
                        t.held++;
                        foreach (KeyValuePair<LogicalFragmentId, SandboxNpcCharacter> root in _multiRoots)
                        {
                            if ("npc-" + Model(root.Value) == of) _multiHeld.Add((root.Value, hit.SlashId, hit.Fragment, frame));
                        }

                        break;
                    case ProvisionalCutAcceptance.EmptySide: t.emptySide++; break;
                    case ProvisionalCutAcceptance.NotAccepted: t.notAccepted++; break;
                    case ProvisionalCutAcceptance.AnchorsRefused: t.anchorsRefused++; break;
                    case ProvisionalCutAcceptance.Aborted: t.aborted++; break;
                    case ProvisionalCutAcceptance.Stale: t.stale++; break;
                    default: t.invalid++; break;
                }

                string admission = hit.Admission.ToString();
                t.admissions.TryGetValue(admission, out int n);
                t.admissions[admission] = n + 1;
                bool accepted = hit.Acceptance == ProvisionalCutAcceptance.Published || hit.Acceptance == ProvisionalCutAcceptance.Pending;
                if (accepted)
                {
                    if (child) t.childAccepted++; else t.roots.Add(of);
                    _multiAcceptedOnSource.TryGetValue(hit.Fragment, out int k);
                    _multiAcceptedOnSource[hit.Fragment] = k + 1;
                }

                if (hit.Acceptance == ProvisionalCutAcceptance.Published)
                {
                    t.publishedAtFrame.TryGetValue(frame, out int p);
                    t.publishedAtFrame[frame] = p + 1;
                }

                HitLogHit(hit, frame, of, child, update);
                SandboxNpcCharacter c = _multiRoots.Values.FirstOrDefault(x => "npc-" + Model(x) == of);
                PoseTablePlayer pose = c != null && c.CharacterRoot != null ? c.CharacterRoot.GetComponent<PoseTablePlayer>() : null;
                Log("multi hit: slashId=" + hit.SlashId + " of=" + of + (child ? " child" : " root") + " fragment=" + hit.Fragment.value
                    + " acceptance=" + hit.Acceptance + " admission=" + hit.Admission + " operation=" + hit.Operation.value + " frame=" + frame
                    + (pose != null ? " pose=(frame " + pose.AppliedFrame + ", source " + pose.AppliedSourceTime.ToString("F4", Inv) + ")" : ""));
            }

            // Every frame of the replay: the latches, each accepted cut's stages, and what the scene holds.
            private void MultiFrame(int frame)
            {
                if (!_multiStartChecked && multiNpc) MultiCheckStart();
                double now = Time.unscaledTimeAsDouble - _clockStart;
                for (int w = 0; w < _katana.WaveCount; w++)
                {
                    SlashTally t = Tally(_katana.SlashIdAt(w));
                    if (t.latchFrame < 0) t.latchFrame = frame;
                }

                _multiMaxWaves = Mathf.Max(_multiMaxWaves, _katana.WaveCount);
                int acceptedNow = 0, provisionalNow = 0, finalNow = 0, committedNow = 0, pending = 0, incomplete = 0;
                foreach (Accepted a in _accepted)
                {
                    if (a.acceptedFrame == frame)
                    {
                        acceptedNow++;
                        a.acceptedTime = now;
                        if (!a.pending)
                        {
                            a.provisionalTime = now;
                            provisionalNow++;
                        }
                    }

                    if (a.provisionalFrame < 0)
                    {
                        ProvisionalCutTransaction x = _world.Driver.TransactionOf(a.operation);
                        ProvisionalCutPhase? phase = x != null ? x.Phase : (ProvisionalCutPhase?)null;
                        if (phase == ProvisionalCutPhase.Published || phase == ProvisionalCutPhase.FinalHeld || phase == ProvisionalCutPhase.HandedOff
                            || (phase == null && a.publishedFrame >= 0))
                        {
                            a.provisionalFrame = frame;
                            a.provisionalTime = now;
                            provisionalNow++;
                            Log("multi op" + a.operation.value + " Provisional published after Pending: frame=" + frame + " phase=" + (phase?.ToString() ?? "record ended")
                                + " accepted@" + a.acceptedFrame + " (" + (frame - a.acceptedFrame) + " frames later)");
                        }
                        else if (phase == null || phase == ProvisionalCutPhase.Unestablished || phase == ProvisionalCutPhase.Recovered)
                        {
                            a.pendingEnd ??= "ended without publication at frame " + frame + " phase=" + (phase?.ToString() ?? "record ended");
                        }
                    }

                    if (a.publishedFrame == frame) { finalNow++; a.finalTime = now; }
                    if (a.committedFrame == frame) { committedNow++; a.committedTime = now; }
                    if (a.provisionalFrame < 0) pending++;
                    if (a.committedFrame < 0) incomplete++;
                }

                // A frame where the budget published some cuts and carried others over.
                if (pending > 0 && provisionalNow > 0)
                {
                    _multiMixedFrames++;
                    _multiFirstMixed ??= "frame " + frame + ": " + pending + " carried over beside " + provisionalNow + " published";
                }

                int live = 0, pieces = 0, convexes = 0, uncut = 0;
                for (int id = 1; id < int.MaxValue; id++)
                {
                    var fragment = new LogicalFragmentId(id);
                    if (!_world.Ledger.TryGetFragmentState(fragment, out LogicalFragmentState state)) break;
                    if (state != LogicalFragmentState.Live) continue;
                    live++;
                    bool root = !_world.Ledger.TryGetOrigin(fragment, out _, out _);
                    if (!root && _world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) && !owner.IsWithdrawn)
                    {
                        pieces++;
                        convexes += ConvexCountOf(owner);
                    }
                }

                // Held hits taken up by the driver: the handle has its Operation now, and the cut is followed from here.
                for (int h = _multiHeld.Count - 1; h >= 0; h--)
                {
                    (SandboxNpcCharacter c, long slash, LogicalFragmentId fragment, int heldFrame) = _multiHeld[h];
                    VpPreparedCharacterCut handle = c.Handle;
                    if (handle != null && handle.Operation.IsSet)
                    {
                        _multiHeld.RemoveAt(h);
                        _multiHeldAccepted++;
                        _multiAcceptedOnSource.TryGetValue(fragment, out int k);
                        _multiAcceptedOnSource[fragment] = k + 1;
                        Tally(slash).roots.Add("npc-" + Model(c));
                        _accepted.Add(new Accepted
                        {
                            slash = slash, fragment = fragment, operation = handle.Operation, child = false, acceptedFrame = frame,
                            name = "op" + handle.Operation.value + "-npc-root-held", pending = true, provisionalFrame = -1,
                        });
                        Log("multi held taken up: " + Model(c) + " slash " + slash + " operation " + handle.Operation.value + " at frame " + frame
                            + " (held at frame " + heldFrame + ", " + (frame - heldFrame) + " frames)");
                    }
                    else if (handle == null || (handle.IsDisposed && !handle.Operation.IsSet))
                    {
                        _multiHeld.RemoveAt(h);
                        _multiHeldEnded++;
                        Log("multi held ended without acceptance: " + Model(c) + " slash " + slash + " at frame " + frame);
                    }
                }

                MultiFollowIdentification();
                uncut = _npcs.Count - _multiRoots.Count(k => _multiAcceptedOnSource.ContainsKey(k.Key));

                // A Slash that was latched and is no longer among the live waves has ended: the storage after it.
                foreach (KeyValuePair<long, SlashTally> s in _multiSlashes)
                {
                    bool flying = false;
                    for (int w = 0; w < _katana.WaveCount; w++) flying |= _katana.SlashIdAt(w) == s.Key;
                    if (!flying && s.Value.latchFrame >= 0 && _multiSlashEnded.Add(s.Key)) MultiStorage("after slash " + s.Key);
                }

                ManualPhysicsClock clock = CutPhysicsStep.Clock;
                FrameLogRow(new CheckFrameRow
                {
                    frame = frame, real = now, waves = _katana.WaveCount, uncutNpcs = uncut, liveFragments = live, livePieces = pieces, liveConvexes = convexes,
                    acceptedOps = _accepted.Count, pendingOps = pending, incompleteOps = incomplete, acceptedThisFrame = acceptedNow,
                    provisionalThisFrame = provisionalNow, finalThisFrame = finalNow, committedThisFrame = committedNow,
                    unsimulated = clock != null ? clock.UnsimulatedSeconds : (double?)null, stepId = clock != null ? clock.StepId : -1,
                });
            }

            private void MultiClose()
            {
                HitLogEnd();
                FrameLogEnd();   // after the hit record's summary: a tail the bounded drain cuts takes the frame record's first
                BuildingClose();
                MobPlanClose();
            }

            // The benchmark's functional judgements, and the cuts' stages by operation (the "op" records).
            private void MultiSummarise()
            {
                MultiStorage("at the end");
                // The cuts' stages by operation: one "op" record each in the hit record (formerly multi-ops.csv).
                HitLogOpsPlanned(_accepted.Count);
                foreach (Accepted a in _accepted)
                {
                    string state = _world.Ledger.TryGetOperation(a.operation, out LogicalCutOperation op) ? op.state.ToString() : "none";
                    HitLogOp(new OpRecord
                    {
                        name = a.name, operation = a.operation.value, slash = a.slash, source = a.fragment.value, acceptance = a.pending ? "Pending" : "Published",
                        acceptedFrame = a.acceptedFrame, provisionalFrame = a.provisionalFrame, finalFrame = a.publishedFrame, committedFrame = a.committedFrame,
                        acceptedTime = a.acceptedTime, provisionalTime = a.provisionalTime, finalTime = a.finalTime, committedTime = a.committedTime,
                        ledgerState = state, pendingEnd = a.pendingEnd, hull = a.hull, fusion = a.fusion, of = LineageOf(a.fragment), child = a.child,
                    });
                }

                foreach (KeyValuePair<long, SlashTally> s in _multiSlashes)
                {
                    SlashTally t = s.Value;
                    Log("multi slash " + s.Key + ": latch frame=" + t.latchFrame + " hits=" + t.hits + " published=" + t.published + " pending=" + t.pending
                        + " held=" + t.held
                        + " noOp(emptySide)=" + t.emptySide + " notAccepted=" + t.notAccepted + " anchorsRefused=" + t.anchorsRefused + " aborted=" + t.aborted
                        + " stale=" + t.stale + " invalid=" + t.invalid + " admissions=[" + string.Join(",", t.admissions.Select(k => k.Key + ":" + k.Value)) + "]"
                        + " rootLineagesAccepted=" + t.roots.Count + " childHits=" + t.childHits + " childAccepted=" + t.childAccepted
                        + " lineagesMet=[" + string.Join(",", t.met.OrderBy(x => x, System.StringComparer.Ordinal)) + "]"
                        + " sameFramePublished max=" + (t.publishedAtFrame.Count > 0 ? t.publishedAtFrame.Values.Max() : 0)
                        + " notMet=[" + string.Join(",", _multiRoots.Values.Select(c => "npc-" + Model(c)).Where(m => !t.met.Contains(m)).OrderBy(x => x, System.StringComparer.Ordinal)) + "]");
                }

                Log("multi: slashes latched=" + _multiSlashes.Count(k => k.Value.latchFrame >= 0) + " max live waves=" + _multiMaxWaves + " (capacity " + Zantetsu.Core.Slash.SlashWaveCore.Capacity + ")"
                    + " frames with Pending beside a publication=" + _multiMixedFrames + (_multiFirstMixed != null ? " (first " + _multiFirstMixed + ")" : ""));

                // The first Slash: ten distinct root lineages hit and accepted, each then published and committed.
                long first = _multiSlashes.Count > 0 ? _multiSlashes.Keys.Min() : 0;
                SlashTally one = first > 0 ? _multiSlashes[first] : null;
                Expect(one != null && one.roots.Count == 10 && _multiRoots.Values.All(c => one.roots.Contains("npc-" + Model(c))),
                    "the first Slash (" + first + ") was accepted on all ten NPCs' lineages (" + (one != null ? one.roots.Count : 0) + ")");
                Expect(_accepted.Where(a => a.slash == first && !a.child).All(a => a.provisionalFrame >= 0 && a.publishedFrame >= 0 && a.committedFrame >= 0),
                    "each of the first Slash's cuts was Provisional published, Final/Logical published and its geometry committed");
                Expect(_multiSlashes.Count >= 5, "the replay latched five Slashes (" + _multiSlashes.Count + ")");
                Expect(_accepted.Any(a => a.child && a.slash != first && a.committedFrame >= 0),
                    "a live child was cut again by another Slash, and that cut committed");

                // Pending: tracked like any accepted cut; the same operation publishes later, never accepted twice.
                List<Accepted> pend = _accepted.Where(a => a.pending).ToList();
                Log("multi pending: " + pend.Count + " accepted as Pending" + (pend.Count > 0 ? ", Provisional published after " + string.Join(",", pend.Select(a => (a.provisionalFrame - a.acceptedFrame) + "f")) : " (none in this run: the budget did not run out at an acceptance)"));
                Expect(pend.All(a => a.provisionalFrame > a.acceptedFrame && a.publishedFrame >= 0 && a.committedFrame >= 0 && a.pendingEnd == null),
                    "every Pending cut was published later as the same operation, then Final/Logical published and committed");
                Log("multi held: " + _multiHeldAccepted + " taken up and accepted, " + _multiHeldEnded + " ended without acceptance, " + _multiHeld.Count + " still held");
                Expect(_multiHeld.Count == 0 && _multiHeldEnded == 0, "every hit held before acceptance was taken up and accepted, once");
                Expect(_multiAcceptedOnSource.Values.All(k => k == 1), "no fragment was accepted twice (no duplicate acceptance)");
                // Each Evaluate's hits read once: the frames that still showed a list already read, and the condition
                // under which the input time names the evaluation (every replay frame advanced the tick time).
                Log("hit lists: " + _hitListRereads.Count + " frame(s) still showed an evaluation already read, not read again"
                    + (_hitListRereads.Count > 0 ? ": " + string.Join(", ", _hitListRereads.Select(r => "frame " + r.frame + " (" + r.count + " hit(s) at input time " + r.at.ToString("R", Inv) + ", fed " + r.fed + ")")) : ""));
                Expect(_timeline.Any(r => r.phase == "replay") && _timeline.Where(r => r.phase == "replay").All(r => r.delta > 0f),
                    "every replay frame had a positive unscaled delta (so each evaluation's input time is later than the last, and names it)");
                Expect(_accepted.All(a => !_world.Ledger.TryGetOperation(a.operation, out LogicalCutOperation op) || op.state == LogicalCutOperationState.Completed),
                    "every accepted operation completed (none lost or aborted)");
            }

            private string MultiLineageOf(LogicalFragmentId root)
            {
                MultiFollowIdentification();
                return _multiRoots.TryGetValue(root, out SandboxNpcCharacter c) ? "npc-" + Model(c) : "other";
            }

            // Each NPC's lineage root as it is issued: the first hit that identifies a character gives its handle a
            // Source, which stays readable after the handle ends. Taken the first time it is seen, and written down.
            private void MultiFollowIdentification()
            {
                foreach (SandboxNpcCharacter c in _npcs)
                {
                    if (c.Handle == null || !c.Handle.Source.IsSet || _multiRoots.ContainsKey(c.Handle.Source)) continue;
                    _multiRoots[c.Handle.Source] = c;
                    Log("multi npc identified: " + Model(c) + " root fragment=" + c.Handle.Source.value + " frame=" + Time.frameCount);
                }
            }

            // The storage's room in every kind, as the product describes it: before the replay, after each Slash, at the end.
            private void MultiStorage(string when)
            {
                string line = "multi storage " + when + ": frame=" + Time.frameCount + " " + (_world != null && _world.Storage != null ? _world.Storage.DescribeRoom() : "none")
                    + (_world != null && _world.Display != null ? " " + _world.Display.DescribeGpuRoom() + "; " + _world.Display.DescribeRoom() : "");
                Log(line);
                MobPlanRecord(line);
            }

            // Written before anything else when the Player is ending without this run having finished -- the product's
            // own termination (Application.Quit) among them: the held log, the frame rows and the hits are kept.
            private void MultiQuitting()
            {
                if (Done) return;
                Log("INCOMPLETE: the Player is quitting before the scenario finished (phase " + _phase + ", frame " + Time.frameCount + ")");
                if (multiNpc) MultiStorage("at quitting");
                // The building rest's summary is still written when the world ended early (its counters and record survive the ending).
                try { PlayableRestSummary(); } catch (System.Exception e) { Log("playable city rest summary at quitting failed: " + e.GetType().Name + ": " + e.Message); }
                // So are the fusion's breakdown and the frame timeline (the snapshot's stages among its markers): a forced ending keeps the costs up to it.
                // The cooking audit's final reconciliation needs the world's ordinary reclaim, which a forced ending does not make: it is not run here.
                if (_world != null && _world.Fusion != null)
                {
                    try { BuildingFusionSummarise(); } catch (System.Exception e) { Log("building fusion summary at quitting failed: " + e.GetType().Name + ": " + e.Message); }
                }

                if (_world != null && _world.Hulls != null)
                {
                    try { BuildingHullSummarise(); } catch (System.Exception e) { Log("building hull summary at quitting failed: " + e.GetType().Name + ": " + e.Message); }
                    BuildingHullClose();
                }

                try { WriteTimeline(false); } catch (System.Exception e) { Log("timeline at quitting failed: " + e.GetType().Name + ": " + e.Message); }
                Log("cooking audit at quitting: not reconciled (the world is not reclaimed the ordinary way at a forced ending)");
                MultiClose();
                ReleaseLog();
            }
        }
    }
}
