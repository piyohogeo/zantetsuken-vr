using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>Immutable mesh-local -> numerical B-rep local rigid frame. Default means no child frame.</summary>
    public readonly struct PhysicsMeshFrame : IEquatable<PhysicsMeshFrame>
    {
        public readonly bool HasFrame;
        public readonly quaternion Rotation;
        public readonly float3 Position;
        internal PhysicsMeshFrame(quaternion rotation, float3 position)
        { HasFrame = true; Rotation = rotation; Position = position; }
        public float4x4 Matrix => HasFrame ? new float4x4(Rotation, Position) : float4x4.identity;
        public bool Equals(PhysicsMeshFrame other) => HasFrame == other.HasFrame
            && (!HasFrame || (Rotation.Equals(other.Rotation) && Position.Equals(other.Position)));
    }

    public sealed unsafe partial class PhysicsOwnerShape
    {
        PhysicsMeshFrame[] _meshFrames;
        bool _preparedPoseWritten;
        public PhysicsMeshFrame MeshFrameOf(int convex) => _meshFrames == null ? default : _meshFrames[TableIndex(convex)];
        internal void PrepareMeshFrames() { _meshFrames = new PhysicsMeshFrame[ConvexCount]; }

        // Only the unpublished one-shot producer has this capability. No public mutable shape/Frame registry.
        internal bool WritePreparedPose(float3[][] bindPoints, PhysicsMeshFrame[] frames)
        {
            if (_preparedPoseWritten || _ownerDone || _freed || _bankUsers != 0 || _workUsers != 0) return false;
            _preparedPoseWritten = true;
            _localLo = new float3(float.PositiveInfinity); _localHi = new float3(float.NegativeInfinity);
            _localBoundsUsable = true;
            for (int c = 0; c < ConvexCount; c++)
            {
                var range = _convexes[c]; var bank = _banks[c]; var frame = frames[c];
                _meshFrames[c] = frame;
                float3 lo = new float3(float.PositiveInfinity), hi = new float3(float.NegativeInfinity);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    float3 p = math.rotate(frame.Rotation, bindPoints[c][v]) + frame.Position;
                    if (!math.all(math.isfinite(p))) return false;
                    bank.vertices[range.vertexBase + v] = p;
                    lo = math.min(lo, p); hi = math.max(hi, p);
                }
                _convexLo[c] = lo; _convexHi[c] = hi; AddToLocalBounds(lo, hi);
            }
            return true;
        }
    }

    /// <summary>
    /// Cold, per-instance bone-local convex meshes and fixed topology. Takes a validated B-rep bank, copies it,
    /// constructs/cooks meshes once, then permits one current-pose write into the unpublished B-rep.
    /// No intermediate source Collider/Actor is created. Main only; does not register or publish a body.
    /// Input ranges address bone-local convexes; their face loops/topology are the caller's validated authoring data.
    /// </summary>
    public sealed unsafe class VpPreparedPhysicsInput : IDisposable
    {
        PhysicsOwnerShape shape;
        readonly float3[][] bindPoints;
        readonly PhysicsMeshFrame[] frames;
        bool attempted, transferred, disposed;
        public int ConvexCount => bindPoints.Length;
        public int ColdCookCalls { get; private set; }
        public bool IsPosed { get; private set; }

        public VpPreparedPhysicsInput(ConvexBrepBank bank, IReadOnlyList<ConvexBrepRange> ranges)
        {
            if (ranges == null || ranges.Count == 0) throw new ArgumentException("Validated bone-local convexes required", nameof(ranges));
            bindPoints = new float3[ranges.Count][]; frames = new PhysicsMeshFrame[ranges.Count];
            var meshes = new Mesh[ranges.Count];
            var owner = PhysicsShapeSource.OwnPreparedMeshes(meshes);
            try
            {
                for (int c = 0; c < ranges.Count; c++)
                {
                    var r = ranges[c];
                    if (r.vertexCount < 4 || r.faceCount < 4) throw new ArgumentException("Nonempty convex required");
                    var vertices = new Vector3[r.vertexCount]; bindPoints[c] = new float3[r.vertexCount];
                    for (int v = 0; v < vertices.Length; v++)
                    {
                        float3 p = bank.vertices[r.vertexBase + v];
                        if (!math.all(math.isfinite(p))) throw new ArgumentException("Finite bind points required");
                        vertices[v] = p; bindPoints[c][v] = p;
                    }
                    var triangles = new List<int>();
                    for (int f = 0; f < r.faceCount; f++)
                    {
                        int start = bank.faceOffsets[r.faceBase + f], end = bank.faceOffsets[r.faceBase + f + 1];
                        if (start < 0 || end > r.faceIndexCount || end - start < 3) throw new ArgumentException("Invalid face loop");
                        for (int k = start; k < end; k++)
                            if ((uint)bank.faceIndices[r.faceIndexBase + k] >= r.vertexCount) throw new ArgumentException("Invalid vertex reference");
                        for (int k = start + 1; k + 1 < end; k++)
                        {
                            triangles.Add(bank.faceIndices[r.faceIndexBase + start]);
                            triangles.Add(bank.faceIndices[r.faceIndexBase + k]);
                            triangles.Add(bank.faceIndices[r.faceIndexBase + k + 1]);
                        }
                    }
                    var mesh = meshes[c] = new Mesh { name = "Prepared bone-local convex" };
                    mesh.vertices = vertices; mesh.triangles = triangles.ToArray();
                    Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking); ColdCookCalls++;
                }
                shape = PhysicsOwnerShape.Authored(bank, ranges, meshes, owner, float4x4.identity);
                shape.PrepareMeshFrames();
            }
            catch { shape?.Dispose(); throw; }
            finally { owner.Release(); }
        }

        /// <summary>Borrowed until TakeShape. Invalid pose consumes the attempt but never exposes partial output.</summary>
        public bool TryPose(IReadOnlyList<float4x4> boneToOwner, out PhysicsOwnerShape posed)
        {
            if (disposed) throw new ObjectDisposedException(nameof(VpPreparedPhysicsInput));
            posed = null;
            if (attempted || transferred) return false;
            attempted = true;
            if (boneToOwner == null || boneToOwner.Count != ConvexCount) return false;
            for (int c = 0; c < frames.Length; c++)
            {
                if (!PhysicsOwnerBuilder.TryRigid(boneToOwner[c], out var rotation, out var position)) return false;
                frames[c] = new PhysicsMeshFrame(rotation, position);
            }
            if (!shape.WritePreparedPose(bindPoints, frames)) return false;
            IsPosed = true; posed = shape; return true;
        }

        /// <summary>
        /// Explicit ownership transfer to the caller, NOT proof of Registry acceptance. On registration refusal the
        /// caller must Dispose the returned shape. Input disposal before transfer reclaims a built-but-unregistered shape.
        /// </summary>
        public PhysicsOwnerShape TakeShape()
        {
            if (disposed) throw new ObjectDisposedException(nameof(VpPreparedPhysicsInput));
            if (!IsPosed || transferred) throw new InvalidOperationException("One successfully posed shape may be transferred once");
            transferred = true; var result = shape; shape = null; return result;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; shape?.Dispose(); shape = null;
        }
    }
}
