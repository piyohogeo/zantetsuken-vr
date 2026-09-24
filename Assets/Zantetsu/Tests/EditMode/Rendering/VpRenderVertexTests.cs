using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>Shared CPU/GPU Compact16uv ABI. Decoded properties are not additional stored fields.</summary>
    public class VpRenderVertexTests
    {
        [Test]
        public void TheVertex_HasOnlyPositionNormalAndUv0InOrder()
        {
            FieldInfo[] fields = typeof(VpRenderVertex).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Select(f => f.Name), Is.EqualTo(new[] { "position", "normalX", "normalY", "u", "v" }));
            Assert.That(fields.Select(f => f.FieldType), Is.EqualTo(new[] { typeof(Vector3), typeof(sbyte), typeof(sbyte), typeof(byte), typeof(byte) }));
        }

        [Test]
        public void TheVertex_Is16BytesWithoutPadding()
        {
            Assert.That(VpRenderVertex.Stride, Is.EqualTo(16));
            Assert.That(UnsafeUtility.SizeOf<VpRenderVertex>(), Is.EqualTo(VpRenderVertex.Stride));
            Assert.That(Marshal.SizeOf<VpRenderVertex>(), Is.EqualTo(VpRenderVertex.Stride));
            Assert.That(Marshal.OffsetOf<VpRenderVertex>("position").ToInt32(), Is.EqualTo(0));
            Assert.That(Marshal.OffsetOf<VpRenderVertex>("normalX").ToInt32(), Is.EqualTo(12));
            Assert.That(Marshal.OffsetOf<VpRenderVertex>("normalY").ToInt32(), Is.EqualTo(13));
            Assert.That(Marshal.OffsetOf<VpRenderVertex>("u").ToInt32(), Is.EqualTo(14));
            Assert.That(Marshal.OffsetOf<VpRenderVertex>("v").ToInt32(), Is.EqualTo(15));
        }

        [Test]
        public void EveryUvCentre_RoundTripsExactly()
        {
            for (int i = 0; i < 256; i++)
            {
                var uv = new Vector2((i + .5f) / 256f, (255 - i + .5f) / 256f);
                var vertex = new VpRenderVertex { normal = Vector3.up, uv0 = uv };
                Assert.That(vertex.u, Is.EqualTo(i)); Assert.That(vertex.v, Is.EqualTo(255 - i));
                Assert.That(vertex.uv0, Is.EqualTo(uv));
            }
        }

        [Test]
        public void OctNormal_AllOctants_RetainDirectionWithinOneDegree()
        {
            for (int x = -9; x <= 9; x++) for (int y = -9; y <= 9; y++) for (int z = -9; z <= 9; z++)
            {
                if (x == 0 && y == 0 && z == 0) continue;
                var source = new Vector3(x, y, z);
                var vertex = new VpRenderVertex { normal = source };
                Assert.That(vertex.HasValidAttributes, Is.True);
                Assert.That(Vector3.Angle(source, vertex.normal), Is.LessThan(1f));
                Assert.That(vertex.normal.magnitude, Is.EqualTo(1f).Within(1e-6f));
            }
        }

        [Test]
        public void UnsupportedAttributes_AreNotSilentlyMadeValid()
        {
            Assert.That(new VpRenderVertex { normal = Vector3.zero }.HasValidAttributes, Is.False);
            Assert.That(new VpRenderVertex { normal = new Vector3(float.NaN, 0, 0) }.HasValidAttributes, Is.False);
            Assert.That(new VpRenderVertex { normal = Vector3.up, uv0 = new Vector2(-.5f, 0) }.HasValidAttributes, Is.False);
            Assert.That(new VpRenderVertex { normal = Vector3.up, uv0 = new Vector2(2, 0) }.HasValidAttributes, Is.False);
            Assert.That(new VpRenderVertex { uv0 = new Vector2(-.5f, 0), normal = Vector3.up }.HasValidAttributes, Is.False, "initializer order cannot erase rejection");
        }
    }
}
