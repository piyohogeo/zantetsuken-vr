using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The product's page backing with switches and counts, for the display's room: a reservation refused after a number
    /// of them, a commit refused past a number of bytes per reservation, and every reservation matched with its release.
    /// A reservation is counted by being alive, not by its address: the system may give a released address out again,
    /// and that is a new reservation, released once like any other.
    /// </summary>
    internal sealed class CountingPageBacking : IVpPageBacking
    {
        private readonly Dictionary<IntPtr, long> _live = new Dictionary<IntPtr, long>();

        /// <summary>Reservations made.</summary>
        public int Reserves;

        /// <summary>Reservations given back, each while it was alive.</summary>
        public int Releases;

        /// <summary>Releases of an address that was not a live reservation.</summary>
        public int ReleasedTwice;

        public int Commits;
        public int RefuseReservesFrom = int.MaxValue;
        public long CommitLimitBytes = long.MaxValue;
        public long GranularityBytes = VpWindowsPageBacking.Instance.Granularity;

        public long Granularity => GranularityBytes;

        /// <summary>Reservations alive now.</summary>
        public int Live => _live.Count;

        public bool TryReserve(long bytes, out IntPtr address, out string failure)
        {
            address = IntPtr.Zero;
            if (Reserves >= RefuseReservesFrom)
            {
                failure = "test: reservation refused";
                return false;
            }

            if (!VpWindowsPageBacking.Instance.TryReserve(bytes, out address, out failure))
            {
                return false;
            }

            Reserves++;
            _live.Add(address, bytes);
            return true;
        }

        public bool TryCommit(IntPtr at, long bytes, out string failure)
        {
            foreach (KeyValuePair<IntPtr, long> reservation in _live)
            {
                long offset = (long)at - (long)reservation.Key;
                if (offset >= 0 && offset < reservation.Value)
                {
                    if (offset + bytes > CommitLimitBytes)
                    {
                        failure = "test: commit past " + CommitLimitBytes + " bytes refused";
                        return false;
                    }

                    if (!VpWindowsPageBacking.Instance.TryCommit(at, bytes, out failure))
                    {
                        return false;
                    }

                    Commits++;
                    return true;
                }
            }

            failure = "test: a commit outside every live reservation";
            return false;
        }

        public void Release(IntPtr address, long bytes)
        {
            if (!_live.Remove(address))
            {
                ReleasedTwice++;
                return;
            }

            Releases++;
            VpWindowsPageBacking.Instance.Release(address, bytes);
        }
    }

    /// <summary>
    /// The display's room on reserved address space (TL, 2026-10-05): reserved from the limits, committed for the first
    /// room and written before the display is handed over; grown by committing more behind the same base; given back
    /// once each, whichever way the display ends. The sizes are checked by items and by bytes, the failures are made on
    /// purpose by a page backing with switches, and what a snapshot builds on reserved room is compared with what one
    /// in managed arrays builds.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        private const int Granule = 64 * 1024;

        /// <summary>A display on <paramref name="pages"/> (null: the system's), over a storage and a table large enough for <paramref name="bodies"/> cubes; null when it could not be made.</summary>
        private Scene NewReservedScene(
            int commands, int instances, int branches, int candidates, VpLogicalCutDisplayLimits limits, IVpPageBacking pages,
            int bodies = 16, int chainDepth = VpDisplayTestCapacities.ChainDepth)
        {
            var scene = new Scene
            {
                storage = new VpCpuGeometryStorage(
                    Math.Max(8192, bodies * 48), Math.Max(32768, bodies * 96), Math.Max(64, bodies + 16), Math.Max(256, (bodies * 2) + 16),
                    Math.Max(256, (bodies * 2) + 16), Allocator.Persistent),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64)),
                endFrame = () => _frame++,
            };
            scene.table = new VpGeometryReferenceTable(
                scene.storage, Math.Max(16, bodies + 16), Math.Max(2, instances), Math.Max(64, bodies + 16), Math.Max(64, limits.instances));
            if (!VpLogicalCutDisplay.TryCreateCore(
                    scene.storage, scene.table, scene.ledger, Materials(), null, null, commands, instances, branches, candidates,
                    chainDepth, VpStencilTestSettings.Create(4), () => _frame, scene.storage.CommittedVertexCapacity,
                    scene.storage.CommittedIndexCapacity, out scene.display, limits, pages))
            {
                scene.storage.Dispose();
                return null;
            }

            return scene;
        }

        private static Dictionary<string, VpRoomLine> LinesOf(VpLogicalCutDisplay display)
        {
            var lines = new List<VpRoomLine>();
            display.DescribeRooms(lines);
            var byName = new Dictionary<string, VpRoomLine>();
            foreach (VpRoomLine line in lines)
            {
                byName.Add(line.name, line);
            }

            return byName;
        }

        private void ShowBodies(Scene scene, int count)
        {
            // A frame the display has settled takes no new body: the bodies are shown in the next one.
            _frame++;
            for (int i = 0; i < count; i++)
            {
                Assert.That(
                    scene.display.TryShow(
                        scene.ledger.AddFragment(), AppendCube(scene.storage, false),
                        Matrix4x4.Translate(new Vector3((i % 32) * 2f, 0f, (i / 32) * 2f))),
                    Is.True, "body " + i);
            }
        }

        /// <summary>
        /// The product's own numbers -- a first room of 1024 each, limits of 65536 each -- by items and by bytes: every
        /// derived count carries its multiplier (eight caps a render fragment, fourteen vertices and twelve fanned
        /// triangles a cap, six vertices a section), the reservation is the limit's and the first commit the first
        /// room's, to the page granularity, and the GPU buffers are the first room's too. The whole room is written out.
        /// </summary>
        [Test]
        public void TheRoomIsReservedFromTheLimits_AndCommittedForTheFirstRoom_ByItemsAndBytes()
        {
            const int n = 1024;
            const int limit = 65536;
            var limits = new VpLogicalCutDisplayLimits(limit, limit, limit, limit);
            using (Scene scene = NewReservedScene(n, n, n, n, limits, null, 16, 256))
            {
                Assert.That(scene, Is.Not.Null, VpLogicalCutDisplay.LastCreationFailure);
                VpLogicalCutDisplay display = scene.display;
                Camera roomCamera = Oblique();
                long cameraBegin = Stopwatch.GetTimestamp();
                Assert.That(display.TryRegisterCamera(roomCamera), Is.True);
                double cameraMs = (Stopwatch.GetTimestamp() - cameraBegin) * 1000.0 / Stopwatch.Frequency;
                Dictionary<string, VpRoomLine> lines = LinesOf(display);

                void Expect(string name, long items, int itemBytes, long reservedItems)
                {
                    Assert.That(lines.ContainsKey(name), Is.True, name);
                    VpRoomLine line = lines[name];
                    Assert.That(line.native, Is.True, name + ": on reserved address space");
                    Assert.That(line.length, Is.EqualTo(items), name + ": items in use");
                    Assert.That(line.itemBytes, Is.EqualTo(itemBytes), name + ": bytes an item");
                    Assert.That(line.reservedLength, Is.EqualTo(reservedItems), name + ": items reserved for");
                }

                // The display's own, by the multipliers.
                Expect("display.capNormals", 112L * n, 16, 112L * limit);
                Expect("display.stencilCommands", 8L * n, UnsafeSize<VpIndirectCommand>(), 8L * limit);
                Expect("display.stencilTransforms", 8L * n, 64, 8L * limit);
                Expect("display.stencilClips", 8L * n, 132, 8L * limit);
                Expect("display.capIndices", 288L * n, 4, 288L * limit);
                foreach (string side in new[] { "", "'" })
                {
                    Expect("display.commands" + side, n, UnsafeSize<VpIndirectCommand>(), limit);
                    Expect("display.commandProvisional" + side, n, 1, limit);
                    Expect("display.transforms" + side, n, 64, limit);
                    Expect("display.clips" + side, n, 132, limit);
                    Expect("display.roots" + side, n, 4, limit);
                    Expect("display.rfCommandStart" + side, n, 4, limit);
                    Expect("display.rfCommandCount" + side, n, 4, limit);
                    Expect("display.rfTransform" + side, n, 64, limit);
                    Expect("display.capRecords" + side, 8L * n, UnsafeSize<LogicalCutCapRecord>(), 8L * limit);
                    Expect("snapshot" + side + ".branches", n, UnsafeSize<VpMultiCutBranch>(), limit);
                    Expect("snapshot" + side + ".renderFragments", n, UnsafeSize<VpMultiCutRenderFragment>(), limit);
                    Expect("snapshot" + side + ".capVertices", 112L * n, 12, 112L * limit);
                    Expect("snapshot" + side + ".sectionVertices", 48L * n, 12, 48L * limit);
                    Expect("snapshot" + side + ".capSection", 8L * n, 4, 8L * limit);
                    Expect("snapshot" + side + ".stack", (2L * n) + 2, 4, (2L * limit) + 2);
                }

                Expect("capJobs.colours", 8L * n, UnsafeSize<VpCapJobColour>(), 8L * limit);
                Expect("capJobs.leftPoints", 48L * n, 8, 48L * limit);
                Expect("capJobs.rightPoints", 48L * n, 8, 48L * limit);
                Expect("capJobs.groupOfJob", 8L * n, 4, 8L * limit);
                Expect("capJobs.lastRenderFragments", n, 4, limit);

                // A batch is made whole and replaced when it grows: its staging is reserved for what it holds.
                Expect("bodyBatch.forwardArguments", n, 20, n);
                Expect("camera0.capVertices", 112L * n, 16, 112L * n);
                Expect("camera0.volumes.forwardArguments", 8L * n, 20, 8L * n);

                long nativeRooms = 0;
                foreach (VpRoomLine line in lines.Values)
                {
                    if (!line.native)
                    {
                        continue;
                    }

                    nativeRooms++;
                    long inUse = line.length * line.itemBytes;
                    long reserved = line.reservedLength * line.itemBytes;
                    Assert.That(line.committedBytes, Is.GreaterThanOrEqualTo(inUse), line.name + ": what is in use is committed");
                    Assert.That(line.committedBytes, Is.LessThan(inUse + Granule), line.name + ": and no more than to the granularity");
                    Assert.That(line.reservedBytes, Is.GreaterThanOrEqualTo(reserved), line.name + ": the limit is reserved");
                    Assert.That(line.reservedBytes, Is.LessThan(reserved + Granule), line.name);
                    Assert.That(line.baseAddress, Is.Not.EqualTo(IntPtr.Zero), line.name);
                }

                // The GPU buffers of the first room: the body's batch, the cap normals and the one camera's stencil batch.
                long body = (n * 20L * 2) + (n * (64L + 132L));
                long normals = 112L * n * 16;
                long camera = (8L * n * 20 * 2) + (8L * n * (64L + 132L)) + (112L * n * 16) + (288L * n * 4);
                Assert.That(display.RoomGpuBytes, Is.EqualTo(body + normals + camera), "the GPU buffers, by the same multipliers");

                VpRoomBytes bytes = display.RoomBytes();
                Assert.That(bytes.nativeCommitted, Is.GreaterThanOrEqualTo(bytes.nativeInUse));
                Assert.That(bytes.nativeCommitted, Is.LessThan(bytes.nativeInUse + (nativeRooms * Granule)));
                Assert.That(bytes.nativeReserved, Is.GreaterThan(bytes.nativeCommitted));
                Assert.That(display.RoomPreparationMilliseconds, Is.GreaterThan(0.0));

                TestContext.Out.WriteLine("ROOM first room 1024 each, limits 65536 each, one camera: native rooms " + nativeRooms
                    + "; native in use " + Mb(bytes.nativeInUse) + ", committed " + Mb(bytes.nativeCommitted) + ", reserved " + Mb(bytes.nativeReserved)
                    + "; managed " + Mb(bytes.managed) + "; GPU " + Mb(display.RoomGpuBytes) + " (body " + Mb(body) + ", cap normals " + Mb(normals) + ", camera " + Mb(camera) + ")"
                    + "; made in " + display.RoomPreparationMilliseconds.ToString("F2") + " ms, the camera's room in " + cameraMs.ToString("F2") + " ms (Editor)");
                var sorted = new List<VpRoomLine>(lines.Values);
                sorted.Sort((a, b) => (b.native ? b.length * b.itemBytes : b.managedBytes).CompareTo(a.native ? a.length * a.itemBytes : a.managedBytes));
                foreach (VpRoomLine line in sorted)
                {
                    TestContext.Out.WriteLine("ROOM  " + line);
                }
            }
        }

        private static int UnsafeSize<T>() where T : struct => Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<T>();

        /// <summary>
        /// A room's numbers and its growth: the part in use is zero when made and when granted more, what was written
        /// stays where it was behind the same base, the view lent is the part in use and never the reservation, and a
        /// prepare without a grant changes nothing a borrower sees. Beside it, a measure (not a requirement): making a
        /// room commits and writes its pages, and writing them again afterwards is what a frame would pay -- the
        /// difference is what was moved to before play.
        /// </summary>
        [Test]
        public void ARoomOfNumbers_IsZeroWhenGranted_KeepsItsBaseAndWhatWasWritten_AndLendsOnlyWhatIsInUse()
        {
            var pages = new CountingPageBacking { GranularityBytes = 4096 };
            Assert.That(VpNumericRoom<Vector4>.TryCreateNative(pages, 1 << 16, 100, out VpNumericRoom<Vector4> room, out string failure), Is.True, failure);
            using (room)
            {
                Assert.That(room.IsNative, Is.True);
                Assert.That(room.Length, Is.EqualTo(100));
                Assert.That(room.ReservedLength, Is.EqualTo(1 << 16));
                Assert.That(room.Valid.Length, Is.EqualTo(100), "the view lent is the part in use, not the reservation");
                Assert.That(room.First(7).Length, Is.EqualTo(7));
                Assert.Throws<ArgumentOutOfRangeException>(() => room.First(101), "nothing past the part in use is lent");
                Assert.Throws<IndexOutOfRangeException>(() => { Vector4 unused = room[100]; }, "nor read");
                for (int i = 0; i < 100; i++)
                {
                    Assert.That(room[i], Is.EqualTo(Vector4.zero), "zero when made");
                    room[i] = new Vector4(i, 1f, 2f, 3f);
                }

                room[5].w = 9f;   // written in place
                Assert.That(room[5], Is.EqualTo(new Vector4(5f, 1f, 2f, 9f)));
                IntPtr at = room.BaseAddress;
                long committed = room.CommittedBytes;

                Assert.That(room.TryPrepare(5000, out failure), Is.True, failure);
                Assert.That(room.Length, Is.EqualTo(100), "prepared, and not yet usable");
                Assert.That(room.Valid.Length, Is.EqualTo(100));
                Assert.That(room.CommittedBytes, Is.GreaterThan(committed), "the pages are there");
                room.Grant(5000);
                Assert.That(room.Length, Is.EqualTo(5000));
                Assert.That(room.BaseAddress, Is.EqualTo(at), "the base did not move");
                Assert.That(room.Valid.Length, Is.EqualTo(5000));
                Assert.That(room[5], Is.EqualTo(new Vector4(5f, 1f, 2f, 9f)), "what was written stays");
                Assert.That(room[99], Is.EqualTo(new Vector4(99f, 1f, 2f, 3f)));
                for (int i = 100; i < 5000; i++)
                {
                    Assert.That(room[i], Is.EqualTo(Vector4.zero), "the new part is zero");
                }

                Assert.That(room.TryGrow((1 << 16) + 1, out failure), Is.False, "past the reservation");
                Assert.That(room.Length, Is.EqualTo(5000));
                pages.CommitLimitBytes = room.CommittedBytes;
                Assert.That(room.TryGrow(1 << 15, out failure), Is.False, "the backing refuses the pages");
                StringAssert.Contains("refused", failure);
                Assert.That(room.Length, Is.EqualTo(5000), "and nothing changed");
                Assert.That(room[5], Is.EqualTo(new Vector4(5f, 1f, 2f, 9f)));
            }

            Assert.Throws<ObjectDisposedException>(() => { NativeArray<Vector4> unused = room.Valid; });
            Assert.That(pages.Releases, Is.EqualTo(pages.Reserves));
            Assert.That(pages.ReleasedTwice, Is.Zero);

            // A managed room is read and written the same way, and grows by a larger array with what was there.
            VpNumericRoom<int> managed = VpNumericRoom<int>.Managed(4);
            managed[3] = 7;
            Assert.That(managed.TryGrow(10, out failure), Is.True);
            Assert.That(managed.Length, Is.EqualTo(10));
            Assert.That(managed[3], Is.EqualTo(7));
            Assert.That(managed[9], Is.Zero);
            Assert.Throws<InvalidOperationException>(() => { NativeArray<int> unused = managed.Valid; }, "a managed room lends no native view");

            // The measure: the cap normals' size at a first room of 1024 (114,688 float4), made five times.
            const int normals = 114688;
            double make = double.MaxValue, again = double.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                long begin = Stopwatch.GetTimestamp();
                Assert.That(VpNumericRoom<Vector4>.TryCreateNative(VpWindowsPageBacking.Instance, normals * 64, normals, out VpNumericRoom<Vector4> measured, out failure), Is.True, failure);
                make = Math.Min(make, (Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency);
                begin = Stopwatch.GetTimestamp();
                measured.Clear(0, normals);
                again = Math.Min(again, (Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency);
                measured.Dispose();
            }

            TestContext.Out.WriteLine("TOUCH a room of " + normals + " float4 (" + Mb(normals * 16L) + "): reserved, committed and every page written "
                + make.ToString("F3") + " ms; the same pages written again " + again.ToString("F3") + " ms (least of five, Editor)");
        }

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F2") + " MB";

        /// <summary>
        /// With the first room of 1024 made before play, the body that takes the display from 512 to 513 render
        /// fragments grows nothing: no count, no snapshot, no page committed, no managed room, no GPU object replaced.
        /// Beside it, a display that starts at the earlier first room (256 commands, 512 instances, 128 branches) crosses
        /// the same boundary by growing -- in place: the bases stay, and what it costs is written out.
        /// </summary>
        [Test]
        public void AfterTheRoomIsPrepared_The513thBody_GrowsNothing()
        {
            const int limit = 65536;
            var limits = new VpLogicalCutDisplayLimits(limit, limit, limit, limit);
            foreach ((string what, int commands, int instances, int branches, bool grows) in new[]
                     {
                         ("first room 1024", 1024, 1024, 1024, false),
                         ("first room 256/512/128 (as it was)", 256, 512, 128, true),
                     })
            {
                using (Scene scene = NewReservedScene(commands, instances, branches, 512, limits, null, 600, 256))
                {
                    Assert.That(scene, Is.Not.Null, VpLogicalCutDisplay.LastCreationFailure);
                    VpLogicalCutDisplay display = scene.display;
                    Assert.That(display.TryRegisterCamera(Oblique()), Is.True, what);
                    ShowBodies(scene, 512);
                    Collect(scene);
                    Collect(scene);   // both sides have been the candidate once: each has the room the 512 need
                    Assert.That(display.RenderFragmentCount, Is.EqualTo(512), what);
                    int growths = display.RoomGrowths;
                    int regrowths = display.SnapshotRegrowths;
                    int retired = display.RetiredGpuObjects;
                    long builds = display.StructureBuilds;
                    VpRoomBytes before = display.RoomBytes();
                    Dictionary<string, VpRoomLine> linesBefore = LinesOf(display);

                    ShowBodies(scene, 1);
                    int collections = GC.CollectionCount(0);
                    long managedBefore = GC.GetTotalMemory(false);
                    long begin = Stopwatch.GetTimestamp();
                    Collect(scene);
                    double ms = (Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency;
                    long managed = GC.GetTotalMemory(false) - managedBefore;
                    bool collected = GC.CollectionCount(0) != collections;
                    begin = Stopwatch.GetTimestamp();
                    Collect(scene);
                    double nextMs = (Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency;

                    Assert.That(display.IsHalted, Is.False, what);
                    Assert.That(display.RenderFragmentCount, Is.EqualTo(513), what + ": the 513th is drawn");
                    VpRoomBytes after = display.RoomBytes();
                    Dictionary<string, VpRoomLine> linesAfter = LinesOf(display);
                    foreach (KeyValuePair<string, VpRoomLine> line in linesBefore)
                    {
                        if (line.Value.native && !line.Key.StartsWith("camera", StringComparison.Ordinal) && !line.Key.StartsWith("bodyBatch", StringComparison.Ordinal))
                        {
                            Assert.That(linesAfter[line.Key].baseAddress, Is.EqualTo(line.Value.baseAddress), what + ": " + line.Key + " stands where it stood");
                            Assert.That(linesAfter[line.Key].reservedBytes, Is.EqualTo(line.Value.reservedBytes), what + ": " + line.Key);
                        }
                    }

                    if (!grows)
                    {
                        Assert.That(display.RoomGrowths, Is.EqualTo(growths), what + ": no count grew");
                        Assert.That(display.SnapshotRegrowths, Is.EqualTo(regrowths), what + ": no snapshot grew");
                        Assert.That(display.RetiredGpuObjects, Is.EqualTo(retired), what + ": no GPU object was replaced");
                        Assert.That(after.nativeCommitted, Is.EqualTo(before.nativeCommitted), what + ": no page was committed");
                        Assert.That(after.nativeInUse, Is.EqualTo(before.nativeInUse), what);
                        Assert.That(after.managed, Is.EqualTo(before.managed), what + ": no managed room was made");
                        Assert.That(display.StructureBuilds - builds, Is.EqualTo(1), what + ": the structure was built once, in the first of the two collections");
                    }
                    else
                    {
                        Assert.That(display.RoomGrowths, Is.GreaterThan(growths), what + ": the room grew");
                        Assert.That(after.nativeInUse, Is.GreaterThan(before.nativeInUse), what);
                        Assert.That(display.LastRoomFailure, Is.Null, what);
                    }

                    TestContext.Out.WriteLine("BOUNDARY " + what + ": the collection that takes 512 to 513 fragments " + ms.ToString("F2") + " ms, the next " + nextMs.ToString("F2")
                        + " ms (Editor); managed rise " + Mb(managed) + (collected ? " (a collection ran inside: the rise says nothing)" : "")
                        + "; counts grown " + (display.RoomGrowths - growths) + ", snapshots grown " + (display.SnapshotRegrowths - regrowths)
                        + ", structure builds " + (display.StructureBuilds - builds) + ", GPU objects replaced " + (display.RetiredGpuObjects - retired)
                        + "; native committed " + Mb(before.nativeCommitted) + " -> " + Mb(after.nativeCommitted) + ", managed room " + Mb(before.managed) + " -> " + Mb(after.managed)
                        + "; " + display.DescribeRoom());
                }
            }
        }

        /// <summary>
        /// A room that grows inside its reservation keeps every base address and the reservation's size, on both sides
        /// of the display, and the body shown before the growth is drawn as it was.
        /// </summary>
        [Test]
        public void GrowingWithinTheReservation_KeepsEveryBase_AndWhatWasShown()
        {
            var limits = new VpLogicalCutDisplayLimits(64, 64, 64, 64);
            using (Scene scene = NewReservedScene(2, 2, 2, 64, limits, null))
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                LogicalFragmentId a = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                LogicalFragmentId b = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                Assert.That(display.TryShow(a, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                Assert.That(display.TryShow(b, AppendCube(scene.storage, false), Matrix4x4.Translate(new Vector3(3f, 0f, 0f))), Is.True);
                Collect(scene);
                Camera camera = Oblique();
                Draw(display, camera);
                Dictionary<string, VpRoomLine> before = LinesOf(display);
                int bBefore = RenderFragmentOfRoot(display, b);
                display.TryGetRenderFragment(bBefore, out VpMultiCutRenderFragment bodyBefore);

                for (int round = 0; round < 3; round++)
                {
                    LogicalFragmentId leaf = a;
                    for (int k = 0; k <= round; k++)
                    {
                        (_, LogicalFragmentId plus, _) = Cut(ledger, leaf, Tilted(k + (round * 3)));
                        leaf = plus;
                    }

                    a = leaf;
                    Collect(scene);
                    Collect(scene);
                    Assert.That(display.IsHalted, Is.False);
                    Assert.That(display.LastRoomFailure, Is.Null, display.LastRoomFailure);
                    Draw(display, camera);
                }

                Assert.That(display.RoomGrowths, Is.GreaterThan(0), "the room grew");
                Dictionary<string, VpRoomLine> after = LinesOf(display);
                int grown = 0;
                foreach (KeyValuePair<string, VpRoomLine> line in before)
                {
                    if (!line.Value.native || line.Key.StartsWith("camera", StringComparison.Ordinal) || line.Key.StartsWith("bodyBatch", StringComparison.Ordinal))
                    {
                        continue;   // a batch is replaced whole when it grows, with its staging
                    }

                    // The two sides exchange their rooms at each adoption: a room is found again under either name.
                    string twin = line.Key.EndsWith("'", StringComparison.Ordinal) ? line.Key.Substring(0, line.Key.Length - 1)
                        : line.Key.StartsWith("snapshot.", StringComparison.Ordinal) ? "snapshot'." + line.Key.Substring(9)
                        : line.Key.StartsWith("snapshot'.", StringComparison.Ordinal) ? "snapshot." + line.Key.Substring(10) : line.Key + "'";
                    VpRoomLine now = after[line.Key].baseAddress == line.Value.baseAddress ? after[line.Key]
                        : after.ContainsKey(twin) ? after[twin] : after[line.Key];
                    Assert.That(now.baseAddress, Is.EqualTo(line.Value.baseAddress), line.Key + ": the base did not move");
                    Assert.That(now.reservedBytes, Is.EqualTo(line.Value.reservedBytes), line.Key + ": the reservation is the same one");
                    Assert.That(now.length, Is.GreaterThanOrEqualTo(line.Value.length), line.Key);
                    grown += now.length > line.Value.length ? 1 : 0;
                }

                Assert.That(grown, Is.GreaterThan(10), "many rooms grew, each in place");
                display.TryGetRenderFragment(RenderFragmentOfRoot(display, b), out VpMultiCutRenderFragment bodyAfter);
                Assert.That(bodyAfter.geometryLocalToWorld, Is.EqualTo(bodyBefore.geometryLocalToWorld), "the body shown before the growth stands where it stood");
                AssertEveryRenderFragmentHasItsCommand(display);
            }
        }

        /// <summary>
        /// What a snapshot builds on reserved room is what one in managed arrays builds, item for item; grown in place it
        /// stays built, keeps its base and what was read from it; a room it is short of is taken by growing and building
        /// again in the same snapshot; and a growth past the reservation, or one the backing refuses, changes nothing.
        /// </summary>
        [Test]
        public void ASnapshotOnReservedRoom_BuildsWhatAManagedOneBuilds_AndGrowsInPlace()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64));
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            var reflected = new List<VpClipBoundary>();
            var small = new VpMultiCutCapacities(2, 16, 2, 16, 32);
            var large = new VpMultiCutCapacities(64, 512, 64, 512, 32);
            var pages = new CountingPageBacking { GranularityBytes = 4096 };
            Assert.That(VpMultiCutSnapshot.TryCreateOnBacking(pages, small, large, out VpMultiCutSnapshot native, out string failure), Is.True, failure);
            using (native)
            {
                var managed = new VpMultiCutSnapshot(large);
                Assert.That(native.IsOnBacking, Is.True);
                Assert.That(managed.IsOnBacking, Is.False);

                VpMultiCutBuildOutcome Build(VpMultiCutSnapshot s) => s.TryBuild(ledger, root, bounds, Matrix4x4.identity, Matrix4x4.identity, reflected, 1e-4f);

                // One body: built in the small room. Grown in place, it is still that build.
                Assert.That(Build(native), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                IntPtr capBase = native.CapVertexArray.BaseAddress;
                native.TryGetRenderFragment(0, out VpMultiCutRenderFragment first);
                long generation = native.BuildGeneration;
                Assert.That(native.TryGrowTo(new VpMultiCutCapacities(8, 64, 8, 64, 32), out failure), Is.True, failure);
                Assert.That(native.IsBuilt, Is.True, "grown, and still built");
                Assert.That(native.BuildGeneration, Is.EqualTo(generation));
                Assert.That(native.CapVertexArray.BaseAddress, Is.EqualTo(capBase), "the base did not move");
                Assert.That(native.Capacities.renderFragments, Is.EqualTo(8));
                Assert.That(native.Capacities.caps, Is.EqualTo(64));
                native.TryGetRenderFragment(0, out VpMultiCutRenderFragment afterGrowth);
                Assert.That(afterGrowth.Equals(first), Is.True, "what was built reads the same");

                // Three rounds of cuts: sixteen leaves, more than the room of eight.
                var leaves = new List<LogicalFragmentId> { root };
                for (int k = 0; k < 4; k++)
                {
                    var next = new List<LogicalFragmentId>();
                    foreach (LogicalFragmentId leaf in leaves)
                    {
                        (_, LogicalFragmentId plus, LogicalFragmentId minus) = Cut(ledger, leaf, Tilted(k));
                        next.Add(plus);
                        next.Add(minus);
                    }

                    leaves = next;
                }

                Assert.That(Build(native), Is.EqualTo(VpMultiCutBuildOutcome.CapacityExceeded), "sixteen leaves do not fit eight");
                Assert.That(native.IsBuilt, Is.False);

                // Past the reservation, and refused by the backing: nothing changes.
                Assert.That(native.TryGrowTo(new VpMultiCutCapacities(65, 512, 64, 512, 32), out failure), Is.False, "past what is reserved");
                Assert.That(native.Capacities.branches, Is.EqualTo(8), "no count changed");
                pages.CommitLimitBytes = 4096;
                Assert.That(native.TryGrowTo(large, out failure), Is.False, "the backing refuses the pages");
                StringAssert.Contains("refused", failure);
                Assert.That(native.Capacities.branches, Is.EqualTo(8), "no count changed");
                Assert.That(native.Capacities.caps, Is.EqualTo(64));
                Assert.That(native.CapVertexArray.BaseAddress, Is.EqualTo(capBase));
                pages.CommitLimitBytes = long.MaxValue;

                // Grown, and built again in the same snapshot: what a managed one builds.
                Assert.That(native.TryGrowTo(large, out failure), Is.True, failure);
                Assert.That(native.CapVertexArray.BaseAddress, Is.EqualTo(capBase), "still the same base");
                Assert.That(Build(native), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                Assert.That(Build(managed), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                AssertSameBuild(managed, native);
                Assert.That(native.RenderFragmentCount, Is.EqualTo(16));

                // And a snapshot made at the large room from the start builds the same.
                Assert.That(VpMultiCutSnapshot.TryCreateOnBacking(pages, large, large, out VpMultiCutSnapshot whole, out failure), Is.True, failure);
                using (whole)
                {
                    Assert.That(Build(whole), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                    AssertSameBuild(managed, whole);
                }
            }

            Assert.That(pages.Releases, Is.EqualTo(pages.Reserves), "every reservation given back");
            Assert.That(pages.ReleasedTwice, Is.Zero, "and none twice");
        }

        private static void AssertSameBuild(VpMultiCutSnapshot a, VpMultiCutSnapshot b)
        {
            Assert.That(b.BranchCount, Is.EqualTo(a.BranchCount), "branches");
            Assert.That(b.CandidateCount, Is.EqualTo(a.CandidateCount), "candidates");
            Assert.That(b.RenderFragmentCount, Is.EqualTo(a.RenderFragmentCount), "render fragments");
            Assert.That(b.ConditionCount, Is.EqualTo(a.ConditionCount), "conditions");
            Assert.That(b.CapCount, Is.EqualTo(a.CapCount), "caps");
            Assert.That(b.CapVertexCount, Is.EqualTo(a.CapVertexCount), "cap vertices");
            for (int i = 0; i < a.BranchCount; i++)
            {
                a.TryGetBranch(i, out VpMultiCutBranch x);
                b.TryGetBranch(i, out VpMultiCutBranch y);
                Assert.That(y.Equals(x), Is.True, "branch " + i);
            }

            for (int i = 0; i < a.CandidateCount; i++)
            {
                a.TryGetCandidate(i, out VpClipCandidate x, out VpClipSelectionState sx);
                b.TryGetCandidate(i, out VpClipCandidate y, out VpClipSelectionState sy);
                Assert.That(y.Equals(x) && sy == sx, Is.True, "candidate " + i);
            }

            for (int i = 0; i < a.RenderFragmentCount; i++)
            {
                a.TryGetRenderFragment(i, out VpMultiCutRenderFragment x);
                b.TryGetRenderFragment(i, out VpMultiCutRenderFragment y);
                Assert.That(y.Equals(x), Is.True, "render fragment " + i);
            }

            for (int i = 0; i < a.ConditionCount; i++)
            {
                a.TryGetCondition(i, out VpCapConstraint x);
                b.TryGetCondition(i, out VpCapConstraint y);
                Assert.That(y.Equals(x), Is.True, "condition " + i);
            }

            for (int c = 0; c < a.CapCount; c++)
            {
                a.TryGetCap(c, out VpMultiCutCap x);
                b.TryGetCap(c, out VpMultiCutCap y);
                Assert.That(y.Equals(x), Is.True, "cap " + c);
                ReadOnlySpan<Vector3> px = a.CapPolygon(c).AsSpan();
                ReadOnlySpan<Vector3> py = b.CapPolygon(c).AsSpan();
                Assert.That(py.Length, Is.EqualTo(px.Length), "cap " + c + " polygon");
                for (int v = 0; v < px.Length; v++)
                {
                    Assert.That(py[v], Is.EqualTo(px[v]), "cap " + c + " vertex " + v);
                    Assert.That(b.CapPolygon(c)[v], Is.EqualTo(px[v]), "the same through the range's indexer");
                }

                ReadOnlySpan<Vector3> sx = a.InitialSection(c).AsSpan();
                ReadOnlySpan<Vector3> sy = b.InitialSection(c).AsSpan();
                Assert.That(sy.Length, Is.EqualTo(sx.Length), "cap " + c + " section");
                for (int v = 0; v < sx.Length; v++)
                {
                    Assert.That(sy[v], Is.EqualTo(sx[v]), "cap " + c + " section vertex " + v);
                }
            }
        }

        /// <summary>
        /// A commit the backing refuses while the room grows is a room that could not be made: told once with what was
        /// short, the display stopped before the frame draws, nothing uploaded and no reference left taken -- the same
        /// contract as a need past a limit. Without a handler the previous snapshot keeps drawing, whole. Every
        /// reservation is given back once either way.
        /// </summary>
        [Test]
        public void ACommitTheBackingRefusesWhileGrowing_IsToldOnce_AndKeepsTheFailureContract()
        {
            foreach (bool handled in new[] { true, false })
            {
                string what = handled ? "handled" : "no handler";
                var pages = new CountingPageBacking { GranularityBytes = 4096 };
                var limits = new VpLogicalCutDisplayLimits(64, 64, 64, 64);
                using (Scene scene = NewReservedScene(2, 2, 2, 64, limits, pages))
                {
                    Assert.That(scene, Is.Not.Null, VpLogicalCutDisplay.LastCreationFailure);
                    LogicalCutLedger ledger = scene.ledger;
                    VpLogicalCutDisplay display = scene.display;
                    var told = new List<string>();
                    if (handled)
                    {
                        display.RoomFailureHandler = told.Add;
                    }

                    LogicalFragmentId a = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                    LogicalFragmentId b = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                    Assert.That(display.TryShow(a, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True, what);
                    Assert.That(display.TryShow(b, AppendCube(scene.storage, false), Matrix4x4.Translate(new Vector3(3f, 0f, 0f))), Is.True, what);
                    Collect(scene);
                    Camera camera = Oblique();
                    Draw(display, camera);
                    Drawn before = Capture(display);
                    int uploads = display.CommandUploads;
                    int references = scene.table.LiveDisplayInstanceCount;
                    VpRoomBytes bytesBefore = display.RoomBytes();

                    // From here no reservation may take a second page: the growth the cut needs is refused by the backing.
                    pages.CommitLimitBytes = 4096;
                    Cut(ledger, a, new float4(0f, 1f, 0f, 0f));
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.False, what + ": not adopted");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads), what + ": nothing uploaded");
                    Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(references), what + ": no reference left taken");
                    Assert.That(display.LastRoomFailure, Is.Not.Null, what + ": recorded");
                    StringAssert.Contains("refused", display.LastRoomFailure, what);
                    StringAssert.Contains("needed", display.LastRoomFailure, what);
                    StringAssert.Contains("held", display.LastRoomFailure, what);
                    StringAssert.Contains("limit", display.LastRoomFailure, what);
                    Assert.That(display.RoomBytes().nativeInUse, Is.LessThanOrEqualTo(bytesBefore.nativeInUse + 4096), what + ": no room was made usable past what one page holds");
                    TestContext.Out.WriteLine(what + ": " + display.LastRoomFailure);

                    if (!handled)
                    {
                        Assert.That(display.IsHalted, Is.False, what + ": refused as it always was");
                        AssertSameShape(before, Capture(display), what + ": the previous snapshot, whole");
                        Draw(display, camera);
                    }
                    else
                    {
                        Assert.That(told.Count, Is.EqualTo(1), what + ": told once");
                        Assert.That(display.IsHalted, Is.True, what + ": stopped");
                        Assert.That(display.HaltReason, Is.EqualTo(LogicalCutDisplayHaltReason.RoomNotEstablished), what);
                        Assert.That(display.IsFrameOpen, Is.False, what + ": nothing older is drawn in this frame");
                        _frame++;
                        Assert.That(display.TryBeginFrame(), Is.False, what + ": still stopped");
                        Assert.That(told.Count, Is.EqualTo(1), what + ": still told once");
                    }

                    _frame++;
                    display.Dispose();
                    Assert.That(scene.table.LiveDisplayInstanceCount, Is.Zero, what + ": every reference back");
                }

                Assert.That(pages.Reserves, Is.GreaterThan(40), what + ": the layout: the display's rooms were reserved on this backing");
                Assert.That(pages.Releases, Is.EqualTo(pages.Reserves), what + ": every reservation given back");
                Assert.That(pages.ReleasedTwice, Is.Zero, what + ": and none twice");
            }
        }

        /// <summary>
        /// Every reservation is given back exactly once: after a display that grew, replaced its GPU objects and had a
        /// camera registered and let go; after a display that could not be made, whichever reservation was the one
        /// refused; and after a camera whose stencil room could not be made.
        /// </summary>
        [Test]
        public void EveryReservation_IsGivenBackOnce_AfterUse_AfterAFailureWhileMaking_AndAfterACameraThatCouldNotBeGivenRoom()
        {
            var limits = new VpLogicalCutDisplayLimits(64, 64, 64, 64);

            // 1. A whole life: made, a camera, grown (the body's batch and the camera's replaced), a second camera let go.
            var pages = new CountingPageBacking();
            int made;
            using (Scene scene = NewReservedScene(2, 2, 2, 64, limits, pages))
            {
                Assert.That(scene, Is.Not.Null, VpLogicalCutDisplay.LastCreationFailure);
                made = pages.Reserves;
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                LogicalFragmentId a = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                Assert.That(display.TryShow(a, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                Collect(scene);
                Camera camera = Oblique();
                Draw(display, camera);
                Camera second = Oblique();
                Assert.That(display.TryRegisterCamera(second), Is.True);
                int withCameras = pages.Reserves;
                Assert.That(withCameras, Is.GreaterThan(made), "each camera's staging is reserved too");
                for (int k = 0; k < 4; k++)
                {
                    (_, LogicalFragmentId plus, _) = Cut(ledger, a, Tilted(k));
                    a = plus;
                    Collect(scene);
                    Draw(display, camera);
                }

                Assert.That(display.RoomGrowths, Is.GreaterThan(0), "the layout: the room grew");
                Assert.That(pages.Reserves, Is.GreaterThan(withCameras), "the layout: a batch was replaced by a larger one, with its staging");
                _frame++;
                Assert.That(display.TryUnregisterCamera(second), Is.True);
            }

            Assert.That(pages.Releases, Is.EqualTo(pages.Reserves), "every reservation given back");
            Assert.That(pages.ReleasedTwice, Is.Zero, "and none twice");

            // 2. A display that could not be made, for each reservation that can be the one refused.
            for (int refused = 0; refused < made; refused++)
            {
                var failing = new CountingPageBacking { RefuseReservesFrom = refused };
                Scene scene = NewReservedScene(2, 2, 2, 64, limits, failing);
                Assert.That(scene, Is.Null, "reservation " + refused + " refused: no display");
                Assert.That(VpLogicalCutDisplay.LastCreationFailure, Is.Not.Null, "reservation " + refused);
                StringAssert.Contains("refused", VpLogicalCutDisplay.LastCreationFailure, "reservation " + refused);
                Assert.That(failing.Reserves, Is.EqualTo(refused), "reservation " + refused + ": the ones before it were made");
                Assert.That(failing.Releases, Is.EqualTo(failing.Reserves), "reservation " + refused + ": and every one given back");
                Assert.That(failing.ReleasedTwice, Is.Zero, "reservation " + refused);
            }

            // 3. A camera whose stencil room is refused: not registered, nothing held, and the display is as it was.
            var cameraFails = new CountingPageBacking { RefuseReservesFrom = made };
            using (Scene scene = NewReservedScene(2, 2, 2, 64, limits, cameraFails))
            {
                Assert.That(scene, Is.Not.Null, VpLogicalCutDisplay.LastCreationFailure);
                Camera camera = Oblique();
                Assert.That(scene.display.TryRegisterCamera(camera), Is.False, "the camera's room could not be made");
                Assert.That(scene.display.LastRoomFailure, Is.Not.Null);
                StringAssert.Contains("camera", scene.display.LastRoomFailure);
                Assert.That(scene.display.IsHalted, Is.False);
                ShowBodies(scene, 1);
                Collect(scene);
            }

            Assert.That(cameraFails.Releases, Is.EqualTo(cameraFails.Reserves), "every reservation given back");
            Assert.That(cameraFails.ReleasedTwice, Is.Zero);
        }

        /// <summary>
        /// A whole write that fails -- an array of zeros that cannot be had, or a buffer that cannot be written -- at any
        /// of its steps leaves no array, no batch and no reservation behind: in a batch on its own, at a camera's
        /// registration (the camera is then not registered, and can be registered afterwards), and while a display is
        /// made (no display, and its failure said). A reservation that is refused does not reach these paths; each step
        /// is made to throw here in turn.
        /// </summary>
        [Test]
        public void AWholeWriteThatFails_AtAnyStep_LeavesNoArray_NoBatch_AndNoReservationBehind()
        {
            var limits = new VpLogicalCutDisplayLimits(64, 64, 64, 64);
            int roomsAtStart = VpNumericRoomCensus.LiveNativeRooms;
            try
            {
                // What the steps are: recorded from writes that succeed.
                var creationSteps = new List<string>();
                var cameraSteps = new List<string>();
                var recording = new CountingPageBacking();
                VpWholeWrite.StepForTest = creationSteps.Add;
                using (Scene scene = NewReservedScene(2, 2, 2, 64, limits, recording))
                {
                    Assert.That(scene, Is.Not.Null, VpLogicalCutDisplay.LastCreationFailure);
                    VpWholeWrite.StepForTest = cameraSteps.Add;
                    Assert.That(scene.display.TryRegisterCamera(Oblique()), Is.True);
                    VpWholeWrite.StepForTest = null;
                }

                TestContext.Out.WriteLine("creation: " + string.Join(", ", creationSteps));
                TestContext.Out.WriteLine("camera: " + string.Join(", ", cameraSteps));
                Assert.That(creationSteps.Count, Is.EqualTo(9), "the body's three arrays and four buffers, the cap normals' array and buffer");
                Assert.That(cameraSteps.Count, Is.EqualTo(11), "the volumes' three arrays and four buffers, the caps' two arrays and two buffers");
                Assert.That(VpWholeWrite.LiveArrays, Is.Zero, "the layout: nothing is alive after a whole write that succeeded");
                Assert.That(recording.Releases, Is.EqualTo(recording.Reserves));

                // 1. A stencil batch on its own.
                for (int failing = 0; failing < cameraSteps.Count; failing++)
                {
                    var pages = new CountingPageBacking();
                    var batch = new VpStencilCapBatch(4, 16, 16, 224, 576, pages);
                    int calls = 0;
                    int at = failing;
                    VpWholeWrite.StepForTest = what => { if (calls++ == at) throw new InvalidOperationException("test: refused at " + what); };
                    Assert.Throws<InvalidOperationException>(() => batch.WriteWholeOnce(), "step " + cameraSteps[failing]);
                    VpWholeWrite.StepForTest = null;
                    Assert.That(VpWholeWrite.LiveArrays, Is.Zero, cameraSteps[failing] + ": no array of zeros left");
                    batch.Dispose();
                    Assert.That(pages.Releases, Is.EqualTo(pages.Reserves), cameraSteps[failing]);
                    Assert.That(pages.ReleasedTwice, Is.Zero, cameraSteps[failing]);
                }

                // 2. A camera's registration.
                for (int failing = 0; failing < cameraSteps.Count; failing++)
                {
                    var pages = new CountingPageBacking();
                    using (Scene scene = NewReservedScene(2, 2, 2, 64, limits, pages))
                    {
                        Assert.That(scene, Is.Not.Null, VpLogicalCutDisplay.LastCreationFailure);
                        int held = pages.Reserves - pages.Releases;
                        Camera camera = Oblique();
                        int calls = 0;
                        int at = failing;
                        VpWholeWrite.StepForTest = what => { if (calls++ == at) throw new InvalidOperationException("test: refused at " + what); };
                        Assert.That(scene.display.TryRegisterCamera(camera), Is.False, cameraSteps[failing] + ": not registered");
                        VpWholeWrite.StepForTest = null;
                        StringAssert.Contains("camera", scene.display.LastRoomFailure, cameraSteps[failing]);
                        StringAssert.Contains("refused at", scene.display.LastRoomFailure, cameraSteps[failing]);
                        Assert.That(VpWholeWrite.LiveArrays, Is.Zero, cameraSteps[failing] + ": no array of zeros left");
                        Assert.That(pages.Reserves - pages.Releases, Is.EqualTo(held), cameraSteps[failing] + ": the batch that was not handed over was given back, staging and all");
                        Assert.That(pages.ReleasedTwice, Is.Zero, cameraSteps[failing]);
                        Assert.That(scene.display.TryGetCameraStencil(camera, out _, out _), Is.False, cameraSteps[failing] + ": the camera holds no slot");
                        Assert.That(scene.display.IsHalted, Is.False, cameraSteps[failing]);

                        // The slot was not used up: the same camera is registered once the write can be made, and drawn.
                        ShowBodies(scene, 1);
                        Collect(scene);
                        Draw(scene.display, camera);
                        Assert.That(scene.display.TryGetCameraStencil(camera, out _, out _), Is.True, cameraSteps[failing]);
                    }

                    Assert.That(pages.Releases, Is.EqualTo(pages.Reserves), cameraSteps[failing] + ": every reservation given back");
                    Assert.That(pages.ReleasedTwice, Is.Zero, cameraSteps[failing]);
                }

                // 3. While a display is made.
                for (int failing = 0; failing < creationSteps.Count; failing++)
                {
                    var pages = new CountingPageBacking();
                    int calls = 0;
                    int at = failing;
                    VpWholeWrite.StepForTest = what => { if (calls++ == at) throw new InvalidOperationException("test: refused at " + what); };
                    Scene scene = NewReservedScene(2, 2, 2, 64, limits, pages);
                    VpWholeWrite.StepForTest = null;
                    Assert.That(scene, Is.Null, creationSteps[failing] + ": no display");
                    Assert.That(VpLogicalCutDisplay.LastCreationFailure, Is.Not.Null, creationSteps[failing]);
                    StringAssert.Contains("written whole", VpLogicalCutDisplay.LastCreationFailure, creationSteps[failing]);
                    Assert.That(VpWholeWrite.LiveArrays, Is.Zero, creationSteps[failing] + ": no array of zeros left");
                    Assert.That(pages.Reserves, Is.GreaterThan(0), creationSteps[failing] + ": the layout: the room had been reserved");
                    Assert.That(pages.Releases, Is.EqualTo(pages.Reserves), creationSteps[failing] + ": every reservation given back");
                    Assert.That(pages.ReleasedTwice, Is.Zero, creationSteps[failing]);
                }
            }
            finally
            {
                VpWholeWrite.StepForTest = null;
            }

            Assert.That(VpNumericRoomCensus.LiveNativeRooms, Is.EqualTo(roomsAtStart), "no native room is left alive by any of it");
        }

        /// <summary>
        /// Counts that the registrations themselves show to be short are grown together, before anything is built: five
        /// bodies more than a room of two holds grow the branches, the instances and the commands in one step, the
        /// snapshot grows once and the structure is built once.
        /// </summary>
        [Test]
        public void CountsTheRegistrationsShowToBeShort_GrowTogether_AndTheStructureIsBuiltOnce()
        {
            var limits = new VpLogicalCutDisplayLimits(64, 64, 64, 64);
            using (Scene scene = NewReservedScene(2, 2, 2, 64, limits, null))
            {
                VpLogicalCutDisplay display = scene.display;
                ShowBodies(scene, 2);
                Collect(scene);
                Assert.That(display.RoomGrowths, Is.Zero, "the layout: two bodies fit the first room");
                long builds = display.StructureBuilds;
                int regrowths = display.SnapshotRegrowths;

                ShowBodies(scene, 5);
                Collect(scene);
                Assert.That(display.IsHalted, Is.False);
                Assert.That(display.LastRoomFailure, Is.Null, display.LastRoomFailure);
                Assert.That(display.RenderFragmentCount, Is.EqualTo(7));
                Assert.That(display.RoomGrowths, Is.EqualTo(3), "the branches, the instances and the commands, once each");
                Assert.That(display.BranchCapacity, Is.GreaterThanOrEqualTo(7));
                Assert.That(display.InstanceCapacity, Is.GreaterThanOrEqualTo(7));
                Assert.That(display.CommandCapacity, Is.GreaterThanOrEqualTo(7));
                Assert.That(display.SnapshotRegrowths - regrowths, Is.EqualTo(1), "the snapshot being built grew once");
                Assert.That(display.StructureBuilds - builds, Is.EqualTo(1), "and the structure was built once, not once per count");
                AssertEveryRenderFragmentHasItsCommand(display);
                Draw(display, Oblique());
            }
        }
    }
}
