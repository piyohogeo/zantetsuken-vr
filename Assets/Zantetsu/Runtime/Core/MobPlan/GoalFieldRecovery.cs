using System;
using System.Collections.Generic;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>
    /// PLANNER-FIELD-RECOVERY (2026-11-15, under evaluation): the way back to the goal field from a place the field has no path
    /// at. The field closes a band about obstacles wider than what walking keeps, so an NPC can stand where the field says
    /// nothing; the planner then fell back to the straight line to the goal and nothing guided it out.
    /// <para>
    /// A small patch about the place (0.25 m steps, `reach` metres each way) is filled from the cells the field CAN be entered at
    /// -- walkable by the walking clearance, their field cell passable and with a path -- with the field's remaining distance
    /// there, and spread over the cells walkable by the walking clearance only (so the way never crosses a crown or a wall).
    /// A patch cell's value is then "the walk to the field + the field's remaining distance"; following the values down from a
    /// point gives the first stretch of that walk. One patch serves one NPC for one planning cycle (every candidate, the old
    /// plan and the tails read the same one).
    /// </para>
    /// </summary>
    public sealed class FieldRecovery
    {
        public const float Step = 0.25f;
        public readonly float CentreX, CentreZ;
        public readonly int Half, Side;
        private readonly float[] value;
        private readonly int[] next;
        /// <summary>Cells the field can be entered at, found in the patch (0: no way back in reach).</summary>
        public int Sources { get; private set; }
        /// <summary>Cells of the patch that are walkable and got a value.</summary>
        public int Reached { get; private set; }
        /// <summary>At the centre: the walk to the field + the remaining distance (infinite: no way back in reach).</summary>
        public float CentreValue => value[Half * Side + Half];
        /// <summary>At the centre: the length of the walk to the field alone, and where it enters the field.</summary>
        public float ConnectMeters { get; private set; } = float.PositiveInfinity;
        public float EntryX { get; private set; }
        public float EntryZ { get; private set; }
        public double BuildMilliseconds { get; private set; }
        public int Reads, Guides, EstimateReads;
        /// <summary>The estimate was made (something read it).</summary>
        public bool EstimateMade => estimate != null;
        /// <summary>
        /// After the estimate is made: the most by which a CONNECTED cell's value stands above a neighbouring cell's estimate, less the
        /// step between them (0: none). The estimate is spread from the lowest connected values and a connected cell keeps its own,
        /// so where two sides of an obstacle have very different ways back, the cells in between take the short side's value and the
        /// long side's connected cells stand that much above them: a step of the two ways' difference, not of one cell.
        /// </summary>
        public float EstimateBoundaryStepMost { get; private set; }
        /// <summary>Pairs (a cell with an estimate only, a connected neighbour) where that step is more than 1 m.</summary>
        public int EstimateBoundarySteps { get; private set; }
        /// <summary>Where the largest such step is: the cell with the estimate only, its estimate, the connected neighbour and its value.</summary>
        public float StepCellX, StepCellZ, StepEstimate, StepNeighbourX, StepNeighbourZ, StepNeighbourValue;
        // FOR A RECORD ONLY (nothing of the cost reads it): per cell with an estimate only, how far its estimate lies BELOW "the
        // NEAREST connected cell's value + the distance to it" (grid distance; of equally near cells the lowest value). 0 where the
        // estimate came from the nearest connected ground; large where the nearest connected ground is on a much longer way back
        // and the estimate took the short side's value across what lies between.
        private float[] gap;
        public float EstimateGapMost { get; private set; }
        /// <summary>Cells with an estimate only whose gap is more than 1 m.</summary>
        public int EstimateGapCells { get; private set; }

        /// <summary>The gap at a point (0: outside the patch, a connected cell, no estimate made, or none).</summary>
        public float EstimateGapAt(float x, float z)
        {
            int cell = Cell(x, z);
            return cell < 0 || gap == null ? 0f : gap[cell];
        }
        // FOR THE COST ONLY: per cell without a connected value, the least of "a connected cell's value + the grid distance to it"
        // (over every cell of the patch, walkable or not), which cell that was and how far. Kept apart from `value`: nothing here is
        // a way. Made at the first read.
        private float[] estimate;
        private int[] estimateFrom;
        private float[] estimateWalk;
        public bool Found => !float.IsPositiveInfinity(CentreValue);

        public FieldRecovery(GoalField field, float centreX, float centreZ, float reach, Func<float, float, bool> walkable)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            CentreX = centreX; CentreZ = centreZ;
            Half = Math.Max(2, (int)Math.Round(reach / Step)); Side = Half * 2 + 1;
            int n = Side * Side;
            value = new float[n];
            var walk = new float[n];      // the walk from the cell to where its way enters the field
            var entry = new int[n];       // that entering cell
            next = new int[n];            // the cell the way back goes to from this one, as the search connected them (-1: none, or the cell enters the field)
            for (int i = 0; i < n; i++) next[i] = -1;
            var open = new bool[n];
            var heapKeys = new float[n * 4 + 16]; var heapCells = new int[n * 4 + 16]; int heapCount = 0;
            void Push(float key, int cell)
            {
                if (heapCount == heapKeys.Length) { Array.Resize(ref heapKeys, heapCount * 2); Array.Resize(ref heapCells, heapCount * 2); }
                int i = heapCount++;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (heapKeys[parent] <= key) break;
                    heapKeys[i] = heapKeys[parent]; heapCells[i] = heapCells[parent]; i = parent;
                }

                heapKeys[i] = key; heapCells[i] = cell;
            }

            for (int cz = 0; cz < Side; cz++)
            {
                for (int cx = 0; cx < Side; cx++)
                {
                    int index = cz * Side + cx;
                    value[index] = float.PositiveInfinity;
                    float x = centreX + (cx - Half) * Step, z = centreZ + (cz - Half) * Step;
                    open[index] = (cx == Half && cz == Half) || walkable(x, z);   // the NPC is where it is
                    if (!open[index]) continue;
                    float remaining = field.CellPath(x, z);   // the field's own cell there: passable and with a path, or infinite
                    if (float.IsPositiveInfinity(remaining)) continue;
                    value[index] = remaining; walk[index] = 0f; entry[index] = index;
                    Push(remaining, index);
                    Sources++;
                }
            }

            const float diagonal = Step * 1.41421356f;
            while (heapCount > 0)
            {
                float key = heapKeys[0]; int cell = heapCells[0];
                heapCount--;
                if (heapCount > 0)
                {
                    float lastKey = heapKeys[heapCount]; int lastCell = heapCells[heapCount];
                    int i = 0;
                    while (true)
                    {
                        int left = 2 * i + 1;
                        if (left >= heapCount) break;
                        int right = left + 1;
                        int child = right < heapCount && heapKeys[right] < heapKeys[left] ? right : left;
                        if (heapKeys[child] >= lastKey) break;
                        heapKeys[i] = heapKeys[child]; heapCells[i] = heapCells[child]; i = child;
                    }

                    heapKeys[i] = lastKey; heapCells[i] = lastCell;
                }

                if (key > value[cell]) continue;
                int cx = cell % Side, cz = cell / Side;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cx + dx, nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx >= Side || nz >= Side) continue;
                        int to = nz * Side + nx;
                        if (!open[to]) continue;
                        if (dx != 0 && dz != 0 && (!open[cz * Side + nx] || !open[nz * Side + cx])) continue;   // no corner cutting
                        float candidate = key + (dx != 0 && dz != 0 ? diagonal : Step);
                        if (candidate < value[to]) { value[to] = candidate; walk[to] = walk[cell] + (dx != 0 && dz != 0 ? diagonal : Step); entry[to] = entry[cell]; next[to] = cell; Push(candidate, to); }
                    }
                }
            }

            for (int i = 0; i < n; i++) if (!float.IsPositiveInfinity(value[i])) Reached++;
            if (Found)
            {
                int centre = Half * Side + Half;
                ConnectMeters = walk[centre];
                EntryX = centreX + (entry[centre] % Side - Half) * Step; EntryZ = centreZ + (entry[centre] / Side - Half) * Step;
            }

            BuildMilliseconds = watch.Elapsed.TotalMilliseconds;
        }

        private int Cell(float x, float z)
        {
            int cx = (int)Math.Round((x - CentreX) / Step) + Half, cz = (int)Math.Round((z - CentreZ) / Step) + Half;
            return cx < 0 || cz < 0 || cx >= Side || cz >= Side ? -1 : cz * Side + cx;
        }

        /// <summary>
        /// The next cell of the way back from a cell: the very connection the search made (so a diagonal the search refused, past an
        /// obstacle's corner, is never taken); -1 where the cell enters the field or has no way.
        /// </summary>
        private int Down(int cell) => next[cell];

        /// <summary>
        /// What a recovery patch may walk on: the static clearance, and the prop cells kept at `keep` (the walking cost's radius +
        /// keep) everywhere -- except that an NPC standing nearer the prop cells than that already (overlapping) may get out through
        /// cells no nearer than it stands now less 0.1 m, and only within `escapeMeters` of where it stands. Elsewhere in the patch,
        /// and for an NPC that keeps the distance, nothing is relaxed: no other piece's inside becomes walkable.
        /// </summary>
        public static Func<float, float, bool> WalkRule(WalkableMap map, float clearance, PropObstacleMap props, float keep, float startX, float startZ, float escapeMeters, out bool overlapping)
        {
            float atStart = props != null ? props.DistanceAt(startX, startZ) : float.PositiveInfinity;
            overlapping = atStart < keep;
            if (props == null) return (x, z) => map.ClearanceAt(x, z) >= clearance;
            if (!overlapping) return (x, z) => map.ClearanceAt(x, z) >= clearance && props.DistanceAt(x, z) >= keep;
            float relaxed = Math.Max(0f, atStart - 0.1f), escape2 = escapeMeters * escapeMeters;
            return (x, z) =>
            {
                if (map.ClearanceAt(x, z) < clearance) return false;
                float d = props.DistanceAt(x, z);
                if (d >= keep) return true;
                return d >= relaxed && (x - startX) * (x - startX) + (z - startZ) * (z - startZ) <= escape2;
            };
        }

        /// <summary>The walk to the field + the field's remaining distance from a point (false: the point is outside the patch or has no way back).</summary>
        public bool TryDistance(float x, float z, out float distance)
        {
            Reads++;
            int cell = Cell(x, z);
            distance = cell >= 0 ? value[cell] : float.PositiveInfinity;
            return !float.IsPositiveInfinity(distance);
        }

        /// <summary>
        /// FOR THE COST ONLY: for a point of the patch that has no connected value, the least of "a connected cell's value + the distance
        /// to it" (grid distance over all cells; between two cells that both have an estimate only it changes by no more than the step
        /// between them, but a connected cell keeps its own value, so beside a connected cell of a much longer way back a step of
        /// the two ways' difference stays -- EstimateBoundaryStepMost), with the cell it came from and how far that is. False: outside the patch, or the patch has
        /// no connected value at all. A point WITH a connected value answers that value (distance 0).
        /// </summary>
        public bool TryEstimate(float x, float z, out float distance, out float fromX, out float fromZ, out float away)
        {
            EstimateReads++;
            distance = float.PositiveInfinity; fromX = x; fromZ = z; away = 0f;
            int cell = Cell(x, z);
            if (cell < 0 || Sources == 0) return false;
            if (estimate == null) MakeEstimate();
            if (float.IsPositiveInfinity(estimate[cell])) return false;
            distance = estimate[cell];
            int from = estimateFrom[cell];
            fromX = CentreX + (from % Side - Half) * Step; fromZ = CentreZ + (from / Side - Half) * Step;
            away = estimateWalk[cell];
            return true;
        }

        private void MakeEstimate()
        {
            int n = Side * Side;
            estimate = new float[n]; estimateFrom = new int[n]; estimateWalk = new float[n];
            var keys = new float[n * 4 + 16]; var cells = new int[n * 4 + 16]; int count = 0;
            void Push(float key, int cell)
            {
                if (count == keys.Length) { Array.Resize(ref keys, count * 2); Array.Resize(ref cells, count * 2); }
                int i = count++;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (keys[parent] <= key) break;
                    keys[i] = keys[parent]; cells[i] = cells[parent]; i = parent;
                }

                keys[i] = key; cells[i] = cell;
            }

            for (int i = 0; i < n; i++)
            {
                estimate[i] = value[i]; estimateFrom[i] = i; estimateWalk[i] = 0f;
                if (!float.IsPositiveInfinity(value[i])) Push(value[i], i);
            }

            const float diagonal = Step * 1.41421356f;
            while (count > 0)
            {
                float key = keys[0]; int cell = cells[0];
                count--;
                if (count > 0)
                {
                    float lastKey = keys[count]; int lastCell = cells[count];
                    int i = 0;
                    while (true)
                    {
                        int left = 2 * i + 1;
                        if (left >= count) break;
                        int right = left + 1;
                        int child = right < count && keys[right] < keys[left] ? right : left;
                        if (keys[child] >= lastKey) break;
                        keys[i] = keys[child]; cells[i] = cells[child]; i = child;
                    }

                    keys[i] = lastKey; cells[i] = lastCell;
                }

                if (key > estimate[cell]) continue;
                int cx = cell % Side, cz = cell / Side;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cx + dx, nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx >= Side || nz >= Side) continue;
                        int to = nz * Side + nx;
                        if (!float.IsPositiveInfinity(value[to])) continue;   // a connected cell keeps its own value
                        float step = dx != 0 && dz != 0 ? diagonal : Step;
                        float candidate = key + step;
                        if (candidate < estimate[to]) { estimate[to] = candidate; estimateFrom[to] = estimateFrom[cell]; estimateWalk[to] = estimateWalk[cell] + step; Push(candidate, to); }
                    }
                }
            }

            for (int cell = 0; cell < n; cell++)
            {
                if (!float.IsPositiveInfinity(value[cell]) || float.IsPositiveInfinity(estimate[cell])) continue;   // cells with an estimate only
                int cx = cell % Side, cz = cell / Side;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cx + dx, nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx >= Side || nz >= Side) continue;
                        int to = nz * Side + nx;
                        if (float.IsPositiveInfinity(value[to])) continue;
                        float above = value[to] - estimate[cell] - (dx != 0 && dz != 0 ? diagonal : Step);
                        if (above > EstimateBoundaryStepMost)
                        {
                            EstimateBoundaryStepMost = above;
                            StepCellX = CentreX + (cx - Half) * Step; StepCellZ = CentreZ + (cz - Half) * Step; StepEstimate = estimate[cell];
                            StepNeighbourX = CentreX + (nx - Half) * Step; StepNeighbourZ = CentreZ + (nz - Half) * Step; StepNeighbourValue = value[to];
                        }

                        if (above > 1f) EstimateBoundarySteps++;
                    }
                }
            }

            // the record's gap: against the nearest connected cell (plain search over the connected cells; a patch is 33 x 33)
            gap = new float[n];
            var connected = new List<int>();
            for (int i = 0; i < n; i++) if (!float.IsPositiveInfinity(value[i])) connected.Add(i);
            for (int cell = 0; cell < n; cell++)
            {
                if (!float.IsPositiveInfinity(value[cell]) || float.IsPositiveInfinity(estimate[cell])) continue;
                int cx = cell % Side, cz = cell / Side;
                float nearest = float.PositiveInfinity, nearestValue = float.PositiveInfinity;
                foreach (int other in connected)
                {
                    int ax = Math.Abs(other % Side - cx), az = Math.Abs(other / Side - cz);
                    float d = Step * (Math.Max(ax, az) + 0.41421356f * Math.Min(ax, az));
                    if (d < nearest - 1e-4f) { nearest = d; nearestValue = value[other]; }
                    else if (d <= nearest + 1e-4f && value[other] < nearestValue) nearestValue = value[other];
                }

                float g = nearestValue + nearest - estimate[cell];
                if (g <= 0f) continue;
                gap[cell] = g;
                if (g > EstimateGapMost) EstimateGapMost = g;
                if (g > 1f) EstimateGapCells++;
            }
        }

        /// <summary>A point about `lookAhead` metres along the way back from a point (false: none).</summary>
        public bool TryGuide(float x, float z, float lookAhead, out float gx, out float gz)
        {
            Guides++;
            gx = x; gz = z;
            int cell = Cell(x, z);
            if (cell < 0 || float.IsPositiveInfinity(value[cell])) return false;
            float walked = 0f;
            for (int guard = 0; guard < Side * Side && walked < lookAhead; guard++)
            {
                int next = Down(cell);
                if (next < 0) break;
                walked += (next % Side != cell % Side && next / Side != cell / Side) ? Step * 1.41421356f : Step;
                cell = next;
            }

            if (walked <= 0f) return false;
            gx = CentreX + (cell % Side - Half) * Step; gz = CentreZ + (cell / Side - Half) * Step;
            return true;
        }

        /// <summary>The way back from the centre as points (x, z pairs), for a picture.</summary>
        public List<float> CentrePath()
        {
            var points = new List<float>();
            if (!Found) return points;
            int cell = Half * Side + Half;
            for (int guard = 0; guard < Side * Side; guard++)
            {
                points.Add(CentreX + (cell % Side - Half) * Step); points.Add(CentreZ + (cell / Side - Half) * Step);
                int next = Down(cell);
                if (next < 0) break;
                cell = next;
            }

            return points;
        }
    }

    /// <summary>One NPC's recovery patch of one planning cycle: made when first needed, then read by every evaluation of that NPC in the cycle.</summary>
    public sealed class FieldRecoveryHolder
    {
        public FieldRecovery Patch;
        public bool Tried;
        /// <summary>The NPC stood nearer the prop cells than walking keeps when the patch was made (the local escape rule applied).</summary>
        public bool Overlapping;
    }
}
