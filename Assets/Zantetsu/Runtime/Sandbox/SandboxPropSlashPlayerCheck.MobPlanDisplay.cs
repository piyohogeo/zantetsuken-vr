using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    // Measurement (the MobPlanSlash unit, the "a hit NPC vanishes" separation): every root cut of an NPC, followed from its
    // hit to 90 frames after its geometry commits -- which slot and individual it was, how often that slot had been used,
    // the acceptance, the withdrawal, the Provisional and Final publications, the commit -- and, frame by frame, whether the
    // cut's source and its two children are registered for drawing in the display's snapshot, where (their world bounds,
    // and whether those are in the view), where their physics owners are, and which stored geometry they have. Written as
    // it goes (mobplan-display.csv, flushed), so a run ended early still has every hit. Only reads.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private sealed class DisplayTrack
            {
                public string name;
                public int slot, activations, reprepared, hitFrame;
                public CutOperationId operation;
                public LogicalFragmentId source;
                public ProvisionalCutAcceptance acceptance;
                public Zantetsu.PhysicsCut.VpPreparedCharacterCut handle;
                public int withdrawnFrame = -1, provisionalFrame = -1, finalFrame = -1, committedFrame = -1, endFrame = -1;
                public int sourceDrawn, childDrawnAfterCommit, childInViewAfterCommit, childLiveAfterCommit, firstChildDrawn = -1;
                public LogicalFragmentId positive, negative;
                public bool done;
            }

            private readonly List<DisplayTrack> _mpDisplay = new List<DisplayTrack>();
            private StreamWriter _mpDisplayRows;
            private int _mpPictured;
            private readonly Dictionary<LogicalFragmentId, (Bounds world, int registration)> _mpDrawn = new Dictionary<LogicalFragmentId, (Bounds, int)>();
            private readonly Plane[] _mpFrustum = new Plane[6];
            private const int DisplayFramesAfterCommit = 90;

            // A root hit on an NPC: the slot, the individual and the reuse, and the cut to follow.
            private void MobPlanTrackHit(in SlashHitConfirmed hit, SandboxNpcCharacter c, int frame)
            {
                if (hit.Acceptance != ProvisionalCutAcceptance.Published && hit.Acceptance != ProvisionalCutAcceptance.Pending
                    && hit.Acceptance != ProvisionalCutAcceptance.Held) return;
                if (_mpDisplayRows == null && _mpDetail)
                {
                    _mpDisplayRows = new StreamWriter(Path.Combine(directory, "mobplan-display.csv")) { AutoFlush = MobPlanLive };
                    _mpDisplayRows.WriteLine("frame,op,name,slot,activations,role,fragment,state,drawn,registration,inView,centreX,centreY,centreZ,extent,ownerX,ownerY,ownerZ,ownerActive,ownerWithdrawn,vertexStart,vertexCount,shown,drawCommands");
                }

                var t = new DisplayTrack
                {
                    name = Model(c), slot = _crowd.Slots.ToList().IndexOf(c), activations = c.Activations, reprepared = c.Reprepared, hitFrame = frame,
                    operation = hit.Operation, source = hit.Fragment, acceptance = hit.Acceptance, handle = c.Handle,
                };
                _mpDisplay.Add(t);
                MobPlanRecord("display track: " + t.name + " slot=" + t.slot + " activations=" + t.activations + " reprepared=" + t.reprepared
                    + " op=" + t.operation.value + " source=" + t.source.value + " acceptance=" + t.acceptance + " frame=" + frame + " cut " + _mpDisplay.Count
                    + " free=" + _crowd.FreeSlots + " returning=" + _crowd.ReturningSlots + " preparing=" + _crowd.PreparingSlots
                    + " lod=" + (_mpLod != null ? _mpLod.CharacterCount : -1) + " detector=" + _detector.CharacterCount
                    + " shown=" + _world.Display.ShownCount + " renderFragments=" + _world.Display.RenderFragmentCount + " drawCommands=" + _world.Display.DrawCommandCount
                    + " roomGrowths=" + _world.Display.RoomGrowths + " branchCapacity=" + _world.Display.BranchCapacity + " instanceCapacity=" + _world.Display.InstanceCapacity
                    + " halted=" + _world.Display.IsHalted);
                Log("mobplan display track: " + t.name + " slot=" + t.slot + " activations=" + t.activations + " reprepared=" + t.reprepared
                    + " op=" + t.operation.value + " source=" + t.source.value + " acceptance=" + t.acceptance + " frame=" + frame
                    + " cuts so far=" + _mpDisplay.Count);
            }

            // Pictures: the first few root hits, and the root hits near and past the branch capacity, up to a bound.
            private bool MobPlanWantsPicture(in SlashHitConfirmed hit)
            {
                if (_world.Ledger.TryGetOrigin(hit.Fragment, out _, out _)) return false;
                LogicalFragmentId fragment = hit.Fragment;
                SandboxNpcCharacter c = _npcs.FirstOrDefault(x => x != null && x.Handle != null && x.Handle.Source == fragment);
                if (c == null || _mpPictured >= 30) return false;
                // The first few, and those near and past the display's branch capacity (128 by default), where cuts were seen
                // not drawn on the headset.
                bool want = _mpPictured < 4 || _world.Display.ShownCount >= 110;
                if (want) _mpPictured++;
                return want;
            }

            // Every frame: what the display's snapshot registers for drawing, and each followed cut's fragments against it.
            private void MobPlanDisplayFrame(int frame)
            {
                if (_mpDisplay.All(t => t.done)) return;
                _mpDrawn.Clear();
                VpLogicalCutDisplay display = _world.Display;
                for (int i = 0; i < display.RenderFragmentCount; i++)
                {
                    if (!display.TryGetRenderFragment(i, out VpMultiCutRenderFragment rf)) continue;
                    Bounds world = TransformBounds(rf.geometryLocalToWorld, rf.localBounds);
                    if (_mpDrawn.TryGetValue(rf.root, out (Bounds world, int registration) known)) { known.world.Encapsulate(world); _mpDrawn[rf.root] = known; }
                    else _mpDrawn[rf.root] = (world, rf.registration);
                }

                Camera view = Camera.main;
                if (view != null) GeometryUtility.CalculateFrustumPlanes(view, _mpFrustum);
                foreach (DisplayTrack t in _mpDisplay)
                {
                    if (t.done) continue;
                    if (t.withdrawnFrame < 0 && t.handle != null && t.handle.IsWithdrawn) t.withdrawnFrame = frame;
                    ProvisionalCutTransaction x = t.operation.IsSet ? _world.Driver.TransactionOf(t.operation) : null;
                    if (t.provisionalFrame < 0 && x != null && (x.Phase == ProvisionalCutPhase.Published || x.Phase == ProvisionalCutPhase.FinalHeld
                        || x.Phase == ProvisionalCutPhase.HandedOff)) t.provisionalFrame = frame;
                    bool final = t.operation.IsSet && _world.Ledger.TryGetOperation(t.operation, out LogicalCutOperation op) && op.state != LogicalCutOperationState.Admitted;
                    if (final && t.finalFrame < 0)
                    {
                        t.finalFrame = frame;
                        _world.Ledger.TryGetOperation(t.operation, out LogicalCutOperation made);
                        t.positive = made.positive;
                        t.negative = made.negative;
                        if (t.provisionalFrame < 0) t.provisionalFrame = frame;
                    }

                    if (t.committedFrame < 0 && t.operation.IsSet && _world.Geometry.StageOf(t.operation) == CutGeometryStage.Committed)
                    {
                        t.committedFrame = frame;
                        t.endFrame = frame + DisplayFramesAfterCommit;
                    }

                    bool sourceDrawn = Row(frame, t, "source", t.source, view != null);
                    if (sourceDrawn) t.sourceDrawn++;
                    bool anyChildDrawn = false, anyChildInView = false, anyChildLive = false;
                    foreach ((string role, LogicalFragmentId child) in new[] { ("positive", t.positive), ("negative", t.negative) })
                    {
                        if (!child.IsSet) continue;
                        bool drawn = Row(frame, t, role, child, view != null, out bool inView, out bool live);
                        anyChildDrawn |= drawn;
                        anyChildInView |= drawn && inView;
                        anyChildLive |= live;
                    }

                    if (anyChildDrawn && t.firstChildDrawn < 0) t.firstChildDrawn = frame;
                    if (t.committedFrame >= 0)
                    {
                        if (anyChildDrawn) t.childDrawnAfterCommit++;
                        if (anyChildInView) t.childInViewAfterCommit++;
                        if (anyChildLive) t.childLiveAfterCommit++;
                    }

                    if (t.endFrame >= 0 && frame >= t.endFrame)
                    {
                        t.done = true;
                        MobPlanRecord(DisplayLine(_mpDisplay.IndexOf(t) + 1, t));
                    }
                }
            }

            private bool Row(int frame, DisplayTrack t, string role, LogicalFragmentId fragment, bool haveView)
            {
                return Row(frame, t, role, fragment, haveView, out _, out _);
            }

            private bool Row(int frame, DisplayTrack t, string role, LogicalFragmentId fragment, bool haveView, out bool inView, out bool live)
            {
                _world.Ledger.TryGetFragmentState(fragment, out LogicalFragmentState state);
                live = state == LogicalFragmentState.Live;
                bool drawn = _mpDrawn.TryGetValue(fragment, out (Bounds world, int registration) d);
                inView = drawn && haveView && GeometryUtility.TestPlanesAABB(_mpFrustum, d.world);
                if (_mpDisplayRows == null) return drawn;
                bool owned = _world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) && owner.Root != null;
                Vector3 o = owned ? owner.Root.transform.position : Vector3.zero;
                bool geometry = _world.Geometry.TryGetGeometry(fragment, out VpStoredGeometry g);
                _mpDisplayRows.WriteLine(string.Join(",", frame, t.operation.value, t.name, t.slot, t.activations, role, fragment.value, state,
                    drawn ? 1 : 0, drawn ? d.registration : -1, inView ? 1 : 0,
                    drawn ? d.world.center.x.ToString("F3", Inv) : "", drawn ? d.world.center.y.ToString("F3", Inv) : "", drawn ? d.world.center.z.ToString("F3", Inv) : "",
                    drawn ? d.world.extents.magnitude.ToString("F3", Inv) : "",
                    owned ? o.x.ToString("F3", Inv) : "", owned ? o.y.ToString("F3", Inv) : "", owned ? o.z.ToString("F3", Inv) : "",
                    owned && owner.Root.activeInHierarchy ? 1 : 0, owned && owner.IsWithdrawn ? 1 : 0,
                    geometry ? g.vertexStart : -1, geometry ? g.vertexCount : -1, _world.Display.ShownCount, _world.Display.DrawCommandCount));
                return drawn;
            }

            private static string DisplayLine(int index, DisplayTrack t)
            {
                string verdict = t.committedFrame < 0 ? "not committed"
                    : t.childDrawnAfterCommit == 0 ? (t.childLiveAfterCommit == 0 ? "children not live after the commit" : "children live but NOT DRAWN after the commit")
                    : t.childInViewAfterCommit == 0 ? "children drawn, out of the view" : "children drawn in the view";
                return "mobplan display cut " + index + ": " + t.name + " slot=" + t.slot + " activations=" + t.activations + " op=" + t.operation.value
                    + " acceptance=" + t.acceptance + " hit@" + t.hitFrame + " withdrawn@" + t.withdrawnFrame + " provisional@" + t.provisionalFrame
                    + " final@" + t.finalFrame + " committed@" + t.committedFrame + " sourceDrawnFrames=" + t.sourceDrawn + " firstChildDrawn@" + t.firstChildDrawn
                    + " afterCommit: drawn " + t.childDrawnAfterCommit + " inView " + t.childInViewAfterCommit + " live " + t.childLiveAfterCommit
                    + " of " + DisplayFramesAfterCommit + " frames -> " + verdict;
            }

            private static Bounds TransformBounds(Matrix4x4 m, Bounds b)
            {
                Vector3 c = m.MultiplyPoint3x4(b.center);
                Vector3 e = b.extents;
                Vector3 x = m.MultiplyVector(new Vector3(e.x, 0f, 0f)), y = m.MultiplyVector(new Vector3(0f, e.y, 0f)), z = m.MultiplyVector(new Vector3(0f, 0f, e.z));
                var extents = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
                return new Bounds(c, 2f * extents);
            }

            // At the end: each followed cut in one line, and the first whose children were never drawn after the commit.
            private void MobPlanDisplaySummary()
            {
                _mpDisplayRows?.Flush();
                int index = 0;
                DisplayTrack firstMissing = null;
                foreach (DisplayTrack t in _mpDisplay)
                {
                    index++;
                    Log(DisplayLine(index, t));
                    if (firstMissing == null && t.committedFrame >= 0 && t.childDrawnAfterCommit == 0) firstMissing = t;
                }

                Log(firstMissing == null ? "mobplan display: every committed NPC cut had a child drawn after its commit (" + _mpDisplay.Count + " cuts)"
                    : "mobplan display: FIRST cut with no child drawn after its commit: " + firstMissing.name + " op " + firstMissing.operation.value
                        + " slot " + firstMissing.slot + " activations " + firstMissing.activations);
                _mpDisplayRows?.Dispose();
                _mpDisplayRows = null;
            }
        }
    }
}
