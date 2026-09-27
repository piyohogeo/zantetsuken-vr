using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Zantetsu.Core.MobPlan
{
    [Serializable]
    public sealed class SelectionStats
    {
        public int PairChecks;          // candidate pairs fully swept
        public int PairPrefiltered;     // candidate pairs skipped by AABB
        public int SweepChanges;
        public int PairRepairs;
        public int Sweeps;
        public bool BaselineFeasible;
        public bool FinalFeasible;
        public bool Timeout;
        public int BlockedAgents;       // agents whose best candidate could not be assigned
        public double CompatibilityMilliseconds;
        public double AssignmentMilliseconds;
        public float MinSeparation = float.PositiveInfinity;
        public int MinSeparationAgentA = -1;
        public int MinSeparationAgentB = -1;
        /// <summary>Indices (into the agents list) whose chosen candidate is incompatible with some other chosen candidate.</summary>
        public List<int> ConflictingAgents = new List<int>();
    }

    public sealed class SelectionAgent
    {
        public int AgentId;
        public float Radius;
        public List<PlanCandidate> Candidates = new List<PlanCandidate>();
        public float Urgency;           // higher first
        public int Chosen;
    }

    /// <summary>
    /// Candidate compatibility matrix + anytime assignment (LOCOMOTION_SEARCH_PLAN.md Section 13): baseline (all old
    /// suffixes), greedy sweeps in urgency order, pair repair. Never a full product search.
    /// </summary>
    public static class MultiAgentSelector
    {
        private sealed class PairTable
        {
            public bool[] Compatible;   // [a * kb + b]
            public float[] MinSeparation;
            public int Kb;
            public bool Get(int a, int b) => Compatible[a * Kb + b];
        }

        public static SelectionStats Select(List<SelectionAgent> agents, PlannerConfig config, Deadline deadline)
        {
            var stats = new SelectionStats();
            var watch = Stopwatch.StartNew();
            var n = agents.Count;
            var tables = new PairTable[n * n];
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    var a = agents[i];
                    var b = agents[j];
                    var table = new PairTable { Kb = b.Candidates.Count, Compatible = new bool[a.Candidates.Count * b.Candidates.Count], MinSeparation = new float[a.Candidates.Count * b.Candidates.Count] };
                    var threshold = a.Radius + b.Radius + config.agentMarginMeters;
                    // The two old suffixes were published together last cycle; re-sampling from a new `now` shifts the chords by
                    // up to one sample, so the baseline pair keeps the margin as hysteresis (>= 2r, the validator's threshold).
                    var baselineThreshold = a.Radius + b.Radius;
                    for (var ca = 0; ca < a.Candidates.Count; ca++)
                    {
                        var ta = a.Candidates[ca].Trajectory;
                        for (var cb = 0; cb < b.Candidates.Count; cb++)
                        {
                            var tb = b.Candidates[cb].Trajectory;
                            var index = ca * table.Kb + cb;
                            var required = ca == 0 && cb == 0 ? baselineThreshold : threshold;
                            if (!ta.BoundsOverlap(tb, threshold))
                            {
                                table.Compatible[index] = true;
                                table.MinSeparation[index] = float.PositiveInfinity;
                                stats.PairPrefiltered++;
                                continue;
                            }

                            var separation = CircleSweep.MinSeparation(ta, tb, threshold, out _);
                            table.Compatible[index] = separation >= required;
                            table.MinSeparation[index] = separation;
                            stats.PairChecks++;
                        }
                    }

                    tables[i * n + j] = table;
                }
            }

            stats.CompatibilityMilliseconds = watch.Elapsed.TotalMilliseconds;
            watch.Restart();

            // Baseline: everybody keeps candidate 0.
            for (var i = 0; i < n; i++) agents[i].Chosen = 0;
            // An infeasible baseline (old suffixes conflicting, e.g. after idle drift) is repaired by the same sweeps:
            // an agent whose current choice conflicts accepts any compatible candidate regardless of cost.
            stats.BaselineFeasible = AllCompatible(agents, tables, n);

            var order = new List<int>();
            for (var i = 0; i < n; i++) order.Add(i);
            order.Sort((x, y) => agents[y].Urgency != agents[x].Urgency ? agents[y].Urgency.CompareTo(agents[x].Urgency) : agents[x].AgentId.CompareTo(agents[y].AgentId));

            for (var sweep = 0; sweep < config.assignmentSweeps; sweep++)
            {
                var changed = 0;
                for (var k = 0; k < n; k++)
                {
                    if (deadline != null && deadline.Expired) { stats.Timeout = true; break; }
                    var i = order[(k + sweep) % n];
                    var agent = agents[i];
                    var best = agent.Chosen;
                    var bestCost = CompatibleWithOthers(agents, tables, n, i, agent.Chosen) ? agent.Candidates[best].TotalCost : float.PositiveInfinity;
                    for (var c = 0; c < agent.Candidates.Count; c++)
                    {
                        if (c == agent.Chosen) continue;
                        var cost = agent.Candidates[c].TotalCost;
                        if (cost >= bestCost - 1e-4f) continue;
                        if (CompatibleWithOthers(agents, tables, n, i, c)) { best = c; bestCost = cost; }
                    }

                    if (best != agent.Chosen) { agent.Chosen = best; changed++; }
                }

                stats.Sweeps++;
                stats.SweepChanges += changed;
                if (changed == 0 || stats.Timeout) break;
            }

            // Pair repair for agents whose best candidate is still blocked.
            if (config.pairRepairEnabled && !stats.Timeout)
            {
                for (var i = 0; i < n; i++)
                {
                    var agent = agents[i];
                    var bestIndex = BestIndex(agent);
                    if (bestIndex == agent.Chosen) continue;
                    if (deadline != null && deadline.Expired) { stats.Timeout = true; break; }
                    var improved = false;
                    for (var j = 0; j < n && !improved; j++)
                    {
                        if (j == i) continue;
                        var other = agents[j];
                        var currentSum = agent.Candidates[agent.Chosen].TotalCost + other.Candidates[other.Chosen].TotalCost;
                        var bestSum = currentSum - 1e-4f;
                        var bestA = -1; var bestB = -1;
                        for (var a = 0; a < agent.Candidates.Count; a++)
                        {
                            for (var b = 0; b < other.Candidates.Count; b++)
                            {
                                var sum = agent.Candidates[a].TotalCost + other.Candidates[b].TotalCost;
                                if (sum >= bestSum) continue;
                                if (!PairCompatible(tables, n, i, a, j, b)) continue;
                                if (!CompatibleWithOthersExcept(agents, tables, n, i, a, j) || !CompatibleWithOthersExcept(agents, tables, n, j, b, i)) continue;
                                bestSum = sum; bestA = a; bestB = b;
                            }
                        }

                        if (bestA >= 0)
                        {
                            agent.Chosen = bestA;
                            other.Chosen = bestB;
                            stats.PairRepairs++;
                            improved = true;
                        }
                    }
                }
            }

            for (var i = 0; i < n; i++) if (BestIndex(agents[i]) != agents[i].Chosen) stats.BlockedAgents++;
            stats.FinalFeasible = AllCompatible(agents, tables, n);
            stats.ConflictingAgents.Clear();
            for (var i = 0; i < n; i++) if (!CompatibleWithOthers(agents, tables, n, i, agents[i].Chosen)) stats.ConflictingAgents.Add(i);

            // Final consistency check + min separation of the chosen assignment.
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    var table = tables[i * n + j];
                    var index = agents[i].Chosen * table.Kb + agents[j].Chosen;
                    var separation = table.MinSeparation[index];
                    if (separation < stats.MinSeparation) { stats.MinSeparation = separation; stats.MinSeparationAgentA = agents[i].AgentId; stats.MinSeparationAgentB = agents[j].AgentId; }
                }
            }

            stats.AssignmentMilliseconds = watch.Elapsed.TotalMilliseconds;
            return stats;
        }

        public static int BestIndex(SelectionAgent agent)
        {
            var best = 0;
            for (var c = 1; c < agent.Candidates.Count; c++) if (agent.Candidates[c].TotalCost < agent.Candidates[best].TotalCost - 1e-4f) best = c;
            return best;
        }

        private static bool PairCompatible(PairTable[] tables, int n, int i, int a, int j, int b)
        {
            return i < j ? tables[i * n + j].Get(a, b) : tables[j * n + i].Get(b, a);
        }

        private static bool AllCompatible(List<SelectionAgent> agents, PairTable[] tables, int n)
        {
            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++)
                    if (!tables[i * n + j].Get(agents[i].Chosen, agents[j].Chosen)) return false;
            return true;
        }

        private static bool CompatibleWithOthers(List<SelectionAgent> agents, PairTable[] tables, int n, int i, int candidate)
        {
            for (var j = 0; j < n; j++)
            {
                if (j == i) continue;
                if (!PairCompatible(tables, n, i, candidate, j, agents[j].Chosen)) return false;
            }

            return true;
        }

        private static bool CompatibleWithOthersExcept(List<SelectionAgent> agents, PairTable[] tables, int n, int i, int candidate, int except)
        {
            for (var j = 0; j < n; j++)
            {
                if (j == i || j == except) continue;
                if (!PairCompatible(tables, n, i, candidate, j, agents[j].Chosen)) return false;
            }

            return true;
        }
    }
}
