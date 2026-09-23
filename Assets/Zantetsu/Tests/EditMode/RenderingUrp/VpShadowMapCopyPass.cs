using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Zantetsu.Rendering.Urp.Tests
{
    /// <summary>
    /// Probe diagnostics: after the main light shadows and the cascade shadow pass, copies URP's main light shadow map
    /// and the cascade pass's diagnostic target into two RFloat textures that can be read back and compared texel by
    /// texel. Enqueue it after the cascade pass; a null cascade pass copies URP's map to both targets for baseline
    /// snapshots. <see cref="Compare"/> reads both copies back; the far value is the one the clear leaves (the majority
    /// value of the URP copy).
    /// </summary>
    public sealed class VpShadowMapCopyPass : ScriptableRenderPass
    {
        private readonly VpCascadeShadowPass _cascadePass;

        private class CopyData
        {
            internal TextureHandle source;
        }

        public VpShadowMapCopyPass(VpCascadeShadowPass cascadePass, int width, int height)
        {
            _cascadePass = cascadePass;
            renderPassEvent = RenderPassEvent.AfterRenderingShadows + 1;
            var descriptor = new RenderTextureDescriptor(width, height, GraphicsFormat.R32_SFloat, GraphicsFormat.None);
            UrpCopy = RTHandles.Alloc(descriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "VP Probe URP Shadow Copy");
            PassCopy = RTHandles.Alloc(descriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "VP Probe Pass Shadow Copy");
            Width = width;
            Height = height;
        }

        public int Width { get; }

        public int Height { get; }

        public RTHandle UrpCopy { get; }

        public RTHandle PassCopy { get; }

        /// <summary>Whether the last recorded frame copied both maps.</summary>
        public bool LastCopied { get; private set; }

        public void Release()
        {
            UrpCopy.Release();
            PassCopy.Release();
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            LastCopied = false;
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            TextureHandle urpShadows = resourceData.mainShadowsTexture;
            TextureHandle passShadows = _cascadePass != null ? _cascadePass.LastTargetHandle : urpShadows;
            if (!urpShadows.IsValid() || !passShadows.IsValid())
            {
                return;
            }

            Copy(renderGraph, urpShadows, renderGraph.ImportTexture(UrpCopy), "VP Probe Copy URP Shadows");
            Copy(renderGraph, passShadows, renderGraph.ImportTexture(PassCopy), "VP Probe Copy Pass Shadows");
            LastCopied = true;
        }

        private static void Copy(RenderGraph renderGraph, TextureHandle source, TextureHandle destination, string name)
        {
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(name, out CopyData data))
            {
                data.source = source;
                builder.UseTexture(source);
                builder.SetRenderAttachment(destination, 0);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (CopyData copyData, RasterGraphContext context) =>
                    Blitter.BlitTexture(context.cmd, copyData.source, new Vector4(1f, 1f, 0f, 0f), 0f, false));
            }
        }

        /// <summary>Snapshots URP's copied shadow map for comparison across frames; null means readback failed.</summary>
        public float[] ReadUrpCopy()
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(UrpCopy.rt, 0, TextureFormat.RFloat);
            request.WaitForCompletion();
            return request.hasError ? null : request.GetData<float>().ToArray();
        }

        /// <summary>Reads both copies back and compares them.</summary>
        public Comparison Compare()
        {
            var comparison = new Comparison();
            AsyncGPUReadbackRequest urpRequest = AsyncGPUReadback.Request(UrpCopy.rt, 0, TextureFormat.RFloat);
            AsyncGPUReadbackRequest passRequest = AsyncGPUReadback.Request(PassCopy.rt, 0, TextureFormat.RFloat);
            urpRequest.WaitForCompletion();
            passRequest.WaitForCompletion();
            if (urpRequest.hasError || passRequest.hasError)
            {
                comparison.readbackError = true;
                return comparison;
            }

            Unity.Collections.NativeArray<float> urp = urpRequest.GetData<float>();
            Unity.Collections.NativeArray<float> pass = passRequest.GetData<float>();
            int zeros = 0;
            for (int i = 0; i < urp.Length; i++)
            {
                zeros += urp[i] == 0f ? 1 : 0;
            }

            float far = zeros * 2 > urp.Length ? 0f : 1f;
            comparison.far = far;
            comparison.texels = urp.Length;
            for (int i = 0; i < urp.Length; i++)
            {
                comparison.urpWritten += urp[i] != far ? 1 : 0;
                comparison.passWritten += pass[i] != far ? 1 : 0;
                float difference = Mathf.Abs(urp[i] - pass[i]);
                if (difference > 0f)
                {
                    comparison.differing++;
                    comparison.maxDifference = Mathf.Max(comparison.maxDifference, difference);
                }
            }

            return comparison;
        }

        public struct Comparison
        {
            public bool readbackError;
            public int texels;
            public float far;
            public int urpWritten;
            public int passWritten;
            public int differing;
            public float maxDifference;

            public override string ToString()
            {
                return "texels=" + texels + " far=" + far + " urp_written=" + urpWritten + " pass_written=" + passWritten + " differing=" + differing
                    + " max_diff=" + maxDifference.ToString("G4", System.Globalization.CultureInfo.InvariantCulture) + " readback_error=" + readbackError;
            }
        }
    }
}
