using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// What a collection keeps from the one before (TL, 2026-10-05; DESIGN 5.6): with nothing a structure is settled
    /// from changed, the registrations are not read from the ledger, the structure is not walked, the commands,
    /// materials, draw ranges and sides are not assembled, and only the instances the placement pass placed anew are
    /// written and sent -- one range a buffer. Every case here runs the display's own collection and upload. The
    /// reference is the same display made to collect everything every time (as it did before), given the very same
    /// steps: after every collection the two draw the same sides, clips, caps, commands and transforms, and each one's
    /// GPU buffers, read back, hold what it adopted.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        // One display with its bodies and its one placement lookup (the same object throughout: where things stand
        // changes inside it, as the product's does).
        private sealed class Run
        {
            public Scene scene;
            public VpTestPlacements at = new VpTestPlacements();
            public readonly List<LogicalFragmentId> bodies = new List<LogicalFragmentId>();
            public readonly List<VpStoredGeometry> geometries = new List<VpStoredGeometry>();
            public bool cuttable;   // its bodies are appended through the cut input gate: a commit can cut them
            public VpLogicalCutDisplay Display => scene.display;
        }

        // The cube of the commit cases: its faces wound as the cut input gate takes them.
        private static readonly float3[] k_cuttablePoints =
        {
            new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, 1f, -1f), new float3(-1f, 1f, -1f),
            new float3(-1f, -1f, 1f), new float3(1f, -1f, 1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
        };

        private static readonly int[][] k_cuttableFaces =
        {
            new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
            new[] { 2, 3, 7, 6 }, new[] { 1, 2, 6, 5 }, new[] { 0, 4, 7, 3 },
        };

        private static VpStoredGeometry AppendCuttableCube(VpCpuGeometryStorage storage)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            foreach (int[] c in k_cuttableFaces)
            {
                float3 n = math.normalize(math.cross(k_cuttablePoints[c[1]] - k_cuttablePoints[c[0]], k_cuttablePoints[c[2]] - k_cuttablePoints[c[0]]));
                uint b = (uint)vertices.Count;
                for (int k = 0; k < 4; k++)
                {
                    vertices.Add(new VpRenderVertex { position = k_cuttablePoints[c[k]], normal = n, uv0 = new float2(0.5f, 0.5f) });
                    topology.Add(c[k]);
                }

                indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }

            Assert.That(
                storage.TryAppendCuttable(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_cuttablePoints.Length,
                    new[] { new VpGeometrySubmesh(0, indices.Count, SideMaterial) }, out VpStoredGeometry geometry, out _),
                Is.True, "append the cube as something that may be cut");
            return geometry;
        }

        private sealed class Twin : IDisposable
        {
            public Run kept, everything;

            public void Dispose()
            {
                kept.scene?.Dispose();
                everything.scene?.Dispose();
            }
        }

        private static Matrix4x4 Stand(int i, float lift = 0f, float turn = 0f) =>
            Matrix4x4.TRS(new Vector3(3f * i, lift, 0f), Quaternion.Euler(0f, turn, 0f), Vector3.one);

        private void AddBody(Run run, Matrix4x4 at)
        {
            LogicalFragmentId fragment = run.scene.ledger.AddFragment();
            VpStoredGeometry geometry = run.cuttable ? AppendCuttableCube(run.scene.storage) : AppendCube(run.scene.storage, false);
            Assert.That(run.Display.TryShow(fragment, geometry, at), Is.True, "body " + run.bodies.Count);
            run.at.Put(fragment, at);
            run.bodies.Add(fragment);
            run.geometries.Add(geometry);
        }

        private Twin NewTwin(int bodies, int instances = 64, bool cuttable = false, bool growing = false)
        {
            var twin = new Twin { kept = new Run { cuttable = cuttable }, everything = new Run { cuttable = cuttable } };
            foreach (Run run in new[] { twin.kept, twin.everything })
            {
                // growing: a first room of that many instances under limits of 64, so that more bodies grow it
                run.scene = growing
                    ? NewReservedScene(instances, instances, instances, 64, new VpLogicalCutDisplayLimits(64, 64, 64, 64), null)
                    : NewScene(instances, Math.Max(128, instances * 2));
                Assert.That(run.scene, Is.Not.Null, "the display was made");
                run.Display.Placement = run.at;
                _frame++;
                for (int i = 0; i < bodies; i++) AddBody(run, Stand(i));
            }

            twin.everything.Display.collectEverythingForTest = true;
            return twin;
        }

        private static void Both(Twin twin, Action<Run> step)
        {
            step(twin.kept);
            step(twin.everything);
        }

        // The GPU's buffers, read back: the commands' arguments, every instance's transform and clip, as adopted.
        private static void AssertGpuHoldsWhatIsAdopted(VpLogicalCutDisplay display, string what)
        {
            // The draw data stands in slots (DESIGN 5.6): the batch goes through every command slot up to the end, free
            // ones among them, and may name every instance record up to its end. A free slot draws no instance on the
            // GPU; a command's instances begin where the display says, not where the commands before it end.
            VpIndexedIndirectDrawBatch batch = display.BodyBatchForTest;
            int commandEnd = display.DrawCommandEnd, instanceEnd = display.DrawInstanceEnd;
            Assert.That(batch.CommandCount, Is.EqualTo(commandEnd), what + ": the batch goes through the command slots that are adopted");
            Assert.That(batch.InstanceCount, Is.EqualTo(instanceEnd), what + ": and may name the instance records that are");
            Assert.That(commandEnd, Is.GreaterThanOrEqualTo(display.DrawCommandCount), what + ": the slots hold the commands that draw");
            Assert.That(instanceEnd, Is.GreaterThanOrEqualTo(display.SideCount), what + ": and the records they draw");
            if (commandEnd > 0)
            {
                var arguments = new GraphicsBuffer.IndirectDrawIndexedArgs[commandEnd];
                batch.ShadowArgumentBuffer.GetData(arguments, 0, 0, commandEnd);
                var forward = new GraphicsBuffer.IndirectDrawIndexedArgs[commandEnd];
                batch.ForwardArgumentBuffer.GetData(forward, 0, 0, commandEnd);
                Matrix4x4[] transforms = null;
                VpInstanceClip[] clips = null;
                if (instanceEnd > 0)
                {
                    transforms = new Matrix4x4[instanceEnd];
                    batch.InstanceBuffer.GetData(transforms, 0, 0, instanceEnd);
                    clips = new VpInstanceClip[instanceEnd];
                    batch.InstanceClipBuffer.GetData(clips, 0, 0, instanceEnd);
                }

                // One stereo condition for the batch, the display's: under single pass instanced the forward arguments
                // draw every instance twice (an eye each), the shadow's once.
                Assert.That(batch.SinglePassInstanced, Is.EqualTo(display.SinglePassInstanced), what + ": the batch draws under the display's stereo condition");
                uint eyes = display.SinglePassInstanced ? 2u : 1u;
                int live = 0, drawn = 0;
                var named = new bool[instanceEnd];
                for (int c = 0; c < commandEnd; c++)
                {
                    Assert.That(display.TryGetCommandSlot(c, out VpIndirectCommand command, out int start, out bool free), Is.True, what + ": command slot " + c);
                    if (free)
                    {
                        Assert.That(command.instanceCount, Is.Zero, what + ": free command slot " + c + " holds a command of no instance");
                        Assert.That(forward[c].instanceCount, Is.Zero, what + ": free command slot " + c + " draws no instance (forward)");
                        Assert.That(arguments[c].instanceCount, Is.Zero, what + ": free command slot " + c + " draws no instance on the GPU");
                        continue;
                    }

                    live++;
                    Assert.That(forward[c].indexCountPerInstance, Is.EqualTo((uint)command.range.indexCount), what + ": command slot " + c + " index count (forward)");
                    Assert.That(forward[c].startIndex, Is.EqualTo((uint)command.range.indexStart), what + ": command slot " + c + " index start (forward)");
                    Assert.That(forward[c].instanceCount, Is.EqualTo((uint)command.instanceCount * eyes), what + ": command slot " + c + " instance count (forward, " + eyes + " an instance)");
                    Assert.That(forward[c].startInstance, Is.EqualTo((uint)start * eyes), what + ": command slot " + c + " first instance (forward)");
                    Assert.That(arguments[c].indexCountPerInstance, Is.EqualTo((uint)command.range.indexCount), what + ": command slot " + c + " index count on the GPU");
                    Assert.That(arguments[c].startIndex, Is.EqualTo((uint)command.range.indexStart), what + ": command slot " + c + " index start");
                    Assert.That(arguments[c].instanceCount, Is.EqualTo((uint)command.instanceCount), what + ": command slot " + c + " instance count");
                    Assert.That(arguments[c].startInstance, Is.EqualTo((uint)start), what + ": command slot " + c + " first instance");
                    for (int k = 0; k < command.instanceCount; k++, drawn++)
                    {
                        int record = start + k;
                        Assert.That(record, Is.LessThan(instanceEnd), what + ": command slot " + c + " names a record below the end");
                        Assert.That(named[record], Is.False, what + ": instance record " + record + " is drawn by one command only");
                        named[record] = true;
                        if (!transforms[record].Equals(display.InstanceRecordTransformForTest(record)))
                        {
                            Assert.Fail(what + ": instance record " + record + " transform on the GPU\n" + transforms[record] + "\nadopted\n" + display.InstanceRecordTransformForTest(record));
                        }

                        Assert.That(clips[record].Equals(display.InstanceRecordClipForTest(record)), Is.True, what + ": instance record " + record + " clip on the GPU is the adopted one");
                    }
                }

                Assert.That(live, Is.EqualTo(display.DrawCommandCount), what + ": the slots that are not free are the commands that draw");
                Assert.That(drawn, Is.EqualTo(display.SideCount), what + ": the commands' instances are the instances");

                // By index -- the registrations in their order -- the display names the same records with the same clips.
                for (int i = 0; i < display.SideCount; i++)
                {
                    display.TryGetSide(i, out LogicalCutDisplaySide side);
                    int record = display.InstanceRecordOfIndexForTest(i);
                    Assert.That(named[record], Is.True, what + ": instance " + i + " is a record a command draws");
                    Assert.That(clips[record].Equals(side.clip), Is.True, what + ": instance " + i + " clip on the GPU is its side's");
                }
            }
            else
            {
                Assert.That(display.DrawCommandCount, Is.Zero, what + ": no slot, no command");
                Assert.That(display.SideCount, Is.Zero, what + ": and no instance");
            }

            // The cap normals: every vertex of every adopted cap holds that cap's outward normal on the GPU.
            VpMultiCutSnapshot adopted = display.AdoptedSnapshot;
            int capVertices = adopted.CapVertexCount;
            Assert.That(display.CapNormalCount, Is.EqualTo(capVertices), what + ": a normal a cap vertex");
            if (capVertices > 0)
            {
                var normals = new Vector4[capVertices];
                display.CapNormalBufferForTest.GetData(normals, 0, 0, capVertices);
                int seen = 0;
                for (int c = 0; c < adopted.CapCount; c++)
                {
                    adopted.TryGetCap(c, out VpMultiCutCap cap);
                    var expect = new Vector4(cap.outwardNormal.x, cap.outwardNormal.y, cap.outwardNormal.z, 0f);
                    for (int v = 0; v < cap.vertexCount; v++, seen++)
                    {
                        if (!normals[cap.vertexStart + v].Equals(expect))
                        {
                            Assert.Fail(what + ": cap " + c + " vertex " + v + " normal on the GPU " + normals[cap.vertexStart + v].ToString("F6") + ", the adopted cap's " + expect.ToString("F6"));
                        }
                    }
                }

                Assert.That(seen, Is.EqualTo(capVertices), what + ": the caps' vertices are the cap vertices");
            }
        }

        // The two displays draw the same: sides (their clips with them), caps and their vertices, commands, transforms,
        // and the bounds their batches cull by.
        private static void AssertSameDrawing(Twin twin, string what)
        {
            VpLogicalCutDisplay a = twin.kept.Display, b = twin.everything.Display;
            Drawn kept = Capture(a), everything = Capture(b);
            Assert.That(kept.sides.Count, Is.EqualTo(everything.sides.Count), what + ": as many instances");
            Assert.That(kept.commands.Count, Is.EqualTo(everything.commands.Count), what + ": as many commands");
            Assert.That(kept.caps.Count, Is.EqualTo(everything.caps.Count), what + ": as many caps");
            for (int i = 0; i < kept.sides.Count; i++)
            {
                Assert.That(kept.sides[i].Equals(everything.sides[i]), Is.True, what + ": side " + i + " (what it is, and its clip)");
                if (!a.InstanceTransformForTest(i).Equals(b.InstanceTransformForTest(i)))
                {
                    Assert.Fail(what + ": instance " + i + " transform\nkept\n" + a.InstanceTransformForTest(i) + "\ncollecting everything\n" + b.InstanceTransformForTest(i));
                }
            }

            for (int c = 0; c < kept.commands.Count; c++)
            {
                Assert.That(kept.commands[c].Equals(everything.commands[c]), Is.True, what + ": command " + c);
            }

            for (int c = 0; c < kept.caps.Count; c++)
            {
                Assert.That(kept.caps[c].Equals(everything.caps[c]), Is.True, what + ": cap " + c);
                Assert.That(kept.vertices[c].Length, Is.EqualTo(everything.vertices[c].Length), what + ": cap " + c + " vertices");
                for (int v = 0; v < kept.vertices[c].Length; v++)
                {
                    Assert.That(kept.vertices[c][v].Equals(everything.vertices[c][v]), Is.True, what + ": cap " + c + " vertex " + v);
                }
            }

            Bounds keptBounds = a.BodyBatchForTest.WorldBounds, everythingBounds = b.BodyBatchForTest.WorldBounds;
            Assert.That(keptBounds.center.Equals(everythingBounds.center) && keptBounds.extents.Equals(everythingBounds.extents), Is.True,
                what + ": the draw's bounds " + keptBounds + " against " + everythingBounds);
            AssertGpuHoldsWhatIsAdopted(a, what + " (kept)");
            AssertGpuHoldsWhatIsAdopted(b, what + " (collecting everything)");

            // The placement pass reads what a render fragment stands as and its branch's selected count from its own
            // registration's part: the same as searching for them, for every render fragment of what is adopted.
            Assert.That(a.AdoptedSnapshot.PlacementReadingsDifferingForTest(out _, out _, out _), Is.EqualTo(0), what + ": read by the registration as searched (kept)");
            Assert.That(b.AdoptedSnapshot.PlacementReadingsDifferingForTest(out _, out _, out _), Is.EqualTo(0), what + ": read by the registration as searched (collecting everything)");
        }

        private void CollectBoth(Twin twin, string what)
        {
            _frame++;
            Assert.That(twin.kept.Display.TryBeginFrame(), Is.True, what + ": collected (kept)");
            Assert.That(twin.everything.Display.TryBeginFrame(), Is.True, what + ": collected (collecting everything)");
            AssertSameDrawing(twin, what);
        }

        private struct Kept
        {
            public long reads, lists, arrangements, assemblies, written, caughtUp, argumentTransfers, argumentElements, instanceTransfers, instanceElements, keptWhole, walks;
            public long argumentCalls, instanceCalls, capNormalCalls, capNormalVertices, capNormalsMade;
            public long commandsWritten, wholeArguments, wholeInstances, wholeCalls;

            public static Kept Of(VpLogicalCutDisplay d) => new Kept
            {
                reads = d.LedgerStateReads, lists = d.RegistrationListBuilds, arrangements = d.DrawArrangements, assemblies = d.CandidateAssemblies,
                written = d.InstanceRecordsWritten, caughtUp = d.InstanceRecordsCaughtUp, argumentTransfers = d.BodyArgumentTransfers,
                argumentElements = d.BodyArgumentElementsTransferred, instanceTransfers = d.BodyInstanceTransfers,
                instanceElements = d.BodyInstanceElementsTransferred, keptWhole = d.StructuresKeptWhole, walks = d.StructureWalks,
                argumentCalls = d.BodyArgumentSetDataCalls, instanceCalls = d.BodyInstanceSetDataCalls, capNormalCalls = d.CapNormalTransfers,
                capNormalVertices = d.CapNormalVerticesTransferred, capNormalsMade = d.CapNormalsMade,
                commandsWritten = d.CommandRecordsWritten, wholeArguments = d.BodyWholeArgumentElementsTransferred,
                wholeInstances = d.BodyWholeInstanceElementsTransferred, wholeCalls = d.BodyWholeSetDataCalls,
            };

            public Kept Since(Kept before) => new Kept
            {
                reads = reads - before.reads, lists = lists - before.lists, arrangements = arrangements - before.arrangements, assemblies = assemblies - before.assemblies,
                written = written - before.written, caughtUp = caughtUp - before.caughtUp, argumentTransfers = argumentTransfers - before.argumentTransfers,
                argumentElements = argumentElements - before.argumentElements, instanceTransfers = instanceTransfers - before.instanceTransfers,
                instanceElements = instanceElements - before.instanceElements, keptWhole = keptWhole - before.keptWhole, walks = walks - before.walks,
                argumentCalls = argumentCalls - before.argumentCalls, instanceCalls = instanceCalls - before.instanceCalls, capNormalCalls = capNormalCalls - before.capNormalCalls,
                capNormalVertices = capNormalVertices - before.capNormalVertices, capNormalsMade = capNormalsMade - before.capNormalsMade,
                commandsWritten = commandsWritten - before.commandsWritten, wholeArguments = wholeArguments - before.wholeArguments,
                wholeInstances = wholeInstances - before.wholeInstances, wholeCalls = wholeCalls - before.wholeCalls,
            };

            public override string ToString() =>
                "ledger reads " + reads + ", registration lists " + lists + ", draw arrangements " + arrangements + ", structure walks " + walks + " (kept whole " + keptWhole
                + "), assemblies " + assemblies + ", instance records written " + written + " (given from the other side " + caughtUp + "), argument updates "
                + argumentTransfers + " (" + argumentCalls + " SetData calls, " + argumentElements + " commands), instance updates " + instanceTransfers + " (" + instanceCalls
                + " SetData calls, " + instanceElements + " instances), sent whole " + wholeArguments + " commands and " + wholeInstances + " instances (" + wholeCalls
                + " SetData calls), commands written " + commandsWritten + ", cap normal SetData calls " + capNormalCalls + " (" + capNormalVertices + " vertices; made on the CPU "
                + capNormalsMade + ")";
        }

        [Test]
        public void KeptCollection_WithNothingChanged_ReadsNothing_WalksNothing_AssemblesNothing_SendsNothing()
        {
            using (Twin twin = NewTwin(16))
            {
                // The first collections settle both sides of the draw data for the structure.
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                Kept before = Kept.Of(twin.kept.Display), everythingBefore = Kept.Of(twin.everything.Display);
                const int frames = 6;
                for (int f = 0; f < frames; f++) CollectBoth(twin, "standing " + f);
                Kept kept = Kept.Of(twin.kept.Display).Since(before), everything = Kept.Of(twin.everything.Display).Since(everythingBefore);
                TestContext.Out.WriteLine("16 standing bodies, " + frames + " collections; kept: " + kept);
                TestContext.Out.WriteLine("16 standing bodies, " + frames + " collections; collecting everything: " + everything);
                Assert.That(kept.reads, Is.EqualTo(0), "no registration is read from the ledger");
                Assert.That(kept.lists, Is.EqualTo(0), "the registration list is not made again");
                Assert.That(kept.walks, Is.EqualTo(0), "no structure is walked");
                Assert.That(kept.keptWhole, Is.EqualTo(frames), "every build kept its structure whole");
                Assert.That(kept.arrangements, Is.EqualTo(0), "what each registration is drawn as is not worked out again");
                Assert.That(kept.assemblies, Is.EqualTo(0), "no side is assembled");
                Assert.That(kept.written, Is.EqualTo(0), "no instance record is written");
                Assert.That(kept.argumentTransfers + kept.instanceTransfers, Is.EqualTo(0), "nothing is sent to the GPU");
                Assert.That(everything.reads, Is.EqualTo(16L * frames), "collecting everything reads every registration every time");
                Assert.That(everything.assemblies, Is.EqualTo(frames));
                Assert.That(everything.instanceElements, Is.EqualTo(16L * frames), "and sends every instance every time");
                Assert.That(twin.kept.Display.SettledCollections, Is.EqualTo(twin.everything.Display.SettledCollections));
            }
        }

        [Test]
        public void KeptCollection_OneMovingBody_WritesAndSendsOnlyIt_AndTheOthersAreNeverRolledBack()
        {
            using (Twin twin = NewTwin(16))
            {
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);

                // A different body moves each frame; then none; the sides of the draw data trade places every frame, so
                // a body moved two frames ago must still stand where it was moved to.
                int[] moving = { 2, 9, 5, -1, 14, -1, -1, 0, 15 };
                var where = new Dictionary<int, Matrix4x4>();
                for (int f = 0; f < moving.Length; f++)
                {
                    int body = moving[f];
                    Kept before = Kept.Of(twin.kept.Display);
                    if (body >= 0)
                    {
                        Matrix4x4 to = Stand(body, 0.5f + f, 20f * f);
                        where[body] = to;
                        Both(twin, run => run.at.Put(run.bodies[body], to));
                    }

                    CollectBoth(twin, "frame " + f + (body >= 0 ? ", body " + body + " moved" : ", nothing moved"));
                    Kept kept = Kept.Of(twin.kept.Display).Since(before);
                    Assert.That(kept.reads + kept.walks + kept.assemblies + kept.arrangements, Is.EqualTo(0), "frame " + f + ": nothing of the structure is done again: " + kept);
                    Assert.That(kept.written, Is.EqualTo(body >= 0 ? 1 : 0), "frame " + f + ": only the moved body's instance is written");
                    Assert.That(kept.instanceElements, Is.EqualTo(body >= 0 ? 1 : 0), "frame " + f + ": and only it is sent");
                    Assert.That(kept.argumentTransfers, Is.EqualTo(0), "frame " + f + ": the commands are not sent");

                    // Every body stands where it was last put, on the adopted side and on the GPU (read back above).
                    for (int i = 0; i < 16; i++)
                    {
                        Matrix4x4 expect = where.TryGetValue(i, out Matrix4x4 moved) ? moved : Stand(i);
                        Assert.That(twin.kept.Display.InstanceTransformForTest(i).Equals(expect), Is.True, "frame " + f + ": body " + i + " stands where it was last put");
                    }
                }
            }
        }

        [Test]
        public void KeptCollection_TwoBodiesMoved_SendOneRange_WithWhatLiesBetween_FarApartOrCloseTogether()
        {
            using (Twin twin = NewTwin(16))
            {
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                Kept before = Kept.Of(twin.kept.Display);
                Both(twin, run => run.at.Put(run.bodies[2], Stand(2, 4f)).Put(run.bodies[13], Stand(13, -2f, 45f)));
                CollectBoth(twin, "bodies 2 and 13 moved");
                Kept kept = Kept.Of(twin.kept.Display).Since(before);
                TestContext.Out.WriteLine("two of 16 moved, far apart: " + kept);
                Assert.That(kept.written, Is.EqualTo(2), "two instance records written");
                Assert.That(kept.instanceTransfers, Is.EqualTo(1), "one update");
                Assert.That(kept.instanceElements, Is.EqualTo(12), "one range, 2..13: the ten between them are sent along (the instance records go as one range, DESIGN 5.6)");
                Assert.That(kept.instanceCalls, Is.EqualTo(2), "the transforms and the clips of the one range");
                Assert.That(kept.argumentElements + kept.wholeArguments + kept.wholeInstances, Is.EqualTo(0), "no command, and nothing whole");

                // The next frame nothing moves; the other side is given that range and nothing is sent.
                before = Kept.Of(twin.kept.Display);
                CollectBoth(twin, "the frame after");
                kept = Kept.Of(twin.kept.Display).Since(before);
                Assert.That(kept.caughtUp, Is.EqualTo(12), "the other side is given the range it was not written over, whole");
                Assert.That(kept.written + kept.instanceElements, Is.EqualTo(0), "and nothing is written or sent");
                CollectBoth(twin, "two frames after");

                // Two close together: the one range again, and the record between them goes along -- from the side being
                // built, which holds it as it stands (read back by CollectBoth).
                before = Kept.Of(twin.kept.Display);
                Both(twin, run => run.at.Put(run.bodies[5], Stand(5, 1f)).Put(run.bodies[7], Stand(7, 2f, 20f)));
                CollectBoth(twin, "bodies 5 and 7 moved");
                kept = Kept.Of(twin.kept.Display).Since(before);
                TestContext.Out.WriteLine("two of 16 moved, close together: " + kept);
                Assert.That(kept.written, Is.EqualTo(2), "two instance records written");
                Assert.That(kept.instanceElements, Is.EqualTo(3), "one range, 5..7: the one between sent along");
                Assert.That(kept.instanceCalls, Is.EqualTo(2), "the transforms and the clips of the one range");
                CollectBoth(twin, "at rest");
            }
        }

        private static (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) CutAndPlace(Run run, int body, float4 plane, Matrix4x4 at)
        {
            (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) made = Cut(run.scene.ledger, run.bodies[body], plane);
            run.at.Put(made.positive, at).Put(made.negative, at);
            return made;
        }

        private static bool CommitBothSides(Run run, int body, CutOperationId cut, float4 plane, LogicalFragmentId positive, LogicalFragmentId negative)
        {
            Assert.That(VpStorageCutInput.TryAcquire(run.scene.storage, run.geometries[body], out VpStorageCutInput input), Is.True, "read the geometry");
            using (input)
            {
                Assert.That(VpStorageCut.TryExecute(run.scene.storage, input, plane, out VpStorageCutResult result), Is.True, "cut it");
                Assert.That(result.positive.IsProduced && result.negative.IsProduced, Is.True, "both sides produced");
                return run.Display.TryCommitCut(
                    run.bodies[body], cut, new Vector4(plane.x, plane.y, plane.z, plane.w), positive, in result.positive, negative,
                    in result.negative, result.kernel.capTriangles);
            }
        }

        [Test]
        public void KeptCollection_DrawsWhatCollectingEverythingDraws_ThroughCutsMovesCommitsRetirementsAndNewBodies()
        {
            using (Twin twin = NewTwin(12, 64, true))
            {
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);

                // A cut admitted and prepared, then published: its two sides are drawn clipped, where their own
                // placements say; then one side moves for several frames, its clip and its cap with it.
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                CutOperationId admitted = default;
                Both(twin, run => admitted = Admit(run.scene.ledger, run.bodies[4], plane));
                CollectBoth(twin, "a cut admitted and prepared");
                LogicalFragmentId positive = default, negative = default;
                Both(twin, run =>
                {
                    Assert.That(run.scene.ledger.Publish(admitted, out positive, out negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    run.at.Put(positive, Stand(4)).Put(negative, Stand(4));
                });
                CollectBoth(twin, "the cut published");
                CollectBoth(twin, "the frame after the publication");
                CollectBoth(twin, "two frames after");
                Kept before = Kept.Of(twin.kept.Display);
                for (int f = 0; f < 4; f++)
                {
                    Both(twin, run => run.at.Put(positive, Stand(4, 0.3f * (f + 1), 15f * f)));
                    CollectBoth(twin, "the positive side moved, frame " + f);
                }

                Kept moving = Kept.Of(twin.kept.Display).Since(before);
                TestContext.Out.WriteLine("a clipped side moving for 4 frames among 13 instances: " + moving);
                Assert.That(moving.walks + moving.assemblies + moving.reads, Is.EqualTo(0), "moving a clipped side does nothing of the structure again");
                Assert.That(twin.kept.Display.CapRecordCount, Is.GreaterThan(0), "caps are drawn");

                // An unclipped body moves in the same frames as the clipped one stands.
                Both(twin, run => run.at.Put(run.bodies[10], Stand(10, 1f)));
                CollectBoth(twin, "an unclipped body moved beside the clipped ones");

                // The geometry commit: the registration is replaced by the two sides' own.
                _frame++;
                Both(twin, run => Assert.That(CommitBothSides(run, 4, admitted, plane, positive, negative), Is.True, "committed"));
                CollectBoth(twin, "the commit");
                CollectBoth(twin, "the frame after the commit");
                CollectBoth(twin, "two frames after the commit");
                Both(twin, run => run.at.Put(negative, Stand(4, -1f)));
                CollectBoth(twin, "a committed side moved");

                // A body retired: it is let go, and the ones after it close up.
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[7]), Is.True));
                CollectBoth(twin, "body 7 retired");
                CollectBoth(twin, "the frame after the retirement");
                CollectBoth(twin, "two frames after the retirement");
                Both(twin, run => run.at.Put(run.bodies[11], Stand(11, 2f)));
                CollectBoth(twin, "the last body moved after the others closed up");

                // A new body taken in.
                _frame++;
                Both(twin, run => AddBody(run, Stand(20)));
                for (int f = 0; f < 3; f++) CollectBoth(twin, "a body taken in, frame " + f);
                Both(twin, run => run.at.Put(run.bodies[12], Stand(20, 3f)).Put(run.bodies[0], Stand(0, 3f)));
                CollectBoth(twin, "the new body and the first moved");

                // A second cut, left uncommitted while a far body is cut and retired piece by piece.
                var second = Normalized(new float4(1f, 0.3f, 0f, 0.2f));
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) late = default;
                Both(twin, run => late = CutAndPlace(run, 1, second, Stand(1)));
                CollectBoth(twin, "a second cut published");
                Both(twin, run => Assert.That(run.scene.ledger.Retire(late.negative), Is.True));
                CollectBoth(twin, "its negative side retired");
                CollectBoth(twin, "the frame after");
                Both(twin, run => run.at.Put(late.positive, Stand(1, 0.7f, 30f)));
                CollectBoth(twin, "its positive side moved");
                CollectBoth(twin, "at rest");
                CollectBoth(twin, "at rest again");
                Assert.That(twin.kept.Display.SideCount, Is.EqualTo(twin.everything.Display.SideCount));
            }
        }

        [Test]
        public void KeptCollection_ACutInOneFamily_ReadsOnlyThatFamilysRegistrations()
        {
            using (Twin twin = NewTwin(16))
            {
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                Kept before = Kept.Of(twin.kept.Display);
                var plane = Normalized(new float4(0f, 1f, 0f, 0f));
                CutOperationId cut = default;
                Both(twin, run => cut = Admit(run.scene.ledger, run.bodies[6], plane));
                CollectBoth(twin, "a cut admitted on one body");
                Kept kept = Kept.Of(twin.kept.Display).Since(before);
                TestContext.Out.WriteLine("one of 16 families changed: " + kept);
                Assert.That(kept.reads, Is.EqualTo(1), "only the registration of the family the ledger named is read");
                Assert.That(kept.lists, Is.EqualTo(0), "the registration list stands");

                // More changes than the ledger keeps the families of: everything is read again, once.
                before = Kept.Of(twin.kept.Display);
                Both(twin, run =>
                {
                    for (int i = 0; i < 1100; i++) run.scene.ledger.NoteOwnershipChanged(run.bodies[3]);
                });
                CollectBoth(twin, "more changes than are kept");
                kept = Kept.Of(twin.kept.Display).Since(before);
                Assert.That(kept.reads, Is.EqualTo(16), "every registration is read again");
                before = Kept.Of(twin.kept.Display);
                CollectBoth(twin, "the frame after");
                CollectBoth(twin, "two frames after");
                CollectBoth(twin, "three frames after");
                kept = Kept.Of(twin.kept.Display).Since(before);
                Assert.That(kept.reads, Is.EqualTo(0), "and then none");
            }
        }

        [Test]
        public void KeptCollection_AGpuBatchReplacedByALargerOne_IsSentEverything()
        {
            // A first room of four instances: the fifth body grows the room and the GPU batch with it.
            using (Twin twin = NewTwin(4, 4, false, true))
            {
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                VpIndexedIndirectDrawBatch first = twin.kept.Display.BodyBatchForTest;
                Both(twin, run => run.at.Put(run.bodies[1], Stand(1, 1f)));
                CollectBoth(twin, "a body moved in the first room");
                _frame++;
                Both(twin, run => { for (int i = 4; i < 11; i++) AddBody(run, Stand(i)); });
                CollectBoth(twin, "seven more bodies: the room and the batch grown");
                Assert.That(twin.kept.Display.BodyBatchForTest, Is.Not.SameAs(first), "the batch was replaced by a larger one");
                Assert.That(twin.kept.Display.BodyBatchForTest.InstanceCount, Is.EqualTo(11));
                for (int f = 0; f < 3; f++) CollectBoth(twin, "after the growth, frame " + f);
                Both(twin, run => run.at.Put(run.bodies[9], Stand(9, 2f)).Put(run.bodies[0], Stand(0, -1f)));
                CollectBoth(twin, "bodies moved in the larger room");
                CollectBoth(twin, "at rest");
            }
        }

        [Test]
        public void KeptCollection_ACollectionRefused_LeavesWhatIsAdoptedAndWhatTheGpuHolds()
        {
            using (Twin twin = NewTwin(4, 4, false, true))
            {
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                Both(twin, run => run.at.Put(run.bodies[2], Stand(2, 1f)));
                CollectBoth(twin, "a body moved");
                VpLogicalCutDisplay display = twin.kept.Display;
                Drawn before = Capture(display);
                var transforms = new List<Matrix4x4>();
                for (int i = 0; i < display.SideCount; i++) transforms.Add(display.InstanceTransformForTest(i));

                // A body moves and more bodies arrive than the room holds, and the room cannot be made larger: the
                // collection is refused. What was adopted, and what the GPU holds, are what they were.
                _frame++;
                twin.kept.at.Put(twin.kept.bodies[1], Stand(1, 5f));
                for (int i = 4; i < 9; i++) AddBody(twin.kept, Stand(i));
                display.FailRoomAllocationForTest = () => true;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "the collection is refused");
                Drawn after = Capture(display);
                Assert.That(after.sides.Count, Is.EqualTo(before.sides.Count), "as many instances as were adopted");
                for (int i = 0; i < before.sides.Count; i++)
                {
                    Assert.That(after.sides[i].Equals(before.sides[i]), Is.True, "side " + i + " is as it was adopted");
                    Assert.That(display.InstanceTransformForTest(i).Equals(transforms[i]), Is.True, "instance " + i + " stands where it was adopted");
                }

                AssertGpuHoldsWhatIsAdopted(display, "after the refused collection");
            }
        }

        [Test]
        public void KeptCollection_DrawsTheSamePicture_WithClippedAndUnclippedBodies()
        {
            using (Twin twin = NewTwin(3))
            {
                // Three cubes side by side; the middle one cut and its upper side lifted, so its clip and its cap show.
                var plane = Normalized(new float4(0f, 1f, 0f, 0f));
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) made = default;
                Both(twin, run => made = CutAndPlace(run, 1, plane, Stand(1)));
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                Both(twin, run => run.at.Put(made.positive, Stand(1, 0.6f)).Put(run.bodies[2], Stand(2, 0.4f, 30f)));
                CollectBoth(twin, "a clipped side and an unclipped body moved");
                CollectBoth(twin, "at rest");
                Both(twin, run => run.at.Put(run.bodies[0], Stand(0, -0.3f)));
                CollectBoth(twin, "another unclipped body moved");

                Camera keptCamera = Looking(new Vector3(3f, 2.5f, -8f), new Vector3(0f, -0.25f, 1f), 6f);
                Camera everythingCamera = Looking(new Vector3(3f, 2.5f, -8f), new Vector3(0f, -0.25f, 1f), 6f);
                Color32[] kept = Draw(twin.kept.Display, keptCamera);
                Color32[] everything = Draw(twin.everything.Display, everythingCamera);
                Assert.That(kept.Length, Is.EqualTo(everything.Length));
                int differing = 0, drawn = 0;
                for (int p = 0; p < kept.Length; p++)
                {
                    if (kept[p].r != everything[p].r || kept[p].g != everything[p].g || kept[p].b != everything[p].b || kept[p].a != everything[p].a) differing++;
                    if (kept[p].r != kept[0].r || kept[p].g != kept[0].g || kept[p].b != kept[0].b) drawn++;
                }

                TestContext.Out.WriteLine("pixels " + kept.Length + ", unlike the corner's (drawn) " + drawn + ", differing between the two displays " + differing);
                Assert.That(drawn, Is.GreaterThan(kept.Length / 50), "the bodies are in the picture");
                Assert.That(differing, Is.EqualTo(0), "the same picture, pixel for pixel");
            }
        }

        [Test]
        public void KeptCollection_TheStereoConditionChanged_SendsTheArgumentsAgain_AndStandingUnderItSendsNothing()
        {
            using (Twin twin = NewTwin(8))
            {
                // One body cut and not committed, so that a command of two instances is among them.
                var plane = Normalized(new float4(0f, 1f, 0f, 0f));
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) made = default;
                Both(twin, run => made = CutAndPlace(run, 2, plane, Stand(2)));
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                VpLogicalCutDisplay display = twin.kept.Display;
                Assert.That(display.DrawsSinglePassInstanced, Is.False, "not single pass instanced at first");

                // Single pass instanced comes on with nothing else changed: the arguments are sent again (the forward
                // ones doubled -- read back by CollectBoth), and no instance record is.
                Kept before = Kept.Of(display);
                Both(twin, run => run.Display.SinglePassInstanced = true);
                CollectBoth(twin, "single pass instanced came on");
                Kept kept = Kept.Of(display).Since(before);
                TestContext.Out.WriteLine("single pass instanced came on, nothing else changed: " + kept);
                Assert.That(display.DrawsSinglePassInstanced, Is.True, "the batch draws single pass instanced");
                Assert.That(kept.wholeArguments, Is.EqualTo(display.DrawCommandEnd), "the arguments are sent again, once: every command slot's, counted as sent whole");
                Assert.That(kept.argumentTransfers + kept.argumentElements, Is.EqualTo(0), "and not as a change");
                Assert.That(kept.wholeInstances, Is.EqualTo(0), "no instance record is sent whole for it: only what the placement pass placed anew");
                Assert.That(kept.walks + kept.assemblies + kept.reads, Is.EqualTo(0), "nothing of the structure is done again for it");

                // Standing under it: nothing is sent.
                CollectBoth(twin, "the frame after");
                before = Kept.Of(display);
                for (int f = 0; f < 4; f++) CollectBoth(twin, "standing under single pass instanced " + f);
                kept = Kept.Of(display).Since(before);
                Assert.That(kept.argumentTransfers + kept.wholeArguments, Is.EqualTo(0), "standing: the arguments are not sent");
                Assert.That(kept.capNormalCalls, Is.EqualTo(0), "nor the cap normals");

                // A body moved under it: its record, and no arguments.
                before = Kept.Of(display);
                Both(twin, run => run.at.Put(run.bodies[0], Stand(0, 1f, 30f)));
                CollectBoth(twin, "a body moved under single pass instanced");
                kept = Kept.Of(display).Since(before);
                Assert.That(kept.argumentTransfers + kept.wholeArguments, Is.EqualTo(0), "a move sends no arguments");
                Assert.That(kept.written, Is.GreaterThanOrEqualTo(1), "its record is written");
                Assert.That(kept.instanceTransfers, Is.EqualTo(1), "and sent");

                // And off again.
                before = Kept.Of(display);
                Both(twin, run => run.Display.SinglePassInstanced = false);
                CollectBoth(twin, "single pass instanced went off");
                kept = Kept.Of(display).Since(before);
                Assert.That(kept.wholeArguments, Is.EqualTo(display.DrawCommandEnd), "the arguments are sent again");
                Assert.That(kept.wholeInstances, Is.EqualTo(0), "and no instance record with them");
                CollectBoth(twin, "at rest");
            }
        }

        [Test]
        public void KeptCollection_ThePlacementPassReadsByRegistration_WhatASearchFinds_ThroughSplitsRetirementsAndClosingUp()
        {
            using (Twin twin = NewTwin(10, 64, true))
            {
                long looked = 0, offset = 0, notFirst = 0, snapshots = 0;
                var seen = new HashSet<VpMultiCutSnapshot>();

                // A collection of both, then each display's adopted snapshot asked (the two snapshots of a display trade
                // places every collection, so both are asked in turn).
                void Step(string what)
                {
                    CollectBoth(twin, what);
                    foreach (Run run in new[] { twin.kept, twin.everything })
                    {
                        VpMultiCutSnapshot adopted = run.Display.AdoptedSnapshot;
                        int differing = adopted.PlacementReadingsDifferingForTest(out int l, out int o, out int n);
                        Assert.That(differing, Is.EqualTo(0), what + ": every reading by the registration is the searched one");
                        Assert.That(l, Is.EqualTo(adopted.RenderFragmentCount), what + ": every render fragment was looked at");
                        looked += l; offset += o; notFirst += n; snapshots++;
                        seen.Add(adopted);
                    }
                }

                for (int f = 0; f < 3; f++) Step("settling " + f);

                // Body 2 cut, then its positive side cut again: one registration of three render fragments.
                var across = Normalized(new float4(0f, 1f, 0f, 0f));
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) once = default, twice = default;
                Both(twin, run => once = CutAndPlace(run, 2, across, Stand(2)));
                Step("body 2 cut");
                var along = Normalized(new float4(1f, 0f, 0f, 0.2f));
                Both(twin, run =>
                {
                    twice = Cut(run.scene.ledger, once.positive, along);
                    run.at.Put(twice.positive, Stand(2)).Put(twice.negative, Stand(2));
                });
                Step("its positive side cut again");
                Step("the frame after");
                Assert.That(twin.kept.Display.AdoptedSnapshot.RenderFragmentCount, Is.EqualTo(12), "ten registrations, one of them of three render fragments");
                Both(twin, run => run.at.Put(twice.negative, Stand(2, 0.5f, 20f)).Put(run.bodies[7], Stand(7, 1f)));
                Step("a piece of the thrice-cut body and a later body moved");
                Step("at rest");

                // Body 6 cut and its geometry committed: its registration is replaced by its two sides' own.
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                CutOperationId admitted = default;
                Both(twin, run => admitted = Admit(run.scene.ledger, run.bodies[6], plane));
                Step("a cut admitted on body 6");
                LogicalFragmentId positive = default, negative = default;
                Both(twin, run =>
                {
                    Assert.That(run.scene.ledger.Publish(admitted, out positive, out negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    run.at.Put(positive, Stand(6)).Put(negative, Stand(6));
                });
                Step("published");
                _frame++;
                Both(twin, run => Assert.That(CommitBothSides(run, 6, admitted, plane, positive, negative), Is.True, "committed"));
                Step("the commit: one registration became two");
                Step("the frame after the commit");
                Both(twin, run => run.at.Put(negative, Stand(6, -1f, 10f)));
                Step("a committed side moved");

                // The first body retired: every later registration's part moves down one, and its render fragments with it.
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[0]), Is.True));
                Step("the first body retired");
                Step("the frame after the retirement");
                Both(twin, run => run.at.Put(twice.positive, Stand(2, -0.4f, -30f)).Put(run.bodies[9], Stand(9, 0.6f)));
                Step("pieces moved after the registrations closed up");

                // One piece of the thrice-cut body retired, then a new body taken in.
                Both(twin, run => Assert.That(run.scene.ledger.Retire(once.negative), Is.True));
                Step("a piece of the thrice-cut body retired");
                Step("the frame after");
                _frame++;
                Both(twin, run => AddBody(run, Stand(20)));
                for (int f = 0; f < 3; f++) Step("a body taken in, frame " + f);
                Both(twin, run => run.at.Put(run.bodies[10], Stand(20, 2f)).Put(twice.negative, Stand(2, 1.5f, 45f)));
                Step("the new body and a piece moved");
                Step("at rest");

                TestContext.Out.WriteLine("snapshots asked " + snapshots + " (" + seen.Count + " snapshot objects), render fragments looked at " + looked
                    + ", of a part not beginning at zero " + offset + ", not the first of their part " + notFirst + "; readings by the registration differing from the searched ones 0");
                Assert.That(seen.Count, Is.EqualTo(4), "both snapshots of both displays were asked");
                Assert.That(offset, Is.GreaterThan(0), "render fragments whose place inside their part is not their number were among them");
                Assert.That(notFirst, Is.GreaterThan(0), "and render fragments that are not the first of their part");
            }
        }

        // The caps of a display, as (first vertex, vertices, plane) in order: to tell which caps a step changed.
        private static List<(int start, int count, float4 plane)> CapsOf(VpLogicalCutDisplay display)
        {
            var caps = new List<(int start, int count, float4 plane)>();
            VpMultiCutSnapshot adopted = display.AdoptedSnapshot;
            for (int c = 0; c < adopted.CapCount; c++)
            {
                adopted.TryGetCap(c, out VpMultiCutCap cap);
                caps.Add((cap.vertexStart, cap.vertexCount, cap.worldPlane));
            }

            return caps;
        }

        [Test]
        public void KeptCollection_StandingCaps_MakeAndSendNoCapNormals_AndCapsThatChangeSendOneRange()
        {
            using (Twin twin = NewTwin(8))
            {
                // Bodies 1, 3 and 6 cut and not committed: six sides, a cap each.
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                var cuts = new (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative)[3];
                int[] cutBodies = { 1, 3, 6 };
                for (int k = 0; k < cutBodies.Length; k++)
                {
                    int body = cutBodies[k];
                    Both(twin, run => cuts[k] = CutAndPlace(run, body, plane, Stand(body)));
                }

                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                VpLogicalCutDisplay display = twin.kept.Display;
                int capVertices = display.CapNormalCount;
                Assert.That(display.AdoptedSnapshot.CapCount, Is.EqualTo(6), "six caps");
                Assert.That(capVertices, Is.GreaterThanOrEqualTo(18), "each with vertices");

                // Standing: nothing of the cap normals is made or sent; collecting everything makes and sends them all.
                Kept before = Kept.Of(display), everythingBefore = Kept.Of(twin.everything.Display);
                const int frames = 5;
                for (int f = 0; f < frames; f++) CollectBoth(twin, "standing " + f);
                Kept kept = Kept.Of(display).Since(before), everything = Kept.Of(twin.everything.Display).Since(everythingBefore);
                TestContext.Out.WriteLine("6 standing caps (" + capVertices + " vertices), " + frames + " collections; kept: " + kept);
                TestContext.Out.WriteLine("6 standing caps (" + capVertices + " vertices), " + frames + " collections; collecting everything: " + everything);
                Assert.That(kept.capNormalCalls, Is.EqualTo(0), "standing caps: the cap normals' buffer is not written");
                Assert.That(kept.capNormalsMade, Is.EqualTo(0), "and no normal is made again");
                Assert.That(everything.capNormalCalls, Is.EqualTo(frames), "collecting everything writes it every time");
                Assert.That(everything.capNormalVertices, Is.EqualTo((long)frames * capVertices), "whole");

                // One side turns: its cap's normal changes, and only that cap's vertices are sent.
                List<(int start, int count, float4 plane)> capsBefore = CapsOf(display);
                before = Kept.Of(display);
                Both(twin, run => run.at.Put(cuts[1].positive, Stand(3, 0.4f, 25f)));
                CollectBoth(twin, "the positive side of the second cut turned");
                kept = Kept.Of(display).Since(before);
                List<(int start, int count, float4 plane)> capsAfter = CapsOf(display);
                Assert.That(capsAfter.Count, Is.EqualTo(capsBefore.Count));
                int changed = -1, changedCount = 0;
                for (int c = 0; c < capsAfter.Count; c++)
                {
                    Assert.That(capsAfter[c].start, Is.EqualTo(capsBefore[c].start), "cap " + c + " lies where it lay");
                    Assert.That(capsAfter[c].count, Is.EqualTo(capsBefore[c].count), "cap " + c + " has the vertices it had");
                    if (!capsAfter[c].plane.Equals(capsBefore[c].plane)) { changed = c; changedCount++; }
                }

                Assert.That(changedCount, Is.EqualTo(1), "one cap's plane changed");
                TestContext.Out.WriteLine("one cap of 6 turned (cap " + changed + ", " + capsAfter[changed].count + " vertices of " + capVertices + "): " + kept);
                Assert.That(kept.capNormalCalls, Is.EqualTo(1), "one write of the cap normals' buffer");
                Assert.That(kept.capNormalVertices, Is.EqualTo(capsAfter[changed].count), "of that cap's vertices only");

                // The frame after, nothing moves: nothing is sent (there is one buffer and one room: no second side to bring up).
                before = Kept.Of(display);
                CollectBoth(twin, "the frame after");
                kept = Kept.Of(display).Since(before);
                Assert.That(kept.capNormalCalls + kept.capNormalsMade, Is.EqualTo(0), "the frame after: nothing made, nothing sent");

                // The first cap's side and the last cap's side turn: one range, the caps between sent along.
                before = Kept.Of(display);
                capsBefore = CapsOf(display);
                Both(twin, run => run.at.Put(cuts[0].positive, Stand(1, 0.2f, 40f)).Put(cuts[0].negative, Stand(1, -0.2f, -15f))
                    .Put(cuts[2].positive, Stand(6, 0.3f, 10f)).Put(cuts[2].negative, Stand(6, -0.3f, 70f)));
                CollectBoth(twin, "both sides of the first and of the last cut turned");
                kept = Kept.Of(display).Since(before);
                capsAfter = CapsOf(display);
                int firstChanged = -1, lastChanged = -1;
                for (int c = 0; c < capsAfter.Count; c++)
                {
                    if (capsAfter[c].plane.Equals(capsBefore[c].plane)) continue;
                    if (firstChanged < 0) firstChanged = c;
                    lastChanged = c;
                }

                Assert.That(firstChanged, Is.EqualTo(0), "the first cap changed");
                Assert.That(lastChanged, Is.EqualTo(capsAfter.Count - 1), "and the last");
                TestContext.Out.WriteLine("the first two and the last two caps of 6 turned: " + kept);
                Assert.That(kept.capNormalCalls, Is.EqualTo(1), "one write");
                Assert.That(kept.capNormalVertices, Is.EqualTo(capVertices), "from the first changed cap to the end of the last: the two between sent along");

                // A cut published on a body before the others' (its caps come first: every later cap moves along), then
                // a side retired (the caps after it close up): the buffer holds the adopted caps' normals each time.
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) early = default;
                Both(twin, run => early = CutAndPlace(run, 0, Normalized(new float4(1f, 0.5f, 0f, 0.1f)), Stand(0)));
                before = Kept.Of(display);
                CollectBoth(twin, "a cut published on the first body");
                kept = Kept.Of(display).Since(before);
                Assert.That(display.AdoptedSnapshot.CapCount, Is.EqualTo(8));
                Assert.That(kept.capNormalVertices, Is.EqualTo(display.CapNormalCount), "every cap moved along: all are sent");
                CollectBoth(twin, "the frame after the cut");
                CollectBoth(twin, "two frames after the cut");
                before = Kept.Of(display);
                CollectBoth(twin, "three frames after the cut");
                kept = Kept.Of(display).Since(before);
                Assert.That(kept.capNormalCalls, Is.EqualTo(0), "standing again: nothing sent");
                Both(twin, run => Assert.That(run.scene.ledger.Retire(cuts[1].negative), Is.True));
                CollectBoth(twin, "a side retired");
                CollectBoth(twin, "the frame after the retirement");
                Both(twin, run => run.at.Put(cuts[2].negative, Stand(6, -0.5f, 5f)));
                CollectBoth(twin, "the last cap's side turned after the caps closed up");
                before = Kept.Of(display);
                CollectBoth(twin, "at rest");
                CollectBoth(twin, "at rest again");
                kept = Kept.Of(display).Since(before);
                Assert.That(kept.capNormalCalls, Is.EqualTo(0), "at rest: nothing sent");
            }
        }

        [Test]
        public void KeptCollection_ACapNormalBufferReplacedByALargerOne_IsSentEveryCapsNormals()
        {
            // A first room of two instances: its cap normals' buffer holds 2 x 8 caps x 14 vertices = 224 normals.
            var twin = new Twin { kept = new Run(), everything = new Run() };
            using (twin)
            {
                foreach (Run run in new[] { twin.kept, twin.everything })
                {
                    run.scene = NewReservedScene(2, 2, 2, 256, new VpLogicalCutDisplayLimits(256, 256, 256, 256), null, 64);
                    Assert.That(run.scene, Is.Not.Null, VpLogicalCutDisplay.LastCreationFailure);
                    run.Display.Placement = run.at;
                    _frame++;
                    for (int i = 0; i < 2; i++) AddBody(run, Stand(i));
                }

                twin.everything.Display.collectEverythingForTest = true;
                var plane = Normalized(new float4(0f, 1f, 0f, 0f));
                Both(twin, run => CutAndPlace(run, 0, plane, Stand(0)));
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                VpLogicalCutDisplay display = twin.kept.Display;
                GraphicsBuffer first = display.CapNormalBufferForTest;
                int firstRoom = first.count;
                Assert.That(display.CapNormalCount, Is.GreaterThan(0).And.LessThanOrEqualTo(firstRoom), "the first caps fit the first buffer");

                // Thirty-two more bodies, each cut: more cap vertices than the first buffer holds.
                _frame++;
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) twentieth = default;
                Both(twin, run =>
                {
                    for (int i = 2; i < 34; i++)
                    {
                        AddBody(run, Stand(i));
                        (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) made = CutAndPlace(run, i, plane, Stand(i));
                        if (i == 20) twentieth = made;
                    }
                });
                Kept before = Kept.Of(display);
                CollectBoth(twin, "thirty-two more cut bodies");
                Kept kept = Kept.Of(display).Since(before);
                TestContext.Out.WriteLine("cap normals' buffer of " + firstRoom + " replaced by one of " + display.CapNormalBufferForTest.count + "; "
                    + display.CapNormalCount + " cap vertices; " + kept);
                Assert.That(display.CapNormalCount, Is.GreaterThan(firstRoom), "more cap vertices than the first buffer held");
                Assert.That(display.CapNormalBufferForTest, Is.Not.SameAs(first), "the buffer was replaced by a larger one");
                Assert.That(kept.capNormalVertices, Is.EqualTo(display.CapNormalCount), "the new buffer was sent every cap's normals");

                // In the larger buffer: standing sends nothing; one side turned sends its cap.
                CollectBoth(twin, "the frame after");
                CollectBoth(twin, "two frames after");
                before = Kept.Of(display);
                CollectBoth(twin, "standing in the larger buffer");
                kept = Kept.Of(display).Since(before);
                Assert.That(kept.capNormalCalls, Is.EqualTo(0), "standing: nothing sent");
                before = Kept.Of(display);
                Both(twin, run => run.at.Put(twentieth.positive, Matrix4x4.TRS(new Vector3(60f, 0.5f, 0f), Quaternion.Euler(20f, 0f, 30f), Vector3.one)));
                CollectBoth(twin, "one side tilted in the larger buffer");
                kept = Kept.Of(display).Since(before);
                Assert.That(kept.capNormalCalls, Is.EqualTo(1), "one write");
                Assert.That(kept.capNormalVertices, Is.GreaterThan(0).And.LessThan(display.CapNormalCount), "of its cap, not of everything");
                CollectBoth(twin, "at rest");
            }
        }


    }

    /// <summary>The ledger's notice of which families changed (2026-10-05), by itself.</summary>
    public sealed class LogicalCutLedgerChangedFamiliesTests
    {
        [Test]
        public void TheLedger_NamesTheFamilyOfEveryChange_InOrder_AndSaysWhenItNoLongerCan()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64));
            LogicalFragmentId a = ledger.AddFragment(), b = ledger.AddFragment(), c = ledger.AddFragment();
            long start = ledger.Revision;
            var families = new List<LogicalFragmentId>();
            Assert.That(ledger.TryReadChangedFamilies(start, families), Is.True);
            Assert.That(families, Is.Empty, "nothing changed: nothing named");

            Assert.That(ledger.Admit(b, new float4(0f, 1f, 0f, 0f), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            ledger.NoteOwnershipChanged(a);
            Assert.That(ledger.Retire(positive), Is.True);
            long changes = ledger.Revision - start;
            Assert.That(ledger.TryReadChangedFamilies(start, families), Is.True);
            Assert.That(families.Count, Is.EqualTo(changes), "one entry per change counted");
            Assert.That(families[families.Count - 1], Is.EqualTo(b), "a piece's change is its family's: the retired piece is of b's");
            Assert.That(families.Contains(a) && families.Contains(b), Is.True);
            Assert.That(families.Contains(c), Is.False, "the family nothing happened to is not named");
            foreach (LogicalFragmentId family in families) Assert.That(family == a || family == b, Is.True);

            // From a later reading, only what came after it.
            long later = ledger.Revision;
            ledger.NoteOwnershipChanged(c);
            families.Clear();
            Assert.That(ledger.TryReadChangedFamilies(later, families), Is.True);
            Assert.That(families, Is.EqualTo(new[] { c }));

            // More changes than are kept: it says so and names nothing.
            for (int i = 0; i < 1024; i++) ledger.NoteOwnershipChanged(a);
            families.Clear();
            Assert.That(ledger.TryReadChangedFamilies(later, families), Is.False, "more changes since than are kept");
            Assert.That(families, Is.Empty);
            families.Clear();
            Assert.That(ledger.TryReadChangedFamilies(ledger.Revision - 1024, families), Is.True, "exactly as many as are kept");
            Assert.That(families.Count, Is.EqualTo(1024));
            Assert.That(ledger.TryReadChangedFamilies(-1, families), Is.False, "not a value read from this ledger");
            Assert.That(ledger.TryReadChangedFamilies(ledger.Revision + 1, families), Is.False);
        }
    }
}
