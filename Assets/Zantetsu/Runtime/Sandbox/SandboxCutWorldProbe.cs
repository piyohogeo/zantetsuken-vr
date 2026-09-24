using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// One representative body in a <see cref="CutWorldRoot"/>, and the keys that ask for a cut of it — the small
    /// confirmation input of a sandbox scene, and nothing more.
    /// <para>
    /// **What it is.** It makes one box: the convex the physics reads, the collider mesh that convex uses, and the
    /// display geometry of the same box, and hands all of it to the world in one registration. Then it waits for a
    /// key and hands a cut request to the world's own entrance. It decides nothing about what a cut is: the plane is
    /// the one named here, in the body's own logical frame, and the two separation impulses are **this scene's own
    /// values for looking at the result** — not a product rule, and not the formula DESIGN 7.2 leaves open.
    /// </para>
    /// <para>
    /// **What it is not.** It is not hit detection and not a stand-in for one: nothing is traced, picked or aimed. It
    /// is not an input system, a UI or a tool. A product caller — a weapon, say — would hand its own request to the
    /// same entrance, and nothing of this would be on its way.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    /// <remarks>
    /// **It runs before the driver in the update phase** (order -150: after the root's -200, before the driver's
    /// -100), so that an ask made from a key press here is taken up by the driver's update **of the same frame**.
    /// Placed after the driver, the same ask waited a whole frame for nothing but the order of the calls. This is a
    /// sandbox arrangement; it decides nothing about where a real hit path would ask from.
    /// </remarks>
    [DefaultExecutionOrder(-150)]
    public sealed class SandboxCutWorldProbe : MonoBehaviour
    {
        [Header("The world")]
        [SerializeField] private CutWorldRoot world;

        [Header("The body")]
        [Tooltip("Where the body stands when the scene begins.")]
        [SerializeField] private Vector3 bodyPosition = new Vector3(0f, 1f, 0f);

        [Tooltip("Half the box's size, in metres.")]
        [SerializeField] private Vector3 bodyExtents = new Vector3(0.5f, 0.5f, 0.5f);

        [SerializeField] private float bodyMass = 12f;

        [Tooltip("Whether the body falls. A sandbox body at rest is easier to look at.")]
        [SerializeField] private bool bodyUsesGravity;

        [Header("The cut this scene asks for")]
        [Tooltip("The plane, in the body's own logical frame: xyz is its normal, w its offset.")]
        [SerializeField] private Vector4 plane = new Vector4(0f, 1f, 0f, 0f);

        [Tooltip("The plane a child is cut with, in that child's own frame.")]
        [SerializeField] private Vector4 childPlane = new Vector4(1f, 0f, 0f, 0f);

        [Tooltip(
            "The two impulses the request carries, in newton-seconds. **A value of this scene, for looking at the "
            + "cut surface** -- DESIGN 7.2 leaves the formula and the direction open, and nothing here decides them.")]
        [SerializeField] private float lookImpulse = 1.5f;

        [Header("Keys")]
        [Tooltip("The key that asks for a cut of the body.")]
        [SerializeField] private Key cutKey = Key.Space;

        [Tooltip("The key that asks for a cut of one of its children.")]
        [SerializeField] private Key cutChildKey = Key.C;

        [Tooltip("The key that ends the world the ordinary way.")]
        [SerializeField] private Key endKey = Key.E;

        private readonly List<IDisposable> _native = new List<IDisposable>();
        private PhysicsOwnerShape _shape;
        private Mesh _colliderMesh;
        private GameObject _actor;

        /// <summary>The body this made, once it is in the world.</summary>
        public LogicalFragmentId Body { get; private set; }

        /// <summary>The actor of the body, which the world owns from the registration on.</summary>
        public GameObject Actor => _actor;

        /// <summary>How many cuts this has asked for.</summary>
        public int Asked { get; private set; }

        private void Start()
        {
            if (world == null || !world.IsReady)
            {
                Debug.LogError(name + ": this probe needs a CutWorldRoot that has been built.", this);
                enabled = false;
                return;
            }

            if (!TryAddBody())
            {
                enabled = false;
            }
        }

        private void OnDestroy()
        {
            // The actor belongs to the world once it has been registered; the shape and the arrays behind it are this
            // component's, and go back here.
            _shape?.Dispose();
            _shape = null;
            foreach (IDisposable array in _native)
            {
                array.Dispose();
            }

            _native.Clear();
            if (_colliderMesh != null)
            {
                Destroy(_colliderMesh);
                _colliderMesh = null;
            }
        }

        private void Update()
        {
            if (world == null)
            {
                return;
            }

            // The project's input is the Input System package's; a keyboard that is not there is simply no input.
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard[cutKey].wasPressedThisFrame)
            {
                AskCut(Body, plane);
            }

            if (keyboard[cutChildKey].wasPressedThisFrame)
            {
                AskChildCut();
            }

            if (keyboard[endKey].wasPressedThisFrame)
            {
                world.Shutdown();
            }
        }

        /// <summary>Hands one cut request to the world's own entrance. Nothing else happens here.</summary>
        public bool AskCut(LogicalFragmentId source, Vector4 at)
        {
            if (world == null || !source.IsSet)
            {
                return false;
            }

            var ask = new ProvisionalCutAsk
            {
                source = source,
                plane = new float4(at.x, at.y, at.z, at.w),
                positiveSeparationImpulse = lookImpulse,
                negativeSeparationImpulse = lookImpulse,
                renderAnchor = _actor != null ? (float3)_actor.transform.position : float3.zero,
            };

            if (!world.TryAsk(in ask))
            {
                return false;
            }

            Asked++;
            return true;
        }

        /// <summary>
        /// Asks for a cut of one child of the body's own cut, which is what "cut the pieces again" is: a published
        /// child is an ordinary live fragment and goes through the same entrance.
        /// </summary>
        public bool AskChildCut()
        {
            if (world == null || !Body.IsSet)
            {
                return false;
            }

            if (!world.Ledger.TryGetReplacingOperation(Body, out CutOperationId operation)
                || !world.Ledger.TryGetOperation(operation, out LogicalCutOperation record)
                || !record.positive.IsSet)
            {
                return false;
            }

            return AskCut(record.positive, childPlane);
        }

        /// <summary>The one box: its convex, its collider mesh, its display geometry, and the registration of all three.</summary>
        private bool TryAddBody()
        {
#if VP_DIAGNOSTIC_SCENE_AB
            lookImpulse = 0f;
            childPlane = new Vector4(1f, 0f, 0f, -.137f);
#endif
            float3 extents = bodyExtents;
            float3[] corners =
            {
                new float3(-extents.x, -extents.y, -extents.z), new float3(extents.x, -extents.y, -extents.z),
                new float3(extents.x, -extents.y, extents.z), new float3(-extents.x, -extents.y, extents.z),
                new float3(-extents.x, extents.y, -extents.z), new float3(extents.x, extents.y, -extents.z),
                new float3(extents.x, extents.y, extents.z), new float3(-extents.x, extents.y, extents.z),
            };

            var faceOffsets = new[] { 0, 4, 8, 12, 16, 20, 24 };
            var faceIndices = new List<int>();
            foreach ((int[] cycle, int _) in Faces)
            {
                faceIndices.AddRange(cycle);
            }

            BuildEdges(faceOffsets, faceIndices.ToArray(), out int[] faceEdges, out BrepEdge[] edges);
            _shape = NewShape(corners, faceOffsets, faceIndices.ToArray(), faceEdges, edges, out _colliderMesh);

            if (!TryAppendGeometry(world.Storage, corners, out VpStoredGeometry geometry))
            {
                Debug.LogError(name + ": the body's display geometry would not fit the world's storage.", this);
                return false;
            }

            _actor = new GameObject("Sandbox body");
            _actor.transform.position = bodyPosition;
            var body = _actor.AddComponent<Rigidbody>();
            body.useGravity = bodyUsesGravity;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = bodyMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = _actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = _shape.MeshOf(0);

            if (world.TryAddBody(
                    _actor, _shape, geometry, Matrix4x4.identity, Matrix4x4.identity, null,
                    out LogicalFragmentId fragment))
            {
                Body = fragment;
                return true;
            }

            Debug.LogError(name + ": the world would not take this body.", this);
            Destroy(_actor);
            _actor = null;
            return false;
        }

        private static readonly (int[] cycle, int submesh)[] Faces =
        {
            (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0), (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
            (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
        };

        private unsafe PhysicsOwnerShape NewShape(
            float3[] corners,
            int[] faceOffsets,
            int[] faceIndices,
            int[] faceEdges,
            BrepEdge[] edges,
            out Mesh colliderMesh)
        {
            var vertices = new NativeArray<float3>(corners, Allocator.Persistent);
            var offsets = new NativeArray<int>(faceOffsets, Allocator.Persistent);
            var indices = new NativeArray<int>(faceIndices, Allocator.Persistent);
            var edgesOfFaces = new NativeArray<int>(faceEdges, Allocator.Persistent);
            var edgeTable = new NativeArray<BrepEdge>(edges, Allocator.Persistent);
            _native.Add(vertices);
            _native.Add(offsets);
            _native.Add(indices);
            _native.Add(edgesOfFaces);
            _native.Add(edgeTable);

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
                vertexBase = 0, vertexCount = corners.Length,
                faceBase = 0, faceCount = Faces.Length,
                faceIndexBase = 0, faceIndexCount = faceIndices.Length,
                edgeBase = 0, edgeCount = edges.Length,
                maxFaceLoop = 4,
            };

            colliderMesh = new Mesh { name = "Sandbox collider", hideFlags = HideFlags.HideAndDontSave };
            var meshVertices = new Vector3[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                meshVertices[i] = corners[i];
            }

            colliderMesh.vertices = meshVertices;
            colliderMesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
            };
            UnityEngine.Physics.BakeMesh(colliderMesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);

            return PhysicsOwnerShape.Authored(
                bank, new[] { range }, new List<Mesh> { colliderMesh }, PhysicsShapeSource.External(),
                float4x4.identity);
        }

        /// <summary>The same box as a display geometry, appended to the world's storage as a cut input.</summary>
        private static bool TryAppendGeometry(
            VpCpuGeometryStorage storage, float3[] corners, out VpStoredGeometry geometry)
        {
#if VP_DIAGNOSTIC_SCENE_AB
            return TryAppendDenseBox(storage, corners, out geometry);
#else
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int face = 0; face < Faces.Length; face++)
                {
                    if (Faces[face].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = Faces[face].cycle;
                    float3 normal = math.normalize(math.cross(
                        corners[c[1]] - corners[c[0]], corners[c[2]] - corners[c[0]]));
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
                            position = corners[c[k]],
                            normal = normal,
                            uv0 = uv[k],
                        });
                        topology.Add(c[k]);
                    }

                    // In the order the face's own cycle runs, which is the order the B-rep face is given in: the
                    // outside faces out. Turning the quad round instead shows the box's inside -- the far faces from
                    // within -- while every record still says the same thing is being drawn.
                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(start, indices.Count - start, submesh));
            }

            return storage.TryAppendCuttable(
                vertices.ToArray(), indices.ToArray(), topology.ToArray(), corners.Length, submeshes.ToArray(),
                out geometry, out _);
#endif
        }

#if VP_DIAGNOSTIC_SCENE_AB
        private static bool TryAppendDenseBox(VpCpuGeometryStorage storage, float3[] corners, out VpStoredGeometry geometry)
        {
            const int divisions = 64;
            var vertices = new List<VpRenderVertex>(); var indices = new List<uint>();
            var topology = new List<int>(); var ids = new Dictionary<Vector3, int>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int material = 0; material < 2; material++)
            {
                int from = indices.Count;
                foreach (var face in Faces)
                {
                    if (face.submesh != material) continue;
                    var c = face.cycle;
                    float3 origin = corners[c[0]], x = corners[c[1]] - origin, y = corners[c[3]] - origin;
                    float3 normal = math.normalize(math.cross(x, y));
                    uint first = (uint)vertices.Count;
                    for (int j = 0; j <= divisions; j++) for (int i = 0; i <= divisions; i++)
                    {
                        Vector3 position = origin + x * (i / (float)divisions) + y * (j / (float)divisions);
                        if (!ids.TryGetValue(position, out int id)) { id = ids.Count; ids.Add(position, id); }
                        topology.Add(id);
                        vertices.Add(new VpRenderVertex { position = position, normal = normal, uv0 = new Vector2(32.5f / 256f, 32.5f / 256f) });
                    }
                    for (uint j = 0; j < divisions; j++) for (uint i = 0; i < divisions; i++)
                    {
                        uint a = first + j * (divisions + 1) + i, b = a + 1, d = a + divisions + 1, c2 = d + 1;
                        indices.Add(a); indices.Add(b); indices.Add(c2); indices.Add(a); indices.Add(c2); indices.Add(d);
                    }
                }
                submeshes.Add(new VpGeometrySubmesh(from, indices.Count - from, material));
            }
            bool accepted = storage.TryAppendCuttable(vertices.ToArray(), indices.ToArray(), topology.ToArray(), ids.Count, submeshes.ToArray(), out geometry, out var verdict);
            Debug.Log($"SCENE AB FIXTURE: subdivisions={divisions} vertices={vertices.Count} indices={indices.Count} topology={ids.Count} accepted={accepted} verdict={verdict}");
            return accepted;
        }
#endif

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
    }
}
