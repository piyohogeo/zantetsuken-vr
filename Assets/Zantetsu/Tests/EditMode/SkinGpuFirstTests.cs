using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.Tests.EditMode
{
    /// <summary>
    /// The GPU-first skin bake's pieces apart from the cut (DESIGN 4.5.15): the launch's choice, the shared input's
    /// acceptance and refusals, the float kernel against the probe's reference, the conservative box, the storage's
    /// deferred geometry and the dispatcher's purpose.
    /// </summary>
    public class SkinGpuFirstTests
    {
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            VpSkinBakeSetup.Mode = VpSkinBakeSetup.Default;   // back to the DEFAULT (cpu-float since 2026-11-03), not to cpu
            foreach (IDisposable d in _disposables) d.Dispose();
            _disposables.Clear();
            foreach (UnityEngine.Object o in _objects) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _objects.Clear();
        }

        private T Track<T>(T o) where T : UnityEngine.Object { _objects.Add(o); return o; }
        private T Keep<T>(T d) where T : IDisposable { _disposables.Add(d); return d; }

        // A small skinned mesh: four vertices, two bones, every vertex weighted to both (FourBones layout as Unity stores it).
        private Mesh SmallRig(out Matrix4x4[] bindposes)
        {
            bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(new Vector3(0, -1, 0)) };
            var mesh = Track(new Mesh { name = "gpu-first small rig" });
            mesh.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) };
            mesh.normals = new[] { Vector3.up, Vector3.right, Vector3.forward, new Vector3(0.6f, 0.8f, 0).normalized };
            mesh.uv = new[] { new Vector2(0.1f, 0.2f), new Vector2(0.9f, 0.4f), new Vector2(0.5f, 0.5f), new Vector2(0.25f, 0.75f) };
            mesh.triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };
            mesh.bindposes = bindposes;
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 0.75f, boneIndex1 = 1, weight1 = 0.25f },
                new BoneWeight { boneIndex0 = 1, weight0 = 0.6f, boneIndex1 = 0, weight1 = 0.4f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 1, weight0 = 0.5f, boneIndex1 = 0, weight1 = 0.5f },
            };
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            return mesh;
        }

        /// <summary>
        /// CPU-SKIN-FLOAT-DEFAULT-1 (2026-11-03): no argument is cpu-float, the DEFAULT; an unrecognised value keeps that default;
        /// <c>-zantetsuSkinBake cpu</c> names the 24-byte bake explicitly (the way back), and says so (Explicit).
        /// </summary>
        [Test]
        public void TheLaunchChoice_IsCpuFloatByDefault_AndTheLastRecognisedValueWins()
        {
            Assert.That(VpSkinBakeSetup.Default, Is.EqualTo(VpSkinBakeMode.CpuFloat), "the default since 2026-11-03");
            VpSkinBakeSetup.ReadArguments(new[] { "player.exe" });
            Assert.That(VpSkinBakeSetup.Mode, Is.EqualTo(VpSkinBakeMode.CpuFloat), "no argument: the default, cpu-float");
            Assert.That(VpSkinBakeSetup.Explicit, Is.False);
            Assert.That(VpSkinBakeSetup.Unrecognised, Is.Null);
            VpSkinBakeSetup.ReadArguments(new[] { "x", VpSkinBakeSetup.Argument, "cpu" });
            Assert.That(VpSkinBakeSetup.Mode, Is.EqualTo(VpSkinBakeMode.Cpu), "explicit cpu: the 24-byte bake, the way back");
            Assert.That(VpSkinBakeSetup.Explicit, Is.True);
            VpSkinBakeSetup.ReadArguments(new[] { "x", VpSkinBakeSetup.Argument, "cpu-float" });
            Assert.That(VpSkinBakeSetup.Mode, Is.EqualTo(VpSkinBakeMode.CpuFloat));
            Assert.That(VpSkinBakeSetup.Explicit, Is.True);
            VpSkinBakeSetup.ReadArguments(new[] { "x", VpSkinBakeSetup.Argument, "gpu" });
            Assert.That(VpSkinBakeSetup.Mode, Is.EqualTo(VpSkinBakeMode.Gpu));
            VpSkinBakeSetup.ReadArguments(new[] { VpSkinBakeSetup.Argument, "GPU", VpSkinBakeSetup.Argument, "float" });
            Assert.That(VpSkinBakeSetup.Mode, Is.EqualTo(VpSkinBakeMode.Float));
            VpSkinBakeSetup.ReadArguments(new[] { VpSkinBakeSetup.Argument, "fast" });
            Assert.That(VpSkinBakeSetup.Mode, Is.EqualTo(VpSkinBakeMode.CpuFloat), "an unrecognised value keeps the default (cpu-float)");
            Assert.That(VpSkinBakeSetup.Explicit, Is.False);
            Assert.That(VpSkinBakeSetup.Unrecognised, Is.EqualTo("fast"));
            VpSkinBakeSetup.ReadArguments(new[] { VpSkinBakeSetup.Argument });
            Assert.That(VpSkinBakeSetup.Unrecognised, Is.EqualTo("(no value)"));
            Assert.That(VpSkinBakeSetup.Name(VpSkinBakeMode.Gpu), Is.EqualTo("gpu"));
        }

        [Test]
        public void TheHitPreparationPurpose_GoesToTheGeometryPool_AndTheCountCoversIt()
        {
            Assert.That(WorkPurposes.IsDefined(WorkPurpose.HitPreparation), Is.True);
            Assert.That(WorkPurposes.DestinationOf(WorkPurpose.HitPreparation), Is.EqualTo(WorkDestination.GeometryPool));
            Assert.That(WorkPurposes.IsUrgent(WorkPurpose.HitPreparation), Is.False);
            Assert.That(WorkPurposes.Count, Is.EqualTo((int)WorkPurpose.HitPreparation + 1));
            Assert.That(Enum.GetValues(typeof(WorkPurpose)).Length, Is.EqualTo(WorkPurposes.Count), "every purpose is counted");
        }

        [Test]
        public void TheSharedInput_IsMadeFromTheSmallRig_AndRefusesWhatWasNotVerified()
        {
            Mesh mesh = SmallRig(out _);
            Assert.That(VpSharedSkinInput.TryPrepare(mesh, out VpSharedSkinInput input, out string refusal), Is.True, refusal);
            Keep(input);
            Assert.That(input.VertexCount, Is.EqualTo(4));
            Assert.That(input.IndexCount, Is.EqualTo(12));
            Assert.That(input.HasUv, Is.True);
            Assert.That(input.Position.format, Is.EqualTo(VertexAttributeFormat.Float32));
            Assert.That(input.MaxInfluences, Is.EqualTo(2));
            Assert.That((mesh.vertexBufferTarget & GraphicsBuffer.Target.Raw) != 0, Is.True, "Raw added to the vertex buffer target");
            Assert.That(input.Weights, Is.Not.Null);
            Assert.That(input.WeightReading == VpSharedSkinInput.WeightReadingKind.Raw || input.WeightReading == VpSharedSkinInput.WeightReadingKind.Structured32, Is.True, input.Describe());
            // The CPU side reads the effective weights in buffer order.
            Assert.That(input.weightCount[0], Is.EqualTo(2));
            Assert.That(input.weightCount[2], Is.EqualTo(1));
            Assert.That(input.weights[input.weightStart[0]], Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(input.bones[input.weightStart[2]], Is.EqualTo(0));

            Assert.That(VpSharedSkinInput.TryPrepare(null, out _, out refusal), Is.False);
            Assert.That(refusal, Is.EqualTo("no mesh"));
            var two = Track(new Mesh { name = "two submeshes" });
            two.vertices = mesh.vertices; two.normals = mesh.normals; two.uv = mesh.uv; two.bindposes = mesh.bindposes; two.boneWeights = mesh.boneWeights;
            two.subMeshCount = 2; two.SetTriangles(new[] { 0, 2, 1, 0, 1, 3 }, 0); two.SetTriangles(new[] { 0, 3, 2, 1, 2, 3 }, 1);
            Assert.That(VpSharedSkinInput.TryPrepare(two, out _, out refusal), Is.False);
            Assert.That(refusal, Does.Contain("submeshes"));
            var unskinned = Track(new Mesh { name = "no weights" });
            unskinned.vertices = mesh.vertices; unskinned.normals = mesh.normals; unskinned.triangles = mesh.triangles;
            Assert.That(VpSharedSkinInput.TryPrepare(unskinned, out _, out refusal), Is.False);
            Assert.That(refusal, Does.Contain("weight layout"));

            Assert.That(VpSharedSkinInput.TryChooseWeightReading(GraphicsBuffer.Target.Vertex | GraphicsBuffer.Target.Structured, 32, out var reading, out refusal), Is.True);
            Assert.That(reading, Is.EqualTo(VpSharedSkinInput.WeightReadingKind.Structured32));
            Assert.That(VpSharedSkinInput.TryChooseWeightReading(GraphicsBuffer.Target.Vertex | GraphicsBuffer.Target.Structured, 16, out _, out refusal), Is.False, "TwoBones structured elements were not verified");
            Assert.That(VpSharedSkinInput.TryChooseWeightReading(GraphicsBuffer.Target.Vertex, 32, out _, out refusal), Is.False);
        }

        [Test]
        public unsafe void TheFloatKernel_MatchesTheProbeReference_BitForBit_AndCopiesTheTopology()
        {
            Mesh mesh = SmallRig(out Matrix4x4[] bindposes);
            Assert.That(VpSharedSkinInput.TryPrepare(mesh, out VpSharedSkinInput input, out string refusal), Is.True, refusal);
            Keep(input);
            // The probe's reference over the same mesh (its own reading of the attributes).
            Assert.That(SkinBakeProbeMeshInput.TryPrepare(mesh, out SkinBakeProbeMeshInput probeInput, out refusal), Is.True, refusal);
            Keep(probeInput);
            SkinBakeProbeCpuBake.Inputs reference = SkinBakeProbeCpuBake.Read(probeInput);
            var palette = new float4x4[]
            {
                (float4x4)(Matrix4x4.TRS(new Vector3(0.1f, 0.2f, 0.3f), Quaternion.Euler(10, 20, 30), Vector3.one) * bindposes[0]),
                (float4x4)(Matrix4x4.TRS(new Vector3(-0.2f, 0.5f, 0.1f), Quaternion.Euler(-40, 5, 70), Vector3.one) * bindposes[1]),
            };
            var expected = new VpRenderVertex[4];
            int expectedInvalid = SkinBakeProbeCpuBake.Bake(reference, palette, expected, out float3 expectedMin, out float3 expectedMax);
            var topologyIn = new NativeArray<int>(new[] { 7, 5, 3, 1 }, Allocator.Temp);
            var output = new NativeArray<VpRenderVertex>(4, Allocator.Temp);
            var topologyOut = new NativeArray<int>(4, Allocator.Temp);
            var bounds = new NativeArray<float3>(2, Allocator.Temp);
            var marker = new NativeArray<int>(1, Allocator.Temp);
            try
            {
                fixed (float4x4* p = palette)
                {
                    int invalid = VpSkinFloatKernel.Skin((float3*)input.positions.GetUnsafeReadOnlyPtr(), (float3*)input.normals.GetUnsafeReadOnlyPtr(), (float2*)input.uv.GetUnsafeReadOnlyPtr(), 1,
                        (int*)input.weightStart.GetUnsafeReadOnlyPtr(), (int*)input.weightCount.GetUnsafeReadOnlyPtr(), (int*)input.bones.GetUnsafeReadOnlyPtr(), (float*)input.weights.GetUnsafeReadOnlyPtr(),
                        p, 2, (int*)topologyIn.GetUnsafeReadOnlyPtr(), (VpRenderVertex*)output.GetUnsafePtr(), (int*)topologyOut.GetUnsafePtr(), 4, (float3*)bounds.GetUnsafePtr(), (int*)marker.GetUnsafePtr());
                    Assert.That(invalid, Is.EqualTo(expectedInvalid));
                    Assert.That(invalid, Is.Zero);
                }

                for (int i = 0; i < 4; i++)
                {
                    Assert.That((float3)output[i].position, Is.EqualTo((float3)expected[i].position), "vertex " + i + " position");
                    Assert.That(output[i].normalX, Is.EqualTo(expected[i].normalX), "vertex " + i + " normal x");
                    Assert.That(output[i].normalY, Is.EqualTo(expected[i].normalY), "vertex " + i + " normal y");
                    Assert.That(output[i].u, Is.EqualTo(expected[i].u));
                    Assert.That(output[i].v, Is.EqualTo(expected[i].v));
                    Assert.That(topologyOut[i], Is.EqualTo(topologyIn[i]));
                }

                Assert.That(bounds[0], Is.EqualTo(expectedMin));
                Assert.That(bounds[1], Is.EqualTo(expectedMax));
                // And the box of the pose holds every skinned vertex.
                Bounds box = VpSkinConservativeBounds.Of(mesh.bounds, palette, 2);
                for (int i = 0; i < 4; i++) Assert.That(box.Contains(output[i].position), Is.True, "vertex " + i + " inside the conservative box");
                var measured = new Bounds(); measured.SetMinMax(bounds[0], bounds[1]);
                Assert.That(VpSkinConservativeBounds.Contains(box, measured), Is.True);
                Assert.That(VpSkinConservativeBounds.Contains(measured, box), Is.False, "the conservative box is the larger");
            }
            finally
            {
                topologyIn.Dispose(); output.Dispose(); topologyOut.Dispose(); bounds.Dispose(); marker.Dispose();
            }
        }

        [Test]
        public void TheConservativeBox_HoldsTheOrigin_AndIsWidenedByTheMargin()
        {
            var bind = new Bounds(new Vector3(0, 1, 0), new Vector3(1, 2, 1));
            var palette = new[] { float4x4.identity };
            Bounds box = VpSkinConservativeBounds.Of(bind, palette, 1, 0f);
            Assert.That(box.min, Is.EqualTo(new Vector3(-0.5f, 0f, -0.5f)));
            Assert.That(box.max, Is.EqualTo(new Vector3(0.5f, 2f, 0.5f)));
            Bounds widened = VpSkinConservativeBounds.Of(bind, palette, 1, 0.25f);
            Assert.That(widened.min, Is.EqualTo(new Vector3(-0.75f, -0.25f, -0.75f)));
            Assert.Throws<ArgumentOutOfRangeException>(() => VpSkinConservativeBounds.Of(bind, palette, 0));
        }
    }
}
