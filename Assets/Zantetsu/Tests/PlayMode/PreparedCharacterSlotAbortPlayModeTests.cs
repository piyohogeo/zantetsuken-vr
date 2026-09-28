using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        // A synthetic needle hull (no licensed data): a 0.25 m box whose top-front edge P0-P1 carries a vertex M at 2/3
        // along it, 8.25e-7 m inside the box (along the inward diagonal of the two faces). The top face is three triangles
        // about M, and the needle (P0, P1, M) closes the gap. A plane containing P0-P1 at 60 degrees from the top makes the
        // owner cut kernel fail its walk (CutFailed / WalkFailed), as a hull with such a face does.
        private static (double3[] vertices, int[][] faces) NeedleHull()
        {
            const double s = 0.25;
            var v = new List<double3>
            {
                new double3(0, 0, s), new double3(s, 0, s), new double3(s, s, s), new double3(0, s, s),
                new double3(0, 0, 0), new double3(s, 0, 0), new double3(s, s, 0), new double3(0, s, 0),
            };
            v.Add(v[0] + (v[1] - v[0]) * (2.0 / 3.0) + math.normalize(new double3(0, 1, -1)) * 8.25e-7);
            const int P0 = 0, P1 = 1, P2 = 2, P3 = 3, Q0 = 4, Q1 = 5, Q2 = 6, Q3 = 7, M = 8;
            var faces = new List<int[]>
            {
                new[] { P3, P0, M }, new[] { P3, M, P2 }, new[] { M, P1, P2 }, new[] { P0, P1, M },
                new[] { P0, Q0, Q1 }, new[] { P0, Q1, P1 }, new[] { Q0, Q3, Q2 }, new[] { Q0, Q2, Q1 },
                new[] { P1, Q1, Q2 }, new[] { P1, Q2, P2 }, new[] { P2, Q2, Q3 }, new[] { P2, Q3, P3 },
                new[] { P3, Q3, Q0 }, new[] { P3, Q0, P0 },
            };
            double3 c = v.Aggregate((a, b) => a + b) / v.Count;
            for (int f = 0; f < faces.Count; f++)
            {
                if (f == 3) continue;
                int[] q = faces[f];
                if (math.dot(math.cross(v[q[1]] - v[q[0]], v[q[2]] - v[q[0]]), v[q[0]] - c) < 0) faces[f] = new[] { q[0], q[2], q[1] };
            }

            bool directed(int a, int b) => faces.Where((f, i) => i != 3).Any(f => Enumerable.Range(0, f.Length).Any(k => f[k] == a && f[(k + 1) % f.Length] == b));
            if (directed(P0, P1)) faces[3] = new[] { P1, P0, M };
            return (v.ToArray(), faces.ToArray());
        }

        // A crowd slot whose hull and display mesh are that needle hull (renderer bind = bone frame, metres).
        private SandboxNpcCharacter NeedleSlot(Vector3 at, out GameObject setup)
        {
            var (points, faces) = NeedleHull();
            string intakeJson = "{\"assets\":[{\"family\":\"needle\",\"objectName\":\"needle mesh\",\"topologyMap\":[" + string.Join(",", Enumerable.Range(0, points.Length))
                                + "],\"topologyCount\":" + points.Length + "}]}";
            var offsets = new List<int> { 0 };
            foreach (int[] f in faces) offsets.Add(offsets[offsets.Count - 1] + f.Length);
            string hullJson = "{\"hulls\":[{\"boneName\":\"Lent character bone\",\"rendererBindVertices\":[" + string.Join(",", points.Select(p => p.x.ToString("R") + "," + p.y.ToString("R") + "," + p.z.ToString("R")))
                              + "],\"faceOffsets\":[" + string.Join(",", offsets) + "],\"faceIndices\":[" + string.Join(",", faces.SelectMany(f => f)) + "]}]}";
            SkinnedMeshRenderer r = LentBonedRig(out Transform _, out GameObject root, out Rigidbody motion);
            Mesh m = r.sharedMesh;
            m.Clear();
            double3 centre = points.Aggregate((x, y) => x + y) / points.Length;
            m.vertices = points.Select(p => new Vector3((float)p.x, (float)p.y, (float)p.z)).ToArray();
            m.normals = points.Select(p => ((Vector3)(float3)(p - centre)).normalized).ToArray();
            m.uv = Enumerable.Repeat(new Vector2(.5f, .5f), points.Length).ToArray();
            m.boneWeights = Enumerable.Repeat(new BoneWeight { weight0 = 1 }, points.Length).ToArray();
            m.bindposes = new[] { Matrix4x4.identity };
            m.triangles = faces.SelectMany(f => f).ToArray();
            m.name = "needle mesh";
            root.transform.position = at;
            var table = ColdTrack(new TextAsset(PoseTablePlayerPlayModeTests.Table("Lent character bone")));
            root.AddComponent<PoseTablePlayer>().Configure(table, root.transform, 0.0, true);
            setup = new GameObject("needle slot");
            setup.SetActive(false);
            var character = setup.AddComponent<SandboxNpcCharacter>();
            void Set(string field, object value) => typeof(SandboxNpcCharacter).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(character, value);
            Set("world", coldWorld);
            Set("characterRoot", root);
            Set("motionBody", motion);
            Set("intake", ColdTrack(new TextAsset(intakeJson) { name = "needle intake" }));
            Set("hulls", ColdTrack(new TextAsset(hullJson) { name = "needle hulls" }));
            Set("family", "needle");
            character.PrepareAsSlot(new SandboxNpcCharacter.SlotShare());
            setup.SetActive(true);
            return character;
        }

        // An individual activated on the slot, held still at its root (the pose table would move the bone).
        private static void ActivateStill(SandboxNpcCharacter slot)
        {
            Assert.That(slot.Activate(), Is.True);
            slot.CharacterRoot.GetComponent<PoseTablePlayer>().enabled = false;
            slot.CharacterRoot.transform.Find("Lent character bone").localPosition = Vector3.zero;
        }

        private static SlashSweep PlaneSweep(long id, double3 normal, double3 through, double3 along)
        {
            double3 travel = math.normalize(math.cross(normal, along));
            Vector3 V(double3 x) => (Vector3)(float3)x;
            return new SlashSweep(id, 0.0, false, new Plane(V(normal), V(through)), V(travel), V(along),
                V(through - .4 * along - .4 * travel), V(through + .4 * along - .4 * travel), V(through - .4 * along + .4 * travel), V(through + .4 * along + .4 * travel));
        }

        /// <summary>
        /// **A slot whose cut failed comes back.** A crowd slot (PrepareAsSlot) whose hull has a needle face is hit by a
        /// plane containing the needle's long edge: the hit is accepted and published, the owner cut kernel fails its
        /// walk, and the cut is aborted (the ledger ends it, its geometry is reclaimed). The failure is counted once, by
        /// its clip status, not again on later frames. The slot is not ready to return while the driver still holds the
        /// cut; once the abort has settled it is, and it is prepared again, comes back as a new individual -- drawn and a
        /// hit target -- and an ordinary Slash cuts it through to commit.
        /// </summary>
        [UnityTest]
        public IEnumerator ASlotWhoseCutFailed_ComesBackAfterTheAbortSettles_AndItsNextIndividualIsCut()
        {
            ColdWorld();
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            SandboxNpcCharacter slot = NeedleSlot(Vector3.zero, out GameObject setup);
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            ActivateStill(slot);
            yield return null;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(slot.Handle);
            var tally = new MobPlanCutFailureTally();
            tally.Attach(coldWorld.Driver);

            var (points, _) = NeedleHull();
            double3 edge = math.normalize(points[1] - points[0]);
            double t = 60 * math.PI / 180;
            double3 n = math.cos(t) * new double3(0, 1, 0) + math.sin(t) * math.cross(edge, new double3(0, 1, 0));
            List<SlashHitConfirmed> hits = Evaluate(detector, PlaneSweep(1, n, points[0], edge), 1);
            Assert.That(hits.Count, Is.EqualTo(1));
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            CutOperationId failed = hits[0].Operation;
            Assert.That(slot.IsReturnReady, Is.False, "not while the cut is held");

            LogicalCutOperation op = default;
            for (int i = 0; i < 60 && !(coldWorld.Ledger.TryGetOperation(failed, out op) && op.state == LogicalCutOperationState.Aborted); i++) yield return null;
            Assert.That(op.state, Is.EqualTo(LogicalCutOperationState.Aborted), "the failed cut was aborted");
            Assert.That(coldWorld.Geometry.StageOf(failed), Is.EqualTo(CutGeometryStage.Reclaimed));
            Assert.That(coldWorld.Driver.KernelFailedCount, Is.EqualTo(1));
            Assert.That(coldWorld.Driver.KernelFailuresWithCutStatus((int)CutStatus.WalkFailed), Is.EqualTo(1));
            Assert.That(coldWorld.Driver.KeptFailureCount, Is.EqualTo(1));
            Assert.That(coldWorld.Driver.KeptFailure(0).operation, Is.EqualTo(failed));
            for (int i = 0; i < 60 && !slot.IsReturnReady; i++) yield return null;
            Assert.That(coldWorld.Driver.IsSettled(failed), Is.True);
            Assert.That(slot.IsReturnReady, Is.True, "the settled abort lets the slot go back");
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(coldWorld.Driver.KernelFailedCount, Is.EqualTo(1), "counted once, not every frame");
            Assert.That(coldWorld.Driver.FailedCutCount, Is.EqualTo(1));

            Assert.That(slot.TryReprepare(), Is.True, slot.Failure);
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            ActivateStill(slot);
            Assert.That(slot.Activations, Is.EqualTo(2), "a new individual on the same slot");
            Assert.That(slot.CharacterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(r => r.enabled && r.gameObject.activeInHierarchy), Is.True, "drawn again");
            yield return null;
            detector.AddCharacter(slot.Handle);
            List<SlashHitConfirmed> next = Evaluate(detector, PlaneSweep(2, new double3(0, 1, 0), new double3(0.125, 0.125, 0.125), new double3(1, 0, 0)), 2);
            Assert.That(next.Count, Is.EqualTo(1), "the new individual is hit");
            Assert.That(next[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(next[0].Operation, Is.Not.EqualTo(failed));
            yield return UntilCommitted(next[0].Operation);
            Assert.That(coldWorld.Driver.KernelFailedCount, Is.EqualTo(1), "the ordinary cut is no failure");
            for (int i = 0; i < 60 && !slot.IsReturnReady; i++) yield return null;
            Assert.That(slot.IsReturnReady, Is.True, "and after its commit the slot can go back again");

            // The check's reading: the one failure is the failed hit's operation, reported once; the ordinary cut is none.
            Assert.That(tally.Reported, Is.EqualTo(1), "reported once");
            Assert.That(tally.TryGet(failed, out ProvisionalCutDriver.CutFailure f), Is.True, "the failure is the failed hit's operation");
            Assert.That(f.outcome, Is.EqualTo(PhysicsCutOutcomeKind.KernelFailed));
            Assert.That((CutStatus)f.cutStatus, Is.EqualTo(CutStatus.WalkFailed));
            Assert.That(tally.TryGet(next[0].Operation, out _), Is.False, "the ordinary cut is no failure");
            Assert.That(tally.FailedBefore(failed, Time.frameCount), Is.True);
            Assert.That(tally.FailedBefore(next[0].Operation, Time.frameCount), Is.False);
            var byModel = tally.Tally(new[] { (failed, false, "needle"), (next[0].Operation, false, "needle"), (failed, false, "needle") }, out int unattributed);
            Assert.That(byModel["needle"].rootAccepted, Is.EqualTo(2), "each accepted cut once");
            Assert.That(byModel["needle"].rootFailed, Is.EqualTo(1));
            Assert.That(byModel["needle"].childAccepted + byModel["needle"].childFailed, Is.Zero);
            Assert.That(byModel["needle"].kinds, Is.EquivalentTo(new Dictionary<string, int> { { "KernelFailed/WalkFailed", 1 } }));
            Assert.That(unattributed, Is.Zero);
            var asChild = tally.Tally(new[] { (failed, true, "needle") }, out _);
            Assert.That(asChild["needle"].childFailed == 1 && asChild["needle"].rootFailed == 0, Is.True, "a child cut's failure is counted as the child's");
            Assert.That(tally.Tally(new (CutOperationId, bool, string)[0], out int none).Count == 0 && none == 1, Is.True, "a failure on no accepted cut is unattributed");
            tally.Detach();

            // The world's own line at its end, with no check: the totals, the clip status and the kept failure.
            string summary = coldWorld.Driver.FailureSummary();
            Assert.That(summary, Does.StartWith("failed cuts 1, KernelFailed 1 [WalkFailed 1], kept 1; operation " + failed.value + " "));
            Object.Destroy(setup);
            yield return null;
            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("^CUT WORLD .* at its end: failed cuts 1, KernelFailed 1 \\[WalkFailed 1\\], kept 1; operation " + failed.value + " "));
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True);
        }
    }
}
