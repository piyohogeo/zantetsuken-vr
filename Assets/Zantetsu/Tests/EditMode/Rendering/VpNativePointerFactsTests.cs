using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// What Unity does to a native pointer on this device when an object is updated, made again or replaced: the
    /// facts the native pointer cache's invalidation rests on (TL, 2026-10-08). Each fact is printed and the ones the
    /// cache relies on are asserted, so that a Unity version that changes them fails here, not in a Player.
    /// </summary>
    public class VpNativePointerFactsTests
    {
        [Test]
        public void GraphicsBuffer_KeepsItsNativeBuffer_AcrossSetData_AndANewObjectHasItsOwn()
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 64, 16);
            var other = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 64, 16);
            try
            {
                IntPtr before = buffer.GetNativeBufferPtr();
                var data = new Vector4[64];
                for (int i = 0; i < 10; i++)
                {
                    data[0] = new Vector4(i, 0, 0, 0);
                    buffer.SetData(data);
                    buffer.SetData(data, 1, 1, 8);
                }

                IntPtr after = buffer.GetNativeBufferPtr();
                TestContext.Out.WriteLine("GraphicsBuffer: before SetData " + before + ", after ten SetData " + after + "; another object " + other.GetNativeBufferPtr());
                Assert.That(before, Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(after, Is.EqualTo(before), "SetData writes into the same native buffer");
                Assert.That(other.GetNativeBufferPtr(), Is.Not.EqualTo(before), "a new object is another native buffer");
            }
            finally
            {
                buffer.Dispose();
                other.Dispose();
            }
        }

        [Test]
        public void Texture2D_KeepsItsNativeTexture_AcrossApply_AndWhatReinitializeDoes()
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            try
            {
                texture.Apply();
                IntPtr first = texture.GetNativeTexturePtr();
                uint count0 = texture.updateCount;
                texture.SetPixel(0, 0, Color.red);
                texture.Apply();
                IntPtr afterApply = texture.GetNativeTexturePtr();
                uint count1 = texture.updateCount;
                bool reinitialized = texture.Reinitialize(8, 8);
                texture.Apply();
                IntPtr afterSameSize = texture.GetNativeTexturePtr();
                uint count2 = texture.updateCount;
                texture.Reinitialize(16, 16);
                texture.Apply();
                IntPtr afterOtherSize = texture.GetNativeTexturePtr();
                uint count3 = texture.updateCount;
                TestContext.Out.WriteLine("Texture2D: first " + first + " (updateCount " + count0 + "), after Apply " + afterApply + " (" + count1 + "), after Reinitialize same size " + afterSameSize + " (" + count2 + ", returned " + reinitialized + "), after Reinitialize 16x16 " + afterOtherSize + " (" + count3 + ")");
                Assert.That(first, Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(afterApply, Is.EqualTo(first), "Apply uploads into the same native texture");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void RenderTexture_WhatReleaseAndCreateDo_ToItsPointers_AndItsCounters()
        {
            var renderTexture = new RenderTexture(16, 16, 16, RenderTextureFormat.ARGB32);
            try
            {
                renderTexture.Create();
                IntPtr colour0 = renderTexture.GetNativeTexturePtr();
                IntPtr depth0 = renderTexture.depthBuffer.GetNativeRenderBufferPtr();
                IntPtr colourBuffer0 = renderTexture.colorBuffer.GetNativeRenderBufferPtr();
                uint count0 = renderTexture.updateCount;
                renderTexture.Release();
                Assert.That(renderTexture.IsCreated(), Is.False);
                renderTexture.Create();
                IntPtr colour1 = renderTexture.GetNativeTexturePtr();
                IntPtr depth1 = renderTexture.depthBuffer.GetNativeRenderBufferPtr();
                IntPtr colourBuffer1 = renderTexture.colorBuffer.GetNativeRenderBufferPtr();
                uint count1 = renderTexture.updateCount;
                TestContext.Out.WriteLine("RenderTexture: colour texture " + colour0 + " -> " + colour1 + " (" + (colour0 == colour1 ? "same" : "CHANGED") + "); depth render buffer handle " + depth0 + " -> " + depth1 + " (" + (depth0 == depth1 ? "same" : "CHANGED") + "); colour render buffer handle " + colourBuffer0 + " -> " + colourBuffer1 + " (" + (colourBuffer0 == colourBuffer1 ? "same" : "CHANGED") + "); updateCount " + count0 + " -> " + count1 + "; format " + renderTexture.graphicsFormat + " depth " + renderTexture.depthStencilFormat);
                Assert.That(colour0, Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(depth0, Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(count1, Is.GreaterThan(count0), "Create raises updateCount: what the pointer cache detects a re-creation by");
                Assert.That(colour1, Is.Not.EqualTo(colour0), "the native colour texture is another after Release and Create (why the cache keeps render buffer handles, not texture pointers)");
            }
            finally
            {
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }
    }
}
