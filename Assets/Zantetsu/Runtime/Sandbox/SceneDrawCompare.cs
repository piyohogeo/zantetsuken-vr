using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>One renderer of a placed instance that the scene draw comparison takes: what Unity draws of it, and what the display is given for it.</summary>
    public sealed class SceneDrawTarget
    {
        public string owner, path;
        public MeshRenderer renderer;
        public Mesh mesh;
        public SceneDrawMeshBank.Entry geometry;

        /// <summary>Per submesh, the display's source index of the material the renderer draws it with.</summary>
        public int[] sourceIndices;

        /// <summary>Where it stands, without its scale; and its scale, which goes into the vertices.</summary>
        public Matrix4x4 placement;
        public Vector3 scale = Vector3.one;

        public LogicalFragmentId fragment;
        public VpStoredGeometry stored;
        public bool shown;
    }

    /// <summary>A renderer the comparison cannot take, and why. It is never left out without a word.</summary>
    public readonly struct SceneDrawRefusal
    {
        public readonly string owner, renderer, reason;

        public SceneDrawRefusal(string owner, string renderer, string reason)
        {
            this.owner = owner;
            this.renderer = renderer;
            this.reason = reason;
        }

        public override string ToString() => owner + " / " + renderer + ": " + reason;
    }

    /// <summary>
    /// The scene draw comparison (TL, 2026-10-07): what a scene's placed instances are drawn as, read off the scene the
    /// same way whichever way it is then drawn -- every MeshRenderer of the instances given, with its mesh, its
    /// submeshes, its materials, its shadow settings and its placement -- and, for each, whether the cut world's
    /// display can draw the very same thing in its place.
    /// <para>
    /// **A target** is a renderer that Unity draws now and that the display can take: a MeshRenderer, enabled, with a
    /// mesh whose every submesh has a material, casting and receiving shadows as the display's bodies do, placed
    /// without shear or mirroring, its geometry at hand (<see cref="SceneDrawMeshBank"/>), and each of its materials
    /// answering to one the world's display draws with (<see cref="SourceIndexOf"/>). **Anything else drawn is
    /// refused with its reason** (<see cref="Refusals"/>): a comparison holds only when there is none. A renderer
    /// Unity does not draw either (switched off) is counted apart and is nobody's.
    /// </para>
    /// Nothing of the scene is changed here.
    /// </summary>
    public sealed class SceneDrawInventory
    {
        public readonly List<SceneDrawTarget> Targets = new List<SceneDrawTarget>();
        public readonly List<SceneDrawRefusal> Refusals = new List<SceneDrawRefusal>();

        public int Instances { get; private set; }
        public int Missing { get; private set; }
        public int NotDrawn { get; private set; }
        public int ListedAgain { get; private set; }
        public int Commands { get; private set; }
        public long Vertices { get; private set; }
        public long Indices { get; private set; }
        public int DistinctMeshes { get; private set; }
        public int DistinctMaterials { get; private set; }
        public int Scaled { get; private set; }

        public long Triangles => Indices / 3;

        /// <summary>
        /// Reads the instances' renderers. <paramref name="bank"/> holds the geometry of meshes that cannot be read
        /// here; a mesh that can be read and is not in it is converted into it now.
        /// </summary>
        public static SceneDrawInventory Collect(
            IEnumerable<KeyValuePair<string, Renderer[]>> instances, IReadOnlyList<CutWorldRoot.MaterialBinding> bound, SceneDrawMeshBank bank)
        {
            if (instances == null) throw new ArgumentNullException(nameof(instances));
            if (bank == null) throw new ArgumentNullException(nameof(bank));
            var made = new SceneDrawInventory();
            var seen = new HashSet<Renderer>();
            var meshes = new HashSet<Mesh>();
            var materials = new Dictionary<Material, KeyValuePair<int, string>>();
            foreach (KeyValuePair<string, Renderer[]> instance in instances)
            {
                made.Instances++;
                if (instance.Value == null) continue;
                foreach (Renderer listed in instance.Value)
                {
                    if (listed == null)
                    {
                        made.Missing++;
                        continue;
                    }

                    if (!seen.Add(listed))
                    {
                        made.ListedAgain++;
                        continue;
                    }

                    if (!listed.enabled || !listed.gameObject.activeInHierarchy || listed.forceRenderingOff)
                    {
                        made.NotDrawn++;
                        continue;
                    }

                    string why = made.TryTake(instance.Key, listed, bound, bank, materials, out SceneDrawTarget target);
                    if (why != null)
                    {
                        made.Refusals.Add(new SceneDrawRefusal(instance.Key, PathOf(listed.transform), why));
                        continue;
                    }

                    made.Targets.Add(target);
                    made.Commands += target.sourceIndices.Length;
                    made.Vertices += target.geometry.vertices.Length;
                    made.Indices += target.geometry.indices.Length;
                    meshes.Add(target.mesh);
                    if (target.scale != Vector3.one) made.Scaled++;
                }
            }

            made.DistinctMeshes = meshes.Count;
            foreach (KeyValuePair<Material, KeyValuePair<int, string>> m in materials)
            {
                if (m.Value.Value == null) made.DistinctMaterials++;
            }

            return made;
        }

        /// <summary>The same, of a scene's placed cuttables: each one's own renderers, as its builder listed them.</summary>
        public static SceneDrawInventory OfPlacedCuttables(
            IEnumerable<PlayableCityCuttable> cuttables, IReadOnlyList<CutWorldRoot.MaterialBinding> bound, SceneDrawMeshBank bank)
        {
            var instances = new List<KeyValuePair<string, Renderer[]>>();
            foreach (PlayableCityCuttable c in cuttables)
            {
                if (c != null) instances.Add(new KeyValuePair<string, Renderer[]>(c.name, c.instanceRenderers));
            }

            return Collect(instances, bound, bank);
        }

        // Null with the target, or why the renderer is not one.
        private string TryTake(
            string owner, Renderer listed, IReadOnlyList<CutWorldRoot.MaterialBinding> bound, SceneDrawMeshBank bank,
            Dictionary<Material, KeyValuePair<int, string>> materials, out SceneDrawTarget target)
        {
            target = null;
            if (!(listed is MeshRenderer renderer))
            {
                return "a " + listed.GetType().Name + ", not a MeshRenderer";
            }

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
            {
                return "no mesh";
            }

            if (renderer.isPartOfStaticBatch)
            {
                return "part of a static batch: its mesh is the batch's, not its own";
            }

            if (renderer.GetComponentInParent<LODGroup>() != null)
            {
                return "under a LOD group: Unity draws one level of it, the display would draw every renderer";
            }

            if (renderer.shadowCastingMode != ShadowCastingMode.On)
            {
                return "casts shadows as " + renderer.shadowCastingMode + "; the display's bodies cast one-sided";
            }

            if (!renderer.receiveShadows)
            {
                return "does not receive shadows; the display's bodies do";
            }

            Material[] slots = renderer.sharedMaterials;
            if (slots.Length != mesh.subMeshCount)
            {
                return slots.Length + " materials for " + mesh.subMeshCount + " submeshes";
            }

            var sourceIndices = new int[slots.Length];
            for (int s = 0; s < slots.Length; s++)
            {
                if (slots[s] == null)
                {
                    return "no material for submesh " + s;
                }

                if (!materials.TryGetValue(slots[s], out KeyValuePair<int, string> known))
                {
                    int index = SourceIndexOf(slots[s], bound, out string whyNot);
                    materials[slots[s]] = known = new KeyValuePair<int, string>(index, whyNot);
                }

                if (known.Value != null)
                {
                    return "submesh " + s + ": " + known.Value;
                }

                sourceIndices[s] = known.Key;
            }

            // Where it stands and its scale apart: the display places bodies rigidly, so the scale goes into the vertices.
            Transform t = renderer.transform;
            Vector3 scale = t.lossyScale;
            if (!(scale.x > 0f) || !(scale.y > 0f) || !(scale.z > 0f))
            {
                return "mirrored or flat: scale " + scale.ToString("R");
            }

            Matrix4x4 rigid = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            Matrix4x4 rebuilt = rigid * Matrix4x4.Scale(scale), full = t.localToWorldMatrix;
            for (int i = 0; i < 16; i++)
            {
                if (Mathf.Abs(rebuilt[i] - full[i]) > 1e-4f * (1f + Mathf.Abs(full[i])))
                {
                    return "placed with a shear (a turned parent scaled unevenly): not a rotation and a scale";
                }
            }

            if ((scale - Vector3.one).sqrMagnitude <= 1e-10f)
            {
                scale = Vector3.one;
            }

            if (!bank.TryGet(mesh, out SceneDrawMeshBank.Entry geometry))
            {
                if (!bank.TryAdd(mesh, out string unreadable) || !bank.TryGet(mesh, out geometry))
                {
                    return (unreadable ?? "its geometry could not be kept") + "; and it is not in the geometry made beforehand";
                }
            }

            if (geometry.submeshIndexCount.Length != mesh.subMeshCount)
            {
                return "the geometry made beforehand has " + geometry.submeshIndexCount.Length + " submeshes, the mesh " + mesh.subMeshCount;
            }

            target = new SceneDrawTarget
            {
                owner = owner, path = PathOf(t), renderer = renderer, mesh = mesh, geometry = geometry,
                sourceIndices = sourceIndices, placement = rigid, scale = scale,
            };
            return null;
        }

        /// <summary>
        /// The display's source index whose material means what <paramref name="surface"/> means, or -1 with why not.
        /// The rule: the renderer's material is the VP mesh surface -- the shading the display's bodies are lit with
        /// (DESIGN 5.3) -- and a material the world draws with has the same base map, the same base colour and the
        /// same palette switch. Nothing is read off a name.
        /// </summary>
        public static int SourceIndexOf(Material surface, IReadOnlyList<CutWorldRoot.MaterialBinding> bound, out string whyNot)
        {
            whyNot = null;
            if (surface == null || surface.shader == null || surface.shader.name != MeshSurfaceShader)
            {
                whyNot = "the material '" + (surface != null ? surface.name : "(none)") + "' is drawn by '" + (surface != null && surface.shader != null ? surface.shader.name : "(none)")
                    + "', not by '" + MeshSurfaceShader + "' (the shading the display's bodies have)";
                return -1;
            }

            if (bound != null)
            {
                for (int i = 0; i < bound.Count; i++)
                {
                    Material display = bound[i].material;
                    if (display != null && SameTexture(surface, display) && SameColour(surface, display) && SameFloat(surface, display, "_VpUsePaletteAtlas"))
                    {
                        return bound[i].sourceIndex;
                    }
                }
            }

            whyNot = "no material of the world's display has the base map, base colour and palette switch of '" + surface.name + "'";
            return -1;
        }

        public const string MeshSurfaceShader = "Zantetsu/VP Mesh Surface";

        private static bool SameTexture(Material a, Material b)
        {
            Texture ta = a.HasProperty("_BaseMap") ? a.GetTexture("_BaseMap") : null;
            Texture tb = b.HasProperty("_BaseMap") ? b.GetTexture("_BaseMap") : null;
            return ta == tb;
        }

        private static bool SameColour(Material a, Material b)
        {
            if (!a.HasProperty("_BaseColor") || !b.HasProperty("_BaseColor")) return a.HasProperty("_BaseColor") == b.HasProperty("_BaseColor");
            Color ca = a.GetColor("_BaseColor"), cb = b.GetColor("_BaseColor");
            const float e = 0.5f / 255f;
            return Mathf.Abs(ca.r - cb.r) <= e && Mathf.Abs(ca.g - cb.g) <= e && Mathf.Abs(ca.b - cb.b) <= e && Mathf.Abs(ca.a - cb.a) <= e;
        }

        private static bool SameFloat(Material a, Material b, string name)
        {
            float fa = a.HasProperty(name) ? a.GetFloat(name) : 0f, fb = b.HasProperty(name) ? b.GetFloat(name) : 0f;
            return Mathf.Approximately(fa, fb);
        }

        internal static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        public string Describe()
        {
            return "instances " + Instances + "; renderers taken " + Targets.Count + " (draw commands, one a submesh: " + Commands + "; vertices " + Vertices + ", indices " + Indices
                + ", triangles " + Triangles + "; distinct meshes " + DistinctMeshes + ", distinct materials " + DistinctMaterials + "; scaled " + Scaled
                + "); refused " + Refusals.Count + "; not drawn by Unity either " + NotDrawn + "; listed by more than one instance " + ListedAgain + "; missing " + Missing;
        }
    }

    /// <summary>
    /// The scene draw comparison's other way of drawing (TL, 2026-10-07): the inventory's targets shown by a cut
    /// world's own display -- its storage, its culling, its body and shadow draws (VP3C by default) -- in place of
    /// their MeshRenderers. **Only the display is used.** Nothing is given to the physics: no owner, no actor, no
    /// collider, no cut input; the instance's colliders and bodies are not touched, and its renderer is switched off
    /// only once the display has taken its geometry, so nothing is drawn twice and nothing is left undrawn.
    /// <para>
    /// **One body a renderer, one command a submesh.** Each target is appended as a geometry of its own -- its mesh's
    /// vertices, its submeshes in order, each with the source index of its material -- and shown as a live fragment
    /// of its own with one display instance. Nothing is merged across targets of one mesh: no instancing.
    /// </para>
    /// <para>
    /// **Where each stands.** The display asks the world's placement lookup where a fragment stands, and a fragment
    /// with no physics owner has no answer there. For the fragments made here the lookup is given a second voice
    /// (<see cref="Placement"/>) that answers "where it was registered" -- they follow nothing -- and passes every
    /// other fragment on unchanged. It is put back when this is disposed.
    /// </para>
    /// Not a display hand-over: a target shown here is not a cut target, and cutting it is not provided for.
    /// </summary>
    public sealed class SceneDrawVpDisplay : IDisposable
    {
        private readonly CutWorldRoot _world;
        private readonly HashSet<LogicalFragmentId> _fragments = new HashSet<LogicalFragmentId>();
        private readonly List<SceneDrawTarget> _shown = new List<SceneDrawTarget>();
        private readonly Dictionary<KeyValuePair<SceneDrawMeshBank.Entry, Vector3>, VpRenderVertex[]> _scaled = new Dictionary<KeyValuePair<SceneDrawMeshBank.Entry, Vector3>, VpRenderVertex[]>();
        private readonly IVpFragmentPlacement _before;
        private readonly Placement _placement;
        private bool _disposed;

        public int ShownCount => _shown.Count;
        public int Commands { get; private set; }
        public long Vertices { get; private set; }
        public long Indices { get; private set; }
        public double AppendSeconds { get; private set; }
        public double ShowSeconds { get; private set; }

        /// <summary>How many times the display asked where one of these fragments stands, since it was made.</summary>
        public long PlacementAnswers => _placement.Answers;

        public SceneDrawVpDisplay(CutWorldRoot world)
        {
            _world = world != null && world.IsReady && world.Display != null ? world : throw new ArgumentException("a ready cut world", nameof(world));
            _before = world.Display.Placement;
            _placement = new Placement(_before, _fragments);
            world.Display.Placement = _placement;
        }

        /// <summary>
        /// Whether the world has the room for the inventory's targets, in words: what is needed against what the
        /// world's profile made, and what would have to grow. False when a need is past a limit that cannot grow.
        /// </summary>
        public static bool CheckRoom(CutWorldRoot world, SceneDrawInventory inventory, out string description)
        {
            VpCpuGeometryStorage storage = world.Storage;
            VpLogicalCutDisplay display = world.Display;
            VpLogicalCutDisplayLimits limits = world.Profile.DisplayLimits;
            long vertexEnd = storage.VertexCount + inventory.Vertices;
            long submeshEnd = storage.SubmeshCount + inventory.Commands;
            long commandEnd = display.DrawCommandEnd + inventory.Commands;
            long instanceEnd = display.DrawInstanceEnd + 2L * inventory.Commands;
            long geometries = world.References.LiveGeometryCount + inventory.Targets.Count;
            long displayInstances = world.References.LiveDisplayInstanceCount + inventory.Targets.Count;
            var text = new StringBuilder();
            bool ok = true;
            void Line(string what, long need, long room, long limit, bool grows)
            {
                bool fits = need <= room, within = need <= limit;
                ok &= within;
                text.Append(what).Append(' ').Append(need).Append(" of ").Append(room).Append(grows ? " (limit " + limit + ")" : "")
                    .Append(within ? (fits ? "" : " -- GROWS while preparing") : " -- PAST ITS LIMIT").Append("; ");
            }

            Line("CPU vertices", vertexEnd, storage.CommittedVertexCapacity, storage.VertexCapacity, true);
            Line("CPU indices", inventory.Indices, storage.CommittedIndexCapacity, storage.IndexCapacity, true);
            Line("submeshes", submeshEnd, storage.SubmeshCapacity, storage.SubmeshCapacity, false);
            Line("GPU vertices", vertexEnd, display.GpuVertexCapacity, storage.VertexCapacity, true);
            Line("GPU indices", inventory.Indices, display.GpuIndexCapacity, storage.IndexCapacity, true);
            Line("draw commands", commandEnd, display.CommandCapacity, limits.commands, true);
            Line("draw instances", instanceEnd, display.InstanceCapacity, limits.instances, true);
            Line("geometry references", geometries, world.References.GeometryCapacity, world.References.GeometryLimit, true);
            Line("display instances", displayInstances, world.References.DisplayInstanceCapacity, world.References.DisplayInstanceLimit, true);
            description = text.ToString();
            return ok;
        }

        /// <summary>
        /// Appends the target's geometry, shows it as a fragment of its own where the renderer stands, and switches
        /// the renderer off. False with the reason, the scene and the world as they were, when the storage or the
        /// display refuses.
        /// </summary>
        public bool TryShow(SceneDrawTarget target, out string refusal)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SceneDrawVpDisplay));
            refusal = null;
            if (target == null || target.shown || target.renderer == null)
            {
                refusal = "not a target to show";
                return false;
            }

            SceneDrawMeshBank.Entry entry = target.geometry;
            var submeshes = new VpGeometrySubmesh[target.sourceIndices.Length];
            for (int s = 0; s < submeshes.Length; s++)
            {
                submeshes[s] = new VpGeometrySubmesh(entry.submeshIndexStart[s], entry.submeshIndexCount[s], target.sourceIndices[s]);
            }

            VpCpuGeometryStorage storage = _world.Storage;
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            bool appended = storage.TryAppendPrepared(VerticesOf(entry, target.scale), entry.indices, entry.Identity, entry.vertices.Length, submeshes, out VpStoredGeometry geometry);
            long afterAppend = System.Diagnostics.Stopwatch.GetTimestamp();
            AppendSeconds += (afterAppend - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (!appended)
            {
                refusal = "the storage refused its geometry (" + entry.vertices.Length + " vertices, " + entry.indices.Length + " indices, " + submeshes.Length + " submeshes)";
                return false;
            }

            LogicalFragmentId fragment = _world.Ledger.AddFragment();
            bool shown = false;
            try
            {
                shown = _world.Display.TryShow(fragment, geometry, target.placement);
            }
            finally
            {
                ShowSeconds += (System.Diagnostics.Stopwatch.GetTimestamp() - afterAppend) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (!shown)
                {
                    // Never shown: the fragment and the geometry are still ours to give back.
                    _world.Ledger.Retire(fragment);
                    bool grouped = storage.TryGetVertexGroup(geometry, out int group);
                    if (storage.TryRetireIndices(geometry.indexRange) && grouped) storage.TryReleaseVertexGroup(group);
                }
            }

            if (!shown)
            {
                refusal = "the display refused to show it";
                return false;
            }

            _fragments.Add(fragment);
            target.fragment = fragment;
            target.stored = geometry;
            target.shown = true;
            target.renderer.enabled = false;   // only now: the display has it
            _shown.Add(target);
            Commands += submeshes.Length;
            Vertices += entry.vertices.Length;
            Indices += entry.indices.Length;
            return true;
        }

        // The geometry's vertices as the target stands: its own for an unscaled one; else a copy with the scale in the
        // positions (and, for an uneven scale, in the normals), made once a geometry and scale.
        private VpRenderVertex[] VerticesOf(SceneDrawMeshBank.Entry entry, Vector3 scale)
        {
            if (scale == Vector3.one)
            {
                return entry.vertices;
            }

            var key = new KeyValuePair<SceneDrawMeshBank.Entry, Vector3>(entry, scale);
            if (_scaled.TryGetValue(key, out VpRenderVertex[] made))
            {
                return made;
            }

            bool even = Mathf.Approximately(scale.x, scale.y) && Mathf.Approximately(scale.x, scale.z);
            made = new VpRenderVertex[entry.vertices.Length];
            for (int i = 0; i < made.Length; i++)
            {
                VpRenderVertex v = entry.vertices[i];
                Vector3 normal = v.normal;
                v.position = Vector3.Scale(v.position, scale);
                if (!even)
                {
                    v.normal = new Vector3(normal.x / scale.x, normal.y / scale.y, normal.z / scale.z).normalized;
                }

                made[i] = v;
            }

            _scaled[key] = made;
            return made;
        }

        /// <summary>The scaled copies are for showing only: once everything is shown they are let go.</summary>
        public void ReleaseScaledCopies() => _scaled.Clear();

        /// <summary>
        /// Puts the world's placement lookup back and switches the shown targets' renderers on again. The fragments
        /// stay the world's until it ends: with their lookup gone the display draws them no more.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_world != null && _world.Display != null && !_world.Display.IsDisposed && ReferenceEquals(_world.Display.Placement, _placement))
            {
                _world.Display.Placement = _before;
            }

            foreach (SceneDrawTarget target in _shown)
            {
                if (target.renderer != null) target.renderer.enabled = true;
            }

            _scaled.Clear();
        }

        /// <summary>The world's lookup with a second voice: a fragment shown here stands where it was registered.</summary>
        private sealed class Placement : IVpFragmentPlacement
        {
            private readonly IVpFragmentPlacement _inner;
            private readonly HashSet<LogicalFragmentId> _own;

            internal long Answers;

            internal Placement(IVpFragmentPlacement inner, HashSet<LogicalFragmentId> own)
            {
                _inner = inner;
                _own = own;
            }

            public VpFragmentPlacementKind TryGetGeometryLocalToWorld(
                LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 geometryLocalToWorld)
            {
                if (_own.Contains(fragment))
                {
                    Answers++;
                    geometryLocalToWorld = default;
                    return VpFragmentPlacementKind.Static;
                }

                if (_inner == null)
                {
                    geometryLocalToWorld = default;
                    return VpFragmentPlacementKind.Static;
                }

                return _inner.TryGetGeometryLocalToWorld(fragment, operation, side, out geometryLocalToWorld);
            }
        }
    }
}
