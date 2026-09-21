using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A cut world put together by the product's own composition root, carried by **Unity's own update loop**
    /// (DESIGN 4.5.6, 7.1, 7.2): a body is registered, a cut is asked for, and everything after that — the Provisional
    /// pair, the cook, the Final handoff, the display geometry commit, and a cut of one of the children — happens
    /// because the driver's <c>Update</c> and <c>LateUpdate</c> run, not because this test called them.
    /// <para>
    /// **Nothing here drives.** <c>Advance</c>, <c>DriveUpdate</c> and <c>DriveLateUpdate</c> are never called from a
    /// test; each case yields frames and watches. What a case does control, where the order has to be fixed, is when a
    /// finished work is handed back -- at the destination, which is a joint the product already has -- and never what
    /// the root or the driver do with it.
    /// </para>
    /// <para>
    /// **Not here**: hit detection, the impulse of a cut (the two values a case gives are a test's input and not a
    /// product value), the building constraint, Character, XR, drawing and any measurement. The simulation runs as
    /// Unity runs it; nothing is stepped by hand.
    /// </para>
    /// </summary>
    public unsafe class CutWorldRootPlayModeTests
    {
        private const double ParentMass = 12.0;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const float DeadlineSeconds = 60f;

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private readonly List<IDisposable> _banks = new List<IDisposable>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private CutWorldProfile _profile;

        [TearDown]
        public void Cleanup()
        {
            foreach (IDisposable disposable in _disposables)
            {
                disposable.Dispose();
            }

            _disposables.Clear();
            foreach (UnityEngine.Object tracked in _objects)
            {
                if (tracked != null)
                {
                    UnityEngine.Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
            foreach (IDisposable bank in _banks)
            {
                bank.Dispose();
            }

            _banks.Clear();
            if (_profile != null)
            {
                UnityEngine.Object.DestroyImmediate(_profile);
                _profile = null;
            }
        }

        private T Track<T>(T tracked)
            where T : UnityEngine.Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        // ----- a destination whose collection a case controls -----------------------------------------------------------

        /// <summary>
        /// One of the product's destinations, wrapped: the work runs as usual and is handed back only once the case
        /// lets it. It holds the **collection**, never the running, and it is the seam the product already has.
        /// </summary>
        private sealed class HoldingExecutor : IWorkExecutor
        {
            private readonly IWorkExecutor _inner;
            private readonly List<IDispatchWork> _held = new List<IDispatchWork>();
            private readonly List<IDispatchWork> _letThrough = new List<IDispatchWork>();

            internal HoldingExecutor(IWorkExecutor inner)
            {
                _inner = inner;
            }

            internal bool HoldEverything { get; set; }

            /// <summary>What it is holding back from the collection now.</summary>
            internal IReadOnlyList<IDispatchWork> Holding => _held;

            public WorkDestination Destination => _inner.Destination;

            public int Capacity => _inner.Capacity;

            public int Held => _inner.Held;

            public bool CanAccept => _inner.CanAccept;

            public bool TryAccept(IDispatchWork work)
            {
                return _inner.TryAccept(work);
            }

            public void BeginAccepted(IDispatchWork work)
            {
                _inner.BeginAccepted(work);
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                // One that the case let through by itself, while the rest stay held.
                if (_letThrough.Count > 0)
                {
                    work = _letThrough[0];
                    _letThrough.RemoveAt(0);
                    completion = WorkCompletion.Finished;
                    return true;
                }

                // What was held back is handed on first, once the case has let it go.
                if (!HoldEverything && _held.Count > 0)
                {
                    work = _held[0];
                    _held.RemoveAt(0);
                    completion = WorkCompletion.Finished;
                    return true;
                }

                if (!_inner.TryTakeFinished(out work, out completion))
                {
                    return false;
                }

                if (!HoldEverything)
                {
                    return true;
                }

                // Finished and taken from the destination, but not handed on: the case decides when the collection
                // sees it. The work itself ran to the end -- what is held is its collection.
                _held.Add(work);
                work = null;
                completion = default;
                return false;
            }

            internal void ReleaseEverything()
            {
                HoldEverything = false;
            }

            /// <summary>
            /// Lets the oldest held work through, leaving the rest held. A request goes through several stages at one
            /// destination, and holding every one of them stops it at the first: this is how a case carries one to the
            /// stage it wants and holds it there.
            /// </summary>
            internal bool TryLetOneThrough()
            {
                if (_held.Count == 0)
                {
                    return false;
                }

                _letThrough.Add(_held[0]);
                _held.RemoveAt(0);
                return true;
            }

            public void CloseForNewWork()
            {
                _inner.CloseForNewWork();
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                return _inner.StopAndConfirm(timeoutMilliseconds) && _held.Count == 0;
            }
        }

        // ----- the world, built by the product's own root ---------------------------------------------------------------

        /// <summary>
        /// The profile a case runs with. It is the product's own type with its own defaults; only the two epsilons and
        /// the sizes a case needs are named here, and what is not named is the profile's default.
        /// </summary>
        private CutWorldProfile NewProfile()
        {
            _profile = ScriptableObject.CreateInstance<CutWorldProfile>();
            return _profile;
        }

        private CutWorldRoot NewWorld(out Shader shader)
        {
            return NewWorld(out shader, null, null);
        }

        private CutWorldRoot NewWorld(
            out Shader shader, Func<WorkDestination, IWorkExecutor> executors, Action terminatePlayer)
        {
            shader = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
            Assert.That(shader, Is.Not.Null, "the display's shader is in this project");

            // Inactive first: a component added to an active object runs its Awake at once, and this one builds the
            // world there -- so the profile and the materials have to be on it before that.
            var rootObject = Track(new GameObject("Cut World"));
            rootObject.SetActive(false);
            CutWorldRoot root = rootObject.AddComponent<CutWorldRoot>();
            var profile = NewProfile();
            var materials = new[]
            {
                new CutWorldRoot.MaterialBinding
                {
                    sourceIndex = SideMaterial,
                    material = Track(new Material(shader) { name = "side" }),
                },
                new CutWorldRoot.MaterialBinding
                {
                    sourceIndex = EndMaterial,
                    material = Track(new Material(shader) { name = "end" }),
                },
            };

            // The two things a scene would carry on the component, given here because there is no scene asset.
            SetPrivate(root, "profile", profile);
            SetPrivate(root, "materials", materials);
            if (executors != null)
            {
                SetPrivate(root, "executors", executors);
            }

            if (terminatePlayer != null)
            {
                SetPrivate(root, "terminatePlayer", terminatePlayer);
            }

            // And now Awake runs, with everything it needs already there.
            rootObject.SetActive(true);
            Assert.That(root.IsReady, Is.True, "the root built the world in Awake");
            return root;
        }

        /// <summary>
        /// Reads what one frame's collection settled, **in that frame**: a coroutine that yields resumes after the
        /// updates and before the late updates, so what it sees is the frame before's. This runs after the driver's
        /// own late update -- by its execution order -- and writes down what was settled and when.
        /// <para>
        /// It drives nothing: it calls nothing of the driver or the root, and only reads.
        /// </para>
        /// </summary>
        [DefaultExecutionOrder(1000)]
        private sealed class LateFrameObserver : MonoBehaviour
        {
            internal CutWorldRoot world;
            internal LogicalFragmentId watched;

            /// <summary>The frame the last reading is of.</summary>
            internal int Frame { get; private set; } = -1;

            /// <summary>How many render fragments were drawn for the watched branch in that frame.</summary>
            internal int Drawn { get; private set; }

            /// <summary>How many Provisional pairs stood in the scene in that frame.</summary>
            internal int Pairs { get; private set; }

            private void LateUpdate()
            {
                if (world == null || !world.IsReady)
                {
                    return;
                }

                Frame = Time.frameCount;
                Drawn = DrawnFragmentsOf(world, watched);
                Pairs = world.Owners.ProvisionalPairCount;
            }
        }

        private static void SetPrivate(object target, string field, object value)
        {
            System.Reflection.FieldInfo info = target.GetType().GetField(
                field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, "the root has a " + field + " field");
            info.SetValue(target, value);
        }

        // ----- one authored body: a physics shape and a display geometry of the same box -------------------------------

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

        /// <summary>The B-rep of one box, with the edge table built from its own faces.</summary>
        private PhysicsOwnerShape NewBoxShape(out Mesh colliderMesh)
        {
            var faceOffsets = new[] { 0, 4, 8, 12, 16, 20, 24 };
            var faceIndices = new List<int>();
            foreach ((int[] cycle, int _) in k_faces)
            {
                faceIndices.AddRange(cycle);
            }

            BuildEdges(faceOffsets, faceIndices.ToArray(), out int[] faceEdges, out BrepEdge[] edges);

            var vertices = new NativeArray<float3>(k_corners, Allocator.Persistent);
            var offsets = new NativeArray<int>(faceOffsets, Allocator.Persistent);
            var indices = new NativeArray<int>(faceIndices.ToArray(), Allocator.Persistent);
            var edgesOfFaces = new NativeArray<int>(faceEdges, Allocator.Persistent);
            var edgeTable = new NativeArray<BrepEdge>(edges, Allocator.Persistent);
            _banks.Add(vertices);
            _banks.Add(offsets);
            _banks.Add(indices);
            _banks.Add(edgesOfFaces);
            _banks.Add(edgeTable);

            var bank = new ConvexBrepBank
            {
                vertices = (float3*)vertices.GetUnsafePtr(),
                faceOffsets = (int*)offsets.GetUnsafePtr(),
                faceIndices = (int*)indices.GetUnsafePtr(),
                faceEdges = (int*)edgesOfFaces.GetUnsafePtr(),
                edges = (BrepEdge*)edgeTable.GetUnsafePtr(),
            };

            var range = new ConvexBrepRange
            {
                vertexBase = 0, vertexCount = k_corners.Length,
                faceBase = 0, faceCount = k_faces.Length,
                faceIndexBase = 0, faceIndexCount = faceIndices.Count,
                edgeBase = 0, edgeCount = edges.Length,
                maxFaceLoop = 4,
            };

            colliderMesh = NewColliderMesh();
            var source = PhysicsShapeSource.External();
            return PhysicsOwnerShape.Authored(
                bank, new[] { range }, new List<Mesh> { colliderMesh }, source, float4x4.identity);
        }

        private Mesh NewColliderMesh()
        {
            var mesh = Track(new Mesh { name = "Authored collider", hideFlags = HideFlags.HideAndDontSave });
            var vertices = new Vector3[k_corners.Length];
            for (int i = 0; i < k_corners.Length; i++)
            {
                vertices[i] = k_corners[i];
            }

            mesh.vertices = vertices;
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
            };
            UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
            return mesh;
        }

        private static void BuildEdges(int[] faceOffsets, int[] faceIndices, out int[] faceEdges, out BrepEdge[] edges)
        {
            var found = new Dictionary<long, int>();
            var table = new List<BrepEdge>();
            faceEdges = new int[faceIndices.Length];
            for (int f = 0; f + 1 < faceOffsets.Length; f++)
            {
                int from = faceOffsets[f];
                int count = faceOffsets[f + 1] - from;
                for (int k = 0; k < count; k++)
                {
                    int a = faceIndices[from + k];
                    int b = faceIndices[from + ((k + 1) % count)];
                    int lo = math.min(a, b);
                    int hi = math.max(a, b);
                    long key = ((long)lo << 32) | (uint)hi;
                    if (!found.TryGetValue(key, out int at))
                    {
                        at = table.Count;
                        found.Add(key, at);
                        table.Add(new BrepEdge { v0 = lo, v1 = hi, f0 = -1, f1 = -1 });
                    }

                    BrepEdge edge = table[at];
                    if (a == lo)
                    {
                        if (edge.f0 < 0)
                        {
                            edge.f0 = f;
                        }
                    }
                    else if (edge.f1 < 0)
                    {
                        edge.f1 = f;
                    }

                    table[at] = edge;
                    faceEdges[from + k] = at;
                }
            }

            edges = table.ToArray();
        }

        /// <summary>The display geometry of that same box, appended to the world's storage as a cut input.</summary>
        private static VpStoredGeometry AppendBoxGeometry(VpCpuGeometryStorage storage, Vector3 offset)
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
                            position = k_corners[c[k]] + (float3)(Vector3)offset,
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

        /// <summary>One body in the world: its actor, its physics shape and its display geometry, all tied together.</summary>
        private LogicalFragmentId AddBody(CutWorldRoot root, Vector3 at)
        {
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxGeometry(root.Storage, Vector3.zero);

            var actor = Track(new GameObject("Body"));
            actor.transform.position = at;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);

            Assert.That(
                root.TryAddBody(
                    actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, null,
                    out LogicalFragmentId fragment),
                Is.True,
                "the body was taken into the world");
            return fragment;
        }

        private static ProvisionalCutAsk Ask(LogicalFragmentId source, float4 plane)
        {
            // The two impulses are this test's input, not a product value: DESIGN 7.2 leaves them to the caller.
            return new ProvisionalCutAsk
            {
                source = source,
                plane = plane,
                positiveSeparationImpulse = 0f,
                negativeSeparationImpulse = 0f,
                renderAnchor = float3.zero,
            };
        }

        /// <summary>
        /// Ends one world the way a caller should: the ending is asked for once and the **ordinary frames** carry it,
        /// and nothing is destroyed until it has finished. A world left unended would be destroyed mid-ending, which
        /// the root reports as the error it is.
        /// </summary>
        private static IEnumerator EndWorld(CutWorldRoot root)
        {
            if (root == null || root.IsReleased)
            {
                yield break;
            }

            root.Shutdown();
            yield return Until(() => root.IsReleased, "the world's ending finished on the ordinary frames");
        }

        private static IEnumerator Until(Func<bool> condition, string what)
        {
            float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, what + ": it had not happened within the deadline");
        }

        private static LogicalCutOperation OperationOf(CutWorldRoot root, CutOperationId operation)
        {
            Assert.That(root.Ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
            return record;
        }

        private static bool IsDrawn(CutWorldRoot root, LogicalFragmentId fragment)
        {
            return DrawnFragmentsOf(root, fragment) > 0;
        }

        /// <summary>How many render fragments the settled collection draws for one branch root.</summary>
        private static int DrawnFragmentsOf(CutWorldRoot root, LogicalFragmentId fragment)
        {
            int found = 0;
            for (int r = 0; r < root.Display.RenderFragmentCount; r++)
            {
                Assert.That(root.Display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                if (rf.root == fragment)
                {
                    found++;
                }
            }

            return found;
        }

        // ----- 1. the frame the cut was taken up in --------------------------------------------------------------------

        /// <summary>
        /// **The pair is published in the update the ask was taken up in, and both sides are drawn in that same
        /// frame's collection.** Nothing of the final cut is waited for: the cook is still running when the display
        /// has already settled two sides.
        /// </summary>
        [UnityTest]
        public IEnumerator AnAskTakenUpByTheUpdateLoop_IsPublishedAndDrawnInThatFrame()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);

            // One collection with the body itself, so that what follows is a change and not the first sight of it.
            yield return null;
            Assert.That(IsDrawn(root, body), Is.True, "the body is drawn before the cut");

            // What that frame's collection settled is read here, after the driver's own late update.
            LateFrameObserver observer = Track(new GameObject("Observer")).AddComponent<LateFrameObserver>();
            observer.world = root;
            observer.watched = body;

            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True, "the ask is noted for the driver's next update");

            // The very next frame: the driver's Update takes the ask up and publishes, its LateUpdate collects. A
            // coroutine resumes between the two, so this is where the publication is read and the observer is what
            // reads the collection.
            yield return null;
            int askedUpIn = Time.frameCount;

            Assert.That(
                root.Owners.ProvisionalPairCount, Is.EqualTo(1),
                "the Provisional pair was published in the update that took the ask up");
            Assert.That(
                root.Ledger.TryGetFragmentState(body, out LogicalFragmentState state)
                && state == LogicalFragmentState.Live,
                Is.True,
                "and the fragment is still the one live fragment: a Provisional publishes no logical child");

            ProvisionalCutTransaction transaction = null;
            foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
            {
                transaction = candidate;
            }

            Assert.That(transaction, Is.Not.Null, "the driver holds the record of that cut");
            LogicalCutOperation record = OperationOf(root, transaction.Operation);
            Assert.That(
                record.state, Is.EqualTo(LogicalCutOperationState.Admitted),
                "the cut is accepted and not published yet: its cook is still running");
            Assert.That(
                transaction.Phase, Is.Not.EqualTo(ProvisionalCutPhase.HandedOff),
                "the Final handoff is not a condition of what follows");

            // **Both sides are drawn in that same frame.** The observer's reading is of the frame the ask was taken
            // up in, after that frame's collection: two render fragments for the one body, one per side of the cut.
            yield return null;
            Assert.That(
                observer.Frame, Is.EqualTo(askedUpIn),
                "the reading is of the frame the cut was taken up in");
            Assert.That(observer.Pairs, Is.EqualTo(1), "the pair stood in the scene in that frame");
            Assert.That(
                observer.Drawn, Is.EqualTo(2),
                "and the body was drawn as the two sides of the cut in that frame's collection");

            yield return EndWorld(root);
        }

        // ----- 2. the whole way, and a cut of a child -------------------------------------------------------------------

        /// <summary>
        /// **The update loop carries a cut to its end by itself**: the Final handoff happens, the display geometry is
        /// really committed, and one of the two children can then be cut in its turn — all without a test calling the
        /// driver.
        /// </summary>
        [UnityTest]
        public IEnumerator TheUpdateLoopCarriesACutToItsCommit_AndAChildCanBeCutAgain()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;

            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;

            CutOperationId operation = default;
            foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
            {
                operation = candidate.Operation;
            }

            Assert.That(operation.IsSet, Is.True, "the cut was accepted");
            yield return Until(
                () => root.Geometry.StageOf(operation) == CutGeometryStage.Committed,
                "the cut finished on both sides, driven by the update loop");

            LogicalCutOperation record = OperationOf(root, operation);
            Assert.That(root.Owners.TryGet(record.positive, out PhysicsFragmentOwner positive), Is.True);
            Assert.That(root.Owners.TryGet(record.negative, out PhysicsFragmentOwner _), Is.True);
            Assert.That(
                root.Geometry.TryGetGeometry(record.positive, out VpStoredGeometry _), Is.True,
                "each child is its own display geometry now");
            Assert.That(root.Owners.ProvisionalPairCount, Is.Zero, "and no Provisional pair is left");
            Assert.That(root.GeometryFaults, Is.Zero, "nothing failed");

            // The child, cut in its turn, through the same entrance.
            ProvisionalCutAsk again = Ask(record.positive, new float4(1f, 0f, 0f, 0f));
            Assert.That(root.TryAsk(in again), Is.True);
            yield return null;

            Assert.That(
                root.Owners.ProvisionalPairCount, Is.EqualTo(1),
                "the child's own pair was published in the update that took its ask up");

            CutOperationId second = default;
            foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
            {
                second = candidate.Operation;
            }

            yield return Until(
                () => root.Geometry.StageOf(second) == CutGeometryStage.Committed,
                "and the child's cut finished on both sides as well");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(
                positive.IsWithdrawn || positive.IsReleased, Is.True,
                "the child that was cut has been replaced by its own two children");

            yield return EndWorld(root);
        }

        // ----- 3. two owners at once ------------------------------------------------------------------------------------

        /// <summary>
        /// **Two bodies cut in the same frame make progress side by side.** Both cuts are really submitted to a
        /// destination — the work is with an executor, not merely recorded somewhere — and one of them finishing is
        /// not what the other waits for.
        /// </summary>
        [UnityTest]
        public IEnumerator TwoOwnersCutAtOnce_BothReachTheirDestination_AndNeitherWaitsForTheOther()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId first = AddBody(root, new Vector3(-4f, 0f, 0f));
            LogicalFragmentId second = AddBody(root, new Vector3(4f, 0f, 0f));
            yield return null;

            ProvisionalCutAsk askFirst = Ask(first, new float4(0f, 1f, 0f, 0f));
            ProvisionalCutAsk askSecond = Ask(second, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in askFirst), Is.True);
            Assert.That(root.TryAsk(in askSecond), Is.True);
            yield return null;

            Assert.That(
                root.Owners.ProvisionalPairCount, Is.EqualTo(2),
                "both cuts were published in the one update that took both asks up");
            Assert.That(root.Driver.Transactions.Count, Is.EqualTo(2), "and both are the driver's records");

            // **Two numeric works of two different owners, with a destination at the same moment.** The count is
            // taken per destination: the physics of a cut goes to the Unity job destination and its display geometry
            // to the geometry pool, so the whole count could reach two with one owner's two duties. Two there means
            // two owners' physics.
            yield return Until(
                () => root.Dispatcher.SubmittedCountFor(WorkDestination.UnityJob) >= 2,
                "both owners' physics work was with the Unity job destination at the same time");

            yield return Until(
                () => root.Driver.Transactions.Count == 0,
                "and both were carried to their end");

            Assert.That(root.Owners.ProvisionalPairCount, Is.Zero, "neither pair is left standing");
            Assert.That(root.GeometryFaults, Is.Zero);

            yield return EndWorld(root);
        }

        // ----- 4. an ending with a work really held -------------------------------------------------------------------

        /// <summary>
        /// **An ending frees nothing while a work is out, and needs no hand driving afterwards.** The geometry work of
        /// a cut is fixed as finished-but-uncollected at its destination; the world is then ended. What that work
        /// holds — its reservation of the storage's room, and its read hold on the body's geometry — is still held
        /// while it is out, and the ending is only finished once the collection has really happened, which the
        /// ordinary frames do by themselves.
        /// </summary>
        [UnityTest]
        public IEnumerator EndingTheWorldWithAWorkHeld_FreesNothingEarly_AndFinishesOnTheOrdinaryFrames()
        {
            HoldingExecutor geometryPool = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination =>
                {
                    if (destination != WorkDestination.GeometryPool)
                    {
                        return null;
                    }

                    geometryPool = new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2));
                    geometryPool.HoldEverything = true;
                    return geometryPool;
                },
                null);

            Assert.That(geometryPool, Is.Not.Null, "the geometry destination is this case's to hold");
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            VpStoredGeometry geometry = root.Geometry.TryGetGeometry(body, out VpStoredGeometry found)
                ? found
                : default;
            Assert.That(
                root.Storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out _),
                Is.True);
            Assert.That(state, Is.EqualTo(VpIndexRangeState.Published), "the body's geometry is published");
            yield return null;

            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);

            CutOperationId operation = default;
            yield return Until(
                () =>
                {
                    foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
                    {
                        operation = candidate.Operation;
                    }

                    return operation.IsSet && root.Geometry.StageOf(operation) == CutGeometryStage.Running;
                },
                "the geometry work of the cut is running");

            // Fixed: finished at its destination and held back from the collection.
            yield return Until(
                () => geometryPool.Holding.Count > 0,
                "its work finished and is held back from the collection");
            VpStorageCutRequest running = root.Geometry.RequestOf(operation);
            Assert.That(running, Is.Not.Null, "the DAG still holds that cut");
            Assert.That(running.HoldsReservation, Is.True, "which holds a reservation of the storage's room");
            int freeWhileOut = root.Storage.FreeIndexRoom;

            // The ending is asked for while that work is out.
            Assert.That(root.Shutdown(), Is.False, "the ending cannot finish while a work is out");
            Assert.That(root.IsEnding, Is.True);
            Assert.That(root.IsReleased, Is.False, "so nothing has been freed");
            Assert.That(root.TryAsk(in ask), Is.False, "and nothing is accepted any more");

            // Frames pass, and still nothing is freed: the work is still out.
            yield return null;
            yield return null;
            Assert.That(root.IsReleased, Is.False, "nothing was freed while the work was still out");
            Assert.That(running.HoldsReservation, Is.True, "its reservation is still held");
            Assert.That(
                root.Storage.FreeIndexRoom, Is.EqualTo(freeWhileOut), "and the room it reserved is still taken");
            Assert.That(
                root.Storage.TryGetIndexState(geometry.indexRange, out state, out _, out _), Is.True,
                "the body's geometry is still there to be read");

            // Let it be collected. From here the ordinary frames finish the ending: nothing of the driver or the
            // frame is called by this case.
            geometryPool.ReleaseEverything();
            yield return Until(() => root.IsReleased, "the ending finished on the ordinary frames");

            Assert.That(running.HoldsReservation, Is.False, "the reservation went back with the work");
            Assert.That(root.IsDrained(), Is.True, "and nothing of the world is out any more");
        }

        // ----- 5. the termination request of DESIGN 4 --------------------------------------------------------------------

        /// <summary>
        /// **A shared geometry that cannot be cut ends the Player.** The body is registered with a display geometry
        /// that was never accepted as a cut input, so the cut of it fails in the geometry route; that failure is a
        /// cause of the common termination of DESIGN 4. The latch is fixed, new cuts are not accepted any more, no
        /// unpublished product is committed, and the Player's termination API — faked here — is called once, however
        /// many causes arrive.
        /// </summary>
        [UnityTest]
        public IEnumerator AGeometryThatCannotBeCut_RequestsThePlayersTermination_Once()
        {
            int terminations = 0;
            CutWorldRoot root = NewWorld(out Shader _, null, () => terminations++);

            // A geometry appended as an ordinary one, not as a cut input: the cut route refuses to read it.
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendPlainBoxGeometry(root.Storage);

            var actor = Track(new GameObject("Body"));
            var rigidbody = actor.AddComponent<Rigidbody>();
            rigidbody.useGravity = false;
            rigidbody.automaticCenterOfMass = false;
            rigidbody.automaticInertiaTensor = false;
            rigidbody.mass = (float)ParentMass;
            rigidbody.centerOfMass = Vector3.zero;
            rigidbody.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);
            Assert.That(
                root.TryAddBody(
                    actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, null,
                    out LogicalFragmentId body),
                Is.True);
            yield return null;

            Assert.That(root.TerminationRequested, Is.False, "nothing has gone wrong yet");

            // The termination is logged once, best effort, before the Player's API is called: that log is part of the
            // contract, so it is expected here rather than being a failure of this case.
            LogAssert.Expect(LogType.Error, new Regex("the Player is being ended"));
            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);

            yield return Until(() => root.TerminationRequested, "the geometry failure requested the termination");

            Assert.That(root.GeometryFaults, Is.GreaterThan(0), "the failure was reported once, where it happened");
            Assert.That(terminations, Is.EqualTo(1), "and the Player's termination API was called once");
            Assert.That(root.TerminationCalls, Is.EqualTo(1));

            // Acceptance is closed from the moment the latch was fixed.
            Assert.That(root.TryAsk(in ask), Is.False, "no cut is accepted after the termination request");
            Assert.That(
                root.Driver.enabled, Is.False, "and the asks noted before it are not taken up either");

            // A second cause changes nothing: the latch is one way and the API is called once.
            yield return null;
            Assert.That(terminations, Is.EqualTo(1), "the termination API is not called again");
            Assert.That(root.TerminationCalls, Is.EqualTo(1));

            // **A termination is not an ending.** The ordinary ending -- ending every cut, collecting, confirming the
            // workers -- is not started by it and cannot be started after it.
            Assert.That(root.Shutdown(), Is.False, "the ordinary ending is not started after a termination request");
            Assert.That(root.IsEnding, Is.False);
            Assert.That(root.IsReleased, Is.False, "and nothing was freed by it");
        }

        /// <summary>
        /// **Nothing unpublished is published after the termination request, including later in the very update it was
        /// made in.** One cut's cook is finished and waiting to be collected; another body's geometry cannot be cut.
        /// In the update where the frame collects the first one's bake, the second one's failure fixes the latch
        /// first — it is reported while the frame is being carried — so the collection that follows does not hand the
        /// first cut over, and no logical child is published for it.
        /// </summary>
        [UnityTest]
        public IEnumerator AfterTheTerminationRequest_AFinishedCutIsNotPublished_EvenLaterInThatUpdate()
        {
            HoldingExecutor physics = null;
            int terminations = 0;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination =>
                {
                    if (destination != WorkDestination.UnityJob)
                    {
                        return null;
                    }

                    physics = new HoldingExecutor(new UnityJobWorkExecutor(8));
                    physics.HoldEverything = true;
                    return physics;
                },
                () => terminations++);

            Assert.That(physics, Is.Not.Null);
            LogicalFragmentId cuttable = AddBody(root, new Vector3(-4f, 0f, 0f));
            LogicalFragmentId unusable = AddPlainBody(root, new Vector3(4f, 0f, 0f));
            yield return null;

            // The first cut, carried to "finished, waiting to be collected": its bake is with the destination and
            // held there, so one collection would finish its cook and hand it over.
            ProvisionalCutAsk first = Ask(cuttable, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in first), Is.True);

            // Its earlier stages are let through one at a time; the one that appears when it reaches the bake stays
            // held, which is what "finished, waiting to be collected" means here.
            ProvisionalCutTransaction held = null;
            float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
                {
                    if (candidate.Source.Equals(cuttable))
                    {
                        held = candidate;
                    }
                }

                if (held != null && held.Cut != null && held.Cut.Stage == PhysicsCutStage.Baking
                    && physics.Holding.Count > 0)
                {
                    break;
                }

                physics.TryLetOneThrough();
                yield return null;
            }

            Assert.That(held, Is.Not.Null, "the driver holds the first cut");
            Assert.That(held.Cut, Is.Not.Null);
            Assert.That(
                held.Cut.Stage, Is.EqualTo(PhysicsCutStage.Baking),
                "the first cut's bake is with the destination");
            Assert.That(physics.Holding.Count, Is.GreaterThan(0), "and it is held there, not collected");

            CutOperationId operation = held.Operation;
            Assert.That(
                OperationOf(root, operation).positive.IsSet, Is.False, "no child of it is published yet");

            // Let that bake be collected from the next update on, and ask for the cut that cannot be done. The update
            // that takes it up is where its geometry fails -- while the frame is carried, before the collection.
            LogAssert.Expect(LogType.Error, new Regex("the Player is being ended"));
            physics.ReleaseEverything();
            ProvisionalCutAsk fails = Ask(unusable, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in fails), Is.True);

            yield return Until(() => root.TerminationRequested, "the geometry failure requested the termination");
            Assert.That(terminations, Is.EqualTo(1));

            // From here the first cut's products may be collected at any moment -- and are not published.
            yield return null;
            yield return null;

            // **The products really came back, and were then not published.** Without these, a cut whose cook had
            // never been collected would pass this case just as well.
            Assert.That(held.Cut, Is.Null, "the first cut's request ended: its result was collected");
            Assert.That(
                held.Phase, Is.EqualTo(ProvisionalCutPhase.FinalHeld),
                "and its record holds the final products");
            Assert.That(held.Products, Is.Not.Null, "which it still has, because nothing was handed over");
            Assert.That(
                held.Phase, Is.Not.EqualTo(ProvisionalCutPhase.HandedOff),
                "the finished cut was not handed over after the termination request");
            Assert.That(
                OperationOf(root, operation).positive.IsSet, Is.False,
                "and no logical child was published for it");
            Assert.That(
                root.Owners.ProvisionalPairCount, Is.GreaterThan(0),
                "its Provisional pair is where it was: nothing was published or ended for it");
        }

        /// <summary>One body whose display geometry was never accepted as a cut input.</summary>
        private LogicalFragmentId AddPlainBody(CutWorldRoot root, Vector3 at)
        {
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendPlainBoxGeometry(root.Storage);

            var actor = Track(new GameObject("Body without a cuttable geometry"));
            actor.transform.position = at;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);

            Assert.That(
                root.TryAddBody(
                    actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, null,
                    out LogicalFragmentId fragment),
                Is.True,
                "the body was taken into the world");
            return fragment;
        }

        /// <summary>A box appended as an ordinary geometry: shown, but never accepted as a cut input.</summary>
        private static VpStoredGeometry AppendPlainBoxGeometry(VpCpuGeometryStorage storage)
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
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex
                        {
                            position = k_corners[c[k]],
                            normal = normal,
                            uv0 = new float2(0.5f, 0.5f),
                        });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(
                    start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            Assert.That(
                storage.TryAppendPrepared(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_corners.Length, submeshes.ToArray(),
                    out VpStoredGeometry geometry),
                Is.True,
                "the geometry is appended, but not as a cut input");
            return geometry;
        }
    }
}
