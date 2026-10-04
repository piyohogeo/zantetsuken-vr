using System;
using System.Collections;
using System.Collections.Generic;
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
    /// The city walk's registration cut short by its deadline (TL, 2026-10-03: the scenario's own path, exercised in the
    /// Editor): the registrars let go and held again in the first frame that registers any, so some are registered and the
    /// rest still waiting; in that same frame a building the hull trial refuses after its display took the geometry (in
    /// flight: the display's until a collection); then the check's own ending for that case
    /// (SandboxPropSlashPlayerCheck.CityWalkRegistrationCutShort: the registrars held, CheckEnding.Run -- the world's shutdown
    /// and reclaim). Afterwards: the world released within the ending's bound with no failure, the waiting ones never
    /// registered (even let go again, as at the quit) and their instances as they were, the registered ones' instances
    /// switched off and their actors destroyed with their owners, the building in flight left nothing; then the registrars
    /// destroyed (as the scene goes at the quit) give back what is their own without an error. Ignored where the licensed
    /// inputs are absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator CityWalk_RegistrationCutShort_TheEndingReleasesTheWorld_WithRegisteredWaitingAndInFlightOnes()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var input = new TextAsset(File.ReadAllText(PlacedPropInputPath)) { name = "bench_001" };
            var college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            // The hull trial's vertex limit below the building's hull (it is refused after its display took it); source 0 bound for the props.
            CutWorldRoot root = NewHullWorld(0.9f, profile => SetPrivate(profile, "vertexLimit", 8), 0.3f, r =>
            {
                var field = typeof(CutWorldRoot).GetField("materials", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var bound = (CutWorldRoot.MaterialBinding[])field.GetValue(r);
                var first = new CutWorldRoot.MaterialBinding { sourceIndex = 0, material = Track(new Material(Shader.Find("Zantetsu/VP Indexed Indirect Unlit")) { name = "first" }) };
                field.SetValue(r, bound.Concat(new[] { first }).ToArray());
            });
            int n = PlayableCityCuttable.PerFrame + 4;
            var placed = new PlayableCityCuttable[n];
            var renderers = new Renderer[n][];
            var colliders = new Collider[n][];
            var actors = new List<GameObject>();
            PlayableCityCuttable.Held = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    GameObject instance = RefusalInstance("bench_001 cut short #" + i, new Vector3(-18f + 3f * i, -1f, 8f), out renderers[i], out colliders[i]);
                    placed[i] = PlaceRegistrar(root, input, "bench_001 cut short #" + i + " (registrar)", Vector3.zero, 0f, Vector3.one);
                    placed[i].target = instance.transform;
                    placed[i].instanceRenderers = renderers[i];
                    placed[i].instanceColliders = colliders[i];
                }

                string asPlaced = States(renderers[0], colliders[0]);
                int bodies = Bodies();
                for (int f = 0; f < 5; f++) yield return null;
                Assert.That(placed.Count(c => c.IsRegistered || c.Failure != null), Is.Zero, "held: none registers");

                // Let go, and cut short in the first frame that registers any (this resumes after the registrars' Update).
                PlayableCityCuttable.Held = false;
                yield return UntilWithin(() => placed.Any(c => c.IsRegistered), 5f, "the first registrations");
                PlayableCityCuttable.Held = true;
                PlayableCityCuttable[] registered = placed.Where(c => c.IsRegistered).ToArray();
                PlayableCityCuttable[] waiting = placed.Where(c => !c.IsRegistered).ToArray();
                Assert.That(registered.Length, Is.InRange(1, n - 1), "part way: " + registered.Length + " of " + n + " registered");
                Assert.That(placed.Count(c => c.Failure != null), Is.Zero, "none refused");
                foreach (PlayableCityCuttable c in registered) actors.Add(c.Registration.Actor);

                // In flight in the same frame: the hull trial refuses the building after its display took the geometry.
                GameObject building = RefusalInstance("college_001 in flight", new Vector3(0f, -1f, -20f), out Renderer[] buildingRenderers, out Collider[] buildingColliders);
                string buildingAsPlaced = States(buildingRenderers, buildingColliders);
                int groups = root.Storage.VertexGroupCount;
                LogAssert.Expect(LogType.Error, new Regex("the hull trial refused the building"));
                Assert.Throws<InvalidOperationException>(() =>
                    PlacedCuttableRegistration.RegisterHull(root, college, building.transform, buildingRenderers, buildingColliders, 10000f, SideMaterial));
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups + 1), "in flight: the geometry still the display's");

                // The check's own ending for a registration cut short.
                var lines = new List<string>();
                var ending = new CheckEnding(lines.Add);
                float endingFrom = Time.realtimeSinceStartup;
                yield return SandboxPropSlashPlayerCheck.CityWalkRegistrationCutShort(root, ending, placed, lines.Add);
                foreach (string line in lines) TestContext.Out.WriteLine(line);
                TestContext.Out.WriteLine("ending took " + (Time.realtimeSinceStartup - endingFrom).ToString("F3") + " s; registered " + registered.Length + ", waiting " + waiting.Length);
                Assert.That(ending.WorldReleased, Is.True, "the world released within the ending's bound");
                Assert.That(ending.Failures, Is.Zero, "the ending's parts did not fail");
                Assert.That(root.IsReleased, Is.True);
                Assert.That(root.GeometryFaults, Is.Zero);
                Assert.That(lines.Any(l => l.Contains("registered " + registered.Length + ", refused 0, still waiting " + waiting.Length)), Is.True, "what stood where written down");

                // The waiting ones: never registered, even let go again (as at the quit), and their instances as they were.
                PlayableCityCuttable.Held = false;
                for (int f = 0; f < 10; f++) yield return null;
                Assert.That(waiting.Count(c => c.IsRegistered || c.Failure != null), Is.Zero, "the waiting ones never registered after the cut");
                for (int i = 0; i < n; i++)
                {
                    if (placed[i].IsRegistered) Assert.That(States(renderers[i], colliders[i]), Is.EqualTo("off,off|off,off"), placed[i].name + ": switched off when it was registered");
                    else Assert.That(States(renderers[i], colliders[i]), Is.EqualTo(asPlaced), placed[i].name + ": as placed");
                }

                Assert.That(States(buildingRenderers, buildingColliders), Is.EqualTo(buildingAsPlaced), "the building in flight: its instance as placed");
                Assert.That(GameObject.Find("Building " + college.name), Is.Null, "the building in flight: no actor of it");
                // The registered ones: the world's release ended their owners -- actors destroyed, shapes given up (PhysicsOwnerRegistry.Dispose).
                Assert.That(actors.All(a => a == null), Is.True, "the registered ones' actors destroyed by the world's release (" + actors.Count(a => a != null) + " left)");
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body left");

                // The registrars destroyed, as the scene goes at the quit: each registered one gives back what is its own (its
                // convex arrays and collider meshes; its shape's second Dispose is the owner's being done again, a no-op), without an error.
                TrackPlacedRegistrars();
                foreach (PlayableCityCuttable c in placed) UnityEngine.Object.Destroy(c.gameObject);
                yield return null;
                Assert.That(placed.All(c => c == null), Is.True, "the registrars destroyed");
                yield return null;
                yield return EndWorld(root);
            }
            finally
            {
                PlayableCityCuttable.Held = false;
                TrackPlacedRegistrars();
            }
        }
    }
}
