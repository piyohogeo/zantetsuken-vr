using System.Collections;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// An owner's Root's Transform held from the Root's giving (TL, 2026-10-01), for the owners the world makes itself: an
    /// authored body (the ordinary constructor), the two sides its cut publishes (the same), and the body-less members of a
    /// fused building group (the fused constructor). Each holds it, reads exactly as asking the Root for it at every read,
    /// moved or not, and lets it go with its Root when released at the world's end.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static void ReadsAsBefore(PhysicsFragmentOwner owner, string what)
        {
            Assert.That(owner.HoldsRootTransformForTest, Is.True, what + ": the Root's Transform held");
            PhysicsFragmentOwner.rootTransformReadAgainForTest = true;
            bool oldOk = owner.TryReadGeometryLocalToWorld(out Matrix4x4 oldM);
            PhysicsFragmentOwner.rootTransformReadAgainForTest = false;
            bool newOk = owner.TryReadGeometryLocalToWorld(out Matrix4x4 newM);
            Assert.That(newOk, Is.EqualTo(oldOk), what + ": the same answer");
            for (int i = 0; i < 16; i++)
                Assert.That(System.BitConverter.SingleToInt32Bits(newM[i]), Is.EqualTo(System.BitConverter.SingleToInt32Bits(oldM[i])), what + ": element " + i);
        }

        [UnityTest]
        public IEnumerator RootTransformHold_AnAuthoredBodyAndItsCutSides_ReadAsBefore_AndLetItGoAtTheEnd()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId body = AddBody(root, new Vector3(1f, 0f, 2f));
            yield return null;
            Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner owner), Is.True);
            ReadsAsBefore(owner, "the authored body");
            owner.Root.transform.SetPositionAndRotation(new Vector3(-2f, 0.5f, 3f), Quaternion.Euler(20f, 40f, 0f));
            ReadsAsBefore(owner, "the authored body, moved and turned");

            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), sides);
            Assert.That(root.Owners.TryGet(sides[0], out PhysicsFragmentOwner positive) && root.Owners.TryGet(sides[1], out PhysicsFragmentOwner negative), Is.True, "both sides have owners");
            root.Owners.TryGet(sides[0], out positive);
            root.Owners.TryGet(sides[1], out negative);
            ReadsAsBefore(positive, "the positive side");
            ReadsAsBefore(negative, "the negative side");
            positive.Root.transform.position += new Vector3(0f, 1f, 0f);
            ReadsAsBefore(positive, "the positive side, moved");

            yield return EndWorld(root);
            Assert.That(positive.IsReleased && negative.IsReleased, Is.True, "released at the world's end");
            Assert.That(positive.HoldsRootTransformForTest || negative.HoldsRootTransformForTest, Is.False, "the Root's Transform let go with the Root");
            Assert.That(positive.TryReadGeometryLocalToWorld(out _), Is.False, "nothing read after the release");
        }

        [UnityTest]
        public IEnumerator RootTransformHold_FusedMembers_ReadAsBefore()
        {
            CutWorldRoot root = NewFusionWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            yield return UntilFused(root, 2, "the halves fused");
            int fused = 0;
            foreach (LogicalFragmentId half in halves)
            {
                Assert.That(root.Owners.TryGet(half, out PhysicsFragmentOwner member), Is.True);
                if (!member.IsFused) continue;
                fused++;
                ReadsAsBefore(member, "a fused member");
                member.Root.transform.localPosition += new Vector3(0.1f, 0f, 0f);
                ReadsAsBefore(member, "a fused member, moved");
            }

            Assert.That(fused, Is.EqualTo(2), "both halves are fused members (the fused constructor)");
            yield return EndWorld(root);
        }
    }
}
