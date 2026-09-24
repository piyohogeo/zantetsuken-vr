using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    public unsafe partial class CutGeometryConnectionTests
    {
        // Same product driver, display commit and guarded teardown as the synthetic fixture.
        // Only input preparation and the assertions below are character-specific.
        sealed class CharacterCompoundData
        {
            public ConvexPoly[] polys;
            public VpRenderVertex[] vertices;
            public uint[] indices;
            public int[] topology;
            public int topologyCount;
            public Matrix4x4 geometryToOwner;
            public Vector3 position;
            public Quaternion rotation;
        }

        [TestCase("character-casual", 0)] [TestCase("character-casual", 1)]
        public void RepairedCharacter_Compound19_CommonPlane_CommitsAndRecuts(string family, int pose)
            => CharacterCompoundCommit(family,pose,false);

        [TestCase("character-casual",0)] [TestCase("character-casual",1)] [TestCase("character-casual",4)]
        [TestCase("character-professional",0)] [TestCase("character-professional",1)] [TestCase("character-professional",4)]
        public void FixedScale_Compound19_CommonPlane_CommitsAndRecuts(string family,int pose)
            => CharacterCompoundCommit(family,pose,true);

        void CharacterCompoundCommit(string family,int pose,bool normalizeFixedScale)
        {
            CharacterCompoundData data = LoadCharacterCompound(family, pose,normalizeFixedScale);
            using (World w = NewWorld(character: data))
            {
                Assert.That(w.shape.ConvexCount, Is.EqualTo(19));
                Assert.That(w.root.GetComponents<MeshCollider>().Length, Is.EqualTo(19));
                Assert.That(VpRenderVertex.Stride, Is.EqualTo(16));
                ulong originalInput = w.harness.InputHash();
                LogicalFragmentId source = w.source;
                for (int stage = 0; stage < 2; stage++)
                {
                    Assert.That(w.registry.TryGet(source, out PhysicsFragmentOwner parent), Is.True);
                    var before = CharacterPolys(parent.Shape);
                    double beforeVolume = before.Sum(CharacterVolume);
                    int axis = stage == 0 ? 1 : 0;
                    double lo = before.Min(p => p.V.Min(v => v[axis]));
                    double hi = before.Max(p => p.V.Max(v => v[axis]));
                    float3 normal = default; normal[axis] = 1;
                    var plane = new float4(normal, (float)-(lo + .5685 * (hi-lo)));
                    ConvexSide[] expectedSides = before.Select(p => CharacterSide(p, plane)).ToArray();
                    int splits = expectedSides.Count(s => s == ConvexSide.Split);
                    Assert.That(splits, Is.GreaterThan(0), "must cut actual proxy geometry");
                    int expectedPositive = expectedSides.Count(s => s != ConvexSide.Negative);
                    int expectedNegative = expectedSides.Count(s => s == ConvexSide.Negative || s == ConvexSide.Split);
                    int inheritedPositive = expectedSides.Count(s => s == ConvexSide.Positive);
                    int inheritedNegative = expectedSides.Count(s => s == ConvexSide.Negative);
                    bool geometryFirst = (pose + stage) % 2 != 0;
                    w.physicsJob.HoldEverything = geometryFirst;
                    w.geometryPool.HoldEverything = !geometryFirst;
                    ProvisionalCutTransaction transaction = RequestPublished(w, source, plane);
                    CollectionAssert.AreEqual(expectedSides, transaction.Classification.Sides, "all input hulls classified");
                    Assert.That(transaction.Classification.Input.convexCount, Is.EqualTo(before.Length));
                    float4 kernelPlane;
                    if (geometryFirst)
                    {
                        RunUntil(w, () => w.dag.StageOf(transaction.Operation) == CutGeometryStage.CpuPublished, "character geometry first");
                        Assert.That(w.commit.Commits, Is.EqualTo(stage), "geometry must wait for child publication");
                        Assert.That(transaction.Phase, Is.Not.EqualTo(ProvisionalCutPhase.HandedOff));
                        Assert.That(w.dag.TryGetKernelPlane(transaction.Operation, out kernelPlane), Is.True, "read plane while work is retained");
                        w.physicsJob.ReleaseEverything();
                    }
                    else
                    {
                        RunUntil(w, () => transaction.Phase == ProvisionalCutPhase.HandedOff, "character physics first");
                        Assert.That(w.commit.Commits, Is.EqualTo(stage), "physics must not wait for geometry");
                        Assert.That(w.dag.StageOf(transaction.Operation), Is.Not.EqualTo(CutGeometryStage.Committed));
                        Assert.That(w.dag.TryGetKernelPlane(transaction.Operation, out kernelPlane), Is.True, "read plane before committed work is released");
                        w.geometryPool.ReleaseEverything();
                    }
                    RunUntil(w, () => transaction.Phase == ProvisionalCutPhase.HandedOff, "character final handoff");
                    RunUntil(w, () => w.dag.StageOf(transaction.Operation) == CutGeometryStage.Committed, "character display commit");
                    Assert.That(w.commit.Commits, Is.EqualTo(stage + 1));
                    Assert.That(w.fault.faults, Is.Empty);
                    Assert.That(w.ledger.Budget.IncompleteCutOperationCount, Is.Zero);
                    Assert.That(w.registry.TryGet(source, out _), Is.False, "source owner retired");
                    Assert.That(w.dag.TryGetGeometry(source, out _), Is.False, "source display replaced");
                    var published = OperationOf(w, transaction.Operation);
                    Assert.That(w.registry.TryGet(published.positive, out PhysicsFragmentOwner positive), Is.True);
                    Assert.That(w.registry.TryGet(published.negative, out PhysicsFragmentOwner negative), Is.True);
                    Assert.That(positive.Shape.ConvexCount, Is.EqualTo(expectedPositive));
                    Assert.That(negative.Shape.ConvexCount, Is.EqualTo(expectedNegative));
                    Assert.That(positive.Shape.ConvexCount + negative.Shape.ConvexCount, Is.EqualTo(before.Length + splits));
                    double afterVolume = CharacterPolys(positive.Shape).Sum(CharacterVolume) + CharacterPolys(negative.Shape).Sum(CharacterVolume);
                    Assert.That(afterVolume, Is.EqualTo(beforeVolume).Within(beforeVolume * 3e-5), "compound volume sum (not union volume)");
                    Assert.That(positive.Mass + negative.Mass, Is.EqualTo((float)transaction.ParentMass).Within(1e-4f));
                    // Independent plane pullback: x_owner = G * x_renderer -> p_renderer = transpose(G) * p_owner.
                    Vector4 pulled = data.geometryToOwner.transpose * new Vector4(plane.x,plane.y,plane.z,plane.w);
                    pulled /= new Vector3(pulled.x,pulled.y,pulled.z).magnitude;
                    Assert.That(math.distance(kernelPlane, new float4(pulled.x,pulled.y,pulled.z,pulled.w)), Is.LessThan(2e-4f));
                    Advance(w);
                    CheckCharacterChild(w, published.positive, positive, data.geometryToOwner, plane, true);
                    CheckCharacterChild(w, published.negative, negative, data.geometryToOwner, plane, false);
                    Assert.That(w.harness.InputHash(), Is.EqualTo(originalInput));
                    Assert.That(w.harness.CheckGuards(), Is.Empty);
                    for (int i = 0; i < data.vertices.Length; i++) Assert.That(w.storage.Vertices[i], Is.EqualTo(data.vertices[i]), "original 16B payload immutable");
                    TestContext.WriteLine($"compound family={family} pose={pose} fill={_fill} stage={stage} input={before.Length} split={splits} inheritedPositive={inheritedPositive} inheritedNegative={inheritedNegative} positive={expectedPositive} negative={expectedNegative} geometryFirst={geometryFirst} volumeBefore={beforeVolume:R} volumeAfter={afterVolume:R} commits={w.commit.Commits} fixedScalePrepared={normalizeFixedScale}");
                    source = published.positive;
                }
                // Check actual display placement after moving/turning the final actor, without a simulation step.
                Assert.That(w.registry.TryGet(source, out PhysicsFragmentOwner moved), Is.True);
                moved.Root.transform.SetPositionAndRotation(new Vector3(2,-1,4), Quaternion.Euler(11,37,-9));
                Advance(w);
                CheckCharacterPlacement(w, source, moved, data.geometryToOwner);
            }
        }

        [TestCase("character-casual", 2)] [TestCase("character-casual", 3)]
        [TestCase("character-professional", 0)] [TestCase("character-professional", 1)]
        [TestCase("character-professional", 2)] [TestCase("character-professional", 3)]
        public void RepairedCharacter_ScaledLineage_RegistrationIsRejectedWithoutCutOrUpload(string family, int pose)
            => CharacterScaledRejection(family,pose,false);

        [TestCase("character-casual",2)] [TestCase("character-casual",3)]
        [TestCase("character-professional",2)] [TestCase("character-professional",3)]
        public void FixedScale_RuntimeParentScale_RemainsRejected(string family,int pose)
            => CharacterScaledRejection(family,pose,true);

        void CharacterScaledRejection(string family,int pose,bool normalizeFixedScale)
        {
            CharacterCompoundData data = LoadCharacterCompound(family,pose,normalizeFixedScale);
            var bounds = new Bounds(data.vertices[0].position, Vector3.zero);
            foreach (var vertex in data.vertices) bounds.Encapsulate(vertex.position);
            Matrix4x4 placement = Matrix4x4.TRS(data.position,data.rotation,Vector3.one)*data.geometryToOwner;
            Assert.That(VpMultiCutSnapshot.IsWithinInputContract(bounds,placement,Matrix4x4.identity), Is.True,
                "the placement and bounds themselves are valid, including scale");
            Assert.That(VpMultiCutSnapshot.IsWithinInputContract(bounds,placement,data.geometryToOwner.inverse), Is.False,
                "only the scaled lineage mapping violates the rigid-only contract");
            using (World w = NewWorld(character:data,expectFrameRejection:true))
            {
                Assert.That(w.registry.TryGet(w.source,out var owner), Is.True);
                Assert.That(owner.Shape.ConvexCount, Is.EqualTo(19));
                Assert.That(w.dag.ActiveCount, Is.Zero);
                Assert.That(w.commit.Commits, Is.Zero);
                Assert.That(w.ledger.Budget.IncompleteCutOperationCount, Is.Zero);
                Assert.That(w.display.RenderFragmentCount, Is.Zero);
                TestContext.WriteLine($"FRAME_REJECT family={family} pose={pose} fill={_fill} hulls=19 mappingDeterminant={data.geometryToOwner.inverse.determinant:R} cuts=0 commits=0 uploads=0 reason=F-CHARACTER-LINEAGE-SCALE fixedScalePrepared={normalizeFixedScale}");
            }
        }

        CharacterCompoundData LoadCharacterCompound(string family, int pose,bool normalizeFixedScale=false)
        {
            const string root = "Assets/Licensed/Compact16uvConvexRepair/";
            string fixturePath = root + "Resources/CharacterPhysicsMigration/" + family + ".json";
            if (!File.Exists(fixturePath)) Assert.Ignore("Private repaired character input missing.");
            Assert.That(CharacterHash(fixturePath), Is.EqualTo(family == "character-casual"
                ? "bbe481867248beef03e88446279e9246861a3ff95641e8e554b6b85a785451ed"
                : "452c746ef4801493a7c78a127291810064cdad218b4f6e78d3c1fcc911c61c4d"));
            var fixture = JsonUtility.FromJson<CharacterFixture>(File.ReadAllText(fixturePath));
            var entry = JsonUtility.FromJson<CharacterManifest>(File.ReadAllText(root + "intake.json")).assets.Single(e => e.family == family);
            Assert.That(CharacterHash(entry.assetPath), Is.EqualTo(fixture.sourceSha256));
            Assert.That(fixture.hulls.Length, Is.EqualTo(19));
            var ancestor = Track(new GameObject("Character compound snapshot"));
            var instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(entry.assetPath), ancestor.transform);
            foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
            var skin = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(s => s.sharedMesh != null && s.sharedMesh.name == entry.objectName);
            var originalSkin=skin;
            using var scaleCache=new VpFixedScaleSkinCache();
            using var preparedInput=normalizeFixedScale ? scaleCache.Prepare(skin,ancestor.transform) : null;
            float preparedScale=1;
            if (normalizeFixedScale)
            {
                skin=preparedInput.Renderer;
                preparedScale=preparedInput.FixedScale;
            }
            if (pose > 0) for (int i = 0; i < skin.bones.Length; i++) skin.bones[i].localRotation *= Quaternion.Euler(0,(i%3-1)*4f,(i%5-2)*3f);
            if (pose == 2 || pose == 3)
            {
                ancestor.transform.SetPositionAndRotation(new Vector3(3,-2,5), Quaternion.Euler(17,31,-12));
                ancestor.transform.localScale = new Vector3(1.3f,.8f,1.1f);
            }
            if (pose == 3 || pose == 4) skin.rootBone.localScale = Vector3.Scale(skin.rootBone.localScale,new Vector3(1.05f,.95f,1.1f));
            var baked = new Mesh(); _meshes.Add(baked);
            // Rejection tests deliberately inspect unsupported post-load parent scale before registration.
            if (normalizeFixedScale && pose!=2 && pose!=3) preparedInput.BakeCurrentPose(baked);
            else skin.BakeMesh(baked, true);
            if (normalizeFixedScale)
            {
                var originalBake=new Mesh(); _meshes.Add(originalBake); originalSkin.BakeMesh(originalBake,true);
                float error=originalBake.vertices.Zip(baked.vertices,(a,b)=>Vector3.Distance(originalSkin.transform.TransformPoint(a),skin.transform.TransformPoint(b))).Max();
                Assert.That(error,Is.LessThan(2e-5f),"normalization preserves visible world geometry");
                TestContext.WriteLine($"prepared family={family} pose={pose} fixedScale={preparedScale:R} maxWorldError={error:R}");
            }
            Matrix4x4 ownerWorld = Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one);
            Matrix4x4 geometryToOwner = ownerWorld.inverse * skin.transform.localToWorldMatrix;
            var polys = fixture.hulls.Select(h =>
            {
                Assert.That(h.convexAccepted, Is.True);
                int bone = Array.FindIndex(skin.bones, b => b.name == h.boneName);
                Assert.That(bone, Is.GreaterThanOrEqualTo(0));
                Matrix4x4 transform = geometryToOwner * skin.transform.worldToLocalMatrix * skin.bones[bone].localToWorldMatrix * skin.sharedMesh.bindposes[bone];
                var vertices = Enumerable.Range(0,h.rendererBindVertices.Length/3).Select(i =>
                {
                    Vector3 bindPoint=new Vector3((float)h.rendererBindVertices[i*3],(float)h.rendererBindVertices[i*3+1],(float)h.rendererBindVertices[i*3+2]);
                    Vector3 p = transform.MultiplyPoint3x4(bindPoint*preparedScale);
                    if (normalizeFixedScale)
                    {
                        Vector3 reference=(ownerWorld.inverse*originalSkin.bones[bone].localToWorldMatrix*originalSkin.sharedMesh.bindposes[bone]).MultiplyPoint3x4(bindPoint);
                        Assert.That(Vector3.Distance(p,reference),Is.LessThan(2e-5f),"all UCX points preserve original posed world positions");
                    }
                    return new double3(p.x,p.y,p.z);
                }).ToArray();
                return new ConvexPoly(vertices, Enumerable.Range(0,h.faceOffsets.Length-1).Select(i => h.faceIndices.Skip(h.faceOffsets[i]).Take(h.faceOffsets[i+1]-h.faceOffsets[i]).ToArray()).ToArray());
            }).ToArray();
            int indexCount = Enumerable.Range(0,baked.subMeshCount).Sum(s => (int)baked.GetIndexCount(s));
            using var vertices16 = new NativeArray<VpRenderVertex>(baked.vertexCount,Allocator.Temp);
            using var indices = new NativeArray<uint>(indexCount,Allocator.Temp);
            Assert.That(VpMeshConverter.TryConvert(baked,vertices16,indices,out _,out _), Is.True);
            Assert.That(baked.subMeshCount, Is.EqualTo(1));
            instance.SetActive(false); // Snapshot only: no duplicate active source renderer/collider in the test world.
            return new CharacterCompoundData { polys=polys, vertices=vertices16.ToArray(), indices=indices.ToArray(),
                topology=entry.topologyMap, topologyCount=entry.topologyCount, geometryToOwner=geometryToOwner,
                position=skin.transform.position, rotation=skin.transform.rotation };
        }

        static VpStoredGeometry AppendCharacter(VpCpuGeometryStorage storage, CharacterCompoundData data)
        {
            Assert.That(storage.TryAppendCuttable(data.vertices,data.indices,data.topology,data.topologyCount,
                new[] { new VpGeometrySubmesh(0,data.indices.Length,SideMaterial) },out var geometry,out var verdict), Is.True, verdict.ToString());
            return geometry;
        }
        Mesh CharacterColliderOf(ConvexPoly p, string name)
        {
            var mesh = new Mesh { name=name }; _meshes.Add(mesh);
            mesh.vertices = p.V.Select(v => new Vector3((float)v.x,(float)v.y,(float)v.z)).ToArray();
            mesh.triangles = p.F.SelectMany(f => Enumerable.Range(1,f.Length-2).SelectMany(k => new[] {f[0],f[k+1],f[k]})).ToArray();
            UnityEngine.Physics.BakeMesh(mesh.GetEntityId(),true,PhysicsCutCook.DefaultCooking);
            return mesh;
        }
        static ConvexPoly[] CharacterPolys(PhysicsOwnerShape shape)
        {
            var polys = new ConvexPoly[shape.ConvexCount];
            for (int c=0;c<polys.Length;c++)
            {
                var view = shape.BankOf(c).View(shape.Convex(c));
                var vertices = new double3[view.V]; var faces = new int[view.F][];
                for (int v=0;v<view.V;v++) vertices[v] = view.v[v];
                for (int f=0;f<view.F;f++)
                {
                    faces[f] = new int[view.faceOff[f+1]-view.faceOff[f]];
                    for (int i=0;i<faces[f].Length;i++) faces[f][i] = view.faceIdx[view.faceOff[f]+i];
                }
                polys[c] = new ConvexPoly(vertices,faces);
            }
            return polys;
        }
        static ConvexSide CharacterSide(ConvexPoly p, float4 plane)
        {
            var distances = p.V.Select(v => math.dot(plane.xyz,(float3)v)+plane.w).ToArray();
            bool positive = distances.Any(d => d > SupportEpsilon), negative = distances.Any(d => d < -SupportEpsilon);
            return positive && negative ? ConvexSide.Split : positive ? ConvexSide.Positive : negative ? ConvexSide.Negative : ConvexSide.NearPlaneToPositive;
        }
        static double CharacterVolume(ConvexPoly p) => p.F.Sum(f => Enumerable.Range(1,f.Length-2).Sum(k => math.dot(p.V[f[0]],math.cross(p.V[f[k]],p.V[f[k+1]]))/6));
        static string CharacterHash(string path) { using var s=File.OpenRead(path); using var h=SHA256.Create(); return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant(); }
        static void CheckCharacterChild(World w, LogicalFragmentId id, PhysicsFragmentOwner owner, Matrix4x4 geometryToOwner, float4 plane, bool positive)
        {
            Assert.That(w.registry.TryResolveFragment(owner.Body,out var resolved,out _), Is.True);
            Assert.That(resolved, Is.EqualTo(id));
            Assert.That(owner.Root.GetComponentsInChildren<MeshCollider>().Length, Is.EqualTo(owner.Shape.ConvexCount));
            foreach (var poly in CharacterPolys(owner.Shape)) foreach (double3 point in poly.V)
                Assert.That((positive ? 1 : -1)*(math.dot(plane.xyz,(float3)point)+plane.w), Is.GreaterThanOrEqualTo(-SupportEpsilon*2));
            Assert.That(w.dag.TryGetGeometry(id,out var geometry), Is.True);
            Assert.That(w.storage.TryAcquireIndexReadLease(geometry.indexRange,out var lease,out var indices), Is.True);
            try
            {
                Assert.That(indices.Length, Is.GreaterThan(0));
                foreach (uint index in indices)
                {
                    float3 point = geometryToOwner.MultiplyPoint3x4(w.storage.Vertices[(int)index].position);
                    Assert.That((positive ? 1 : -1)*(math.dot(plane.xyz,point)+plane.w), Is.GreaterThanOrEqualTo(-SupportEpsilon*2), "display side in same owner frame");
                }
            }
            finally { Assert.That(w.storage.TryReleaseIndexReadLease(lease), Is.True); }
            CheckCharacterPlacement(w,id,owner,geometryToOwner);
        }
        static void CheckCharacterPlacement(World w, LogicalFragmentId id, PhysicsFragmentOwner owner, Matrix4x4 geometryToOwner)
        {
            var actual = DrawnPlacement(w,id);
            var expected = owner.Root.transform.localToWorldMatrix*geometryToOwner;
            foreach (Vector3 p in k_probes) Assert.That(Vector3.Distance(actual.MultiplyPoint3x4(p),expected.MultiplyPoint3x4(p)), Is.LessThan(2e-5));
        }
        [Serializable] sealed class CharacterManifest { public CharacterEntry[] assets; }
        [Serializable] sealed class CharacterEntry { public string family,assetPath,objectName; public int[] topologyMap; public int topologyCount; }
        [Serializable] sealed class CharacterFixture { public string sourceSha256; public CharacterHull[] hulls; }
        [Serializable] sealed class CharacterHull { public string boneName; public bool convexAccepted; public double[] rendererBindVertices; public int[] faceOffsets,faceIndices; }
    }
}
