using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// Shared ownership of cooked collider meshes: what one set of them came from, and how many owners and pieces of
    /// work still need it.
    /// <para>
    /// A child inherits the cooked mesh of a convex the plane did not split, so that mesh outlives the owner it came
    /// from. Retiring that owner must not destroy it, and holding a C# reference is not enough, because what destroys
    /// it is <see cref="PhysicsCutProducts.Dispose"/> and something has to decide when that is called. This is that
    /// decision, kept as small as it can be: whoever uses the meshes takes a hold, and the last one to let go gives
    /// them back, once.
    /// </para>
    /// <para>
    /// This is not the Provisional collision lease of DESIGN 7.1.1, whose contract is unchanged, and it keeps no
    /// ancestor actor, transaction or branch alive — only the meshes. The two sides of a cut hold it separately, so
    /// either can be retired on its own.
    /// </para>
    /// </summary>
    public sealed class PhysicsShapeSource
    {
        private PhysicsCutProducts _products;
        private int _users;
        private bool _owns;
        private bool _released;

        private PhysicsShapeSource(PhysicsCutProducts products)
        {
            _products = products;
        }

        /// <summary>
        /// The meshes of a cut, **still the caller's**. Users are counted from now on, but nothing is given back until
        /// <see cref="TakeOwnership"/> has moved the ownership here. A call that ends without taking it leaves the
        /// products exactly as they were, for the caller to go on using or to dispose itself.
        /// </summary>
        public static PhysicsShapeSource For(PhysicsCutProducts products)
        {
            if (products == null)
            {
                throw new ArgumentNullException(nameof(products));
            }

            return new PhysicsShapeSource(products);
        }

        /// <summary>
        /// The one point where the products stop being the caller's: from here, the last user to let go gives them
        /// back. It is called once, at the place where the handover has succeeded.
        /// </summary>
        public void TakeOwnership()
        {
            if (_released)
            {
                throw new ObjectDisposedException(nameof(PhysicsShapeSource), "these shapes have already been given back");
            }

            _owns = true;
        }

        /// <summary>Whether the products are this one's to give back yet.</summary>
        public bool Owns => _owns;

        /// <summary>
        /// Meshes that came from somewhere else and are not this one's to give back — an authored collider shape, for
        /// instance. It still counts its users, so that the same rules can be written once.
        /// </summary>
        public static PhysicsShapeSource External()
        {
            return new PhysicsShapeSource(null);
        }

        /// <summary>How many owners and pieces of work are holding this.</summary>
        public int Users => _users;

        /// <summary>Whether what it owned has been given back. Always false while it owns nothing.</summary>
        public bool IsReleased => _released;

        /// <summary>
        /// Takes a hold. A user is an owner whose colliders use one of these meshes, or a piece of work that is still
        /// reading them; both are the same thing here.
        /// </summary>
        public void Acquire()
        {
            if (_released)
            {
                throw new ObjectDisposedException(nameof(PhysicsShapeSource), "these shapes have already been given back");
            }

            _users = checked(_users + 1);
        }

        /// <summary>Lets a hold go, and gives the meshes back when it was the last one.</summary>
        public void Release()
        {
            if (_users <= 0)
            {
                throw new InvalidOperationException("released more often than it was acquired");
            }

            _users--;
            if (_users > 0 || _released)
            {
                return;
            }

            if (!_owns)
            {
                // Nothing here is this one's to give back: an authored shape, or a handover that did not happen.
                return;
            }

            _released = true;
            _products?.Dispose();
            _products = null;
        }
    }

    /// <summary>
    /// What one physics owner is made of: its convexes, each with the cooked collider mesh its shape uses and a hold
    /// on whoever owns that mesh.
    /// <para>
    /// The B-rep is this owner's own copy, in one bank. It is copied rather than shared because the numerical kernel
    /// reads one bank (DESIGN 7.2), and a side that inherited some convexes and had others produced for it would
    /// otherwise have its shape spread over two. Copying is what makes the owner cuttable again on its own, which is
    /// also what DESIGN 7.2 asks for by keeping the B-rep the authoritative input for the next cut. The copy is a
    /// plain element copy: face offsets, face indices, face edges and edges are all local to their own convex, so
    /// only the bases change.
    /// </para>
    /// <para>
    /// The collider meshes are not copied and not baked again. Each convex names the mesh its collider uses and the
    /// <see cref="PhysicsShapeSource"/> that owns it, and this shape holds that source until it is disposed.
    /// </para>
    /// <para>
    /// **The bank outlives the owner while work is reading it.** A cut of this owner reads these arrays from a worker,
    /// so retiring the owner cannot free them: whoever submits such work takes a hold with
    /// <see cref="AcquireForWork"/> and lets it go once the work has been collected. <see cref="Dispose"/> then means
    /// that the owner is finished with this, and the arrays and the mesh holds go back when the last reader is done —
    /// once, whichever of the two comes last. Nothing schedules or waits here.
    /// </para>
    /// </summary>
    public sealed unsafe class PhysicsOwnerShape : IDisposable
    {
        private readonly List<PhysicsShapeSource> _sources = new List<PhysicsShapeSource>(2);
        private readonly List<Mesh> _meshes = new List<Mesh>(4);
        private ConvexBrepRange[] _convexes = Array.Empty<ConvexBrepRange>();
        private NativeArray<float3> _vertices;
        private NativeArray<int> _faceOffsets;
        private NativeArray<int> _faceIndices;
        private NativeArray<int> _faceEdges;
        private NativeArray<BrepEdge> _edges;
        private int _workUsers;
        private bool _ownerDone;
        private bool _freed;

        private PhysicsOwnerShape(float4x4 localToOwner)
        {
            LocalToOwner = localToOwner;
        }

        /// <summary>From the numerical local frame these convexes are in to the owner's frame.</summary>
        public float4x4 LocalToOwner { get; }

        /// <summary>The bank this owner's convexes live in. It is this shape's own.</summary>
        public ConvexBrepBank Bank { get; private set; }

        public int ConvexCount => _convexes.Length;

        /// <summary>Where one convex is in <see cref="Bank"/>.</summary>
        public ConvexBrepRange Convex(int index)
        {
            return _convexes[index];
        }

        /// <summary>The cooked collider mesh of one convex. It belongs to a <see cref="PhysicsShapeSource"/>, not here.</summary>
        public Mesh MeshOf(int index)
        {
            return _meshes[index];
        }

        /// <summary>The meshes by convex index, which is what a cut of this owner inherits from.</summary>
        public IReadOnlyList<Mesh> Meshes => _meshes;

        /// <summary>How many pieces of work are still reading this bank.</summary>
        public int WorkUsers => _workUsers;

        /// <summary>Whether the bank and the mesh holds have gone back.</summary>
        public bool IsFreed => _freed;

        /// <summary>
        /// Takes a hold for work that will read this bank — a cut of this owner, submitted and not yet collected. The
        /// bank stays where it is until that hold goes, whatever happens to the owner in the meantime.
        /// </summary>
        public void AcquireForWork()
        {
            if (_freed)
            {
                throw new ObjectDisposedException(nameof(PhysicsOwnerShape), "this shape has already gone back");
            }

            _workUsers = checked(_workUsers + 1);
        }

        /// <summary>Lets a reader's hold go, once its work has been collected.</summary>
        public void ReleaseFromWork()
        {
            if (_workUsers <= 0)
            {
                throw new InvalidOperationException("released more often than it was acquired");
            }

            _workUsers--;
            FreeIfIdle();
        }

        /// <summary>
        /// The shape of an owner that was not made by a cut: an authored compound, with its convexes and their
        /// existing cooked meshes. The B-rep is copied here, so the bank handed in is only read during this call.
        /// </summary>
        public static PhysicsOwnerShape Authored(
            ConvexBrepBank bank,
            IReadOnlyList<ConvexBrepRange> convexes,
            IReadOnlyList<Mesh> meshes,
            PhysicsShapeSource source,
            float4x4 localToOwner)
        {
            if (convexes == null || meshes == null || source == null)
            {
                throw new ArgumentNullException(convexes == null ? nameof(convexes) : meshes == null ? nameof(meshes) : nameof(source));
            }

            if (convexes.Count != meshes.Count || convexes.Count == 0)
            {
                throw new ArgumentException("one mesh per convex, and at least one convex", nameof(meshes));
            }

            var shape = new PhysicsOwnerShape(localToOwner);
            try
            {
                var parts = new Part[convexes.Count];
                for (int i = 0; i < convexes.Count; i++)
                {
                    parts[i] = new Part { bank = bank, range = convexes[i], mesh = meshes[i], source = source };
                }

                shape.Fill(parts);
            }
            catch
            {
                shape.Dispose();
                throw;
            }

            return shape;
        }

        /// <summary>
        /// The shape of one side of a Provisional pair (DESIGN 7.1.1): the source's **own** convexes, the ones the
        /// 7.6 classification put on this side, shared exactly as they are.
        /// <para>
        /// No cut is made here, no collider mesh is copied and nothing is baked here: each part names the very mesh
        /// the source's own collider uses and takes a hold on whoever owns it, so those meshes outlive this side however
        /// the source ends. A convex the plane crosses belongs to both sides and is named by both, which is what
        /// DESIGN 7.1.1 allows when it accepts the ghost contacts of a shared old convex. The B-rep is copied into
        /// this side's own bank, as every shape's is, because the numerical kernel reads one bank.
        /// </para>
        /// </summary>
        internal static PhysicsOwnerShape ProvisionalSide(PhysicsOwnerShape source, IReadOnlyList<int> convexes)
        {
            if (source == null || convexes == null)
            {
                throw new ArgumentNullException(source == null ? nameof(source) : nameof(convexes));
            }

            if (convexes.Count == 0)
            {
                throw new ArgumentException("a side with no convex is not an owner", nameof(convexes));
            }

            var shape = new PhysicsOwnerShape(source.LocalToOwner);
            try
            {
                var parts = new Part[convexes.Count];
                for (int i = 0; i < convexes.Count; i++)
                {
                    int c = convexes[i];
                    if (c < 0 || c >= source._convexes.Length)
                    {
                        throw new ArgumentOutOfRangeException(nameof(convexes), c, "not a convex of the source");
                    }

                    parts[i] = new Part
                    {
                        bank = source.Bank,
                        range = source._convexes[c],
                        mesh = source._meshes[c],
                        source = source.SourceOf(c),
                    };
                }

                shape.Fill(parts);
            }
            catch
            {
                shape.Dispose();
                throw;
            }

            return shape;
        }

        /// <summary>
        /// The shape of one side of a finished cut: the convexes that side adopted, each produced here or inherited
        /// from <paramref name="parent"/>, copied into this side's own bank.
        /// <para>
        /// A produced convex uses the mesh the cut cooked, held through <paramref name="productsSource"/>; an
        /// inherited one uses the parent's mesh for that same input convex, held through whatever owns it there. The
        /// parent is only read: nothing of it is taken away, and it stays usable until it is retired on its own.
        /// </para>
        /// </summary>
        public static PhysicsOwnerShape OfSide(
            PhysicsOwnerShape parent, PhysicsCutProducts products, PhysicsShapeSource productsSource, bool positive)
        {
            if (parent == null || products == null || productsSource == null)
            {
                throw new ArgumentNullException(parent == null ? nameof(parent) : products == null ? nameof(products) : nameof(productsSource));
            }

            int count = products.PartCount(positive);
            if (count == 0)
            {
                throw new ArgumentException("a side with no convex is not an owner", nameof(positive));
            }

            var shape = new PhysicsOwnerShape(products.LocalToOwner);
            try
            {
                var parts = new Part[count];
                for (int i = 0; i < count; i++)
                {
                    PhysicsCutPart part = products.Part(positive, i);
                    parts[i] = part.borrowed
                        ? new Part
                        {
                            bank = parent.Bank,
                            range = parent._convexes[part.inputConvex],
                            mesh = parent._meshes[part.inputConvex],
                            source = parent.SourceOf(part.inputConvex),
                        }
                        : new Part { bank = products.Bank, range = part.range, mesh = part.mesh, source = productsSource };
                }

                shape.Fill(parts);
            }
            catch
            {
                shape.Dispose();
                throw;
            }

            return shape;
        }

        /// <summary>
        /// The owner is finished with this shape. Its bank and its mesh holds go back now, or when the last piece of
        /// work reading the bank has been collected, whichever is later.
        /// </summary>
        public void Dispose()
        {
            _ownerDone = true;
            FreeIfIdle();
        }

        private void FreeIfIdle()
        {
            if (_freed || !_ownerDone || _workUsers > 0)
            {
                return;
            }

            _freed = true;
            Release(ref _vertices);
            Release(ref _faceOffsets);
            Release(ref _faceIndices);
            Release(ref _faceEdges);
            Release(ref _edges);
            Bank = default;
            for (int i = 0; i < _sources.Count; i++)
            {
                _sources[i].Release();
            }

            _sources.Clear();
        }

        private struct Part
        {
            internal ConvexBrepBank bank;
            internal ConvexBrepRange range;
            internal Mesh mesh;
            internal PhysicsShapeSource source;
        }

        private readonly List<int> _sourceOf = new List<int>(4);

        private PhysicsShapeSource SourceOf(int convex)
        {
            return _sources[_sourceOf[convex]];
        }

        private void Fill(Part[] parts)
        {
            int vertices = 0, faceOffsets = 0, faceIndices = 0, edges = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                ConvexBrepRange r = parts[i].range;
                vertices += r.vertexCount;
                faceOffsets += r.faceCount + 1;
                faceIndices += r.faceIndexCount;
                edges += r.edgeCount;
            }

            // One element each at least: a zero-length native array has no pointer to give.
            _vertices = new NativeArray<float3>(math.max(1, vertices), Allocator.Persistent);
            _faceOffsets = new NativeArray<int>(math.max(1, faceOffsets), Allocator.Persistent);
            _faceIndices = new NativeArray<int>(math.max(1, faceIndices), Allocator.Persistent);
            _faceEdges = new NativeArray<int>(math.max(1, faceIndices), Allocator.Persistent);
            _edges = new NativeArray<BrepEdge>(math.max(1, edges), Allocator.Persistent);
            var bank = new ConvexBrepBank
            {
                vertices = (float3*)_vertices.GetUnsafePtr(),
                faceOffsets = (int*)_faceOffsets.GetUnsafePtr(),
                faceIndices = (int*)_faceIndices.GetUnsafePtr(),
                faceEdges = (int*)_faceEdges.GetUnsafePtr(),
                edges = (BrepEdge*)_edges.GetUnsafePtr(),
            };

            _convexes = new ConvexBrepRange[parts.Length];
            int vBase = 0, fBase = 0, iBase = 0, eBase = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                ConvexBrepRange r = parts[i].range;
                ConvexBrepBank from = parts[i].bank;
                UnsafeUtility.MemCpy(bank.vertices + vBase, from.vertices + r.vertexBase, (long)r.vertexCount * sizeof(float3));
                UnsafeUtility.MemCpy(bank.faceOffsets + fBase, from.faceOffsets + r.faceBase, (long)(r.faceCount + 1) * sizeof(int));
                UnsafeUtility.MemCpy(bank.faceIndices + iBase, from.faceIndices + r.faceIndexBase, (long)r.faceIndexCount * sizeof(int));
                UnsafeUtility.MemCpy(bank.faceEdges + iBase, from.faceEdges + r.faceIndexBase, (long)r.faceIndexCount * sizeof(int));
                UnsafeUtility.MemCpy(bank.edges + eBase, from.edges + r.edgeBase, (long)r.edgeCount * sizeof(BrepEdge));

                // Only the bases move: everything inside a convex is addressed from its own start.
                ConvexBrepRange moved = r;
                moved.vertexBase = vBase;
                moved.faceBase = fBase;
                moved.faceIndexBase = iBase;
                moved.edgeBase = eBase;
                _convexes[i] = moved;

                vBase += r.vertexCount;
                fBase += r.faceCount + 1;
                iBase += r.faceIndexCount;
                eBase += r.edgeCount;

                _meshes.Add(parts[i].mesh);
                int at = _sources.IndexOf(parts[i].source);
                if (at < 0)
                {
                    // One hold per source, however many of its meshes this owner uses.
                    // Held before it is recorded: a hold that was not taken must not be let go later.
                    parts[i].source.Acquire();
                    at = _sources.Count;
                    _sources.Add(parts[i].source);
                }

                _sourceOf.Add(at);
            }

            Bank = bank;
        }

        private static void Release<T>(ref NativeArray<T> array)
            where T : struct
        {
            if (array.IsCreated)
            {
                array.Dispose();
            }

            array = default;
        }
    }
}
