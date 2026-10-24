using System;
using System.Collections.Generic;
using System.Diagnostics;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    [Flags]
    public enum SafetyCollectionReason { None = 0, Fragments = 1, VertexBlocks = 2 }

    public sealed partial class CutFragmentGroupCollection
    {
        public const int SafetySamplePerGroup = 8, SafetyPieceLimit = 64, SafetyRetireLimit = 16;
        private readonly List<LogicalFragmentId> _safetyPieces = new List<LogicalFragmentId>(SafetyRetireLimit);
        private readonly int[] _safetyPasses = new int[(int)CutGroupPass.Count];
        public int SafetyTurns { get; private set; }
        public int SafetyBlockedTurns { get; private set; }
        public int SafetyGroupsCompleted { get; private set; }
        public int SafetyPiecesRetired { get; private set; }
        public int SafetyLastPieceChecks { get; private set; }
        public int SafetyMaxPieceChecks { get; private set; }
        public double SafetyMaxRetireMs { get; private set; }
        public int SafetyPassedAs(CutGroupPass pass) => _safetyPasses[(int)pass];
        public SafetyCollectionReason SafetyReason => ReadSafetyReason(Settings);

        // Read from current allocator usage, including open reservations and not-yet-returned lineages.
        // VertexBlockCount is a high-water address, not the usage of its allocator.
        private SafetyCollectionReason ReadSafetyReason(in CutFragmentGroupSettings settings)
        {
            if (!settings.enabled || !settings.safetyEnabled || !settings.IsValid || _world.Ledger == null || _world.Storage == null)
                return SafetyCollectionReason.None;
            SafetyCollectionReason reason = SafetyCollectionReason.None;
            if (_world.Ledger.LiveCutFragmentCount > settings.SafetyFragmentThreshold) reason |= SafetyCollectionReason.Fragments;
            if (_world.Storage.VertexBlockCapacity - _world.Storage.FreeVertexBlockRoom >= settings.safetyVertexBlockThreshold)
                reason |= SafetyCollectionReason.VertexBlocks;
            return reason;
        }

        private void StepSafety(in CutFragmentGroupSettings settings, SafetyCollectionReason reason)
        {
            SafetyTurns++; WalkedTurns++; SafetyLastPieceChecks = 0;
            if (TryRearmOne(ignoreBudget: true)) return;
            Group oldest = null;
            int chosenBegin = 0;
            // Check every registration's age, without sorting, so a rolling group window cannot prefer a younger
            // ready family. Sample only a bounded slice of each potentially older family's pieces. Busy slices
            // advance their existing cursor; they cannot hide ready siblings indefinitely.
            foreach (Group group in _groups)
            {
                if (group.ended || (oldest != null && group.registrationOrder >= oldest.registrationOrder)) continue;
                int count = _world.Ledger.LiveCutCountOf(group.root);
                if (count == 0) continue;
                int begin = group.pieceCursor % count, take = Math.Min(count, SafetySamplePerGroup);
                bool ready = false;
                for (int i = 0; i < take; i++)
                {
                    if (_world.Ledger.TryGetLiveCutFragmentAt(group.root, (begin + i) % count, out LogicalFragmentId piece)
                        && SafetyCanRetire(group, piece)) { ready = true; break; }
                }
                group.pieceCursor = (begin + take) % count;
                if (ready) { oldest = group; chosenBegin = begin; }
            }
            bool did = oldest != null && RetireSafetyPieces(oldest, chosenBegin, in settings, reason);
            if (!did) SafetyBlockedTurns++;
            SafetyMaxPieceChecks = Math.Max(SafetyMaxPieceChecks, SafetyLastPieceChecks);
        }

        private bool SafetyCanRetire(Group group, LogicalFragmentId piece)
        {
            SafetyLastPieceChecks++;
            CutGroupPass pass;
            LogicalCutLedger ledger = _world.Ledger;
            if (!ledger.IsCurrentTarget(piece)) pass = CutGroupPass.NothingToEnd;
            else if (ledger.TryGetActiveOperation(piece, out _) || _world.Owners.TryGetProvisionalOf(piece, out _)) pass = CutGroupPass.CutUnderWay;
            else if (_world.Geometry == null || (!_world.Geometry.HasNoGeometry(piece) && !_world.Geometry.TryGetGeometry(piece, out _))) pass = CutGroupPass.GeometryOpen;
            else if (!_world.Owners.TryGet(piece, out PhysicsFragmentOwner owner) || owner.IsWithdrawn || owner.IsReleased || owner.IsFused || owner.Root == null) pass = CutGroupPass.NoOwner;
            else if (group.origin != null && !group.origin.MayEndNow(out _)) pass = CutGroupPass.SharedBusy;
            else pass = CutGroupPass.Candidate;
            _safetyPasses[(int)pass]++;
            return pass == CutGroupPass.Candidate;
        }

        private bool RetireSafetyPieces(Group group, int begin, in CutFragmentGroupSettings settings, SafetyCollectionReason reason)
        {
            LogicalCutLedger ledger = _world.Ledger;
            int count = ledger.LiveCutCountOf(group.root), before = ledger.LiveCutFragmentCount;
            long began = Stopwatch.GetTimestamp();
            if (count == 1)
            {
                // Keep the final piece until the existing whole-group path can end the shared origin and
                // schedule display adoption, original return and re-arm. No separate draining marker is needed.
                var candidate = new CutGroupCandidate(group.root, ledger.FamilyRevisionOf(group.root), count, float.PositiveInfinity);
                if (!TryApply(in candidate, out CutGroupPass refusal)) { _safetyPasses[(int)refusal]++; return false; }
                SafetyGroupsCompleted++; SafetyPiecesRetired += before - ledger.LiveCutFragmentCount;
                RecordSafetyRetirement(group, reason, before, Since(began) * 1000.0, true);
                return true;
            }
            _safetyPieces.Clear();
            int take = Math.Min(count, SafetyPieceLimit);
            for (int i = 0; i < take && _safetyPieces.Count < SafetyRetireLimit; i++)
            {
                if (ledger.TryGetLiveCutFragmentAt(group.root, (begin + i) % count, out LogicalFragmentId piece)
                    && SafetyCanRetire(group, piece)) _safetyPieces.Add(piece);
            }
            group.pieceCursor = (begin + take) % count;
            int retired = 0;
            RetiringRoot = group.root;
            try
            {
                foreach (LogicalFragmentId piece in _safetyPieces)
                {
                    // Fragment-only pressure ends as soon as the upper threshold is reached, even mid-batch.
                    // Block pressure persists until the storage really gives the lineage's room back.
                    if ((reason & SafetyCollectionReason.VertexBlocks) == 0 && ledger.LiveCutFragmentCount <= settings.SafetyFragmentThreshold) break;
                    if (ledger.LiveCutCountOf(group.root) <= 1) break;
                    if (!SafetyCanRetire(group, piece) || !ledger.Retire(piece)) continue;
                    _world.Owners.Retire(piece); _world.Geometry.Forget(piece); retired++;
                }
            }
            finally { RetiringRoot = default; _safetyPieces.Clear(); }
            if (retired == 0) return false;
            double seconds = Since(began);
            _perFragment.Add(seconds / retired);
            LastRetiredFrame = UnityEngine.Time.frameCount; LastRetiredFragments = retired; LastRetiredDistance = float.PositiveInfinity;
            _collectAfterFrame = UnityEngine.Time.frameCount + 1;
            FragmentsRetired += retired; PiecesRetired += retired; SafetyPiecesRetired += retired;
            RecordSafetyRetirement(group, reason, before, seconds * 1000.0, false);
            return true;
        }

        private void RecordSafetyRetirement(Group group, SafetyCollectionReason reason, int before, double ms, bool completed)
        {
            SafetyMaxRetireMs = Math.Max(SafetyMaxRetireMs, ms);
#if UNITY_EDITOR || ZANTETSU_ANDROID_COLLECTION_PROBE
            if (_safetyEvents.Count >= 4096) { SafetyEventsDropped++; return; }
            _safetyEvents.Add(new SafetyEvent { frame = UnityEngine.Time.frameCount, root = group.root.value,
                order = group.registrationOrder, reason = (int)reason, liveBefore = before, liveAfter = _world.Ledger.LiveCutFragmentCount,
                usedBlocks = _world.Storage.VertexBlockCapacity - _world.Storage.FreeVertexBlockRoom,
                fragmentThreshold = Settings.SafetyFragmentThreshold, blockThreshold = Settings.safetyVertexBlockThreshold,
                completed = completed, retireMs = ms });
#endif
        }
#if UNITY_EDITOR || ZANTETSU_ANDROID_COLLECTION_PROBE
        [Serializable]
        public struct SafetyEvent
        {
            public int frame, root, reason, liveBefore, liveAfter, usedBlocks, fragmentThreshold, blockThreshold;
            public long order; public bool completed; public double retireMs;
        }
        private readonly List<SafetyEvent> _safetyEvents = new List<SafetyEvent>(64);
        public IReadOnlyList<SafetyEvent> SafetyEvents => _safetyEvents;
        public int SafetyEventsDropped { get; private set; }
        public void ClearSafetyEvents() => _safetyEvents.Clear();
#endif
    }
}
