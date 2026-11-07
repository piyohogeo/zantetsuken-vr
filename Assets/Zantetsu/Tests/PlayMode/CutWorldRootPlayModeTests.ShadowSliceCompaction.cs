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
        [UnityTest, Order(18)]
        public IEnumerator GpuCull_18_ComputeShadowSlices_WithWaveCompaction_DrawTheSameScenesAsFixedCommands()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12) Assert.Ignore("cascade command compaction requires Direct3D12");

            bool selectionBefore = VpGpuCullSetup.ShadowSliceSelection;
            VpGpuCullSetup.ShadowSliceSelection = true;
            bool computeBefore = VpGpuCullSetup.ComputeShadowSlices;
            VpGpuCullSetup.ShadowSliceSelection = selectionBefore;
            bool castersFixedBefore = VpCommandCompaction.CastersFixed;
            List<Color32[]> nativeBefore = s_gpuCullNativeImages;
            List<Color32[]> compactedBefore = s_gpuCullCompactedImages;
            int widthBefore = s_gpuCullImageWidth;
            int heightBefore = s_gpuCullImageHeight;
            try
            {
                VpGpuCullSetup.ShadowSliceSelection = true;
                VpGpuCullSetup.ComputeShadowSlices = true;
                VpCommandCompaction.CastersFixed = false;
                yield return GpuCullScenes(true, true, false);
                List<Color32[]> fixedImages = s_gpuCullNativeImages;
                Assert.That(fixedImages, Is.Not.Null);
                Assert.That(fixedImages.Count, Is.EqualTo(35));
                int width = s_gpuCullImageWidth;
                int height = s_gpuCullImageHeight;
                yield return Cleanup();
                Fresh();

                long before = VpCommandCompaction.Compactions;
                yield return GpuCullScenes(true, true, true);
                Assert.That(VpCommandCompaction.Compactions - before, Is.GreaterThan(10), "the wave compaction was exercised");
                List<Color32[]> compactedImages = s_gpuCullCompactedImages;
                Assert.That(compactedImages, Is.Not.Null);
                Assert.That(compactedImages.Count, Is.EqualTo(fixedImages.Count));
                Assert.That(s_gpuCullImageWidth, Is.EqualTo(width));
                Assert.That(s_gpuCullImageHeight, Is.EqualTo(height));

                var lines = new List<string>();
                int worst = 0;
                for (int image = 0; image < fixedImages.Count; image++)
                {
                    int differing = CountDifferingPixels(fixedImages[image], compactedImages[image], 2, out int largest, out int lit);
                    lines.Add(s_gpuCullStages[image / GpuCullViews] + ", " + s_gpuCullViewNames[image % GpuCullViews]
                        + ", pixels differing by more than 2 " + differing + ", largest difference " + largest + ", drawn pixels " + lit);
                    worst = Math.Max(worst, differing);
                }

                TestContext.Out.WriteLine(string.Join("\n", lines));
                Assert.That(worst, Is.Zero, "compute-shadow pictures differ between fixed and wave-compacted commands\n" + string.Join("\n", lines));
            }
            finally
            {
                VpGpuCullSetup.ComputeShadowSlices = computeBefore;
                VpGpuCullSetup.ShadowSliceSelection = selectionBefore;
                VpCommandCompaction.CastersFixed = castersFixedBefore;
                s_gpuCullNativeImages = nativeBefore;
                s_gpuCullCompactedImages = compactedBefore;
                s_gpuCullImageWidth = widthBefore;
                s_gpuCullImageHeight = heightBefore;
            }
        }
    }
}
