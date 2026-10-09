using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering.Urp
{
    /// <summary>
    /// Explicit on-device diagnostic for the native direct Clip path. Two orange camera-facing quads use the same
    /// small geometry: the left quad keeps its whole surface with one valid plane, and the right keeps only its
    /// right half. Colour, main-light shadow and camera-only AppSW re-rasterization all receive both records.
    /// Disabled by default; normal scenes allocate and submit nothing for this probe.
    /// </summary>
    public sealed class VpNativeDirectClipProbe : IDisposable
    {
        public static bool Enabled { get => VpNativeDrawPlugin.VulkanDirectClipProbe; set => VpNativeDrawPlugin.VulkanDirectClipProbe = value; }

        private static readonly VpGeometryRange Range = new VpGeometryRange(0, 4, 0, 6);
        private VpGpuIndexedGeometryBuffers _buffers;
        private Material _body;
        private Material _caster;
        private VpNativeDrawRoute _route;
        private bool _disposed;

        public void Append(VpNativeDrawRoute route, Camera camera, Material baseMaterial)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VpNativeDirectClipProbe));
            if (!Enabled || route == null || !route.SeparateClipDraws || camera == null || baseMaterial == null) return;
            if (_route != null && !ReferenceEquals(_route, route))
                throw new InvalidOperationException("a direct Clip probe belongs to one native route");
            EnsureResources(route, baseMaterial);

            Transform view = camera.transform;
            Vector3 forward = view.forward, right = view.right, up = view.up;
            Vector3 middle = view.position + 3f * forward;
            Vector3 left = middle - .85f * right, clipped = middle + .85f * right;
            Matrix4x4 leftToWorld = Matrix4x4.TRS(left, view.rotation, Vector3.one);
            Matrix4x4 clippedToWorld = Matrix4x4.TRS(clipped, view.rotation, Vector3.one);
            // All vertices lie one metre on the kept side of this plane. The record still has PlaneCount=1.
            Vector3 fullPlanePoint = left - forward;
            var fullPlane = new Vector4(forward.x, forward.y, forward.z, -Vector3.Dot(forward, fullPlanePoint));
            var halfPlane = new Vector4(right.x, right.y, right.z, -Vector3.Dot(right, clipped));
            VpInstanceClip full = VpInstanceClip.Keep(fullPlane, 1f);
            VpInstanceClip half = VpInstanceClip.Keep(halfPlane, 1f);
            Vector3 size = 1.4f * Abs(right) + 1.4f * Abs(up) + .01f * Abs(forward);
            var leftBounds = new Bounds(left, size);
            var clippedBounds = new Bounds(clipped, size);
            int layer = camera.gameObject.layer;
            route.AddClipBody(camera, _buffers, _body, Range, leftToWorld, full, leftBounds, layer);
            route.AddClipBody(camera, _buffers, _body, Range, clippedToWorld, half, clippedBounds, layer);
            route.AddClipCasters(camera, _buffers, _caster, Range, leftToWorld, full, leftBounds, layer);
            route.AddClipCasters(camera, _buffers, _caster, Range, clippedToWorld, half, clippedBounds, layer);
        }

        private void EnsureResources(VpNativeDrawRoute route, Material baseMaterial)
        {
            if (_buffers != null) return;
            Shader casterShader = Shader.Find("Zantetsu/VP Indexed Indirect Shadow Caster");
            if (casterShader == null) throw new InvalidOperationException("the direct Clip probe shadow shader is missing");
            var buffers = new VpGpuIndexedGeometryBuffers(4, 6);
            Material body = null, caster = null;
            try
            {
                // Clockwise from the camera, outward normal -Z. The packed vertex setters also support Legacy32.
                buffers.VertexBuffer.SetData(new[]
                {
                    new VpRenderVertex { position = new Vector3(-.7f, -.7f, 0f), normal = Vector3.back, uv0 = new Vector2(0f, 0f) },
                    new VpRenderVertex { position = new Vector3(.7f, -.7f, 0f), normal = Vector3.back, uv0 = new Vector2(1f, 0f) },
                    new VpRenderVertex { position = new Vector3(.7f, .7f, 0f), normal = Vector3.back, uv0 = new Vector2(1f, 1f) },
                    new VpRenderVertex { position = new Vector3(-.7f, .7f, 0f), normal = Vector3.back, uv0 = new Vector2(0f, 1f) },
                });
                buffers.IndexBuffer.SetData(new uint[] { 0, 2, 1, 0, 3, 2 });
                body = new Material(baseMaterial) { name = "VP Direct Clip Probe Orange" };
                body.SetFloat("_VpUsePaletteAtlas", 0f);
                body.SetColor("_BaseColor", new Color(1f, .25f, .03f, 1f));
                body.SetTexture("_BaseMap", Texture2D.whiteTexture);
                SetDirectKeywords(body);
                caster = new Material(casterShader) { name = "VP Direct Clip Probe Two-sided Caster" };
                caster.SetFloat("_Cull", (float)CullMode.Off);
                SetDirectKeywords(caster);
                _route = route;
                _buffers = buffers;
                _body = body;
                _caster = caster;
            }
            catch
            {
                buffers.Dispose();
                DestroyMaterial(body);
                DestroyMaterial(caster);
                throw;
            }
        }

        private static void SetDirectKeywords(Material material)
        {
            material.DisableKeyword("VP_NO_CLIP");
            material.DisableKeyword("VP_GPU_CULLED");
            material.DisableKeyword("VP_NATIVE_MULTIDRAW");
            material.EnableKeyword("VP_NATIVE_DIRECT");
        }

        private static Vector3 Abs(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_buffers != null)
            {
                // Buffers stay alive until every issued callback that named them has run or was discarded.
                _buffers.Dispose(_route.Retire);
                _buffers = null;
            }
            if (_body != null || _caster != null)
            {
                // Native passes have copied the material values into their issue blocks and cached the pipeline.
                VpNativeDrawRelease.Defer("VP direct Clip probe materials", _route?.IssuedEventsWait(),
                    new IDisposable[] { new MaterialRelease(_body, _caster) });
                _body = _caster = null;
            }
            _route = null;
        }

        private sealed class MaterialRelease : IDisposable
        {
            private Material _body, _caster;
            public MaterialRelease(Material body, Material caster) { _body = body; _caster = caster; }
            public void Dispose()
            {
                DestroyMaterial(_body);
                DestroyMaterial(_caster);
                _body = _caster = null;
            }
        }

        private static void DestroyMaterial(Material material)
        {
            if (material == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(material);
            else UnityEngine.Object.DestroyImmediate(material);
        }
    }
}
