#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// FACE-PICK-1 (development and the Editor only): the collection, during ordinary play, of drawn faces the person points at -- a
    /// one-sided polygon left in the air, a cut face where none should be -- so that what the face is and what made it can be followed
    /// afterwards. One pick is what the left controller's line passes through when the left X button is pressed
    /// (<c>Sandbox.FaceCollector</c>), found in **what the display draws** and not in the colliders:
    /// <list type="bullet">
    /// <item>every command slot of the display's adopted draw data and every instance record it draws, each triangle placed by the
    /// instance's own transform, met from either face, and kept or left out by the instance's own clip record
    /// (<see cref="VpLogicalCutDisplay.TryGetInstanceRecordForDiagnosis"/>); a slot no registration names is searched like any other;</item>
    /// <item>every prepared temporary cap (<see cref="VpLogicalCutDisplay.TryGetCapRecord"/>), its polygon met from either face. A cap is
    /// drawn through a stencil, so the polygon met is not yet the face seen: the pick says whether the last classification made a job of
    /// it and how many times the eye's ray crosses the cap's body beyond the point (odd: the stencil would let it through).</item>
    /// </list>
    /// The nearest few along the line are saved in their order with their kind (<see cref="FacePick.MaxKept"/> that the clip keeps, then
    /// <see cref="FacePick.MaxClipped"/> the clip leaves out, said as such), each with: the triangle (its number in its command, its three
    /// indices and vertices as stored, placed in the world), the draw slot and instance record, the transform and clip record drawn with,
    /// the registration that holds the slot (or that none does), the render fragment with its branches and selected boundaries, the cap's
    /// record and polygon, the ledger's and the physics owner's word on every fragment named (or that there is none), and the whole drawn
    /// geometry of the registration. Where the piece is an NPC's and can be read as an issue reads one, the piece collection's own save
    /// (<see cref="NpcPiecePickCopy"/>) is made beside it under the same number. Everything goes through the capture's sink.
    /// <para>
    /// **What this does not see.** The search reads the display's adopted draw data and the storage's vertices on the CPU; what the GPU
    /// holds is read back only for the candidates saved (their indices and vertices compared, the answer kept). The selection on the GPU,
    /// the compaction and the passes' own states are not read: a face the CPU data does not explain is a pick with nothing met -- kept,
    /// with its pictures, as such.
    /// </para>
    /// </summary>
    internal static class FacePick
    {
        public const string Prefix = "FACE COLLECT: ";

        /// <summary>How many candidates the clip keeps are saved, nearest first; and how many the clip leaves out.</summary>
        internal const int MaxKept = 6, MaxClipped = 2;

        /// <summary>A point is left out by a clip plane only when it is farther than this on the plane's other side (metres).</summary>
        internal const float ClipTolerance = 1e-5f;

        /// <summary>The most raw bytes of drawn geometry one pick saves; a geometry past it is named and left out.</summary>
        internal const long GeometryBudget = 48L << 20;

        internal const int KindMesh = 0, KindCap = 1;
    }

    /// <summary>One description of a record for both directions: every call writes its value, or reads it into the same place.</summary>
    internal sealed unsafe class FacePickIo
    {
        private readonly SpatialSplitHashWriter _w;
        private readonly SpatialSplitHashReader _r;

        internal FacePickIo(SpatialSplitHashWriter w) { _w = w; }
        internal FacePickIo(SpatialSplitHashReader r) { _r = r; }

        internal bool Reading => _r != null;

        internal void I(ref int v) { if (_r != null) v = _r.Int(); else _w.Int(v); }
        internal void U(ref uint v) { if (_r != null) v = _r.UInt(); else _w.UInt(v); }
        internal void L(ref long v) { if (_r != null) v = _r.Long(); else _w.Long(v); }
        internal void F(ref float v) { if (_r != null) v = _r.Float(); else _w.Float(v); }
        internal void D(ref double v) { if (_r != null) v = _r.Double(); else _w.Double(v); }
        internal void B(ref bool v) { if (_r != null) v = _r.Bool(); else _w.Byte(v); }
        internal void S(ref string v) { if (_r != null) v = _r.String(); else _w.String(v ?? ""); }
        internal void V3(ref float3 v) { F(ref v.x); F(ref v.y); F(ref v.z); }
        internal void V4(ref float4 v) { F(ref v.x); F(ref v.y); F(ref v.z); F(ref v.w); }
        internal void M(ref float4x4 v) { if (_r != null) v = _r.Matrix(); else _w.Matrix(v); }

        internal void A<T>(ref T[] v) where T : unmanaged
        {
            if (_r != null)
            {
                int n = _r.Count("array", 1 << 27);
                v = _r.Array<T>(n);
            }
            else
            {
                int n = v != null ? v.Length : 0;
                _w.Int(n);
                if (n > 0)
                {
                    fixed (T* p = v)
                    {
                        _w.Bytes(p, (long)n * sizeof(T));
                    }
                }
            }
        }

        internal void Strings(ref string[] v)
        {
            int n = v != null ? v.Length : 0;
            I(ref n);
            if (_r != null)
            {
                if (n < 0 || n > 1 << 20) throw new InvalidDataException("a text list of " + n);
                v = new string[n];
            }

            for (int i = 0; i < n; i++) S(ref v[i]);
        }

        internal void List<T>(ref List<T> v, Action<T, FacePickIo> pass) where T : class, new()
        {
            int n = v != null ? v.Count : 0;
            I(ref n);
            if (_r != null)
            {
                if (n < 0 || n > 1 << 16) throw new InvalidDataException("a list of " + n);
                v = new List<T>(n);
                for (int i = 0; i < n; i++) v.Add(new T());
            }

            for (int i = 0; i < n; i++) pass(v[i], this);
        }

        internal void Identity(ref NpcPieceGeometryIdentity g)
        {
            I(ref g.vertexStart); I(ref g.vertexCount); I(ref g.topologyVertexCount); I(ref g.submeshStart); I(ref g.submeshCount); I(ref g.blockStart); I(ref g.blockCount);
        }
    }

    /// <summary>What the ledger, the physics owners, the geometry's DAG and the additional split say of one fragment a pick names.</summary>
    internal sealed class FacePickFragmentState
    {
        internal int fragment;
        internal string failure = "";
        internal bool known, currentTarget, hasRoot, hasParent, hasOrigin, hasOperation, hasActive, hasReplacing, hasSplitSource, fixedOwner;
        internal string state = "";
        internal int root, parent, generation, originOperation, activeOperation, replacingOperation, splitSource, anchors = -1;
        internal float originSide;
        internal int operationSource, operationPositive, operationNegative;
        internal string operationState = "";
        internal float4 operationPlane;

        // The physics owner: found at all; and then what it is.
        internal bool ownerFound, ownerWithdrawn, ownerReleased, ownerDisplayOnly, ownerFused, ownerHasRoot, ownerHasBody, ownerShapeLive, provisionalUnderWay, hasGeometryToOwner, npc;
        internal bool bodyKinematic, bodySleeping;
        internal string ownerName = "";
        internal float4x4 ownerLocalToWorld, geometryToOwner;
        internal float3 bodyPosition, bodyVelocity;
        internal float4 bodyRotation;
        internal float mass;
        internal int convexes = -1;

        // The geometry the DAG holds for it now.
        internal bool dagGeometry;
        internal NpcPieceGeometryIdentity dagIdentity;

        // The additional split's note and look.
        internal bool hasNote, noteAttempted, noteBoneLed, noteFellBack;
        internal int noteEndFrame, noteMadeFrame, noteMadeFrom;
        internal string noteEnd = "", noteMadeBy = "", noteBoneVerdict = "", noteBoneReason = "", examine = "";

        internal static void Pass(FacePickFragmentState s, FacePickIo io)
        {
            io.I(ref s.fragment); io.S(ref s.failure);
            io.B(ref s.known); io.B(ref s.currentTarget); io.B(ref s.hasRoot); io.B(ref s.hasParent); io.B(ref s.hasOrigin); io.B(ref s.hasOperation); io.B(ref s.hasActive);
            io.B(ref s.hasReplacing); io.B(ref s.hasSplitSource); io.B(ref s.fixedOwner); io.S(ref s.state);
            io.I(ref s.root); io.I(ref s.parent); io.I(ref s.generation); io.I(ref s.originOperation); io.I(ref s.activeOperation); io.I(ref s.replacingOperation); io.I(ref s.splitSource);
            io.I(ref s.anchors); io.F(ref s.originSide);
            io.I(ref s.operationSource); io.I(ref s.operationPositive); io.I(ref s.operationNegative); io.S(ref s.operationState); io.V4(ref s.operationPlane);
            io.B(ref s.ownerFound); io.B(ref s.ownerWithdrawn); io.B(ref s.ownerReleased); io.B(ref s.ownerDisplayOnly); io.B(ref s.ownerFused); io.B(ref s.ownerHasRoot); io.B(ref s.ownerHasBody);
            io.B(ref s.ownerShapeLive); io.B(ref s.provisionalUnderWay); io.B(ref s.hasGeometryToOwner); io.B(ref s.npc); io.B(ref s.bodyKinematic); io.B(ref s.bodySleeping);
            io.S(ref s.ownerName); io.M(ref s.ownerLocalToWorld); io.M(ref s.geometryToOwner); io.V3(ref s.bodyPosition); io.V3(ref s.bodyVelocity); io.V4(ref s.bodyRotation);
            io.F(ref s.mass); io.I(ref s.convexes);
            io.B(ref s.dagGeometry); io.Identity(ref s.dagIdentity);
            io.B(ref s.hasNote); io.B(ref s.noteAttempted); io.B(ref s.noteBoneLed); io.B(ref s.noteFellBack);
            io.I(ref s.noteEndFrame); io.I(ref s.noteMadeFrame); io.I(ref s.noteMadeFrom);
            io.S(ref s.noteEnd); io.S(ref s.noteMadeBy); io.S(ref s.noteBoneVerdict); io.S(ref s.noteBoneReason); io.S(ref s.examine);
        }

        internal string Describe()
        {
            if (failure.Length > 0) return "fragment " + fragment + ": NOT READ (" + failure + ")";
            var t = new StringBuilder(256);
            t.Append("fragment ").Append(fragment).Append(known ? ": " + state : ": NOT KNOWN to the ledger").Append(currentTarget ? ", a current target" : ", not a current target");
            if (hasRoot) t.Append(", root ").Append(root);
            if (hasParent) t.Append(", parent ").Append(parent).Append(" (generation ").Append(generation).Append(')');
            if (hasOrigin) t.Append(", made by operation ").Append(originOperation).Append(" side ").Append(originSide.ToString("R", CultureInfo.InvariantCulture)).Append(hasOperation ? " (" + operationState + " of fragment " + operationSource + ")" : " (operation not read)");
            if (hasSplitSource) t.Append(", split from ").Append(splitSource);
            if (hasActive) t.Append(", a cut under way: operation ").Append(activeOperation);
            t.Append(ownerFound ? "; owner " + (ownerHasRoot ? ownerName : "(root gone)") + (ownerDisplayOnly ? " display only" : "") + (ownerFused ? " fused" : "") + (ownerWithdrawn ? " WITHDRAWN" : "") + (ownerReleased ? " RELEASED" : "")
                + (ownerHasBody ? ", body" + (bodyKinematic ? " kinematic" : "") + (bodySleeping ? " asleep" : "") : ", no body") + ", convexes " + convexes : "; NO physics owner");
            t.Append(dagGeometry ? "; geometry " + dagIdentity : "; no settled geometry");
            if (npc) t.Append("; an NPC's");
            if (hasNote) t.Append("; split note: ").Append(noteAttempted ? "last attempt ended " + noteEnd + " at frame " + noteEndFrame : "not attempted").Append(noteMadeBy.Length > 0 ? ", made from " + noteMadeFrom + " by " + noteMadeBy + " at frame " + noteMadeFrame : "");
            return t.ToString();
        }
    }

    /// <summary>One render fragment of the adopted snapshot as a pick found it: what it is drawn as, its branches and their boundaries.</summary>
    internal sealed class FacePickRenderFragment
    {
        internal int index = -1, registration = -1, registrationRoot, root;
        internal bool found, aggregated;
        internal float rootPendingSide;
        internal int branchStart, branchCount, conditionStart, conditionCount, capStart, capCount;
        internal float3 boundsCentre, boundsExtents;
        internal float4x4 geometryLocalToWorld;
        internal float4[] clipPlanes = Array.Empty<float4>();

        // The branches it stands for, and their candidates flattened (candidateBranch: which of the branches above).
        internal int[] branchFragment = Array.Empty<int>(), branchPendingOperation = Array.Empty<int>(), branchSelected = Array.Empty<int>(), branchCandidates = Array.Empty<int>();
        internal float[] branchPendingSide = Array.Empty<float>();
        internal int[] candidateBranch = Array.Empty<int>(), candidateOperation = Array.Empty<int>(), candidateState = Array.Empty<int>(), candidatePending = Array.Empty<int>(), candidateRequires = Array.Empty<int>();
        internal float[] candidateSide = Array.Empty<float>();
        internal float4[] candidatePlane = Array.Empty<float4>();

        internal static void Pass(FacePickRenderFragment s, FacePickIo io)
        {
            io.I(ref s.index); io.I(ref s.registration); io.I(ref s.registrationRoot); io.I(ref s.root); io.B(ref s.found); io.B(ref s.aggregated); io.F(ref s.rootPendingSide);
            io.I(ref s.branchStart); io.I(ref s.branchCount); io.I(ref s.conditionStart); io.I(ref s.conditionCount); io.I(ref s.capStart); io.I(ref s.capCount);
            io.V3(ref s.boundsCentre); io.V3(ref s.boundsExtents); io.M(ref s.geometryLocalToWorld); io.A(ref s.clipPlanes);
            io.A(ref s.branchFragment); io.A(ref s.branchPendingOperation); io.A(ref s.branchSelected); io.A(ref s.branchCandidates); io.A(ref s.branchPendingSide);
            io.A(ref s.candidateBranch); io.A(ref s.candidateOperation); io.A(ref s.candidateState); io.A(ref s.candidatePending); io.A(ref s.candidateRequires);
            io.A(ref s.candidateSide); io.A(ref s.candidatePlane);
        }
    }

    /// <summary>
    /// The whole drawn geometry of one registration (every command it holds), or of one draw slot no registration names: the indices as
    /// drawn -- the storage's global vertex numbers -- and each vertex they name once, as the storage (or, for a region the GPU bakes, the
    /// vertex buffer) holds it.
    /// </summary>
    internal sealed class FacePickGeometry
    {
        internal int ownerList, ownerIndex, fragment, vertexStride;
        internal bool hasIdentity, gpuRegion;
        internal NpcPieceGeometryIdentity identity;
        internal string indexState = "", failure = "";
        internal int indexStart, indexCount;
        internal float3 boundsCentre, boundsExtents;
        internal float4x4 objectToWorld, lineageToGeometryLocal;

        // By command, in the registration's order.
        internal int[] commandSlot = Array.Empty<int>(), commandVertexStart = Array.Empty<int>(), commandVertexCount = Array.Empty<int>(), commandIndexStart = Array.Empty<int>(),
            commandIndexCount = Array.Empty<int>(), commandInstances = Array.Empty<int>(), commandSource = Array.Empty<int>(), commandMaterialIndex = Array.Empty<int>(), commandOffset = Array.Empty<int>();
        internal string[] commandMaterial = Array.Empty<string>(), commandShader = Array.Empty<string>(), commandFailure = Array.Empty<string>();

        internal uint[] indices = Array.Empty<uint>();           // every read command's, one after another (commandOffset: where each begins; -1 not read)
        internal uint[] vertexIds = Array.Empty<uint>();         // ascending, each once
        internal VpRenderVertex[] vertices = Array.Empty<VpRenderVertex>();   // in the ids' order
        internal byte[] vertexRead = Array.Empty<byte>();        // 1 where the vertex was read; 0 where its number is outside what could be read

        internal static void Pass(FacePickGeometry s, FacePickIo io)
        {
            io.I(ref s.ownerList); io.I(ref s.ownerIndex); io.I(ref s.fragment); io.I(ref s.vertexStride); io.B(ref s.hasIdentity); io.B(ref s.gpuRegion); io.Identity(ref s.identity);
            io.S(ref s.indexState); io.S(ref s.failure); io.I(ref s.indexStart); io.I(ref s.indexCount);
            io.V3(ref s.boundsCentre); io.V3(ref s.boundsExtents); io.M(ref s.objectToWorld); io.M(ref s.lineageToGeometryLocal);
            io.A(ref s.commandSlot); io.A(ref s.commandVertexStart); io.A(ref s.commandVertexCount); io.A(ref s.commandIndexStart); io.A(ref s.commandIndexCount);
            io.A(ref s.commandInstances); io.A(ref s.commandSource); io.A(ref s.commandMaterialIndex); io.A(ref s.commandOffset);
            io.Strings(ref s.commandMaterial); io.Strings(ref s.commandShader); io.Strings(ref s.commandFailure);
            if (io.Reading && s.vertexStride != UnsafeUtility.SizeOf<VpRenderVertex>())
            {
                throw new InvalidDataException("the geometry's vertices are of " + s.vertexStride + " bytes; this build's are of " + UnsafeUtility.SizeOf<VpRenderVertex>());
            }

            io.A(ref s.indices); io.A(ref s.vertexIds); io.A(ref s.vertices); io.A(ref s.vertexRead);
        }

        /// <summary>The saved vertex of a global number: false where the geometry does not hold it (or it was not read).</summary>
        internal bool TryGetVertex(uint id, out VpRenderVertex vertex)
        {
            int at = Array.BinarySearch(vertexIds, id);
            if (at < 0 || vertexRead[at] == 0)
            {
                vertex = default;
                return false;
            }

            vertex = vertices[at];
            return true;
        }

        internal int CommandOfSlot(int slot) => Array.IndexOf(commandSlot, slot);
    }

    /// <summary>One candidate of a pick: a triangle of a drawn instance, or a prepared cap's polygon, met by the line.</summary>
    internal sealed class FacePickCandidate
    {
        internal int kind, rank;
        internal bool clipRejected, backFace;
        internal float distance, eyeFacing = float.NaN;
        internal float3 hitWorld, normalWorld;

        // References into the pick's lists (-1: none).
        internal int renderFragmentRef = -1, geometryRef = -1, stateRef = -1;

        // A drawn triangle.
        internal int slot = -1, instance, record, startInstance, instanceCount, vertexStart, vertexCount, indexStart, indexCount, materialIndex = -1;
        internal bool clipRoute, provisional, capMarked;
        internal string material = "", shader = "";
        internal int triangle = -1, indexAt = -1;
        internal uint i0, i1, i2;
        internal VpRenderVertex[] triangleVertices = Array.Empty<VpRenderVertex>();
        internal float3 w0, w1, w2, hitLocal, barycentric;
        internal float4x4 transform;
        internal float4[] clipPlanes = Array.Empty<float4>();
        internal float clipMin = float.PositiveInfinity;
        internal int sideSource, sideOperation, sideFragment, sideRenderFragment;
        internal float side;
        internal bool sidePublished, sideFixed;
        internal int ownerList, ownerIndex = -1, ownerCommand = -1, ownerFragment, ownerRenderFragmentsShown, ownerFirstRenderFragmentShown, ownerInstanceStart, ownerInstanceStride, ownerSlotRenderFragments, renderFragment = -1;
        internal bool ownerSplitShown, ownerAwaitingShown, ownerSlotsClipped, ownerGpuRegion;
        internal int indexSource, gpuCheck;
        internal string indexFailure = "", gpuCheckDetail = "";

        // A prepared cap.
        internal int capIndex = -1, capVertexCount, capFan = -1, capSource, capOperation, capFragment, capRenderFragment = -1;
        internal float capSide;
        internal bool capPublished, capFixed, capHasJob, capLastColour, eyeEstimated, eyeInside;
        internal int capJob = -1, capColour = -1, capVolumeGroup = -1, eyeCrossingsBeyond, eyeCrossingsBefore;
        internal float4 capPlane;
        internal float3 capOutward;
        internal float3[] capPolygon = Array.Empty<float3>();

        internal static void Pass(FacePickCandidate s, FacePickIo io)
        {
            io.I(ref s.kind); io.I(ref s.rank); io.B(ref s.clipRejected); io.B(ref s.backFace); io.F(ref s.distance); io.F(ref s.eyeFacing); io.V3(ref s.hitWorld); io.V3(ref s.normalWorld);
            io.I(ref s.renderFragmentRef); io.I(ref s.geometryRef); io.I(ref s.stateRef);
            io.I(ref s.slot); io.I(ref s.instance); io.I(ref s.record); io.I(ref s.startInstance); io.I(ref s.instanceCount); io.I(ref s.vertexStart); io.I(ref s.vertexCount);
            io.I(ref s.indexStart); io.I(ref s.indexCount); io.I(ref s.materialIndex); io.B(ref s.clipRoute); io.B(ref s.provisional); io.B(ref s.capMarked);
            io.S(ref s.material); io.S(ref s.shader); io.I(ref s.triangle); io.I(ref s.indexAt); io.U(ref s.i0); io.U(ref s.i1); io.U(ref s.i2);
            int stride = UnsafeUtility.SizeOf<VpRenderVertex>();
            io.I(ref stride);
            if (io.Reading && stride != UnsafeUtility.SizeOf<VpRenderVertex>()) throw new InvalidDataException("the candidate's vertices are of " + stride + " bytes");
            io.A(ref s.triangleVertices);
            io.V3(ref s.w0); io.V3(ref s.w1); io.V3(ref s.w2); io.V3(ref s.hitLocal); io.V3(ref s.barycentric); io.M(ref s.transform); io.A(ref s.clipPlanes); io.F(ref s.clipMin);
            io.I(ref s.sideSource); io.I(ref s.sideOperation); io.I(ref s.sideFragment); io.I(ref s.sideRenderFragment); io.F(ref s.side); io.B(ref s.sidePublished); io.B(ref s.sideFixed);
            io.I(ref s.ownerList); io.I(ref s.ownerIndex); io.I(ref s.ownerCommand); io.I(ref s.ownerFragment); io.I(ref s.ownerRenderFragmentsShown); io.I(ref s.ownerFirstRenderFragmentShown);
            io.I(ref s.ownerInstanceStart); io.I(ref s.ownerInstanceStride); io.I(ref s.ownerSlotRenderFragments); io.I(ref s.renderFragment);
            io.B(ref s.ownerSplitShown); io.B(ref s.ownerAwaitingShown); io.B(ref s.ownerSlotsClipped); io.B(ref s.ownerGpuRegion);
            io.I(ref s.indexSource); io.I(ref s.gpuCheck); io.S(ref s.indexFailure); io.S(ref s.gpuCheckDetail);
            io.I(ref s.capIndex); io.I(ref s.capVertexCount); io.I(ref s.capFan); io.I(ref s.capSource); io.I(ref s.capOperation); io.I(ref s.capFragment); io.I(ref s.capRenderFragment);
            io.F(ref s.capSide); io.B(ref s.capPublished); io.B(ref s.capFixed); io.B(ref s.capHasJob); io.B(ref s.capLastColour); io.B(ref s.eyeEstimated); io.B(ref s.eyeInside);
            io.I(ref s.capJob); io.I(ref s.capColour); io.I(ref s.capVolumeGroup); io.I(ref s.eyeCrossingsBeyond); io.I(ref s.eyeCrossingsBefore);
            io.V4(ref s.capPlane); io.V3(ref s.capOutward); io.A(ref s.capPolygon);
        }

        internal string Describe()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            string P(float3 v) => "(" + v.x.ToString("F3", inv) + ", " + v.y.ToString("F3", inv) + ", " + v.z.ToString("F3", inv) + ")";
            var t = new StringBuilder(256);
            t.Append('[').Append(rank).Append("] ").Append(kind == FacePick.KindCap ? "CAP" : "MESH").Append(clipRejected ? " (left out by its clip)" : "").Append(" at ").Append(distance.ToString("F3", inv)).Append(" m ").Append(P(hitWorld))
                .Append(backFace ? ", met from the back" : ", met from the front");
            if (!float.IsNaN(eyeFacing)) t.Append(eyeFacing > 0f ? ", facing the eye" : ", facing away from the eye");
            if (kind == FacePick.KindMesh)
            {
                t.Append(": slot ").Append(slot).Append(" instance ").Append(instance).Append(" (record ").Append(record).Append("), triangle ").Append(triangle).Append(" [").Append(i0).Append(", ").Append(i1).Append(", ").Append(i2).Append(']')
                    .Append(capMarked ? " (a cap's vertices)" : "").Append(", material ").Append(material).Append(", planes ").Append(clipPlanes.Length)
                    .Append(ownerList == 0 ? ", NO REGISTRATION names the slot" : ", registration " + (VpDrawSlotOwnerList)ownerList + " #" + ownerIndex + " of fragment " + ownerFragment + ", render fragment " + renderFragment)
                    .Append(", indices from ").Append(indexSource == 1 ? "the storage" : indexSource == 2 ? "THE GPU (" + indexFailure + ")" : "nowhere")
                    .Append(", GPU ").Append(gpuCheck == 1 ? "the same" : gpuCheck == 2 ? "DIFFERENT (" + gpuCheckDetail + ")" : gpuCheck == 3 ? "not read (" + gpuCheckDetail + ")" : "not compared");
            }
            else
            {
                t.Append(": cap ").Append(capIndex).Append(" of render fragment ").Append(capRenderFragment).Append(", source ").Append(capSource).Append(" operation ").Append(capOperation).Append(" side ").Append(capSide.ToString("R", inv))
                    .Append(capPublished ? ", published as fragment " + capFragment : ", not published").Append(", polygon of ").Append(capVertexCount)
                    .Append(capHasJob ? ", job " + capJob + " colour " + capColour + (capLastColour ? " (the last colour)" : "") : ", NO JOB in the last classification")
                    .Append(eyeEstimated ? ", the eye's ray crosses its body " + eyeCrossingsBeyond + " times beyond (" + (eyeInside ? "inside: the stencil would show it" : "outside: the stencil would hide it") + "), " + eyeCrossingsBefore + " before" : ", no eye estimate");
            }

            return t.ToString();
        }
    }

    /// <summary>One pick of the face collection: the line, the eye, what was searched, and the candidates with everything they name.</summary>
    internal sealed class FacePickSample
    {
        internal const string Magic = "ZTKSFAC1", End = "END!";
        internal const int FormatVersion = 1;

        internal string sessionId = "", codeVersion = "", sha256 = "", utc = "", bodyRoute = "", graphicsDevice = "", failure = "";
        internal int pick, frame;
        internal double realtimeSeconds;
        internal long world, ledgerRevision, displayGeneration;

        // The line and the eye.
        internal bool aimed, hasCamera, stereo;
        internal float3 aimOrigin, aimDirection, cameraPosition;
        internal float reach;
        internal float4 cameraRotation;
        internal float4x4 worldToCamera, projection;
        internal int pixelWidth, pixelHeight;
        internal string imageBefore = "", imageAfter = "";

        // What was searched.
        internal int commandEnd, instanceEnd, slots, freeSlots, instances, unreadRecords, coarse, boxed, slotsRead, slotsUnread, triangles, meshHits, meshClipRejected, capRecords, capHits;
        internal bool hasStencil, stencilPreparedNow;
        internal string stencilOutcome = "";
        internal int stencilCapRecords, stencilEmptyCaps, stencilHiddenCaps, stencilJobs, stencilVolumeGroups;

        // The piece collection's own save of the first candidate's piece, where it could be made.
        internal bool pieceAttempted, pieceSaved;
        internal int pieceFragment;
        internal string pieceFailure = "", pieceSample = "", pieceBones = "", pieceRecord = "";

        // Unity's own renderers (not the display's draws) whose world boxes the line passes through, nearest first, a line each: by the
        // box alone -- no triangle of theirs is read. A face the display's data does not explain may be one of these.
        internal string[] unityRenderers = Array.Empty<string>();

        internal List<FacePickCandidate> candidates = new List<FacePickCandidate>();
        internal List<FacePickRenderFragment> renderFragments = new List<FacePickRenderFragment>();
        internal List<FacePickGeometry> geometries = new List<FacePickGeometry>();
        internal List<FacePickFragmentState> states = new List<FacePickFragmentState>();

        internal static string FileName(long world, long pick) =>
            "w" + world.ToString("D3", CultureInfo.InvariantCulture) + "-a" + pick.ToString("D7", CultureInfo.InvariantCulture) + ".zsf.gz";

        internal static string ImageName(long world, long pick, string which) =>
            "w" + world.ToString("D3", CultureInfo.InvariantCulture) + "-a" + pick.ToString("D7", CultureInfo.InvariantCulture) + "-" + which + ".png";

        private void Pass(FacePickIo io)
        {
            io.S(ref sessionId); io.S(ref codeVersion); io.S(ref utc); io.S(ref bodyRoute); io.S(ref graphicsDevice); io.S(ref failure);
            io.I(ref pick); io.I(ref frame); io.D(ref realtimeSeconds); io.L(ref world); io.L(ref ledgerRevision); io.L(ref displayGeneration);
            io.B(ref aimed); io.B(ref hasCamera); io.B(ref stereo); io.V3(ref aimOrigin); io.V3(ref aimDirection); io.V3(ref cameraPosition); io.F(ref reach); io.V4(ref cameraRotation);
            io.M(ref worldToCamera); io.M(ref projection); io.I(ref pixelWidth); io.I(ref pixelHeight); io.S(ref imageBefore); io.S(ref imageAfter);
            io.I(ref commandEnd); io.I(ref instanceEnd); io.I(ref slots); io.I(ref freeSlots); io.I(ref instances); io.I(ref unreadRecords); io.I(ref coarse); io.I(ref boxed);
            io.I(ref slotsRead); io.I(ref slotsUnread); io.I(ref triangles); io.I(ref meshHits); io.I(ref meshClipRejected); io.I(ref capRecords); io.I(ref capHits);
            io.B(ref hasStencil); io.B(ref stencilPreparedNow); io.S(ref stencilOutcome);
            io.I(ref stencilCapRecords); io.I(ref stencilEmptyCaps); io.I(ref stencilHiddenCaps); io.I(ref stencilJobs); io.I(ref stencilVolumeGroups);
            io.B(ref pieceAttempted); io.B(ref pieceSaved); io.I(ref pieceFragment); io.S(ref pieceFailure); io.S(ref pieceSample); io.S(ref pieceBones); io.S(ref pieceRecord);
            io.Strings(ref unityRenderers);
            io.List(ref candidates, FacePickCandidate.Pass);
            io.List(ref renderFragments, FacePickRenderFragment.Pass);
            io.List(ref geometries, FacePickGeometry.Pass);
            io.List(ref states, FacePickFragmentState.Pass);
        }

        internal byte[] Make(out string hash, out long raw)
        {
            using (var memory = new MemoryStream())
            {
                using (var gzip = new GZipStream(memory, System.IO.Compression.CompressionLevel.Fastest, true))
                using (var w = new SpatialSplitHashWriter(gzip, new byte[1 << 16]))
                {
                    w.Ascii(Magic);
                    w.Int(FormatVersion);
                    Pass(new FacePickIo(w));
                    w.Ascii(End);
                    hash = w.Finish();
                    raw = w.Length;
                }

                return memory.ToArray();
            }
        }

        internal static FacePickSample Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var r = new SpatialSplitHashReader(gzip))
            {
                var s = new FacePickSample();
                r.Expect(Magic);
                int version = r.Int();
                if (version != FormatVersion) throw new InvalidDataException("face pick format " + version + ", this reads " + FormatVersion);
                s.Pass(new FacePickIo(r));
                r.Expect(End);
                s.sha256 = r.Finish();
                return s;
            }
        }

        internal FacePickFragmentState StateOf(int fragment)
        {
            for (int i = 0; i < states.Count; i++) if (states[i].fragment == fragment) return states[i];
            return null;
        }

        /// <summary>The faces.jsonl line: the pick, its file and what it found first.</summary>
        internal string Line(string file, string hash, long raw, int fileBytes)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            string J(string s) => SpatialSplitCaptureSink.Json(s ?? "");
            string V(float3 v) => "[" + J(v.x.ToString("R", inv)) + "," + J(v.y.ToString("R", inv)) + "," + J(v.z.ToString("R", inv)) + "]";
            int kept = 0, clipped = 0;
            for (int i = 0; i < candidates.Count; i++) { if (candidates[i].clipRejected) clipped++; else kept++; }
            FacePickCandidate first = candidates.Count > 0 ? candidates[0] : null;
            return "{\"pick\":" + pick + ",\"world\":" + world + ",\"frame\":" + frame + ",\"seconds\":" + realtimeSeconds.ToString("R", inv) + ",\"utc\":" + J(utc) + ",\"failure\":" + J(failure)
                + ",\"file\":" + J(file) + ",\"sha256\":" + J(hash) + ",\"rawBytes\":" + raw + ",\"fileBytes\":" + fileBytes
                + ",\"imageBefore\":" + J(imageBefore) + ",\"imageAfter\":" + J(imageAfter)
                + ",\"displayGeneration\":" + displayGeneration + ",\"ledgerRevision\":" + ledgerRevision + ",\"bodyRoute\":" + J(bodyRoute)
                + ",\"aimOrigin\":" + V(aimOrigin) + ",\"aimDirection\":" + V(aimDirection)
                + ",\"candidates\":" + candidates.Count + ",\"kept\":" + kept + ",\"clipRejected\":" + clipped + ",\"meshHits\":" + meshHits + ",\"capHits\":" + capHits
                + ",\"geometries\":" + geometries.Count + ",\"renderFragments\":" + renderFragments.Count + ",\"states\":" + states.Count
                + ",\"firstKind\":" + J(first == null ? "" : first.kind == FacePick.KindCap ? "cap" : "mesh") + ",\"firstClipRejected\":" + (first != null && first.clipRejected ? "true" : "false")
                + ",\"firstHit\":" + (first != null ? V(first.hitWorld) : "null") + ",\"firstDistance\":" + J(first != null ? first.distance.ToString("R", inv) : "")
                + ",\"firstSlot\":" + (first != null ? first.slot : -1) + ",\"firstTriangle\":" + (first != null ? first.triangle : -1) + ",\"firstCap\":" + (first != null ? first.capIndex : -1)
                + ",\"firstOwnerList\":" + (first != null ? first.ownerList : -1) + ",\"firstFragment\":" + (first != null && first.stateRef >= 0 ? states[first.stateRef].fragment : -1)
                + ",\"pieceAttempted\":" + (pieceAttempted ? "true" : "false") + ",\"pieceSaved\":" + (pieceSaved ? "true" : "false") + ",\"pieceFailure\":" + J(pieceFailure) + "}";
        }

        internal string Describe()
        {
            var t = new StringBuilder(1024);
            t.Append("pick ").Append(pick).Append(" frame ").Append(frame).Append(", display generation ").Append(displayGeneration).Append(", route ").Append(bodyRoute)
                .Append(": searched ").Append(slots).Append(" slots (").Append(freeSlots).Append(" free), ").Append(instances).Append(" instances, ").Append(coarse).Append(" near the line, ").Append(boxed)
                .Append(" boxes met, ").Append(triangles).Append(" triangles tested (").Append(slotsUnread).Append(" slots not read); mesh hits ").Append(meshHits).Append(" (").Append(meshClipRejected)
                .Append(" left out by clips), caps ").Append(capRecords).Append(" with ").Append(capHits).Append(" met");
            if (failure.Length > 0) t.Append("; ").Append(failure);
            for (int i = 0; i < candidates.Count; i++) t.Append("\n    ").Append(candidates[i].Describe());
            for (int i = 0; i < states.Count; i++) t.Append("\n    ").Append(states[i].Describe());
            for (int i = 0; i < unityRenderers.Length; i++) t.Append("\n    Unity renderer: ").Append(unityRenderers[i]);
            if (pieceAttempted) t.Append("\n    the piece's own save (fragment ").Append(pieceFragment).Append("): ").Append(pieceSaved ? pieceSample + " " + pieceBones + " " + pieceRecord : "NOT made (" + pieceFailure + ")");
            return t.ToString();
        }
    }

    /// <summary>The search and the copy of one pick (the main thread, in the frame of the press).</summary>
    internal static unsafe class FacePickCopy
    {
        /// <summary>Where the line pointed and the eye stood, as the collector read them (the world's frame).</summary>
        internal struct Aim
        {
            internal bool aimed, hasCamera, stereo;
            internal float3 origin, direction, cameraPosition;
            internal float reach;
            internal float4 cameraRotation;
            internal float4x4 worldToCamera, projection;
            internal int pixelWidth, pixelHeight;
            internal Camera camera;
        }

        private struct Hit
        {
            internal double t, u, v;
            internal int kind, slot, instance, triangle, cap, fan;
            internal bool clipped, back;
            internal float clipMin;
        }

        private sealed class SlotData
        {
            internal int slot, startInstance, source;
            internal VpIndirectCommand command;
            internal bool hasOwner, read, tried;
            internal VpDrawSlotOwner owner;
            internal uint[] indices;
            internal VpRenderVertex[] gpuVertices;   // a region the GPU bakes: the command's own vertex range read back
            internal string failure = "";
            internal Material material;
            internal bool provisional;
        }

        private static readonly Dictionary<int, VpDrawSlotOwner> s_owners = new Dictionary<int, VpDrawSlotOwner>();
        private static readonly Dictionary<int, SlotData> s_slots = new Dictionary<int, SlotData>();
        private static readonly List<Hit> s_hits = new List<Hit>();

        // ---- the coarse look, every frame --------------------------------------------------------------------------------

        /// <summary>
        /// How many drawn instances' boxes the line passes through within <paramref name="reach"/> (the coarse look of every frame: a
        /// bounding sphere first, then the command's box in the instance's own frame; no index, vertex or triangle is read).
        /// </summary>
        internal static int Coarse(VpLogicalCutDisplay display, float3 origin, float3 direction, float reach)
        {
            if (display == null || display.IsDisposed) return 0;
            int found = 0;
            int end = display.DrawCommandEnd;
            for (int slot = 0; slot < end; slot++)
            {
                if (!display.TryGetCommandSlot(slot, out VpIndirectCommand command, out int start, out bool free) || free) continue;
                for (int k = 0; k < command.instanceCount; k++)
                {
                    if (!display.TryGetInstanceRecordForDiagnosis(start + k, out Matrix4x4 transform, out _, out _)) continue;
                    if (!SphereMeets(in transform, command.localBounds, origin, direction, reach)) continue;
                    float det = transform.determinant;
                    if (!(math.abs(det) > 1e-20f) || float.IsNaN(det) || float.IsInfinity(det)) continue;
                    Matrix4x4 inverse = transform.inverse;
                    float least = math.sqrt(math.min(math.lengthsq(new float3(transform.m00, transform.m10, transform.m20)), math.min(math.lengthsq(new float3(transform.m01, transform.m11, transform.m21)), math.lengthsq(new float3(transform.m02, transform.m12, transform.m22)))));
                    if (BoxMeets(command.localBounds, (float3)inverse.MultiplyPoint3x4(origin), (float3)inverse.MultiplyVector(direction), reach, least)) found++;
                }
            }

            return found;
        }

        private static bool SphereMeets(in Matrix4x4 m, Bounds local, float3 origin, float3 direction, float reach)
        {
            float3 centre = m.MultiplyPoint3x4(local.center);
            float sx = math.lengthsq(new float3(m.m00, m.m10, m.m20)), sy = math.lengthsq(new float3(m.m01, m.m11, m.m21)), sz = math.lengthsq(new float3(m.m02, m.m12, m.m22));
            float scale = math.sqrt(math.max(sx, math.max(sy, sz)));
            float radius = (math.length((float3)local.extents) * scale * 1.1f) + 0.05f;
            float along = math.clamp(math.dot(centre - origin, direction), 0f, reach);
            return math.lengthsq(origin + (direction * along) - centre) <= radius * radius;
        }

        // The line in an instance's own frame against its command's box, a little larger than it is.
        private static bool BoxMeets(Bounds local, double3 o, double3 d, double reach, float minScale)
        {
            double3 c = (float3)local.center;
            double3 e = ((float3)local.extents * 1.1f) + (0.02f / math.max(minScale, 1e-6f));
            double lo = 0.0, hi = reach;
            for (int a = 0; a < 3; a++)
            {
                double oa = o[a] - c[a], da = d[a];
                if (math.abs(da) < 1e-12)
                {
                    if (math.abs(oa) > e[a]) return false;
                    continue;
                }

                double t0 = (-e[a] - oa) / da, t1 = (e[a] - oa) / da;
                if (t0 > t1) { double s = t0; t0 = t1; t1 = s; }
                if (t0 > lo) lo = t0;
                if (t1 < hi) hi = t1;
                if (lo > hi) return false;
            }

            return true;
        }

        // Moeller-Trumbore from either face: t along the (unnormalized) direction, the barycentric u, v, and whether the line comes from the back of the winding.
        private static bool Triangle(double3 o, double3 d, double3 p0, double3 p1, double3 p2, out double t, out double u, out double v, out bool back)
        {
            t = u = v = 0.0;
            back = false;
            double3 e1 = p1 - p0, e2 = p2 - p0;
            double3 h = math.cross(d, e2);
            double det = math.dot(e1, h);
            double scale = math.lengthsq(e1) * math.lengthsq(e2) * math.lengthsq(d);
            if (!(det * det > 1e-24 * scale)) return false;   // parallel, or a triangle of no area
            double inv = 1.0 / det;
            double3 s = o - p0;
            u = math.dot(s, h) * inv;
            if (u < -1e-9 || u > 1.0 + 1e-9) return false;
            double3 q = math.cross(s, e1);
            v = math.dot(d, q) * inv;
            if (v < -1e-9 || u + v > 1.0 + 1e-9) return false;
            t = math.dot(e2, q) * inv;
            back = det < 0.0;
            return true;
        }

        private static float ClipMin(in VpInstanceClip clip, float3 world)
        {
            float least = float.PositiveInfinity;
            for (int i = 0; i < clip.PlaneCount; i++)
            {
                Vector4 p = clip.SignedPlane(i);
                float distance = (p.x * world.x) + (p.y * world.y) + (p.z * world.z) + p.w;
                if (distance < least) least = distance;
            }

            return least;
        }

        private static float4[] Planes(in VpInstanceClip clip)
        {
            var planes = new float4[clip.PlaneCount];
            for (int i = 0; i < planes.Length; i++) planes[i] = (Vector4)clip.SignedPlane(i);
            return planes;
        }

        private static SlotData SlotOf(VpLogicalCutDisplay display, int slot)
        {
            if (s_slots.TryGetValue(slot, out SlotData known)) return known;
            var data = new SlotData { slot = slot };
            display.TryGetCommandSlot(slot, out data.command, out data.startInstance, out bool _);
            display.TryGetCommandSlotMaterialForDiagnosis(slot, out data.material, out data.provisional);
            data.hasOwner = s_owners.TryGetValue(slot, out data.owner);
            s_slots[slot] = data;
            return data;
        }

        private static bool Read(VpLogicalCutDisplay display, SlotData data)
        {
            if (data.tried) return data.read;
            data.tried = true;
            data.read = display.TryCopyRangeIndicesForDiagnosis(data.command.range, data.hasOwner, data.owner.geometry, ref data.indices, out data.source, out string failure);
            data.failure = failure ?? "";
            if (data.read && data.hasOwner && data.owner.gpuRegion && data.command.range.vertexCount > 0)
            {
                // The storage does not hold these vertices yet (the GPU bakes them): what is drawn is the vertex buffer's.
                var gpu = new VpRenderVertex[data.command.range.vertexCount];
                if (display.TryReadGpuVerticesForDiagnosis(data.command.range.vertexStart, gpu.Length, gpu, out string why)) data.gpuVertices = gpu;
                else data.failure += (data.failure.Length > 0 ? "; " : "") + "the GPU-baked region's vertices were not read (" + why + ")";
            }

            return data.read;
        }

        private static bool Vertex(SlotData data, VpRenderVertex* pool, int poolLength, uint id, out VpRenderVertex vertex)
        {
            if (data.gpuVertices != null)
            {
                long local = (long)id - data.command.range.vertexStart;
                if (local >= 0 && local < data.gpuVertices.Length)
                {
                    vertex = data.gpuVertices[local];
                    return true;
                }
            }

            if (id < (uint)poolLength)
            {
                vertex = pool[id];
                return true;
            }

            vertex = default;
            return false;
        }

        // Every triangle of one instance the line meets within the reach, added as mesh hits.
        private static void Search(VpLogicalCutDisplay display, SlotData data, int instance, in Matrix4x4 transform, in VpInstanceClip clip, float3 origin, float3 direction, float reach,
            VpRenderVertex* pool, int poolLength, FacePickSample counts, List<Hit> into)
        {
            float det = transform.determinant;
            if (!(math.abs(det) > 1e-20f) || float.IsNaN(det) || float.IsInfinity(det)) return;
            Matrix4x4 inverse = transform.inverse;
            double3 o = (float3)inverse.MultiplyPoint3x4(origin);
            double3 d = (float3)inverse.MultiplyVector(direction);
            float sx = math.length(new float3(transform.m00, transform.m10, transform.m20)), sy = math.length(new float3(transform.m01, transform.m11, transform.m21)), sz = math.length(new float3(transform.m02, transform.m12, transform.m22));
            if (!BoxMeets(data.command.localBounds, o, d, reach, math.min(sx, math.min(sy, sz)))) return;
            if (counts != null) counts.boxed++;
            if (!Read(display, data)) return;
            int count = data.command.range.indexCount - (data.command.range.indexCount % 3);
            uint[] indices = data.indices;
            for (int i = 0; i < count; i += 3)
            {
                if (!Vertex(data, pool, poolLength, indices[i], out VpRenderVertex a) || !Vertex(data, pool, poolLength, indices[i + 1], out VpRenderVertex b)
                    || !Vertex(data, pool, poolLength, indices[i + 2], out VpRenderVertex c))
                {
                    continue;
                }

                if (counts != null) counts.triangles++;
                if (!Triangle(o, d, (float3)a.position, (float3)b.position, (float3)c.position, out double t, out double u, out double v, out bool back)) continue;
                if (!(t > 0.0) || t > reach) continue;
                float3 world = origin + (direction * (float)t);
                float least = ClipMin(in clip, world);
                // A mirroring placement turns the winding over: the face met is said in the world's sense.
                into.Add(new Hit { t = t, u = u, v = v, kind = FacePick.KindMesh, slot = data.slot, instance = instance, triangle = i / 3, cap = -1, fan = -1, clipped = least < -FacePick.ClipTolerance, back = det < 0f ? !back : back, clipMin = least });
            }
        }

        // ---- one pick ----------------------------------------------------------------------------------------------------

        /// <summary>
        /// Searches what <paramref name="world"/>'s display draws along the line and hands the pick's record to <paramref name="sink"/>:
        /// true with the record (also where nothing was met -- a pick of nothing is a record, its <see cref="FacePickSample.failure"/>
        /// saying so); false with <paramref name="failure"/> where there is no world to read or no room in the queue.
        /// <paramref name="done"/> is told once by the writer for the record's item, and -- where the piece's own save was made -- once
        /// for each of its <see cref="NpcPiecePickCopy.ItemsPerPick"/> items; <paramref name="items"/> is how many times in all.
        /// The pictures are the caller's (their names are in the record).
        /// </summary>
        internal static bool TryPick(CutWorldRoot world, SpatialSplitCaptureSink sink, int pick, string codeVersion, in Aim aim, Dictionary<NpcVertexBones, (string file, string hash)> models,
            Action<bool, string> done, out FacePickSample sample, out int items, out string failure)
        {
            sample = null;
            items = 0;
            failure = null;
            VpLogicalCutDisplay display = world != null ? world.Display : null;
            VpCpuGeometryStorage storage = world != null ? world.Storage : null;
            LogicalCutLedger ledger = world != null ? world.Ledger : null;
            if (display == null || display.IsDisposed || storage == null || ledger == null || sink == null) { failure = "no cut world with a display, or no sink"; return false; }
            CutSpatialSplit split = world.SpatialSplit;
            long captureWorld = split != null ? split.CaptureWorld : 0;
            var s = new FacePickSample
            {
                sessionId = sink.sessionId, codeVersion = codeVersion ?? "", pick = pick, frame = Time.frameCount, realtimeSeconds = Time.realtimeSinceStartupAsDouble,
                utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), world = captureWorld, ledgerRevision = ledger.Revision, displayGeneration = display.AdoptedGenerationForDiagnosis,
                bodyRoute = world.BodyRoute ?? "", graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                aimed = aim.aimed, hasCamera = aim.hasCamera, stereo = aim.stereo, aimOrigin = aim.origin, aimDirection = aim.direction, cameraPosition = aim.cameraPosition, reach = aim.reach,
                cameraRotation = aim.cameraRotation, worldToCamera = aim.worldToCamera, projection = aim.projection, pixelWidth = aim.pixelWidth, pixelHeight = aim.pixelHeight,
                imageBefore = FacePickSample.ImageName(captureWorld, pick, "before"), imageAfter = FacePickSample.ImageName(captureWorld, pick, "after"),
                commandEnd = display.DrawCommandEnd, instanceEnd = display.DrawInstanceEnd,
            };

            try
            {
                s_owners.Clear();
                s_slots.Clear();
                s_hits.Clear();
                if (!aim.aimed)
                {
                    s.failure = "the left controller is not tracked: nothing was searched";
                }
                else
                {
                    display.CopySlotOwnersForDiagnosis(s_owners);
                    NativeArray<VpRenderVertex>.ReadOnly vertices = storage.Vertices;
                    VpRenderVertex* pool = (VpRenderVertex*)vertices.GetUnsafeReadOnlyPtr();
                    int poolLength = vertices.Length;
                    SearchDraws(display, in aim, pool, poolLength, s);
                    SearchCaps(display, in aim, s);
                    Choose(world, display, storage, in aim, pool, poolLength, s);
                    s.unityRenderers = UnityRenderers(in aim);
                    if (s.candidates.Count == 0) s.failure = "nothing the display draws was met within " + aim.reach.ToString("R", CultureInfo.InvariantCulture) + " m along the line";
                }

                if (aim.camera != null && display.TryGetCameraStencil(aim.camera, out VpStencilPreparation preparation, out VpStencilCameraCounts stencil))
                {
                    s.hasStencil = true;
                    s.stencilPreparedNow = stencil.preparedNow;
                    s.stencilOutcome = preparation.outcome.ToString();
                    s.stencilCapRecords = preparation.capRecords;
                    s.stencilEmptyCaps = preparation.emptyCaps;
                    s.stencilHiddenCaps = preparation.hiddenCaps;
                    s.stencilJobs = preparation.jobs;
                    s.stencilVolumeGroups = preparation.volumeGroups;
                }

                // The piece's own save (the first candidate's piece, where it is an NPC's and reads as an issue's input does), under the same number.
                FacePickCandidate first = s.candidates.Count > 0 ? s.candidates[0] : null;
                FacePickFragmentState firstState = first != null && first.stateRef >= 0 ? s.states[first.stateRef] : null;
                bool pieceHanded = false;
                if (firstState != null && firstState.npc && split != null)
                {
                    s.pieceAttempted = true;
                    s.pieceFragment = firstState.fragment;
                    var pieceAim = new NpcPiecePickCopy.Aim { origin = aim.origin, direction = aim.direction, hitWorld = first.hitWorld, hitDistance = first.distance, hitCollider = "(face pick: no collider)" };
                    if (NpcPiecePickCopy.TryPick(world, split, sink, pick, codeVersion, new LogicalFragmentId(firstState.fragment), in pieceAim, models ?? new Dictionary<NpcVertexBones, (string file, string hash)>(), done,
                            out NpcPiecePickSample piece, out string pieceFailure))
                    {
                        pieceHanded = true;
                        s.pieceSaved = true;
                        s.pieceSample = piece.sampleFile;
                        s.pieceBones = piece.bonesFile;
                        s.pieceRecord = NpcPiecePickSample.FileName(piece.world, piece.pick);
                    }
                    else
                    {
                        s.pieceFailure = pieceFailure ?? "";
                    }
                }

                string name = FacePickSample.FileName(captureWorld, pick);
                byte[] content = s.Make(out string hash, out long raw);
                var item = new SpatialSplitBoneItem
                {
                    file = name, content = content, index = "faces.jsonl", line = s.Line(name, hash, raw, content.Length), bytes = content.Length + SpatialSplitCaptureResult.Overhead, done = done,
                };
                s.sha256 = hash;
                if (!sink.TryReserve(item.bytes, false))
                {
                    // The piece's items, if handed, are the writer's already: their callbacks come, and the caller counts the pick failed at once.
                    failure = "no room in the queue for the face pick's record (" + sink.LastRefusal + ")" + (pieceHanded ? "; the piece's own save was handed already" : "");
                    return false;
                }

                sink.HandBone(item);
                items = 1 + (pieceHanded ? NpcPiecePickCopy.ItemsPerPick : 0);
                sample = s;
                return true;
            }
            catch (Exception e)
            {
                failure = "the face pick threw: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                s_owners.Clear();
                s_slots.Clear();
                s_hits.Clear();
            }
        }

        // Unity's own renderers along the line, by their world boxes: at most twelve, nearest first. The collection's own line and mark are left out.
        private static string[] UnityRenderers(in Aim aim)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var found = new List<(float distance, string text)>();
            var ray = new Ray(aim.origin, aim.direction);
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                Bounds box = renderer.bounds;
                if (!box.IntersectRay(ray, out float distance) || distance > aim.reach) continue;
                string path = renderer.name;
                Transform at = renderer.transform.parent;
                for (int depth = 0; depth < 4 && at != null; depth++, at = at.parent) path = at.name + "/" + path;
                if (path.Contains("Zantetsu piece probe") || path.Contains("Zantetsu face")) continue;
                Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                var materials = new StringBuilder();
                foreach (Material material in renderer.sharedMaterials) materials.Append(materials.Length > 0 ? ", " : "").Append(material != null ? material.name : "(none)");
                distance = math.max(0f, distance);
                found.Add((distance, renderer.GetType().Name + " " + path + " at " + distance.ToString("F3", inv) + " m (its box), mesh " + (mesh != null ? mesh.name + " of " + mesh.vertexCount + " vertices" : "(none)")
                    + ", materials [" + materials + "], layer " + renderer.gameObject.layer + ", shadows " + renderer.shadowCastingMode + ", box centre (" + box.center.x.ToString("F2", inv) + ", " + box.center.y.ToString("F2", inv) + ", "
                    + box.center.z.ToString("F2", inv) + ") extents (" + box.extents.x.ToString("F2", inv) + ", " + box.extents.y.ToString("F2", inv) + ", " + box.extents.z.ToString("F2", inv) + ")"));
            }

            found.Sort((a, b) => a.distance.CompareTo(b.distance));
            var lines = new string[math.min(found.Count, 12)];
            for (int i = 0; i < lines.Length; i++) lines[i] = found[i].text;
            return lines;
        }

        private static void SearchDraws(VpLogicalCutDisplay display, in Aim aim, VpRenderVertex* pool, int poolLength, FacePickSample s)
        {
            int end = display.DrawCommandEnd;
            for (int slot = 0; slot < end; slot++)
            {
                if (!display.TryGetCommandSlot(slot, out VpIndirectCommand command, out int start, out bool free)) continue;
                s.slots++;
                if (free || command.instanceCount <= 0)
                {
                    s.freeSlots++;
                    continue;
                }

                for (int k = 0; k < command.instanceCount; k++)
                {
                    if (!display.TryGetInstanceRecordForDiagnosis(start + k, out Matrix4x4 transform, out VpInstanceClip clip, out _))
                    {
                        s.unreadRecords++;
                        continue;
                    }

                    s.instances++;
                    if (!SphereMeets(in transform, command.localBounds, aim.origin, aim.direction, aim.reach)) continue;
                    s.coarse++;
                    SlotData data = SlotOf(display, slot);
                    Search(display, data, k, in transform, in clip, aim.origin, aim.direction, aim.reach, pool, poolLength, s, s_hits);
                }
            }

            foreach (SlotData data in s_slots.Values)
            {
                if (!data.tried) continue;
                if (data.read) s.slotsRead++; else s.slotsUnread++;
            }

            for (int i = 0; i < s_hits.Count; i++)
            {
                s.meshHits++;
                if (s_hits[i].clipped) s.meshClipRejected++;
            }
        }

        private static void SearchCaps(VpLogicalCutDisplay display, in Aim aim, FacePickSample s)
        {
            int caps = display.CapRecordCount;
            s.capRecords = caps;
            double3 o = aim.origin, d = aim.direction;
            for (int c = 0; c < caps; c++)
            {
                if (!display.TryGetCapRecord(c, out LogicalCutCapRecord record) || record.vertexCount < 3) continue;
                if (!display.TryGetCapVertex(c, 0, out Vector3 p0)) continue;
                double best = double.PositiveInfinity;
                int fan = -1;
                bool back = false;
                for (int k = 1; k + 1 < record.vertexCount; k++)
                {
                    if (!display.TryGetCapVertex(c, k, out Vector3 p1) || !display.TryGetCapVertex(c, k + 1, out Vector3 p2)) break;
                    if (!Triangle(o, d, (float3)p0, (float3)p1, (float3)p2, out double t, out _, out _, out bool _)) continue;
                    if (!(t > 0.0) || t > aim.reach || t >= best) continue;
                    best = t;
                    fan = k - 1;
                    back = math.dot((float3)record.outwardNormal, aim.direction) > 0f;   // the cap's own outward side, not the fan's winding
                }

                if (fan < 0) continue;
                s.capHits++;
                s_hits.Add(new Hit { t = best, kind = FacePick.KindCap, slot = -1, instance = -1, triangle = -1, cap = c, fan = fan, clipped = false, back = back, clipMin = float.PositiveInfinity });
            }
        }

        // The nearest few, each made into a candidate with everything it names.
        private static void Choose(CutWorldRoot world, VpLogicalCutDisplay display, VpCpuGeometryStorage storage, in Aim aim, VpRenderVertex* pool, int poolLength, FacePickSample s)
        {
            s_hits.Sort((a, b) => a.t.CompareTo(b.t));
            int kept = 0, clipped = 0, gpuChecks = 0;
            long budget = FacePick.GeometryBudget;
            var rfIndex = new Dictionary<int, int>();
            var geometryIndex = new Dictionary<long, int>();
            // The ones the clip keeps first, nearest first; then the ones it leaves out, nearest first.
            var chosen = new List<Hit>(FacePick.MaxKept + FacePick.MaxClipped);
            for (int i = 0; i < s_hits.Count && kept < FacePick.MaxKept; i++)
            {
                if (s_hits[i].clipped) continue;
                chosen.Add(s_hits[i]);
                kept++;
            }

            for (int i = 0; i < s_hits.Count && clipped < FacePick.MaxClipped; i++)
            {
                if (!s_hits[i].clipped) continue;
                chosen.Add(s_hits[i]);
                clipped++;
            }

            for (int i = 0; i < chosen.Count; i++)
            {
                Hit hit = chosen[i];
                var c = new FacePickCandidate
                {
                    kind = hit.kind, rank = s.candidates.Count, clipRejected = hit.clipped, backFace = hit.back, distance = (float)hit.t, hitWorld = aim.origin + (aim.direction * (float)hit.t),
                };
                int fragment = 0;
                if (hit.kind == FacePick.KindMesh) fragment = FillMesh(display, storage, in hit, c, pool, poolLength, ref gpuChecks);
                else fragment = FillCap(display, in aim, in hit, c, pool, poolLength);
                if (aim.hasCamera)
                {
                    float3 toEye = aim.cameraPosition - c.hitWorld;
                    c.eyeFacing = math.lengthsq(toEye) > 0f ? math.dot(c.normalWorld, math.normalize(toEye)) : float.NaN;
                }

                int rf = hit.kind == FacePick.KindMesh ? c.renderFragment : c.capRenderFragment;
                if (rf >= 0)
                {
                    if (!rfIndex.TryGetValue(rf, out int at))
                    {
                        at = s.renderFragments.Count;
                        s.renderFragments.Add(ReadRenderFragment(display, rf));
                        rfIndex[rf] = at;
                    }

                    c.renderFragmentRef = at;
                    FacePickRenderFragment read = s.renderFragments[at];
                    AddState(world, s, read.root);
                    AddState(world, s, read.registrationRoot);
                    for (int b = 0; b < read.branchFragment.Length; b++) AddState(world, s, read.branchFragment[b]);
                    if (fragment <= 0) fragment = read.root;
                }

                if (hit.kind == FacePick.KindMesh)
                {
                    AddState(world, s, c.ownerFragment);
                    AddState(world, s, c.sideSource);
                    AddState(world, s, c.sideFragment);
                    SlotData data = s_slots[c.slot];
                    long key = data.hasOwner ? (((long)data.owner.list) << 40) | (uint)data.owner.index : (1L << 62) | (uint)c.slot;
                    if (!geometryIndex.TryGetValue(key, out int g))
                    {
                        g = s.geometries.Count;
                        s.geometries.Add(CopyGeometry(display, storage, data, pool, poolLength, ref budget));
                        geometryIndex[key] = g;
                    }

                    c.geometryRef = g;
                }
                else
                {
                    AddState(world, s, c.capSource);
                    AddState(world, s, c.capFragment);
                    // The geometry the cap's body is drawn from: the registration that holds its render fragment.
                    if (TryOwnerOfRenderFragment(c.capRenderFragment, out VpDrawSlotOwner owner))
                    {
                        long key = (((long)owner.list) << 40) | (uint)owner.index;
                        if (!geometryIndex.TryGetValue(key, out int g))
                        {
                            g = s.geometries.Count;
                            s.geometries.Add(CopyGeometry(display, storage, SlotOf(display, owner.commandStart), pool, poolLength, ref budget));
                            geometryIndex[key] = g;
                        }

                        c.geometryRef = g;
                    }
                }

                c.stateRef = AddState(world, s, fragment);
                s.candidates.Add(c);
            }
        }

        private static bool TryOwnerOfRenderFragment(int renderFragment, out VpDrawSlotOwner found)
        {
            found = default;
            bool any = false;
            foreach (VpDrawSlotOwner owner in s_owners.Values)
            {
                if (renderFragment < owner.firstRenderFragmentShown || renderFragment >= owner.firstRenderFragmentShown + owner.slotRenderFragments) continue;
                if (any && (int)owner.list >= (int)found.list) continue;   // the shown registration before the others
                found = owner;
                any = true;
            }

            return any;
        }

        private static int FillMesh(VpLogicalCutDisplay display, VpCpuGeometryStorage storage, in Hit hit, FacePickCandidate c, VpRenderVertex* pool, int poolLength, ref int gpuChecks)
        {
            SlotData data = s_slots[hit.slot];
            VpGeometryRange range = data.command.range;
            c.slot = hit.slot;
            c.instance = hit.instance;
            c.startInstance = data.startInstance;
            c.record = data.startInstance + hit.instance;
            c.instanceCount = data.command.instanceCount;
            c.vertexStart = range.vertexStart; c.vertexCount = range.vertexCount; c.indexStart = range.indexStart; c.indexCount = range.indexCount;
            c.clipRoute = data.command.clipRoute;
            c.provisional = data.provisional;
            c.material = data.material != null ? data.material.name : "";
            c.shader = data.material != null && data.material.shader != null ? data.material.shader.name : "";
            c.indexSource = data.source;
            c.indexFailure = data.failure;
            c.triangle = hit.triangle;
            c.indexAt = range.indexStart + (3 * hit.triangle);
            c.i0 = data.indices[3 * hit.triangle]; c.i1 = data.indices[(3 * hit.triangle) + 1]; c.i2 = data.indices[(3 * hit.triangle) + 2];
            Vertex(data, pool, poolLength, c.i0, out VpRenderVertex a);
            Vertex(data, pool, poolLength, c.i1, out VpRenderVertex b);
            Vertex(data, pool, poolLength, c.i2, out VpRenderVertex v);
            c.triangleVertices = new[] { a, b, v };
            c.capMarked = a.u == 247 && a.v == 247 && b.u == 247 && b.v == 247 && v.u == 247 && v.v == 247;
            display.TryGetInstanceRecordForDiagnosis(c.record, out Matrix4x4 transform, out VpInstanceClip clip, out LogicalCutDisplaySide side);
            c.transform = transform;
            c.w0 = transform.MultiplyPoint3x4(a.position); c.w1 = transform.MultiplyPoint3x4(b.position); c.w2 = transform.MultiplyPoint3x4(v.position);
            c.barycentric = new float3((float)(1.0 - hit.u - hit.v), (float)hit.u, (float)hit.v);
            c.hitLocal = ((float3)a.position * c.barycentric.x) + ((float3)b.position * c.barycentric.y) + ((float3)v.position * c.barycentric.z);
            float3 n = math.cross(c.w1 - c.w0, c.w2 - c.w0);
            c.normalWorld = math.lengthsq(n) > 0f ? math.normalize(n) : float3.zero;
            c.clipPlanes = Planes(in clip);
            c.clipMin = hit.clipMin;
            c.sideSource = side.source.value; c.sideOperation = side.operation.value; c.sideFragment = side.fragment.value; c.sideRenderFragment = side.renderFragment;
            c.side = side.side; c.sidePublished = side.published; c.sideFixed = side.fixedByAnchors;
            if (data.hasOwner)
            {
                VpDrawSlotOwner o = data.owner;
                c.ownerList = (int)o.list; c.ownerIndex = o.index; c.ownerCommand = o.command; c.ownerFragment = o.fragment.value;
                c.ownerRenderFragmentsShown = o.renderFragmentsShown; c.ownerFirstRenderFragmentShown = o.firstRenderFragmentShown; c.ownerInstanceStart = o.instanceStart;
                c.ownerInstanceStride = o.instanceStride; c.ownerSlotRenderFragments = o.slotRenderFragments;
                c.ownerSplitShown = o.splitShown; c.ownerAwaitingShown = o.awaitingShown; c.ownerSlotsClipped = o.slotsClipped; c.ownerGpuRegion = o.gpuRegion;
                c.renderFragment = hit.instance < o.slotRenderFragments && o.list == VpDrawSlotOwnerList.Shown ? o.firstRenderFragmentShown + hit.instance : -1;
                if (storage.TryGetSubmeshes(o.geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes) && submeshes.Length == o.commands) c.materialIndex = submeshes[o.command].materialIndex;
            }

            // What the GPU holds of this triangle, for the first few: its three indices and its three vertices, compared.
            if (gpuChecks < 3 && !c.clipRejected)
            {
                gpuChecks++;
                var gpuIndices = new uint[3];
                var gpuVertex = new VpRenderVertex[1];
                if (!display.TryReadGpuIndicesForDiagnosis(c.indexAt, 3, gpuIndices, out string why))
                {
                    c.gpuCheck = 3;
                    c.gpuCheckDetail = why;
                }
                else if (gpuIndices[0] != c.i0 || gpuIndices[1] != c.i1 || gpuIndices[2] != c.i2)
                {
                    c.gpuCheck = 2;
                    c.gpuCheckDetail = "the GPU's indices are " + gpuIndices[0] + ", " + gpuIndices[1] + ", " + gpuIndices[2];
                }
                else
                {
                    c.gpuCheck = 1;
                    for (int k = 0; k < 3 && c.gpuCheck == 1; k++)
                    {
                        uint id = k == 0 ? c.i0 : k == 1 ? c.i1 : c.i2;
                        VpRenderVertex cpu = c.triangleVertices[k];
                        if (!display.TryReadGpuVerticesForDiagnosis((int)id, 1, gpuVertex, out why))
                        {
                            c.gpuCheck = 3;
                            c.gpuCheckDetail = why;
                        }
                        else if (UnsafeUtility.MemCmp(UnsafeUtility.AddressOf(ref gpuVertex[0]), UnsafeUtility.AddressOf(ref cpu), UnsafeUtility.SizeOf<VpRenderVertex>()) != 0)
                        {
                            c.gpuCheck = 2;
                            Vector3 p = gpuVertex[0].position;
                            c.gpuCheckDetail = "vertex " + id + " is at (" + p.x.ToString("R", CultureInfo.InvariantCulture) + ", " + p.y.ToString("R", CultureInfo.InvariantCulture) + ", " + p.z.ToString("R", CultureInfo.InvariantCulture) + ") on the GPU";
                        }
                    }
                }
            }

            return side.published && side.fragment.IsSet ? side.fragment.value : data.hasOwner ? data.owner.fragment.value : side.source.value;
        }

        private static int FillCap(VpLogicalCutDisplay display, in Aim aim, in Hit hit, FacePickCandidate c, VpRenderVertex* pool, int poolLength)
        {
            display.TryGetCapRecord(hit.cap, out LogicalCutCapRecord record);
            c.capIndex = hit.cap;
            c.capFan = hit.fan;
            c.capVertexCount = record.vertexCount;
            c.capSource = record.source.value; c.capOperation = record.operation.value; c.capFragment = record.fragment.value; c.capRenderFragment = record.renderFragment;
            c.capSide = record.side; c.capPublished = record.published; c.capFixed = record.fixedByAnchors;
            c.capPlane = (Vector4)record.worldPlane;
            c.capOutward = record.outwardNormal;
            c.normalWorld = record.outwardNormal;
            c.capPolygon = new float3[record.vertexCount];
            for (int k = 0; k < record.vertexCount; k++)
            {
                display.TryGetCapVertex(hit.cap, k, out Vector3 p);
                c.capPolygon[k] = p;
            }

            if (display.TryGetCapJobForDiagnosis(hit.cap, out VpCapJob job, out int jobIndex, out bool last))
            {
                c.capHasJob = true;
                c.capJob = jobIndex;
                c.capColour = job.colour;
                c.capVolumeGroup = job.volumeGroup;
                c.capLastColour = last;
            }

            // The stencil lets a cap through where the eye's ray is inside the cap's body at the cap: the body's faces the ray crosses
            // beyond the point on the cap's kept side, counted over every command of the registration that holds the render fragment (an
            // odd count: inside). An estimate from the centre eye, for a cap that faces it; the passes themselves are not read.
            if (aim.hasCamera && TryOwnerOfRenderFragment(record.renderFragment, out VpDrawSlotOwner owner))
            {
                float3 toPoint = c.hitWorld - aim.cameraPosition;
                float length = math.length(toPoint);
                if (length > 1e-5f)
                {
                    float3 direction = toPoint / length;
                    int k = record.renderFragment - owner.firstRenderFragmentShown;
                    var crossings = new List<Hit>();
                    for (int command = 0; command < owner.commands; command++)
                    {
                        SlotData data = SlotOf(display, owner.commandStart + command);
                        if (!display.TryGetInstanceRecordForDiagnosis(data.startInstance + k, out Matrix4x4 transform, out VpInstanceClip clip, out _)) continue;
                        Search(display, data, k, in transform, in clip, aim.cameraPosition, direction, length + 1000f, pool, poolLength, null, crossings);
                    }

                    float margin = math.max(1e-4f, length * 1e-5f);
                    for (int i = 0; i < crossings.Count; i++)
                    {
                        if (crossings[i].t > length + margin)
                        {
                            // The stencil's volume is the body on the cap's own kept side alone: a face on the other side of the cap's plane is none of it.
                            float3 at = aim.cameraPosition + (direction * (float)crossings[i].t);
                            if (math.dot(c.capOutward, at - c.hitWorld) <= 0f) c.eyeCrossingsBeyond++;
                        }
                        else if (crossings[i].t < length - margin && !crossings[i].clipped) c.eyeCrossingsBefore++;
                    }

                    c.eyeEstimated = true;
                    c.eyeInside = (c.eyeCrossingsBeyond & 1) == 1;
                }
            }

            return record.published && record.fragment.IsSet ? record.fragment.value : record.source.value;
        }

        private static FacePickRenderFragment ReadRenderFragment(VpLogicalCutDisplay display, int index)
        {
            var r = new FacePickRenderFragment { index = index };
            if (!display.TryGetRenderFragment(index, out VpMultiCutRenderFragment rf)) return r;
            r.found = true;
            r.registration = rf.registration;
            if (display.TryGetRegistrationRootForDiagnosis(rf.registration, out LogicalFragmentId registrationRoot)) r.registrationRoot = registrationRoot.value;
            r.root = rf.root.value;
            r.rootPendingSide = rf.rootPendingSide;
            r.aggregated = rf.aggregated;
            r.branchStart = rf.branchStart; r.branchCount = rf.branchCount; r.conditionStart = rf.conditionStart; r.conditionCount = rf.conditionCount; r.capStart = rf.capStart; r.capCount = rf.capCount;
            r.boundsCentre = rf.localBounds.center; r.boundsExtents = rf.localBounds.extents;
            r.geometryLocalToWorld = rf.geometryLocalToWorld;
            r.clipPlanes = Planes(rf.clip);
            int branches = math.clamp(rf.branchCount, 0, 256);
            r.branchFragment = new int[branches]; r.branchPendingOperation = new int[branches]; r.branchSelected = new int[branches]; r.branchCandidates = new int[branches]; r.branchPendingSide = new float[branches];
            var candidateBranch = new List<int>(); var operation = new List<int>(); var state = new List<int>(); var pending = new List<int>(); var requires = new List<int>();
            var side = new List<float>(); var plane = new List<float4>();
            for (int b = 0; b < branches; b++)
            {
                if (!display.TryGetBranch(rf.branchStart + b, out VpMultiCutBranch branch)) continue;
                r.branchFragment[b] = branch.fragment.value;
                r.branchPendingOperation[b] = branch.pendingOperation.value;
                r.branchPendingSide[b] = branch.pendingSide;
                r.branchSelected[b] = branch.selectedCount;
                r.branchCandidates[b] = branch.candidateCount;
                for (int k = 0; k < branch.candidateCount && k < 64; k++)
                {
                    if (!display.TryGetCandidate(branch.candidateStart + k, out VpClipCandidate candidate, out VpClipSelectionState selection)) continue;
                    candidateBranch.Add(b);
                    operation.Add(candidate.boundary.face.operation.value);
                    side.Add(candidate.boundary.side);
                    plane.Add(candidate.plane);
                    pending.Add(candidate.pending ? 1 : 0);
                    requires.Add(candidate.requires.face.operation.value);
                    state.Add((int)selection);
                }
            }

            r.candidateBranch = candidateBranch.ToArray(); r.candidateOperation = operation.ToArray(); r.candidateState = state.ToArray(); r.candidatePending = pending.ToArray();
            r.candidateRequires = requires.ToArray(); r.candidateSide = side.ToArray(); r.candidatePlane = plane.ToArray();
            return r;
        }

        // The whole drawn geometry of the registration that holds the slot (every command of it), or of the one slot where none does.
        private static FacePickGeometry CopyGeometry(VpLogicalCutDisplay display, VpCpuGeometryStorage storage, SlotData first, VpRenderVertex* pool, int poolLength, ref long budget)
        {
            var g = new FacePickGeometry { vertexStride = UnsafeUtility.SizeOf<VpRenderVertex>(), fragment = 0, ownerIndex = -1 };
            int commands = 1, commandStart = first.slot;
            NativeArray<VpGeometrySubmesh>.ReadOnly submeshes = default;
            bool hasSubmeshes = false;
            if (first.hasOwner)
            {
                VpDrawSlotOwner o = first.owner;
                g.ownerList = (int)o.list; g.ownerIndex = o.index; g.fragment = o.fragment.value; g.gpuRegion = o.gpuRegion;
                g.hasIdentity = true;
                g.identity = NpcPieceGeometryIdentity.Of(in o.geometry);
                g.boundsCentre = o.localBounds.center; g.boundsExtents = o.localBounds.extents;
                g.objectToWorld = o.objectToWorld; g.lineageToGeometryLocal = o.lineageToGeometryLocal;
                if (storage.TryGetIndexState(o.geometry.indexRange, out VpIndexRangeState state, out g.indexStart, out g.indexCount)) g.indexState = state.ToString();
                else g.indexState = "not a current range of the storage";
                commands = o.commands;
                commandStart = o.commandStart;
                hasSubmeshes = storage.TryGetSubmeshes(o.geometry, out submeshes) && submeshes.Length == o.commands;
            }

            g.commandSlot = new int[commands]; g.commandVertexStart = new int[commands]; g.commandVertexCount = new int[commands]; g.commandIndexStart = new int[commands];
            g.commandIndexCount = new int[commands]; g.commandInstances = new int[commands]; g.commandSource = new int[commands]; g.commandMaterialIndex = new int[commands];
            g.commandOffset = new int[commands]; g.commandMaterial = new string[commands]; g.commandShader = new string[commands]; g.commandFailure = new string[commands];
            var datas = new SlotData[commands];
            long indexTotal = 0;
            for (int c = 0; c < commands; c++)
            {
                SlotData data = SlotOf(display, commandStart + c);
                datas[c] = data;
                bool read = Read(display, data);
                g.commandSlot[c] = data.slot;
                g.commandVertexStart[c] = data.command.range.vertexStart; g.commandVertexCount[c] = data.command.range.vertexCount;
                g.commandIndexStart[c] = data.command.range.indexStart; g.commandIndexCount[c] = data.command.range.indexCount;
                g.commandInstances[c] = data.command.instanceCount;
                g.commandSource[c] = read ? data.source : 0;
                g.commandMaterialIndex[c] = hasSubmeshes ? submeshes[c].materialIndex : -1;
                g.commandMaterial[c] = data.material != null ? data.material.name : "";
                g.commandShader[c] = data.material != null && data.material.shader != null ? data.material.shader.name : "";
                g.commandFailure[c] = data.failure;
                g.commandOffset[c] = -1;
                if (read) indexTotal += data.command.range.indexCount;
            }

            // The room first: indices, and at most as many vertices as indices.
            long worst = indexTotal * (sizeof(uint) + sizeof(uint) + g.vertexStride + 1);
            if (worst > budget && indexTotal * sizeof(uint) > budget)
            {
                g.failure = "left out: its " + indexTotal + " indices are past what is left of the pick's " + FacePick.GeometryBudget + " bytes";
                return g;
            }

            var indices = new uint[indexTotal];
            int at = 0;
            for (int c = 0; c < commands; c++)
            {
                if (g.commandSource[c] == 0) continue;
                g.commandOffset[c] = at;
                Array.Copy(datas[c].indices, 0, indices, at, g.commandIndexCount[c]);
                at += g.commandIndexCount[c];
            }

            var ids = (uint[])indices.Clone();
            Array.Sort(ids);
            int unique = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                if (i == 0 || ids[i] != ids[i - 1]) ids[unique++] = ids[i];
            }

            long bytes = (indexTotal * sizeof(uint)) + ((long)unique * (sizeof(uint) + g.vertexStride + 1));
            if (bytes > budget)
            {
                g.failure = "left out: its " + bytes + " bytes are past what is left of the pick's " + FacePick.GeometryBudget;
                return g;
            }

            budget -= bytes;
            Array.Resize(ref ids, unique);
            var vertices = new VpRenderVertex[unique];
            var read2 = new byte[unique];
            for (int i = 0; i < unique; i++)
            {
                // A region the GPU bakes is read from whichever of the registration's commands holds the vertex.
                bool found = false;
                for (int c = 0; c < commands && !found; c++)
                {
                    if (datas[c].gpuVertices == null) continue;
                    long local = (long)ids[i] - datas[c].command.range.vertexStart;
                    if (local < 0 || local >= datas[c].gpuVertices.Length) continue;
                    vertices[i] = datas[c].gpuVertices[local];
                    found = true;
                }

                if (!found && !g.gpuRegion && ids[i] < (uint)poolLength)
                {
                    vertices[i] = pool[ids[i]];
                    found = true;
                }

                read2[i] = found ? (byte)1 : (byte)0;
            }

            g.indices = indices;
            g.vertexIds = ids;
            g.vertices = vertices;
            g.vertexRead = read2;
            return g;
        }

        private static int AddState(CutWorldRoot world, FacePickSample s, int fragment)
        {
            if (fragment <= 0) return -1;
            for (int i = 0; i < s.states.Count; i++) if (s.states[i].fragment == fragment) return i;
            s.states.Add(ReadState(world, fragment));
            return s.states.Count - 1;
        }

        /// <summary>What the world says of one fragment now; every part that cannot be read is said as not there, and a throw is kept as the failure.</summary>
        internal static FacePickFragmentState ReadState(CutWorldRoot world, int value)
        {
            var s = new FacePickFragmentState { fragment = value };
            try
            {
                var fragment = new LogicalFragmentId(value);
                LogicalCutLedger ledger = world.Ledger;
                if (ledger.TryGetFragmentState(fragment, out LogicalFragmentState state))
                {
                    s.known = true;
                    s.state = state.ToString();
                    s.currentTarget = ledger.IsCurrentTarget(fragment);
                    if (ledger.TryGetRoot(fragment, out LogicalFragmentId root)) { s.hasRoot = true; s.root = root.value; }
                    if (ledger.TryGetParent(fragment, out LogicalFragmentId parent))
                    {
                        s.hasParent = true;
                        s.parent = parent.value;
                        LogicalFragmentId at = fragment;
                        for (int depth = 0; depth < 4096 && ledger.TryGetParent(at, out LogicalFragmentId up); depth++)
                        {
                            s.generation++;
                            at = up;
                        }
                    }

                    if (ledger.TryGetOrigin(fragment, out CutOperationId origin, out float side))
                    {
                        s.hasOrigin = true;
                        s.originOperation = origin.value;
                        s.originSide = side;
                        if (ledger.TryGetOperation(origin, out LogicalCutOperation operation))
                        {
                            s.hasOperation = true;
                            s.operationSource = operation.source.value;
                            s.operationPositive = operation.positive.value;
                            s.operationNegative = operation.negative.value;
                            s.operationState = operation.state.ToString();
                            s.operationPlane = operation.plane;
                        }
                    }

                    if (ledger.TryGetActiveOperation(fragment, out CutOperationId active)) { s.hasActive = true; s.activeOperation = active.value; }
                    if (ledger.TryGetReplacingOperation(fragment, out CutOperationId replacing)) { s.hasReplacing = true; s.replacingOperation = replacing.value; }
                    if (ledger.TryGetSplitSource(fragment, out LogicalFragmentId source)) { s.hasSplitSource = true; s.splitSource = source.value; }
                    if (ledger.TryGetAnchorCount(fragment, out int anchors)) s.anchors = anchors;
                    s.fixedOwner = ledger.IsFixedOwner(fragment);
                }

                PhysicsOwnerRegistry owners = world.Owners;
                if (owners != null && owners.TryGet(fragment, out PhysicsFragmentOwner owner) && owner != null)
                {
                    s.ownerFound = true;
                    s.ownerWithdrawn = owner.IsWithdrawn;
                    s.ownerReleased = owner.IsReleased;
                    s.ownerDisplayOnly = owner.IsDisplayOnly;
                    s.ownerFused = owner.IsFused;
                    s.ownerHasRoot = owner.Root != null;
                    s.ownerHasBody = owner.Body != null;
                    s.ownerShapeLive = owner.Shape != null && !owner.Shape.IsFreed;
                    s.convexes = s.ownerShapeLive ? owner.Shape.ConvexCount : -1;
                    s.mass = owner.Mass;
                    if (owner.GeometryLocalToOwner != null) { s.hasGeometryToOwner = true; s.geometryToOwner = owner.GeometryLocalToOwner.Value; }
                    if (s.ownerHasRoot)
                    {
                        s.ownerName = owner.Root.name;
                        s.ownerLocalToWorld = owner.Root.transform.localToWorldMatrix;
                    }

                    if (s.ownerHasBody)
                    {
                        Rigidbody body = owner.Body;
                        s.bodyPosition = body.position;
                        s.bodyVelocity = body.linearVelocity;
                        s.bodyRotation = new float4(body.rotation.x, body.rotation.y, body.rotation.z, body.rotation.w);
                        s.bodyKinematic = body.isKinematic;
                        s.bodySleeping = body.IsSleeping();
                    }
                }

                s.provisionalUnderWay = owners != null && owners.TryGetProvisionalOf(fragment, out _);
                CutDag dag = world.Geometry;
                if (dag != null && dag.TryGetGeometry(fragment, out VpStoredGeometry geometry))
                {
                    s.dagGeometry = true;
                    s.dagIdentity = NpcPieceGeometryIdentity.Of(in geometry);
                }

                s.npc = world.NpcBones != null && world.NpcBones.Knows(fragment);
                CutSpatialSplit split = world.SpatialSplit;
                if (split != null)
                {
                    if (s.known) s.examine = split.Examine(fragment).ToString();
                    if (split.TryGetPieceNote(fragment, out CutSpatialSplit.PieceNote note))
                    {
                        s.hasNote = true;
                        s.noteAttempted = note.attempted;
                        s.noteBoneLed = note.boneLed;
                        s.noteFellBack = note.fellBack;
                        s.noteEndFrame = note.endFrame;
                        s.noteMadeFrame = note.madeFrame;
                        s.noteMadeFrom = note.madeFrom.value;
                        s.noteEnd = note.attempted ? note.end.ToString() : "";
                        s.noteMadeBy = note.madeBy ?? "";
                        s.noteBoneVerdict = note.bone.Entered ? note.bone.verdict.ToString() : "";
                        s.noteBoneReason = note.bone.reason ?? "";
                    }
                }
            }
            catch (Exception e)
            {
                s.failure = e.GetType().Name + ": " + e.Message;
            }

            return s;
        }

        /// <summary>A pick that could not be taken at all, as its faces.jsonl line (no file).</summary>
        internal static void HandFailure(SpatialSplitCaptureSink sink, long world, int pick, string failure)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var item = new SpatialSplitBoneItem
            {
                index = "faces.jsonl",
                line = "{\"pick\":" + pick + ",\"world\":" + world + ",\"frame\":" + Time.frameCount + ",\"seconds\":" + Time.realtimeSinceStartupAsDouble.ToString("R", inv)
                    + ",\"utc\":" + SpatialSplitCaptureSink.Json(DateTime.UtcNow.ToString("o", inv)) + ",\"failure\":" + SpatialSplitCaptureSink.Json(failure ?? "") + ",\"file\":\"\"}",
                bytes = SpatialSplitCaptureResult.Overhead,
            };
            if (sink.TryReserve(item.bytes, true)) sink.HandBone(item);
        }

        /// <summary>
        /// One picture of a pick as an item of the writer's (the PNG and its images.jsonl line, with the frame it was taken in): true where
        /// it was handed (<paramref name="done"/> is then told by the writer), false with why where the queue had no room.
        /// </summary>
        internal static bool TryHandImage(SpatialSplitCaptureSink sink, long world, int pick, string which, byte[] png, int width, int height, int frame, string how, Action<bool, string> done, out string why)
        {
            string name = FacePickSample.ImageName(world, pick, which);
            string hash;
            using (SHA256 sha = SHA256.Create()) hash = SpatialSplitHash.Hex(sha.ComputeHash(png));
            var item = new SpatialSplitBoneItem
            {
                file = name, content = png, index = "images.jsonl",
                line = "{\"pick\":" + pick + ",\"world\":" + world + ",\"which\":" + SpatialSplitCaptureSink.Json(which) + ",\"file\":" + SpatialSplitCaptureSink.Json(name) + ",\"sha256\":" + SpatialSplitCaptureSink.Json(hash)
                    + ",\"fileBytes\":" + png.Length + ",\"width\":" + width + ",\"height\":" + height + ",\"frame\":" + frame + ",\"how\":" + SpatialSplitCaptureSink.Json(how ?? "") + "}",
                bytes = png.Length + SpatialSplitCaptureResult.Overhead, done = done,
            };
            if (!sink.TryReserve(item.bytes, false))
            {
                why = "no room in the queue for the picture (" + sink.LastRefusal + ")";
                return false;
            }

            sink.HandBone(item);
            why = null;
            return true;
        }
    }

    /// <summary>
    /// A face collection's folder read back (FACE-PICK-1): every pick's record found, read whole and matched to its index line's hash,
    /// its pictures matched to theirs, and each candidate worked out again from what was saved alone -- the triangle placed by the saved
    /// transform and met by the saved line where the record says, its indices and vertices found in the saved geometry.
    /// </summary>
    internal static class FacePickSet
    {
        internal sealed class Pick
        {
            internal int number;
            internal string failure;   // the line's own failure where the pick has no file
            internal FacePickSample sample;
            internal string imageBefore, imageAfter;
            internal int imageBeforeFrame = -1, imageAfterFrame = -1;
            internal readonly List<string> problems = new List<string>();
        }

        internal static string Field(string line, string name)
        {
            string key = "\"" + name + "\":";
            int at = line.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;
            at += key.Length;
            if (at < line.Length && line[at] == '"')
            {
                var text = new StringBuilder();
                for (int i = at + 1; i < line.Length; i++)
                {
                    char ch = line[i];
                    if (ch == '\\' && i + 1 < line.Length)
                    {
                        char next = line[++i];
                        text.Append(next == 'n' ? '\n' : next == 't' ? '\t' : next);
                        continue;
                    }

                    if (ch == '"') return text.ToString();
                    text.Append(ch);
                }

                return text.ToString();
            }

            int end = at;
            while (end < line.Length && line[end] != ',' && line[end] != '}') end++;
            return line.Substring(at, end - at);
        }

        private static string FileHash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return SpatialSplitHash.Hex(sha.ComputeHash(file));
            }
        }

        internal static List<Pick> Read(string folder)
        {
            var picks = new List<Pick>();
            var byNumber = new Dictionary<int, Pick>();
            string index = Path.Combine(folder, "faces.jsonl");
            if (!File.Exists(index)) return picks;
            foreach (string line in File.ReadAllLines(index))
            {
                if (line.Trim().Length == 0) continue;
                var pick = new Pick { number = int.Parse(Field(line, "pick") ?? "-1", CultureInfo.InvariantCulture) };
                picks.Add(pick);
                if (byNumber.ContainsKey(pick.number)) pick.problems.Add("the pick's number is listed twice");
                byNumber[pick.number] = pick;
                string file = Field(line, "file");
                if (string.IsNullOrEmpty(file))
                {
                    pick.failure = Field(line, "failure") ?? "";
                    continue;
                }

                string path = Path.Combine(folder, file);
                if (!File.Exists(path)) { pick.problems.Add("the record " + file + " is not there"); continue; }
                try
                {
                    pick.sample = FacePickSample.Read(path);
                }
                catch (Exception e)
                {
                    pick.problems.Add("the record " + file + " does not read: " + e.GetType().Name + ": " + e.Message);
                    continue;
                }

                if (pick.sample.sha256 != Field(line, "sha256")) pick.problems.Add("the record's content hash is not its line's");
                if (pick.sample.pick != pick.number) pick.problems.Add("the record says pick " + pick.sample.pick);
                Recheck(pick.sample, pick.problems);
            }

            string images = Path.Combine(folder, "images.jsonl");
            if (File.Exists(images))
            {
                foreach (string line in File.ReadAllLines(images))
                {
                    if (line.Trim().Length == 0) continue;
                    int number = int.Parse(Field(line, "pick") ?? "-1", CultureInfo.InvariantCulture);
                    if (!byNumber.TryGetValue(number, out Pick pick)) continue;
                    string which = Field(line, "which"), file = Field(line, "file");
                    int frame = int.Parse(Field(line, "frame") ?? "-1", CultureInfo.InvariantCulture);
                    string path = Path.Combine(folder, file ?? "");
                    if (!File.Exists(path)) { pick.problems.Add("the picture " + file + " is not there"); continue; }
                    if (FileHash(path) != Field(line, "sha256")) pick.problems.Add("the picture " + file + " is not the one its line names (hash)");
                    if (pick.sample != null && file != (which == "before" ? pick.sample.imageBefore : pick.sample.imageAfter)) pick.problems.Add("the picture " + file + " is not the name the record gives");
                    if (which == "before") { pick.imageBefore = file; pick.imageBeforeFrame = frame; }
                    else { pick.imageAfter = file; pick.imageAfterFrame = frame; }
                }
            }

            return picks;
        }

        /// <summary>Every candidate worked out again from the saved parts alone; what does not come out as saved is added to <paramref name="problems"/>.</summary>
        internal static void Recheck(FacePickSample s, List<string> problems)
        {
            double3 o = s.aimOrigin, d = s.aimDirection;
            for (int i = 0; i < s.candidates.Count; i++)
            {
                FacePickCandidate c = s.candidates[i];
                string who = "candidate " + i;
                if (c.rank != i) problems.Add(who + ": its rank is " + c.rank);
                if (i > 0 && !c.clipRejected && !s.candidates[i - 1].clipRejected && c.distance < s.candidates[i - 1].distance) problems.Add(who + ": nearer than the one before it");
                float tolerance = 1e-3f + (1e-4f * c.distance);
                if (c.kind == FacePick.KindMesh)
                {
                    if (c.triangleVertices.Length != 3) { problems.Add(who + ": no triangle"); continue; }
                    Matrix4x4 m = c.transform;
                    float3 w0 = m.MultiplyPoint3x4(c.triangleVertices[0].position), w1 = m.MultiplyPoint3x4(c.triangleVertices[1].position), w2 = m.MultiplyPoint3x4(c.triangleVertices[2].position);
                    if (math.distance(w0, c.w0) > tolerance || math.distance(w1, c.w1) > tolerance || math.distance(w2, c.w2) > tolerance) problems.Add(who + ": the saved vertices placed by the saved transform are not the saved world points");
                    if (!Meets(o, d, w0, w1, w2, out double t) || math.abs(t - c.distance) > tolerance) problems.Add(who + ": the saved line does not meet the saved triangle at the saved distance");
                    float least = float.PositiveInfinity;
                    for (int p = 0; p < c.clipPlanes.Length; p++) least = math.min(least, math.dot(c.clipPlanes[p].xyz, c.hitWorld) + c.clipPlanes[p].w);
                    if ((least < -FacePick.ClipTolerance) != c.clipRejected && math.abs(least) > 1e-3f) problems.Add(who + ": the saved clip planes do not say what the record says of the point");
                    if (c.geometryRef < 0 || c.geometryRef >= s.geometries.Count) { problems.Add(who + ": names no saved geometry"); continue; }
                    FacePickGeometry g = s.geometries[c.geometryRef];
                    if (g.failure.Length > 0) continue;   // named and left out: said in the record
                    int command = g.CommandOfSlot(c.slot);
                    if (command < 0) { problems.Add(who + ": its slot is not a command of its saved geometry"); continue; }
                    int offset = g.commandOffset[command];
                    if (offset < 0) { problems.Add(who + ": its command's indices were not saved"); continue; }
                    int at = offset + (3 * c.triangle);
                    if (at + 2 >= g.indices.Length || g.indices[at] != c.i0 || g.indices[at + 1] != c.i1 || g.indices[at + 2] != c.i2) { problems.Add(who + ": the saved geometry's indices at its triangle are not its own"); continue; }
                    for (int k = 0; k < 3; k++)
                    {
                        uint id = k == 0 ? c.i0 : k == 1 ? c.i1 : c.i2;
                        if (!g.TryGetVertex(id, out VpRenderVertex v)) { problems.Add(who + ": vertex " + id + " is not in the saved geometry"); continue; }
                        VpRenderVertex own = c.triangleVertices[k];
                        if (v.position != own.position || v.normalX != own.normalX || v.normalY != own.normalY || v.u != own.u || v.v != own.v) problems.Add(who + ": vertex " + id + " of the saved geometry is not the candidate's");
                    }
                }
                else
                {
                    if (c.capPolygon.Length != c.capVertexCount || c.capVertexCount < 3) { problems.Add(who + ": no polygon"); continue; }
                    bool met = false;
                    for (int k = 1; k + 1 < c.capPolygon.Length && !met; k++)
                    {
                        met = Meets(o, d, c.capPolygon[0], c.capPolygon[k], c.capPolygon[k + 1], out double t) && math.abs(t - c.distance) <= tolerance;
                    }

                    if (!met) problems.Add(who + ": the saved line does not meet the saved polygon at the saved distance");
                }

                if (c.renderFragmentRef >= s.renderFragments.Count || c.stateRef >= s.states.Count) problems.Add(who + ": a reference past the pick's lists");
            }
        }

        private static bool Meets(double3 o, double3 d, double3 p0, double3 p1, double3 p2, out double t)
        {
            t = 0.0;
            double3 e1 = p1 - p0, e2 = p2 - p0, h = math.cross(d, e2);
            double det = math.dot(e1, h);
            if (math.abs(det) < 1e-18) return false;
            double3 s = o - p0;
            double u = math.dot(s, h) / det;
            double3 q = math.cross(s, e1);
            double v = math.dot(d, q) / det;
            t = math.dot(e2, q) / det;
            return u >= -1e-6 && v >= -1e-6 && u + v <= 1.0 + 1e-6;
        }

        internal static string Describe(List<Pick> picks)
        {
            var t = new StringBuilder(4096);
            int whole = 0, failed = 0, withProblems = 0, nothing = 0, mesh = 0, cap = 0, orphan = 0, pictures = 0;
            foreach (Pick p in picks)
            {
                if (p.sample == null && p.failure != null) failed++;
                if (p.problems.Count > 0) withProblems++;
                if (p.sample != null && p.problems.Count == 0) whole++;
                if (p.sample != null && p.sample.candidates.Count == 0) nothing++;
                if (p.sample != null && p.sample.candidates.Count > 0)
                {
                    FacePickCandidate first = p.sample.candidates[0];
                    if (first.kind == FacePick.KindCap) cap++; else mesh++;
                    if (first.kind == FacePick.KindMesh && first.ownerList == 0) orphan++;
                }

                if (p.imageBefore != null) pictures++;
                if (p.imageAfter != null) pictures++;
            }

            t.Append("FACE PICKS: ").Append(picks.Count).Append(" listed; ").Append(whole).Append(" read whole and worked out again as saved, ").Append(withProblems).Append(" with problems, ").Append(failed)
                .Append(" not taken at the time; first candidates: mesh ").Append(mesh).Append(" (").Append(orphan).Append(" of a slot no registration names), cap ").Append(cap).Append(", nothing met ").Append(nothing)
                .Append("; pictures ").Append(pictures);
            foreach (Pick p in picks)
            {
                t.Append("\n  #").Append(p.number).Append(": ");
                if (p.sample == null) { t.Append(p.failure != null ? "not taken: " + p.failure : "NOT READ"); }
                else
                {
                    t.Append(p.sample.Describe());
                    t.Append("\n    pictures: before ").Append(p.imageBefore ?? "MISSING").Append(" (frame ").Append(p.imageBeforeFrame).Append("), after ").Append(p.imageAfter ?? "MISSING").Append(" (frame ").Append(p.imageAfterFrame).Append(')');
                }

                foreach (string problem in p.problems) t.Append("\n    PROBLEM: ").Append(problem);
            }

            return t.ToString();
        }
    }
}
#endif
