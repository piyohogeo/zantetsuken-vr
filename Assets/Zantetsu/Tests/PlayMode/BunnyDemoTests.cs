#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Unity.Mathematics;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public sealed class BunnyDemoTests
    {
        const string Path = "Assets/NonCommercialDemo/StanfordBunny/StanfordBunnyDemo.unity";
        sealed class Input : IPlayMenuInput
        {
            public PlayMenuInputFrame frame;
            public PlayMenuInputFrame Read(Transform space, Camera camera) => frame;
        }

        [UnityTest]
        public IEnumerator ActualBunnyCuts_MenuCountAndResetReplaceTheWholeWorld()
        {
            EditorSceneManager.LoadSceneInPlayMode(Path,new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            var demo = Object.FindFirstObjectByType<BunnyDemo>();
            Assert.That(demo,Is.Not.Null);
            Assert.That(demo.SpawnedCount,Is.EqualTo(3));
            var root = demo.world;
            Assert.That(root.Owners.TryGet(demo.Bodies[0].Fragment,out var owner),Is.True);
            Assert.That(owner.Shape.ConvexCount,Is.EqualTo(32));
            yield return null;
            yield return Picture("Logs/bunny-scene.png");
            // Exercise the real admission, geometry worker, physics cooking and publication, not a replacement mesh.
            var source = demo.Bodies[0].Fragment;
            var ask = new ProvisionalCutAsk {source=source,plane=new float4(0,1,0,-.3f), renderAnchor=float3.zero};
            float positiveVelocity = float.NaN, negativeVelocity = float.NaN;
            System.Action<ProvisionalOwnerPair> captureImpulse = pair =>
            {
                positiveVelocity = pair.Positive.LinearVelocity.y;
                negativeVelocity = pair.Negative.LinearVelocity.y;
            };
            root.Owners.ProvisionalAdded += captureImpulse;
            var acceptance = root.Driver.RequestCut(in ask,out var transaction,out var admission);
            Assert.That(transaction,Is.Not.Null,acceptance + ": " + admission);
            float deadline = Time.realtimeSinceStartup+30f;
            while (transaction.Phase != ProvisionalCutPhase.HandedOff && Time.realtimeSinceStartup<deadline) yield return null;
            Assert.That(transaction.Phase,Is.EqualTo(ProvisionalCutPhase.HandedOff),"physics cut finished and handed off: " + transaction.CutOutcome);
            Assert.That(root.Ledger.TryGetReplacement(source,out var positive,out var negative),Is.True,"Actual compound Bunny publishes two pieces");
            root.Owners.ProvisionalAdded -= captureImpulse;
            Assert.That(positiveVelocity,Is.EqualTo(1f).Within(.001f),"positive piece receives 1 m/s at publication");
            Assert.That(negativeVelocity,Is.EqualTo(-1f).Within(.001f),"negative piece receives -1 m/s at publication");
            Assert.That(root.TerminationRequested,Is.False);
            // Open by the same input frame as a left-controller menu press.
            var menu = PlayMenu.Instance;
            var device = new Input(); menu.Input = device;
            yield return null;
            device.frame.menuDown = true;
            yield return null;
            Assert.That(menu.IsOpen,Is.True);
            Assert.That(menu.View.Rows.Count,Is.EqualTo(6));
            Assert.That(VpCutSurfaceColour.DebugEnabled,Is.False);
            var debugRow = menu.View.Rows[4];
            device.frame.menuDown = false;
            device.frame.hasPointer = true;
            var debugPoint = debugRow.background.rectTransform.TransformPoint(Vector3.zero);
            device.frame.pointerOrigin = debugPoint - debugRow.background.rectTransform.forward;
            device.frame.pointerDirection = debugRow.background.rectTransform.forward;
            yield return null;
            device.frame.selectDown = true;
            yield return null;
            Assert.That(VpCutSurfaceColour.DebugEnabled,Is.True);
            device.frame.selectDown = false;
            yield return null;
            yield return Picture("Logs/bunny-menu.png");
            menu.View.Rows[3].write(2);
            Assert.That(demo.SpawnedCount,Is.EqualTo(3),"changing the slider alone does not rebuild");
            device.frame.menuDown=false;
            var rect = menu.View.ResetRect;
            Vector3 point = rect.TransformPoint(Vector3.zero);
            device.frame.hasPointer=true;
            device.frame.pointerOrigin=point-rect.forward;
            device.frame.pointerDirection=rect.forward;
            yield return null;
            device.frame.selectDown=true;
            yield return null;
            deadline = Time.realtimeSinceStartup+30f;
            while (demo.Resetting && Time.realtimeSinceStartup<deadline) yield return null;
            yield return null;
            var replacement = Object.FindFirstObjectByType<BunnyDemo>();
            Assert.That(replacement,Is.Not.Null);
            Assert.That(replacement.world,Is.Not.SameAs(root));
            Assert.That(replacement.SpawnedCount,Is.EqualTo(2));
            Assert.That(VpCutSurfaceColour.DebugEnabled,Is.True,"Bunny reset preserves debug colour selection");
            Assert.That(PlayMenu.Instance.IsOpen,Is.False);
            // Repeated reset: no pieces or old bodies survive and menu references bind to the new scene.
            replacement.RequestedCount=1;
            replacement.ResetScene();
            yield return null;
            deadline=Time.realtimeSinceStartup+30f;
            while(replacement.Resetting && Time.realtimeSinceStartup<deadline) yield return null;
            yield return null;
            Assert.That(Object.FindFirstObjectByType<BunnyDemo>().SpawnedCount,Is.EqualTo(1));
            Assert.That(replacement.world.Driver.SeparationStrength,Is.Not.Null,"scene reset preserves mass-based impulse");
            Assert.That(replacement.world.Driver.SeparationStrength(2),Is.EqualTo(2f));
        }

        static IEnumerator Picture(string path)
        {
            bool done=false;
            System.Action capture=()=> { if (!done) { SavePicture(path); done=true; } };
            // The product settles its drawing AFTER all LateUpdates, in its own physics step loop.
            CutPhysicsStep.JoinAfterCollect(capture);
            try { while(!done) yield return null; }
            finally { CutPhysicsStep.LeaveAfterCollect(capture); }
        }

        static void SavePicture(string path)
        {
            var camera=Camera.main;
            var before=camera.targetTexture;
            var target=new RenderTexture(1280,960,24);
            var active=RenderTexture.active;
            var image=new Texture2D(1280,960,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture=target;
                camera.Render();
                RenderTexture.active=target;
                image.ReadPixels(new Rect(0,0,1280,960),0,0); image.Apply();
                System.IO.File.WriteAllBytes(path,image.EncodeToPNG());
            }
            finally {camera.targetTexture=before; RenderTexture.active=active; target.Release(); Object.Destroy(target); Object.Destroy(image);}
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            var demo = Object.FindFirstObjectByType<BunnyDemo>();
            if(demo != null)
            {
                float deadline=Time.realtimeSinceStartup+30f;
                while(!demo.world.Shutdown() && Time.realtimeSinceStartup<deadline) yield return null;
                Assert.That(demo.world.IsReleased,Is.True);
            }
            VpCutSurfaceColour.SetDebugEnabled(false);
            if (PlayMenu.Instance != null) Object.Destroy(PlayMenu.Instance.gameObject);
            yield return null;
        }
    }
}
#endif
