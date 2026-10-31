using System.Collections.Generic;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The world's skin draw (DESIGN 4.5.12): the uncut skinned individuals (the crowd's characters) drawn through the
    /// indirect VP draw with 24-byte vertices skinned in the vertex stage, beside the display of the cut geometry. Made
    /// with the world where the launch and the body route allow it (<see cref="VpSkinDrawSetup"/>); the characters
    /// register their models and individuals, and leave it when their cut's display begins.
    /// </summary>
    public sealed partial class CutWorldRoot
    {
        /// <summary>The skin draw, or null when the skins are drawn by their renderers (<see cref="SkinRouteReason"/> says why).</summary>
        public VpSkinDisplay SkinDisplay { get; private set; }

        /// <summary>The material the skin draw draws the individuals with: the one of source index 0, which the characters' cut pieces are drawn with.</summary>
        public Material SkinDrawMaterial { get; private set; }

        /// <summary>"SKIN24-NATIVE", "SKIN24-UNITY" or "RENDERERS".</summary>
        public string SkinRoute { get; private set; }

        public string SkinRouteReason { get; private set; }

        /// <summary>Whether this world draws the uncut skinned individuals itself.</summary>
        public bool DrawsSkins => SkinDisplay != null && !SkinDisplay.IsDisposed;

        /// <summary>The material the casters are drawn with (the one-sided one), for whoever draws the skin draw by Unity's calls.</summary>
        public Material ShadowMaterial => shadowMaterial;

        /// <summary>The late-update scheduler of the skin draw's worker gather (SKIN-BENCH-3), or null (the main thread gathers, or no skin draw).</summary>
        public VpSkinGatherLateUpdate SkinGatherScheduler { get; private set; }

        private void MakeSkinDisplay(Dictionary<int, Material> materialsBySourceIndex)
        {
            SkinDisplay = null;
            SkinDrawMaterial = null;
            VpSkinDrawRequest request = VpSkinDrawSetup.Request;
            string asked = request == VpSkinDrawRequest.Renderers ? "asked for the renderers (" + VpSkinDrawSetup.RenderersArgument + ")"
                : request == VpSkinDrawRequest.SkinDraw ? "asked for the skin draw (" + VpSkinDrawSetup.SkinDrawArgument + ")"
                : "the default (the skin draw is taken where " + VpSkinDrawSetup.SkinDrawArgument + " asks for it)";
            if (!VpSkinDrawSetup.Wanted(request, BodyRoute) || Display == null || !Display.CullsOnGpu)
            {
                SkinRoute = "RENDERERS";
                SkinRouteReason = request == VpSkinDrawRequest.SkinDraw
                    ? "the bodies are drawn by " + BodyRoute + ", without the GPU selection (" + asked + ")"
                    : asked;
                UnityEngine.Debug.Log("VP SKIN ROUTE: RENDERERS -- the skinned individuals are drawn by their SkinnedMeshRenderers: " + SkinRouteReason, this);
                return;
            }

            Material material = materialsBySourceIndex.TryGetValue(0, out Material bound) ? bound : null;
            string failure = material == null ? "no material is bound for source index 0" : null;
            VpSkinDisplay display = null;
            // The room for the models and their bone boxes: the launch's where it asks (SKIN-BENCH-3, TL review 3: a crowd of
            // 49 models under a profile of 8 had 41 of them drawn by their renderers), else the profile's.
            int modelCapacity = VpSkinDrawSetup.ModelCapacity > 0 ? VpSkinDrawSetup.ModelCapacity : profile.SkinModelCapacity;
            int boneBoxCapacity = VpSkinDrawSetup.BoneBoxCapacity > 0 ? VpSkinDrawSetup.BoneBoxCapacity : profile.SkinBoneBoxCapacity;
            if (failure == null && !VpSkinDisplay.TryCreate(
                    profile.StencilSettings.cameraCapacity, Display.NativeArguments, shadowMaterial, profile.SkinInstanceCapacity, modelCapacity,
                    profile.SkinPaletteCapacity, boneBoxCapacity, profile.SkinGpuVertexInitialCapacity, profile.SkinGpuIndexInitialCapacity,
                    profile.SkinGpuVertexCapacityLimit, profile.SkinGpuIndexCapacityLimit, out display, out failure))
            {
                display = null;
            }

            if (display == null)
            {
                SkinRoute = "RENDERERS";
                SkinRouteReason = "the skin draw could not be made (" + asked + "): " + failure;
                if (request == VpSkinDrawRequest.SkinDraw)
                {
                    UnityEngine.Debug.LogError("VP SKIN ROUTE: SKIN DRAW REQUESTED (" + VpSkinDrawSetup.SkinDrawArgument + ") BUT NOT AVAILABLE: " + failure
                        + "; the skinned individuals are drawn by their SkinnedMeshRenderers", this);
                }
                else
                {
                    UnityEngine.Debug.LogWarning("VP SKIN ROUTE: RENDERERS -- " + SkinRouteReason, this);
                }

                return;
            }

            SkinDisplay = display;
            SkinDrawMaterial = material;
            SkinRoute = Display.NativeArguments ? "SKIN24-NATIVE" : "SKIN24-UNITY";
            SkinRouteReason = asked;
            // The worker gather's schedule at the end of the late update (SKIN-BENCH-3), wherever the worker gather is in
            // use -- the state, not the display string, which carries the threshold too ("worker-from-n"; TL review 2, P2).
            if (display.WorkerActive)
            {
                SkinGatherScheduler = gameObject.AddComponent<VpSkinGatherLateUpdate>();
                SkinGatherScheduler.Display = display;
            }

            UnityEngine.Debug.Log("VP SKIN ROUTE: " + SkinRoute + " -- the uncut skinned individuals through the indirect draw (24-byte vertices skinned in the vertex stage, "
                + "selection by instance bounds), drawn by " + (Display.NativeArguments ? "the plugin's route" : "Unity's calls") + "; material " + material.name
                + "; room: " + profile.SkinInstanceCapacity + " individuals, " + modelCapacity + " models"
                + (VpSkinDrawSetup.ModelCapacity > 0 ? " (profile " + profile.SkinModelCapacity + ", " + VpSkinDrawSetup.ModelCapacityArgument + " " + VpSkinDrawSetup.ModelCapacity + ")" : " (profile)")
                + ", " + profile.SkinPaletteCapacity + " palette matrices, " + boneBoxCapacity + " bone boxes"
                + (VpSkinDrawSetup.BoneBoxCapacity > 0 ? " (profile " + profile.SkinBoneBoxCapacity + ", " + VpSkinDrawSetup.BoneBoxCapacityArgument + " " + VpSkinDrawSetup.BoneBoxCapacity + ")" : " (profile)") + "; "
                + "palette gather " + display.GatherModeActual + " (" + VpSkinGatherSetup.Argument + " " + VpSkinGatherSetup.Name(display.GatherModeAsked) + "); " + asked, this);
        }

        private void ReleaseSkinDisplay()
        {
            if (SkinDisplay == null) return;
            UnityEngine.Debug.Log("VP SKIN ROUTE (end): " + name + " " + SkinDisplay.Describe()
                + (SkinGatherScheduler != null ? "; scheduled by the late update " + SkinGatherScheduler.Scheduled + " frames" : ""), this);
            if (SkinGatherScheduler != null) { SkinGatherScheduler.Display = null; Destroy(SkinGatherScheduler); SkinGatherScheduler = null; }
            SkinDisplay.Dispose();
            SkinDisplay = null;
        }
    }
}
