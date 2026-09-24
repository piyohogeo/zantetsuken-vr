using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public class CutWorldAtlasLifecycleTests
    {
        private readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
        private readonly List<CutWorldRoot> worlds=new List<CutWorldRoot>();
        private Texture2D oldNormal,oldDebug;
        [SetUp] public void SetUp(){oldNormal=VpCutSurfaceAtlas.Normal;oldDebug=VpCutSurfaceAtlas.Debug;VpCutSurfaceAtlas.Clear();}
        private T Own<T>(T value) where T:UnityEngine.Object{owned.Add(value);return value;}
        private Texture2D Texture(int size=256)=>Own(new Texture2D(size,size));
        private CutWorldRoot World(Texture2D normal,Texture2D debug)
        {
            var host=Own(new GameObject("Atlas lifecycle"));host.SetActive(false);
            var root=host.AddComponent<CutWorldRoot>();worlds.Add(root);
            Set(root,"profile",Own(ScriptableObject.CreateInstance<CutWorldProfile>()));
            Set(root,"normalPaletteAtlas",normal);Set(root,"debugPaletteAtlas",debug);
            host.SetActive(true);return root;
        }
        private static void Set(object target,string field,object value)=>target.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);
        [Test] public void PairBindsAtAwake_AndClearsOnlyAfterShutdown_WithoutDestroyingAssets()
        {
            var normal=Texture();var debug=Texture();var root=World(normal,debug);
            Assert.That(root.IsReady,Is.True);Assert.That(VpCutSurfaceAtlas.Normal,Is.SameAs(normal));
            Assert.That(root.Shutdown(),Is.True);Assert.That(VpCutSurfaceAtlas.IsBound,Is.False);
            Assert.That(normal!=null&&debug!=null,Is.True);
            var second=World(normal,debug);Assert.That(second.IsReady,Is.True); // scene reload may bind again
        }
        [TestCase(false)][TestCase(true)] public void PartialOrWrongSizePair_RefusesBeforeAllocatingWorld(bool wrongSize)
        {
            LogAssert.Expect(LogType.Error,"Atlas lifecycle: atlas setup requires a 256x256 pair and an unowned global binding.");
            var root=World(Texture(wrongSize?128:256),wrongSize?Texture():null);
            Assert.That(root.IsReady,Is.False);Assert.That(root.Storage,Is.Null);Assert.That(VpCutSurfaceAtlas.IsBound,Is.False);
        }
        [Test] public void SecondAtlasWorldCannotStealBinding_AndItsShutdownDoesNotClearFirst()
        {
            var first=World(Texture(),Texture());var normal=VpCutSurfaceAtlas.Normal;
            LogAssert.Expect(LogType.Error,"Atlas lifecycle: atlas setup requires a 256x256 pair and an unowned global binding.");
            var second=World(Texture(),Texture());Assert.That(second.IsReady,Is.False);Assert.That(second.Storage,Is.Null);
            Assert.That(second.Shutdown(),Is.True);Assert.That(VpCutSurfaceAtlas.Normal,Is.SameAs(normal));Assert.That(first.IsReady,Is.True);
        }
        [Test] public void OptOutWorldDoesNotOwnOrClearAnExistingBinding()
        {
            var normal=Texture();VpCutSurfaceAtlas.Bind(normal,Texture());var root=World(null,null);
            Assert.That(root.IsReady,Is.True);Assert.That(root.Shutdown(),Is.True);Assert.That(VpCutSurfaceAtlas.Normal,Is.SameAs(normal));
        }
        [TearDown] public void TearDown()
        {
            foreach(var world in worlds)if(world!=null)Assert.That(world.Shutdown(),Is.True,"No body/work was submitted by this fixture");
            worlds.Clear();
            foreach(var value in owned)if(value!=null)UnityEngine.Object.DestroyImmediate(value);owned.Clear();
            VpCutSurfaceAtlas.Clear();if(oldNormal!=null&&oldDebug!=null)VpCutSurfaceAtlas.Bind(oldNormal,oldDebug);
        }
    }
}
