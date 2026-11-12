using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// FACE-PICK-1: the face collection's search and copy (the main thread's part, with a world): what the display draws is found along a
    /// line with no collider and no physics owner in the way of finding it, from either face, placed by the instance's own transform and
    /// kept or left out by its own clip; a drawn triangle and a temporary cap are told apart; and what is saved reads back as it was.
    /// </summary>
    public partial class CutWorldRootPlayModeTests
    {
        private static byte[] FacePickTestPicture(Color32 colour)
        {
            var picture = new Texture2D(8, 8, TextureFormat.RGB24, false);
            try
            {
                var pixels = new Color32[64];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = colour;
                picture.SetPixels32(pixels);
                picture.Apply(false);
                return picture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(picture);
            }
        }

        private static bool FacePickSameMatrix(float4x4 a, Matrix4x4 b)
        {
            float4x4 other = b;
            return math.cmax(math.abs(a.c0 - other.c0)) < 1e-5f && math.cmax(math.abs(a.c1 - other.c1)) < 1e-5f && math.cmax(math.abs(a.c2 - other.c2)) < 1e-5f && math.cmax(math.abs(a.c3 - other.c3)) < 1e-5f;
        }

        /// <summary>
        /// **A one-sided triangle with no collider and no physics owner is picked from its front and from its back, and reads back as
        /// saved.** One triangle appended to the storage and shown by the display for a ledger fragment nothing else knows -- no actor, no
        /// body, no collider, no owner; the display draws its registrations at their own placements, not where the physics owners say --
        /// placed by a transform that turns and moves it. A line from its front meets it (met from the
        /// front), a line from behind meets the same triangle (met from the back); a line past it meets nothing and is a pick all the same.
        /// The record says the registration that draws it, that no physics owner holds the fragment, the triangle's number, indices and
        /// vertices as the storage holds them and the transform it is drawn with; the pictures handed with a pick are listed under its
        /// number with the frames they were taken in. Read back from the folder: every hash as listed, every candidate worked out again
        /// from the saved parts alone, the saved vertices, indices and transform the live ones.
        /// </summary>
        [UnityTest]
        public IEnumerator FacePick_AOneSidedTriangleWithNoColliderOrOwner_IsPickedFromBothFaces_AndReadsBackAsSaved()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var corners = new[] { new float3(0f, 0f, 0f), new float3(1f, 0f, 0f), new float3(0f, 1f, 0f) };   // wound so that its front faces +z
            var vertices = new VpRenderVertex[3];
            for (int k = 0; k < 3; k++) vertices[k] = new VpRenderVertex { position = corners[k], normal = new Vector3(0f, 0f, 1f), uv0 = new Vector2(0.25f + (0.25f * k), 0.5f) };
            Assert.That(
                root.Storage.TryAppendPrepared(vertices, new uint[] { 0, 1, 2 }, new[] { 0, 1, 2 }, 3, new[] { new VpGeometrySubmesh(0, 3, SideMaterial) }, out VpStoredGeometry geometry),
                Is.True, "the triangle is appended");
            LogicalFragmentId fragment = root.Ledger.AddFragment();
            // A world's display asks the physics owners where each fragment stands, and draws one they do not know as nothing. This
            // display is told to draw every registration at its own placement instead (the display's default without a world), which
            // is how a draw with no owner behind it can be on the screen at all.
            root.Display.Placement = null;
            Matrix4x4 placement = Matrix4x4.TRS(new Vector3(5f, 2f, -3f), Quaternion.Euler(0f, 90f, 0f), Vector3.one);   // its front now faces +x
            Assert.That(root.Display.TryShow(fragment, geometry, placement), Is.True, "the display takes it in");
            yield return null;
            yield return null;
            Assert.That(root.Display.TryGetDrawSlots(fragment, out int commandStart, out int commandCount, out int _, out int _, out int _), Is.True, "the layout: it is drawn from slots of its own");
            Assert.That(commandCount, Is.EqualTo(1));
            Assert.That(root.Owners.TryGet(fragment, out _), Is.False, "the layout: no physics owner");
            Vector3 inside = placement.MultiplyPoint3x4(new Vector3(0.25f, 0.25f, 0f));
            Assert.That(Physics.Raycast(inside + new Vector3(3f, 0f, 0f), Vector3.left, 10f), Is.False, "the layout: no collider along the line");

            string folder = Path.Combine(Path.GetTempPath(), "zantetsu-face-pick-playmode-" + Guid.NewGuid().ToString("N"));
            var sink = new SpatialSplitCaptureSink(folder, SpatialSplitCaptureLimits.Default, "playmode test", new[] { "-zantetsuFaceCollect <directory>" });
            var told = new List<(int pick, bool ok, string why)>();
            Action<bool, string> Told(int pick) => (ok, why) => { lock (told) told.Add((pick, ok, why)); };
            try
            {
                Assert.That(FacePickCopy.Coarse(root.Display, inside + new Vector3(3f, 0f, 0f), new float3(-1f, 0f, 0f), 10f), Is.GreaterThan(0), "the coarse look finds its box");
                Assert.That(FacePickCopy.Coarse(root.Display, inside + new Vector3(3f, 40f, 0f), new float3(-1f, 0f, 0f), 10f), Is.Zero, "and nothing where nothing is drawn");

                // Pick 1: from its front (+x of it, looking along -x), with an eye beside the line.
                var front = new FacePickCopy.Aim
                {
                    aimed = true, origin = inside + new Vector3(3f, 0f, 0f), direction = new float3(-1f, 0f, 0f), reach = 10f,
                    hasCamera = true, cameraPosition = inside + new Vector3(3f, 0.2f, 0f), cameraRotation = new float4(0f, 0f, 0f, 1f), worldToCamera = float4x4.identity, projection = float4x4.identity, pixelWidth = 8, pixelHeight = 8,
                };
                Assert.That(FacePickCopy.TryPick(root, sink, 1, "playmode test", in front, null, Told(1), out FacePickSample first, out int items, out string failure), Is.True, failure);
                TestContext.Out.WriteLine("FACE PICK front: " + first.Describe());
                Assert.That(items, Is.EqualTo(1), "the record alone: not an NPC's piece");
                Assert.That(first.candidates, Has.Count.EqualTo(1), "one face along the line");
                FacePickCandidate a = first.candidates[0];
                Assert.That(a.kind == FacePick.KindMesh && !a.clipRejected && !a.backFace, Is.True, "a drawn triangle met from its front");
                Assert.That(a.distance, Is.EqualTo(3f).Within(1e-3f));
                Assert.That(math.distance(a.hitWorld, (float3)inside), Is.LessThan(1e-3f));
                Assert.That(a.eyeFacing, Is.GreaterThan(0f), "its front faces the eye");
                Assert.That(a.slot == commandStart && a.instance == 0 && a.triangle == 0 && a.instanceCount == 1, Is.True, "its draw slot, instance and triangle");
                Assert.That(a.ownerList == (int)VpDrawSlotOwnerList.Shown && a.ownerFragment == fragment.value && a.ownerCommand == 0, Is.True, "the registration that draws it");
                Assert.That(a.i0 == (uint)geometry.vertexStart && a.i1 == (uint)geometry.vertexStart + 1 && a.i2 == (uint)geometry.vertexStart + 2, Is.True, "its indices are the storage's global numbers");
                for (int k = 0; k < 3; k++) Assert.That(a.triangleVertices[k].position == vertices[k].position && a.triangleVertices[k].u == vertices[k].u && a.triangleVertices[k].normalX == vertices[k].normalX, Is.True, "vertex " + k + " as stored");
                Assert.That(FacePickSameMatrix(a.transform, placement), Is.True, "the transform it is drawn with");
                Assert.That(math.distance(a.w1, (float3)placement.MultiplyPoint3x4(corners[1])), Is.LessThan(1e-4f), "placed by it");
                Assert.That(a.clipPlanes, Is.Empty, "not clipped");
                Assert.That(a.indexSource, Is.EqualTo(1), "its indices read from the storage");
                Assert.That(a.materialIndex, Is.EqualTo(SideMaterial));
                Assert.That(a.gpuCheck, Is.Not.EqualTo(2), "the GPU does not hold another triangle (" + a.gpuCheckDetail + ")");
                TestContext.Out.WriteLine("FACE PICK front: the GPU comparison " + a.gpuCheck + " " + a.gpuCheckDetail);
                Assert.That(a.stateRef, Is.GreaterThanOrEqualTo(0));
                FacePickFragmentState state = first.states[a.stateRef];
                Assert.That(state.fragment == fragment.value && state.known && state.currentTarget && !state.ownerFound && !state.npc && !state.dagGeometry, Is.True,
                    "the ledger knows the fragment; no physics owner, no bone record, no geometry of the DAG's: " + state.Describe());
                Assert.That(first.pieceAttempted, Is.False);
                Assert.That(a.geometryRef, Is.EqualTo(0));
                FacePickGeometry saved = first.geometries[0];
                Assert.That(saved.failure, Is.Empty);
                Assert.That(saved.indices, Is.EqualTo(new[] { a.i0, a.i1, a.i2 }), "the whole drawn geometry: its one triangle");
                Assert.That(saved.vertexIds.Length == 3 && saved.vertexRead.All(r => r == 1), Is.True);
                Assert.That(FacePickCopy.TryHandImage(sink, first.world, 1, "before", FacePickTestPicture(new Color32(10, 20, 30, 255)), 8, 8, 101, "test", Told(1), out failure), Is.True, failure);
                Assert.That(FacePickCopy.TryHandImage(sink, first.world, 1, "after", FacePickTestPicture(new Color32(200, 20, 30, 255)), 8, 8, 102, "test", Told(1), out failure), Is.True, failure);

                // Pick 2: the same triangle from behind.
                var behind = new FacePickCopy.Aim { aimed = true, origin = inside - new Vector3(2f, 0f, 0f), direction = new float3(1f, 0f, 0f), reach = 10f };
                Assert.That(FacePickCopy.TryPick(root, sink, 2, "playmode test", in behind, null, Told(2), out FacePickSample second, out items, out failure), Is.True, failure);
                TestContext.Out.WriteLine("FACE PICK behind: " + second.Describe());
                Assert.That(second.candidates, Has.Count.EqualTo(1));
                FacePickCandidate b = second.candidates[0];
                Assert.That(b.kind == FacePick.KindMesh && b.backFace && b.triangle == 0 && b.slot == a.slot && b.i0 == a.i0, Is.True, "the same triangle, met from the back");
                Assert.That(b.distance, Is.EqualTo(2f).Within(1e-3f));
                Assert.That(float.IsNaN(b.eyeFacing), Is.True, "no eye given");

                // Pick 3: a line past it meets nothing the display draws, and is a pick all the same.
                var past = new FacePickCopy.Aim { aimed = true, origin = inside + new Vector3(3f, 40f, 0f), direction = new float3(-1f, 0f, 0f), reach = 10f };
                Assert.That(FacePickCopy.TryPick(root, sink, 3, "playmode test", in past, null, Told(3), out FacePickSample third, out items, out failure), Is.True, failure);
                Assert.That(third.candidates, Is.Empty);
                StringAssert.Contains("nothing the display draws was met", third.failure);

                // Pick 4: a controller that is not tracked searches nothing.
                var lost = new FacePickCopy.Aim { aimed = false, reach = 10f };
                Assert.That(FacePickCopy.TryPick(root, sink, 4, "playmode test", in lost, null, Told(4), out FacePickSample fourth, out items, out failure), Is.True, failure);
                StringAssert.Contains("not tracked", fourth.failure);
                FacePickCopy.HandFailure(sink, first.world, 5, "a pick not taken at all (the test's)");

                Assert.That(sink.Close(10.0), Is.True, "the writer ended");
                lock (told)
                {
                    Assert.That(told.Any(t => !t.ok), Is.False, string.Join("; ", told.Select(t => t.pick + ":" + t.ok + ":" + t.why)));
                    Assert.That(told.Count(t => t.pick == 1), Is.EqualTo(3), "pick 1: its record and two pictures");
                    Assert.That(told.Count(t => t.pick == 2) == 1 && told.Count(t => t.pick == 3) == 1 && told.Count(t => t.pick == 4) == 1, Is.True);
                }

                List<FacePickSet.Pick> picks = FacePickSet.Read(folder);
                TestContext.Out.WriteLine("FACE PICK read back: " + FacePickSet.Describe(picks));
                Assert.That(picks, Has.Count.EqualTo(5));
                for (int i = 0; i < 4; i++) Assert.That(picks[i].sample != null && picks[i].problems.Count == 0, Is.True, "pick " + (i + 1) + ": " + string.Join("; ", picks[i].problems));
                Assert.That(picks[4].sample == null && picks[4].failure.Contains("not taken at all"), Is.True, "the pick not taken is a line with its reason");
                Assert.That(picks[0].imageBefore == first.imageBefore && picks[0].imageAfter == first.imageAfter && picks[0].imageBeforeFrame == 101 && picks[0].imageAfterFrame == 102, Is.True,
                    "the pictures under the pick's number, with their frames");
                Assert.That(File.Exists(Path.Combine(folder, first.imageBefore)) && File.Exists(Path.Combine(folder, first.imageAfter)), Is.True);
                Assert.That(picks[1].imageBefore, Is.Null, "no picture was handed for pick 2");
                FacePickSample read = picks[0].sample;
                Assert.That(read.sha256, Is.EqualTo(first.sha256), "the content read is the content written");
                FacePickCandidate ra = read.candidates[0];
                Assert.That(ra.kind == a.kind && ra.slot == a.slot && ra.instance == a.instance && ra.record == a.record && ra.triangle == a.triangle && ra.indexAt == a.indexAt, Is.True, "the face's identity");
                Assert.That(ra.i0 == a.i0 && ra.i1 == a.i1 && ra.i2 == a.i2, Is.True, "its indices");
                for (int k = 0; k < 3; k++) Assert.That(ra.triangleVertices[k].position == vertices[k].position && ra.triangleVertices[k].v == vertices[k].v && ra.triangleVertices[k].normalY == vertices[k].normalY, Is.True, "vertex " + k);
                Assert.That(math.all(ra.transform.c0 == a.transform.c0) && math.all(ra.transform.c1 == a.transform.c1) && math.all(ra.transform.c2 == a.transform.c2) && math.all(ra.transform.c3 == a.transform.c3), Is.True, "its transform, every bit");
                Assert.That(ra.ownerFragment == fragment.value && ra.material == a.material && ra.backFace == a.backFace && ra.distance == a.distance, Is.True);
                FacePickGeometry rg = read.geometries[ra.geometryRef];
                Assert.That(rg.indices, Is.EqualTo(saved.indices));
                Assert.That(rg.vertexIds, Is.EqualTo(saved.vertexIds));
                Assert.That(rg.identity.Equals(NpcPieceGeometryIdentity.Of(in geometry)), Is.True, "the geometry's identity");
                Assert.That(rg.TryGetVertex(a.i2, out VpRenderVertex found) && found.position == vertices[2].position, Is.True);
                Assert.That(read.states[ra.stateRef].ownerFound, Is.False);
                Assert.That(picks[1].sample.candidates[0].backFace, Is.True);
                Assert.That(Directory.GetFiles(folder, "*.partial"), Is.Empty);
            }
            finally
            {
                sink.Close(0.0);
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            yield return EndWorld(root);
        }

        /// <summary>
        /// **A drawn triangle and a temporary cap are told apart, the clip is applied, and each names what it belongs to.** A box cut at
        /// its middle with the cut's geometry held back: the two sides are the parent's geometry drawn twice, each clipped to its own
        /// half, and the cut faces are temporary caps. A line down through the box meets, in order: the top face (drawn by the upper
        /// side's instance), the two caps at the cut, the bottom face (the lower side's instance) -- and the same top and bottom faces
        /// once more each through the other side's instance, where that instance's clip leaves them out: saved as left out, after the
        /// kept ones. Each mesh candidate carries its render fragment with one clip plane and its published side's fragment with a live
        /// owner; each cap carries its operation, its side and its polygon, and the estimate from an eye above says the cap that faces up
        /// is inside its body. Read back, every candidate comes out again as saved.
        /// </summary>
        [UnityTest]
        public IEnumerator FacePick_ADrawnTriangleAndATemporaryCap_AreToldApart_WithTheClipApplied()
        {
            HoldingExecutor geometryPool = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.GeometryPool ? geometryPool = Held(new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2))) : null,
                null);
            Assert.That(geometryPool, Is.Not.Null);
            geometryPool.HoldEverything = true;
            root.Driver.RemainingMainSeconds = () => 1.0;
            var centre = new Vector3(0f, 4f, 0f);
            LogicalFragmentId body = AddBody(root, centre);
            Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner wholeOwner), Is.True);
            wholeOwner.Body.isKinematic = true;
            yield return null;
            yield return null;

            Assert.That(root.TryAsk(Ask(body, new float4(0f, 1f, 0f, 0f))), Is.True);
            CutOperationId operation = default;
            yield return Until(
                () =>
                {
                    foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions) operation = candidate.Operation;
                    return operation.IsSet && geometryPool.HoldingCount > 0 && root.Ledger.TryGetOperation(operation, out LogicalCutOperation published) && published.positive.IsSet;
                },
                "the children are published while the cut's geometry is held back");
            Assert.That(root.Ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
            Assert.That(root.Owners.TryGet(record.positive, out PhysicsFragmentOwner upper) && root.Owners.TryGet(record.negative, out PhysicsFragmentOwner lower), Is.True);
            root.Owners.TryGet(record.positive, out upper);
            root.Owners.TryGet(record.negative, out lower);
            upper.Body.isKinematic = true;
            lower.Body.isKinematic = true;
            upper.Root.transform.SetPositionAndRotation(centre, Quaternion.identity);
            lower.Root.transform.SetPositionAndRotation(centre, Quaternion.identity);
            yield return null;
            yield return null;
            yield return null;
            Assert.That(root.Display.CapRecordCount, Is.GreaterThan(0), "the layout: the cut faces are temporary caps");

            string folder = Path.Combine(Path.GetTempPath(), "zantetsu-face-pick-playmode-" + Guid.NewGuid().ToString("N"));
            var sink = new SpatialSplitCaptureSink(folder, SpatialSplitCaptureLimits.Default, "playmode test", new[] { "-zantetsuFaceCollect <directory>" });
            try
            {
                var down = new FacePickCopy.Aim
                {
                    aimed = true, origin = new float3(0.3f, 10f, 0.2f), direction = new float3(0f, -1f, 0f), reach = 10f,
                    hasCamera = true, cameraPosition = new float3(0.3f, 12f, 0.2f), cameraRotation = new float4(0f, 0f, 0f, 1f), worldToCamera = float4x4.identity, projection = float4x4.identity,
                };
                Assert.That(FacePickCopy.TryPick(root, sink, 1, "playmode test", in down, null, null, out FacePickSample sample, out int _, out string failure), Is.True, failure);
                TestContext.Out.WriteLine("FACE PICK through the cut box: " + sample.Describe());
                List<FacePickCandidate> kept = sample.candidates.Where(c => !c.clipRejected).ToList();
                List<FacePickCandidate> leftOut = sample.candidates.Where(c => c.clipRejected).ToList();
                Assert.That(kept.Select(c => c.kind), Is.EqualTo(new[] { FacePick.KindMesh, FacePick.KindCap, FacePick.KindCap, FacePick.KindMesh }), "top face, the two caps, bottom face");
                Assert.That(kept[0].distance, Is.EqualTo(5f).Within(0.02f), "the top face");
                Assert.That(kept[1].distance, Is.EqualTo(6f).Within(0.02f), "the cut");
                Assert.That(kept[2].distance, Is.EqualTo(6f).Within(0.02f));
                Assert.That(kept[3].distance, Is.EqualTo(7f).Within(0.02f), "the bottom face");
                for (int i = 0; i < sample.candidates.Count; i++) Assert.That(sample.candidates[i].rank, Is.EqualTo(i), "ranked in the saved order");
                Assert.That(sample.candidates.Take(4).All(c => !c.clipRejected) && sample.candidates.Skip(4).All(c => c.clipRejected), Is.True, "the kept ones first, then the ones left out");
                Assert.That(leftOut, Has.Count.EqualTo(2), "the same two faces through the other side's instance, left out by its clip");
                Assert.That(leftOut.All(c => c.kind == FacePick.KindMesh && c.clipMin < 0f && c.clipPlanes.Length == 1), Is.True);
                Assert.That(leftOut.Select(c => c.distance).OrderBy(d => d).ToArray(), Is.EqualTo(new[] { 5f, 7f }).Within(0.02f));

                // The drawn faces: the parent's geometry, one instance a side, each with one plane and its side's published fragment.
                Assert.That(kept[0].instance, Is.Not.EqualTo(kept[3].instance), "the two sides are two instances");
                Assert.That(kept[0].clipPlanes.Length == 1 && kept[3].clipPlanes.Length == 1, Is.True, "one plane each");
                Assert.That(kept[0].sidePublished && kept[0].sideFragment == record.positive.value && kept[0].sideOperation == operation.value, Is.True, "the top face is of the published upper side");
                Assert.That(kept[3].sidePublished && kept[3].sideFragment == record.negative.value, Is.True, "the bottom face of the lower");
                Assert.That(kept[0].ownerFragment == body.value && kept[0].ownerList == (int)VpDrawSlotOwnerList.Shown, Is.True, "both drawn from the parent's registration");
                Assert.That(kept[0].renderFragmentRef >= 0 && sample.renderFragments[kept[0].renderFragmentRef].found && sample.renderFragments[kept[0].renderFragmentRef].clipPlanes.Length == 1, Is.True, "its render fragment, with the plane");
                Assert.That(kept[0].renderFragment, Is.Not.EqualTo(kept[3].renderFragment));
                FacePickFragmentState upperState = sample.StateOf(record.positive.value), lowerState = sample.StateOf(record.negative.value), parentState = sample.StateOf(body.value);
                Assert.That(upperState != null && upperState.ownerFound && upperState.ownerHasBody && upperState.hasOrigin && upperState.originOperation == operation.value && upperState.originSide > 0f, Is.True,
                    "the upper side: a live owner, made by the cut's positive side");
                Assert.That(lowerState != null && lowerState.ownerFound && lowerState.originSide < 0f && lowerState.parent == body.value, Is.True);
                Assert.That(parentState != null && parentState.known && !parentState.currentTarget, Is.True, "the parent is known and no longer a current target");
                Assert.That(sample.states[kept[0].stateRef].fragment, Is.EqualTo(record.positive.value), "the first candidate's own fragment is its side's");

                // The caps: one a side, at the cut, of this operation; the one that faces up is seen from above and inside its body.
                Assert.That(kept[1].capOperation == operation.value && kept[2].capOperation == operation.value && kept[1].capSource == body.value, Is.True);
                Assert.That(kept[1].capSide * kept[2].capSide, Is.LessThan(0f), "one a side");
                Assert.That(kept[1].capIndex, Is.Not.EqualTo(kept[2].capIndex));
                Assert.That(kept[1].capVertexCount >= 3 && kept[1].capPolygon.Length == kept[1].capVertexCount, Is.True, "its polygon");
                Assert.That(kept[1].capPolygon.All(p => math.abs(p.y - 4f) < 0.02f), Is.True, "in the cut's plane");
                FacePickCandidate facingUp = kept[1].capOutward.y > 0f ? kept[1] : kept[2], facingDown = kept[1].capOutward.y > 0f ? kept[2] : kept[1];
                Assert.That(facingUp.capOutward.y > 0.9f && facingDown.capOutward.y < -0.9f, Is.True);
                Assert.That(!facingUp.backFace && facingDown.backFace, Is.True, "the line comes from above: the cap that faces up is met from its front");
                Assert.That(facingUp.eyeFacing > 0f && facingDown.eyeFacing < 0f, Is.True);
                Assert.That(facingUp.capFragment, Is.EqualTo(record.negative.value), "the cap that faces up closes the lower side");
                Assert.That(facingUp.eyeEstimated && facingUp.eyeInside && facingUp.eyeCrossingsBeyond == 1, Is.True, "the eye's ray leaves the lower side's body once beyond the cap: inside");
                Assert.That(facingDown.eyeEstimated && !facingDown.eyeInside, Is.True, "nothing of the upper side's body lies beyond its cap from above");
                Assert.That(sample.capHits, Is.EqualTo(2));
                Assert.That(sample.unityRenderers.Length, Is.LessThanOrEqualTo(12), "Unity's own renderers along the line are listed by their boxes");
                foreach (string line in sample.unityRenderers) TestContext.Out.WriteLine("FACE PICK Unity renderer: " + line);
                Assert.That(sample.meshHits == 4 && sample.meshClipRejected == 2, Is.True, "four faces met, two left out");
                Assert.That(sample.geometries, Has.Count.EqualTo(1), "one registration draws all of it: the parent's geometry, saved once");
                Assert.That(sample.geometries[0].failure.Length == 0 && sample.geometries[0].indices.Length == 36, Is.True, "the whole box");

                Assert.That(sink.Close(10.0), Is.True, "the writer ended");
                List<FacePickSet.Pick> picks = FacePickSet.Read(folder);
                TestContext.Out.WriteLine("FACE PICK read back: " + FacePickSet.Describe(picks));
                Assert.That(picks, Has.Count.EqualTo(1));
                Assert.That(picks[0].sample != null && picks[0].problems.Count == 0, Is.True, string.Join("; ", picks[0].problems));
                Assert.That(picks[0].sample.sha256, Is.EqualTo(sample.sha256));
                Assert.That(picks[0].sample.candidates.Select(c => c.kind), Is.EqualTo(sample.candidates.Select(c => c.kind)));
                Assert.That(picks[0].sample.candidates.Select(c => c.clipRejected), Is.EqualTo(sample.candidates.Select(c => c.clipRejected)));
            }
            finally
            {
                sink.Close(0.0);
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            geometryPool.HoldEverything = false;
            geometryPool.ReleaseEverything();
            yield return Until(
                () => root.Ledger.TryGetOperation(operation, out LogicalCutOperation now) && now.state == LogicalCutOperationState.Completed, "the cut's geometry committed");
            yield return null;
            yield return EndWorld(root);
        }
    }
}
