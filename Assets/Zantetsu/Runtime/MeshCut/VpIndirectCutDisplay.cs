using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

// So that the tests can drive the frame counter this display reads, which the product path takes from the engine.
[assembly: InternalsVisibleTo("Zantetsu.Core.EditModeTests")]

namespace Zantetsu.MeshCut
{
    /// <summary>What one cut request did to the display.</summary>
    public enum VpIndirectCutOutcome
    {
        /// <summary>The parent was replaced by the sides the cut produced, and the buffers carry them.</summary>
        Swapped = 0,

        /// <summary>
        /// The plane missed: one side reused the input and the other was empty, so the parent is still shown and
        /// nothing was registered, transferred or retired. This is the case DESIGN 4.5.6 counts as zero index transfers.
        /// </summary>
        KeptParent = 1,

        /// <summary>The cut refused the request. Nothing was cut, nothing was transferred, the parent is still shown.</summary>
        CutRefused = 2,

        /// <summary>
        /// The cut ran out of its attempts. Nothing was published or transferred; the figures to call again with are in
        /// <see cref="VpIndirectCutResult.cut"/>'s required options.
        /// </summary>
        CapacityRetry = 3,

        /// <summary>
        /// The cut succeeded but the display could not be prepared — a material, a capacity or a registration — so the
        /// sides it had published were given back and the parent is still shown, drawn from the same buffer contents as
        /// before. The vertices the cut appended stay in the storage (see
        /// <see cref="VpIndirectCutResult.appendedVerticesKept"/>).
        /// </summary>
        DisplayPreparationFailed = 4,
    }

    /// <summary>The outcome of one cut request, with what it actually transferred.</summary>
    public readonly struct VpIndirectCutResult
    {
        internal VpIndirectCutResult(
            VpIndirectCutOutcome outcome,
            VpStorageCutResult cut,
            int shownCount,
            int appendedVerticesKept,
            int vertexTransfers,
            int indexTransfers,
            int transferredVertices,
            int transferredIndices)
        {
            this.outcome = outcome;
            this.cut = cut;
            this.shownCount = shownCount;
            this.appendedVerticesKept = appendedVerticesKept;
            this.vertexTransfers = vertexTransfers;
            this.indexTransfers = indexTransfers;
            this.transferredVertices = transferredVertices;
            this.transferredIndices = transferredIndices;
        }

        public readonly VpIndirectCutOutcome outcome;

        /// <summary>The cut's own result: its status and, on a retry, the options to call again with.</summary>
        public readonly VpStorageCutResult cut;

        /// <summary>How many geometries the display shows now.</summary>
        public readonly int shownCount;

        /// <summary>
        /// Vertices the cut committed that no display uses, which happens only when the cut succeeded and the display
        /// preparation then failed. The storage is append-only, so these are not given back.
        /// </summary>
        public readonly int appendedVerticesKept;

        /// <summary>How many vertex transfers this request issued: one for a cut that appended vertices, otherwise none.</summary>
        public readonly int vertexTransfers;

        /// <summary>
        /// How many index transfers this request issued. A cut that produces sides issues exactly one, covering the
        /// contiguous run both sides occupy; a cut that reuses the input issues none (DESIGN 4.5.6).
        /// </summary>
        public readonly int indexTransfers;

        public readonly int transferredVertices;

        public readonly int transferredIndices;
    }

    /// <summary>
    /// A Stage 3 indexed indirect display of one stored geometry that can be cut once while it is on screen, swapping
    /// the display to the two sides the cut produced. Nothing goes through a Unity Mesh, and nothing here depends on the
    /// Unity Mesh display: both are built on the same storage, the same cut API and the same reference table.
    /// <para>
    /// **The GPU buffers mirror the storage.** A vertex is transferred to the position of its own global number and a
    /// published index range to the position it occupies in the storage, so a draw leaves baseVertexIndex at 0 and a cut
    /// needs no renumbering of anything that was already there. The buffers are made once, at the storage's own shape,
    /// and kept until this display ends: no growth, no replacement, no fence of our own. A cut that would not fit is a
    /// plain refusal, not a reallocation.
    /// </para>
    /// <para>
    /// **What a cut transfers.** Only the vertices it appended, as one range, and only the contiguous run its two sides
    /// occupy, as one transfer, both read straight out of the storage: no index or vertex is copied into an array of
    /// ours on the way. Inherited vertices and the indices that were already there are neither repacked nor sent again.
    /// A cut that reuses its input transfers nothing at all and leaves the commands alone.
    /// </para>
    /// <para>
    /// **When updates happen.** A cut request is held, not applied where it is made. <see cref="BeginFrame()"/> opens a
    /// frame and applies whatever is outstanding — the cut, the transforms — and <see cref="Render"/> then registers the
    /// draws. A frame is identified by the engine's frame number, so calling <see cref="BeginFrame()"/> again within the
    /// same frame does nothing: once a frame has drawn, nothing reopens it, and a request made then waits for the next
    /// frame. Every camera and the shadow pass of one frame therefore read the same settled data. Writing into these
    /// buffers between frames is an ordinary Unity update through the ordinary API; <c>SetData</c> having returned is
    /// not a statement about GPU work, and no wait for GPU completion is added here.
    /// </para>
    /// <para>
    /// This display owns the buffers, the batch, and the geometry registrations and display instances it takes from the
    /// reference table, and gives all of them back in <see cref="Dispose"/>. Materials and textures are the caller's.
    /// An ordinary refusal — bad input, no room, an unresolved material — changes nothing at all. A GPU call that throws
    /// is different: transfers already made cannot be taken back, so the exception propagates, the display stops
    /// accepting draws and updates, and <see cref="Dispose"/> is what is left to call. Main thread only.
    /// </para>
    /// </summary>
    public sealed class VpIndirectCutDisplay : IDisposable
    {
        private sealed class Shown
        {
            public VpStoredGeometry geometry;
            public VpGeometryReference reference;
            public VpDisplayInstanceReference instance;
            public bool hasRegistration;
            public Matrix4x4 objectToWorld;
            public VpIndirectCommand[] commands;
            public Material[] commandMaterials;
        }

        private readonly VpCpuGeometryStorage _storage;
        private readonly VpGeometryReferenceTable _table;
        private readonly IReadOnlyDictionary<int, Material> _materials;
        private readonly Material _shadowMaterial;
        private readonly VpGpuIndexedGeometryBuffers _buffers;
        private readonly VpIndexedIndirectDrawBatch _batch;
        private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();

        // Which frame it is. The product path reads the engine's own counter; a test drives this instead, so that the
        // frame a test means is the frame both BeginFrame and Render see.
        private readonly Func<int> _frameSource;

        // What is shown, and the list the next arrangement is built in. Room for both is taken before a cut begins and
        // never during the swap, which is a reference exchange: once the children are on the GPU, nothing can fail.
        private List<Shown> _shown = new List<Shown>(4);
        private List<Shown> _spare = new List<Shown>(4);

        private bool _pendingCut;

        // The geometry the held request is for, kept as the geometry itself. A position in the shown list would mean
        // something else by the time the request is applied; this cannot come to mean a different geometry.
        private VpStoredGeometry _pendingTarget;
        private float4 _pendingPlane;
        private VpStorageCutOptions _pendingOptions;
        private bool _instancesOutstanding;
        private bool _frameOpened;
        private int _frameId;
        private bool _drawRegisteredThisFrame;
        private bool _broken;
        private bool _disposed;

        private VpIndirectCutDisplay(
            VpCpuGeometryStorage storage,
            VpGeometryReferenceTable table,
            IReadOnlyDictionary<int, Material> materials,
            Material shadowMaterial,
            VpGpuIndexedGeometryBuffers buffers,
            VpIndexedIndirectDrawBatch batch,
            Func<int> frameSource)
        {
            _storage = storage;
            _table = table;
            _materials = materials;
            _shadowMaterial = shadowMaterial;
            _buffers = buffers;
            _batch = batch;
            _frameSource = frameSource;
        }

        private int CurrentFrame => _frameSource != null ? _frameSource() : Time.frameCount;

        /// <summary>How many geometries are on screen.</summary>
        public int ShownCount => _shown.Count;

        public bool IsDisposed => _disposed;

        /// <summary>
        /// Whether a GPU update was interrupted part-way. Such a display draws and updates no more, because what
        /// reached the GPU cannot be established; only <see cref="Dispose"/> is left to call.
        /// </summary>
        public bool IsBroken => _broken;

        /// <summary>Vertex transfers this display has issued, its first upload included.</summary>
        public int VertexTransfers { get; private set; }

        /// <summary>Index transfers this display has issued, its first upload included.</summary>
        public int IndexTransfers { get; private set; }

        /// <summary>Command and instance uploads this display has issued, its first upload included.</summary>
        public int CommandUploads { get; private set; }

        /// <summary>The result of the last cut request this display applied; the outcome is defaulted before the first.</summary>
        public VpIndirectCutResult LastCutResult { get; private set; }

        /// <summary>Whether a cut request is waiting for the next frame.</summary>
        public bool HasPendingCut => _pendingCut;

        /// <summary>Whether a transform change is waiting for the next frame.</summary>
        public bool HasPendingTransform => _instancesOutstanding;

        /// <summary>Whether a draw has already been registered in the frame opened by <see cref="BeginFrame()"/>.</summary>
        public bool HasDrawnThisFrame => _drawRegisteredThisFrame;

        public VpStoredGeometry GetShownGeometry(int index)
        {
            ThrowIfDisposed();
            return _shown[index].geometry;
        }

        public Matrix4x4 GetShownTransform(int index)
        {
            ThrowIfDisposed();
            return _shown[index].objectToWorld;
        }

        /// <summary>How many draw commands the geometry at <paramref name="index"/> contributes: one per submesh.</summary>
        public int GetShownCommandCount(int index)
        {
            ThrowIfDisposed();
            return _shown[index].commands.Length;
        }

        /// <summary>
        /// One command of a shown geometry, in submesh order. Its index start is a position in the index buffer, which
        /// mirrors the storage, so it is where that submesh's indices lie in the storage itself.
        /// </summary>
        public VpIndirectCommand GetShownCommand(int index, int command)
        {
            ThrowIfDisposed();
            return _shown[index].commands[command];
        }

        /// <summary>The Material a command is drawn with, resolved from its stored source material index. It stays the caller's.</summary>
        public Material GetShownCommandMaterial(int index, int command)
        {
            ThrowIfDisposed();
            return _shown[index].commandMaterials[command];
        }

        /// <summary>
        /// Builds the display of one stored geometry and transfers everything it needs: the committed vertices at their
        /// own numbers and the geometry's published indices where they lie. <paramref name="shadowMaterial"/> may be
        /// null, and then these commands cast no shadow.
        /// <para>
        /// The registration is the last thing taken, after every transfer and the commands are in place, because giving
        /// a registration back would retire the geometry's index range: a geometry this display could not show has to be
        /// left exactly as it was found. So a refusal here transfers nothing it keeps, registers nothing, retires
        /// nothing, and releases every buffer it made.
        /// </para>
        /// Returns false with a null display when an argument is null; when the geometry cannot be prepared; when a
        /// stored material index has no Material; when the commands or their instances exceed the capacities asked for
        /// here; or when the table has no room to register the geometry and show it.
        /// </summary>
        public static bool TryCreate(
            VpCpuGeometryStorage storage,
            VpGeometryReferenceTable table,
            VpStoredGeometry geometry,
            IReadOnlyDictionary<int, Material> materialsBySourceIndex,
            Material shadowMaterial,
            Matrix4x4 objectToWorld,
            int commandCapacity,
            int instanceCapacity,
            out VpIndirectCutDisplay display)
        {
            return TryCreate(
                storage,
                table,
                geometry,
                materialsBySourceIndex,
                shadowMaterial,
                objectToWorld,
                commandCapacity,
                instanceCapacity,
                null,
                out display);
        }

        /// <summary>
        /// The same, with the frame counter this display reads given by the caller instead of taken from the engine.
        /// It exists for tests, which have no frame loop to advance: <paramref name="frameSource"/> is what both
        /// <see cref="BeginFrame"/> and <see cref="Render"/> ask, so a test decides what "the same frame" means. The
        /// product path passes nothing and reads <c>Time.frameCount</c>.
        /// </summary>
        internal static bool TryCreate(
            VpCpuGeometryStorage storage,
            VpGeometryReferenceTable table,
            VpStoredGeometry geometry,
            IReadOnlyDictionary<int, Material> materialsBySourceIndex,
            Material shadowMaterial,
            Matrix4x4 objectToWorld,
            int commandCapacity,
            int instanceCapacity,
            Func<int> frameSource,
            out VpIndirectCutDisplay display)
        {
            display = null;
            if (storage == null || table == null || materialsBySourceIndex == null || commandCapacity <= 0 || instanceCapacity <= 0)
            {
                return false;
            }

            // ---- everything on the CPU first, so a refusal costs no GPU resource at all
            if (!TryPrepare(storage, materialsBySourceIndex, geometry, out VpIndirectCommand[] commands, out Material[] commandMaterials))
            {
                return false;
            }

            if (commands.Length > commandCapacity || commands.Length > instanceCapacity)
            {
                return false;
            }

            VpGpuIndexedGeometryBuffers buffers = null;
            VpIndexedIndirectDrawBatch batch = null;
            bool taken = false;
            try
            {
                // The buffers are the storage's shape, which is what makes every position mean the same on both sides.
                buffers = new VpGpuIndexedGeometryBuffers(storage.VertexCapacity, storage.IndexCapacity);
                batch = new VpIndexedIndirectDrawBatch(commandCapacity, instanceCapacity);

                var built = new VpIndirectCutDisplay(storage, table, materialsBySourceIndex, shadowMaterial, buffers, batch, frameSource);
                var shown = new Shown
                {
                    geometry = geometry,
                    objectToWorld = objectToWorld,
                    commands = commands,
                    commandMaterials = commandMaterials,
                };

                built._shown.Add(shown);
                if (!VpStoredGeometryTransfer.TryUploadCommittedVertices(storage, buffers.VertexBuffer, 0, storage.VertexCount, out int vertices)
                    || !VpStoredGeometryTransfer.TryUploadPublishedIndices(storage, buffers.IndexBuffer, geometry.indexRange, out int indices))
                {
                    return false;
                }

                built.CountTransfers(vertices, indices);
                if (!built.TryUploadCommandsFor(built._shown))
                {
                    return false;
                }

                // Last, with the GPU already holding everything: the registration, which is also the handover of this
                // geometry's retirement to the table.
                if (!table.TryRegisterGeometryWithDisplayInstance(
                        geometry, out VpGeometryReference reference, out VpDisplayInstanceReference instance))
                {
                    return false;
                }

                shown.reference = reference;
                shown.instance = instance;
                shown.hasRegistration = true;
                display = built;
                taken = true;
                return true;
            }
            finally
            {
                if (!taken)
                {
                    batch?.Dispose();
                    buffers?.Dispose();
                }
            }
        }

        /// <summary>
        /// Holds a cut of the one geometry on screen, to be applied when the next frame opens and never in the middle of
        /// a frame that has already drawn. <paramref name="plane"/> is in the geometry's own coordinates — the space the
        /// stored vertices are in — not in world space, and the display's transform is not applied to it.
        /// Returns false when a request is already waiting or the display does not show exactly one geometry.
        /// </summary>
        public bool TryRequestCut(float4 plane, in VpStorageCutOptions options)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            if (_shown.Count != 1)
            {
                return false;
            }

            return TryRequestCut(_shown[0].geometry, plane, options);
        }

        /// <summary>The request with the cut's default options.</summary>
        public bool TryRequestCut(float4 plane)
        {
            return TryRequestCut(plane, default);
        }

        /// <summary>
        /// Holds a cut of <paramref name="target"/>, which must be one of the geometries on screen now. The target is
        /// remembered as the geometry it is, so the request cannot come to mean a different one: when the frame opens,
        /// the display looks for that same geometry among what it shows, and refuses the cut if it is no longer there.
        /// Everything else on screen is left alone by the cut that follows.
        /// <para>
        /// <paramref name="plane"/> is in the target's own coordinates — the space the stored vertices are in — not in
        /// world space, and the display's transform is not applied to it. One request is held at a time.
        /// </para>
        /// Returns false when a request is already waiting, or when the target is not one of the shown geometries.
        /// </summary>
        public bool TryRequestCut(VpStoredGeometry target, float4 plane, in VpStorageCutOptions options)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            if (_pendingCut || IndexOfShown(target) < 0)
            {
                return false;
            }

            _pendingCut = true;
            _pendingTarget = target;
            _pendingPlane = plane;
            _pendingOptions = options;
            return true;
        }

        /// <summary>The targeted request with the cut's default options.</summary>
        public bool TryRequestCut(VpStoredGeometry target, float4 plane)
        {
            return TryRequestCut(target, plane, default);
        }

        /// <summary>The geometry the held request is for; false with a default when nothing is waiting.</summary>
        public bool TryGetPendingTarget(out VpStoredGeometry target)
        {
            ThrowIfDisposed();
            target = _pendingCut ? _pendingTarget : default;
            return _pendingCut;
        }

        /// <summary>
        /// Where <paramref name="geometry"/> is among the shown geometries, or -1. Identity is the published index
        /// range: the storage hands out one registration per range, so two shown geometries never share one.
        /// </summary>
        public int IndexOfShown(VpStoredGeometry geometry)
        {
            ThrowIfDisposed();
            for (int i = 0; i < _shown.Count; i++)
            {
                if (_shown[i].geometry.indexRange.Equals(geometry.indexRange))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Moves one shown geometry. The new transform is used from the next frame, like every other update, so a frame
        /// that has begun drawing keeps the placement it started with.
        /// </summary>
        public bool TrySetTransform(int index, Matrix4x4 objectToWorld)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            if (index < 0 || index >= _shown.Count)
            {
                return false;
            }

            _shown[index].objectToWorld = objectToWorld;
            _instancesOutstanding = true;
            return true;
        }

        /// <summary>
        /// Opens the current frame and applies whatever is outstanding — the held cut, the transforms — leaving the
        /// buffers settled for every draw of that frame. Calling it again within the same frame does nothing at all: a
        /// frame that has drawn is not reopened, and what is waiting waits for the next one.
        /// </summary>
        public void BeginFrame()
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            int frame = CurrentFrame;
            if (_frameOpened && _frameId == frame)
            {
                return;
            }

            _frameOpened = true;
            _frameId = frame;
            _drawRegisteredThisFrame = false;
            ApplyOutstanding();
        }

        /// <summary>
        /// Registers this frame's draws: one forward call per run of commands sharing a Material, each followed by its
        /// shadow call when a shadow material was given. Drawing does not settle anything by itself — the frame was
        /// settled when it opened — so a second camera of the same frame draws exactly the same data.
        /// </summary>
        public void Render(int layer, Camera camera = null)
        {
            ThrowIfDisposed();
            ThrowIfBroken();

            // A frame draws only what it settled, and a frame nobody opened has settled nothing. Being open for an
            // earlier frame is not being open for this one, so nothing is registered and nothing is updated here.
            if (!_frameOpened || _frameId != CurrentFrame)
            {
                throw new InvalidOperationException(
                    "BeginFrame has not opened this frame, so there is nothing settled to draw. Call BeginFrame once per frame, before drawing.");
            }

            _drawRegisteredThisFrame = true;
            int command = 0;
            for (int s = 0; s < _shown.Count; s++)
            {
                Shown shown = _shown[s];
                int start = 0;
                while (start < shown.commandMaterials.Length)
                {
                    int end = start + 1;
                    while (end < shown.commandMaterials.Length
                        && ReferenceEquals(shown.commandMaterials[end], shown.commandMaterials[start]))
                    {
                        end++;
                    }

                    if (_shadowMaterial != null)
                    {
                        _batch.Render(shown.commandMaterials[start], _shadowMaterial, _properties, _buffers, layer, command + start, end - start, camera);
                    }
                    else
                    {
                        _batch.RenderForward(shown.commandMaterials[start], _properties, _buffers, layer, command + start, end - start, camera);
                    }

                    start = end;
                }

                command += shown.commands.Length;
            }
        }

        /// <summary>
        /// Gives back everything this display owns: the buffers, the batch, and every geometry registration and display
        /// instance it took. The borrowed materials and the storage are left alone. Disposing again does nothing, and a
        /// display whose GPU update was interrupted is disposed like any other. This is the owner's ordinary teardown
        /// and says nothing about work already submitted to the GPU, which is why the caller stops drawing first.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (Shown shown in _shown)
            {
                ReleaseRegistration(shown);
            }

            _shown.Clear();

            // The other list holds nothing of its own - an arrangement that was taken up is in _shown now, and one that
            // was abandoned was reclaimed - but it is emptied so that nothing is held on to after this.
            _spare.Clear();
            _batch.Dispose();
            _buffers.Dispose();
        }

        /// <summary>
        /// Applies what is waiting, in the one order that matters: the cut first, because it settles what is shown, and
        /// then the transforms, if the cut did not already carry them across. Only what actually reached the GPU is
        /// taken off the waiting list — a cut that changed nothing leaves a held transform held.
        /// </summary>
        private void ApplyOutstanding()
        {
            bool commandsUploaded = false;
            if (_pendingCut)
            {
                _pendingCut = false;
                VpIndirectCutResult result = ApplyCut(_pendingTarget, _pendingPlane, _pendingOptions);
                _pendingTarget = default;
                LastCutResult = result;

                // The swap is the only outcome that rewrote the commands, and it wrote them from the transforms as they
                // stand, so it carries a transform requested in the same frame. Every other outcome left them alone.
                commandsUploaded = result.outcome == VpIndirectCutOutcome.Swapped;
            }

            if (!_instancesOutstanding)
            {
                return;
            }

            if (commandsUploaded || TryUploadCommandsFor(_shown))
            {
                _instancesOutstanding = false;
            }
        }

        private VpIndirectCutResult ApplyCut(VpStoredGeometry target, float4 plane, VpStorageCutOptions options)
        {
            // The target is looked for by its own identity, not by where it sat when the request was made: whatever has
            // happened since, this cut is either of that geometry or of nothing.
            int targetIndex = IndexOfShown(target);
            if (targetIndex < 0)
            {
                return new VpIndirectCutResult(VpIndirectCutOutcome.CutRefused, default, _shown.Count, 0, 0, 0, 0, 0);
            }

            Shown subject = _shown[targetIndex];
            var cut = default(VpStorageCutResult);

            // The room to hold both sides, the children made from them, and the arrangement that would replace what is
            // shown, is taken here: before the lease, before the cut, while no side exists yet. From the moment the cut
            // publishes one, every path out of this call has to be able to give it back, and a path with an allocation
            // still ahead of it is not one. After the cut these are only written into.
            var sides = new VpStorageCutSide[2];
            var prepared = new Shown[2];
            EnsureRoom(_shown.Count + 1);
            _spare.Clear();

            if (!VpStorageCutInput.TryAcquire(_storage, subject.geometry, out VpStorageCutInput input))
            {
                return new VpIndirectCutResult(VpIndirectCutOutcome.CutRefused, cut, _shown.Count, 0, 0, 0, 0, 0);
            }

            int verticesBefore = _storage.VertexCount;
            using (input)
            {
                VpStorageCut.TryExecute(_storage, input, plane, options, out cut);
            }

            if (cut.status != VpStorageCutStatus.Ok)
            {
                VpIndirectCutOutcome refused = cut.status == VpStorageCutStatus.CapacityRetry
                    ? VpIndirectCutOutcome.CapacityRetry
                    : VpIndirectCutOutcome.CutRefused;
                return new VpIndirectCutResult(refused, cut, _shown.Count, 0, 0, 0, 0, 0);
            }

            // The plane missed: the input is lent back and nothing on the GPU changes. Zero transfers, by contract.
            if (cut.positive.IsBorrowed || cut.negative.IsBorrowed)
            {
                return new VpIndirectCutResult(VpIndirectCutOutcome.KeptParent, cut, _shown.Count, 0, 0, 0, 0, 0);
            }

            sides[0] = cut.positive;
            sides[1] = cut.negative;
            int appended = _storage.VertexCount - verticesBefore;
            int vertexTransfers = 0;
            int indexTransfers = 0;
            int transferredVertices = 0;
            int transferredIndices = 0;
            bool touchedGpu = false;
            try
            {
                if (!TryPrepareSides(sides, subject, prepared))
                {
                    _spare.Clear();
                    Reclaim(sides, prepared);
                    return new VpIndirectCutResult(
                        VpIndirectCutOutcome.DisplayPreparationFailed, cut, _shown.Count, appended, 0, 0, 0, 0);
                }

                // The arrangement that would replace what is shown: every other geometry exactly as it is, in its own
                // order, with the target's place taken by the sides it became. Built in the room reserved above.
                for (int i = 0; i < _shown.Count; i++)
                {
                    if (i != targetIndex)
                    {
                        _spare.Add(_shown[i]);
                        continue;
                    }

                    foreach (Shown child in prepared)
                    {
                        if (child != null)
                        {
                            _spare.Add(child);
                        }
                    }
                }

                // Everything that can refuse has refused by now: capacities, materials and both registrations. What
                // follows writes to the GPU, and a GPU call that throws cannot be taken back.
                if (appended > 0)
                {
                    touchedGpu = true;
                    if (!VpStoredGeometryTransfer.TryUploadCommittedVertices(
                            _storage, _buffers.VertexBuffer, verticesBefore, appended, out transferredVertices))
                    {
                        _spare.Clear();
                        Reclaim(sides, prepared);
                        return new VpIndirectCutResult(
                            VpIndirectCutOutcome.DisplayPreparationFailed, cut, _shown.Count, appended, 0, 0, 0, 0);
                    }

                    vertexTransfers = 1;
                    VertexTransfers++;
                }

                // One transfer for both sides: they are one contiguous run in the storage and go across as one. What
                // the other geometries already have in the buffers is not written again.
                touchedGpu = true;
                if (!VpStoredGeometryTransfer.TryUploadPublishedIndexRun(
                        _storage,
                        _buffers.IndexBuffer,
                        cut.positive.geometry.indexRange,
                        cut.negative.geometry.indexRange,
                        out transferredIndices))
                {
                    _spare.Clear();
                    Reclaim(sides, prepared);
                    return new VpIndirectCutResult(
                        VpIndirectCutOutcome.DisplayPreparationFailed, cut, _shown.Count, appended, vertexTransfers, 0, transferredVertices, 0);
                }

                indexTransfers = 1;
                IndexTransfers++;

                // The whole arrangement's commands go across while the display still shows the old one, so a refusal
                // here leaves it exactly as it was: the batch keeps what it had, and nothing already shown was written
                // over - the cut only ever wrote past it.
                if (!TryUploadCommandsFor(_spare))
                {
                    _spare.Clear();
                    Reclaim(sides, prepared);
                    return new VpIndirectCutResult(
                        VpIndirectCutOutcome.DisplayPreparationFailed, cut, _shown.Count, appended,
                        vertexTransfers, indexTransfers, transferredVertices, transferredIndices);
                }

                // Only now, with the GPU holding the new arrangement, does the display change hands - by exchanging the
                // two lists, which needs nothing. The target alone is retired; every other geometry keeps its
                // registration, its indices, its materials and its transform.
                List<Shown> previous = _shown;
                _shown = _spare;
                _spare = previous;
                _spare.Clear();
                ReleaseRegistration(subject);
                return new VpIndirectCutResult(
                    VpIndirectCutOutcome.Swapped, cut, _shown.Count, 0,
                    vertexTransfers, indexTransfers, transferredVertices, transferredIndices);
            }
            catch
            {
                // The published sides are given back whatever went wrong. What was already written to the GPU cannot
                // be, so if the update had begun this display stops drawing and updating and waits to be disposed.
                _spare.Clear();
                Reclaim(sides, prepared);
                if (touchedGpu)
                {
                    _broken = true;
                }

                throw;
            }
        }

        /// <summary>Makes sure both lists can hold <paramref name="count"/> entries, so that the swap needs nothing.</summary>
        private void EnsureRoom(int count)
        {
            if (_shown.Capacity < count)
            {
                _shown.Capacity = count;
            }

            if (_spare.Capacity < count)
            {
                _spare.Capacity = count;
            }
        }

        /// <summary>
        /// Prepares both produced sides: their commands at the positions their indices occupy in the storage, their
        /// materials, the capacities they need, and finally their registrations. Each side's record exists before its
        /// registration is taken, so a taken registration is recorded at once and none is ever left untracked.
        /// </summary>
        private bool TryPrepareSides(VpStorageCutSide[] sides, Shown subject, Shown[] prepared)
        {
            // What the whole arrangement would need, not only the new sides: everything else stays on screen and keeps
            // its commands, so they are part of what has to fit.
            int commandTotal = 0;
            for (int i = 0; i < _shown.Count; i++)
            {
                if (!ReferenceEquals(_shown[i], subject))
                {
                    commandTotal += _shown[i].commands.Length;
                }
            }

            int producedCommands = 0;
            for (int i = 0; i < sides.Length; i++)
            {
                if (!sides[i].IsProduced)
                {
                    continue;
                }

                if (!TryPrepare(_storage, _materials, sides[i].geometry, out VpIndirectCommand[] commands, out Material[] materials))
                {
                    return false;
                }

                prepared[i] = new Shown
                {
                    geometry = sides[i].geometry,

                    // The sides take the place of what they were cut from, so they take its placement too.
                    objectToWorld = subject.objectToWorld,
                    commands = commands,
                    commandMaterials = materials,
                };

                producedCommands += commands.Length;
            }

            commandTotal += producedCommands;
            if (producedCommands == 0 || commandTotal > _batch.CommandCapacity || commandTotal > _batch.InstanceCapacity)
            {
                return false;
            }

            for (int i = 0; i < prepared.Length; i++)
            {
                if (prepared[i] == null)
                {
                    continue;
                }

                if (!_table.TryRegisterGeometryWithDisplayInstance(
                        prepared[i].geometry, out VpGeometryReference reference, out VpDisplayInstanceReference instance))
                {
                    return false;
                }

                prepared[i].reference = reference;
                prepared[i].instance = instance;
                prepared[i].hasRegistration = true;
            }

            return true;
        }

        /// <summary>
        /// Builds one geometry's commands at the position its indices already occupy in the storage, and resolves a
        /// Material for each. That position is what makes the GPU a mirror of the storage. No index is copied out: the
        /// transfer reads the storage where it lies.
        /// </summary>
        private static bool TryPrepare(
            VpCpuGeometryStorage storage,
            IReadOnlyDictionary<int, Material> materialsBySourceIndex,
            VpStoredGeometry geometry,
            out VpIndirectCommand[] commands,
            out Material[] commandMaterials)
        {
            commands = null;
            commandMaterials = null;
            if (!storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out int indexStart, out _)
                || state != VpIndexRangeState.Published
                || !VpStoredGeometryDraw.TryBuildCommands(storage, geometry, indexStart, out commands, out int[] materialIndices, out _))
            {
                return false;
            }

            var resolved = new Material[commands.Length];
            for (int c = 0; c < commands.Length; c++)
            {
                if (!materialsBySourceIndex.TryGetValue(materialIndices[c], out Material material) || material == null)
                {
                    commands = null;
                    return false;
                }

                resolved[c] = material;
            }

            commandMaterials = resolved;
            return true;
        }

        /// <summary>
        /// Writes the commands of the given entries, and one instance transform per command, as one upload. The batch
        /// refuses without changing anything when they do not fit, which is what lets the caller offer the children
        /// while the parent is still the one on screen.
        /// </summary>
        private bool TryUploadCommandsFor(IReadOnlyList<Shown> entries)
        {
            int total = 0;
            for (int e = 0; e < entries.Count; e++)
            {
                total += entries[e].commands.Length;
            }

            var commands = new VpIndirectCommand[total];
            var transforms = new Matrix4x4[total];
            int next = 0;
            for (int e = 0; e < entries.Count; e++)
            {
                Shown entry = entries[e];
                for (int c = 0; c < entry.commands.Length; c++)
                {
                    commands[next] = entry.commands[c];
                    transforms[next] = entry.objectToWorld;
                    next++;
                }
            }

            bool uploaded;
            try
            {
                uploaded = _batch.TryUpload(commands, transforms, false);
            }
            catch
            {
                // This is the GPU write of this path, whoever called it: a refusal above changes nothing, but an
                // exception may leave part of the arguments written, and that cannot be taken back.
                _broken = true;
                throw;
            }

            if (!uploaded)
            {
                return false;
            }

            CommandUploads++;
            return true;
        }

        /// <summary>
        /// Gives back every side the cut published that did not end up shown: one whose registration was taken goes back
        /// through the reference table, which retires its index range with the registration, and one that never got that
        /// far — including a side prepared but refused a registration — is retired straight in the storage, because
        /// nothing else knows it exists. What the cut appended to the vertices stays where it is.
        /// </summary>
        private void Reclaim(VpStorageCutSide[] sides, Shown[] prepared)
        {
            for (int i = 0; i < sides.Length; i++)
            {
                if (prepared[i] != null && prepared[i].hasRegistration)
                {
                    ReleaseRegistration(prepared[i]);
                }
                else if (sides[i].IsProduced)
                {
                    _storage.TryRetireIndices(sides[i].geometry.indexRange);
                }

                prepared[i] = null;
            }
        }

        /// <summary>The instance first, because a geometry a live instance references cannot be retired, then the geometry.</summary>
        private void ReleaseRegistration(Shown shown)
        {
            if (shown == null || !shown.hasRegistration)
            {
                return;
            }

            _table.TryRetireDisplayInstance(shown.instance);
            _table.TryRetireGeometry(shown.reference);
            shown.instance = default;
            shown.reference = default;
            shown.hasRegistration = false;
        }

        private void CountTransfers(int vertices, int indices)
        {
            if (vertices > 0)
            {
                VertexTransfers++;
            }

            if (indices > 0)
            {
                IndexTransfers++;
            }
        }

        private void ThrowIfBroken()
        {
            if (_broken)
            {
                throw new InvalidOperationException(
                    "A GPU update of this VpIndirectCutDisplay was interrupted, so what reached the GPU cannot be established. "
                    + "It draws and updates no more; dispose it.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpIndirectCutDisplay));
            }
        }
    }
}
