using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>The fixed AoS layout of the VP vertex (DESIGN 4.5.1): position, normal, uv0 and nothing else, 32 bytes.</summary>
    public class VpRenderVertexTests
    {
        [Test]
        public void TheVertex_HasOnlyPositionNormalAndUv0InOrder()
        {
            FieldInfo[] fields = typeof(VpRenderVertex).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Select(f => f.Name), Is.EqualTo(new[] { "position", "normal", "uv0" }));
            Assert.That(fields.Select(f => f.FieldType), Is.EqualTo(new[] { typeof(Vector3), typeof(Vector3), typeof(Vector2) }));
        }

        [Test]
        public void TheVertex_Is32BytesWithoutPadding()
        {
            Assert.That(VpRenderVertex.Stride, Is.EqualTo(32));
            Assert.That(UnsafeUtility.SizeOf<VpRenderVertex>(), Is.EqualTo(VpRenderVertex.Stride));
            Assert.That(Marshal.SizeOf<VpRenderVertex>(), Is.EqualTo(VpRenderVertex.Stride));
            Assert.That(Marshal.OffsetOf<VpRenderVertex>("position").ToInt32(), Is.EqualTo(0));
            Assert.That(Marshal.OffsetOf<VpRenderVertex>("normal").ToInt32(), Is.EqualTo(12));
            Assert.That(Marshal.OffsetOf<VpRenderVertex>("uv0").ToInt32(), Is.EqualTo(24));
        }
    }
}
