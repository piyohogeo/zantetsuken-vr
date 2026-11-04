#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    // NPC-BONE-PROBE-1 (diagnosis only): what the additional split last did with each fragment, kept in development builds for the piece
    // probe (Sandbox's PieceProbe, -zantetsuPieceProbe): the attempt's end, the bones' decision, whether it fell back, and, for a piece a
    // split made, its source and the route. A bounded table (cleared when full); read by DescribePiece only -- nothing is decided by it.
    public sealed partial class CutSpatialSplit
    {
#if ZANTETSU_OBSERVE
        private struct PieceNote
        {
            internal bool attempted, boneLed, fellBack;
            internal int endFrame, madeFrame;
            internal SpatialSplitEnd end;
            internal NpcBoneSplitRecord bone;
            internal LogicalFragmentId madeFrom;
            internal string madeBy;
        }

        private const int PieceNoteRoom = 16384;
        private readonly Dictionary<LogicalFragmentId, PieceNote> _pieceNotes = new Dictionary<LogicalFragmentId, PieceNote>();

        private void NotePieceEnd(Attempt attempt, SpatialSplitEnd end)
        {
            if (_pieceNotes.Count >= PieceNoteRoom) _pieceNotes.Clear();
            _pieceNotes.TryGetValue(attempt.fragment, out PieceNote note);
            note.attempted = true;
            note.endFrame = Time.frameCount;
            note.end = end;
            note.boneLed = attempt.boneLed;
            note.fellBack = attempt.boneFellBack;
            note.bone = attempt.bone;
            _pieceNotes[attempt.fragment] = note;
        }

        private void NotePieceMade(LogicalFragmentId source, LogicalFragmentId positive, LogicalFragmentId negative, Attempt attempt)
        {
            string by = attempt.BoneRoute ? "the bones"
                : (attempt.boneFellBack ? "the fallback's " : "") + (attempt.graphLed ? "graph" : IsBentResult(in attempt.search) ? "bent partition" : attempt.cut ? "convex cut" : "plane");
            if (_pieceNotes.Count >= PieceNoteRoom - 2) _pieceNotes.Clear();
            _pieceNotes[positive] = new PieceNote { madeFrom = source, madeFrame = Time.frameCount, madeBy = by + " (positive side)" };
            _pieceNotes[negative] = new PieceNote { madeFrom = source, madeFrame = Time.frameCount, madeBy = by + " (negative side)" };
        }
#endif

        /// <summary>
        /// One line on a fragment for the piece probe (diagnosis only): its object, its actor and body, its convexes and their bones, its
        /// NPC record, how the split looks at it now, what made it (a split's source and route) and how its last attempt ended. Reads only.
        /// </summary>
        public string DescribePiece(LogicalFragmentId fragment)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder(512);
            text.Append("fragment ").Append(fragment.value);
            if (_world.Ledger != null && _world.Ledger.TryGetRoot(fragment, out LogicalFragmentId root)) text.Append(" (of the object of fragment ").Append(root.value).Append(')');
            if (_world.Owners != null && _world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner))
            {
                text.Append("; actor ").Append(owner.Root != null ? owner.Root.name + " #" + owner.Root.GetInstanceID().ToString(inv) : "none")
                    .Append(", body #").Append(owner.Body != null ? owner.Body.GetInstanceID().ToString(inv) : "none");
                PhysicsOwnerShape shape = owner.Shape;
                if (shape != null && !shape.IsFreed)
                {
                    text.Append(", convexes ").Append(shape.ConvexCount);
                    if (shape.HasConvexBones)
                    {
                        text.Append(" (bones");
                        for (int c = 0; c < shape.ConvexCount; c++) text.Append(' ').Append(shape.ConvexBone(c));
                        text.Append(')');
                    }
                }
            }
            else
            {
                text.Append("; no owner now");
            }

            if (_world.NpcBones != null && _world.NpcBones.TryGet(fragment, out NpcBoneFragment record))
            {
                int kept = 0, n = record.Lineage.NodeCount;
                for (int k = 0; k < n; k++) if (record.Piece.IsJointKept(k)) kept++;
                text.Append("; an NPC's: joints kept ").Append(kept).Append(" of ").Append(n);
            }

            text.Append("; the split's look now: ").Append(Examine(fragment));
#if ZANTETSU_OBSERVE
            if (_pieceNotes.TryGetValue(fragment, out PieceNote note))
            {
                if (note.madeFrom.IsSet) text.Append("; made at frame ").Append(note.madeFrame).Append(" from fragment ").Append(note.madeFrom.value).Append(" by ").Append(note.madeBy);
                if (note.attempted)
                {
                    text.Append("; its last attempt ended ").Append(note.end).Append(" at frame ").Append(note.endFrame);
                    if (note.boneLed && note.bone.Entered)
                    {
                        text.Append(", the bones: ").Append(note.bone.verdict).Append(" (").Append(note.bone.reason).Append("), bones held ").Append(note.bone.pieceBones)
                            .Append(", clusters ").Append(note.bone.clusters).Append(", groups ").Append(note.bone.groups).Append(" (").Append(note.bone.fullGroups).Append(" with a convex and triangles, ")
                            .Append(note.bone.unboundGroups).Append(" mesh alone)").Append(note.fellBack ? ", then the method as before" : "");
                    }
                }
                else
                {
                    text.Append("; not attempted since it was made");
                }
            }
            else
            {
                text.Append("; no note: not attempted, and not made by an additional split");
            }
#else
            text.Append("; the split's notes are kept in development builds only");
#endif
            return text.ToString();
        }
    }
}
