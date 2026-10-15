using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The bent partition of the additional spatial split is on in a fresh profile (D-237, 2026-10-16), with the
    /// provisional limits of D-235, and a Player's arguments switch it off or on for a run.
    /// </summary>
    public sealed class SpatialSplitBentDefaultTests
    {
        [Test]
        public void AFreshProfile_HasTheBentPartitionOn_WithTheProvisionalLimits()
        {
            var profile = ScriptableObject.CreateInstance<CutWorldProfile>();
            try
            {
                CutSpatialSplitSettings settings = profile.SpatialSplit;
                Assert.That(settings.enabled, Is.True, "the split itself is on by default (2026-10-11)");
                Assert.That(settings.bentSearch, Is.True, "the bent partition is on by default (D-237)");
                Assert.That(settings.bentLimits.Equals(SpatialSplitBentLimits.Default), Is.True, "the limits are D-235's provisional ones");
                Assert.That(settings.bentLimits.IsValid, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void TheArguments_SwitchTheBentPartitionOffOrOn_AndWithoutThemTheProfileStands()
        {
            Assert.That(CutWorldRoot.TryReadBentSwitch(new[] { "player.exe", "-zantetsuScenario", "city-walk" }, out bool on), Is.False, "neither argument: the profile's setting");
            Assert.That(CutWorldRoot.TryReadBentSwitch(null, out on), Is.False);
            Assert.That(CutWorldRoot.TryReadBentSwitch(new[] { CutWorldRoot.SpatialSplitBentOffArgument }, out on), Is.True);
            Assert.That(on, Is.False, "the off argument: off for the run (the comparison's control)");
            Assert.That(CutWorldRoot.TryReadBentSwitch(new[] { CutWorldRoot.SpatialSplitBentArgument }, out on), Is.True);
            Assert.That(on, Is.True, "the on argument: on for the run whatever the profile");
            Assert.That(CutWorldRoot.TryReadBentSwitch(new[] { CutWorldRoot.SpatialSplitBentOffArgument, CutWorldRoot.SpatialSplitBentArgument }, out on), Is.True);
            Assert.That(on, Is.True, "both: on wins");
            Assert.That(CutWorldRoot.SpatialSplitBentOffArgument, Is.EqualTo("-zantetsuSpatialSplitBentOff"));
        }
    }
}
