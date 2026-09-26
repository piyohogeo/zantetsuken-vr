using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// What a cut world takes in at registration (DESIGN 7.2.2): a building is registered as one, explicitly, and a body
    /// with a joint of its own is not a cut target -- refused once, there, and not looked for afterwards.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator ABuildingIsRegisteredAsOne_AndABodyWithItsOwnJointIsNotRegisteredAtAll()
        {
            CutWorldRoot root = NewWorld(out Shader _);

            LogicalFragmentId building = AddBody(root, Vector3.zero, building: true);
            Assert.That(root.Owners.TryGet(building, out PhysicsFragmentOwner owner), Is.True);
            Assert.That(owner.Building, Is.EqualTo(BuildingLineage.RegisteredBuilding), "true, depth 0");
            LogicalFragmentId other = AddBody(root, new Vector3(4f, 0f, 0f));
            Assert.That(root.Owners.TryGet(other, out PhysicsFragmentOwner otherOwner), Is.True);
            Assert.That(otherOwner.Building, Is.EqualTo(BuildingLineage.NotBuilding), "false, 0 unless said otherwise");

            // A body carrying a joint of its own.
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            GameObject actor = TrackActor(new GameObject("Jointed Body"));
            actor.transform.position = new Vector3(-4f, 0f, 0f);
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);
            actor.AddComponent<HingeJoint>();
            int fragmentsBefore = root.Ledger.FragmentCount;

            LogAssert.Expect(LogType.Error, new Regex("a body with a joint is not a cut target"));
            Assert.That(
                root.TryAddBody(actor, shape, default(VpStoredGeometry), Matrix4x4.identity, Matrix4x4.identity, null, true,
                    out LogicalFragmentId refused),
                Is.False);
            Assert.That(refused.IsSet, Is.False);
            Assert.That(root.Ledger.FragmentCount, Is.EqualTo(fragmentsBefore), "no fragment was made for it");
            Assert.That(root.Owners.Count, Is.EqualTo(2), "and no owner");

            yield return null;
            yield return EndWorld(root);
        }
    }
}
