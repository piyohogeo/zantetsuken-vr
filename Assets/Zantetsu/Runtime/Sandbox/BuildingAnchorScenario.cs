using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>Real-asset registration and deterministic cut scenario. Uses the world entrance, not a fake
    /// publication. Deliberately does not claim blade/sweep coverage or provide a general asset importer.</summary>
    public sealed class BuildingAnchorScenario : MonoBehaviour
    {
        public CutWorldRoot world;
        public TextAsset buildingInput, propInput;
        public Vector3 buildingPosition = new Vector3(0, 0, 16);
        public Vector3 propPosition = new Vector3(-3, 0, 5);
        public Camera view;
        public bool automatic = true;
        readonly List<IDisposable> _native = new List<IDisposable>();
        readonly List<PhysicsOwnerShape> _shapes = new List<PhysicsOwnerShape>();
        readonly List<Mesh> _meshes = new List<Mesh>();
        readonly List<LogicalFragmentId> _fixed = new List<LogicalFragmentId>();
        readonly List<Vector3> _fixedPositions = new List<Vector3>();
        readonly List<Quaternion> _fixedRotations = new List<Quaternion>();
        string _output;
        int _completed;
        bool _failed;

        [Serializable] public sealed class Fixture
        {
            public int schemaVersion, topologyCount;
            public string name, source, sha256;
            public bool isBuilding, isCuttable;
            public Vertex[] render;
            public uint[] indices;
            public int[] topology;
            public Hull[] hulls;
            public Vector3[] anchors;
            public VpRenderVertex[] Vertices() => render.Select(v => new VpRenderVertex
                { position = v.position, normal = v.normal, uv0 = v.uv }).ToArray();
        }
        [Serializable] public sealed class Vertex { public Vector3 position, normal; public Vector2 uv; }
        [Serializable] public sealed class Hull { public Vector3[] vertices; public int[] faceOffsets, faceIndices; }

        IEnumerator Start()
        {
            Application.runInBackground = true;
            Application.targetFrameRate = 90;
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-buildingAnchorOut");
            _output = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
            if (_output != null) Directory.CreateDirectory(_output);
            // The hidden automation window need not present. Capture uses the same explicit URP request
            // as the existing rendering probes; interactive Editor playback keeps the camera enabled.
            if (_output != null && view != null) view.enabled = false;
            yield return null;
            LogicalFragmentId building = default, prop = default;
            try
            {
                if (world == null || !world.IsReady) throw new InvalidOperationException("World not ready");
                building = Register(buildingInput, buildingPosition, 10000f, true);
                prop = Register(propInput, propPosition, 50f, false);
            }
            catch (Exception e) { Fail(e.ToString()); }
            if (!_failed && automatic)
            {
                yield return new WaitForSecondsRealtime(2);
                yield return Capture("01-before");
                // Horizontal planes leave all authored ground anchors in the bottom half.
                yield return Cut(building, new float4(0, 1, 0, -5), true, 1, true);
                yield return Capture("02-building-first");
                yield return Cut(prop, new float4(0, 1, 0, -1.2f), false, 0, true);
                yield return Capture("03-prop-first");
                // Follow the live child: plane remains in lineage coordinates, independently of falling/motion.
                if (!_failed && world.Ledger.TryGetReplacingOperation(building, out var operation)
                    && world.Ledger.TryGetOperation(operation, out var cut))
                    yield return Cut(cut.positive, new float4(1, 0, 0, -0.17f), true, 2, false);
                yield return Capture("04-building-recut");
                yield return new WaitForSecondsRealtime(2);
                if (!_failed)
                {
                    for (int i = 0; i < _fixed.Count; ++i)
                        if (!world.Owners.TryGet(_fixed[i], out var owner)
                            || Vector3.Distance(owner.Root.transform.position, _fixedPositions[i]) > 0.001f
                            || Quaternion.Angle(owner.Root.transform.rotation, _fixedRotations[i]) > 0.01f)
                            Fail("Anchored child moved or disappeared: " + _fixed[i]);
                    if (_completed != 3 || world.GeometryFaults != 0) Fail("Incomplete scenario/geometry fault");
                }
                yield return Capture("05-after");
                yield return Capture("06-prop-after");
            }
            else if (!_failed) yield break;
            // A manual camera submission at end-of-frame still owns this frame's draw resources.
            // End the world on the next update, after the display has closed that frame.
            yield return null;
            float ending = Time.realtimeSinceStartup + 30;
            while (!world.Shutdown() && Time.realtimeSinceStartup < ending) yield return null;
            if (!world.IsReleased) Fail("World did not release");
            if (world.IsReleased) ReleaseInputs();
            Log("FINISHED code=" + (_failed ? 14 : 0) + " completed=" + _completed + " released=" + world.IsReleased);
            if (!Application.isEditor) Application.Quit(_failed ? 14 : 0);
        }

        LogicalFragmentId Register(TextAsset input, Vector3 position, float mass, bool building)
        {
            var data = JsonUtility.FromJson<Fixture>(input.text);
            if (!data.isCuttable || data.isBuilding != building || data.hulls.Length != 1 || data.anchors.Length == 0)
                throw new InvalidOperationException("Scenario fixture metadata mismatch: " + input.name);
            Hull hull = data.hulls[0];
            BuildEdges(hull.faceOffsets, hull.faceIndices, out var faceEdges, out var edges);
            var shape = NewShape(hull.vertices.Select(v => (float3)v).ToArray(), hull.faceOffsets,
                hull.faceIndices, faceEdges, edges, out var mesh);
            _shapes.Add(shape); _meshes.Add(mesh);
            if (!world.Storage.TryAppendCuttable(data.Vertices(), data.indices, data.topology, data.topologyCount,
                new[] { new VpGeometrySubmesh(0, data.indices.Length, 0) }, out var geometry, out var verdict))
                throw new InvalidOperationException("Geometry refused: " + data.name + " " + verdict);
            var actor = new GameObject(data.name);
            actor.transform.position = position;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = true; body.mass = mass;
            // Authored support fixes the initial actor; TryAddBody records that author's state.
            body.isKinematic = true;
            var collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true; collider.sharedMesh = mesh;
            if (!world.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity,
                data.anchors.Select(v => (float3)v).ToArray(), data.isBuilding, out var fragment))
                throw new InvalidOperationException("Registration refused: " + data.name);
            if (!world.Owners.TryGet(fragment, out var owner) || !owner.FixedByAnchors || !body.isKinematic)
                throw new InvalidOperationException("Authored anchors did not fix root");
            Log("REGISTER name=" + data.name + " fragment=" + fragment + " building=" + data.isBuilding
                + " anchors=" + data.anchors.Length + " sourceSha256=" + data.sha256);
            return fragment;
        }

        IEnumerator Cut(LogicalFragmentId source, float4 plane, bool building, int depth, bool hasFixedSide)
        {
            if (_failed) yield break;
            var ask = new ProvisionalCutAsk { source = source, plane = plane,
                positiveSeparationImpulse = 0, negativeSeparationImpulse = 0 };
            world.Ledger.TryGetAnchorCount(source, out int sourceAnchors);
            if (!world.TryAsk(in ask)) { Fail("Request refused " + source); yield break; }
            float deadline = Time.realtimeSinceStartup + 30;
            bool sawPair = false;
            while (Time.realtimeSinceStartup < deadline)
            {
                if ((world.Ledger.TryGetActiveOperation(source, out var id) || world.Ledger.TryGetReplacingOperation(source, out id))
                    && world.Ledger.TryGetOperation(id, out var op))
                {
                    if (!sawPair && world.Owners.TryGetProvisional(id, out var pair))
                    {
                        sawPair = true;
                        if (pair.Positive.FixedByAnchors || pair.Negative.FixedByAnchors != hasFixedSide
                            || (pair.Positive.BuildingWorld != null) != building
                            || (pair.Negative.BuildingWorld != null) != (building && !hasFixedSide))
                            Fail("Provisional anchor/World D6 mismatch " + id);
                        Log("PROVISIONAL op=" + id + " positiveFixed=" + pair.Positive.FixedByAnchors
                            + " negativeFixed=" + pair.Negative.FixedByAnchors
                            + " worldD6=" + (pair.Positive.BuildingWorld != null) + "/" + (pair.Negative.BuildingWorld != null));
                    }
                    if (op.state == LogicalCutOperationState.Completed)
                    {
                        try
                        {
                            ValidateChild(op.positive, building, depth, false);
                            ValidateChild(op.negative, building, depth, hasFixedSide);
                            world.Ledger.TryGetAnchorCount(op.positive, out int positiveAnchors);
                            world.Ledger.TryGetAnchorCount(op.negative, out int negativeAnchors);
                            if (positiveAnchors + negativeAnchors != sourceAnchors)
                                throw new InvalidOperationException("Authored anchors were lost or duplicated");
                            ++_completed;
                            Log("COMMIT op=" + id + " source=" + source + " provisionalObserved=" + sawPair);
                        }
                        catch (Exception e) { Fail(e.ToString()); }
                        yield break;
                    }
                    if (!op.IsIncomplete) { Fail("Operation ended as " + op.state); yield break; }
                }
                yield return null;
            }
            Fail("Timeout waiting for source " + source);
        }

        void ValidateChild(LogicalFragmentId fragment, bool building, int depth, bool anchored)
        {
            if (!world.Owners.TryGet(fragment, out var owner)) throw new InvalidOperationException("Missing Final owner");
            world.Ledger.TryGetAnchorCount(fragment, out int anchors);
            if ((anchors > 0) != anchored || owner.FixedByAnchors != anchored || owner.Body.isKinematic != anchored)
                throw new InvalidOperationException("Anchor/fixed mismatch " + fragment);
            if (owner.Building.IsBuildingDerived != building || owner.Building.SplitDepth != depth)
                throw new InvalidOperationException("Building lineage mismatch " + fragment);
            var joint = owner.BuildingWorldConstraint;
            if ((joint != null) != (building && !anchored)) throw new InvalidOperationException("World D6 mismatch " + fragment);
            if (joint != null && (joint.connectedBody != null || joint.yMotion != ConfigurableJointMotion.Free))
                throw new InvalidOperationException("Not a vertical-free World D6");
            if (anchored)
            {
                _fixed.Add(fragment); _fixedPositions.Add(owner.Root.transform.position);
                _fixedRotations.Add(owner.Root.transform.rotation);
            }
            Log("CHILD fragment=" + fragment + " anchors=" + anchors + " fixed=" + anchored
                + " building=" + building + " depth=" + depth + " worldD6=" + (joint != null)
                + " limit=" + (joint != null ? joint.linearLimit.limit : 0));
        }

        IEnumerator Capture(string label)
        {
            if (_output == null || view == null) yield break;
            var position = view.transform.position;
            var rotation = view.transform.rotation;
            if (label.Contains("prop"))
            {
                view.transform.position = propPosition + new Vector3(2, 3, -4);
                view.transform.LookAt(propPosition + Vector3.up);
            }
            var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
            target.Create();
            var previous = view.targetTexture;
            view.targetTexture = target;
            yield return null;
            yield return new WaitForEndOfFrame();
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (!RenderPipeline.SupportsRenderRequest(view, request)) Fail("Camera render request unsupported");
            else RenderPipeline.SubmitRenderRequest(view, request);
            var active = RenderTexture.active;
            RenderTexture.active = target;
            var pixels = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            pixels.Apply();
            File.WriteAllBytes(Path.Combine(_output, label + ".png"), pixels.EncodeToPNG());
            var colours = pixels.GetPixels32();
            int visible = colours.Count(c => c.r > 8 || c.g > 8 || c.b > 8);
            Log("CAPTURE " + label + " nonBlackPixels=" + visible);
            if (visible == 0) Fail("Empty camera image: " + label);
            RenderTexture.active = active;
            view.targetTexture = previous;
            view.transform.SetPositionAndRotation(position, rotation);
            Destroy(pixels); target.Release(); Destroy(target);
        }
        void Fail(string message) { _failed = true; Debug.LogError("BUILDING ANCHOR FAILED: " + message); Log("FAILED " + message); }
        void Log(string message)
        {
            message = "BUILDING ANCHOR frame=" + Time.frameCount + " " + message;
            Debug.Log(message);
            if (_output != null) File.AppendAllText(Path.Combine(_output, "scenario.txt"), message + Environment.NewLine);
        }
        void ReleaseInputs()
        {
            foreach (var shape in _shapes) shape.Dispose(); _shapes.Clear();
            foreach (var array in _native) array.Dispose(); _native.Clear();
            foreach (var mesh in _meshes) Destroy(mesh); _meshes.Clear();
        }
        void OnDestroy() { if (world == null || world.IsReleased) ReleaseInputs(); }
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
                faceBase = 0, faceCount = faceOffsets.Length - 1,
                faceIndexBase = 0, faceIndexCount = faceIndices.Length,
                edgeBase = 0, edgeCount = edges.Length,
                maxFaceLoop = Enumerable.Range(0, faceOffsets.Length - 1).Max(i => faceOffsets[i+1] - faceOffsets[i]),
            };

            colliderMesh = new Mesh { name = "Sandbox collider", hideFlags = HideFlags.HideAndDontSave };
            var meshVertices = new Vector3[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                meshVertices[i] = corners[i];
            }

            colliderMesh.vertices = meshVertices;
            var triangles = new List<int>();
            for (int f = 0; f + 1 < faceOffsets.Length; f++)
                for (int k = faceOffsets[f] + 1; k + 1 < faceOffsets[f+1]; k++)
                { triangles.Add(faceIndices[faceOffsets[f]]); triangles.Add(faceIndices[k+1]); triangles.Add(faceIndices[k]); }
            colliderMesh.triangles = triangles.ToArray();
            UnityEngine.Physics.BakeMesh(colliderMesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);

            return PhysicsOwnerShape.Authored(
                bank, new[] { range }, new List<Mesh> { colliderMesh }, PhysicsShapeSource.External(),
                float4x4.identity);
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
    }
}
