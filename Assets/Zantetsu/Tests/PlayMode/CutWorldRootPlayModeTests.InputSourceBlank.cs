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
    /// A licensed input with its source written empty, as the distribution build carries it (unit 11, TL 2026-10-18:
    /// Tools/Quest3s/InputSources.ps1 takes that one value out): read as the runtime reads it, rebuilt at a placed scale
    /// (PlayableCityCuttable.Scaled, which copies the source) and registered, it gives what the original gives. Ignored
    /// where the licensed input (bench_001) is absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator InputSourceBlank_TheInputWithAnEmptySource_IsReadRebuiltAndRegisteredAsTheOriginal()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            string originalText = File.ReadAllText(PlacedPropInputPath);
            // The same change as the build's: the one source value taken out, nothing else.
            var source = new Regex("\"source\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            MatchCollection found = source.Matches(originalText);
            Assert.That(found.Count, Is.EqualTo(1), "the input holds one source");
            Group value = found[0].Groups[1];
            Assert.That(value.Length, Is.GreaterThan(0), "the original's source is not empty");
            string blankText = originalText.Remove(value.Index, value.Length);

            var original = new TextAsset(originalText) { name = "bench_001" };
            var blank = new TextAsset(blankText) { name = "bench_001 blank source" };
            PlacedCuttableInput originalData = PlayableCityCuttable.ParsedInput(original);
            PlacedCuttableInput blankData = PlayableCityCuttable.ParsedInput(blank);
            Assert.That(blankData.source, Is.EqualTo(""), "read with an empty source");
            Assert.That(originalData.source, Is.Not.Empty);
            string originalJson = JsonUtility.ToJson(originalData);
            originalData.source = "";
            Assert.That(JsonUtility.ToJson(blankData), Is.EqualTo(JsonUtility.ToJson(originalData)), "read as the original but for the source");
            originalData = JsonUtility.FromJson<PlacedCuttableInput>(originalJson);

            const float scale = 1.6f;
            PlacedCuttableInput rebuilt = PlayableCityCuttable.Scaled(blankData, scale);
            Assert.That(rebuilt.source, Is.EqualTo(""), "the rebuilt input carries the empty source");
            PlacedCuttableInput rebuiltOriginal = PlayableCityCuttable.Scaled(originalData, scale);
            Assert.That(rebuiltOriginal.source, Is.EqualTo(originalData.source));
            rebuiltOriginal.source = "";
            Assert.That(JsonUtility.ToJson(rebuilt), Is.EqualTo(JsonUtility.ToJson(rebuiltOriginal)), "rebuilt at the placed scale as the original is");

            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                PlayableCityCuttable a = PlaceRegistrar(root, original, "bench_001 original source", new Vector3(3f, -1f, 2f), 90f, Vector3.one * scale);
                PlayableCityCuttable b = PlaceRegistrar(root, blank, "bench_001 blank source", new Vector3(-3f, -1f, 2f), 90f, Vector3.one * scale);
                yield return UntilWithin(() => (a.IsRegistered || a.Failure != null) && (b.IsRegistered || b.Failure != null), 5f, "both registrations");
                Assert.That(a.Failure, Is.Null);
                Assert.That(b.Failure, Is.Null, "the blank-source input registered without a refusal");
                Assert.That(b.IsRegistered && a.IsRegistered, Is.True);
                Assert.That(b.RegisteredScale, Is.EqualTo(a.RegisteredScale).Within(1e-6f));
                Assert.That(b.Registration.AnchorCount, Is.EqualTo(a.Registration.AnchorCount), "every anchor taken alike");
                Bounds made(PlayableCityCuttable c) => c.Registration.Actor.GetComponentsInChildren<MeshCollider>(true).Single().sharedMesh.bounds;
                Assert.That(Vector3.Distance(made(b).size, made(a).size), Is.LessThan(1e-5f), "the same convex");
                Assert.That(Vector3.Distance(made(b).center, made(a).center), Is.LessThan(1e-5f));
                yield return EndWorld(root);
            }
            finally
            {
                TrackPlacedRegistrars();
            }
        }
    }
}
