using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The building lineage and the building World D6 limits of DESIGN 7.2.2 (T-094), as values: the planned depth,
    /// its saturation, and limits that stay finite and not negative at every depth.
    /// </summary>
    public class BuildingWorldD6Tests
    {
        [Test]
        public void ABuildingsChildren_AreOneDeeper_AndAnythingElseStaysFalseAndZero()
        {
            BuildingLineage building = BuildingLineage.RegisteredBuilding;
            Assert.That(building.IsBuildingDerived, Is.True);
            Assert.That(building.SplitDepth, Is.Zero, "a registered building starts at depth 0");
            Assert.That(building.ChildOfSplit(), Is.EqualTo(BuildingLineage.BuildingAt(1)));
            Assert.That(building.ChildOfSplit().ChildOfSplit(), Is.EqualTo(BuildingLineage.BuildingAt(2)));

            BuildingLineage other = BuildingLineage.NotBuilding;
            Assert.That(other.IsBuildingDerived, Is.False);
            Assert.That(other.SplitDepth, Is.Zero);
            Assert.That(other.ChildOfSplit(), Is.EqualTo(BuildingLineage.NotBuilding), "a non-building stays false and 0");
            Assert.That(default(BuildingLineage), Is.EqualTo(BuildingLineage.NotBuilding), "and that is the default");
        }

        [Test]
        public void TheDepth_SaturatesAtTheIntegerLimit()
        {
            Assert.That(BuildingLineage.BuildingAt(int.MaxValue - 1).ChildOfSplit().SplitDepth, Is.EqualTo(int.MaxValue));
            Assert.That(BuildingLineage.BuildingAt(int.MaxValue).ChildOfSplit().SplitDepth, Is.EqualTo(int.MaxValue),
                "the limit is not passed, and does not wrap below 0");
        }

        [Test]
        public void TheLimits_ShrinkByTheRatio_AndStayFiniteAndNotNegativeToTheIntegerLimit()
        {
            var settings = new BuildingWorldD6Settings(0.25f, 15f, 0.5f);
            Assert.That(settings.IsValid, Is.True);
            Assert.That(settings.LimitMetres(1), Is.EqualTo(0.25f), "L(1) = L1");
            Assert.That(settings.AngleDegrees(1), Is.EqualTo(15f), "A(1) = A1");
            Assert.That(settings.LimitMetres(2), Is.EqualTo(0.125f).Within(1e-7f), "L(2) = L1 r");
            Assert.That(settings.AngleDegrees(3), Is.EqualTo(3.75f).Within(1e-6f), "A(3) = A1 r^2");

            foreach (int depth in new[] { 1, 2, 10, 100, 149, 150, 1000, 100000, int.MaxValue - 1, int.MaxValue })
            {
                float limit = settings.LimitMetres(depth);
                float angle = settings.AngleDegrees(depth);
                Assert.That(float.IsFinite(limit) && limit >= 0f, Is.True, "distance at depth " + depth + ": " + limit);
                Assert.That(float.IsFinite(angle) && angle >= 0f, Is.True, "angle at depth " + depth + ": " + angle);
            }

            Assert.That(settings.LimitMetres(int.MaxValue), Is.Zero, "an underflow is 0, not a denormal or a negative");
            Assert.That(settings.AngleDegrees(1000), Is.Zero);

            // A ratio just under 1 keeps the first values for a long way, and still ends finite.
            var slow = new BuildingWorldD6Settings(1f, 180f, 0.9999999f);
            Assert.That(slow.IsValid, Is.True);
            Assert.That(float.IsFinite(slow.AngleDegrees(int.MaxValue)) && slow.AngleDegrees(int.MaxValue) >= 0f, Is.True);
        }

        [Test]
        public void OnlyUsableSettings_AreValid_AndNoLimitIsMadeForDepthZero()
        {
            Assert.That(new BuildingWorldD6Settings(0f, 0f, 0.5f).IsValid, Is.True, "zero limits are allowed; no minimum");
            Assert.That(new BuildingWorldD6Settings(-0.1f, 15f, 0.5f).IsValid, Is.False);
            Assert.That(new BuildingWorldD6Settings(0.25f, 181f, 0.5f).IsValid, Is.False, "an angle a joint cannot take");
            Assert.That(new BuildingWorldD6Settings(0.25f, 15f, 0f).IsValid, Is.False);
            Assert.That(new BuildingWorldD6Settings(0.25f, 15f, 1f).IsValid, Is.False);
            Assert.That(new BuildingWorldD6Settings(float.NaN, 15f, 0.5f).IsValid, Is.False);
            Assert.That(new BuildingWorldD6Settings(0.25f, float.PositiveInfinity, 0.5f).IsValid, Is.False);
            Assert.That(default(BuildingWorldD6Settings).IsValid, Is.False, "a settings value never given is not usable");

            Assert.Throws<System.ArgumentOutOfRangeException>(() => BuildingWorldD6Settings.Provisional.LimitMetres(0));
        }

        [Test]
        public void OnlyTheAnchorlessSidesOfABuildingChild_NeedAConstraint()
        {
            BuildingLineage child = BuildingLineage.RegisteredBuilding.ChildOfSplit();
            Assert.That(BuildingWorldD6.Needed(in child, false, false), Is.EqualTo(2));
            Assert.That(BuildingWorldD6.Needed(in child, true, false), Is.EqualTo(1));
            Assert.That(BuildingWorldD6.Needed(in child, true, true), Is.Zero);
            BuildingLineage other = BuildingLineage.NotBuilding;
            Assert.That(BuildingWorldD6.Needed(in other, false, false), Is.Zero);
        }
    }
}
