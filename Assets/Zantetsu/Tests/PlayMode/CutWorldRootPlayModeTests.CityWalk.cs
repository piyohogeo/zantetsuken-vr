using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The city walk's placed cuttables (TL, 2026-10-03), through PlayableCityCuttable as the scene has them: a prop placed
    /// at a uniform scale is registered at its placed size (its convex scaled, at the instance's pose, the stand-in gone),
    /// one scaled unevenly is refused with its reason and not retried, and many instances of one asset are registered a
    /// few a frame from one parse of the input. Ignored where the licensed input (bench_001, a Megacity prop with one
    /// convex and four anchors) is absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator CityWalk_PropAtAUniformScale_RegisteredAtItsPlacedSize()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var input = new TextAsset(File.ReadAllText(PlacedPropInputPath)) { name = "bench_001" };
            PlacedCuttableInput data = PlayableCityCuttable.ParsedInput(input);
            CutWorldRoot root = NewPlacedHullWorld();
            const float scale = 1.6f;
            try
            {
                PlayableCityCuttable c = PlaceRegistrar(root, input, "bench_001 scaled", new Vector3(3f, -1f, 2f), 90f, Vector3.one * scale);
                yield return UntilWithin(() => c.IsRegistered || c.Failure != null, 5f, "the scaled prop's registration");
                Assert.That(c.Failure, Is.Null, "registered without a refusal");
                Assert.That(c.IsRegistered, Is.True);
                Assert.That(c.RegisteredScale, Is.EqualTo(c.target.lossyScale.x).And.EqualTo(scale).Within(1e-5f), "registered at the instance's scale, as its transform gives it");
                Assert.That(c.enabled, Is.False, "a registrar does its one registration and stops");
                Transform actor = c.Registration.Actor.transform;
                Assert.That(Vector3.Distance(actor.position, c.target.position), Is.LessThan(1e-4f), "the actor stands at the instance's position");
                Assert.That(Quaternion.Angle(actor.rotation, c.target.rotation), Is.LessThan(0.01f), "and turned as it is");
                Assert.That((actor.lossyScale - Vector3.one).sqrMagnitude, Is.LessThan(1e-8f), "the actor itself unscaled: the scale is in its shape");
                // Its convex is the input's scaled by the instance's scale: the collider's extent along each local axis.
                MeshCollider collider = c.Registration.Actor.GetComponentsInChildren<MeshCollider>(true).Single();
                Bounds local = new Bounds(data.hulls[0].vertices[0], Vector3.zero);
                foreach (Vector3 v in data.hulls[0].vertices) local.Encapsulate(v);
                Bounds made = collider.sharedMesh.bounds;
                Assert.That(Vector3.Distance(made.size, local.size * scale), Is.LessThan(1e-3f), "the convex at the placed size (" + made.size.ToString("F4") + " against " + (local.size * scale).ToString("F4") + ")");
                Assert.That(Vector3.Distance(made.center, local.center * scale), Is.LessThan(1e-3f), "about the input's own origin");
                Assert.That(c.Registration.AnchorCount, Is.EqualTo(data.anchors.Length), "every anchor taken");
                yield return null;
                Assert.That(GameObject.Find("bench_001 scaled (unscaled stand-in)"), Is.Null, "the stand-in is gone");
                yield return EndWorld(root);
            }
            finally
            {
                TrackPlacedRegistrars();
            }
        }

        [UnityTest]
        public IEnumerator CityWalk_PropScaledUnevenly_RefusedWithItsReason_NotRetried()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var input = new TextAsset(File.ReadAllText(PlacedPropInputPath)) { name = "bench_001" };
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                PlayableCityCuttable c = PlaceRegistrar(root, input, "bench_001 uneven", new Vector3(-3f, -1f, 2f), 0f, new Vector3(1f, 2f, 1f));
                yield return UntilWithin(() => c.IsRegistered || c.Failure != null, 5f, "the uneven prop's answer");
                Assert.That(c.IsRegistered, Is.False);
                Assert.That(c.Failure, Does.Contain("scaled unevenly"), "the reason named: " + c.Failure);
                Assert.That(c.enabled, Is.False, "not retried");
                Assert.That(GameObject.Find("bench_001 uneven (unscaled stand-in)"), Is.Null, "no stand-in made for a refused one");
                yield return EndWorld(root);
            }
            finally
            {
                TrackPlacedRegistrars();
            }
        }

        [UnityTest]
        public IEnumerator CityWalk_ManyInstancesOfOneAsset_AFewAFrame_FromOneParse()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var input = new TextAsset(File.ReadAllText(PlacedPropInputPath)) { name = "bench_001" };
            CutWorldRoot root = NewPlacedHullWorld();
            int n = PlayableCityCuttable.PerFrame + 4;
            try
            {
                var placed = new PlayableCityCuttable[n];
                for (int i = 0; i < n; i++) placed[i] = PlaceRegistrar(root, input, "bench_001 #" + i, new Vector3(-12f + 3f * i, -1f, 8f), 0f, Vector3.one);
                int most = 0;
                float by = Time.realtimeSinceStartup + 10f;
                int before = 0;
                while (placed.Any(c => !c.IsRegistered && c.Failure == null) && Time.realtimeSinceStartup < by)
                {
                    yield return null;
                    int now = placed.Count(c => c.IsRegistered || c.Failure != null);
                    most = Mathf.Max(most, now - before);
                    before = now;
                }

                Assert.That(placed.All(c => c.IsRegistered), Is.True, "every instance registered (" + string.Join("; ", placed.Where(c => c.Failure != null).Select(c => c.name + ": " + c.Failure)) + ")");
                Assert.That(most, Is.LessThanOrEqualTo(PlayableCityCuttable.PerFrame), "at most " + PlayableCityCuttable.PerFrame + " a frame (" + most + ")");
                Assert.That(placed.Select(c => c.Registration.Fragment).Distinct().Count(), Is.EqualTo(n), "each its own fragment");
                Assert.That(ReferenceEquals(PlayableCityCuttable.ParsedInput(input), PlayableCityCuttable.ParsedInput(input)), Is.True, "the input parsed once");
                yield return EndWorld(root);
            }
            finally
            {
                TrackPlacedRegistrars();
            }
        }
    }
}
