using System;
using System.Collections.Generic;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The always-kinematic building's cut limit (TL, 2026-10-01; off unless N is given). n is a building's display
    /// geometries now: its live, non-empty, committed display fragments (not caps or submeshes, not its hull, history or
    /// member count). A cut goes on only from a settled n with 1 &lt;= n &lt; N; a building whose n reaches N -- from the
    /// cut that took it there, or found so before a cut -- is cut no more, for good (a hull exchange does not undo it): it
    /// leaves the detector's targets, a hit on it is refused, and its held requests end with the reason; the last cut
    /// published runs to its end (its display commit, its drop, its hull update). A cut accepted is applied to every
    /// geometry it crosses, nothing cut short. While a building's last display operations are still open, its next cut
    /// waits (held), so that n is counted settled; other buildings are not held up. The drop D(n) = D0 (1 - log2 n /
    /// log2 N)^p is computed once from the n the cut goes on from.
    /// </summary>
    public sealed partial class BuildingHullFusion
    {
        private bool LimitOn => _settings.LimitOn;

        private readonly HashSet<int> _cutStopped = new HashSet<int>();
        private readonly Dictionary<CutOperationId, int> _displayOpBuilding = new Dictionary<CutOperationId, int>();
        private readonly Dictionary<int, int> _displayOpenOf = new Dictionary<int, int>();
        private readonly Dictionary<int, HullHit> _limitAwaiting = new Dictionary<int, HullHit>();
        private readonly List<string> _limitRecords = new List<string>();
        private readonly Dictionary<int, string> _stopRecords = new Dictionary<int, string>();
        private readonly Dictionary<int, double> _stopAt = new Dictionary<int, double>();

        /// <summary>When the building was stopped (the trial's real seconds, as the hits' askedAt), if it was.</summary>
        public bool TryGetStopTime(int building, out double realSeconds) => _stopAt.TryGetValue(building, out realSeconds);

        /// <summary>The cut limit's records: each cut's n before, n after once settled, N, p, D0 and the distance taken; each stop.</summary>
        public IReadOnlyList<string> LimitRecords => _limitRecords;

        /// <summary>Each stopped building's record: the hit that took it to N (or the count that found it there), the requests ended.</summary>
        public IReadOnlyDictionary<int, string> StopRecords => _stopRecords;

        /// <summary>Hits refused by the cut limit (a stopped building, n = 0, or n &gt;= N before the cut).</summary>
        public int HitsRefusedByLimit { get; private set; }

        /// <summary>Held requests ended by a building's stop.</summary>
        public int HeldEndedByLimit { get; private set; }

        /// <summary>Whether the building is cut no more (the cut limit reached).</summary>
        public bool IsCutStopped(int building) => _cutStopped.Contains(building);

        public int StoppedBuildings => _cutStopped.Count;

        /// <summary>The drop D(n) = D0 (1 - log2 n / log2 N)^p, for 1 &lt;= n &lt; N.</summary>
        internal static double LimitDistance(int n, int limit, double exponent, double baseMetres)
        {
            if (n < 1 || n >= limit) throw new ArgumentOutOfRangeException(nameof(n), n, "1 <= n < N");
            return baseMetres * Math.Pow(1.0 - Math.Log(n) / Math.Log(limit), exponent);
        }

        /// <summary>A building's display geometries now: its live, non-empty, committed display fragments.</summary>
        public int CountDisplayGeometries(int building)
        {
            int n = 0;
            foreach (HullGroup g in _groups)
            {
                if (g.Building != building || g.State == HullGroupState.Gone) continue;
                foreach (HullGroup.DisplayMember m in g.Members)
                {
                    if (!_ledger.IsCurrentTarget(m.fragment) || _dag.HasNoGeometry(m.fragment)) continue;
                    if (_dag.TryGetGeometryFrame(m.fragment, out Matrix4x4 _)) n++;
                }
            }

            return n;
        }

        /// <summary>Whether a building's last display operations are still open (its count not settled).</summary>
        private bool DisplayOpen(int building) => _displayOpenOf.TryGetValue(building, out int open) && open > 0;

        private void NoteDisplayOperation(int building, CutOperationId operation)
        {
            _displayOpBuilding[operation] = building;
            _displayOpenOf.TryGetValue(building, out int open);
            _displayOpenOf[building] = open + 1;
        }

        private void DisplayOperationEnded(CutOperationId operation)
        {
            if (!_displayOpBuilding.TryGetValue(operation, out int building)) return;
            _displayOpBuilding.Remove(operation);
            if (_displayOpenOf.TryGetValue(building, out int open)) _displayOpenOf[building] = Math.Max(0, open - 1);
        }

        /// <summary>
        /// The limit at a cut's start (its acceptance, or a held request's resumption): a stopped building refuses; n is
        /// counted settled; n = 0 goes no further (no logarithm); n &gt;= N stops the building and refuses; otherwise the
        /// drop is computed once from n and kept on the hit.
        /// </summary>
        private bool PassesLimit(HullGroup group, HullHit hit, out string refusal)
        {
            refusal = null;
            if (!LimitOn) return true;
            int limit = _settings.geometryLimit;
            if (IsCutStopped(group.Building))
            {
                refusal = "NotAccepted: the building's cut limit was reached (N = " + limit + ")";
                return false;
            }

            int n = CountDisplayGeometries(group.Building);
            hit.geometriesBefore = n;
            if (n <= 0)
            {
                refusal = "NotAccepted: the building has no live, non-empty, committed display geometry (n = 0)";
                return false;
            }

            if (n >= limit)
            {
                Stop(group.Building, hit, n, "n = " + n + " >= N = " + limit + " before hit " + hit.id);
                refusal = "NotAccepted: the building's cut limit was reached (n = " + n + " >= N = " + limit + ")";
                return false;
            }

            hit.limitDistance = LimitDistance(n, limit, _settings.dropExponent, _settings.dropBaseMetres);
            return true;
        }

        private void RefuseByLimit(HullHit hit, string refusal)
        {
            HitsRefusedByLimit++;
            Finish(hit, refusal, false);
            _limitRecords.Add("hit " + hit.id + " (slash " + hit.slashId + ", group " + hit.group + ") refused: " + refusal + (hit.geometriesBefore >= 0 ? " [n before " + hit.geometriesBefore + "]" : ""));
            Record("hit " + hit.id + " refused by the cut limit: " + refusal);
        }

        /// <summary>A building cut no more, for good; its held requests end with the reason.</summary>
        private void Stop(int building, HullHit by, int n, string why)
        {
            if (!_cutStopped.Add(building)) return;
            _stopAt[building] = _realSeconds();
            var ended = new List<int>();
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                HeldHit h = _held[i];
                if (h.group.Building != building) continue;
                _held.RemoveAt(i);
                h.hit.geometriesBefore = n;
                HitsRefusedByLimit++;
                HeldEndedByLimit++;
                Finish(h.hit, "NotAccepted: the building's cut limit was reached while it was held (n = " + n + " >= N = " + _settings.geometryLimit + ")", false);
                ended.Add(h.hit.id);
            }

            string record = "building " + building + " stopped: " + why + " (N " + _settings.geometryLimit + ", p " + _settings.dropExponent.ToString("R") + ", D0 " + _settings.dropBaseMetres.ToString("R")
                + "); held requests ended [" + string.Join(" ", ended) + "]";
            _stopRecords[building] = "by hit " + (by != null ? by.id.ToString() : "-") + ": " + record;
            _limitRecords.Add(record);
            Record(record);
        }

        /// <summary>After the display operations settle: a building whose last cut's operations all ended has its n counted; at N or more it stops.</summary>
        private void SettleLimits()
        {
            if (!LimitOn || _limitAwaiting.Count == 0) return;
            List<int> ready = null;
            foreach (KeyValuePair<int, HullHit> w in _limitAwaiting) if (!DisplayOpen(w.Key)) (ready ??= new List<int>()).Add(w.Key);
            if (ready == null) return;
            foreach (int building in ready)
            {
                HullHit hit = _limitAwaiting[building];
                _limitAwaiting.Remove(building);
                int n = CountDisplayGeometries(building);
                hit.geometriesAfter = n;
                _limitRecords.Add("hit " + hit.id + " (group " + hit.group + "): n before " + hit.geometriesBefore + ", after " + n + " (settled), N " + _settings.geometryLimit + ", p " + _settings.dropExponent.ToString("R")
                    + ", D0 " + _settings.dropBaseMetres.ToString("R") + ", distance " + hit.limitDistance.ToString("R"));
                if (n >= _settings.geometryLimit) Stop(building, hit, n, "n = " + n + " >= N after hit " + hit.id);
            }
        }
    }
}
