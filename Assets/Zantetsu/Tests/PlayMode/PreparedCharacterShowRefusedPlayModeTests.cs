using System.Collections;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        /// <summary>
        /// **A root whose show is refused after its append gives its room back (DESIGN 4.5.3).** Two characters of one
        /// model are prepared. A's display input is appended and its show refused: the request fails, the appended range
        /// is retired by the request that still owns it, and the lineage's vertex room goes back through the ordinary
        /// reclamation, leaving no range or group behind. B's cut then takes exactly that room, and the high-water does
        /// not move.
        /// </summary>
        [UnityTest] public IEnumerator D6T_ARootShowRefusedAfterItsAppend_GivesItsRoomBack_ForTheNextAppend()
        {
            ColdWorld();
            coldWarm=new VpPhysicsColdPreparation();
            var a=PrepareCharacter(Vector3.zero,out _);var b=PrepareCharacter(new Vector3(3,0,0),out _);
            yield return null;
            Assert.That(a.TryFinishPreparation()&&b.TryFinishPreparation(),Is.True);
            VpCpuGeometryStorage storage=coldWorld.Storage;
            int highWater=storage.VertexCount,groups=storage.VertexGroupCount,released=storage.VertexGroupsReleased,free=storage.FreeVertexRoom;

            coldWorld.Display.RefusePreparedRootShowForTest=()=>true;
            var refused=a.TryCut(new float4(1,0,0,-.25f),float3.zero);
            coldWorld.Display.RefusePreparedRootShowForTest=null;
            Assert.That(refused.Outcome,Is.EqualTo(VpCharacterCutOutcome.Failed));
            Assert.That(a.LastFailure,Does.Contain("show prepared root"));
            int appended=storage.VertexCount-highWater;
            Assert.That(appended,Is.GreaterThan(0),"the layout: the root was appended before its show was refused");
            Assert.That(storage.FreeVertexRoom,Is.EqualTo(free-appended),"the layout: the append holds its room until reclaimed");

            for(int i=0;i<3;i++)yield return null;
            Assert.That(storage.VertexGroupsReleased,Is.EqualTo(released+1),"the refused root's room went back, once");
            Assert.That(storage.VertexGroupCount,Is.EqualTo(groups),"no group is left behind");
            Assert.That(storage.FreeVertexRoom,Is.EqualTo(free),"all of it");
            Assert.That(coldWorld.TerminationRequested,Is.False);

            var result=b.TryCut(new float4(1,0,0,-.25f),float3.zero);
            Assert.That(result.Outcome,Is.EqualTo(VpCharacterCutOutcome.Requested));
            var bCut=Transaction(result.Operation);
            Assert.That(coldWorld.Geometry.TryGetGeometry(bCut.Source,out var bBase),Is.True);
            Assert.That(bBase.vertexStart,Is.EqualTo(highWater),"the next append takes the room given back");
            Assert.That(bBase.vertexCount,Is.EqualTo(appended),"the layout: the same model, as many vertices");
            Assert.That(storage.VertexCount,Is.EqualTo(highWater+appended),"and the high-water does not move");
        }
    }
}
