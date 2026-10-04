using NUnit.Framework;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The city walk (TL, 2026-10-03): a placed instance at a uniform scale registered from its input scaled by it (the
    /// drawn positions, the convex and the anchors; nothing else), every stage's deadline kept within the run's budget
    /// only in a city walk, and the hull trial's end judged a building at a time.
    /// </summary>
    public class CityWalkCheckTests
    {
        private static PlacedCuttableInput Input()
        {
            return new PlacedCuttableInput
            {
                schemaVersion = 1, topologyCount = 3, name = "probe", source = "src", sha256 = "abc", isBuilding = false, isCuttable = true,
                render = new[]
                {
                    new PlacedCuttableInput.Vertex { position = new Vector3(1f, 0f, 0f), normal = Vector3.up, uv = new Vector2(0.1f, 0.2f) },
                    new PlacedCuttableInput.Vertex { position = new Vector3(0f, 2f, 0f), normal = Vector3.right, uv = new Vector2(0.3f, 0.4f) },
                    new PlacedCuttableInput.Vertex { position = new Vector3(0f, 0f, -3f), normal = Vector3.forward, uv = new Vector2(0.5f, 0.6f) },
                },
                indices = new uint[] { 0, 1, 2 },
                topology = new[] { 0, 1, 2 },
                hulls = new[] { new PlacedCuttableInput.Hull { vertices = new[] { new Vector3(1f, 1f, 1f), new Vector3(-1f, 0f, 2f) }, faceOffsets = new[] { 0, 3 }, faceIndices = new[] { 0, 1, 0 } } },
                anchors = new[] { new Vector3(0.5f, 0f, -0.5f) },
            };
        }

        [Test]
        public void Scaled_ScalesThePositionsTheConvexAndTheAnchors_AndKeepsTheRest()
        {
            PlacedCuttableInput data = Input();
            PlacedCuttableInput scaled = PlayableCityCuttable.Scaled(data, 1.6f);
            for (int i = 0; i < data.render.Length; i++)
            {
                Assert.That(scaled.render[i].position, Is.EqualTo(data.render[i].position * 1.6f), "position " + i);
                Assert.That(scaled.render[i].normal, Is.EqualTo(data.render[i].normal), "normal " + i + " (a uniform scale keeps it)");
                Assert.That(scaled.render[i].uv, Is.EqualTo(data.render[i].uv), "uv " + i);
            }

            Assert.That(scaled.hulls[0].vertices, Is.EqualTo(new[] { new Vector3(1.6f, 1.6f, 1.6f), new Vector3(-1.6f, 0f, 3.2f) }));
            Assert.That(scaled.hulls[0].faceOffsets, Is.EqualTo(data.hulls[0].faceOffsets));
            Assert.That(scaled.hulls[0].faceIndices, Is.EqualTo(data.hulls[0].faceIndices));
            Assert.That(scaled.anchors, Is.EqualTo(new[] { new Vector3(0.8f, 0f, -0.8f) }));
            Assert.That(scaled.indices, Is.EqualTo(data.indices));
            Assert.That(scaled.topology, Is.EqualTo(data.topology));
            Assert.That((scaled.topologyCount, scaled.name, scaled.source, scaled.sha256, scaled.isBuilding, scaled.isCuttable),
                Is.EqualTo((data.topologyCount, data.name, data.source, data.sha256, data.isBuilding, data.isCuttable)));
            // The input itself is left as it was (it is shared by every instance of its asset).
            Assert.That(data.render[1].position, Is.EqualTo(new Vector3(0f, 2f, 0f)));
            Assert.That(data.hulls[0].vertices[0], Is.EqualTo(new Vector3(1f, 1f, 1f)));
            Assert.That(data.anchors[0], Is.EqualTo(new Vector3(0.5f, 0f, -0.5f)));
        }

        [Test]
        public void StageDeadline_InACityWalk_NeverPastTheBudget_OtherwiseItsOwnLength()
        {
            Assert.That(SandboxPropSlashPlayerCheck.CityWalkStageDeadline(100f, 60f, true, 565f), Is.EqualTo(160f), "its own length within the budget");
            Assert.That(SandboxPropSlashPlayerCheck.CityWalkStageDeadline(530f, 60f, true, 565f), Is.EqualTo(565f), "cut at the budget");
            Assert.That(SandboxPropSlashPlayerCheck.CityWalkStageDeadline(530f, 60f, false, 565f), Is.EqualTo(590f), "outside a city walk the budget does not apply");
        }

        [Test]
        public void BuildingsPastOneEach_JudgesEachBuildingApart()
        {
            // One building: one fixed and one free group is within the rule; a second fixed one is past it.
            Assert.That(SandboxPropSlashPlayerCheck.BuildingsPastOneEach(new[] { (1, true), (1, false) }), Is.Zero);
            Assert.That(SandboxPropSlashPlayerCheck.BuildingsPastOneEach(new[] { (1, true), (1, true) }), Is.EqualTo(1));
            // Many buildings, each one fixed group (and one with a free one too): none past the rule, though the world holds
            // many fixed groups (the single-building judgement read them as one building's).
            Assert.That(SandboxPropSlashPlayerCheck.BuildingsPastOneEach(new[] { (1, true), (2, true), (3, true), (3, false) }), Is.Zero);
            Assert.That(SandboxPropSlashPlayerCheck.BuildingsPastOneEach(new[] { (1, true), (2, false), (2, false), (3, true), (3, true) }), Is.EqualTo(2));
        }

        [Test]
        public void TheSyntheticDropStop_IsRequiredOnlyWhereItsSectionRan_JudgedWhereARunsOwnReCutStopped_ElseNotApplicable()
        {
            Assert.That(CheckJudgement.HullDropStop(true, 0), Is.EqualTo(CheckJudgement.Kind.Required), "the section ran: a stop is required");
            Assert.That(CheckJudgement.HullDropStop(true, 2), Is.EqualTo(CheckJudgement.Kind.Required));
            Assert.That(CheckJudgement.HullDropStop(false, 1), Is.EqualTo(CheckJudgement.Kind.JudgedWhereItArose), "a city walk's own re-cut stopped a drop: judged");
            Assert.That(CheckJudgement.HullDropStop(false, 0), Is.EqualTo(CheckJudgement.Kind.NotApplicable), "neither: not applicable, not a pass");
        }

        [Test]
        public void ASlotReuse_IsRequiredOfTheMobPlanScenarios_CountedInACityWalk_ElseNotExercised()
        {
            Assert.That(CheckJudgement.SlotReuse(false, 0), Is.EqualTo(CheckJudgement.Kind.Required), "the MobPlan scenario's condition unchanged");
            Assert.That(CheckJudgement.SlotReuse(false, 5), Is.EqualTo(CheckJudgement.Kind.Required));
            Assert.That(CheckJudgement.SlotReuse(true, 3), Is.EqualTo(CheckJudgement.Kind.Counted));
            Assert.That(CheckJudgement.SlotReuse(true, 0), Is.EqualTo(CheckJudgement.Kind.NotExercised), "a city walk with none: not exercised, not a pass");
        }
    }
}
