using System;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.Rendering.Tests
{
    public class VpCutSurfaceAtlasTests
    {
        [Test]
        public void DebugSwitch_ChangesOnlyCapTexture_AndInvalidBindLeavesStateUntouched()
        {
            var oldNormal=VpCutSurfaceAtlas.Normal;var oldDebug=VpCutSurfaceAtlas.Debug;
            var state=VpCutSurfaceColour.Capture();
            var normal=new Texture2D(256,256);var debug=new Texture2D(256,256);var wrong=new Texture2D(2,2);
            try
            {
                VpCutSurfaceColour.SetDebugEnabled(false);VpCutSurfaceAtlas.Bind(normal,debug);
                Assert.That(Shader.GetGlobalTexture("_VpPaletteSurface"),Is.SameAs(normal));
                Assert.That(Shader.GetGlobalTexture("_VpPaletteCaps"),Is.SameAs(normal));
                VpCutSurfaceColour.SetDebugEnabled(true);
                Assert.That(Shader.GetGlobalTexture("_VpPaletteSurface"),Is.SameAs(normal));
                Assert.That(Shader.GetGlobalTexture("_VpPaletteCaps"),Is.SameAs(debug));
                Assert.Throws<ArgumentException>(()=>VpCutSurfaceAtlas.Bind(normal,wrong));
                Assert.That(Shader.GetGlobalTexture("_VpPaletteCaps"),Is.SameAs(debug));
                VpCutSurfaceColour.ApplyDefaults();
                Assert.That(Shader.GetGlobalTexture("_VpPaletteCaps"),Is.SameAs(normal));
                VpCutSurfaceAtlas.Clear();
                Assert.That(normal!=null && debug!=null,Is.True,"caller retains ownership");
                Assert.That(Shader.GetGlobalFloat("_VpPaletteAtlasEnabled"),Is.Zero);
            }
            finally
            {
                VpCutSurfaceAtlas.Clear();VpCutSurfaceColour.Restore(state);
                if(oldNormal!=null&&oldDebug!=null)VpCutSurfaceAtlas.Bind(oldNormal,oldDebug);
                UnityEngine.Object.DestroyImmediate(normal);UnityEngine.Object.DestroyImmediate(debug);UnityEngine.Object.DestroyImmediate(wrong);
            }
        }
    }
}
