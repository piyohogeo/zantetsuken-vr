using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Zantetsu.MeshCut.Tests.ProbeBaseline
{
    /// <summary>
    /// The Burst-compiled cut. One job, run single threaded on the calling thread (plan 7.5: no
    /// scheduler in the first algorithm comparison).
    ///
    /// Every stage boundary and every arithmetic decision matches the C# core (scan.edgehash.diagA.split)
    /// and the C++ reference, including the canonical low-to-high edge direction for interpolation, the
    /// 64-bit key mixing that keeps the edge map from degenerating, the phase 3.5 rules (on-plane
    /// vertices and edges, through-vertex crossings, faces lying in the plane, a mesh lying entirely in
    /// the plane) and the split cap (<see cref="BurstCapping"/>).
    ///
    /// Stages cannot be timed from inside: Burst does not compile Stopwatch. So the job takes a phase
    /// selector instead. Running it with <see cref="PhaseAll"/> does the whole cut in one call, which is
    /// what the end-to-end pass measures; running it once per phase lets the managed harness time each
    /// stage from outside. Both paths execute the same code, so the breakdown describes the same work
    /// the end-to-end number covers -- the difference between them is job dispatch, which is measured
    /// and reported rather than assumed away.
    ///
    /// Scalars that must survive between separate calls live in <see cref="State"/>, because a job
    /// struct is copied by value and its fields do not persist across calls.
    /// </summary>
    // CompileSynchronously matters here. Burst compiles asynchronously in the Editor by default, and a
    // job that runs before its compilation finishes silently executes as managed IL instead. Measuring
    // that and reporting it as Burst would be the same class of mistake as measuring tier-0 JIT.
    [BurstCompile(FloatMode = FloatMode.Default, FloatPrecision = FloatPrecision.Standard,
                  OptimizeFor = OptimizeFor.Performance, CompileSynchronously = true)]
    public struct CutJob : IJob
    {
        public const int PhaseAll = -1;
        public const int PhasePrepare = 0;
        public const int PhaseClassify = 1;
        public const int PhaseCandidate = 2;
        public const int PhaseIntersect = 3;
        public const int PhaseEmit = 4;
        public const int PhaseContour = 5;
        public const int PhaseCap = 6;
        public const int PhaseMaterialize = 7;
        public const int PhaseCount = 8;

        public const int StateNodeCount = 0;
        public const int StateCrossingCount = 1;
        public const int StatePositiveVertices = 2;
        public const int StateNegativeVertices = 3;
        public const int StateOnPlaneVertices = 4;
        /// <summary>0 running, 1 unsupported, 2 whole mesh on one side (nothing else to do).</summary>
        public const int StateStatus = 5;
        public const int StateBoundaryEdges = 6;
        /// <summary>Mask of the edge->node map region this cut uses (see Intersect).</summary>
        public const int StateMapMask = 7;
        public const int StateSize = 8;

        // Unsupported reasons, so the managed side can name them.
        public const int FailNone = 0;
        public const int FailCoplanarZeroArea = 1;
        public const int FailCapStalled = 2;
        public const int FailCapDisk = 3;

        [ReadOnly] public NativeArray<float> Px, Py, Pz;
        [ReadOnly] public NativeArray<int> Indices;
        [ReadOnly] public NativeArray<float> Attributes;
        public int AttributeFloats;
        public int VertexCount, TriangleCount;

        public float Nx, Ny, Nz, Offset;
        public double OnPlaneEpsilon;
        public bool WithCap;
        /// <summary>Cap z-order hashing threshold; the harness can vary it to attribute cap time.</summary>
        public int CapHashThreshold;
        public int Phase;

        public NativeArray<int> State;

        public NativeArray<float> Dist;
        public NativeArray<sbyte> Side;
        public NativeArray<sbyte> TriClass;
        public NativeArray<byte> LoneCorner;
        public NativeArray<int> NodeP, NodeQ;

        public NativeArray<long> MapKeys;
        public NativeArray<int> MapValues;
        public NativeArray<long> NodeKeys;
        public NativeArray<double> NodeParam;
        public NativeArray<int> NodeSourceVertex;
        public NativeArray<float> NodeX, NodeY, NodeZ, NodeAttr;

        // Phase 3.5: side masks of edges lying in the plane and the boundary ones among them.
        public NativeHashMap<long, int> OnPlaneEdgeSides;
        public NativeList<long> OnPlaneEdgeOrder;
        public NativeList<long> PlaneBoundaryEdges;

        public NativeArray<int> PosOriginalMap, PosNodeMap;
        public NativeList<int> PosSourceVertex, PosSourceNode, PosIndices, PosSegFrom, PosSegTo;
        public NativeList<int> PosContourNodes, PosContourStart;
        public NativeList<byte> PosContourClosed;
        public NativeList<F3> PosAux;
        public NativeList<float> PosPx, PosPy, PosPz, PosAttr;

        public NativeArray<int> NegOriginalMap, NegNodeMap;
        public NativeList<int> NegSourceVertex, NegSourceNode, NegIndices, NegSegFrom, NegSegTo;
        public NativeList<int> NegContourNodes, NegContourStart;
        public NativeList<byte> NegContourClosed;
        public NativeList<F3> NegAux;
        public NativeList<float> NegPx, NegPy, NegPz, NegAttr;

        public NativeArray<int> Next, Prev;
        public NativeArray<byte> Used;

        public NativeArray<BurstScanEdgeHashCut.Counters> Result;

        public void Execute()
        {
            if (Phase != PhaseAll)
            {
                RunPhase(Phase);
                return;
            }
            for (int p = 0; p < PhaseCount; p++)
            {
                RunPhase(p);
                if (State[StateStatus] != 0) return;
            }
        }

        private void RunPhase(int phase)
        {
            switch (phase)
            {
                case PhasePrepare: Prepare(); break;
                case PhaseClassify: Classify(); break;
                case PhaseCandidate: Candidate(); break;
                case PhaseIntersect: Intersect(); break;
                case PhaseEmit: Emit(); break;
                case PhaseContour: Contour(); break;
                case PhaseCap: Cap(); break;
                case PhaseMaterialize: Materialize(); break;
            }
        }

        // ------------------------------------------------------------------ stages

        private void Prepare()
        {
            for (int i = 0; i < StateSize; i++) State[i] = 0;

            PosSourceVertex.Clear(); PosSourceNode.Clear(); PosIndices.Clear(); PosSegFrom.Clear(); PosSegTo.Clear();
            PosContourNodes.Clear(); PosContourStart.Clear(); PosContourClosed.Clear(); PosAux.Clear();
            NegSourceVertex.Clear(); NegSourceNode.Clear(); NegIndices.Clear(); NegSegFrom.Clear(); NegSegTo.Clear();
            NegContourNodes.Clear(); NegContourStart.Clear(); NegContourClosed.Clear(); NegAux.Clear();
            for (int i = 0; i < VertexCount; i++) { PosOriginalMap[i] = -1; NegOriginalMap[i] = -1; }
            PlaneBoundaryEdges.Clear();
            OnPlaneEdgeOrder.Clear();

            Result[0] = new BurstScanEdgeHashCut.Counters();
        }

        private void Classify()
        {
            int positive = 0, negative = 0, onPlane = 0;
            double eps = OnPlaneEpsilon;
            for (int v = 0; v < VertexCount; v++)
            {
                float d = Nx * Px[v] + Ny * Py[v] + Nz * Pz[v] + Offset;
                Dist[v] = d;
                double ad = d < 0f ? -(double)d : d;
                if (ad <= eps) { Side[v] = 0; onPlane++; }
                else if (d > 0f) { Side[v] = 1; positive++; }
                else { Side[v] = -1; negative++; }
            }
            State[StatePositiveVertices] = positive;
            State[StateNegativeVertices] = negative;
            State[StateOnPlaneVertices] = onPlane;
        }

        private void Candidate()
        {
            int crossingCount = 0;
            int coplanarTriangle = -1;
            bool threeState = State[StateOnPlaneVertices] > 0;
            if (threeState) OnPlaneEdgeSides.Clear();

            for (int t = 0; t < TriangleCount; t++)
            {
                int i = t * 3;
                int a = Indices[i], b = Indices[i + 1], c = Indices[i + 2];
                sbyte sa = Side[a], sb = Side[b], sc = Side[c];
                if (!threeState)
                {
                    int sum = sa + sb + sc;
                    if (sum == 3) TriClass[t] = 1;
                    else if (sum == -3) TriClass[t] = -1;
                    else
                    {
                        TriClass[t] = 0;
                        crossingCount++;
                        LoneCorner[t] = (sa != sb && sa != sc) ? (byte)0 : ((sb != sa && sb != sc) ? (byte)1 : (byte)2);
                    }
                    continue;
                }

                int positive = (sa > 0 ? 1 : 0) + (sb > 0 ? 1 : 0) + (sc > 0 ? 1 : 0);
                int negative = (sa < 0 ? 1 : 0) + (sb < 0 ? 1 : 0) + (sc < 0 ? 1 : 0);
                if (positive > 0 && negative > 0)
                {
                    crossingCount++;
                    if (positive + negative == 3)
                    {
                        TriClass[t] = 0;
                        LoneCorner[t] = (sa != sb && sa != sc) ? (byte)0 : ((sb != sa && sb != sc) ? (byte)1 : (byte)2);
                    }
                    else
                    {
                        TriClass[t] = 2;
                        LoneCorner[t] = sa == 0 ? (byte)0 : sb == 0 ? (byte)1 : (byte)2;
                    }
                    continue;
                }
                if (positive > 0) TriClass[t] = 1;
                else if (negative > 0) TriClass[t] = -1;
                else
                {
                    int s = CoplanarSide(a, b, c);
                    if (s == 0) { coplanarTriangle = t; break; }
                    TriClass[t] = (sbyte)s;
                }
                int mask = TriClass[t] > 0 ? 1 : 2;
                if (sa == 0 && sb == 0) AddOnPlaneEdge(a, b, mask);
                if (sb == 0 && sc == 0) AddOnPlaneEdge(b, c, mask);
                if (sc == 0 && sa == 0) AddOnPlaneEdge(c, a, mask);
            }

            var counters = Result[0];
            if (coplanarTriangle >= 0)
            {
                State[StateStatus] = 1;
                counters.Failure = FailCoplanarZeroArea;
                counters.FailureTriangle = coplanarTriangle;
                Result[0] = counters;
                return;
            }
            State[StateCrossingCount] = crossingCount;
            counters.CandidateTriangles = TriangleCount;
            counters.CrossingTriangles = crossingCount;

            if (threeState)
                for (int k = 0; k < OnPlaneEdgeOrder.Length; k++)
                    if (OnPlaneEdgeSides[OnPlaneEdgeOrder[k]] == 3) PlaneBoundaryEdges.Add(OnPlaneEdgeOrder[k]);
            State[StateBoundaryEdges] = PlaneBoundaryEdges.Length;

            int positiveVertices = State[StatePositiveVertices], negativeVertices = State[StateNegativeVertices];
            // A mesh lying entirely in the plane is not cut; returned whole on the positive side.
            if (positiveVertices == 0 && negativeVertices == 0)
            {
                WholeMesh(ref counters, 1, 1);
                Result[0] = counters;
                return;
            }
            // Nothing crosses, no edge lies in the plane, every vertex on one side.
            if (crossingCount == 0 && PlaneBoundaryEdges.Length == 0 && (negativeVertices == 0 || positiveVertices == 0))
            {
                WholeMesh(ref counters, negativeVertices == 0 ? 1 : -1, 0);
                Result[0] = counters;
                return;
            }
            Result[0] = counters;
        }

        private void WholeMesh(ref BurstScanEdgeHashCut.Counters counters, int side, int allOnPlane)
        {
            State[StateStatus] = 2;
            counters.PlaneMissSide = (sbyte)side;
            counters.AllOnPlane = allOnPlane;
            counters.OutputTriangles = TriangleCount;
            counters.OutputVertices = VertexCount;
        }

        private int CoplanarSide(int a, int b, int c)
        {
            float e0x = Px[b] - Px[a], e0y = Py[b] - Py[a], e0z = Pz[b] - Pz[a];
            float e1x = Px[c] - Px[a], e1y = Py[c] - Py[a], e1z = Pz[c] - Pz[a];
            float nx = e0y * e1z - e0z * e1y;
            float ny = e0z * e1x - e0x * e1z;
            float nz = e0x * e1y - e0y * e1x;
            float d = nx * Nx + ny * Ny + nz * Nz;
            return d > 0f ? -1 : d < 0f ? 1 : 0;
        }

        private void AddOnPlaneEdge(int u, int w, int mask)
        {
            long key = EdgeKey(u, w);
            if (OnPlaneEdgeSides.TryGetValue(key, out int seen)) OnPlaneEdgeSides[key] = seen | mask;
            else { OnPlaneEdgeSides.Add(key, mask); OnPlaneEdgeOrder.Add(key); }
        }

        private void Intersect()
        {
            // The map is persistent and sized for the worst case; clearing all of it every cut would
            // charge a memset proportional to the mesh, not to the cut (2 MB on a 16k-triangle mesh).
            // Size the region actually used from the counts the earlier stages produced, as the C++
            // reference does: a crossed edge has at most two nodes per crossing triangle, and every
            // plane boundary edge contributes at most two vertex nodes.
            int expected = State[StateCrossingCount] * 2 + State[StateBoundaryEdges] * 2 + 1;
            int mapSize = 16;
            while (mapSize < expected * 4 && mapSize < MapKeys.Length) mapSize <<= 1;
            int mask = mapSize - 1;
            State[StateMapMask] = mask;
            for (int i = 0; i < mapSize; i++) MapKeys[i] = long.MinValue;

            int nodeCount = 0;
            for (int t = 0; t < TriangleCount; t++)
            {
                sbyte cls = TriClass[t];
                if (cls == 0)
                {
                    CornerVertices(t, out int va, out int vb, out int vc);
                    NodeP[t] = NodeOfEdge(va, vb, mask, ref nodeCount);
                    NodeQ[t] = NodeOfEdge(vc, va, mask, ref nodeCount);
                }
                else if (cls == 2)
                {
                    CornerVertices(t, out int vz, out int vx, out int vy);
                    NodeP[t] = VertexNode(vz, mask, ref nodeCount);
                    NodeQ[t] = NodeOfEdge(vx, vy, mask, ref nodeCount);
                }
            }
            for (int k = 0; k < PlaneBoundaryEdges.Length; k++)
            {
                long key = PlaneBoundaryEdges[k];
                VertexNode((int)(key >> 32), mask, ref nodeCount);
                VertexNode((int)(key & 0xFFFFFFFF), mask, ref nodeCount);
            }
            State[StateNodeCount] = nodeCount;

            var counters = Result[0];
            counters.GeneratedVertices = nodeCount;
            Result[0] = counters;
        }

        private void Emit()
        {
            int nodeCount = State[StateNodeCount];
            for (int i = 0; i < nodeCount; i++) { PosNodeMap[i] = -1; NegNodeMap[i] = -1; }
            bool hasBoundary = State[StateBoundaryEdges] > 0;

            for (int t = 0; t < TriangleCount; t++)
            {
                int i = t * 3;
                sbyte cls = TriClass[t];
                if (cls == 2)
                {
                    CornerVertices(t, out int vz, out int vx, out int vy);
                    int zn = NodeP[t], m = NodeQ[t];
                    bool xPos = Side[vx] > 0, yPos = Side[vy] > 0;
                    // (z, x, M) to x's side, (z, M, y) to y's side, both keeping the original winding.
                    if (xPos) { PosIndices.Add(PosNode(zn)); PosIndices.Add(PosOriginal(vx)); PosIndices.Add(PosNode(m)); PosSegFrom.Add(m); PosSegTo.Add(zn); }
                    else { NegIndices.Add(NegNode(zn)); NegIndices.Add(NegOriginal(vx)); NegIndices.Add(NegNode(m)); NegSegFrom.Add(m); NegSegTo.Add(zn); }
                    if (yPos) { PosIndices.Add(PosNode(zn)); PosIndices.Add(PosNode(m)); PosIndices.Add(PosOriginal(vy)); PosSegFrom.Add(zn); PosSegTo.Add(m); }
                    else { NegIndices.Add(NegNode(zn)); NegIndices.Add(NegNode(m)); NegIndices.Add(NegOriginal(vy)); NegSegFrom.Add(zn); NegSegTo.Add(m); }
                    continue;
                }
                if (cls > 0)
                {
                    PosIndices.Add(PosOriginal(Indices[i]));
                    PosIndices.Add(PosOriginal(Indices[i + 1]));
                    PosIndices.Add(PosOriginal(Indices[i + 2]));
                    if (hasBoundary) EmitPlaneBoundary(true, Indices[i], Indices[i + 1], Indices[i + 2]);
                    continue;
                }
                if (cls < 0)
                {
                    NegIndices.Add(NegOriginal(Indices[i]));
                    NegIndices.Add(NegOriginal(Indices[i + 1]));
                    NegIndices.Add(NegOriginal(Indices[i + 2]));
                    if (hasBoundary) EmitPlaneBoundary(false, Indices[i], Indices[i + 1], Indices[i + 2]);
                    continue;
                }

                CornerVertices(t, out int va, out int vb, out int vc);
                int p = NodeP[t], q = NodeQ[t];
                if (Side[va] > 0)
                {
                    // Lone-vertex side keeps the original winding as (a, P, Q); its cut edge runs P -> Q.
                    PosIndices.Add(PosOriginal(va)); PosIndices.Add(PosNode(p)); PosIndices.Add(PosNode(q));
                    PosSegFrom.Add(p); PosSegTo.Add(q);

                    // The other side is the quad (P, b, c, Q) split on the P-c diagonal; its edge runs Q -> P.
                    int qp = NegNode(p), qb = NegOriginal(vb), qc = NegOriginal(vc), qq = NegNode(q);
                    NegIndices.Add(qp); NegIndices.Add(qb); NegIndices.Add(qc);
                    NegIndices.Add(qp); NegIndices.Add(qc); NegIndices.Add(qq);
                    NegSegFrom.Add(q); NegSegTo.Add(p);
                }
                else
                {
                    NegIndices.Add(NegOriginal(va)); NegIndices.Add(NegNode(p)); NegIndices.Add(NegNode(q));
                    NegSegFrom.Add(p); NegSegTo.Add(q);

                    int qp = PosNode(p), qb = PosOriginal(vb), qc = PosOriginal(vc), qq = PosNode(q);
                    PosIndices.Add(qp); PosIndices.Add(qb); PosIndices.Add(qc);
                    PosIndices.Add(qp); PosIndices.Add(qc); PosIndices.Add(qq);
                    PosSegFrom.Add(q); PosSegTo.Add(p);
                }
            }
        }

        private void EmitPlaneBoundary(bool positive, int a, int b, int c)
        {
            EmitIfBoundary(positive, a, b);
            EmitIfBoundary(positive, b, c);
            EmitIfBoundary(positive, c, a);
        }

        private void EmitIfBoundary(bool positive, int u, int w)
        {
            if (Side[u] != 0 || Side[w] != 0) return;
            if (!OnPlaneEdgeSides.TryGetValue(EdgeKey(u, w), out int seen) || seen != 3) return;
            int mask = State[StateMapMask];
            int nodeCount = State[StateNodeCount];
            int from = VertexNode(u, mask, ref nodeCount), to = VertexNode(w, mask, ref nodeCount);
            if (positive) { PosSegFrom.Add(from); PosSegTo.Add(to); }
            else { NegSegFrom.Add(from); NegSegTo.Add(to); }
        }

        private void Contour()
        {
            var counters = Result[0];
            BuildContours(PosSegFrom, PosSegTo, PosContourNodes, PosContourStart, PosContourClosed, ref counters, true);
            BuildContours(NegSegFrom, NegSegTo, NegContourNodes, NegContourStart, NegContourClosed, ref counters, false);
            Result[0] = counters;
        }

        private void Cap()
        {
            if (!WithCap) return;
            int nodeCount = State[StateNodeCount];
            if (nodeCount == 0) return;

            // Projection onto the plane: the same seed axis, cross products and float dot products as
            // CapContext.Build, so every ear decision matches the other runtimes bit for bit.
            float nx = Nx, ny = Ny, nz = Nz;
            float sx, sy, sz;
            if (Math.Abs(nx) < 0.9f) { sx = 1; sy = 0; sz = 0; } else { sx = 0; sy = 1; sz = 0; }
            float ux = sy * nz - sz * ny, uy = sz * nx - sx * nz, uz = sx * ny - sy * nx;
            float inv = 1f / (float)Math.Sqrt(ux * ux + uy * uy + uz * uz);
            ux *= inv; uy *= inv; uz *= inv;
            float vx = ny * uz - nz * uy, vy = nz * ux - nx * uz, vz = nx * uy - ny * ux;

            var u = new NativeArray<float>(nodeCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var v = new NativeArray<float>(nodeCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var pos = new NativeArray<F3>(nodeCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int n = 0; n < nodeCount; n++)
            {
                float px = NodeX[n], py = NodeY[n], pz = NodeZ[n];
                u[n] = px * ux + py * uy + pz * uz;
                v[n] = px * vx + py * vy + pz * vz;
                pos[n] = new F3(px, py, pz);
            }
            var context = new BurstCapping.Context { U = u, V = v, Pos = pos, Eps = (float)OnPlaneEpsilon, HashThreshold = CapHashThreshold };

            var counters = Result[0];
            bool ok = AttachCaps(context, true, ref counters) && AttachCaps(context, false, ref counters);
            if (!ok) State[StateStatus] = 1;
            Result[0] = counters;
            u.Dispose(); v.Dispose(); pos.Dispose();
        }

        private bool AttachCaps(in BurstCapping.Context context, bool positive, ref BurstScanEdgeHashCut.Counters counters)
        {
            NativeList<int> contourNodes = positive ? PosContourNodes : NegContourNodes;
            NativeList<int> contourStart = positive ? PosContourStart : NegContourStart;
            NativeList<byte> contourClosed = positive ? PosContourClosed : NegContourClosed;
            int contours = contourClosed.Length;
            for (int c = 0; c < contours; c++)
            {
                if (contourClosed[c] == 0) continue;
                int start = contourStart[c], end = contourStart[c + 1];
                int k = end - start;
                if (k == 2) { counters.CapCyclesClosedBySurface++; continue; }

                // Cap traversal order is the surface contour reversed (plan 8.1).
                var cycle = new NativeArray<int>(k, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                cycle[0] = contourNodes[start];
                for (int i = 1; i < k; i++) cycle[i] = contourNodes[start + k - i];

                var result = new BurstCapping.CapOut
                {
                    Triangles = new NativeList<int>(k * 3, Allocator.Temp),
                    Aux = new NativeList<F3>(4, Allocator.Temp)
                };
                BurstCapping.SplitCap(context, cycle, k, ref result);
                if (result.Failure != 0)
                {
                    counters.Failure = result.Failure == 1 ? FailCapStalled : FailCapDisk;
                    result.Triangles.Dispose(); result.Aux.Dispose(); cycle.Dispose();
                    return false;
                }

                // Auxiliary vertices get fragment ids first, then the triangles are resolved.
                int auxBase = positive ? PosSourceVertex.Length : NegSourceVertex.Length;
                for (int i = 0; i < result.Aux.Length; i++)
                {
                    if (positive) { PosSourceVertex.Add(-1); PosSourceNode.Add(-1); PosAux.Add(result.Aux[i]); }
                    else { NegSourceVertex.Add(-1); NegSourceNode.Add(-1); NegAux.Add(result.Aux[i]); }
                }
                for (int i = 0; i < result.Triangles.Length; i++)
                {
                    int local = result.Triangles[i];
                    int id = local < k ? (positive ? PosNode(cycle[local]) : NegNode(cycle[local])) : auxBase + (local - k);
                    if (positive) PosIndices.Add(id); else NegIndices.Add(id);
                }
                counters.CapCycleNodes += k;
                counters.CapTriangles += result.Triangles.Length / 3;
                counters.CapDegenerateTriangles += result.Degenerate;
                counters.CapReflexVertices += result.Reflex;
                counters.CapArtifactTriangles += result.Artifacts;
                counters.CapAuxiliaryVertices += result.Aux.Length;
                counters.CapBoundaryCrossings += result.Crossings;
                result.Triangles.Dispose(); result.Aux.Dispose(); cycle.Dispose();
            }
            return true;
        }

        private void Materialize()
        {
            MaterializeSide(PosSourceVertex, PosSourceNode, PosAux, PosPx, PosPy, PosPz, PosAttr);
            MaterializeSide(NegSourceVertex, NegSourceNode, NegAux, NegPx, NegPy, NegPz, NegAttr);

            var counters = Result[0];
            counters.OutputTriangles = (PosIndices.Length + NegIndices.Length) / 3;
            counters.OutputVertices = PosSourceVertex.Length + NegSourceVertex.Length;

            int nodeCount = State[StateNodeCount];
            ulong keyHash = 0, paramHash = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                ulong k = Mix(0x51ED270Bul, (ulong)NodeKeys[n]);
                keyHash += k;
                long quantized = (long)Math.Round(NodeParam[n] / 1e-9);
                paramHash += Mix(k, (ulong)quantized);
            }
            counters.NodeKeyHash = keyHash;
            counters.NodeParamHash = paramHash;
            Result[0] = counters;
        }

        // ------------------------------------------------------------------ helpers

        public static long EdgeKey(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        private void CornerVertices(int t, out int va, out int vb, out int vc)
        {
            int i = t * 3;
            int v0 = Indices[i], v1 = Indices[i + 1], v2 = Indices[i + 2];
            byte lone = LoneCorner[t];
            if (lone == 0) { va = v0; vb = v1; vc = v2; }
            else if (lone == 1) { va = v1; vb = v2; vc = v0; }
            else { va = v2; vb = v0; vc = v1; }
        }

        private static ulong Mix(ulong h, ulong v)
        {
            h ^= v + 0x9E3779B97F4A7C15ul + (h << 6) + (h >> 2);
            h ^= h >> 30; h *= 0xBF58476D1CE4E5B9ul;
            h ^= h >> 27; h *= 0x94D049BB133111EBul;
            h ^= h >> 31;
            return h;
        }

        private int FindSlot(long key, int mask, out bool found)
        {
            int slot = (int)(Mix(0x9E3779B9ul, (ulong)key) & (ulong)mask);
            while (MapKeys[slot] != long.MinValue)
            {
                if (MapKeys[slot] == key) { found = true; return slot; }
                slot = (slot + 1) & mask;
            }
            found = false;
            return slot;
        }

        private int NodeOfEdge(int a, int b, int mask, ref int nodeCount)
        {
            long key = EdgeKey(a, b);
            int slot = FindSlot(key, mask, out bool found);
            if (found) return MapValues[slot];

            int lo = (int)(key >> 32), hi = (int)(key & 0xFFFFFFFF);
            // The edge's canonical low-to-high direction, so every incident triangle agrees exactly.
            float dLo = Dist[lo], dHi = Dist[hi];
            double param = dLo / (double)(dLo - dHi);
            float f = (float)param;

            int id = nodeCount++;
            MapKeys[slot] = key;
            MapValues[slot] = id;
            NodeKeys[id] = key;
            NodeParam[id] = param;
            NodeSourceVertex[id] = -1;
            NodeX[id] = Px[lo] + (Px[hi] - Px[lo]) * f;
            NodeY[id] = Py[lo] + (Py[hi] - Py[lo]) * f;
            NodeZ[id] = Pz[lo] + (Pz[hi] - Pz[lo]) * f;
            if (AttributeFloats > 0) InterpolateAttributes(lo, hi, f, id);
            return id;
        }

        // An existing vertex on the plane is a node keyed by the degenerate edge (v, v), parameter 0.
        private int VertexNode(int v, int mask, ref int nodeCount)
        {
            long key = EdgeKey(v, v);
            int slot = FindSlot(key, mask, out bool found);
            if (found) return MapValues[slot];

            int id = nodeCount++;
            State[StateNodeCount] = nodeCount;
            MapKeys[slot] = key;
            MapValues[slot] = id;
            NodeKeys[id] = key;
            NodeParam[id] = 0.0;
            NodeSourceVertex[id] = v;
            NodeX[id] = Px[v]; NodeY[id] = Py[v]; NodeZ[id] = Pz[v];
            if (AttributeFloats > 0) InterpolateAttributes(v, v, 0f, id);
            return id;
        }

        private void InterpolateAttributes(int lo, int hi, float t, int node)
        {
            int a = lo * AttributeFloats, b = hi * AttributeFloats, o = node * AttributeFloats;
            for (int k = 0; k < AttributeFloats; k++)
                NodeAttr[o + k] = Attributes[a + k] + (Attributes[b + k] - Attributes[a + k]) * t;

            // The game vertex is normal(3) + uv(2) + tangent(4). Directional channels are renormalized
            // with the same guard and fallback the other runtimes use, and the tangent's handedness flag
            // is taken from the lower-id endpoint rather than interpolated.
            if (AttributeFloats == 9)
            {
                Normalize(o, 0f, 1f, 0f);
                Normalize(o + 5, 1f, 0f, 0f);
                NodeAttr[o + 8] = Attributes[a + 8];
            }
        }

        private void Normalize(int o, float fx, float fy, float fz)
        {
            float x = NodeAttr[o], y = NodeAttr[o + 1], z = NodeAttr[o + 2];
            float l2 = x * x + y * y + z * z;
            if (l2 <= 1e-20f) { NodeAttr[o] = fx; NodeAttr[o + 1] = fy; NodeAttr[o + 2] = fz; return; }
            float inv = 1f / (float)Math.Sqrt(l2);
            NodeAttr[o] = x * inv; NodeAttr[o + 1] = y * inv; NodeAttr[o + 2] = z * inv;
        }

        private int PosOriginal(int v)
        {
            int id = PosOriginalMap[v];
            if (id >= 0) return id;
            id = PosSourceVertex.Length;
            PosOriginalMap[v] = id;
            PosSourceVertex.Add(v);
            PosSourceNode.Add(-1);
            return id;
        }

        private int PosNode(int n)
        {
            int source = NodeSourceVertex[n];
            if (source >= 0) return PosOriginal(source);
            int id = PosNodeMap[n];
            if (id >= 0) return id;
            id = PosSourceVertex.Length;
            PosNodeMap[n] = id;
            PosSourceVertex.Add(-1);
            PosSourceNode.Add(n);
            return id;
        }

        private int NegOriginal(int v)
        {
            int id = NegOriginalMap[v];
            if (id >= 0) return id;
            id = NegSourceVertex.Length;
            NegOriginalMap[v] = id;
            NegSourceVertex.Add(v);
            NegSourceNode.Add(-1);
            return id;
        }

        private int NegNode(int n)
        {
            int source = NodeSourceVertex[n];
            if (source >= 0) return NegOriginal(source);
            int id = NegNodeMap[n];
            if (id >= 0) return id;
            id = NegSourceVertex.Length;
            NegNodeMap[n] = id;
            NegSourceVertex.Add(-1);
            NegSourceNode.Add(n);
            return id;
        }

        /// <summary>
        /// Chains the recorded directed cut segments using node identity alone. Positions are never
        /// consulted, so sheets that meet in space but not in topology stay separate (plan 7.3). Open
        /// chains first, then cycles; the node lists are kept for the caps and the output.
        /// </summary>
        private void BuildContours(NativeList<int> segFrom, NativeList<int> segTo,
                                   NativeList<int> contourNodes, NativeList<int> contourStart, NativeList<byte> contourClosed,
                                   ref BurstScanEdgeHashCut.Counters counters, bool count)
        {
            int nodeCount = State[StateNodeCount];
            for (int i = 0; i < nodeCount; i++) { Next[i] = -1; Prev[i] = -1; Used[i] = 0; }
            for (int s = 0; s < segFrom.Length; s++)
            {
                Next[segFrom[s]] = segTo[s];
                Prev[segTo[s]] = segFrom[s];
            }

            contourStart.Add(0);
            for (int pass = 0; pass < 2; pass++)
            {
                for (int n = 0; n < nodeCount; n++)
                {
                    if (Used[n] != 0 || Next[n] < 0) continue;
                    if (pass == 0 && Prev[n] >= 0) continue;

                    int length = 0;
                    int cur = n;
                    while (cur >= 0 && Used[cur] == 0)
                    {
                        Used[cur] = 1;
                        contourNodes.Add(cur);
                        length++;
                        cur = Next[cur];
                    }
                    contourStart.Add(contourNodes.Length);
                    contourClosed.Add((byte)(pass == 1 ? 1 : 0));
                    if (!count) continue;
                    if (pass == 0) counters.OpenContourCount++; else counters.LoopCount++;
                    counters.LoopVertexTotal += length;
                    if (length > counters.LoopVertexMax) counters.LoopVertexMax = length;
                }
            }
        }

        private void MaterializeSide(NativeList<int> sourceVertex, NativeList<int> sourceNode, NativeList<F3> aux,
                                     NativeList<float> px, NativeList<float> py, NativeList<float> pz,
                                     NativeList<float> attr)
        {
            int n = sourceVertex.Length;
            px.ResizeUninitialized(n);
            py.ResizeUninitialized(n);
            pz.ResizeUninitialized(n);
            attr.ResizeUninitialized(n * AttributeFloats);

            int auxCursor = 0;
            for (int i = 0; i < n; i++)
            {
                int v = sourceVertex[i];
                if (v >= 0)
                {
                    px[i] = Px[v]; py[i] = Py[v]; pz[i] = Pz[v];
                    if (AttributeFloats > 0)
                    {
                        int src = v * AttributeFloats, dst = i * AttributeFloats;
                        for (int k = 0; k < AttributeFloats; k++) attr[dst + k] = Attributes[src + k];
                    }
                    continue;
                }
                int node = sourceNode[i];
                if (node >= 0)
                {
                    px[i] = NodeX[node]; py[i] = NodeY[node]; pz[i] = NodeZ[node];
                    if (AttributeFloats > 0)
                    {
                        int src = node * AttributeFloats, dst = i * AttributeFloats;
                        for (int k = 0; k < AttributeFloats; k++) attr[dst + k] = NodeAttr[src + k];
                    }
                    continue;
                }
                // Auxiliary cap vertex, in output order. Attributes are node 0's, the same placeholder
                // the other runtimes use.
                F3 p = aux[auxCursor++];
                px[i] = p.X; py[i] = p.Y; pz[i] = p.Z;
                if (AttributeFloats > 0)
                {
                    int dst = i * AttributeFloats;
                    for (int k = 0; k < AttributeFloats; k++) attr[dst + k] = NodeAttr[k];
                }
            }
        }
    }
}
