using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Zantetsu.Quest
{
    /// <summary>Reads actual camera and AppSW target descriptors, without adding GPU work.</summary>
    public sealed class QuestXrTargetInfoFeature : ScriptableRendererFeature
    {
        private TargetInfoPass _pass;
        public static string Latest { get; private set; }

        public override void Create() => _pass = new TargetInfoPass { renderPassEvent = RenderPassEvent.AfterRenderingOpaques };
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (renderingData.cameraData.cameraType == CameraType.Game) renderer.EnqueuePass(_pass);
        }

        private sealed class TargetInfoPass : ScriptableRenderPass
        {
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var camera = frameData.Get<UniversalCameraData>();
                var xr = camera.xr;
                if (xr == null || !xr.enabled) return;
                var colour = camera.cameraTargetDescriptor;
                var motion = xr.motionVectorRenderTargetDesc;
                string info = "camera=" + colour.width + "x" + colour.height + "x" + colour.volumeDepth
                    + " cameraMsaa=" + colour.msaaSamples + " scale=" + camera.renderScale
                    + " hasMotionVectorPass=" + xr.hasMotionVectorPass
                    + " motion=" + motion.width + "x" + motion.height + "x" + motion.volumeDepth
                    + " motionMsaa=" + motion.msaaSamples + " motionFormat=" + motion.graphicsFormat
                    + "/" + motion.depthStencilFormat + " views=" + xr.viewCount;
                if (info == Latest) return;
                Latest = info;
                Debug.Log("QUEST XR TARGET: " + info);
            }
        }
    }
}
