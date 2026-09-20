using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    /// <summary>What one cut request did to the display.</summary>
    public enum VpCutDisplayOutcome
    {
        /// <summary>The parent was replaced by the sides the cut produced. The parent's display is gone.</summary>
        Swapped = 0,

        /// <summary>
        /// The plane missed: one side reused the input and the other was empty, so the parent is still shown and
        /// nothing was registered, created or retired.
        /// </summary>
        KeptParent = 1,

        /// <summary>The cut refused the request. Nothing was cut and the parent is still shown.</summary>
        CutRefused = 2,

        /// <summary>
        /// The cut ran out of its attempts. Nothing was published and the parent is still shown; the requirement to
        /// call again with is in <see cref="VpCutDisplayResult.cut"/>'s required options.
        /// </summary>
        CapacityRetry = 3,

        /// <summary>
        /// The cut succeeded but the display could not be prepared, so the parent is still shown and the children this
        /// request had begun to build were taken back. **The vertices the cut appended stay in the storage**: the
        /// display was rolled back, the storage was not, and it cannot be (see
        /// <see cref="VpCutDisplayResult.appendedVerticesKept"/>).
        /// </summary>
        DisplayPreparationFailed = 4,
    }

    /// <summary>The outcome of one cut request, with the cut's own result for the caller to read.</summary>
    public readonly struct VpCutDisplayResult
    {
        internal VpCutDisplayResult(VpCutDisplayOutcome outcome, VpStorageCutResult cut, int shownCount, int appendedVerticesKept)
        {
            this.outcome = outcome;
            this.cut = cut;
            this.shownCount = shownCount;
            this.appendedVerticesKept = appendedVerticesKept;
        }

        public readonly VpCutDisplayOutcome outcome;

        /// <summary>The cut's own result, including its status and, on a retry, the options to call again with.</summary>
        public readonly VpStorageCutResult cut;

        /// <summary>How many geometries the display shows now.</summary>
        public readonly int shownCount;

        /// <summary>
        /// Vertices the cut committed to the storage that no display uses, which only happens when the cut succeeded
        /// and the display preparation then failed. The storage is append-only, so these are not given back.
        /// </summary>
        public readonly int appendedVerticesKept;
    }

    /// <summary>
    /// Shows geometries a <see cref="VpCpuGeometryStorage"/> owns as Unity MeshRenderers, and cuts what it shows once
    /// on request, swapping the display to the two sides only when everything they need is ready.
    /// <para>
    /// The order is what makes the swap safe. The parent's read lease is taken and given back around the cut itself;
    /// the children's meshes and materials are then prepared while the parent is still on screen and the children are
    /// held inactive; and only when every one of them is ready does the same main-thread call hide the parent, show the
    /// children, retire the parent's display instance and then its geometry, and destroy the parent's mesh. A failure
    /// anywhere before that leaves the parent exactly as it was and takes back whatever the request had begun to build.
    /// </para>
    /// <para>
    /// Ownership is divided and stated. The cut puts the two sides in the storage and shows nothing. This display owns
    /// what it registers in the <see cref="VpGeometryReferenceTable"/> — one geometry reference and one display
    /// instance per shown geometry — together with the Meshes and the GameObjects it creates, and it gives all of them
    /// back in <see cref="Dispose"/>. Materials and textures are the caller's and are only borrowed; nothing here
    /// destroys them. The reference table is the caller's too: this display uses the existing one and adds no counting
    /// scheme of its own, so a geometry is retired only through the table, only when no instance references it.
    /// </para>
    /// <para>
    /// A side the cut reports as reusing the input is the parent borrowed back, not a new owner: it is neither
    /// registered nor shown a second time. An empty side gets no mesh and no geometry. Main thread only, one cut per
    /// call, and no job, no physics, no collider, no VP3 buffer is touched.
    /// </para>
    /// </summary>
    public sealed class VpCutDisplay : IDisposable
    {
        private sealed class Shown
        {
            public VpStoredGeometry geometry;
            public VpGeometryReference reference;
            public VpDisplayInstanceReference instance;
            public Mesh mesh;
            public GameObject gameObject;

            // Whether each token was actually taken. Kept as flags rather than compared against a default token,
            // because giving one back that was never taken, or failing to give back one that was, are both wrong.
            public bool hasReference;
            public bool hasInstance;
        }

        // The two sides in the order they are always in: the positive one first. Held once, so that naming a child
        // needs nothing new while a published side is waiting to be shown or given back.
        private static readonly string[] k_sideSuffixes = { " +", " -" };

        private readonly VpCpuGeometryStorage _storage;
        private readonly VpGeometryReferenceTable _table;
        private readonly IReadOnlyDictionary<int, Material> _materials;
        private readonly Transform _parentTransform;
        // Room for the parent and for both sides, taken once here: neither showing the first geometry nor the swap that
        // replaces it with two has to grow this list, so neither of them can fail on the way to committing.
        private readonly List<Shown> _shown = new List<Shown>(2);
        private bool _disposed;

        private VpCutDisplay(
            VpCpuGeometryStorage storage,
            VpGeometryReferenceTable table,
            IReadOnlyDictionary<int, Material> materials,
            Transform parentTransform)
        {
            _storage = storage;
            _table = table;
            _materials = materials;
            _parentTransform = parentTransform;
        }

        /// <summary>How many geometries are on screen.</summary>
        public int ShownCount => _shown.Count;

        public bool IsDisposed => _disposed;

        /// <summary>The geometry shown at <paramref name="index"/>, in the order the display holds them.</summary>
        public VpStoredGeometry GetShownGeometry(int index)
        {
            ThrowIfDisposed();
            return _shown[index].geometry;
        }

        /// <summary>The GameObject showing the geometry at <paramref name="index"/>; it stays this display's.</summary>
        public GameObject GetShownObject(int index)
        {
            ThrowIfDisposed();
            return _shown[index].gameObject;
        }

        /// <summary>
        /// Shows one stored geometry. Returns false with a null display when an argument is null, when the geometry
        /// cannot be copied into a Mesh, when a material its submeshes name is not in <paramref name="materials"/>, or
        /// when the table has no room to register it and show it. A refusal leaves the geometry as it was found, still
        /// published in the storage: nothing is registered, nothing is kept and nothing is retired.
        /// </summary>
        public static bool TryCreate(
            VpCpuGeometryStorage storage,
            VpGeometryReferenceTable table,
            VpStoredGeometry geometry,
            IReadOnlyDictionary<int, Material> materials,
            Transform parentTransform,
            out VpCutDisplay display)
        {
            display = null;
            if (storage == null || table == null || materials == null)
            {
                return false;
            }

            var built = new VpCutDisplay(storage, table, materials, parentTransform);
            if (!built.TryShow(geometry, "vp cut display", null, out Shown shown))
            {
                return false;
            }

            // The list was given its room at construction, so this cannot fail after the resources were taken.
            shown.gameObject.SetActive(true);
            built._shown.Add(shown);
            display = built;
            return true;
        }

        /// <summary>
        /// Cuts the single geometry on screen by <paramref name="plane"/> and, if both the cut and the whole display
        /// preparation succeed, swaps the display to the sides it produced. Returns true only for
        /// <see cref="VpCutDisplayOutcome.Swapped"/> and <see cref="VpCutDisplayOutcome.KeptParent"/>; on every other
        /// outcome the parent is still shown and <paramref name="result"/> says what happened.
        /// <para>
        /// <paramref name="plane"/> is in the geometry's own coordinates — the space the stored vertices are in — not
        /// in world space, and the display's transform is not applied to it. A caller holding a world plane converts it
        /// itself; nothing here does, and no world-plane entry point exists.
        /// </para>
        /// <para>
        /// <paramref name="options"/> is passed to the cut untouched, so a caller answering a
        /// <see cref="VpCutDisplayOutcome.CapacityRetry"/> passes back the required options it was given. Nothing here
        /// retries by itself and nothing grows the storage.
        /// </para>
        /// </summary>
        public bool TryCutOnce(float4 plane, in VpStorageCutOptions options, out VpCutDisplayResult result)
        {
            ThrowIfDisposed();
            result = default;
            if (_shown.Count != 1)
            {
                result = new VpCutDisplayResult(VpCutDisplayOutcome.CutRefused, default, _shown.Count, 0);
                return false;
            }

            Shown parent = _shown[0];
            var cut = default(VpStorageCutResult);

            // The room to hold both sides, and the children made from them, is taken here — before the lease, before
            // the cut, before anything of the sort exists. From the moment the cut publishes a side, every path out of
            // this call has to be able to give that side back, and a path with an allocation still ahead of it is not
            // one. After the cut these are only written into.
            var sides = new VpStorageCutSide[2];
            var children = new Shown[2];

            // The lease is held around the cut and given back here, before anything is built: it never outlives the call.
            if (!VpStorageCutInput.TryAcquire(_storage, parent.geometry, out VpStorageCutInput input))
            {
                result = new VpCutDisplayResult(VpCutDisplayOutcome.CutRefused, default, _shown.Count, 0);
                return false;
            }

            using (input)
            {
                VpStorageCut.TryExecute(_storage, input, plane, options, out cut);
            }

            if (cut.status != VpStorageCutStatus.Ok)
            {
                // Nothing was published, so the parent simply stays on screen.
                VpCutDisplayOutcome refused = cut.status == VpStorageCutStatus.CapacityRetry
                    ? VpCutDisplayOutcome.CapacityRetry
                    : VpCutDisplayOutcome.CutRefused;
                result = new VpCutDisplayResult(refused, cut, _shown.Count, 0);
                return false;
            }

            // The plane missed: one side is the parent borrowed back and the other is empty. Nothing is registered,
            // nothing is shown twice, and the parent keeps the display it already has.
            if (cut.positive.IsBorrowed || cut.negative.IsBorrowed)
            {
                result = new VpCutDisplayResult(VpCutDisplayOutcome.KeptParent, cut, _shown.Count, 0);
                return true;
            }

            // ---- everything the children need, while the parent is still shown and they are not
            // Both sides go into the array taken above, each in its own place, and keep it whether or not they end up
            // shown. Whatever happens next, every side the cut published is either shown or given back. A side that
            // reuses the input is the parent and never reaches this point.
            sides[0] = cut.positive;
            sides[1] = cut.negative;
            bool ready = true;
            try
            {
                for (int i = 0; i < sides.Length; i++)
                {
                    if (!sides[i].IsProduced)
                    {
                        continue;
                    }

                    if (!TryShow(sides[i].geometry, parent.gameObject.name + k_sideSuffixes[i], parent.gameObject.transform, out Shown child))
                    {
                        ready = false;
                        break;
                    }

                    children[i] = child;
                }
            }
            catch
            {
                Reclaim(sides, children);
                throw;
            }

            int preparedCount = 0;
            foreach (Shown child in children)
            {
                if (child != null)
                {
                    preparedCount++;
                }
            }

            if (!ready || preparedCount == 0)
            {
                // The display is rolled back and both sides are given back; the storage's appended vertices and
                // metadata are not, and cannot be: that part of the storage is append-only.
                Reclaim(sides, children);
                result = new VpCutDisplayResult(
                    VpCutDisplayOutcome.DisplayPreparationFailed, cut, _shown.Count, AppendedVertices(in cut));
                return false;
            }

            // ---- the swap itself, with nothing left that can fail
            // Nothing from here on asks for anything: the list was given room for two at construction, Clear keeps that
            // room, and at most two children go back into it. The parent is not let go of until they are all in.
            foreach (Shown child in children)
            {
                if (child != null)
                {
                    child.gameObject.SetActive(true);
                }
            }

            parent.gameObject.SetActive(false);
            _shown.Clear();
            foreach (Shown child in children)
            {
                if (child != null)
                {
                    _shown.Add(child);
                }
            }

            Release(parent);
            result = new VpCutDisplayResult(VpCutDisplayOutcome.Swapped, cut, _shown.Count, 0);
            return true;
        }

        /// <summary>The cut with the default options; see <see cref="TryCutOnce(float4, in VpStorageCutOptions, out VpCutDisplayResult)"/>.</summary>
        public bool TryCutOnce(float4 plane, out VpCutDisplayResult result)
        {
            return TryCutOnce(plane, default, out result);
        }

        /// <summary>
        /// Gives back everything this display owns: every Mesh and GameObject it made, and every geometry reference and
        /// display instance it registered, each exactly once. The borrowed materials and the storage are left alone.
        /// Disposing again does nothing.
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
                Release(shown);
            }

            _shown.Clear();
        }

        /// <summary>
        /// Registers one geometry, copies it into a Mesh and builds its renderer, held inactive. When
        /// <paramref name="inheritFrom"/> is given, the new object takes that transform's place instead of sitting at
        /// the identity under the display's parent transform. Returns false having given back whatever it had taken and
        /// having registered nothing, so the geometry is left exactly as it was found and the caller decides what
        /// becomes of it.
        /// </summary>
        private bool TryShow(VpStoredGeometry geometry, string name, Transform inheritFrom, out Shown shown)
        {
            shown = null;
            var built = new Shown { geometry = geometry };
            try
            {
                // Everything that can fail without a registration is done first, because giving a registration back is
                // not an undo: the table retires the geometry's index range along with it. A geometry this display was
                // handed and could not show has to be left exactly as it was found.
                if (!VpStoredGeometryMesh.TryCreateMesh(_storage, geometry, out Mesh mesh))
                {
                    Release(built);
                    return false;
                }

                built.mesh = mesh;
                if (!VpStoredGeometryMesh.TryResolveMaterials(_storage, geometry, _materials, out Material[] materials))
                {
                    Release(built);
                    return false;
                }

                var go = new GameObject(name);
                built.gameObject = go;
                go.SetActive(false);
                if (_parentTransform != null)
                {
                    go.transform.SetParent(_parentTransform, false);
                }

                if (inheritFrom != null)
                {
                    // A side takes the place of what it was cut from, not the identity: a display that had been moved,
                    // turned or scaled keeps its placement across the swap. The parent is taken from the same transform
                    // so that the local values mean the same thing under it.
                    go.transform.SetParent(inheritFrom.parent, false);
                    go.transform.localPosition = inheritFrom.localPosition;
                    go.transform.localRotation = inheritFrom.localRotation;
                    go.transform.localScale = inheritFrom.localScale;
                }

                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = materials;

                // The registration and the instance are taken in one step, which either does both or neither. Nothing
                // here has to undo a registration, and nothing here can: giving one back retires the geometry's index
                // range, and a geometry this display could not show is not this display's to retire.
                if (!_table.TryRegisterGeometryWithDisplayInstance(
                        geometry, out VpGeometryReference reference, out VpDisplayInstanceReference instance))
                {
                    Release(built);
                    return false;
                }

                built.reference = reference;
                built.hasReference = true;
                built.instance = instance;
                built.hasInstance = true;

                // Last: once this returns, the display owns the registration, the instance, the mesh and the object.
                shown = built;
                return true;
            }
            catch
            {
                Release(built);
                throw;
            }
        }

        /// <summary>
        /// Gives back every side the cut published that this call did not end up showing, by the only route each one
        /// has: a side that was registered goes back through the reference table, which retires its index range with
        /// the registration; a side that never got that far is retired straight in the storage, because nothing else
        /// knows it exists. A side that reuses the input is the parent and is never in this array. What the cut
        /// appended to the vertices and the metadata stays where it is.
        /// </summary>
        /// <summary>
        /// How many vertices one cut appended, named by the sides that share them. Not the distance the storage's
        /// high-water moved: a cut may be given room below it, and then that distance says nothing.
        /// </summary>
        private static int AppendedVertices(in VpStorageCutResult cut)
        {
            if (cut.positive.IsProduced)
            {
                return cut.positive.geometry.vertexCount;
            }

            return cut.negative.IsProduced ? cut.negative.geometry.vertexCount : 0;
        }

        private void Reclaim(VpStorageCutSide[] sides, Shown[] children)
        {
            for (int i = 0; i < sides.Length; i++)
            {
                if (children[i] != null)
                {
                    Release(children[i]);
                    children[i] = null;
                }
                else if (sides[i].IsProduced)
                {
                    _storage.TryRetireIndices(sides[i].geometry.indexRange);
                }
            }
        }

        /// <summary>
        /// Gives back one shown geometry's own resources, in the order the reference table requires: the instance
        /// first, because a geometry with a live instance cannot be retired, then the geometry, then the Unity objects.
        /// Each part is given back only if it was taken.
        /// </summary>
        private void Release(Shown shown)
        {
            if (shown == null)
            {
                return;
            }

            if (shown.hasInstance)
            {
                _table.TryRetireDisplayInstance(shown.instance);
                shown.instance = default;
                shown.hasInstance = false;
            }

            if (shown.hasReference)
            {
                _table.TryRetireGeometry(shown.reference);
                shown.reference = default;
                shown.hasReference = false;
            }

            if (shown.gameObject != null)
            {
                Destroy(shown.gameObject);
                shown.gameObject = null;
            }

            if (shown.mesh != null)
            {
                Destroy(shown.mesh);
                shown.mesh = null;
            }
        }

        private static void Destroy(UnityEngine.Object o)
        {
            if (o == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(o);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(o);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpCutDisplay));
            }
        }
    }
}
