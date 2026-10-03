using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A placed cuttable's registration that fails leaves the scene as it found it (TL, 2026-10-03): no actor, body or
    /// collider of it stays, the instance's renderers and colliders keep the states they had (on or off), and a geometry
    /// the world never showed gives its storage room back. Each failure is made on purpose: the display refusing a source
    /// material it does not bind (the prop path and the building path), and the storage refusing a geometry with a zero
    /// normal. Ignored where the licensed inputs are absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const string RefusalPropInputPath = "Assets/Licensed/WalkCity/Inputs/bench_001.json";

        /// <summary>An instance with two renderers and two colliders, one of each switched off beforehand.</summary>
        private GameObject RefusalInstance(string name, Vector3 at, out Renderer[] renderers, out Collider[] colliders)
        {
            GameObject instance = TrackActor(new GameObject(name));
            instance.transform.position = at;
            var drawn = new GameObject("drawn");
            drawn.transform.SetParent(instance.transform, false);
            drawn.AddComponent<MeshFilter>();
            var on = drawn.AddComponent<MeshRenderer>();
            var hidden = new GameObject("hidden");
            hidden.transform.SetParent(instance.transform, false);
            hidden.AddComponent<MeshFilter>();
            var off = hidden.AddComponent<MeshRenderer>();
            off.enabled = false;
            var solid = instance.AddComponent<BoxCollider>();
            var spare = instance.AddComponent<SphereCollider>();
            spare.enabled = false;
            renderers = new Renderer[] { on, off };
            colliders = new Collider[] { solid, spare };
            return instance;
        }

        private static string States(Renderer[] renderers, Collider[] colliders) =>
            string.Join(",", renderers.Select(r => r.enabled ? "on" : "off")) + "|" + string.Join(",", colliders.Select(c => c.enabled ? "on" : "off"));

        private static int Bodies() => UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;

        [UnityTest]
        public IEnumerator PlacedRegistration_RefusedByTheDisplay_LeavesNothingAndTheInstanceAsItWas()
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            CutWorldRoot root = NewHullWorld(0.9f);   // binds the source materials 7 and 2 only: the input's 0 is refused at the display
            yield return null;
            GameObject instance = RefusalInstance("bench_001 refused", new Vector3(2f, -1f, 2f), out Renderer[] renderers, out Collider[] colliders);
            string before = States(renderers, colliders);
            int bodies = Bodies(), groups = root.Storage.VertexGroupCount, freeVertices = root.Storage.FreeVertexRoom, freeBlocks = root.Storage.FreeVertexBlockRoom;
            LogAssert.Expect(LogType.Error, new Regex("the display refused to show this body"));
            var e = Assert.Throws<InvalidOperationException>(() =>
                PlacedCuttableRegistration.Register(root, data, instance.transform, renderers, colliders, 50f, false));
            TestContext.Out.WriteLine("refusal: " + e.Message);
            Assert.That(e.Message, Does.Contain("refused by the world").And.Contain("given back: the actor, the convex, the stored geometry"));
            yield return null;   // the frame the actor's destroy takes effect in
            Assert.That(Bodies(), Is.EqualTo(bodies), "no body of the refused registration stays");
            Assert.That(GameObject.Find("Prop " + data.name), Is.Null, "no actor of it stays");
            Assert.That(States(renderers, colliders), Is.EqualTo(before), "the instance's renderers and colliders keep their states");
            Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "its vertex group given back");
            Assert.That(root.Storage.FreeVertexRoom, Is.EqualTo(freeVertices), "its vertex room given back");
            Assert.That(root.Storage.FreeVertexBlockRoom, Is.EqualTo(freeBlocks), "its vertex blocks given back");
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator PlacedRegistration_RefusedByTheStorage_MakesNoActorAndLeavesTheInstanceAsItWas()
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            data.render[data.indices[0]].normal = Vector3.zero;   // a zero normal: the cut input gate refuses the geometry
            CutWorldRoot root = NewHullWorld(0.9f);
            yield return null;
            GameObject instance = RefusalInstance("bench_001 zero normal", new Vector3(-2f, -1f, 2f), out Renderer[] renderers, out Collider[] colliders);
            string before = States(renderers, colliders);
            int bodies = Bodies(), groups = root.Storage.VertexGroupCount, freeVertices = root.Storage.FreeVertexRoom;
            var e = Assert.Throws<InvalidOperationException>(() =>
                PlacedCuttableRegistration.Register(root, data, instance.transform, renderers, colliders, 50f, false));
            TestContext.Out.WriteLine("refusal: " + e.Message);
            Assert.That(e.Message, Does.Contain("the geometry was refused").And.Contain("given back: the convex").And.Not.Contain("the actor"));
            yield return null;
            Assert.That(Bodies(), Is.EqualTo(bodies), "no body made");
            Assert.That(States(renderers, colliders), Is.EqualTo(before), "the instance's renderers and colliders keep their states");
            Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "nothing stored");
            Assert.That(root.Storage.FreeVertexRoom, Is.EqualTo(freeVertices), "no vertex room taken");
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator PlacedHullRegistration_RefusedByTheDisplay_LeavesNoActorAndTheInstanceAsItWas()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            CutWorldRoot root = NewHullWorld(0.9f);
            yield return null;
            GameObject instance = RefusalInstance("college_001 refused", new Vector3(0f, -1f, 0f), out Renderer[] renderers, out Collider[] colliders);
            string before = States(renderers, colliders);
            int bodies = Bodies(), groupsMade = root.Hulls.GroupsMade;
            int groups = root.Storage.VertexGroupCount, freeVertices = root.Storage.FreeVertexRoom, freeBlocks = root.Storage.FreeVertexBlockRoom;
            LogAssert.Expect(LogType.Error, new Regex("the display refused to show the building"));
            var e = Assert.Throws<InvalidOperationException>(() =>
                PlacedCuttableRegistration.RegisterHull(root, data, instance.transform, renderers, colliders, 10000f, 5));   // source 5: not bound
            TestContext.Out.WriteLine("refusal: " + e.Message);
            // The display never showed it, so the geometry is given back here, once (2026-10-03: no longer kept).
            Assert.That(e.Message, Does.Contain("refused by the hull trial").And.Contain("given back: the actor, the convex, the stored geometry (indices retired True, vertex room released True)"));
            Assert.That(e.Message, Does.Not.Contain("left to the world"));
            yield return null;
            Assert.That(Bodies(), Is.EqualTo(bodies), "no body of the refused building stays");
            Assert.That(GameObject.Find("Building " + data.name), Is.Null, "no actor of it stays");
            Assert.That(States(renderers, colliders), Is.EqualTo(before), "the instance's renderers and colliders keep their states");
            Assert.That(root.Hulls.GroupsMade, Is.EqualTo(groupsMade), "no hull group made");
            Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "its vertex group given back");
            Assert.That(root.Storage.FreeVertexRoom, Is.EqualTo(freeVertices), "its vertex room given back");
            Assert.That(root.Storage.FreeVertexBlockRoom, Is.EqualTo(freeBlocks), "its vertex blocks given back");
            yield return EndWorld(root);
        }
    }
}
