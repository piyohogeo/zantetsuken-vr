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
        private Mesh[] _preparedMeshes;
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

        // The prepared-input constructor retains a hold until its shape has been created or construction fails.
        internal static PhysicsShapeSource OwnPreparedMeshes(Mesh[] meshes)
        {
            var source = new PhysicsShapeSource(null) { _preparedMeshes = meshes, _owns = true };
            source.Acquire();
            return source;
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
            if (_preparedMeshes != null)
            {
                foreach (Mesh mesh in _preparedMeshes) PhysicsCutCook.DestroyMesh(mesh);
                _preparedMeshes = null;
            }
        }
    }

    /// <summary>
    /// What one physics owner is made of: its convexes, each with the cooked collider mesh its shape uses and a hold
    /// on whoever owns that mesh.
    /// <para>
    /// An authored shape owns its B-rep in one block of its own, every convex copied in (a plain element copy: face
    /// offsets, face indices, face edges and edges are all local to their own convex, so only the bases change). A
    /// side made from another shape copies nothing: each of its convexes keeps the bank it already lives in -- the
    /// source's or the parent's block, or the cut's products -- and the side holds the block's ultimate owner alive
    /// (<see cref="AcquireAsBank"/>) for as long as it addresses it. The numerical kernel reads a bank per convex
    /// (<c>ConvexCutOwnerInput.banks</c>), which is what lets a side's shape be spread over several blocks and
    /// still be the authoritative input for the next cut (DESIGN 7.2).
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
    public sealed unsafe partial class PhysicsOwnerShape : IDisposable
    {
        private readonly List<PhysicsShapeSource> _sources = new List<PhysicsShapeSource>(2);
        /// <summary>
        /// One mesh per convex, in convex order. **Made with the convex count**: a list that starts at four and
        /// doubles allocates and copies its way up for a shape of more convexes than that, and the count is known
        /// before this object exists.
        /// </summary>
        private readonly List<Mesh> _meshes;
        private ConvexBrepRange[] _convexes = Array.Empty<ConvexBrepRange>();
        private ConvexBrepBank[] _banks = Array.Empty<ConvexBrepBank>();
        private float3[] _convexLo = Array.Empty<float3>();
        private float3[] _convexHi = Array.Empty<float3>();
        /// <summary>
        /// The one native block an authored shape's B-rep lives in: vertices, face offsets, face indices, face
        /// edges and edges, each at a 16-byte-aligned offset. One allocation, one release; the bank's pointers
        /// slice it. A side has none: its convexes address the banks they came from.
        /// </summary>
        private NativeArray<byte> _block;

        /// <summary>
        /// Per convex, the shape that owns the block its bank is: this shape for an authored convex, another shape
        /// for one borrowed from an authored block, and null for one that lives in a cut's products (the products'
        /// source keeps those). What a borrower holds is these owners, directly.
        /// </summary>
        private PhysicsOwnerShape[] _blockOwnerOf = Array.Empty<PhysicsOwnerShape>();

        /// <summary>
        /// For a shape made from a list of another shape's convexes, which convex of that shape each of its own is;
        /// empty for any other shape. See <see cref="InputConvexOf"/>.
        /// </summary>
        private int[] _inputConvexOf = Array.Empty<int>();

        // A Provisional side selects immutable descriptor entries from its source's tables. The tables are
        // shared by reference; the native banks and meshes still have this side's own, direct holds below.
        // Nested selections are flattened, so a disposed intermediate shape is never a read dependency.
        private int[] _viewConvexOf;
        private IReadOnlyList<Mesh> _meshView;

        /// <summary>The block owners this one's convexes address, held until this shape is freed.</summary>
        private readonly List<PhysicsOwnerShape> _bankOwners = new List<PhysicsOwnerShape>(2);

        /// <summary>How many shapes address this one's block; the block is not freed while any does.</summary>
        private int _bankUsers;

        /// <summary>The mesh holds have gone back: the owner is done and no work reads this shape.</summary>
        private bool _sourcesReleased;
        private float3 _localLo = new float3(float.PositiveInfinity);
        private float3 _localHi = new float3(float.NegativeInfinity);
        private bool _localBoundsUsable = true;
        private int _workUsers;
        private bool _ownerDone;
        private bool _freed;

        /// <summary>
        /// <paramref name="convexCount"/> is how many convexes this shape will hold, which every caller knows
        /// before it builds one. Owned tables use that size for their per-convex lists. A Provisional view shares
        /// its source's descriptor tables and mesh list instead; its source-hold mapping is still its own.
        /// Owned descriptor arrays are made by <see cref="BeginBorrowing"/> or <see cref="Fill"/>.
        /// </summary>
        private PhysicsOwnerShape(float4x4 localToOwner, int convexCount, PhysicsOwnerShape viewOf = null)
        {
            LocalToOwner = localToOwner;
            _meshes = viewOf == null ? new List<Mesh>(convexCount) : viewOf._meshes;
            _sourceOf = new List<int>(convexCount);
            if (viewOf != null)
            {
                _convexes = viewOf._convexes;
                _banks = viewOf._banks;
                _blockOwnerOf = viewOf._blockOwnerOf;
                _convexLo = viewOf._convexLo;
                _convexHi = viewOf._convexHi;
                _meshFrames = viewOf._meshFrames;
            }
        }

        /// <summary>From the numerical local frame these convexes are in to the owner's frame.</summary>
        public float4x4 LocalToOwner { get; }

        /// <summary>
        /// The bank convex <paramref name="index"/>'s range addresses. An authored shape's convexes all live in its
        /// own block; a side's live where they were before -- the source's or the parent's bank, or the cut's
        /// products -- and this shape holds whoever keeps that bank alive.
        /// </summary>
        public ConvexBrepBank BankOf(int index)
        {
            return _banks[TableIndex(index)];
        }

        /// <summary>How many shapes address this shape's block right now.</summary>
        public int BankUsers => _bankUsers;

        /// <summary>The shape that owns the block convex <paramref name="index"/> lives in, or null for a cut's products.</summary>
        public PhysicsOwnerShape BlockOwnerOf(int index)
        {
            return _blockOwnerOf[TableIndex(index)];
        }

        /// <summary>Whether this shape's mesh holds have gone back already (its owner done, its work collected).</summary>
        public bool MeshHoldsReleased => _sourcesReleased;

        /// <summary>
        /// Which convex of the shape this one was made from convex <paramref name="index"/> is, or -1 when this shape
        /// was not made that way (an authored shape, or a side made from a cut's products). It is written once, where
        /// the side is made from a list of its source's convexes, and it is the correspondence the Final handoff reads
        /// to find the Provisional collider of an inherited part without searching for one.
        /// </summary>
        public int InputConvexOf(int index)
        {
            return index >= 0 && index < _inputConvexOf.Length ? _inputConvexOf[index] : -1;
        }

        public int ConvexCount => _viewConvexOf == null ? _convexes.Length : _viewConvexOf.Length;

        private int TableIndex(int index) => _viewConvexOf == null ? index : _viewConvexOf[index];

        /// <summary>Where one convex is in <see cref="Bank"/>.</summary>
        public ConvexBrepRange Convex(int index)
        {
            return _convexes[TableIndex(index)];
        }

        /// <summary>
        /// The box one convex of this shape lies in, in this shape's local frame. It was settled once, where that
        /// convex entered the system -- at the authored entry, or from what the cut's job measured while it wrote the
        /// collider -- and has been carried from shape to shape since. Nothing recomputes it from vertices.
        /// </summary>
        public void ConvexBounds(int index, out float3 lo, out float3 hi)
        {
            int at = TableIndex(index);
            lo = _convexLo[at];
            hi = _convexHi[at];
        }

        /// <summary>The cooked collider mesh of one convex. It belongs to a <see cref="PhysicsShapeSource"/>, not here.</summary>
        public Mesh MeshOf(int index)
        {
            return _meshes[TableIndex(index)];
        }

        /// <summary>The meshes by convex index, which is what a cut of this owner inherits from.</summary>
        public IReadOnlyList<Mesh> Meshes => _viewConvexOf == null
            ? _meshes
            : _meshView ?? (_meshView = new PhysicsShapeMeshView(this));

        /// <summary>
        /// The box every convex of this shape lies inside, in **this shape's own local frame** -- the frame
        /// <see cref="LocalToOwner"/> maps from, which is the frame the bank's vertices are in.
        /// <para>
        /// It is the union of its convexes' own boxes (<see cref="ConvexBounds"/>). Each of those was settled where
        /// that convex entered the system -- walked once at the authored entry, or taken from what the cut's job
        /// measured as it wrote the collider -- and has been **carried from shape to shape** since. Building a shape
        /// unions boxes it was handed; **it reads no vertex to do it**, however many shapes are built.
        /// </para>
        /// <para>
        /// Each convex's box holds that convex itself, not merely what its collider claims: the authored one is the
        /// collider's bounds widened to the vertices actually seen there, and a produced one is the job's own
        /// measurements, which the mesh's stored bounds can sit very slightly inside. The collider's bounds are in it
        /// as well, because the collider is what physics touches and it may reach further than the convex.
        /// </para>
        /// <para>
        /// It is settled when the shape is built and never computed again. What it depends on -- which convexes this
        /// shape is made of and where each sits inside it -- is fixed for the shape's life, so a shape whose
        /// arrangement differs is a different shape and gets its own. **Where the owner is in the world is not part of
        /// it**, and moving or turning the owner cannot change it.
        /// </para>
        /// <para>
        /// False when the shape has no convex or a vertex that is not finite. The out values are written either way,
        /// and on false they are whatever the partial union had reached -- not a box to use.
        /// </para>
        /// </summary>
        public bool TryLocalBounds(out float3 lo, out float3 hi)
        {
            lo = _localLo;
            hi = _localHi;
            return _localBoundsUsable
                && ConvexCount > 0
                && math.all(math.isfinite(_localLo))
                && math.all(math.isfinite(_localHi))
                && math.all(_localHi >= _localLo);
        }

        /// <summary>How many pieces of work are still reading this bank.</summary>
        public int WorkUsers => _workUsers;

        /// <summary>Another shape's convexes address this one's banks from now until it lets go; this shape stays for it.</summary>
        private void AcquireAsBank()
        {
            if (_freed)
            {
                throw new ObjectDisposedException(nameof(PhysicsOwnerShape), "this shape has already gone back");
            }

            _bankUsers = checked(_bankUsers + 1);
        }

        private void ReleaseAsBank()
        {
            if (_bankUsers <= 0)
            {
                throw new InvalidOperationException("released as a bank more often than it was acquired");
            }

            _bankUsers--;
            FreeIfIdle();
        }

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

            // After the checks above, so an input that is refused is still refused before anything is made.
            var shape = new PhysicsOwnerShape(localToOwner, convexes.Count);
            try
            {
                var parts = new Part[convexes.Count];
                for (int i = 0; i < convexes.Count; i++)
                {
                    // The one place a mesh from outside is taken on trust, so the trust is checked here rather than
                    // wherever its bounds are later read -- and the one walk of these vertices, whose result becomes
                    // this convex's box and is never worked out again.
                    RequireMeshBoundsEnclose(bank, convexes[i], meshes[i], i, out float3 lo, out float3 hi);
                    parts[i] = new Part
                    {
                        bank = bank, range = convexes[i], mesh = meshes[i], source = source, lo = lo, hi = hi,
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
        /// The shape of one side of a Provisional pair (DESIGN 7.1.1): the source's **own** convexes, the ones the
        /// 7.6 classification put on this side, shared exactly as they are.
        /// <para>
        /// No cut is made here, no collider mesh is copied and nothing is baked here: each part names the very mesh
        /// the source's own collider uses and takes a hold on whoever owns it, so those meshes outlive this side however
        /// the source ends. A convex the plane crosses belongs to both sides and is named by both, which is what
        /// DESIGN 7.1.1 allows when it accepts the ghost contacts of a shared old convex. The B-rep is not copied:
        /// each convex keeps the source's bank, and this side holds the source's block owner alive.
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

            var indices = new int[convexes.Count];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = convexes[i];
            }

            return ProvisionalSideFromOwnedIndices(source, indices);
        }

        /// <summary>
        /// The builder gives up its freshly allocated index array here. No caller may mutate it afterwards;
        /// the general list entrance above retains its snapshot semantics by copying first.
        /// </summary>
        internal static PhysicsOwnerShape ProvisionalSideFromOwnedIndices(PhysicsOwnerShape source, int[] indices)
        {
            var shape = new PhysicsOwnerShape(source.LocalToOwner, indices.Length, source);
            try
            {
                shape._inputConvexOf = indices;
                shape._viewConvexOf = source._viewConvexOf == null ? indices : new int[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    int c = indices[i];
                    if (c < 0 || c >= source.ConvexCount)
                    {
                        throw new ArgumentOutOfRangeException("convexes", c, "not a convex of the source");
                    }

                    int at = source.TableIndex(c);
                    shape._viewConvexOf[i] = at;
                    shape.AddToLocalBounds(source._convexLo[at], source._convexHi[at]);
                    shape.HoldSource(source.SourceOf(c));
                    shape.HoldBank(source._blockOwnerOf[at]);
                }
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
        /// from <paramref name="parent"/>, borrowed as they are: the parent's bank and block owner, not a copy.
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

            var shape = new PhysicsOwnerShape(products.LocalToOwner, count);
            try
            {
                shape.BeginBorrowing(count);
                for (int i = 0; i < count; i++)
                {
                    PhysicsCutPart part = products.Part(positive, i);
                    if (part.borrowed)
                    {
                        int at = parent.TableIndex(part.inputConvex);
                        shape.Borrow(i, new Part
                        {
                            bank = parent._banks[at],
                            bankOwner = parent._blockOwnerOf[at],
                            range = parent._convexes[at],
                            mesh = parent._meshes[at],
                            source = parent.SourceOf(part.inputConvex),
                            meshFrame = parent.MeshFrameOf(part.inputConvex),

                            // Inherited uncut: the parent's box for that very convex.
                            lo = parent._convexLo[at],
                            hi = parent._convexHi[at],
                        });
                        continue;
                    }

                    // Produced: the box of this convex, from one look at its mesh.
                    ProducedBox(part, out float3 producedLow, out float3 producedHigh);
                    shape.Borrow(i, new Part
                    {
                        bank = products.Bank,
                        range = part.range,
                        mesh = part.mesh,
                        source = productsSource,
                        lo = producedLow,
                        hi = producedHigh,
                    });
                }
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

            // The owner is done and nothing of this shape's work is out: the mesh holds go back now, once. What
            // any borrower reads of this shape is its block, and a borrower holds the meshes it uses itself.
            if (!_sourcesReleased)
            {
                _sourcesReleased = true;
                for (int i = 0; i < _sources.Count; i++)
                {
                    _sources[i].Release();
                }

                _sources.Clear();
            }

            if (_bankUsers > 0)
            {
                return;
            }

            _freed = true;
            Release(ref _block);
            _banks = Array.Empty<ConvexBrepBank>();
            _blockOwnerOf = Array.Empty<PhysicsOwnerShape>();
            for (int i = 0; i < _bankOwners.Count; i++)
            {
                _bankOwners[i].ReleaseAsBank();
            }

            _bankOwners.Clear();
        }

        private struct Part
        {
            internal ConvexBrepBank bank;
            internal ConvexBrepRange range;
            internal Mesh mesh;
            internal PhysicsShapeSource source;
            internal PhysicsMeshFrame meshFrame;

            /// <summary>The shape that owns the block <see cref="bank"/> is in, when a shape does; null when it is a cut's products'.</summary>
            internal PhysicsOwnerShape bankOwner;

            /// <summary>The box this convex lies in, in the local frame. Settled by whoever made this part.</summary>
            internal float3 lo;
            internal float3 hi;
        }

        /// <summary>
        /// Which of <see cref="_sources"/> each convex's mesh came from, in convex order. **Made with the convex
        /// count**, for the same reason as <see cref="_meshes"/>.
        /// </summary>
        private readonly List<int> _sourceOf;

        private PhysicsShapeSource SourceOf(int convex)
        {
            return _sources[_sourceOf[convex]];
        }

        /// <summary>
        /// That an authored mesh really is the collider of the convex it was given for: its own bounds cover that
        /// convex, in the same frame. A mesh that is not the collider of its convex is an input fault, and it is
        /// refused here, once, at the only entry that takes a mesh from outside -- not carried into a shape where it
        /// would describe something else than the convex beside it.
        /// <para>
        /// **This is a check on the input, not what makes the shape's box safe.** The box includes the convex's own
        /// vertices, so it holds them whatever a collider claims. Nor does this catch every frame mismatch: a box in
        /// another frame that happens to be large enough still covers the convex and still passes.
        /// </para>
        /// <para>
        /// A shape made by a cut is not checked here: a produced convex's mesh is written from that very convex, and
        /// an inherited one carries a mesh that passed here already.
        /// </para>
        /// <para>
        /// The comparison allows a small slack, relative to the box's own size, because the bounds are floats built
        /// from those same vertices and are allowed to be no tighter than rounding leaves them. The slack is this
        /// check's alone: the box that is kept is not widened by it, and does not need to be.
        /// </para>
        /// </summary>
        private static void RequireMeshBoundsEnclose(
            ConvexBrepBank bank, ConvexBrepRange range, Mesh mesh, int index, out float3 boxLo, out float3 boxHi)
        {
            if (mesh == null)
            {
                throw new ArgumentException("convex " + index + " has no collider mesh", "meshes");
            }

            Bounds bounds = mesh.bounds;
            float3 lo = bounds.min;
            float3 hi = bounds.max;
            if (!math.all(math.isfinite(lo)) || !math.all(math.isfinite(hi)) || math.any(hi < lo))
            {
                throw new ArgumentException(
                    "the collider mesh of convex " + index + " has no usable bounds", "meshes");
            }

            if (range.vertexCount <= 0)
            {
                throw new ArgumentException("convex " + index + " has no vertex", "convexes");
            }

            // The one walk of these vertices. What it finds becomes this convex's box, so the slack below decides
            // only whether the input is refused, never how wide the box that is kept turns out to be.
            float3 slack = math.max(new float3(1e-4f), math.abs(hi - lo) * 1e-4f);
            boxLo = lo;
            boxHi = hi;
            for (int v = 0; v < range.vertexCount; v++)
            {
                float3 at = bank.vertices[range.vertexBase + v];
                if (!math.all(math.isfinite(at)))
                {
                    throw new ArgumentException("convex " + index + " has a vertex that is not finite", "convexes");
                }

                if (math.any(at < lo - slack) || math.any(at > hi + slack))
                {
                    throw new ArgumentException(
                        "the collider mesh of convex " + index + " does not enclose that convex: its bounds are ["
                        + lo + ", " + hi + "] and the convex reaches " + at
                        + ". The mesh and the convex must be in the same frame.",
                        "meshes");
                }

                boxLo = math.min(boxLo, at);
                boxHi = math.max(boxHi, at);
            }
        }

        /// <summary>
        /// The box of one produced convex: **what the job measured while it wrote that convex's mesh**, widened by
        /// the mesh's own bounds where those are usable. No vertex is read, and nothing is cached -- the mesh is
        /// asked **once**, and the two ends come out of that one answer.
        /// <para>
        /// **Both measurements are kept, and for different reasons.** The job's are the safe ones: the mesh reports
        /// its bounds through a centre-and-size pair of floats and can come out very slightly inside them, and
        /// because the widening only ever grows the box, that rounding can never pull the job's extremes in. The
        /// mesh's are kept as well because the collider is what physics touches.
        /// </para>
        /// <para>
        /// The rule is the one it always was. With no mesh, the job's extremes stand as they are. With one, each end
        /// is judged finite **on its own**, so a mesh whose minimum is usable and whose maximum is not still widens
        /// the low end.
        /// </para>
        /// </summary>
        private static void ProducedBox(PhysicsCutPart part, out float3 lo, out float3 hi)
        {
            lo = part.bounds.c0;
            hi = part.bounds.c1;
            if (part.mesh == null)
            {
                return;
            }

            // One look. Bounds is a value: its min and its max come from the same centre and extents, which is what
            // the two calls this replaces each worked out for themselves.
            Bounds box = part.mesh.bounds;
            float3 low = box.min;
            if (math.all(math.isfinite(low)))
            {
                lo = math.min(lo, low);
            }

            float3 high = box.max;
            if (math.all(math.isfinite(high)))
            {
                hi = math.max(hi, high);
            }
        }

        /// <summary>
        /// Widens this shape's local box by one part's own box -- the one that part was handed, not one worked out
        /// here. A box that is not usable leaves the shape without one at all, rather than with a box that covers only
        /// some of it. **No vertex is read.**
        /// </summary>
        private void AddToLocalBounds(float3 lo, float3 hi)
        {
            if (!_localBoundsUsable)
            {
                return;
            }

            if (!math.all(math.isfinite(lo)) || !math.all(math.isfinite(hi)) || math.any(hi < lo))
            {
                _localBoundsUsable = false;
                return;
            }

            _localLo = math.min(_localLo, lo);
            _localHi = math.max(_localHi, hi);
        }

        /// <summary>
        /// Makes room for a borrowed side of <paramref name="count"/> convexes. **Nothing is held yet**; each convex
        /// is taken by <see cref="Borrow"/>, which is where the holds are taken, one at a time.
        /// <para>
        /// A caller that fails part way gives the shape back, and what was already held goes back with it exactly
        /// once -- the mesh sources through <see cref="FreeIfIdle"/>, the block owners through the same. A convex
        /// that was never taken holds nothing, and a slot never written stays as it was made.
        /// </para>
        /// </summary>
        private void BeginBorrowing(int count)
        {
            _convexes = new ConvexBrepRange[count];
            _banks = new ConvexBrepBank[count];
            _blockOwnerOf = new PhysicsOwnerShape[count];
            _convexLo = new float3[count];
            _convexHi = new float3[count];
        }

        /// <summary>
        /// Takes one borrowed convex into the place it is kept: its range in the bank it came from, its mesh, its
        /// box, and the holds on whoever keeps the bank and the mesh. The same writes the array-at-a-time borrowing
        /// made, in the same order, without the array.
        /// </summary>
        private void Borrow(int i, in Part part)
        {
            _convexes[i] = part.range;
            _banks[i] = part.bank;
            _blockOwnerOf[i] = part.bankOwner;
            _meshes.Add(part.mesh);
            if (part.meshFrame.HasFrame)
            {
                if (_meshFrames == null) _meshFrames = new PhysicsMeshFrame[_convexes.Length];
                _meshFrames[i] = part.meshFrame;
            }
            _convexLo[i] = part.lo;
            _convexHi[i] = part.hi;
            AddToLocalBounds(part.lo, part.hi);
            HoldSource(part.source);
            HoldBank(part.bankOwner);
        }

        private void HoldBank(PhysicsOwnerShape owner)
        {
            if (owner != null && !_bankOwners.Contains(owner))
            {
                // Held before it is recorded: a hold that was not taken must not be let go later.
                owner.AcquireAsBank();
                _bankOwners.Add(owner);
            }
        }

        /// <summary>
        /// Takes the parts of an authored shape: every convex is copied into one block this shape owns. A borrowed
        /// side does not come through here -- it is taken convex by convex, by <see cref="Borrow"/>.
        /// </summary>
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
            long verticesAt = 0;
            long faceOffsetsAt = verticesAt + Align16((long)math.max(1, vertices) * sizeof(float3));
            long faceIndicesAt = faceOffsetsAt + Align16((long)math.max(1, faceOffsets) * sizeof(int));
            long faceEdgesAt = faceIndicesAt + Align16((long)math.max(1, faceIndices) * sizeof(int));
            long edgesAt = faceEdgesAt + Align16((long)math.max(1, faceIndices) * sizeof(int));
            long blockBytes = edgesAt + Align16((long)math.max(1, edges) * sizeof(BrepEdge));

            // Every byte of the block is written by the copies below, so it is not cleared first.
            _block = new NativeArray<byte>(
                checked((int)blockBytes), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            byte* block = (byte*)_block.GetUnsafePtr();
            var bank = new ConvexBrepBank
            {
                vertices = (float3*)(block + verticesAt),
                faceOffsets = (int*)(block + faceOffsetsAt),
                faceIndices = (int*)(block + faceIndicesAt),
                faceEdges = (int*)(block + faceEdgesAt),
                edges = (BrepEdge*)(block + edgesAt),
            };

            _convexes = new ConvexBrepRange[parts.Length];
            _banks = new ConvexBrepBank[parts.Length];
            _blockOwnerOf = new PhysicsOwnerShape[parts.Length];
            _convexLo = new float3[parts.Length];
            _convexHi = new float3[parts.Length];
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
                _banks[i] = bank;
                _blockOwnerOf[i] = this;

                vBase += r.vertexCount;
                fBase += r.faceCount + 1;
                iBase += r.faceIndexCount;
                eBase += r.edgeCount;

                _meshes.Add(parts[i].mesh);
                _convexLo[i] = parts[i].lo;
                _convexHi[i] = parts[i].hi;
                AddToLocalBounds(parts[i].lo, parts[i].hi);
                HoldSource(parts[i].source);
            }
        }

        /// <summary>One hold per source, however many of its meshes this owner uses; held before it is recorded.</summary>
        private void HoldSource(PhysicsShapeSource source)
        {
            int at = _sources.IndexOf(source);
            if (at < 0)
            {
                source.Acquire();
                at = _sources.Count;
                _sources.Add(source);
            }

            _sourceOf.Add(at);
        }

        private static long Align16(long bytes)
        {
            return (bytes + 15) & ~15L;
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

    // Only made if a caller requests the Provisional shape's public mesh-list view. The build uses MeshOf
    // directly, and a Final owner still exposes its original List. Enumeration is not part of the cut path.
    internal sealed class PhysicsShapeMeshView : IReadOnlyList<Mesh>
    {
        private readonly PhysicsOwnerShape _shape;

        internal PhysicsShapeMeshView(PhysicsOwnerShape shape) { _shape = shape; }
        public int Count => _shape.ConvexCount;
        public Mesh this[int index] => _shape.MeshOf(index);

        public IEnumerator<Mesh> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
