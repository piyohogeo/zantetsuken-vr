using System;
using System.Runtime.CompilerServices;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// Explicit Main-thread load-time API preparation. Share one instance at the application/bootstrap level,
    /// not per NPC. Temporary roots and a mesh-source hold remain until deferred destruction is confirmed.
    /// No implicit call on hit. This does not replace current-pose classification/mass or actual pair construction.
    /// </summary>
    public sealed class VpPhysicsColdPreparation : IDisposable
    {
        bool warmed;
        GameObject pendingPositive, pendingNegative;
        PhysicsShapeSource pendingMeshes;
        public bool IsPrepared => warmed && pendingMeshes == null;

        /// <summary>
        /// Call on a later loading frame until true, without spinning. Only then are the temporary native roots
        /// gone and the borrowed Mesh hold released. No bank/pose is held, so input pose/disposal may proceed.
        /// </summary>
        public bool TryFinish()
        {
            if (pendingPositive != null || pendingNegative != null) return false;
            pendingPositive = pendingNegative = null;
            pendingMeshes?.Release(); pendingMeshes = null;
            return true;
        }

        public void Dispose()
        {
            if (!TryFinish()) throw new InvalidOperationException("Cold native destruction is pending; finish on a later loading frame");
        }

        /// <summary>
        /// Uses one still-unposed D2 input as representative. Subsequent successful calls are no-ops.
        /// Creates disposable inactive bodies/joint/collider; never publishes or simulates them. PlayMode destroys
        /// are deferred to end of frame. Keep this preparer and call TryFinish after destruction before cut-ready.
        /// A failure leaves the flag false; the caller may retry explicitly. No rollback of first-use engine caches.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Prepare(VpPreparedPhysicsInput representative)
        {
            if (warmed) { TryFinish(); return; }
            if (!TryFinish()) throw new InvalidOperationException("A failed preparation still has pending native cleanup");
            if (representative == null) throw new ArgumentNullException(nameof(representative));
            var shape = representative.ColdPreparationShape();
            if (!shape.TryLocalBounds(out var lo, out var hi) || shape.MeshOf(0) == null)
                throw new InvalidOperationException("Representative needs finite bounds and a cooked Mesh");

            // Real view/hold paths, with no saved view or change to the producer's one-shot pose state.
            var indices = new int[shape.ConvexCount];
            for (int c = 0; c < indices.Length; c++) indices[c] = c;
            using (var view = PhysicsOwnerShape.ProvisionalSideFromOwnedIndices(shape, indices)) { }
            float3 center = lo * .5f + hi * .5f;
            var plane = new float4(0, 1, 0, -center.y);
            PhysicsCutClassification classified = null;
            try
            {
                if (!PhysicsCutClassification.TryClassify(shape, plane, 1e-5f, 60, 128, out classified))
                    throw new InvalidOperationException("Representative classification refused");
            }
            finally { classified?.Dispose(); }
            WarmMass(shape, plane);
            float3 normal = math.normalize(new float3(1, 2, 3));
            WarmMass(shape, new float4(normal, -math.dot(normal, center)));

            pendingMeshes = representative.AcquireColdMeshHold();
            try
            {
                var positive = pendingPositive = new GameObject("Cold physics preparation positive"); positive.SetActive(false);
                var negative = pendingNegative = new GameObject("Cold physics preparation negative"); negative.SetActive(false);
                var p = new PhysicsOwnerSide(true, positive, positive, positive.AddComponent<Rigidbody>());
                var n = new PhysicsOwnerSide(false, negative, negative, negative.AddComponent<Rigidbody>());
                // PhysicsOwnerSide constructs the same List<MeshCollider>(4) as the real builder.
                p.DeclareMassPropertiesExplicit(); n.DeclareMassPropertiesExplicit();
                ProvisionalSeparation.Configure(p, n, new float3(1, 2, 3));
                // D2 output uses a dedicated convex frame. Cold input has no pose yet; identity is only warm data.
                var collider = PhysicsOwnerBuilder.CreateMeshCollider(positive,
                    new PhysicsMeshFrame(quaternion.identity, float3.zero));
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = shape.MeshOf(0);
                p.Add(collider);
            }
            finally
            {
                // Inactive MeshCollider did not reliably clear sharedMesh in PlayMode. Hold its source until both
                // roots compare Unity-null instead of relying on that setter or an inactive OnDestroy callback.
                try { PhysicsOwnerBuilder.DestroyObject(pendingPositive); }
                finally
                {
                    try { PhysicsOwnerBuilder.DestroyObject(pendingNegative); }
                    finally { TryFinish(); }
                }
            }
            warmed = true;
        }

        static void WarmMass(PhysicsOwnerShape shape, float4 plane)
        {
            if (!ProvisionalBoxMass.TryDivide(shape, plane, 60, new float3(1), quaternion.identity,
                out _, out _, out _)) throw new InvalidOperationException("Representative box mass refused");
        }
    }
}
