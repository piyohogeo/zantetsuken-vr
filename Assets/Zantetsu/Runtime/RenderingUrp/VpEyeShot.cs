using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Zantetsu.Rendering.Urp
{
    /// <summary>
    /// A picture of each eye as the frame leaves URP: a pass recorded after everything else of a camera copies every
    /// slice of the camera's final colour target (two under Single Pass Instanced, one otherwise) into a texture of
    /// its own and reads it back asynchronously; each slice becomes a PNG. For comparing what two routes draw into
    /// the eye textures themselves -- not the mirror window, which shows one eye. The copy is a blit (it resolves a
    /// multisampled target); the readback is asynchronous and never waits on the GPU. Rows come as the GPU holds
    /// them (bottom row first on Direct3D), the same for every picture taken this way.
    /// </summary>
    public static class VpEyeShot
    {
        private static readonly List<Pass> s_passes = new List<Pass>();
        private static int s_pending;

        /// <summary>How many pictures are requested or read back but not yet written.</summary>
        public static int Pending => s_pending;

        /// <summary>The last failure of a request or a readback, or null.</summary>
        public static string LastFailure { get; private set; }

        /// <summary>How many pictures were written.</summary>
        public static int Written { get; private set; }

        /// <summary>
        /// Asks for the eye pictures of <paramref name="camera"/>'s next rendering, written as
        /// <paramref name="pathPrefix"/>-eye0.png, -eye1.png. To be called before the camera renders (from
        /// RenderPipelineManager.beginCameraRendering, or a frame earlier). False with the reason when the pipeline
        /// cannot take the pass.
        /// </summary>
        public static bool TryRequest(Camera camera, string pathPrefix, out string failure)
        {
            failure = VpGpuCullCameraRoute.CheckEnvironment();
            if (failure != null)
            {
                return false;
            }

            if (camera == null || string.IsNullOrEmpty(pathPrefix))
            {
                failure = "no camera or no path";
                return false;
            }

            camera.TryGetComponent(out UniversalAdditionalCameraData cameraData);
            ScriptableRenderer renderer = cameraData != null ? cameraData.scriptableRenderer : null;
            if (renderer == null)
            {
                renderer = (GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset)?.scriptableRenderer;
            }

            if (renderer == null)
            {
                failure = "the camera has no URP renderer to enqueue a pass on";
                return false;
            }

            Pass pass = null;
            foreach (Pass candidate in s_passes)
            {
                if (ReferenceEquals(candidate.camera, camera) && candidate.pathPrefix == null)
                {
                    pass = candidate;
                    break;
                }
            }

            if (pass == null)
            {
                pass = new Pass(camera);
                s_passes.Add(pass);
            }

            pass.pathPrefix = pathPrefix;
            renderer.EnqueuePass(pass);
            return true;
        }

        /// <summary>The slice shader's Resources name.</summary>
        public const string SliceShaderResource = "VpEyeShotSlice";

        private static readonly int SourceId = Shader.PropertyToID("_VpEyeSource");
        private static readonly int SliceId = Shader.PropertyToID("_VpEyeSlice");
        private static Material s_slicer;

        private static Material SliceMaterial()
        {
            if (s_slicer == null)
            {
                Shader shader = Resources.Load<Shader>(SliceShaderResource);
                if (shader == null)
                {
                    return null;
                }

                s_slicer = new Material(shader) { name = "VP Eye Shot Slice", hideFlags = HideFlags.HideAndDontSave };
            }

            return s_slicer;
        }

        /// <summary>
        /// Records the drawing of <paramref name="slice"/> of the texture array <paramref name="source"/> over the
        /// whole of <paramref name="destination"/>: one point sample a pixel at level 0, no copy of the resource. The
        /// pass's way of taking an eye's picture; public for a test that checks the slice shader on a small array.
        /// False when the slice shader is not in the build.
        /// </summary>
        public static bool TryDrawSlice(CommandBuffer commands, RenderTargetIdentifier source, int slice, RenderTexture destination)
        {
            Material slicer = SliceMaterial();
            if (slicer == null)
            {
                return false;
            }

            DrawSlice(commands, source, slice, destination, slicer);
            return true;
        }

        private static void DrawSlice(CommandBuffer commands, RenderTargetIdentifier source, int slice, RenderTexture destination, Material slicer)
        {
            commands.SetGlobalTexture(SourceId, source);
            commands.SetGlobalInt(SliceId, slice);
            commands.SetRenderTarget(destination);
            commands.SetViewport(new Rect(0f, 0f, destination.width, destination.height));
            commands.DrawProcedural(Matrix4x4.identity, slicer, 0, MeshTopology.Triangles, 3, 1);
        }

        // ---- the main light's shadow map, as a picture -----------------------------------------------------------

        private static readonly List<ShadowMapPass> s_shadowPasses = new List<ShadowMapPass>();

        /// <summary>
        /// Asks for a picture of the main light's shadow map of <paramref name="camera"/>'s next rendering, taken right
        /// after the shadows are rendered (every caster's, by any route) and written as <paramref name="path"/> (a
        /// PNG; the depth as grey, every cascade's slice in its place in the atlas). Diagnosis: whether a route's
        /// casters are in each slice. False with the reason when the pipeline cannot take the pass.
        /// </summary>
        public static bool TryRequestShadowMap(Camera camera, string path, out string failure)
        {
            failure = VpGpuCullCameraRoute.CheckEnvironment();
            if (failure != null)
            {
                return false;
            }

            if (camera == null || string.IsNullOrEmpty(path))
            {
                failure = "no camera or no path";
                return false;
            }

            camera.TryGetComponent(out UniversalAdditionalCameraData cameraData);
            ScriptableRenderer renderer = cameraData != null ? cameraData.scriptableRenderer : null;
            if (renderer == null)
            {
                renderer = (GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset)?.scriptableRenderer;
            }

            if (renderer == null)
            {
                failure = "the camera has no URP renderer to enqueue a pass on";
                return false;
            }

            ShadowMapPass pass = null;
            foreach (ShadowMapPass candidate in s_shadowPasses)
            {
                if (ReferenceEquals(candidate.camera, camera) && candidate.path == null)
                {
                    pass = candidate;
                    break;
                }
            }

            if (pass == null)
            {
                pass = new ShadowMapPass(camera);
                s_shadowPasses.Add(pass);
            }

            pass.path = path;
            renderer.EnqueuePass(pass);
            return true;
        }

        private sealed class ShadowMapPassData
        {
            public ShadowMapPass pass;
            public TextureHandle shadowMap;
            public int width;
            public int height;
            public string path;
        }

        private sealed class ShadowMapPass : ScriptableRenderPass
        {
            public readonly Camera camera;
            public string path;

            public ShadowMapPass(Camera camera)
            {
                this.camera = camera;
                // After every caster pass of the shadows event (URP's and the plugin route's, which sit at +0).
                renderPassEvent = RenderPassEvent.AfterRenderingShadows + 20;
                profilingSampler = new ProfilingSampler("VP Shadow Map Shot");
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (!ReferenceEquals(cameraData.camera, camera) || path == null)
                {
                    return;
                }

                string file = path;
                path = null;
                try
                {
                    UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                    TextureHandle shadowMap = resourceData.mainShadowsTexture;
                    if (!shadowMap.IsValid())
                    {
                        LastFailure = "no main light shadow map this frame";
                        return;
                    }

                    TextureDesc description = renderGraph.GetTextureDesc(shadowMap);
                    using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(passName, out ShadowMapPassData data, profilingSampler))
                    {
                        data.pass = this;
                        data.shadowMap = shadowMap;
                        data.width = description.width;
                        data.height = description.height;
                        data.path = file;
                        builder.UseTexture(shadowMap, AccessFlags.Read);
                        builder.AllowGlobalStateModification(true);
                        builder.AllowPassCulling(false);
                        builder.SetRenderFunc(static (ShadowMapPassData passData, UnsafeGraphContext context) => passData.pass.Execute(passData, context));
                    }
                }
                catch (Exception e)
                {
                    LastFailure = "the shadow map shot pass could not be recorded: " + e.Message;
                }
            }

            private void Execute(ShadowMapPassData data, UnsafeGraphContext context)
            {
                CommandBuffer commands = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                RTHandle source = data.shadowMap;
                if (data.width <= 0 || data.height <= 0)
                {
                    LastFailure = "the shadow map has no size";
                    return;
                }

                // The depth sampled as a colour (its value in every channel) by the ordinary blit: the map is a 2D
                // atlas, not an array.
                var copy = new RenderTexture(data.width, data.height, 0, GraphicsFormat.R8G8B8A8_UNorm) { name = "VP Shadow Map Shot" };
                copy.Create();
                commands.Blit((RenderTargetIdentifier)source, copy);
                string file = data.path;
                int width = data.width, height = data.height;
                s_pending++;
                commands.RequestAsyncReadback(copy, 0, TextureFormat.RGBA32, request =>
                {
                    try
                    {
                        if (request.hasError)
                        {
                            LastFailure = "the readback of " + file + " failed";
                            return;
                        }

                        byte[] bytes = ImageConversion.EncodeArrayToPNG(request.GetData<byte>().ToArray(), GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height);
                        File.WriteAllBytes(file, bytes);
                        Written++;
                    }
                    catch (Exception e)
                    {
                        LastFailure = file + ": " + e.Message;
                    }
                    finally
                    {
                        s_pending--;
                        copy.Release();
                        UnityEngine.Object.Destroy(copy);
                    }
                });
            }
        }

        private sealed class PassData
        {
            public Pass pass;
            public TextureHandle colour;
            public bool array;
            public int slices;
            public int width;
            public int height;
            public GraphicsFormat format;
            public string pathPrefix;
        }

        private sealed class Pass : ScriptableRenderPass
        {
            public readonly Camera camera;
            public string pathPrefix;

            public Pass(Camera camera)
            {
                this.camera = camera;

                // After everything: the final target (the XR eye texture or the camera's target), single-sampled, so
                // that a copy of a slice is possible (CopyTexture cannot resolve a multisampled source).
                renderPassEvent = RenderPassEvent.AfterRendering + 10;
                profilingSampler = new ProfilingSampler("VP Eye Shot");
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (!ReferenceEquals(cameraData.camera, camera) || pathPrefix == null)
                {
                    return;
                }

                string prefix = pathPrefix;
                pathPrefix = null;
                try
                {
                    UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                    // URP's own colour texture when there is one (the intermediate of a camera that needs it: MSAA,
                    // post-processing, XR), else the active target. An imported target (the XR swap chain image, the
                    // back buffer) has no render graph descriptor to ask -- asking threw and reset the frame on
                    // 2026-10-07 (natS1) -- so the size and slices then come from the camera's own target descriptor.
                    TextureHandle colour = resourceData.activeColorTexture;
                    if (!colour.IsValid())
                    {
                        LastFailure = "no colour target";
                        return;
                    }

                    RenderTextureDescriptor target = cameraData.cameraTargetDescriptor;
                    int width = target.width, height = target.height;
                    bool array = target.dimension == TextureDimension.Tex2DArray;
                    int slices = array ? Mathf.Max(1, target.volumeDepth) : 1;
                    GraphicsFormat format = target.graphicsFormat;
                    try
                    {
                        TextureDesc description = renderGraph.GetTextureDesc(colour);
                        if (description.width > 0 && description.height > 0)
                        {
                            width = description.width;
                            height = description.height;
                            array = description.dimension == TextureDimension.Tex2DArray;
                            slices = array ? Mathf.Max(1, description.slices) : 1;
                            format = description.format;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // An imported texture: the camera's descriptor stands.
                    }

                    using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(passName, out PassData data, profilingSampler))
                    {
                        data.pass = this;
                        data.colour = colour;
                        data.array = array;
                        data.slices = slices;
                        data.width = width;
                        data.height = height;
                        data.format = format;
                        data.pathPrefix = prefix;
                        builder.UseTexture(colour, AccessFlags.Read);
                        builder.AllowGlobalStateModification(true);
                        builder.AllowPassCulling(false);
                        builder.SetRenderFunc(static (PassData passData, UnsafeGraphContext context) => passData.pass.Execute(passData, context));
                    }
                }
                catch (Exception e)
                {
                    // A picture must never cost the frame it is taken in.
                    LastFailure = "the eye shot pass could not be recorded: " + e.Message;
                }
            }

            private void Execute(PassData data, UnsafeGraphContext context)
            {
                CommandBuffer commands = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                RTHandle source = data.colour;
                if (data.width <= 0 || data.height <= 0)
                {
                    LastFailure = "the camera target has no size";
                    return;
                }

                // A blit samples the source as a Texture2D, which an XR eye texture (a Texture2DArray) is not: every
                // picture of natS2/vpS2/natS3/vpS3 came out all zero. A copy of a slice (CopyTexture) ended the Player
                // one frame after the first request (natS4, 2026-10-08, exit 0xC0000005). An array's slice is now
                // drawn through the slice shader (one sample a pixel, level 0); a plain 2D target keeps the blit.
                Material slicer = data.array ? SliceMaterial() : null;
                if (data.array && slicer == null)
                {
                    LastFailure = "the slice shader is not in the build (Resources/" + SliceShaderResource + ")";
                    return;
                }

                for (int slice = 0; slice < data.slices; slice++)
                {
                    var copy = new RenderTexture(data.width, data.height, 0, GraphicsFormat.R8G8B8A8_UNorm) { name = "VP Eye Shot " + slice };
                    copy.Create();
                    if (data.array)
                    {
                        DrawSlice(commands, source, slice, copy, slicer);
                    }
                    else
                    {
                        // By identifier: the Texture conversion of an imported target (the camera's own) asserts (play-31).
                        commands.Blit((RenderTargetIdentifier)source, copy);
                    }

                    string path = data.pathPrefix + "-eye" + slice + ".png";
                    int width = data.width, height = data.height;
                    s_pending++;
                    commands.RequestAsyncReadback(copy, 0, TextureFormat.RGBA32, request =>
                    {
                        try
                        {
                            if (request.hasError)
                            {
                                LastFailure = "the readback of " + path + " failed";
                                return;
                            }

                            byte[] bytes = ImageConversion.EncodeArrayToPNG(request.GetData<byte>().ToArray(), GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height);
                            File.WriteAllBytes(path, bytes);
                            Written++;
                        }
                        catch (Exception e)
                        {
                            LastFailure = path + ": " + e.Message;
                        }
                        finally
                        {
                            s_pending--;
                            copy.Release();
                            UnityEngine.Object.Destroy(copy);
                        }
                    });
                }
            }
        }
    }
}
