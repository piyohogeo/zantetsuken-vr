using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The graph stage of the additional spatial split and its graph-first order -- the configuration C -- are on in a fresh
    /// profile (D-256, 2026-10-26: adopted without an overall performance verdict; partial measurements exist), with the provisional limits of 2026-10-23, and a
    /// Player's arguments switch them off (back to the configuration D) or on for a run.
    /// </summary>
    public sealed class SpatialSplitGraphDefaultTests
    {
        [Test]
        public void AFreshProfile_HasTheGraphStageAndTheGraphFirstOrderOn_WithTheProvisionalLimits()
        {
            var profile = ScriptableObject.CreateInstance<CutWorldProfile>();
            try
            {
                CutSpatialSplitSettings settings = profile.SpatialSplit;
                Assert.That(settings.enabled, Is.True, "the split itself is on by default (2026-10-11)");
                Assert.That(settings.bentSearch, Is.True, "the bent partition stays on by default (D-237): the fallback after an unfinished graph");
                Assert.That(settings.graphSearch, Is.True, "the graph stage is on by default (D-256)");
                Assert.That(settings.graphFirst, Is.True, "the graph-first order is on by default (D-256)");
                Assert.That(settings.graphLimits.Equals(SpatialSplitGraphLimits.Default), Is.True, "the limits are the provisional ones of 2026-10-23");
                Assert.That(settings.IsValid, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void TheArguments_SwitchTheGraphStageOffOrOn_AndWithoutThemTheProfileStands()
        {
            Assert.That(CutWorldRoot.TryReadGraphSwitch(new[] { "player.exe", "-zantetsuScenario", "city-walk" }, out bool on), Is.False, "neither argument: the profile's setting");
            Assert.That(CutWorldRoot.TryReadGraphSwitch(null, out on), Is.False);
            Assert.That(CutWorldRoot.TryReadGraphSwitch(new[] { CutWorldRoot.SpatialSplitGraphOffArgument }, out on), Is.True);
            Assert.That(on, Is.False, "the off argument: off for the run (the configuration D)");
            Assert.That(CutWorldRoot.TryReadGraphSwitch(new[] { CutWorldRoot.SpatialSplitGraphArgument }, out on), Is.True);
            Assert.That(on, Is.True, "the on argument: on for the run whatever the profile");
            Assert.That(CutWorldRoot.TryReadGraphSwitch(new[] { CutWorldRoot.SpatialSplitGraphOffArgument, CutWorldRoot.SpatialSplitGraphArgument }, out on), Is.True);
            Assert.That(on, Is.True, "both: on wins");
            Assert.That(CutWorldRoot.SpatialSplitGraphOffArgument, Is.EqualTo("-zantetsuSpatialSplitGraphOff"));
        }

        [Test]
        public void TheArguments_SwitchTheGraphFirstOrderOffOrOn_AndWithoutThemTheProfileStands()
        {
            Assert.That(CutWorldRoot.TryReadGraphFirstSwitch(new[] { "player.exe", "-zantetsuScenario", "city-walk" }, out bool on), Is.False, "neither argument: the profile's setting");
            Assert.That(CutWorldRoot.TryReadGraphFirstSwitch(new[] { CutWorldRoot.SpatialSplitGraphFirstOffArgument }, out on), Is.True);
            Assert.That(on, Is.False, "the off argument: the order as before for the run (the configuration D)");
            Assert.That(CutWorldRoot.TryReadGraphFirstSwitch(new[] { CutWorldRoot.SpatialSplitGraphFirstArgument }, out on), Is.True);
            Assert.That(on, Is.True);
            Assert.That(CutWorldRoot.SpatialSplitGraphFirstOffArgument, Is.EqualTo("-zantetsuSpatialSplitGraphFirstOff"));
            // the two off arguments together are the way back to D: neither reader takes the other's argument
            string[] d = { CutWorldRoot.SpatialSplitGraphOffArgument, CutWorldRoot.SpatialSplitGraphFirstOffArgument };
            Assert.That(CutWorldRoot.TryReadGraphSwitch(d, out on), Is.True); Assert.That(on, Is.False);
            Assert.That(CutWorldRoot.TryReadGraphFirstSwitch(d, out on), Is.True); Assert.That(on, Is.False);
            Assert.That(CutWorldRoot.TryReadGraphFirstSwitch(new[] { CutWorldRoot.SpatialSplitGraphOffArgument }, out on), Is.False, "the graph's off argument is not the order's");
        }
    }
}
