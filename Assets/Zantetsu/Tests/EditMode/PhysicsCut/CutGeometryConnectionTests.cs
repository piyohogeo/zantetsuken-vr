using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;
using Zantetsu.MeshCut;
using Zantetsu.MeshCut.Tests;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// One accepted cut carrying **both** duties to their end through the product entrance (DESIGN 4.5.6, 7.1, 7.2):
    /// the physics — Provisional pair, cut, cook, Final handoff, the two logical children — and the display geometry —
    /// the kernel over the stored geometry, the real transfer and commit into the display, and the children's own
    /// geometry that a cut of them reads.
    /// <para>
    /// **Everything here is the product path.** The driver is the one caller; the cut is admitted once, through it;
    /// the display commit is <see cref="VpDisplayGeometryCommit"/> over a real <see cref="VpLogicalCutDisplay"/>, with
    /// a real storage and a real transfer, and not a stand-in; the actors are real Rigidbodies and what is drawn
    /// follows them through the real placement lookup. What the test controls is only **when a finished work is
    /// collected**, which is what lets "physics first" and "geometry first" be two cases rather than a race.
    /// </para>
    /// <para>
    /// The simulation is not stepped and nothing is measured.
    /// </para>
    /// <para>
    /// **Each case here is run three times over, on what the blocks of a cut hold before anything writes them**
    /// (DESIGN 7.2): as the product takes them, cleared the way they used to be, and filled with a pattern that is
    /// nothing like zero. Coming out the same in all three says that **on the inputs and paths these cases reach**,
    /// no result depended on what a block held to begin with. It does not prove that nothing is read before it is
    /// written -- what says that is the reading and the writing, matched up region by region. This is the second
    /// line of evidence beside it. The report's <c>ranToEnd</c> and the bake's <c>done</c> are cleared by the
    /// product whatever this says, and the endings below are what says so.
    /// </para>
    /// </summary>
    [TestFixture(-1)]
    [TestFixture(0x00)]
    [TestFixture(0xCD)]
    public unsafe partial class CutGeometryConnectionTests
    {
        private const double ParentMass = 12.0;
        private const float SupportEpsilon = 1e-4f;
        private const float AnchorEpsilon = 1e-5f;
        private const int VertexLimit = 128;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const int DeadlineMilliseconds = 30000;

        // What one test makes. The lists are per test: a test whose ending was not confirmed keeps its lists, moved
        // whole into _heldBack, and the next test starts with fresh ones.
        private List<IDisposable> _disposables = new List<IDisposable>();
        private List<World> _worlds = new List<World>();
        private List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private List<Mesh> _meshes = new List<Mesh>();

        /// <summary>
        /// What earlier tests of this fixture could not confirm the ending of, kept for the fixture's life and never
        /// touched again: their worlds, with the state and reasons of each ending, and the objects they tracked.
        /// </summary>
        private readonly List<CutFixtureHeldBack> _heldBack = new List<CutFixtureHeldBack>();

        private int _frame;
        private readonly int _fill;
        private int _fillWas;

        public CutGeometryConnectionTests(int fill)
        {
            _fill = fill;
        }

        [SetUp]
        public void ResetFrame()
        {
            _frame = 1;
            _fillWas = PhysicsCutBlocks.Fill;
            PhysicsCutBlocks.Fill = _fill;
            FreshLists();
        }

        private void FreshLists()
        {
            _disposables = new List<IDisposable>();
            _worlds = new List<World>();
            _objects = new List<UnityEngine.Object>();
            _meshes = new List<Mesh>();
        }

        /// <summary>
        /// Every world is ended (see <see cref="CutFixtureEnding"/>), then what the test tracked is destroyed -- but
        /// only when every ending was confirmed. Otherwise nothing more is destroyed: the lists are moved whole into
        /// <see cref="_heldBack"/>, so that what a worker could still read stays, and the test is failed with the
        /// reasons, unless it had already failed, in which case the reasons are written beside that failure.
        /// </summary>
        [TearDown]
        public void Cleanup()
        {
            PhysicsCutBlocks.Fill = _fillWas;
            var report = new List<string>();
            bool notConfirmed = false;
            Exception teardownError = null;
            try
            {
                foreach (IDisposable disposable in _disposables)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception failure)
                    {
                        notConfirmed = true;
                        report.Add("a disposable threw: " + failure);
                    }
                }

                foreach (World w in _worlds)
                {
                    if (w.Ending.State != CutFixtureEndingState.ReleasedSafely || w.Ending.Failed)
                    {
                        notConfirmed = true;
                        report.Add(w.Ending.Describe());
                    }
                }
            }
            catch (Exception failure)
            {
                teardownError = failure;
                notConfirmed = true;
                report.Add("teardown threw: " + failure);
            }
            finally
            {
                if (!notConfirmed)
                {
                    // In this order, and no list after the first failure: what it and the rest still hold is kept.
                    notConfirmed = !CutFixtureTeardown.DestroyInOrder(
                        report,
                        new KeyValuePair<string, IList>("objects", _objects),
                        new KeyValuePair<string, IList>("meshes", _meshes));
                }

                if (notConfirmed)
                {
                    _heldBack.Add(new CutFixtureHeldBack(
                        TestContext.CurrentContext.Test.FullName, _worlds, _objects, _meshes, null, report));
                    FreshLists();
                }
                else
                {
                    _disposables.Clear();
                    _worlds.Clear();
                }
            }

            CutFixtureTeardown.Report(report, notConfirmed, teardownError);
        }

        private T Track<T>(T tracked)
            where T : UnityEngine.Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        // ----- a destination whose collection the test controls --------------------------------------------------------

        /// <summary>
        /// A destination that runs work for real and hands it back only once the test has let it go. Holding something
        /// back delays a collection that would otherwise happen; it never makes a work finish, and it never stops one
        /// from being submitted -- which is what makes "it was really offered to a destination" observable.
        /// </summary>
        private sealed class HoldingExecutor : IWorkExecutor
        {
            private readonly List<IDispatchWork> _held = new List<IDispatchWork>();
            private readonly List<IDispatchWork> _released = new List<IDispatchWork>();
            private bool _closed;

            internal HoldingExecutor(WorkDestination destination, int capacity)
            {
                Destination = destination;
                Capacity = capacity;
            }

            /// <summary>Every work this destination was ever given, in order.</summary>
            internal List<IDispatchWork> Accepted { get; } = new List<IDispatchWork>();

            /// <summary>What it is holding now: given to it, not yet handed back.</summary>
            internal IReadOnlyList<IDispatchWork> Holding => _held;

            internal bool HoldEverything { get; set; }

            public WorkDestination Destination { get; }

            public int Capacity { get; }

            public int Held => _held.Count;

            public bool CanAccept => !_closed && _held.Count < Capacity;

            /// <summary>
            /// Whether work given to this destination **from now on** may be collected as soon as it is finished,
            /// while what it already holds stays held. It is how one request is let through while another waits.
            /// </summary>
            internal bool LetNewWorkThrough { get; set; }

            /// <summary>Lets one of the works this was given go, leaving the others held.</summary>
            internal void Release(int accepted)
            {
                IDispatchWork work = Accepted[accepted];
                if (!_released.Contains(work))
                {
                    _released.Add(work);
                }
            }

            internal void ReleaseEverything()
            {
                HoldEverything = false;
                for (int i = 0; i < Accepted.Count; i++)
                {
                    if (!_released.Contains(Accepted[i]))
                    {
                        _released.Add(Accepted[i]);
                    }
                }
            }

            private bool Lets(IDispatchWork work)
            {
                return !HoldEverything || _released.Contains(work);
            }

            public bool TryAccept(IDispatchWork work)
            {
                if (!CanAccept)
                {
                    return false;
                }

                _held.Add(work);
                Accepted.Add(work);
                if (LetNewWorkThrough)
                {
                    _released.Add(work);
                }

                return true;
            }

            public void BeginAccepted(IDispatchWork work)
            {
                work.Begin();
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                for (int i = 0; i < _held.Count; i++)
                {
                    IDispatchWork candidate = _held[i];
                    if (!candidate.IsComplete || !Lets(candidate))
                    {
                        continue;
                    }

                    _held.RemoveAt(i);
                    work = candidate;
                    completion = WorkCompletion.Finished;
                    return true;
                }

                work = null;
                completion = default;
                return false;
            }

            public void CloseForNewWork()
            {
                _closed = true;
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                _closed = true;
                return _held.Count == 0;
            }
        }

        /// <summary>Where a geometry failure would go. These cases expect none, so one is a failure of the test.</summary>
        private sealed class RecordingFault : ICutGeometryFault
        {
            internal readonly List<CutGeometryFault> faults = new List<CutGeometryFault>();

            public void GeometryFailed(in CutGeometryFault fault)
            {
                faults.Add(fault);
            }
        }

        // ----- one authored body: a physics shape, a display geometry, and the services ---------------------------------

        private sealed class World : IDisposable, ICutFixtureWorld
        {
            internal OwnerCutHarness harness;
            internal PhysicsShapeSource meshSource;
            internal PhysicsOwnerShape shape;
            internal VpCpuGeometryStorage storage;
            internal VpGeometryReferenceTable table;
            internal LogicalCutLedger ledger;
            internal VpLogicalCutDisplay display;
            internal PhysicsOwnerRegistry registry;
            internal PhysicsOwnerPlacementLookup lookup;
            internal HoldingExecutor physicsJob;
            internal HoldingExecutor geometryPool;
            internal WorkerPoolExecutor background;
            internal SharedWorkDispatcher dispatcher;
            internal PhysicsCutCook cook;
            internal SharedWorkFrame frame;
            internal VpDisplayGeometryCommit commit;
            internal RecordingFault fault;
            internal CutDag dag;
            internal ProvisionalCutDriver driver;
            internal LogicalFragmentId source;
            internal GameObject root;
            internal VpStoredGeometry baseGeometry;

            /// <summary>This world's ending: run once, from whichever Dispose comes first.</summary>
            internal readonly CutFixtureEnding Ending = new CutFixtureEnding();

            ProvisionalCutDriver ICutFixtureWorld.Driver => driver;

            CutDag ICutFixtureWorld.Dag => dag;

            PhysicsOwnerRegistry ICutFixtureWorld.Registry => registry;

            PhysicsCutCook ICutFixtureWorld.Cook => cook;

            SharedWorkFrame ICutFixtureWorld.Frame => frame;

            SharedWorkDispatcher ICutFixtureWorld.Dispatcher => dispatcher;

            void ICutFixtureWorld.ReleaseHeldWork()
            {
                physicsJob?.ReleaseEverything();
                geometryPool?.ReleaseEverything();
            }

            void ICutFixtureWorld.PumpDirectly()
            {
                cook?.Pump();
                dag?.Pump();
            }

            IReadOnlyList<KeyValuePair<string, Action>> ICutFixtureWorld.Tail
            {
                get
                {
                    var tail = new List<KeyValuePair<string, Action>>(4);
                    if (display != null)
                    {
                        tail.Add(new KeyValuePair<string, Action>("display", () => display.Dispose()));
                    }

                    if (background != null)
                    {
                        tail.Add(new KeyValuePair<string, Action>("background", () => background.Dispose()));
                    }

                    if (storage != null)
                    {
                        tail.Add(new KeyValuePair<string, Action>("storage", () => storage.Dispose()));
                    }

                    if (harness != null)
                    {
                        tail.Add(new KeyValuePair<string, Action>("harness", () => harness.Dispose()));
                    }

                    return tail;
                }
            }

            public void Dispose()
            {
                Ending.Run(this, DeadlineMilliseconds);
            }
        }

        private static readonly Matrix4x4 k_geometryLocalToOwner = Matrix4x4.identity;

        private World NewWorld(int frameBudget = 64, int concurrentReservations = 1, CharacterCompoundData character = null, bool expectFrameRejection = false)
        {
            var w = new World
            {
                harness = new OwnerCutHarness(),
                registry = new PhysicsOwnerRegistry(),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8)),
                storage = new VpCpuGeometryStorage(character == null ? 8192 : 65536, character == null ? 32768 : 262144, 128, 512, 512, Allocator.Persistent),
                physicsJob = new HoldingExecutor(WorkDestination.UnityJob, 4),
                geometryPool = new HoldingExecutor(WorkDestination.GeometryPool, 4),
                background = WorkerPoolExecutor.BackgroundPool(2),
                fault = new RecordingFault(),
            };
            _disposables.Add(w);
            _worlds.Add(w);

            w.table = new VpGeometryReferenceTable(w.storage, 64, 64);
            Assert.That(
                VpLogicalCutDisplay.TryCreate(
                    w.storage, w.table, w.ledger, Materials(), null, null, 32, 32,
                    VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates,
                    VpDisplayTestCapacities.ChainDepth, VpStencilTestSettings.Create(), () => _frame,
                    out VpLogicalCutDisplay display),
                Is.True,
                "the display is created");
            w.display = display;

            w.dispatcher = new SharedWorkDispatcher(8, 2, frameBudget, w.physicsJob, w.geometryPool, w.background);
            // A test value, and only a test value: how many cuts may hold a cook reservation at once.
            w.cook = new PhysicsCutCook(w.dispatcher, concurrentReservations);
            w.frame = new SharedWorkFrame(w.dispatcher);
            w.frame.Add(w.cook);
            w.commit = new VpDisplayGeometryCommit(display);
            w.dag = new CutDag(w.storage, w.ledger, w.dispatcher, w.commit, w.fault);

            // The physics shape of the authored body: one box, the same box the display geometry is.
            w.harness.planeN = new float3(0f, 1f, 0f);
            w.harness.planeW = 0f;
            w.harness.eps = SupportEpsilon;
            w.harness.parentMass = ParentMass;
            if (character == null) w.harness.Add(CaseGenerator.Box());
            else foreach (ConvexPoly poly in character.polys) w.harness.Add(poly);
            w.harness.Build();

            var ranges = new ConvexBrepRange[w.harness.input.convexCount];
            var colliderMeshes = new List<Mesh>();
            for (int c = 0; c < ranges.Length; c++)
            {
                ranges[c] = w.harness.input.convexes[c];
                colliderMeshes.Add(character == null
                    ? BoxColliderOf(w.harness.input.bank, ranges[c], "Authored " + c)
                    : CharacterColliderOf(character.polys[c], "Character UCX " + c));
            }

            w.meshSource = PhysicsShapeSource.External();
            w.shape = PhysicsOwnerShape.Authored(
                w.harness.input.bank, ranges, colliderMeshes, w.meshSource, float4x4.identity);

            w.root = Track(new GameObject("Authored Source"));
            if (character != null) w.root.transform.SetPositionAndRotation(character.position, character.rotation);
            var body = w.root.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            for (int c = 0; c < w.shape.ConvexCount; c++)
            {
                MeshCollider collider = w.root.AddComponent<MeshCollider>();
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = w.shape.MeshOf(c);
            }

            w.source = w.ledger.AddFragment();
            Matrix4x4 geometryToOwner = character == null ? k_geometryLocalToOwner : character.geometryToOwner;
            w.registry.RegisterAuthored(w.source, w.root, body, w.shape, false, geometryToOwner);

            // What is drawn follows the physics owners, which is what makes the placements in these cases the real
            // actors' and not a value a test supplied.
            w.lookup = new PhysicsOwnerPlacementLookup(w.registry);
            w.display.Placement = w.lookup;

            // The display geometry of that same body, in the same coordinates, registered with both the display and
            // the DAG -- the one mapping, given by the caller in both places.
            w.baseGeometry = character == null ? AppendBox(w.storage) : AppendCharacter(w.storage, character);
            w.dag.RegisterBaseGeometry(w.source, w.baseGeometry, geometryToOwner.inverse);
            bool shown = w.display.TryShow(
                    w.source, w.baseGeometry, w.root.transform.localToWorldMatrix * geometryToOwner, geometryToOwner.inverse,
                    Array.Empty<VpClipBoundary>());
            if (expectFrameRejection)
            {
                Assert.That(shown, Is.False, "scaled lineage mapping must be rejected by the existing contract");
                Assert.That(w.display.VertexTransfers, Is.Zero);
                Assert.That(w.display.IndexTransfers, Is.Zero);
                return w;
            }
            Assert.That(shown,
                Is.True,
                "the body is shown");

            var driverObject = Track(new GameObject("Provisional Cut Driver"));
            w.driver = driverObject.AddComponent<ProvisionalCutDriver>();
            w.Ending.BindAttempted();
            w.driver.Bind(
                w.ledger, w.registry, w.cook, w.frame, w.display, SupportEpsilon, AnchorEpsilon, VertexLimit,
                () => _frame, w.dag);
            w.Ending.Bound(w.driver);
            return w;
        }

        private Dictionary<int, Material> Materials()
        {
            Shader shader = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
            return new Dictionary<int, Material>
            {
                { SideMaterial, Track(new Material(shader) { name = "side" }) },
                { EndMaterial, Track(new Material(shader) { name = "end" }) },
            };
        }

        // A closed box on eight corners: four side quads in one submesh and the two ends in another.
        private static readonly float3[] k_corners =
        {
            new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, -1f, 1f), new float3(-1f, -1f, 1f),
            new float3(-1f, 1f, -1f), new float3(1f, 1f, -1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
        };

        private static readonly (int[] cycle, int submesh)[] k_faces =
        {
            (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0), (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
            (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
        };

        private static VpStoredGeometry AppendBox(VpCpuGeometryStorage storage)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int face = 0; face < k_faces.Length; face++)
                {
                    if (k_faces[face].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[face].cycle;
                    float3 normal = math.normalize(math.cross(
                        k_corners[c[1]] - k_corners[c[0]], k_corners[c[2]] - k_corners[c[0]]));
                    uint b = (uint)vertices.Count;
                    var uv = new[]
                    {
                        new float2(0.05f, 0.1f), new float2(0.95f, 0.1f),
                        new float2(0.95f, 0.9f), new float2(0.05f, 0.9f),
                    };
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex
                        {
                            position = k_corners[c[k]],
                            normal = normal,
                            uv0 = uv[k],
                        });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(
                    start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            Assert.That(
                storage.TryAppendCuttable(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_corners.Length, submeshes.ToArray(),
                    out VpStoredGeometry geometry, out _),
                Is.True,
                "the display geometry of the body is appended");
            return geometry;
        }

        private Mesh BoxColliderOf(ConvexBrepBank bank, ConvexBrepRange range, string name)
        {
            var lo = new float3(float.PositiveInfinity);
            var hi = new float3(float.NegativeInfinity);
            for (int v = 0; v < range.vertexCount; v++)
            {
                float3 at = bank.vertices[range.vertexBase + v];
                lo = math.min(lo, at);
                hi = math.max(hi, at);
            }

            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x, lo.y, lo.z),
                new Vector3(hi.x, hi.y, lo.z), new Vector3(lo.x, hi.y, lo.z),
                new Vector3(lo.x, lo.y, hi.z), new Vector3(hi.x, lo.y, hi.z),
                new Vector3(hi.x, hi.y, hi.z), new Vector3(lo.x, hi.y, hi.z),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
            };
            UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
            _meshes.Add(mesh);
            return mesh;
        }

        // ----- driving the one product entrance ------------------------------------------------------------------------

        private static ProvisionalCutAsk Ask(LogicalFragmentId source, float4 plane)
        {
            return new ProvisionalCutAsk { source = source, plane = plane, renderAnchor = float3.zero };
        }

        private ProvisionalCutTransaction RequestPublished(World w, LogicalFragmentId source, float4 plane)
        {
            ProvisionalCutAsk ask = Ask(source, plane);
            Assert.That(
                w.driver.RequestCut(in ask, out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission),
                Is.EqualTo(ProvisionalCutAcceptance.Published),
                "the cut was accepted and its pair published");
            Assert.That(admission, Is.EqualTo(LogicalCutAdmission.Admitted));
            return transaction;
        }

        /// <summary>One frame of the product update, with the display's own collection after it.</summary>
        private void Advance(World w)
        {
            w.driver.Advance(_frame);
            w.driver.DriveLateUpdate();
            _frame++;
        }

        private void RunUntil(World w, Func<bool> condition, string what)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < DeadlineMilliseconds)
            {
                Advance(w);
                if (condition())
                {
                    return;
                }

                System.Threading.Thread.Sleep(1);
            }

            Assert.Fail(what + ": it had not happened within the deadline");
        }

        private static LogicalCutOperation OperationOf(World w, CutOperationId operation)
        {
            Assert.That(w.ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
            return record;
        }

        /// <summary>How one branch is really drawn, read from the collection the display settled.</summary>
        private static Matrix4x4 DrawnPlacement(World w, LogicalFragmentId fragment)
        {
            for (int r = 0; r < w.display.RenderFragmentCount; r++)
            {
                Assert.That(w.display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                if (rf.root == fragment)
                {
                    return rf.geometryLocalToWorld;
                }
            }

            Assert.Fail("nothing is drawn for " + fragment);
            return default;
        }

        /// <summary>
        /// The whole placement, not just where its origin lands: points away from the origin and on each axis are
        /// compared too, so a rotation that is not followed shows.
        /// </summary>
        private static void AssertDrawnAt(World w, LogicalFragmentId fragment, Transform actor, string what)
        {
            Matrix4x4 drawn = DrawnPlacement(w, fragment);
            Matrix4x4 expected = actor.localToWorldMatrix * k_geometryLocalToOwner;
            foreach (Vector3 point in k_probes)
            {
                Vector3 was = drawn.MultiplyPoint3x4(point);
                Vector3 should = expected.MultiplyPoint3x4(point);
                Assert.That(
                    (was - should).magnitude, Is.LessThan(1e-4f),
                    what + ", at " + point.ToString("F2") + ": expected " + should.ToString("F5")
                    + ", was " + was.ToString("F5"));
            }
        }

        /// <summary>The origin and one point on each axis: enough for a rotation to show.</summary>
        private static readonly Vector3[] k_probes =
        {
            Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
            new Vector3(0.7f, -0.3f, 0.5f),
        };

        // ----- 1. physics does not wait for geometry -------------------------------------------------------------------

        /// <summary>
        /// **The physics goes first and the geometry catches up.** With the geometry work held back, the cut is
        /// published, handed over and its two children exist while the geometry is still running; the real commit,
        /// when it arrives, gives each child its own geometry and is what returns the operation's incomplete unit.
        /// </summary>
        [Test]
        public void TheGeometryArrivingLate_DoesNotHoldUpThePhysics_AndIsAppliedWhenItArrives()
        {
            using (World w = NewWorld())
            {
                w.geometryPool.HoldEverything = true;
                ProvisionalCutTransaction transaction = RequestPublished(w, w.source, new float4(0f, 1f, 0f, 0f));
                CutOperationId operation = transaction.Operation;
                Assert.That(w.dag.ActiveCount, Is.EqualTo(1), "the one acceptance registered the one geometry work");

                RunUntil(w, () => transaction.Phase == ProvisionalCutPhase.HandedOff, "the handoff happens");

                LogicalCutOperation published = OperationOf(w, operation);
                Assert.That(
                    published.positive.IsSet && published.negative.IsSet, Is.True,
                    "the two children are published while the geometry is still out");
                Assert.That(
                    w.dag.StageOf(operation), Is.Not.EqualTo(CutGeometryStage.Committed),
                    "and the geometry has not been committed");
                Assert.That(w.commit.Commits, Is.Zero);
                Assert.That(
                    w.ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1),
                    "the operation's incomplete unit is still held: the geometry duty is not done");
                Assert.That(
                    w.dag.TryGetGeometry(published.positive, out VpStoredGeometry _), Is.False,
                    "and neither child is a geometry of its own yet");
                Assert.That(w.dag.TryGetGeometry(published.negative, out VpStoredGeometry _), Is.False);

                // The late arrival.
                w.geometryPool.ReleaseEverything();
                RunUntil(w, () => w.dag.StageOf(operation) == CutGeometryStage.Committed, "the geometry commits");

                Assert.That(w.commit.Commits, Is.EqualTo(1), "through the real display commit");
                Assert.That(w.fault.faults, Is.Empty, "and nothing failed");
                Assert.That(
                    w.ledger.Budget.IncompleteCutOperationCount, Is.Zero,
                    "the incomplete unit goes back with the geometry duty, and not before");
                Assert.That(
                    w.dag.TryGetGeometry(published.positive, out VpStoredGeometry positiveGeometry), Is.True,
                    "each child is its own geometry from here");
                Assert.That(w.dag.TryGetGeometry(published.negative, out VpStoredGeometry negativeGeometry), Is.True);
                Assert.That(positiveGeometry.Equals(negativeGeometry), Is.False, "two sides, not one");
                Assert.That(
                    positiveGeometry.Equals(w.baseGeometry), Is.False,
                    "and neither is the body they were cut from");
            }
        }

        // ----- 2. geometry does not publish before the logical children ------------------------------------------------

        /// <summary>
        /// **The geometry waits for its own publication.** With the physics cook held back, the geometry result is
        /// ready and is *not* committed: the display still shows the body. It is committed once the handoff has
        /// published the two children, and not one moment earlier.
        /// </summary>
        [Test]
        public void TheGeometryFinishingFirst_IsNotCommittedUntilTheChildrenArePublished()
        {
            using (World w = NewWorld())
            {
                w.physicsJob.HoldEverything = true;
                ProvisionalCutTransaction transaction = RequestPublished(w, w.source, new float4(0f, 1f, 0f, 0f));
                CutOperationId operation = transaction.Operation;

                RunUntil(
                    w, () => w.dag.StageOf(operation) == CutGeometryStage.CpuPublished,
                    "the geometry kernel finishes first");

                Assert.That(w.commit.Commits, Is.Zero, "nothing has been committed");
                Assert.That(
                    OperationOf(w, operation).state, Is.EqualTo(LogicalCutOperationState.Admitted),
                    "because the operation is not published yet");
                Assert.That(
                    w.dag.TryGetGeometry(w.source, out VpStoredGeometry still) && still.Equals(w.baseGeometry),
                    Is.True,
                    "and the body is still its own geometry");
                Assert.That(
                    transaction.Phase, Is.Not.EqualTo(ProvisionalCutPhase.HandedOff),
                    "the physics is the one that is waiting here");

                w.physicsJob.ReleaseEverything();
                RunUntil(w, () => transaction.Phase == ProvisionalCutPhase.HandedOff, "the handoff happens");
                RunUntil(w, () => w.dag.StageOf(operation) == CutGeometryStage.Committed, "and then the commit does");

                LogicalCutOperation published = OperationOf(w, operation);
                Assert.That(w.commit.Commits, Is.EqualTo(1));
                Assert.That(w.dag.TryGetGeometry(published.positive, out VpStoredGeometry _), Is.True);
                Assert.That(
                    w.dag.TryGetGeometry(w.source, out VpStoredGeometry _), Is.False,
                    "and the body it replaced is no longer a geometry of its own");
            }
        }

        // ----- 3. a child cut while its parent's geometry is still out -------------------------------------------------

        /// <summary>
        /// **A published child can be cut at once, and its geometry waits for its parent's commit.** While the parent
        /// is uncommitted the child's kernel is not offered to any destination at all; the parent's commit is what
        /// makes it ready, and it is really submitted **in the same update** that commit happened in, out of that
        /// frame's remaining budget.
        /// </summary>
        [Test]
        public void ACutOfAChild_WaitsForTheParentsCommit_AndIsSubmittedInThatSameUpdate()
        {
            using (World w = NewWorld())
            {
                w.geometryPool.HoldEverything = true;
                ProvisionalCutTransaction first = RequestPublished(w, w.source, new float4(0f, 1f, 0f, 0f));
                RunUntil(w, () => first.Phase == ProvisionalCutPhase.HandedOff, "the parent's handoff happens");
                LogicalCutOperation parent = OperationOf(w, first.Operation);

                // The child is cut while the parent's geometry is still running.
                ProvisionalCutTransaction second = RequestPublished(w, parent.positive, new float4(1f, 0f, 0f, 0f));
                Assert.That(w.dag.ActiveCount, Is.EqualTo(2), "two admitted cuts, two geometry works");
                Assert.That(
                    w.dag.StageOf(second.Operation), Is.EqualTo(CutGeometryStage.WaitingForBasis),
                    "the child's kernel waits for the geometry it is to be cut from");

                int offeredBefore = w.geometryPool.Accepted.Count;
                Assert.That(offeredBefore, Is.EqualTo(1), "only the parent's kernel has been offered so far");

                // **Only the parent's** work is let go, so what follows is the parent's commit and what that commit
                // makes possible -- and nothing else. Which update commits is the display's to decide; what is watched
                // here is that the child's kernel is offered **in that one**, and not in a later one.
                w.geometryPool.Release(0);
                int offeredAtTheCommit = -1;
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    int offered = w.geometryPool.Accepted.Count;
                    Assert.That(
                        offered, Is.EqualTo(offeredBefore),
                        "nothing of the child was offered while its parent was uncommitted");
                    Advance(w);
                    if (w.dag.StageOf(first.Operation) == CutGeometryStage.Committed)
                    {
                        offeredAtTheCommit = w.geometryPool.Accepted.Count;
                        break;
                    }

                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(
                    w.dag.StageOf(first.Operation), Is.EqualTo(CutGeometryStage.Committed),
                    "the parent's geometry committed");
                Assert.That(
                    offeredAtTheCommit, Is.EqualTo(offeredBefore + 1),
                    "and the child's kernel was really offered to the destination in that same update");
                Assert.That(
                    w.dag.StageOf(second.Operation), Is.EqualTo(CutGeometryStage.Running),
                    "which is what it had been waiting for");

                w.geometryPool.ReleaseEverything();

                RunUntil(w, () => second.Phase == ProvisionalCutPhase.HandedOff, "the child's own handoff happens");
                RunUntil(
                    w, () => w.dag.StageOf(second.Operation) == CutGeometryStage.Committed,
                    "and the child's geometry commits in its turn");
                Assert.That(w.commit.Commits, Is.EqualTo(2), "two real commits");
                Assert.That(w.fault.faults, Is.Empty);
                Assert.That(w.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "both duties are done");
            }
        }

        // ----- 4. what is drawn follows the real actors ----------------------------------------------------------------

        /// <summary>
        /// **The drawing follows the actors, before the commit and after it.** The published pair's own actors are
        /// what the body's two sides are drawn at while the geometry is uncommitted, and once the commit has made the
        /// children their own geometry each child is drawn where its own actor stands — including after the actor is
        /// moved again.
        /// </summary>
        [Test]
        public void WhatIsDrawn_FollowsTheRealActors_BeforeAndAfterTheCommit()
        {
            using (World w = NewWorld())
            {
                w.geometryPool.HoldEverything = true;
                ProvisionalCutTransaction transaction = RequestPublished(w, w.source, new float4(0f, 1f, 0f, 0f));
                RunUntil(w, () => transaction.Phase == ProvisionalCutPhase.HandedOff, "the handoff happens");
                LogicalCutOperation published = OperationOf(w, transaction.Operation);
                w.registry.TryGet(published.positive, out PhysicsFragmentOwner positive);
                w.registry.TryGet(published.negative, out PhysicsFragmentOwner negative);

                // Before the commit: the actor is moved **and turned**, and the branch drawn for it follows both.
                // The body itself is not drawn any more -- it has been replaced by its two children, which is the
                // logical publication, not the commit.
                positive.Root.transform.SetPositionAndRotation(
                    new Vector3(3f, 1f, -2f), Quaternion.Euler(15f, 40f, -25f));
                Advance(w);
                AssertDrawnAt(
                    w, published.positive, positive.Root.transform,
                    "the positive branch is drawn at its own actor before any commit");
                AssertDrawnAt(
                    w, published.negative, negative.Root.transform, "and the negative one at its own");

                w.geometryPool.ReleaseEverything();
                RunUntil(
                    w, () => w.dag.StageOf(transaction.Operation) == CutGeometryStage.Committed,
                    "the geometry commits");
                Advance(w);

                AssertDrawnAt(
                    w, published.positive, positive.Root.transform,
                    "the positive child is drawn at its actor, turned as it is, after the commit");
                AssertDrawnAt(
                    w, published.negative, negative.Root.transform, "and the negative child at its own");

                // And it goes on following, through another move and another turn.
                positive.Root.transform.SetPositionAndRotation(
                    new Vector3(-1.5f, 4f, 0.5f), Quaternion.Euler(-60f, 10f, 35f));
                negative.Root.transform.SetPositionAndRotation(
                    new Vector3(2f, -3f, 1f), Quaternion.Euler(5f, -80f, 0f));
                Advance(w);
                AssertDrawnAt(
                    w, published.positive, positive.Root.transform,
                    "the child follows its actor after the commit as well");
                AssertDrawnAt(
                    w, published.negative, negative.Root.transform, "and so does its sibling");
            }
        }

        // ----- 5. ending a cut while its geometry is running -----------------------------------------------------------

        /// <summary>
        /// **An explicit ending gives both sides' resources back, once, and not before they are free.** A cut ended
        /// while its geometry result has not been collected publishes nothing: what that geometry reserved in the
        /// storage stays reserved until the work is taken back, and goes back exactly once when it is. The physics
        /// side ends the ordinary way of DESIGN 7.1.1, which retires the source — so the body the cut was of is gone
        /// from the display too, without anything ever having been committed.
        /// <para>
        /// **What is held here is the collection, not the running.** This destination runs its work as usual; it
        /// hands the finished work back only when the test lets it. So "not collected" is what these steps are about,
        /// and a claim about a kernel still computing would not be this test's to make.
        /// </para>
        /// <para>
        /// The last steps use the **frame alone**, with no call to the driver at all: the geometry left over is the
        /// frame's to carry, which is what makes it survive a driver that has gone.
        /// </para>
        /// </summary>
        [Test]
        public void EndingACutWhoseGeometryIsNotCollected_CommitsNothing_AndGivesBothSidesBackOnce()
        {
            using (World w = NewWorld())
            {
                int freeIndexRoomBefore = w.storage.FreeIndexRoom;
                w.geometryPool.HoldEverything = true;
                ProvisionalCutTransaction transaction = RequestPublished(w, w.source, new float4(0f, 1f, 0f, 0f));
                CutOperationId operation = transaction.Operation;
                RunUntil(
                    w, () => w.dag.StageOf(operation) == CutGeometryStage.Running,
                    "the geometry work has been offered and is not collected");

                int freeIndexRoomWhileOut = w.storage.FreeIndexRoom;
                Assert.That(
                    freeIndexRoomWhileOut, Is.LessThan(freeIndexRoomBefore),
                    "the cut reserved room in the storage for what it is producing");

                // The two things it holds while it runs, watched directly: its reservation of the storage's room, and
                // its read hold on the geometry it is cutting.
                VpStorageCutRequest running = w.dag.RequestOf(operation);
                Assert.That(running, Is.Not.Null, "the geometry cut is the DAG's, and running");
                Assert.That(running.HoldsReservation, Is.True, "it holds a reservation of the storage's room");
                Assert.That(
                    w.storage.TryGetIndexState(
                        w.baseGeometry.indexRange, out VpIndexRangeState sourceState, out _, out int baseIndexCount),
                    Is.True);
                Assert.That(
                    sourceState, Is.EqualTo(VpIndexRangeState.Published),
                    "and the geometry it reads is the body's, published");

                Assert.That(w.driver.EndCut(operation), Is.True, "the cut is ended explicitly");
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "the pair is out of the scene");

                // Asked to end, and not collected yet. The cut is given up rather than interrupted, so what it is
                // running with is still its own.
                Advance(w);
                Assert.That(
                    w.dag.IsDrained, Is.False, "the work it was given up with has not come back yet");
                Assert.That(running.HoldsReservation, Is.True, "its reservation is still held");
                Assert.That(
                    w.storage.FreeIndexRoom, Is.EqualTo(freeIndexRoomWhileOut),
                    "so the room it reserved is still taken");
                Assert.That(
                    w.storage.TryGetIndexState(w.baseGeometry.indexRange, out sourceState, out _, out _), Is.True);
                Assert.That(
                    sourceState, Is.EqualTo(VpIndexRangeState.Retiring),
                    "and the body's geometry, retired with its fragment, is waiting on this cut's read hold");

                // Collected -- through the frame alone, with the driver not called at all.
                w.geometryPool.ReleaseEverything();
                var clock = Stopwatch.StartNew();
                while (!w.dag.IsDrained && clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    w.frame.Update(_frame++);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(
                    w.dag.IsDrained, Is.True, "the frame alone carried the leftover geometry to its end");
                Assert.That(w.dag.ActiveCount, Is.Zero);

                Assert.That(
                    w.dag.StageOf(operation), Is.EqualTo(CutGeometryStage.Reclaimed),
                    "the geometry ended without a commit");
                Assert.That(w.commit.Commits, Is.Zero, "nothing was committed");
                Assert.That(w.fault.faults, Is.Empty, "an ending is not a geometry failure");
                Assert.That(running.HoldsReservation, Is.False, "the reservation is closed now that the work is back");
                Assert.That(
                    w.storage.TryGetIndexState(w.baseGeometry.indexRange, out sourceState, out _, out _), Is.True);
                Assert.That(
                    sourceState, Is.EqualTo(VpIndexRangeState.Free),
                    "and the read hold on the body's geometry is gone, so that range is free");

                // The two contributions, told apart: the room this cut had reserved, which goes back to where it was,
                // and the body's own geometry, which went back because the abort retired it.
                int afterCollection = w.storage.FreeIndexRoom;
                Assert.That(
                    afterCollection, Is.EqualTo(freeIndexRoomBefore + baseIndexCount),
                    "exactly the cut's reservation and the retired body's own range, and nothing else: "
                    + freeIndexRoomBefore + " + " + baseIndexCount + " was expected, " + afterCollection + " found");
                Assert.That(
                    w.ledger.Budget.IncompleteCutOperationCount, Is.Zero,
                    "the incomplete unit went back with it, once");

                // Going round again gives nothing back a second time.
                for (int i = 0; i < 3; i++)
                {
                    w.frame.Update(_frame++);
                }

                Assert.That(
                    w.storage.FreeIndexRoom, Is.EqualTo(afterCollection),
                    "and nothing was given back twice");
                Assert.That(w.dag.ActiveCount, Is.Zero);
                Assert.That(transaction.Products, Is.Null, "the physics record gave its own back");
                Assert.That(w.driver.Transactions.Count, Is.Zero);

                // Nothing of the cut was published, so nothing took the body's place. The body itself went with the
                // ending, because an explicit end of an accepted cut is the abort of DESIGN 7.1.1 and that retires the
                // source -- which the display follows by showing nothing for it.
                Assert.That(
                    w.ledger.TryGetFragmentState(w.source, out LogicalFragmentState state)
                        && state == LogicalFragmentState.Retired,
                    Is.True,
                    "the source was retired with the abort");
                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner _), Is.False, "and so was its owner");
                Advance(w);
                Assert.That(w.display.ShownCount, Is.Zero, "the display shows nothing for a retired body");
                Assert.That(w.commit.Commits, Is.Zero, "and no commit ever happened for this cut");
            }
        }

        // ----- 5b. what the last turn of the frame brought back --------------------------------------------------------

        /// <summary>
        /// **A result that comes back in the last turn of a frame is handed over in that same update.** Two cuts are
        /// in flight; the second one's work is let through and the first one's is released only at the moment the
        /// second is handed over — which happens inside the collection, so the first one's cook can only be collected
        /// by a **further** turn of the frame. Both are handed over in the one call to the product entrance.
        /// <para>
        /// A test that simply ran frames until everything finished would pass either way, so the order is fixed here
        /// rather than waited for.
        /// </para>
        /// </summary>
        [Test]
        public void AResultCollectedInTheLastTurnOfTheFrame_IsHandedOverInThatSameUpdate()
        {
            // Two cuts are in flight at once here, so the cook must be able to reserve for two: with one reservation
            // the held cut would keep it and the other could never start.
            using (World w = NewWorld(concurrentReservations: 2))
            {
                // The first cut, all the way, so that its two children are two sources of their own.
                ProvisionalCutTransaction parent = RequestPublished(w, w.source, new float4(0f, 1f, 0f, 0f));
                RunUntil(
                    w, () => w.dag.StageOf(parent.Operation) == CutGeometryStage.Committed,
                    "the first cut is finished on both sides");
                LogicalCutOperation published = OperationOf(w, parent.Operation);

                // The first of the two cuts is carried to **one collection short of finishing**: its meshes are
                // applied and its bake is with the destination, held there. Collecting that bake is what will finish
                // its cook -- which is exactly the case this is about.
                //
                // Nothing of it is collected unless this lets it through, and it lets through only what the
                // destination was given **before** the round: so whatever work appears in the round that reaches the
                // bake -- that bake -- stays held.
                w.physicsJob.HoldEverything = true;
                ProvisionalCutTransaction held = RequestPublished(w, published.positive, new float4(1f, 0f, 0f, 0f));
                int letThrough = 0;
                var toTheBake = Stopwatch.StartNew();
                while (toTheBake.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    if (held.Cut != null && held.Cut.Stage == PhysicsCutStage.Baking)
                    {
                        break;
                    }

                    while (letThrough < w.physicsJob.Accepted.Count)
                    {
                        w.physicsJob.Release(letThrough);
                        letThrough++;
                    }

                    Advance(w);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(
                    held.Cut, Is.Not.Null, "the held cut still has its request");
                Assert.That(
                    held.Cut.Stage, Is.EqualTo(PhysicsCutStage.Baking),
                    "its bake is with the destination, and held there");
                Assert.That(
                    w.physicsJob.Holding.Count, Is.EqualTo(1), "that bake is the one work the destination holds");

                // **Finished, and only its collection pending.** Being at the bake and uncollected is not evidence
                // that the bake is done; without this the case would be about a work that had not finished, which any
                // implementation would leave for a later update.
                IDispatchWork bake = w.physicsJob.Holding[0];
                var toFinish = Stopwatch.StartNew();
                while (!bake.IsComplete && toFinish.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(
                    bake.IsComplete, Is.True,
                    "the bake has finished: what is left of this cut is one collection and nothing else");
                Assert.That(
                    held.Cut.IsOver, Is.False, "and it has not been collected, so the cook is not over");

                // What is asked for **after this** may be collected as soon as it finishes.
                w.physicsJob.LetNewWorkThrough = true;
                ProvisionalCutTransaction goesFirst = RequestPublished(w, published.negative, new float4(1f, 0f, 0f, 0f));

                // The held one is released **at the moment the other is handed over**, which happens inside the
                // collection: so its own cook can only be collected by a further turn of the frame.
                int releases = 0;
                FinalHandoffPublication.publishedHook = () =>
                {
                    releases++;
                    w.physicsJob.ReleaseEverything();
                };

                try
                {
                    var clock = Stopwatch.StartNew();
                    while (goesFirst.Phase != ProvisionalCutPhase.HandedOff
                           && clock.ElapsedMilliseconds < DeadlineMilliseconds)
                    {
                        // Up to the update in which the first of the two is handed over.
                        Assert.That(
                            held.Phase, Is.Not.EqualTo(ProvisionalCutPhase.HandedOff),
                            "the held cut is not handed over before the other one is");
                        Advance(w);
                        System.Threading.Thread.Sleep(1);
                    }

                    Assert.That(
                        goesFirst.Phase, Is.EqualTo(ProvisionalCutPhase.HandedOff),
                        "one of the two was handed over");
                    Assert.That(releases, Is.GreaterThan(0), "which is what released the other one's work");
                    Assert.That(
                        held.Phase, Is.EqualTo(ProvisionalCutPhase.HandedOff),
                        "and the other was handed over in that same update, not at the next call");
                }
                finally
                {
                    FinalHandoffPublication.publishedHook = null;
                }

                Assert.That(
                    w.driver.Transactions.Count, Is.Zero, "neither record is left waiting for another update");
            }
        }

        // ----- 6. one acceptance, one of each -------------------------------------------------------------------------

        /// <summary>
        /// **One request, one operation, one geometry work — and a no-op leaves neither.** The plane that leaves one
        /// side of the shape empty is refused before the ledger is asked, so there is no operation for a geometry work
        /// to belong to.
        /// </summary>
        [Test]
        public void OneRequestLeavesOneOperationAndOneGeometryWork_AndANoOpLeavesNeither()
        {
            using (World w = NewWorld())
            {
                w.geometryPool.HoldEverything = true;
                w.physicsJob.HoldEverything = true;

                // A plane that misses the body entirely: the support scan says there is no cut here.
                var missing = new ProvisionalCutAsk
                {
                    source = w.source,
                    plane = new float4(0f, 1f, 0f, -100f),
                    renderAnchor = float3.zero,
                };
                Assert.That(
                    w.driver.RequestCut(in missing, out ProvisionalCutTransaction none, out LogicalCutAdmission said),
                    Is.EqualTo(ProvisionalCutAcceptance.EmptySide));
                Assert.That(said, Is.EqualTo(LogicalCutAdmission.NoOp), "nothing was admitted");
                Assert.That(none, Is.Null);
                Assert.That(w.dag.ActiveCount, Is.Zero, "and no geometry work was registered for it");
                Assert.That(w.ledger.Budget.IncompleteCutOperationCount, Is.Zero);
                Assert.That(w.geometryPool.Accepted, Is.Empty, "nothing was offered to a destination either");

                ProvisionalCutTransaction transaction = RequestPublished(w, w.source, new float4(0f, 1f, 0f, 0f));
                Assert.That(w.dag.ActiveCount, Is.EqualTo(1), "the one acceptance has one geometry work");
                Assert.That(
                    w.ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1),
                    "and one incomplete operation, not two");
                Assert.That(
                    w.dag.StageOf(transaction.Operation), Is.Not.EqualTo(CutGeometryStage.Reclaimed),
                    "which is this cut's own");
                Assert.That(w.driver.Transactions.Count, Is.EqualTo(1), "one physics record for it");
            }
        }
    }
}
