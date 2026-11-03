using System.Collections;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The GPU bake probe's kernel over the known rig, in the Editor's graphics device (DESIGN 4.5.14): the mesh's own
    /// buffers read through Raw, one palette, the 16-byte output into a poisoned buffer between guards, read back and
    /// compared with the CPU reference -- under the bind pose, the strong bend, with the bones moved after the palette
    /// was taken, and with the renderer disabled; the indices copied with a base vertex.
    /// </summary>
    public sealed class SkinBakeProbePlayModeTests
    {
        private SkinBakeProbeRig _rig;
        private SkinBakeProbeMeshInput _input;
        private SkinBakeProbeGpuBake _gpu;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _rig = new SkinBakeProbeRig("probe test rig", null);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _gpu?.Dispose(); _gpu = null;
            _input?.Dispose(); _input = null;
            _rig?.Dispose(); _rig = null;
            yield return null;
        }

        private IEnumerator Verified(string name, SkinBakeProbeMeshInput input, float4x4[] palette, VpRenderVertex[] reference, int start, System.Action<SkinBakeProbeCompare.Result> check)
        {
            _gpu.Bake(input, palette, _gpu.Output, start);
            SkinBakeProbeCompare.Result result = null;
            _gpu.Verify(name, _gpu.Output, start, input.VertexCount, 2, 2, reference, r => result = r);
            for (int f = 0; f < 600 && result == null; f++) { _gpu.Update(); yield return null; }
            Assert.That(result, Is.Not.Null, name + ": the readback did not complete within 600 frames");
            Debug.Log(result.Describe());
            check(result);
        }

        [UnityTest]
        public IEnumerator TheGpuBake_MatchesTheCpuReference_UnderEachPose_AndFollowsTheTakenPaletteOnly()
        {
            Assume.That(SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback, "compute and async readback are needed");
            Assert.That(SkinBakeProbeMeshInput.TryPrepare(_rig.Mesh, out _input, out string refusal), Is.True, refusal);
            _gpu = new SkinBakeProbeGpuBake();
            _gpu.EnsureOwnOutput(1024, 2048);
            SkinBakeProbeCpuBake.Inputs inputs = SkinBakeProbeCpuBake.Read(_input);
            var reference = new VpRenderVertex[_input.VertexCount];
            int start = 3;
            for (int pose = 0; pose < 3; pose++)
            {
                _rig.SetPose(pose);
                yield return null;
                float4x4[] palette = SkinBakeProbeCpuBake.Palette(_rig.Renderer, out int missing);
                Assert.That(missing, Is.Zero);
                int invalid = SkinBakeProbeCpuBake.Bake(inputs, palette, reference, out _, out _);
                Assert.That(invalid, Is.Zero, "the rig's reference has no invalid vertex");
                yield return Verified("pose " + pose, _input, palette, (VpRenderVertex[])reference.Clone(), start, r =>
                {
                    Assert.That(r.readbackError, Is.False);
                    Assert.That(r.processed, Is.EqualTo(r.count), "every vertex processed once");
                    Assert.That(r.guardBeforeIntact && r.guardAfterIntact, Is.True, "nothing written outside the region");
                    Assert.That(r.positionOverTolerance, Is.Zero, "positions within " + SkinBakeProbeCompare.PositionTolerance + " m (max " + r.positionMaxError + ")");
                    Assert.That(r.normalOther, Is.Zero, "no normal apart from quantisation boundaries");
                    Assert.That(r.uvOther, Is.Zero);
                    Assert.That(r.invalidGpu, Is.Zero); Assert.That(r.invalidDisagree, Is.Zero); Assert.That(r.bonesOutsidePalette, Is.Zero);
                    Assert.That(r.Pass, Is.True, r.Describe());
                });
                start += _input.VertexCount + 5;
            }

            // The palette taken under the bend, the bones and the root moved away: the bake follows the palette.
            _rig.SetPose(1);
            yield return null;
            float4x4[] taken = SkinBakeProbeCpuBake.Palette(_rig.Renderer, out _);
            SkinBakeProbeCpuBake.Bake(inputs, taken, reference, out _, out _);
            _rig.Disturb();
            yield return null;
            yield return Verified("taken palette, bones moved", _input, taken, (VpRenderVertex[])reference.Clone(), start, r => Assert.That(r.Pass, Is.True, r.Describe()));
            start += _input.VertexCount + 5;

            // The renderer disabled after the preparation: the mesh's buffers still serve.
            _rig.SetPose(2);
            yield return null;
            float4x4[] strong = SkinBakeProbeCpuBake.Palette(_rig.Renderer, out _);
            SkinBakeProbeCpuBake.Bake(inputs, strong, reference, out _, out _);
            _rig.Renderer.enabled = false;
            yield return null;
            yield return Verified("renderer disabled", _input, strong, (VpRenderVertex[])reference.Clone(), start, r => Assert.That(r.Pass, Is.True, r.Describe()));

            // The indices with a base vertex.
            _gpu.CopyIndices(_input, _gpu.OutputIndices, 7, 4096u);
            SkinBakeProbeCompare.IndexResult indices = null;
            _gpu.VerifyIndices("indices", _gpu.OutputIndices, 7, _input.IndexCount, inputs.indices, 4096u, r => indices = r);
            for (int f = 0; f < 600 && indices == null; f++) { _gpu.Update(); yield return null; }
            Assert.That(indices, Is.Not.Null);
            Assert.That(indices.Pass, Is.True, indices.Describe());
            Assert.That(_gpu.PendingCount, Is.Zero);
        }

        /// <summary>SKIN-BAKE-PROBE-2: with the verification counts off (the measurement's setting) the bake writes the same vertices; the result says the counts are not known.</summary>
        [UnityTest]
        public IEnumerator TheGpuBake_WithTheCountsOff_WritesTheSameVertices()
        {
            Assume.That(SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback, "compute and async readback are needed");
            Assert.That(SkinBakeProbeMeshInput.TryPrepare(_rig.Mesh, out _input, out string refusal), Is.True, refusal);
            _gpu = new SkinBakeProbeGpuBake { CountsEnabled = false };
            _gpu.EnsureOwnOutput(1024, 2048);
            SkinBakeProbeCpuBake.Inputs inputs = SkinBakeProbeCpuBake.Read(_input);
            var reference = new VpRenderVertex[_input.VertexCount];
            _rig.SetPose(2);
            yield return null;
            float4x4[] palette = SkinBakeProbeCpuBake.Palette(_rig.Renderer, out _);
            SkinBakeProbeCpuBake.Bake(inputs, palette, reference, out _, out _);
            yield return Verified("counts off", _input, palette, reference, 3, r =>
            {
                Assert.That(r.countsKnown, Is.False, "the counts were not taken");
                Assert.That(r.readbackError, Is.False);
                Assert.That(r.guardBeforeIntact && r.guardAfterIntact, Is.True);
                Assert.That(r.positionOverTolerance, Is.Zero); Assert.That(r.normalOther, Is.Zero); Assert.That(r.uvOther, Is.Zero); Assert.That(r.invalidDisagree, Is.Zero);
                Assert.That(r.Pass, Is.True, r.Describe());
                Assert.That(r.Describe(), Does.Contain("counts off"));
            });
            Assert.That(_gpu.PendingCount, Is.Zero);
        }
    }
}
