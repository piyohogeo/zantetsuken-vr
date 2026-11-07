using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest, Order(17)]
        public IEnumerator GpuCull_17_ComputeShadowSlices_DrawTheSameScenesAsTheVertexSliceJudgement()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the shadow slice compute experiment draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType);
            }

            bool selectionBefore = VpGpuCullSetup.ShadowSliceSelection;
            VpGpuCullSetup.ShadowSliceSelection = true;
            bool computeBefore = VpGpuCullSetup.ComputeShadowSlices;
            VpGpuCullSetup.ShadowSliceSelection = selectionBefore;
            List<Color32[]> imagesBefore = s_gpuCullNativeImages;
            int widthBefore = s_gpuCullImageWidth;
            int heightBefore = s_gpuCullImageHeight;
            try
            {
                VpGpuCullSetup.ShadowSliceSelection = true;
                VpGpuCullSetup.ComputeShadowSlices = false;
                yield return GpuCullScenes(true, true);
                List<Color32[]> vertexSelected = s_gpuCullNativeImages;
                Assert.That(vertexSelected, Is.Not.Null, "the existing vertex judgement took its scenes in this test");
                Assert.That(vertexSelected.Count, Is.EqualTo(35));
                int width = s_gpuCullImageWidth;
                int height = s_gpuCullImageHeight;

                // The helper ends its world. Release the fixture's scene objects before taking the same stages again.
                yield return Cleanup();
                Fresh();

                VpGpuCullSetup.ComputeShadowSlices = true;
                yield return GpuCullScenes(true, true);
                List<Color32[]> computeSelected = s_gpuCullNativeImages;
                Assert.That(computeSelected, Is.Not.Null, "the compute judgement took its scenes in this test");
                Assert.That(computeSelected.Count, Is.EqualTo(vertexSelected.Count));
                Assert.That(s_gpuCullImageWidth, Is.EqualTo(width));
                Assert.That(s_gpuCullImageHeight, Is.EqualTo(height));

                var lines = new List<string>();
                int worst = 0;
                for (int image = 0; image < vertexSelected.Count; image++)
                {
                    int differing = CountDifferingPixels(vertexSelected[image], computeSelected[image], 2, out int largest, out int lit);
                    lines.Add(s_gpuCullStages[image / GpuCullViews] + ", " + s_gpuCullViewNames[image % GpuCullViews]
                        + ": pixels differing by more than 2 " + differing + ", largest difference " + largest + ", drawn pixels " + lit);
                    worst = Math.Max(worst, differing);
                }

                TestContext.Out.WriteLine(string.Join("\n", lines));
                Assert.That(worst, Is.Zero, "pixels differing between vertex and compute shadow slice judgement:\n" + string.Join("\n", lines));
            }
            finally
            {
                VpGpuCullSetup.ComputeShadowSlices = computeBefore;
                VpGpuCullSetup.ShadowSliceSelection = selectionBefore;
                s_gpuCullNativeImages = imagesBefore;
                s_gpuCullImageWidth = widthBefore;
                s_gpuCullImageHeight = heightBefore;
            }
        }
    }
}
