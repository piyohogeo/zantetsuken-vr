using System;
using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Main-thread, load-scope cache for immutable source meshes and fixed positive uniform scale.
    /// Own this at the asset/scene scope, dispose all inputs before disposing the cache.
    /// Rig transforms and clips are never rewritten. Not a general affine-scale or blendshape path.
    /// </summary>
    public sealed class VpFixedScaleSkinCache : IDisposable
    {
        readonly Dictionary<(Mesh, float), Mesh> meshes = new Dictionary<(Mesh, float), Mesh>();
        readonly HashSet<SkinnedMeshRenderer> sources = new HashSet<SkinnedMeshRenderer>();
        bool disposed;
        public int CachedMeshCount => meshes.Count;
        public int ActiveInputCount => sources.Count;
        public int PreparedMeshBuildCount { get; private set; }

        public VpFixedScaleSkinInput Prepare(SkinnedMeshRenderer source, Transform unitParent)
        {
            if (disposed) throw new ObjectDisposedException(nameof(VpFixedScaleSkinCache));
            float scale = VpFixedScaleSkinInput.Validate(source,unitParent);
            if (sources.Contains(source)) throw new InvalidOperationException("Source renderer is already prepared by this cache.");
            var key = (source.sharedMesh,scale);
            if (!meshes.TryGetValue(key,out Mesh mesh))
            {
                // Unit scale needs no extra mesh allocation. The asset remains externally owned.
                mesh = source.sharedMesh;
                if (scale != 1f)
                {
                    mesh = UnityEngine.Object.Instantiate(source.sharedMesh);
                    try
                    {
                        mesh.name = source.sharedMesh.name+"-FixedScalePrepared";
                        var positions = mesh.vertices;
                        for (int i=0;i<positions.Length;i++) positions[i] *= scale;
                        mesh.vertices = positions;
                        var bindposes = mesh.bindposes;
                        var inverseScale = Matrix4x4.Scale(Vector3.one/scale);
                        for (int i=0;i<bindposes.Length;i++) bindposes[i] *= inverseScale;
                        mesh.bindposes = bindposes;
                        mesh.bounds = new Bounds(source.sharedMesh.bounds.center*scale,source.sharedMesh.bounds.size*scale);
                        PreparedMeshBuildCount++;
                    }
                    catch { DestroyOwned(mesh); throw; }
                }
                meshes.Add(key,mesh);
            }
            var input = new VpFixedScaleSkinInput(this,source,unitParent,mesh,scale);
            sources.Add(source);
            return input;
        }

        internal void Release(SkinnedMeshRenderer source) => sources.Remove(source);

        public void Dispose()
        {
            if (disposed) return;
            if (sources.Count != 0) throw new InvalidOperationException("Dispose prepared inputs before their mesh cache.");
            foreach (var entry in meshes) if (entry.Key.Item2 != 1f) DestroyOwned(entry.Value);
            meshes.Clear(); disposed=true;
        }

        internal static void DestroyOwned(UnityEngine.Object item)
        {
            if (item == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(item);
            else UnityEngine.Object.DestroyImmediate(item);
        }
    }

    /// <summary>
    /// One rig instance's unit-scale renderer and synchronous current-pose input.
    /// Dispose before unloading its rig/cache. External code must not mutate the shared prepared mesh.
    /// Only the common parent may move/rotate; its scale stays one. Bone animation is unrestricted by this class.
    /// Renderer/material/enabled animation bindings are not redirected to the replacement renderer.
    /// </summary>
    public sealed class VpFixedScaleSkinInput : IDisposable
    {
        VpFixedScaleSkinCache cache;
        readonly SkinnedMeshRenderer source;
        readonly bool sourceEnabled;
        public SkinnedMeshRenderer Renderer { get; private set; }
        public float FixedScale { get; }

        internal VpFixedScaleSkinInput(VpFixedScaleSkinCache cache, SkinnedMeshRenderer source, Transform parent, Mesh mesh, float scale)
        {
            this.cache=cache; this.source=source; sourceEnabled=source.enabled; FixedScale=scale;
            var go = new GameObject(source.name+"-UnitScaleRenderer");
            try
            {
                go.layer=source.gameObject.layer;
                go.transform.SetParent(parent,false);
                go.transform.SetPositionAndRotation(source.transform.position,source.transform.rotation);
                var r = go.AddComponent<SkinnedMeshRenderer>();
                r.enabled=false;
                r.sharedMesh=mesh; r.bones=source.bones; r.rootBone=source.rootBone;
                r.sharedMaterials=source.sharedMaterials; r.quality=source.quality;
                r.updateWhenOffscreen=source.updateWhenOffscreen;
                r.localBounds=new Bounds(source.localBounds.center*scale,source.localBounds.size*scale);
                r.shadowCastingMode=source.shadowCastingMode; r.receiveShadows=source.receiveShadows;
                r.lightProbeUsage=source.lightProbeUsage; r.reflectionProbeUsage=source.reflectionProbeUsage;
                r.probeAnchor=source.probeAnchor; r.renderingLayerMask=source.renderingLayerMask;
                r.sortingLayerID=source.sortingLayerID; r.sortingOrder=source.sortingOrder;
                r.motionVectorGenerationMode=source.motionVectorGenerationMode;
                r.skinnedMotionVectors=source.skinnedMotionVectors; r.forceRenderingOff=source.forceRenderingOff;
                var properties=new MaterialPropertyBlock();
                source.GetPropertyBlock(properties); r.SetPropertyBlock(properties);
                for (int i=0;i<source.sharedMaterials.Length;i++)
                {
                    properties.Clear(); source.GetPropertyBlock(properties,i); r.SetPropertyBlock(properties,i);
                }
                Renderer=r;
                source.enabled=false; r.enabled=sourceEnabled;
            }
            catch { source.enabled=sourceEnabled; VpFixedScaleSkinCache.DestroyOwned(go); throw; }
        }

        // No mesh-sized work here: the prepared vertex/bindpose basis is shared and reused.
        public void BakeCurrentPose(Mesh destination)
        {
            if (cache == null || Renderer == null) throw new ObjectDisposedException(nameof(VpFixedScaleSkinInput));
            if (destination == null || destination == Renderer.sharedMesh || destination == source.sharedMesh)
                throw new ArgumentException("Bake into a separate caller-owned mesh.",nameof(destination));
            RequireRigid(Renderer.transform);
            Renderer.BakeMesh(destination,true);
        }

        // The same once-only basis conversion applies to authoring Renderer-bind physics points.
        public Vector3 PrepareBindPoint(Vector3 point) => point*FixedScale;

        public void Dispose()
        {
            if (cache == null) return;
            if (Renderer != null)
            {
                Renderer.enabled=false; // Stop drawing immediately, even with deferred Player destruction.
                VpFixedScaleSkinCache.DestroyOwned(Renderer.gameObject); Renderer=null;
            }
            if (source != null) source.enabled=sourceEnabled;
            cache.Release(source); cache=null;
        }

        internal static float Validate(SkinnedMeshRenderer source, Transform parent)
        {
            if (source == null || parent == null || source.sharedMesh == null)
                throw new ArgumentException("A source mesh/renderer and unit-scale parent are required.");
            if (!source.gameObject.activeInHierarchy || !parent.gameObject.activeInHierarchy)
                throw new ArgumentException("Prepare active instances; renderer enabled state may be false.");
            if (!source.sharedMesh.isReadable || source.sharedMesh.blendShapeCount != 0)
                throw new ArgumentException("Readable non-blendshape mesh required.");
            RequireRigid(parent);
            Vector3 s=source.transform.lossyScale;
            float scale=s.x;
            if (!(scale>0) || !float.IsFinite(1f/scale) || Mathf.Abs(s.y-scale)>scale*1e-5f || Mathf.Abs(s.z-scale)>scale*1e-5f)
                throw new ArgumentException("Only fixed positive uniform scale is supported.");
            RequireMatrix(source.transform,scale);
            return scale;
        }

        static void RequireRigid(Transform transform) => RequireMatrix(transform,1f);
        static void RequireMatrix(Transform transform,float scale)
        {
            Matrix4x4 expected=Matrix4x4.TRS(transform.position,transform.rotation,Vector3.one*scale);
            Matrix4x4 actual=transform.localToWorldMatrix;
            for (int i=0;i<16;i++)
                if (!float.IsFinite(actual[i]) || Mathf.Abs(expected[i]-actual[i])>1e-5f*Mathf.Max(1,scale))
                    throw new ArgumentException("Frame has unsupported scale, reflection or shear.");
        }
    }
}
