using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>One convex of a saved piece: its vertices and its faces (loops of vertex indices), in the shape's own frame.</summary>
    [Serializable]
    public sealed class ConvexTrimCaptureConvex
    {
        public Vector3[] vertices;
        public int[] faceOffsets, faceIndices;
    }

    /// <summary>A collider's physics material as it was read; <see cref="has"/> false where it had none (the engine's default).</summary>
    [Serializable]
    public sealed class ConvexTrimCaptureMaterial
    {
        public bool has;
        public float dynamicFriction, staticFriction, bounciness;
        public int frictionCombine, bounceCombine;

        internal static ConvexTrimCaptureMaterial Of(Collider collider)
        {
            PhysicsMaterial m = collider != null ? collider.sharedMaterial : null;
            return m == null
                ? new ConvexTrimCaptureMaterial()
                : new ConvexTrimCaptureMaterial
                {
                    has = true, dynamicFriction = m.dynamicFriction, staticFriction = m.staticFriction, bounciness = m.bounciness,
                    frictionCombine = (int)m.frictionCombine, bounceCombine = (int)m.bounceCombine,
                };
        }

        internal PhysicsMaterial Make(List<UnityEngine.Object> made)
        {
            if (!has) return null;
            var m = new PhysicsMaterial("Saved material")
            {
                dynamicFriction = dynamicFriction, staticFriction = staticFriction, bounciness = bounciness,
                frictionCombine = (PhysicsMaterialCombine)frictionCombine, bounceCombine = (PhysicsMaterialCombine)bounceCombine,
            };
            made.Add(m);
            return m;
        }
    }

    /// <summary>
    /// One collider near a saved piece that is not the piece's own and that the piece's colliders collide with (the layer
    /// matrix, the layer overrides and the ignored pairs of the frame): what it may be resting on.
    /// </summary>
    [Serializable]
    public sealed class ConvexTrimCaptureSupport
    {
        /// <summary>
        /// "box", "sphere", "capsule", "convex" (a convex mesh: its vertices), "mesh" (a triangle mesh: the triangles near
        /// the piece), "heightfield" (its surface under the piece as rays from above found it in the game: a stand-in for
        /// a mesh that cannot be read), or "bounds" (its world box alone: a stand-in where nothing else could be had).
        /// </summary>
        public string kind;
        public string name;
        public int layer;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;

        /// <summary>False where the saved form is a stand-in ("heightfield", "bounds"), and why.</summary>
        public bool exact;
        public string substitution;

        /// <summary>The distance between the piece's world box and the collider's, metres.</summary>
        public float distance;

        /// <summary>A box's or a capsule's or a sphere's own numbers (the collider's local frame).</summary>
        public Vector3 center, size;
        public float radius, height;
        public int direction;

        /// <summary>A mesh's vertices (the collider's local frame) and, for a triangle mesh, its triangles; of how many the mesh has.</summary>
        public Vector3[] vertices;
        public int[] triangles;
        public int trianglesOfMesh;

        /// <summary>A heightfield: world x and z of the first sample, the step, the counts, and a height a sample (<see cref="ConvexTrimCapture.NoSurface"/> negated where the ray found nothing).</summary>
        public Vector3 gridOrigin;
        public float gridStep;
        public int gridX, gridZ;
        public float[] heights;

        /// <summary>The collider's world box, always.</summary>
        public Vector3 boundsCenter, boundsSize;

        public ConvexTrimCaptureMaterial material;

        /// <summary>Its body, if it has one: a fragment of the cut world (its number; -1 otherwise), and how it stood.</summary>
        public bool hasBody, kinematic, sleeping;
        public int fragment = -1;
        public Vector3 velocity, angularVelocity;

        /// <summary>Held still in the game: no body, or a kinematic one.</summary>
        public bool Fixed => !hasBody || kinematic;
    }

    /// <summary>
    /// A piece of the cut world saved as it stood in one frame, for the convex trim's evaluation (2026-11-20): what is drawn
    /// of it, every convex it is made of, where it stood and how it moved, the colliders near it that it collides with --
    /// what it may be resting on -- and what the game's own physics scene measured under it in that frame (the clearance
    /// and the collider it was measured against), so that a replay can be judged against the game. What was near and is
    /// not saved (it does not collide with the piece, or it is past the limit) is counted and named.
    /// Taken on the main thread; one JSON file a piece.
    /// </summary>
    [Serializable]
    public sealed class ConvexTrimCapture
    {
        public const int Schema = 3;

        /// <summary>The oldest schema still read (2: without the trim's own account of the fragment).</summary>
        public const int SchemaOldest = 2;

        public int schemaVersion = Schema;
        public string note;
        public int frame;
        public double time;
        public int fragment, root;
        public bool propLineage, fixedByAnchors;

        /// <summary>What the product's trim says of the fragment in this frame (a <see cref="ConvexTrimPass"/>'s name; empty where there is no trim).</summary>
        public string productPass;

        /// <summary>
        /// The trim's own account of the fragment in this frame (schema 3), so that a float that is still there can be told
        /// apart: the trim never came to it, or it came and the shape still reaches. Whether the trim is on and its two
        /// thresholds; where the fragment stands (<see cref="CutConvexTrim.StateOf"/>: "InAttempt", "Queued", or a look's
        /// answer) and, for "Done", how the attempt that marked it ended; the newest record of an attempt on it -- how it
        /// ended, when, the vertices of the bake's input before and after (in all and a convex), the trims, the reach
        /// over the 26 directions before and after, the candidates left alone by the reason; and the nearest ancestor an
        /// attempt was published for (-1: none), whose trimmed convexes this fragment's were cut from.
        /// </summary>
        public bool trimEnabled;
        public float trimMinOverhang, trimMinSize;
        public string trimState = "", trimMarkedEnd = "";
        public bool trimHasRecord;
        public string trimEnd = "", trimNote = "";
        public int trimIssuedFrame, trimEndedFrame, trimTrims, trimTrimmedConvexes, trimVerticesBefore, trimVerticesAfter;
        public float trimReachBefore, trimReachAfter;
        public int[] trimLeft, trimConvexBefore, trimConvexAfter;
        public int trimmedAncestor = -1, trimmedAncestorEndedFrame;

        // where it stood and how it moved
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 velocity, angularVelocity;
        public bool sleeping, kinematic, useGravity;
        public int joints;
        public int layer;
        public float mass;
        public Vector3 centreOfMass, inertia;
        public Quaternion inertiaRotation;
        public float bodySleepThreshold, linearDamping, angularDamping, maxDepenetrationVelocity, ownContactOffset;
        public int bodySolverIterations, bodySolverVelocityIterations;
        public ConvexTrimCaptureMaterial material;

        /// <summary>The geometry's frame and the shape's frame, each carried to the owner's (the Root's).</summary>
        public Matrix4x4 geometryToOwner, shapeToOwner;

        /// <summary>The vertices the fragment's indices name, each once, in the geometry's own frame; and its triangles over them.</summary>
        public Vector3[] meshPositions;
        public int[] meshIndices;

        public ConvexTrimCaptureConvex[] convexes;
        public ConvexTrimCaptureSupport[] supports;

        /// <summary>How far round the piece's world box the supports were looked for, metres.</summary>
        public float supportReach;

        /// <summary>
        /// Of the colliders near (not the piece's own, no triggers): how many, how many the piece does not collide with
        /// (not saved; the first of them named with why), how many were past <see cref="SupportsMost"/> (not saved: the
        /// farthest; the nearest of them at this distance), and whether the query's own buffer was filled.
        /// </summary>
        public int supportsNear, supportsNotColliding, supportsDropped;
        public string[] notColliding;
        public float nearestDroppedDistance;
        public bool nearOverflow;

        /// <summary>
        /// What the game's physics scene measured in this frame (<see cref="ConvexTrimCaptureReplay.Clearance"/>): the least
        /// clearance straight down under a vertex of what is drawn to a collider the piece collides with
        /// (<see cref="NoSurface"/> where there is none within reach), where, and against which collider -- its index among
        /// <see cref="supports"/>, -1 where that collider is not saved.
        /// </summary>
        public bool measured;
        public float measuredClearance;
        public Vector3 measuredAt;
        public string measuredAgainst;
        public int measuredAgainstLayer = -1, measuredAgainstSupport = -1, measuredAgainstFragment = -1;
        public float captureMilliseconds;

        // the physics the frame ran under
        public Vector3 gravity;
        public float contactOffset, sleepThreshold, bounceThreshold;
        public int solverIterations, solverVelocityIterations;

        /// <summary>The most vertices of one support's saved mesh; past it the support is saved as a heightfield.</summary>
        public const int SupportVerticesMost = 60000;

        /// <summary>The most supports saved: the nearest.</summary>
        public const int SupportsMost = 96;

        /// <summary>The clearance where no surface is under the piece within reach (JSON has no infinity).</summary>
        public const float NoSurface = 1e6f;

        /// <summary>A test's: every mesh is taken as one that cannot be read (a Release Player's scenery; the Editor keeps its meshes readable).</summary>
        internal static bool TestMeshesUnreadable;

        private static bool Readable(Mesh mesh) => mesh.isReadable && !TestMeshesUnreadable;

        private const int NearBuffer = 4096;
        private const int NamedMost = 32;
        private const int GridMost = 128;
        private const float GridStepLeast = 0.02f;

        /// <summary>Whether the body is at rest as the evaluation means it: asleep, or all but still.</summary>
        public bool Resting => sleeping || (velocity.magnitude < 0.02f && angularVelocity.magnitude < 0.05f);

        /// <summary>
        /// Whether two colliders collide by the frame's settings: the layer matrix, the layer overrides of the colliders and
        /// their bodies (an exclusion wins over an inclusion), and the pairs ignored by name. Null where they do; why not otherwise.
        /// </summary>
        internal static string WhyNoCollision(Collider mine, Collider other)
        {
            int a = mine.gameObject.layer, b = other.gameObject.layer;
            LayerMask Include(Collider c) => c.includeLayers | (c.attachedRigidbody != null ? c.attachedRigidbody.includeLayers : (LayerMask)0);
            LayerMask Exclude(Collider c) => c.excludeLayers | (c.attachedRigidbody != null ? c.attachedRigidbody.excludeLayers : (LayerMask)0);
            if ((Exclude(mine).value & (1 << b)) != 0 || (Exclude(other).value & (1 << a)) != 0) return "excluded by a layer override";
            bool included = (Include(mine).value & (1 << b)) != 0 || (Include(other).value & (1 << a)) != 0;
            if (Physics.GetIgnoreLayerCollision(a, b) && !included) return "layers " + a + " and " + b + " do not collide";
            if (Physics.GetIgnoreCollision(mine, other)) return "the pair is ignored";
            return null;
        }

        private static float BoxDistance(Bounds a, Bounds b)
        {
            Vector3 d = Vector3.Max(Vector3.zero, Vector3.Max(a.min - b.max, b.min - a.max));
            return d.magnitude;
        }

        /// <summary>
        /// Saves the piece <paramref name="fragment"/> is now. Null, with <paramref name="refusal"/>, where it has no owner with a
        /// body and a shape of its own, or its geometry is not committed or not readable.
        /// </summary>
        public static unsafe ConvexTrimCapture Take(CutWorldRoot world, LogicalFragmentId fragment, string note, float supportReach, out string refusal)
        {
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            refusal = null;
            if (world == null || world.Owners == null || world.Geometry == null || world.Storage == null)
            {
                refusal = "no cut world";
                return null;
            }

            if (!world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.IsWithdrawn || owner.Body == null || owner.Root == null || owner.Shape == null || owner.Shape.IsFreed)
            {
                refusal = "fragment " + fragment.value + " has no body and shape of its own now";
                return null;
            }

            if (owner.GeometryLocalToOwner == null || !world.Geometry.TryGetGeometry(fragment, out VpStoredGeometry geometry)
                || !world.Storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view))
            {
                refusal = "the geometry of fragment " + fragment.value + " is not committed or not readable now";
                return null;
            }

            var capture = new ConvexTrimCapture
            {
                note = note, frame = Time.frameCount, time = Time.realtimeSinceStartupAsDouble, fragment = fragment.value, fixedByAnchors = owner.FixedByAnchors,
                supportReach = supportReach, gravity = Physics.gravity, contactOffset = Physics.defaultContactOffset, sleepThreshold = Physics.sleepThreshold,
                bounceThreshold = Physics.bounceThreshold, solverIterations = Physics.defaultSolverIterations, solverVelocityIterations = Physics.defaultSolverVelocityIterations,
                productPass = string.Empty, nearestDroppedDistance = -1f,
            };
            try
            {
                NativeArray<VpRenderVertex>.ReadOnly vertices = world.Storage.Vertices;
                var slot = new Dictionary<uint, int>(view.Length);
                var positions = new List<Vector3>(view.Length);
                var indices = new int[view.Length];
                for (int i = 0; i < view.Length; i++)
                {
                    uint id = view[i];
                    if (!slot.TryGetValue(id, out int numbered))
                    {
                        numbered = positions.Count;
                        slot.Add(id, numbered);
                        positions.Add(vertices[(int)id].position);
                    }

                    indices[i] = numbered;
                }

                capture.meshPositions = positions.ToArray();
                capture.meshIndices = indices;
            }
            finally
            {
                world.Storage.TryReleaseIndexReadLease(lease);
            }

            if (capture.meshPositions.Length == 0)
            {
                refusal = "the geometry of fragment " + fragment.value + " names no vertex";
                return null;
            }

            if (world.Ledger != null && world.Ledger.TryGetRoot(fragment, out LogicalFragmentId root))
            {
                capture.root = root.value;
                capture.propLineage = world.Lifetime != null && world.Lifetime.IsPropLineage(root);
            }

            if (world.ConvexTrim != null) NoteTrim(world, fragment, capture);

            PhysicsOwnerShape shape = owner.Shape;
            capture.geometryToOwner = owner.GeometryLocalToOwner.Value;
            capture.shapeToOwner = (Matrix4x4)shape.LocalToOwner;
            capture.convexes = new ConvexTrimCaptureConvex[shape.ConvexCount];
            for (int c = 0; c < shape.ConvexCount; c++) capture.convexes[c] = ConvexOf(shape, c);

            Rigidbody body = owner.Body;
            owner.Root.transform.GetPositionAndRotation(out capture.position, out capture.rotation);
            capture.kinematic = body.isKinematic;
            capture.sleeping = body.IsSleeping();
            capture.useGravity = body.useGravity;
            capture.velocity = body.isKinematic ? Vector3.zero : body.linearVelocity;
            capture.angularVelocity = body.isKinematic ? Vector3.zero : body.angularVelocity;
            capture.mass = body.mass;
            capture.centreOfMass = body.centerOfMass;
            capture.inertia = body.inertiaTensor;
            capture.inertiaRotation = body.inertiaTensorRotation;
            capture.bodySleepThreshold = body.sleepThreshold;
            capture.linearDamping = body.linearDamping;
            capture.angularDamping = body.angularDamping;
            capture.maxDepenetrationVelocity = body.maxDepenetrationVelocity;
            capture.bodySolverIterations = body.solverIterations;
            capture.bodySolverVelocityIterations = body.solverVelocityIterations;
            capture.joints = body.GetComponents<Joint>().Length;
            capture.layer = owner.Root.layer;

            // the piece's own colliders: what the frame's collision settings are asked about
            var own = new List<Collider>();
            foreach (Collider collider in owner.Root.GetComponentsInChildren<Collider>())
            {
                if (collider.enabled && !collider.isTrigger && collider.attachedRigidbody == body) own.Add(collider);
            }

            if (own.Count > 0)
            {
                capture.ownContactOffset = own[0].contactOffset;
                capture.material = ConvexTrimCaptureMaterial.Of(own[0]);
                capture.layer = own[0].gameObject.layer;
            }
            else
            {
                capture.material = new ConvexTrimCaptureMaterial();
            }

            // the piece's world box: what is drawn and what its convexes are
            Matrix4x4 ownerToWorld = owner.Root.transform.localToWorldMatrix;
            Matrix4x4 meshToWorld = ownerToWorld * capture.geometryToOwner, shapeToWorld = ownerToWorld * capture.shapeToOwner;
            var box = new Bounds(meshToWorld.MultiplyPoint3x4(capture.meshPositions[0]), Vector3.zero);
            foreach (Vector3 p in capture.meshPositions) box.Encapsulate(meshToWorld.MultiplyPoint3x4(p));
            foreach (ConvexTrimCaptureConvex convex in capture.convexes)
            {
                foreach (Vector3 p in convex.vertices) box.Encapsulate(shapeToWorld.MultiplyPoint3x4(p));
            }

            // what is near, and of it what the piece collides with -- the nearest first, so that what the limit leaves out is the farthest
            PhysicsScene physics = owner.Root.scene.GetPhysicsScene();
            var region = new Bounds(box.center, box.size + (Vector3.one * (2f * supportReach)));
            var buffer = new Collider[NearBuffer];
            int found = physics.OverlapBox(region.center, region.extents, buffer, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            capture.nearOverflow = found >= NearBuffer;
            var collides = new Dictionary<Collider, bool>();
            var candidates = new List<(Collider collider, float distance)>();
            var named = new List<string>();
            for (int i = 0; i < found; i++)
            {
                Collider collider = buffer[i];
                if (collider == null || !collider.enabled || collider.isTrigger || collider.attachedRigidbody == body) continue;
                capture.supportsNear++;
                string why = WhyNot(own, collider);
                collides[collider] = why == null;
                if (why != null)
                {
                    capture.supportsNotColliding++;
                    if (named.Count < NamedMost) named.Add(collider.name + " (layer " + collider.gameObject.layer + "): " + why);
                    continue;
                }

                candidates.Add((collider, BoxDistance(box, collider.bounds)));
            }

            capture.notColliding = named.ToArray();
            candidates.Sort((x, y) => x.distance.CompareTo(y.distance));
            var supports = new List<ConvexTrimCaptureSupport>();
            var indexOf = new Dictionary<Collider, int>();
            foreach ((Collider collider, float distance) in candidates)
            {
                if (supports.Count >= SupportsMost)
                {
                    if (capture.supportsDropped++ == 0) capture.nearestDroppedDistance = distance;
                    continue;
                }

                indexOf[collider] = supports.Count;
                supports.Add(SupportOf(world, collider, distance, region, box));
            }

            capture.supports = supports.ToArray();

            // what the game's own scene measures under the piece in this frame
            bool Counts(Collider c)
            {
                if (c == null || c.attachedRigidbody == body) return false;
                if (!collides.TryGetValue(c, out bool yes))
                {
                    yes = c.enabled && !c.isTrigger && WhyNot(own, c) == null;
                    collides[c] = yes;
                }

                return yes;
            }

            capture.measuredClearance = ConvexTrimCaptureReplay.Clearance(physics, capture.meshPositions, meshToWorld, supportReach, Counts, out RaycastHit nearest, out Vector3 at);
            capture.measured = true;
            capture.measuredAt = at;
            if (nearest.collider != null)
            {
                capture.measuredAgainst = nearest.collider.name;
                capture.measuredAgainstLayer = nearest.collider.gameObject.layer;
                capture.measuredAgainstSupport = indexOf.TryGetValue(nearest.collider, out int saved) ? saved : -1;
                Rigidbody against = nearest.collider.attachedRigidbody;
                if (against != null && world.Owners.TryResolveFragment(against, out LogicalFragmentId other, out float _)) capture.measuredAgainstFragment = other.value;
            }
            else
            {
                capture.measuredAgainst = string.Empty;
            }

            capture.captureMilliseconds = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - begin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            return capture;
        }

        // The trim's own account of the fragment (see trimEnabled).
        private static void NoteTrim(CutWorldRoot world, LogicalFragmentId fragment, ConvexTrimCapture capture)
        {
            CutConvexTrim trim = world.ConvexTrim;
            capture.trimEnabled = trim.Settings.enabled;
            capture.trimMinOverhang = trim.Settings.minOverhangMeters;
            capture.trimMinSize = trim.Settings.minSizeMeters;
            capture.trimState = trim.StateOf(fragment, out capture.trimMarkedEnd);
            capture.productPass = capture.trimState == "InAttempt" || capture.trimState == "Queued" ? nameof(ConvexTrimPass.Candidate) : capture.trimState;
            if (trim.TryGetLastRecord(fragment, out ConvexTrimRecord record))
            {
                capture.trimHasRecord = true;
                capture.trimEnd = record.end.ToString();
                capture.trimNote = record.note ?? string.Empty;
                capture.trimIssuedFrame = record.issuedFrame;
                capture.trimEndedFrame = record.endedFrame;
                capture.trimTrims = record.trimsInAll;
                capture.trimTrimmedConvexes = record.trimmedConvexes;
                capture.trimVerticesBefore = record.verticesBeforeInAll;
                capture.trimVerticesAfter = record.verticesAfterInAll;
                capture.trimReachBefore = record.overhangBefore;
                capture.trimReachAfter = record.overhangAfter;
                capture.trimLeft = (int[])record.left.Clone();
                capture.trimConvexBefore = record.verticesBefore;
                capture.trimConvexAfter = record.verticesAfter;
            }

            if (world.Ledger == null) return;
            LogicalFragmentId at = fragment;
            for (int depth = 0; depth < 64 && world.Ledger.TryGetParent(at, out LogicalFragmentId parent); depth++)
            {
                at = parent;
                if (!trim.TryGetLastRecord(at, out ConvexTrimRecord of) || of.end != ConvexTrimEnd.Published) continue;
                capture.trimmedAncestor = at.value;
                capture.trimmedAncestorEndedFrame = of.endedFrame;
                break;
            }
        }

        private static string WhyNot(List<Collider> own, Collider other)
        {
            if (own.Count == 0) return "the piece has no collider";
            string why = null;
            foreach (Collider mine in own)
            {
                why = WhyNoCollision(mine, other);
                if (why == null) return null;
            }

            return why;
        }

        private static unsafe ConvexTrimCaptureConvex ConvexOf(PhysicsOwnerShape shape, int c)
        {
            ConvexBrepRange range = shape.Convex(c);
            ConvexBrepBank bank = shape.BankOf(c);
            var convex = new ConvexTrimCaptureConvex
            {
                vertices = new Vector3[range.vertexCount], faceOffsets = new int[range.faceCount + 1], faceIndices = new int[range.faceIndexCount],
            };
            for (int v = 0; v < range.vertexCount; v++) convex.vertices[v] = bank.vertices[range.vertexBase + v];
            for (int f = 0; f <= range.faceCount; f++) convex.faceOffsets[f] = bank.faceOffsets[range.faceBase + f];
            for (int i = 0; i < range.faceIndexCount; i++) convex.faceIndices[i] = bank.faceIndices[range.faceIndexBase + i];
            return convex;
        }

        private static ConvexTrimCaptureSupport SupportOf(CutWorldRoot world, Collider collider, float distance, Bounds region, Bounds pieceBox)
        {
            Transform t = collider.transform;
            var support = new ConvexTrimCaptureSupport
            {
                kind = "bounds", name = collider.name, layer = collider.gameObject.layer, position = t.position, rotation = t.rotation, scale = t.lossyScale,
                boundsCenter = collider.bounds.center, boundsSize = collider.bounds.size, distance = distance, exact = true, substitution = string.Empty,
                material = ConvexTrimCaptureMaterial.Of(collider),
            };
            Rigidbody body = collider.attachedRigidbody;
            PhysicsFragmentOwner otherOwner = null;
            if (body != null)
            {
                support.hasBody = true;
                support.kinematic = body.isKinematic;
                support.sleeping = body.IsSleeping();
                support.velocity = body.isKinematic ? Vector3.zero : body.linearVelocity;
                support.angularVelocity = body.isKinematic ? Vector3.zero : body.angularVelocity;
                if (world.Owners.TryResolveFragment(body, out LogicalFragmentId other, out float _))
                {
                    support.fragment = other.value;
                    world.Owners.TryGet(other, out otherOwner);
                }
            }

            string why = null;
            switch (collider)
            {
                case BoxCollider box:
                    support.kind = "box";
                    support.center = box.center;
                    support.size = box.size;
                    return support;
                case SphereCollider sphere:
                    support.kind = "sphere";
                    support.center = sphere.center;
                    support.radius = sphere.radius;
                    return support;
                case CapsuleCollider capsule:
                    support.kind = "capsule";
                    support.center = capsule.center;
                    support.radius = capsule.radius;
                    support.height = capsule.height;
                    support.direction = capsule.direction;
                    return support;
                case MeshCollider mesh when mesh.sharedMesh != null && mesh.convex:
                    if (Readable(mesh.sharedMesh))
                    {
                        support.kind = "convex";
                        support.vertices = mesh.sharedMesh.vertices;
                        return support;
                    }

                    // a fragment's convex whose Mesh cannot be read: its B-rep, where the collider stands in the shape's own frame
                    if (otherOwner != null && otherOwner.Shape != null && !otherOwner.Shape.IsFreed)
                    {
                        PhysicsOwnerShape shape = otherOwner.Shape;
                        for (int c = 0; c < shape.ConvexCount; c++)
                        {
                            if (shape.MeshOf(c) != mesh.sharedMesh || shape.MeshFrameOf(c).HasFrame) continue;
                            support.kind = "convex";
                            support.vertices = ConvexOf(shape, c).vertices;
                            return support;
                        }
                    }

                    why = "a convex mesh that cannot be read";
                    break;
                case MeshCollider mesh when mesh.sharedMesh != null:
                    if (!Readable(mesh.sharedMesh))
                    {
                        why = "a mesh that cannot be read";
                    }
                    else if (TrianglesNear(mesh, region, support))
                    {
                        support.kind = "mesh";
                        return support;
                    }
                    else
                    {
                        why = "more than " + SupportVerticesMost + " vertices of the mesh are near";
                    }

                    break;
                default:
                    why = "a " + collider.GetType().Name;
                    break;
            }

            // A stand-in: the collider's surface as rays from above the piece find it, over the piece's footprint.
            support.exact = false;
            support.substitution = why;
            if (!Heightfield(collider, region, pieceBox, support)) support.substitution = why + "; no ray from above found it: its world box";
            return support;
        }

        // The triangles of a readable mesh whose boxes meet the region (asked in the collider's own frame, so a superset), with
        // their vertices numbered again. False where they are more than may be saved.
        private static bool TrianglesNear(MeshCollider collider, Bounds region, ConvexTrimCaptureSupport support)
        {
            Mesh mesh = collider.sharedMesh;
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            Matrix4x4 toLocal = collider.transform.worldToLocalMatrix;
            var local = new Bounds(toLocal.MultiplyPoint3x4(region.center), Vector3.zero);
            for (int corner = 0; corner < 8; corner++)
            {
                var sign = new Vector3((corner & 1) != 0 ? 1f : -1f, (corner & 2) != 0 ? 1f : -1f, (corner & 4) != 0 ? 1f : -1f);
                local.Encapsulate(toLocal.MultiplyPoint3x4(region.center + Vector3.Scale(region.extents, sign)));
            }

            var slot = new Dictionary<int, int>();
            var keptVertices = new List<Vector3>();
            var kept = new List<int>();
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                Vector3 lo = Vector3.Min(a, Vector3.Min(b, c)), hi = Vector3.Max(a, Vector3.Max(b, c));
                if (lo.x > local.max.x || hi.x < local.min.x || lo.y > local.max.y || hi.y < local.min.y || lo.z > local.max.z || hi.z < local.min.z) continue;
                for (int k = 0; k < 3; k++)
                {
                    int id = triangles[i + k];
                    if (!slot.TryGetValue(id, out int at))
                    {
                        at = keptVertices.Count;
                        slot.Add(id, at);
                        keptVertices.Add(vertices[id]);
                    }

                    kept.Add(at);
                }

                if (keptVertices.Count > SupportVerticesMost) return false;
            }

            support.vertices = keptVertices.ToArray();
            support.triangles = kept.ToArray();
            support.trianglesOfMesh = triangles.Length / 3;
            return true;
        }

        // False where no ray found the collider (the support stays its world box).
        private static bool Heightfield(Collider collider, Bounds region, Bounds pieceBox, ConvexTrimCaptureSupport support)
        {
            Bounds own = collider.bounds;
            float x0 = Mathf.Max(region.min.x, own.min.x), x1 = Mathf.Min(region.max.x, own.max.x);
            float z0 = Mathf.Max(region.min.z, own.min.z), z1 = Mathf.Min(region.max.z, own.max.z);
            if (x1 < x0 || z1 < z0) return false;
            float step = Mathf.Max(GridStepLeast, Mathf.Max(x1 - x0, z1 - z0) / (GridMost - 1));
            int nx = Mathf.Min(GridMost, Mathf.FloorToInt((x1 - x0) / step) + 2), nz = Mathf.Min(GridMost, Mathf.FloorToInt((z1 - z0) / step) + 2);
            float top = pieceBox.max.y + 0.05f, depth = top - region.min.y;
            var heights = new float[nx * nz];
            int hits = 0;
            for (int j = 0; j < nz; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    var ray = new Ray(new Vector3(x0 + (i * step), top, z0 + (j * step)), Vector3.down);
                    if (collider.Raycast(ray, out RaycastHit hit, depth))
                    {
                        heights[(j * nx) + i] = hit.point.y;
                        hits++;
                    }
                    else
                    {
                        heights[(j * nx) + i] = -NoSurface;
                    }
                }
            }

            if (hits == 0) return false;
            support.kind = "heightfield";
            support.gridOrigin = new Vector3(x0, 0f, z0);
            support.gridStep = step;
            support.gridX = nx;
            support.gridZ = nz;
            support.heights = heights;
            return true;
        }

        public string ToJson() => JsonUtility.ToJson(this, false);

        public static ConvexTrimCapture FromJson(string json)
        {
            ConvexTrimCapture capture = JsonUtility.FromJson<ConvexTrimCapture>(json);
            if (capture == null || capture.schemaVersion < SchemaOldest || capture.schemaVersion > Schema || capture.meshPositions == null || capture.convexes == null)
            {
                throw new InvalidDataException("not a convex trim capture of schema " + SchemaOldest + " to " + Schema);
            }

            capture.supports ??= Array.Empty<ConvexTrimCaptureSupport>();
            capture.material ??= new ConvexTrimCaptureMaterial();
            return capture;
        }
    }

    /// <summary>
    /// The saved supports a replayed piece's convexes are within <see cref="ConvexTrimCaptureReplay.TouchMeters"/> of, by
    /// what they were in the game: held still and saved as they are; stand-ins (a heightfield); world boxes alone;
    /// bodies that were asleep; bodies that were moving.
    /// </summary>
    [Serializable]
    public struct ConvexTrimTouch
    {
        public int fixedExact, standIn, bounds, asleep, moving;

        public int All => fixedExact + standIn + bounds + asleep + moving;

        public override string ToString() => "fixed " + fixedExact + (standIn > 0 ? ", stand-ins " + standIn : string.Empty) + (bounds > 0 ? ", boxes " + bounds : string.Empty)
            + (asleep > 0 ? ", bodies asleep " + asleep : string.Empty) + (moving > 0 ? ", bodies moving " + moving : string.Empty);
    }

    /// <summary>One variant of a replayed capture -- the convexes as saved, or trimmed -- after it was let go as it was saved.</summary>
    [Serializable]
    public sealed class ConvexTrimReplayRun
    {
        public bool slept;
        public int steps;

        /// <summary>What the variant touched at the saved pose, before anything moved, and at rest.</summary>
        public ConvexTrimTouch startsOn, restsOn;

        /// <summary>The least clearance straight down under a vertex of what is drawn, metres, at rest (as the game measured it at the save); how far the lowest drawn point came down from the saved pose; how far the piece moved and turned.</summary>
        public float clearance, lowestCameDown, moved, turnedDegrees;

        /// <summary>How far the variant's convexes reach below what is drawn, straight down, at the saved pose and at rest.</summary>
        public float reachDownAtSaved, reachDownAtRest;
    }

    /// <summary>What the replay of one capture came to.</summary>
    [Serializable]
    public sealed class ConvexTrimReplayResult
    {
        /// <summary>A sink: what is drawn is below a support's surface by more than this, metres.</summary>
        public const float SinkMeters = 0.02f;

        /// <summary>The replay stands as the game stood where the piece as saved stays within these and the clearance is within <see cref="SinkMeters"/> of the game's.</summary>
        public const float StaysMeters = 0.02f, StaysDegrees = 2f;

        public string name;
        public int fragment, meshVertices, triangles;
        public string productPass;

        /// <summary>The whole collision box's extents (shape's frame), and whether the trim looks at a body of this size.</summary>
        public Vector3 extents;
        public bool large;

        /// <summary>The piece as saved: free (no anchors, not kinematic, no joint), at rest.</summary>
        public bool free, resting;

        /// <summary>The supports: saved, as they are (exact) and held still in the game; stand-ins; world boxes alone; bodies asleep; bodies moving; left out for the limit.</summary>
        public int supports, supportsExactFixed, supportsApproximate, supportsAsBounds, supportsAsleep, supportsMoving, supportsDropped;

        /// <summary>What the game measured at the save, and against what; what the replay's scene measures at the saved pose before anything moves.</summary>
        public bool measured;
        public float measuredClearance;
        public string measuredAgainst;
        public bool measuredAgainstSaved;
        public float savedClearance;

        /// <summary>
        /// "A": a free piece the product's trim looks at, at rest, and every saved support it touched at the saved pose is
        /// saved as it is and was held still in the game (what was merely near does not count). "B": as A but a support it
        /// touched is a stand-in (a heightfield) or a body asleep (held still here), or it touched none.
        /// "C": anything else -- the numbers of the geometry and the vertices stand, the fall does not. And why not A.
        /// <see cref="notes"/>: what does not change the tier and is good to know.
        /// </summary>
        public string tier;
        public string[] reasons;
        public string[] notes;

        /// <summary>Whether the piece as saved stayed where and as the game had it (see <see cref="StaysMeters"/>): the replay may be believed.</summary>
        public bool offReproduced;

        /// <summary>How much further the trimmed piece's lowest drawn point came down than the piece as saved; whether it ended below a support's surface where the piece as saved did not.</summary>
        public float trimCameDown;
        public bool trimSank;

        /// <summary>
        /// The trim in the game at the save (a capture of schema 3 with the trim on): where the fragment stood and how its
        /// newest attempt ended, the vertices and the reach that attempt had before and after, the nearest trimmed
        /// ancestor; and -- <see cref="gameTrimKind"/> -- which of these the saved shape is: "published" (an attempt on
        /// this fragment was published: the shape is a trimmed one), "inherited" (cut from a trimmed ancestor's and not
        /// trimmed itself since), "nothing to trim" (an attempt found nothing at the threshold or nothing it could keep),
        /// "no turn yet" (queued, in an attempt, or never ended: the trim had not come to it), "passed over" (not the
        /// trim's: too small, not cut-made, ...), "trim off".
        /// </summary>
        public bool gameTrimOn;
        public string gameTrimState, gameTrimEnd, gameTrimKind;
        public int gameTrimVerticesBefore, gameTrimVerticesAfter, gameTrimTrims, gameTrimmedAncestor = -1, gameFramesSinceTrim;
        public float gameTrimReachBefore, gameTrimReachAfter;

        /// <summary>Per convex: the B-rep's vertices before and after the trim, and the trims adopted.</summary>
        public int[] verticesBefore, verticesAfter, trims;
        public int verticesBeforeInAll, verticesAfterInAll, trimsInAll, trimmedConvexes, candidatesBefore;
        public int[] left;
        public float reach26Before, reach26After;
        public ConvexTrimReplayRun asSaved, trimmed;

        private static string Metres(float v) => v >= ConvexTrimCapture.NoSurface * 0.5f ? "none" : v.ToString("F3", CultureInfo.InvariantCulture);

        public string Describe()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            string run(ConvexTrimReplayRun r) => r == null ? "not run" : "clearance " + Metres(r.clearance) + " m (came down " + r.lowestCameDown.ToString("F3", inv)
                + ", moved " + r.moved.ToString("F3", inv) + " m, turned " + r.turnedDegrees.ToString("F1", inv) + " deg, " + (r.slept ? "slept" : "NOT asleep") + " after " + r.steps
                + " steps; reach down at the saved pose " + r.reachDownAtSaved.ToString("F3", inv) + ", at rest " + r.reachDownAtRest.ToString("F3", inv)
                + "; touching at the saved pose: " + r.startsOn + "; at rest: " + r.restsOn + ")";
            string game = !gameTrimOn ? string.Empty : "; the trim in the game: " + gameTrimKind + " (state " + gameTrimState + (string.IsNullOrEmpty(gameTrimEnd) ? string.Empty : ", its attempt ended " + gameTrimEnd
                + " " + gameFramesSinceTrim + " frames before the save, vertices " + gameTrimVerticesBefore + " -> " + gameTrimVerticesAfter + ", trims " + gameTrimTrims + ", reach "
                + gameTrimReachBefore.ToString("F3", inv) + " -> " + gameTrimReachAfter.ToString("F3", inv) + " m") + (gameTrimmedAncestor >= 0 ? ", trimmed ancestor " + gameTrimmedAncestor : string.Empty) + ")";
            return name + ": tier " + tier + (reasons != null && reasons.Length > 0 ? " [" + string.Join("; ", reasons) + "]" : string.Empty)
                + (notes != null && notes.Length > 0 ? " (" + string.Join("; ", notes) + ")" : string.Empty) + game
                + ", fragment " + fragment + " (" + (string.IsNullOrEmpty(productPass) ? "no look" : productPass) + "), extents " + extents.x.ToString("F2", inv) + " x " + extents.y.ToString("F2", inv) + " x "
                + extents.z.ToString("F2", inv) + ", supports " + supports + " (exact and fixed " + supportsExactFixed + ", stand-ins " + supportsApproximate + ", boxes " + supportsAsBounds
                + ", asleep " + supportsAsleep + ", moving " + supportsMoving + ", left out " + supportsDropped + "); the game measured " + (measured ? Metres(measuredClearance) + " m against "
                + (string.IsNullOrEmpty(measuredAgainst) ? "nothing" : measuredAgainst) + (measuredAgainstSaved ? string.Empty : " (NOT saved)") : "nothing")
                + ", the replay's scene " + Metres(savedClearance) + " m; vertices " + verticesBeforeInAll + " -> " + verticesAfterInAll + " (trims " + trimsInAll + " on " + trimmedConvexes
                + " convexes; reach over 26 directions " + reach26Before.ToString("F3", inv) + " -> " + reach26After.ToString("F3", inv) + " m); as saved: " + run(asSaved)
                + (asSaved != null ? (offReproduced ? " = the game" : " NOT as the game") : string.Empty) + "; trimmed: " + run(trimmed)
                + (trimmed != null && asSaved != null ? " -> " + (trimCameDown * 100f).ToString("F1", inv) + " cm lower than as saved" + (trimSank ? ", SANK" : string.Empty) : string.Empty);
        }
    }

    /// <summary>
    /// Puts a saved piece back among its saved supports, in a physics scene of its own, and lets it settle twice: with its
    /// convexes as they were saved, and with them trimmed by the numbers the product runs (<see cref="ConvexTrimSearch"/>).
    /// The piece starts as it was saved -- its pose, its motion, whether gravity pulls it -- under the saved gravity,
    /// thresholds, iterations and materials. The supports are held still, whatever they were, which is why a piece is
    /// sorted (<see cref="ConvexTrimReplayResult.tier"/>) and the piece as saved is compared with what the game measured
    /// (<see cref="ConvexTrimReplayResult.offReproduced"/>) before the trimmed piece's fall is read as the trim's doing.
    /// A piece held in the game (anchors, kinematic, a joint) is not let go: its numbers are the geometry's alone.
    /// Play mode only (a scene with its own physics is made and unloaded); the steps are simulated here, one after
    /// another, so a replay takes no frames.
    /// </summary>
    public static unsafe class ConvexTrimCaptureReplay
    {
        // How far above a drawn vertex its ray starts: a vertex this far below a surface is still found below it.
        private const float RayLift = 1f;
        private static readonly RaycastHit[] s_hits = new RaycastHit[64];

        public static ConvexTrimReplayResult Run(ConvexTrimCapture capture, string name, CutConvexTrimSettings settings, int vertexLimit, float seconds = 15f, float step = 1f / 45f)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            var result = new ConvexTrimReplayResult
            {
                name = name, fragment = capture.fragment, supports = capture.supports.Length, supportsDropped = capture.supportsDropped, productPass = capture.productPass,
                meshVertices = capture.meshPositions.Length, triangles = capture.meshIndices != null ? capture.meshIndices.Length / 3 : 0,
                free = !capture.fixedByAnchors && !capture.kinematic && capture.joints == 0, resting = capture.Resting,
                measured = capture.measured, measuredClearance = capture.measuredClearance, measuredAgainst = capture.measuredAgainst,
                measuredAgainstSaved = capture.measuredAgainstSupport >= 0 || string.IsNullOrEmpty(capture.measuredAgainst),
            };
            int count = capture.convexes.Length;
            var before = new Vector3[count][];
            var after = new Vector3[count][];
            Trim(capture, settings, vertexLimit, result, before, after);
            NoteGameTrim(capture, result);
            var placed = new List<(Collider collider, ConvexTrimCaptureSupport support)>();

            Vector3 gravity = Physics.gravity;
            float bounce = Physics.bounceThreshold, contactOffset = Physics.defaultContactOffset;
            Scene scene = SceneManager.CreateScene("Convex trim replay " + Guid.NewGuid().ToString("N"), new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            var made = new List<UnityEngine.Object>();
            try
            {
                // the frame's own physics, for as long as the replay's colliders are made and stepped
                Physics.gravity = capture.gravity;
                if (capture.bounceThreshold > 0f) Physics.bounceThreshold = capture.bounceThreshold;
                if (capture.contactOffset > 0f) Physics.defaultContactOffset = capture.contactOffset;
                PhysicsScene physics = scene.GetPhysicsScene();
                foreach (ConvexTrimCaptureSupport support in capture.supports) placed.Add((MakeSupport(support, scene, made), support));
                Physics.SyncTransforms();
                Matrix4x4 savedMeshToWorld = Matrix4x4.TRS(capture.position, capture.rotation, Vector3.one) * capture.geometryToOwner;
                result.savedClearance = Clearance(physics, capture.meshPositions, savedMeshToWorld, capture.supportReach, c => true, out _, out _);
                if (result.free)
                {
                    result.asSaved = Settle(capture, before, scene, physics, made, placed, seconds, step);
                    result.trimmed = result.trimsInAll > 0 ? Settle(capture, after, scene, physics, made, placed, seconds, step) : null;
                }
            }
            finally
            {
                Physics.gravity = gravity;
                Physics.bounceThreshold = bounce;
                Physics.defaultContactOffset = contactOffset;
                foreach (UnityEngine.Object o in made)
                {
                    if (o != null) UnityEngine.Object.Destroy(o);
                }

                SceneManager.UnloadSceneAsync(scene);
            }

            Sort(capture, result);
            if (result.asSaved != null)
            {
                result.offReproduced = capture.measured && result.asSaved.slept && result.asSaved.moved <= ConvexTrimReplayResult.StaysMeters
                    && result.asSaved.turnedDegrees <= ConvexTrimReplayResult.StaysDegrees
                    && Mathf.Abs(result.asSaved.clearance - capture.measuredClearance) <= ConvexTrimReplayResult.SinkMeters;
                if (result.trimmed != null)
                {
                    result.trimCameDown = result.trimmed.lowestCameDown - result.asSaved.lowestCameDown;
                    result.trimSank = result.trimmed.clearance < -ConvexTrimReplayResult.SinkMeters && result.trimmed.clearance < result.asSaved.clearance - ConvexTrimReplayResult.SinkMeters;
                }
            }

            return result;
        }

        private static void NoteGameTrim(ConvexTrimCapture capture, ConvexTrimReplayResult result)
        {
            result.gameTrimOn = capture.trimEnabled;
            result.gameTrimState = capture.trimState ?? string.Empty;
            result.gameTrimEnd = capture.trimHasRecord ? capture.trimEnd : string.Empty;
            result.gameTrimVerticesBefore = capture.trimVerticesBefore;
            result.gameTrimVerticesAfter = capture.trimVerticesAfter;
            result.gameTrimTrims = capture.trimTrims;
            result.gameTrimReachBefore = capture.trimReachBefore;
            result.gameTrimReachAfter = capture.trimReachAfter;
            result.gameTrimmedAncestor = capture.trimmedAncestor;
            result.gameFramesSinceTrim = capture.trimHasRecord ? capture.frame - capture.trimEndedFrame : 0;
            string state = result.gameTrimState;
            bool done = state == nameof(ConvexTrimPass.Done);
            result.gameTrimKind = !capture.trimEnabled ? "trim off"
                : done && capture.trimMarkedEnd == nameof(ConvexTrimEnd.Published) ? "published"
                : done && (capture.trimMarkedEnd == nameof(ConvexTrimEnd.BelowThreshold) || capture.trimMarkedEnd == nameof(ConvexTrimEnd.AllRefused))
                    ? (capture.trimmedAncestor >= 0 ? "inherited" : "nothing to trim")
                : done ? "ended " + capture.trimMarkedEnd
                : state == "InAttempt" || state == "Queued" || state == nameof(ConvexTrimPass.Candidate) || state == nameof(ConvexTrimPass.CutUnderWay) || state == nameof(ConvexTrimPass.GeometryOpen)
                    ? "no turn yet"
                : "passed over";
        }

        // Which evaluation the capture may enter, and why not the main one. After the piece as saved was looked at: what it
        // touched at the saved pose decides, not what was merely near.
        private static void Sort(ConvexTrimCapture capture, ConvexTrimReplayResult result)
        {
            foreach (ConvexTrimCaptureSupport support in capture.supports)
            {
                if (support.kind == "bounds") result.supportsAsBounds++;
                else if (!support.exact) result.supportsApproximate++;
                if (support.hasBody && !support.kinematic)
                {
                    if (support.sleeping) result.supportsAsleep++;
                    else result.supportsMoving++;
                }
                else if (support.exact)
                {
                    result.supportsExactFixed++;
                }
            }

            var c = new List<string>();
            var b = new List<string>();
            var notes = new List<string>();
            if (!result.large) c.Add("too small for the trim");
            else if (!string.IsNullOrEmpty(capture.productPass) && capture.productPass != nameof(ConvexTrimPass.Candidate) && capture.productPass != nameof(ConvexTrimPass.Done))
            {
                c.Add("the product's trim passes it over: " + capture.productPass);
            }

            if (capture.fixedByAnchors) c.Add("held by anchors");
            if (capture.kinematic) c.Add("kinematic");
            if (capture.joints > 0) c.Add("held by a joint");
            if (!capture.Resting) c.Add("it was moving");
            if (!capture.useGravity) c.Add("gravity did not pull it");
            if (!capture.measured) c.Add("nothing was measured at the save");
            if (capture.supportsDropped > 0 || capture.nearOverflow) c.Add("supports were left out for the limit");
            if (capture.measured && !result.measuredAgainstSaved) c.Add("what the game measured against is not saved");
            if (result.asSaved != null)
            {
                ConvexTrimTouch on = result.asSaved.startsOn;
                if (on.moving > 0) c.Add("it touched a body that was moving");
                if (on.bounds > 0) c.Add("it touched a support saved as its world box alone");
                if (on.standIn > 0) b.Add("it touched a stand-in (heightfield)");
                if (on.asleep > 0) b.Add("it touched a body asleep, held still here");
                if (on.All == 0) b.Add("it touched no saved support");
                if (result.supportsMoving > 0 && on.moving == 0) notes.Add("near and not touched: " + result.supportsMoving + " moving");
                if (result.supportsAsleep > on.asleep) notes.Add("near and not touched: " + (result.supportsAsleep - on.asleep) + " asleep");
                if (result.trimmed != null && (result.trimmed.restsOn.asleep > 0 || result.trimmed.restsOn.moving > 0)) notes.Add("trimmed, it came to rest on a body that was not held in the game");
            }

            result.tier = c.Count > 0 ? "C" : b.Count > 0 ? "B" : "A";
            c.AddRange(b);
            result.reasons = c.ToArray();
            result.notes = notes.ToArray();
        }

        // The numbers: the saved convexes as B-reps (their edges made from their faces), the mesh's support planes, the trims.
        private static void Trim(ConvexTrimCapture capture, CutConvexTrimSettings settings, int vertexLimit, ConvexTrimReplayResult result, Vector3[][] before, Vector3[][] after)
        {
            int count = capture.convexes.Length;
            var blocks = new List<NativeArray<byte>>();
            var workspaceBlock = default(NativeArray<byte>);
            try
            {
                var ranges = new ConvexBrepRange[count];
                var banks = new ConvexBrepBank[count];
                float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
                for (int c = 0; c < count; c++)
                {
                    ConvexTrimCaptureConvex convex = capture.convexes[c];
                    before[c] = convex.vertices;
                    after[c] = convex.vertices;
                    foreach (Vector3 p in convex.vertices) { lo = math.min(lo, p); hi = math.max(hi, p); }
                    BrepBuffer b = MakeBrep(convex, blocks);
                    banks[c] = new ConvexBrepBank { vertices = b.v, faceOffsets = b.faceOff, faceIndices = b.faceIdx, faceEdges = b.faceEdge, edges = b.edges };
                    ranges[c] = new ConvexBrepRange { vertexCount = b.V, faceCount = b.F, faceIndexCount = b.I, edgeCount = b.E, maxFaceLoop = ConvexTrimSearch.MaxFaceLoop(in b) };
                }

                result.extents = (Vector3)(hi - lo);
                float3 e = hi - lo;
                result.large = (e.x > settings.minSizeMeters ? 1 : 0) + (e.y > settings.minSizeMeters ? 1 : 0) + (e.z > settings.minSizeMeters ? 1 : 0) >= 2;
                ConvexTrimWorkspace.Layout(null, ranges, vertexLimit, out long bytes);
                workspaceBlock = new NativeArray<byte>(checked((int)bytes), Allocator.Persistent);
                ConvexTrimWorkspace w = ConvexTrimWorkspace.Layout((byte*)workspaceBlock.GetUnsafePtr(), ranges, vertexLimit, out _);
                for (int c = 0; c < count; c++) w.banks[c] = banks[c];
                var vertices = new VpRenderVertex[capture.meshPositions.Length];
                for (int i = 0; i < vertices.Length; i++) vertices[i].position = capture.meshPositions[i];
                var indices = new uint[capture.meshIndices.Length];
                for (int i = 0; i < indices.Length; i++) indices[i] = (uint)capture.meshIndices[i];
                float4x4 geometryToOwner = capture.geometryToOwner, shapeToOwner = capture.shapeToOwner;
                float* support = stackalloc float[ConvexTrimSearch.Directions];
                bool planes;
                fixed (VpRenderVertex* v = vertices)
                fixed (uint* ix = indices)
                {
                    planes = ConvexTrimSearch.MeshSupport(v, vertices.Length, ix, indices.Length, in geometryToOwner, support);
                }

                if (!planes || !ConvexTrimSearch.IsRigid(in shapeToOwner)) throw new InvalidDataException("the capture's mesh or frames are not usable");
                ConvexTrimSearch.SetPlanes(ref w, in shapeToOwner, support);
                // A body the product would not look at (too small) is replayed untrimmed: the threshold is set out of reach.
                ConvexTrimSearch.Run(ref w, result.large ? settings.minOverhangMeters : float.MaxValue, vertexLimit, settings.minKeptVolume, settings.minKeptVolumeRatio, out ConvexTrimOutcome outcome);
                result.verticesBefore = new int[count];
                result.verticesAfter = new int[count];
                result.trims = new int[count];
                for (int c = 0; c < count; c++)
                {
                    ref ConvexTrimConvex convex = ref w.convexes[c];
                    result.verticesBefore[c] = convex.verticesBefore;
                    result.verticesAfter[c] = convex.verticesAfter;
                    result.trims[c] = convex.trims;
                    if (convex.trims == 0) continue;
                    after[c] = new Vector3[convex.current.V];
                    for (int v = 0; v < convex.current.V; v++) after[c][v] = convex.current.v[v];
                }

                result.verticesBeforeInAll = outcome.verticesBefore;
                result.verticesAfterInAll = outcome.verticesAfter;
                result.trimsInAll = outcome.trims;
                result.trimmedConvexes = outcome.trimmedConvexes;
                result.candidatesBefore = outcome.candidatesBefore;
                result.reach26Before = outcome.overhangBefore;
                result.reach26After = outcome.overhangAfter;
                result.left = new int[(int)ConvexTrimRefusal.Count];
                for (int r = 0; r < result.left.Length; r++) result.left[r] = outcome.left[r];
            }
            finally
            {
                if (workspaceBlock.IsCreated) workspaceBlock.Dispose();
                foreach (NativeArray<byte> block in blocks) block.Dispose();
            }
        }

        private static BrepBuffer MakeBrep(ConvexTrimCaptureConvex convex, List<NativeArray<byte>> blocks)
        {
            int v = convex.vertices.Length, f = convex.faceOffsets.Length - 1, i = convex.faceIndices.Length, e = i / 2;
            long vAt = 0;
            long fAt = vAt + Align((long)Math.Max(1, v) * sizeof(float3));
            long iAt = fAt + Align((long)(f + 1) * sizeof(int));
            long feAt = iAt + Align((long)Math.Max(1, i) * sizeof(int));
            long eAt = feAt + Align((long)Math.Max(1, i) * sizeof(int));
            int hashCap = math.ceilpow2(math.max(16, 2 * i));
            long keysAt = eAt + Align((long)Math.Max(1, e) * sizeof(BrepEdge));
            long valuesAt = keysAt + Align((long)hashCap * sizeof(long));
            long bytes = valuesAt + Align((long)hashCap * sizeof(int));
            var block = new NativeArray<byte>(checked((int)bytes), Allocator.Persistent);
            blocks.Add(block);
            byte* at = (byte*)block.GetUnsafePtr();
            var b = new BrepBuffer
            {
                v = (float3*)(at + vAt), faceOff = (int*)(at + fAt), faceIdx = (int*)(at + iAt), faceEdge = (int*)(at + feAt), edges = (BrepEdge*)(at + eAt),
                vCap = v, fCap = f, iCap = i, eCap = e, V = v, F = f, I = i,
            };
            for (int k = 0; k < v; k++) b.v[k] = convex.vertices[k];
            for (int k = 0; k <= f; k++) b.faceOff[k] = convex.faceOffsets[k];
            for (int k = 0; k < i; k++) b.faceIdx[k] = convex.faceIndices[k];
            int probes = 0;
            int status = ConvexClipKernel.BuildEdgeTable(ref b, (long*)(at + keysAt), (int*)(at + valuesAt), hashCap, ref probes);
            if (status != 0) throw new InvalidDataException("a saved convex is not a closed two-manifold: " + (CutStatus)status);
            return b;
        }

        private static long Align(long bytes) => (bytes + 15) & ~15L;

        private static Collider MakeSupport(ConvexTrimCaptureSupport support, Scene scene, List<UnityEngine.Object> made)
        {
            var host = new GameObject("Support " + support.name);
            made.Add(host);
            SceneManager.MoveGameObjectToScene(host, scene);
            Transform t = host.transform;
            Collider collider;
            switch (support.kind)
            {
                case "box":
                {
                    t.SetPositionAndRotation(support.position, support.rotation);
                    t.localScale = support.scale;
                    var box = host.AddComponent<BoxCollider>();
                    box.center = support.center;
                    box.size = support.size;
                    collider = box;
                    break;
                }

                case "sphere":
                {
                    t.SetPositionAndRotation(support.position, support.rotation);
                    t.localScale = support.scale;
                    var sphere = host.AddComponent<SphereCollider>();
                    sphere.center = support.center;
                    sphere.radius = support.radius;
                    collider = sphere;
                    break;
                }

                case "capsule":
                {
                    t.SetPositionAndRotation(support.position, support.rotation);
                    t.localScale = support.scale;
                    var capsule = host.AddComponent<CapsuleCollider>();
                    capsule.center = support.center;
                    capsule.radius = support.radius;
                    capsule.height = support.height;
                    capsule.direction = support.direction;
                    collider = capsule;
                    break;
                }

                case "convex":
                case "mesh":
                {
                    t.SetPositionAndRotation(support.position, support.rotation);
                    t.localScale = support.scale;
                    bool convex = support.kind == "convex";
                    int[] triangles = support.triangles;
                    if (convex)
                    {
                        // a convex's mesh has its vertices alone: any triangles over them serve (PhysX makes the hull of the points)
                        triangles = new int[Math.Max(0, support.vertices.Length - 2) * 3];
                        for (int k = 0; k + 2 < support.vertices.Length; k++) { triangles[3 * k] = 0; triangles[(3 * k) + 1] = k + 1; triangles[(3 * k) + 2] = k + 2; }
                    }

                    collider = MeshSupport(host, "Support " + support.name, support.vertices, triangles, convex, made);
                    break;
                }

                case "heightfield":
                {
                    // world coordinates: a quad where all four corners were found
                    var vertices = new Vector3[support.gridX * support.gridZ];
                    var triangles = new List<int>();
                    for (int j = 0; j < support.gridZ; j++)
                    {
                        for (int i = 0; i < support.gridX; i++)
                        {
                            vertices[(j * support.gridX) + i] = new Vector3(support.gridOrigin.x + (i * support.gridStep), support.heights[(j * support.gridX) + i], support.gridOrigin.z + (j * support.gridStep));
                        }
                    }

                    bool Found(int i, int j) => support.heights[(j * support.gridX) + i] > -ConvexTrimCapture.NoSurface * 0.5f;
                    for (int j = 0; j + 1 < support.gridZ; j++)
                    {
                        for (int i = 0; i + 1 < support.gridX; i++)
                        {
                            if (!Found(i, j) || !Found(i + 1, j) || !Found(i, j + 1) || !Found(i + 1, j + 1)) continue;
                            int a = (j * support.gridX) + i, b = a + 1, c = a + support.gridX, d = c + 1;
                            triangles.Add(a); triangles.Add(c); triangles.Add(b);
                            triangles.Add(b); triangles.Add(c); triangles.Add(d);
                        }
                    }

                    if (triangles.Count == 0)
                    {
                        t.position = support.boundsCenter;
                        var none = host.AddComponent<BoxCollider>();
                        none.size = Vector3.zero;
                        none.enabled = false;   // single samples, no surface between them: nothing to stand on
                        collider = none;
                        break;
                    }

                    for (int k = 0; k < vertices.Length; k++)
                    {
                        if (vertices[k].y <= -ConvexTrimCapture.NoSurface * 0.5f) vertices[k].y = support.boundsCenter.y;   // not in any triangle
                    }

                    collider = MeshSupport(host, "Support " + support.name, vertices, triangles.ToArray(), false, made);
                    break;
                }

                default:
                {
                    t.position = support.boundsCenter;
                    var box = host.AddComponent<BoxCollider>();
                    box.size = support.boundsSize;
                    collider = box;
                    break;
                }
            }

            if (support.material != null) collider.sharedMaterial = support.material.Make(made);
            return collider;
        }

        /// <summary>How near a piece's convex is to a support for the two to count as touching, metres.</summary>
        public const float TouchMeters = 0.02f;

        private static readonly Vector3[] s_nudges =
        {
            Vector3.zero, Vector3.down * TouchMeters, Vector3.up * TouchMeters, Vector3.left * TouchMeters, Vector3.right * TouchMeters,
            Vector3.forward * TouchMeters, Vector3.back * TouchMeters,
        };

        // The supports the piece's convexes overlap where they stand, or would if moved TouchMeters along an axis.
        private static ConvexTrimTouch Touching(List<MeshCollider> own, List<(Collider collider, ConvexTrimCaptureSupport support)> placed)
        {
            var touch = default(ConvexTrimTouch);
            foreach ((Collider collider, ConvexTrimCaptureSupport support) in placed)
            {
                if (collider == null || !collider.enabled) continue;
                Transform st = collider.transform;
                bool touches = false;
                for (int k = 0; k < own.Count && !touches; k++)
                {
                    Transform mt = own[k].transform;
                    for (int n = 0; n < s_nudges.Length && !touches; n++)
                    {
                        touches = Physics.ComputePenetration(own[k], mt.position + s_nudges[n], mt.rotation, collider, st.position, st.rotation, out _, out _);
                    }
                }

                if (!touches) continue;
                if (support.kind == "bounds") touch.bounds++;
                else if (!support.exact) touch.standIn++;
                else if (support.hasBody && !support.kinematic)
                {
                    if (support.sleeping) touch.asleep++;
                    else touch.moving++;
                }
                else
                {
                    touch.fixedExact++;
                }
            }

            return touch;
        }

        private static Collider MeshSupport(GameObject host, string name, Vector3[] vertices, int[] triangles, bool convex, List<UnityEngine.Object> made)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            made.Add(mesh);
            if (vertices.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            var collider = host.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = convex;
            collider.sharedMesh = mesh;
            return collider;
        }

        private static ConvexTrimReplayRun Settle(ConvexTrimCapture capture, Vector3[][] convexes, Scene scene, PhysicsScene physics, List<UnityEngine.Object> made,
            List<(Collider collider, ConvexTrimCaptureSupport support)> placed, float seconds, float step)
        {
            var piece = new GameObject("Replayed piece " + capture.fragment);
            made.Add(piece);
            SceneManager.MoveGameObjectToScene(piece, scene);
            piece.transform.SetPositionAndRotation(capture.position, capture.rotation);
            var frame = new GameObject("Shape Frame");
            frame.transform.SetParent(piece.transform, false);
            Matrix4x4 shapeToOwner = capture.shapeToOwner;
            frame.transform.SetLocalPositionAndRotation(shapeToOwner.GetColumn(3), shapeToOwner.rotation);
            var body = piece.AddComponent<Rigidbody>();
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = capture.mass;
            body.centerOfMass = capture.centreOfMass;
            body.inertiaTensor = capture.inertia;
            body.inertiaTensorRotation = capture.inertiaRotation;
            body.useGravity = capture.useGravity;
            body.isKinematic = false;
            body.linearDamping = capture.linearDamping;
            body.angularDamping = capture.angularDamping;
            if (capture.bodySleepThreshold > 0f) body.sleepThreshold = capture.bodySleepThreshold;
            if (capture.maxDepenetrationVelocity > 0f) body.maxDepenetrationVelocity = capture.maxDepenetrationVelocity;
            if (capture.bodySolverIterations > 0) body.solverIterations = capture.bodySolverIterations;
            if (capture.bodySolverVelocityIterations > 0) body.solverVelocityIterations = capture.bodySolverVelocityIterations;
            PhysicsMaterial material = capture.material != null ? capture.material.Make(made) : null;
            var own = new List<MeshCollider>();
            for (int c = 0; c < convexes.Length; c++)
            {
                Vector3[] points = convexes[c];
                float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
                var copy = new NativeArray<float3>(points.Length, Allocator.Temp);
                for (int k = 0; k < points.Length; k++) { copy[k] = points[k]; lo = math.min(lo, points[k]); hi = math.max(hi, points[k]); }
                Mesh mesh = CutConvexTrim.MakePositionsMesh("Replayed convex " + c, (float3*)copy.GetUnsafePtr(), points.Length, lo, hi);
                copy.Dispose();
                made.Add(mesh);
                Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
                var collider = frame.AddComponent<MeshCollider>();
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = mesh;
                collider.sharedMaterial = material;
                if (capture.ownContactOffset > 0f) collider.contactOffset = capture.ownContactOffset;
                own.Add(collider);
            }

            Physics.SyncTransforms();
            var run = new ConvexTrimReplayRun { startsOn = Touching(own, placed) };
            body.linearVelocity = capture.velocity;
            body.angularVelocity = capture.angularVelocity;
            body.WakeUp();
            int most = Mathf.CeilToInt(seconds / step);
            for (run.steps = 0; run.steps < most;)
            {
                physics.Simulate(step);
                run.steps++;
                if (run.steps > 20 && body.IsSleeping())
                {
                    run.slept = true;
                    break;
                }
            }

            Physics.SyncTransforms();
            piece.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Matrix4x4 savedOwnerToWorld = Matrix4x4.TRS(capture.position, capture.rotation, Vector3.one);
            Matrix4x4 ownerToWorld = Matrix4x4.TRS(position, rotation, Vector3.one);
            run.clearance = Clearance(physics, capture.meshPositions, ownerToWorld * capture.geometryToOwner, capture.supportReach, c => c.attachedRigidbody != body, out _, out _);
            run.lowestCameDown = LowestMesh(capture, savedOwnerToWorld) - LowestMesh(capture, ownerToWorld);
            run.moved = Vector3.Distance(position, capture.position);
            run.turnedDegrees = Quaternion.Angle(rotation, capture.rotation);
            run.reachDownAtSaved = ReachDown(capture, convexes, capture.rotation);
            run.reachDownAtRest = ReachDown(capture, convexes, rotation);
            run.restsOn = Touching(own, placed);

            // the piece leaves the scene before the next variant is put in its place
            piece.SetActive(false);
            return run;
        }

        private static float LowestMesh(ConvexTrimCapture capture, Matrix4x4 ownerToWorld)
        {
            Matrix4x4 toWorld = ownerToWorld * capture.geometryToOwner;
            float lowest = float.MaxValue;
            foreach (Vector3 p in capture.meshPositions) lowest = Mathf.Min(lowest, toWorld.MultiplyPoint3x4(p).y);
            return lowest;
        }

        private static float ReachDown(ConvexTrimCapture capture, Vector3[][] convexes, Quaternion rotation)
        {
            Matrix4x4 turn = Matrix4x4.Rotate(rotation);
            Matrix4x4 mesh = turn * capture.geometryToOwner, shape = turn * capture.shapeToOwner;
            float lowestMesh = float.MaxValue, lowestConvex = float.MaxValue;
            foreach (Vector3 p in capture.meshPositions) lowestMesh = Mathf.Min(lowestMesh, mesh.MultiplyPoint3x4(p).y);
            foreach (Vector3[] convex in convexes)
            {
                foreach (Vector3 p in convex) lowestConvex = Mathf.Min(lowestConvex, shape.MultiplyPoint3x4(p).y);
            }

            return lowestMesh - lowestConvex;
        }

        /// <summary>
        /// The least clearance straight down from a vertex of what is drawn to a collider that <paramref name="counts"/>:
        /// the distance to the surface below it within <paramref name="below"/> metres, or -- negative -- how far it is
        /// below the surface of a collider it is inside of (asked by rays from <see cref="RayLift"/> above the vertex; a
        /// collider whose top is above the vertex counts only where the vertex is inside it, so that what lies on the
        /// piece is not read as the piece having sunk; a triangle mesh has no inside and counts always).
        /// <see cref="ConvexTrimCapture.NoSurface"/> where nothing is there. One measure for the game's scene at the save
        /// and for the replay's.
        /// </summary>
        public static float Clearance(PhysicsScene physics, Vector3[] meshPositions, Matrix4x4 meshToWorld, float below, Func<Collider, bool> counts, out RaycastHit nearest, out Vector3 at)
        {
            float least = ConvexTrimCapture.NoSurface;
            nearest = default;
            at = default;
            float length = RayLift + Mathf.Max(below, 0.05f);
            foreach (Vector3 local in meshPositions)
            {
                Vector3 p = meshToWorld.MultiplyPoint3x4(local);
                int n = physics.Raycast(p + (Vector3.up * RayLift), Vector3.down, s_hits, length, ~0, QueryTriggerInteraction.Ignore);
                for (int k = 0; k < n; k++)
                {
                    Collider collider = s_hits[k].collider;
                    float d = s_hits[k].distance - RayLift;
                    if (d >= least || collider == null || !counts(collider)) continue;
                    if (d < 0f && HasInside(collider) && !Inside(collider, p)) continue;
                    least = d;
                    nearest = s_hits[k];
                    at = p;
                }
            }

            return least;
        }

        // The colliders a point can be asked to be inside of (Collider.ClosestPoint answers for these alone).
        private static bool HasInside(Collider collider)
        {
            return collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider || (collider is MeshCollider mesh && mesh.convex);
        }

        // Inside, and not merely on the collider's side: the point and four points a centimetre from it, level with it.
        private static bool Inside(Collider collider, Vector3 p)
        {
            const float Aside = 0.01f;
            return At(collider, p) && At(collider, p + new Vector3(Aside, 0f, 0f)) && At(collider, p - new Vector3(Aside, 0f, 0f))
                && At(collider, p + new Vector3(0f, 0f, Aside)) && At(collider, p - new Vector3(0f, 0f, Aside));
        }

        private static bool At(Collider collider, Vector3 p) => (collider.ClosestPoint(p) - p).sqrMagnitude <= 1e-10f;

        /// <summary>Writes a capture into <paramref name="folder"/> under a name of its own, and returns the file's path.</summary>
        public static string Write(ConvexTrimCapture capture, string folder, int number)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "float-" + number.ToString("000", CultureInfo.InvariantCulture) + "-fragment" + capture.fragment.ToString(CultureInfo.InvariantCulture) + ".json");
            File.WriteAllText(path, capture.ToJson());
            return path;
        }

        /// <summary>The heads of a trim record's line (<see cref="TrimRecordLine"/>).</summary>
        public const string TrimRecordHeads = "serial\tfragment\tend\tissuedFrame\tendedFrame\tconvexes\tverticesBefore\tverticesAfter\ttrims\ttrimmedConvexes\treachBefore\treachAfter\tleft"
            + "\tperConvexBefore\tperConvexAfter\tworkerSearchMs\tworkerClipMs\tworkerBakeMs\tissueMs\tcollectMs\tmeshMs\thullCheckMs\tpublishMs\tnote";

        /// <summary>One attempt's record as a tab-separated line (no line end).</summary>
        public static string TrimRecordLine(ConvexTrimRecord r)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            string Ms(double seconds) => (seconds * 1000.0).ToString("F3", inv);
            var text = new StringBuilder(256);
            text.Append(r.serial).Append('\t').Append(r.fragment.value).Append('\t').Append(r.end).Append('\t').Append(r.issuedFrame).Append('\t').Append(r.endedFrame).Append('\t')
                .Append(r.convexes).Append('\t').Append(r.verticesBeforeInAll).Append('\t').Append(r.verticesAfterInAll).Append('\t').Append(r.trimsInAll).Append('\t').Append(r.trimmedConvexes)
                .Append('\t').Append(r.overhangBefore.ToString("F3", inv)).Append('\t').Append(r.overhangAfter.ToString("F3", inv)).Append('\t').Append(string.Join(",", r.left))
                .Append('\t').Append(r.verticesBefore != null ? string.Join(",", r.verticesBefore) : string.Empty).Append('\t').Append(r.verticesAfter != null ? string.Join(",", r.verticesAfter) : string.Empty)
                .Append('\t').Append(Ms(r.workerSearchSeconds)).Append('\t').Append(Ms(r.workerClipSeconds)).Append('\t').Append(Ms(r.workerBakeSeconds)).Append('\t').Append(Ms(r.issueSeconds))
                .Append('\t').Append(Ms(r.collectSeconds)).Append('\t').Append(Ms(r.meshSeconds)).Append('\t').Append(Ms(r.hullCheckSeconds)).Append('\t').Append(Ms(r.publishSeconds))
                .Append('\t').Append((r.note ?? string.Empty).Replace('\t', ' ').Replace('\n', ' '));
            return text.ToString();
        }

        /// <summary>The heads of a slow frame's line (<see cref="SlowFrameLine"/>).</summary>
        public static string SlowFrameHeads
        {
            get
            {
                var text = new StringBuilder("frame\twholeMs\tstagesMs\tstages");
                for (int k = 0; k < (int)ConvexTrimFramePart.Count; k++) text.Append('\t').Append((ConvexTrimFramePart)k).Append("Ms");
                text.Append("\tremainingBeforeMs\tremainingAfterMs\tlaterStepMs\thold\theavyWork\tgcThisFrame\tgcDuringTurn\tframeDeltaMs\tcutsInFlight\tsplitHasAttempt\tsplitWaitingMain\tsplitFound"
                    + "\tfragment\tconvexes\tmeshes\tindexCount\tblockBytes");
                return text.ToString();
            }
        }

        /// <summary>One slow frame's record as a tab-separated line (no line end).</summary>
        public static string SlowFrameLine(ConvexTrimSlowFrame f)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            string Ms(double seconds) => (seconds * 1000.0).ToString("F3", inv);
            var text = new StringBuilder(256);
            text.Append(f.frame).Append('\t').Append(Ms(f.wholeSeconds)).Append('\t').Append(Ms(f.stagesSeconds)).Append('\t').Append(f.stages.ToString().Replace(", ", "+"));
            for (int k = 0; k < (int)ConvexTrimFramePart.Count; k++) text.Append('\t').Append(Ms(f.partSeconds[k]));
            text.Append('\t').Append(Ms(f.remainingBeforeSeconds)).Append('\t').Append(Ms(f.remainingAfterSeconds)).Append('\t').Append(Ms(f.laterStepSeconds)).Append('\t').Append(f.hold)
                .Append('\t').Append((f.heavyWork ?? string.Empty).Replace('\t', ' ')).Append('\t').Append(f.gcThisFrame).Append('\t').Append(f.gcDuringTurn).Append('\t').Append(Ms(f.frameDeltaSeconds))
                .Append('\t').Append(f.cutsInFlight).Append('\t').Append(f.splitHasAttempt ? 1 : 0).Append('\t').Append(f.splitWaitingMain).Append('\t').Append(f.splitFound)
                .Append('\t').Append(f.fragment).Append('\t').Append(f.convexes).Append('\t').Append(f.meshes).Append('\t').Append(f.indexCount).Append('\t').Append(f.blockBytes);
            return text.ToString();
        }

        /// <summary>
        /// Writes the trim's line of tallies and the records it keeps now (a line an attempt, tab-separated) into
        /// <paramref name="folder"/> as trim-records.txt, over the file of an earlier call. The trim keeps the newest
        /// <see cref="CutConvexTrim.RecordsKept"/> only: where older ones were dropped the first line says how many -- a
        /// session's every attempt is kept by <see cref="ConvexTrimRecordLog"/>, not by this. Null where the world has no trim.
        /// </summary>
        public static string WriteTrimRecords(CutWorldRoot world, string folder)
        {
            if (world == null || world.ConvexTrim == null) return null;
            CutConvexTrim trim = world.ConvexTrim;
            var text = new StringBuilder();
            text.AppendLine("frame " + Time.frameCount + ": records below " + trim.Records.Count + (trim.RecordsDropped > 0 ? " (THE OLDEST " + trim.RecordsDropped + " ATTEMPTS ARE NOT KEPT)" : " (every attempt)")
                + "; " + trim.Describe());
            text.AppendLine(TrimRecordHeads);
            foreach (ConvexTrimRecord r in trim.Records) text.AppendLine(TrimRecordLine(r));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "trim-records.txt");
            File.WriteAllText(path, text.ToString());
            return path;
        }

        /// <summary>
        /// The table of a folder's replays: the main evaluation first -- the pieces of tier A whose replay as saved stood as
        /// the game stood, and how far the trim brought each down -- then tier B the same way, then what the others are
        /// good for (the geometry and the vertices), and the vertices of all.
        /// </summary>
        public static string Summarise(IReadOnlyList<ConvexTrimReplayResult> results)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            string M(float v) => v >= ConvexTrimCapture.NoSurface * 0.5f ? "none" : v.ToString("F3", inv);
            foreach (string tier in new[] { "A", "B" })
            {
                int inTier = 0, reproduced = 0, trimmed = 0, lower = 0, sank = 0, awake = 0;
                var lines = new StringBuilder();
                foreach (ConvexTrimReplayResult r in results)
                {
                    if (r.tier != tier) continue;
                    inTier++;
                    if (!r.offReproduced)
                    {
                        lines.AppendLine("  " + r.name + ": the replay as saved is NOT as the game was (the game " + M(r.measuredClearance) + " m; the replay " + (r.asSaved != null ? M(r.asSaved.clearance)
                            + " m, moved " + r.asSaved.moved.ToString("F3", inv) + " m, turned " + r.asSaved.turnedDegrees.ToString("F1", inv) + " deg, " + (r.asSaved.slept ? "slept" : "not asleep") : "not run") + "): not counted");
                        continue;
                    }

                    reproduced++;
                    if (r.trimmed == null)
                    {
                        lines.AppendLine("  " + r.name + ": gap " + M(r.asSaved.clearance) + " m; nothing to trim (reach over 26 directions " + r.reach26Before.ToString("F3", inv) + " m)");
                        continue;
                    }

                    trimmed++;
                    if (r.trimCameDown > 0.005f) lower++;
                    if (r.trimSank) sank++;
                    if (!r.trimmed.slept) awake++;
                    lines.AppendLine("  " + r.name + ": gap " + M(r.asSaved.clearance) + " -> " + M(r.trimmed.clearance) + " m, " + (r.trimCameDown * 100f).ToString("F1", inv) + " cm lower"
                        + (r.trimSank ? ", SANK" : string.Empty) + (r.trimmed.slept ? string.Empty : ", not asleep") + "; reach down at the saved pose " + r.asSaved.reachDownAtSaved.ToString("F3", inv) + " -> "
                        + r.trimmed.reachDownAtSaved.ToString("F3", inv) + " m, at rest " + r.trimmed.reachDownAtRest.ToString("F3", inv) + " m; vertices " + r.verticesBeforeInAll + " -> "
                        + r.verticesAfterInAll + "; fragment " + r.fragment + "; rested on: " + r.asSaved.startsOn + " -> " + r.trimmed.restsOn);
                }

                text.AppendLine("tier " + tier + (tier == "A" ? " (free, at rest, everything it touched saved as it is and held still in the game)" : " (as A, but it touched a stand-in or a body asleep, or nothing saved)")
                    + ": " + inTier + " pieces; the replay as saved stood as the game stood for " + reproduced + "; of them trimmed " + trimmed + ": lower by more than 5 mm " + lower
                    + ", sank " + sank + ", not asleep " + awake);
                text.Append(lines);
            }

            // With the trim on in the game: what was still floating at the save, by what the trim had done of it.
            bool anyOn = false;
            foreach (ConvexTrimReplayResult r in results) anyOn |= r.gameTrimOn;
            if (anyOn)
            {
                var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var rows = new StringBuilder();
                int floating = 0;
                foreach (ConvexTrimReplayResult r in results)
                {
                    if (!r.gameTrimOn || !r.measured || r.measuredClearance <= ConvexTrimReplayResult.SinkMeters || r.measuredClearance >= ConvexTrimCapture.NoSurface * 0.5f) continue;
                    floating++;
                    kinds.TryGetValue(r.gameTrimKind, out int n);
                    kinds[r.gameTrimKind] = n + 1;
                    rows.AppendLine("  " + r.name + ": fragment " + r.fragment + ", gap " + M(r.measuredClearance) + " m, " + r.gameTrimKind + " (state " + r.gameTrimState
                        + (string.IsNullOrEmpty(r.gameTrimEnd) ? string.Empty : ", attempt ended " + r.gameTrimEnd + " " + r.gameFramesSinceTrim + " frames before, vertices " + r.gameTrimVerticesBefore + " -> "
                        + r.gameTrimVerticesAfter + ", reach " + r.gameTrimReachBefore.ToString("F3", inv) + " -> " + r.gameTrimReachAfter.ToString("F3", inv) + " m")
                        + (r.gameTrimmedAncestor >= 0 ? ", trimmed ancestor " + r.gameTrimmedAncestor : string.Empty) + "); the saved shape reaches " + r.reach26Before.ToString("F3", inv)
                        + " m over the 26 directions" + (r.asSaved != null ? ", " + r.asSaved.reachDownAtSaved.ToString("F3", inv) + " m straight down at the saved pose" : string.Empty)
                        + "; tier " + r.tier + (r.asSaved != null ? (r.offReproduced ? ", replay = the game" : ", replay NOT as the game") : string.Empty));
                }

                text.AppendLine("the trim was on in the game; still floating at the save (gap above " + ConvexTrimReplayResult.SinkMeters.ToString("F2", inv) + " m): " + floating + " captures");
                foreach (KeyValuePair<string, int> e in kinds) text.AppendLine("  " + e.Value + " x " + e.Key);
                text.Append(rows);
            }

            int others = 0;
            var why = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (ConvexTrimReplayResult r in results)
            {
                if (r.tier != "C") continue;
                others++;
                foreach (string reason in r.reasons)
                {
                    why.TryGetValue(reason, out int n);
                    why[reason] = n + 1;
                }
            }

            text.AppendLine("tier C (the geometry and the vertices only): " + others + " pieces");
            foreach (KeyValuePair<string, int> e in why) text.AppendLine("  " + e.Value + " x " + e.Key);

            int bodies = 0, more = 0, same = 0, fewer = 0, before = 0, after = 0, most = 0, limit = 0;
            foreach (ConvexTrimReplayResult r in results)
            {
                if (r.left != null) limit += r.left[(int)ConvexTrimRefusal.VertexLimit];
                if (r.trimsInAll == 0) continue;
                bodies++;
                before += r.verticesBeforeInAll;
                after += r.verticesAfterInAll;
                for (int c = 0; c < r.trims.Length; c++)
                {
                    if (r.trims[c] == 0) continue;
                    int d = r.verticesAfter[c] - r.verticesBefore[c];
                    if (d > 0) more++;
                    else if (d < 0) fewer++;
                    else same++;
                    most = Math.Max(most, d);
                }
            }

            text.AppendLine("vertices (the bake's input), all " + results.Count + " pieces: trimmed bodies " + bodies + ", " + before + " -> " + after + "; trimmed convexes with more " + more
                + ", as many " + same + ", fewer " + fewer + "; most added to one convex " + most + "; candidates left alone for the vertex limit " + limit);
            return text.ToString();
        }
    }

    /// <summary>
    /// Every attempt of a world's trim, a line each, appended to trim-records.txt in a folder as the attempts end -- so that
    /// a long session's attempts are all there, past the <see cref="CutConvexTrim.RecordsKept"/> the trim itself keeps. Made
    /// with what the trim keeps at that moment (and a line that says so where older attempts were already dropped), then fed
    /// by <see cref="CutConvexTrim.Recorded"/>. **Nothing is written, nor a line made, as an attempt ends**: the records are
    /// held as they are and written when <see cref="Flush"/> or <see cref="WriteTallies"/> is called (a press of the
    /// collection, the application's end) -- a file's append on the main thread in the frame an attempt ended was the
    /// trim's slowest frame of a session (2026-11-20: 40 of 66 frames of 0.5 ms or more, the longest 5.0 ms). Past
    /// <see cref="HeldMost"/> records they are written all the same, so that a session's memory stays bounded.
    /// The trim's line of tallies goes to trim-tallies.txt. Main thread; a diagnosis's (the float collection), not the product's.
    /// </summary>
    public sealed class ConvexTrimRecordLog : IDisposable
    {
        public const int HeldMost = 65536;

        private readonly CutConvexTrim _trim;
        private readonly CutSpatialSplit _split;
        private readonly string _folder;
        private readonly StringBuilder _held = new StringBuilder();
        private readonly StringBuilder _heldSlow = new StringBuilder();
        private readonly List<ConvexTrimRecord> _heldRecords = new List<ConvexTrimRecord>();
        private readonly List<ConvexTrimSlowFrame> _heldSlowFrames = new List<ConvexTrimSlowFrame>();
        private bool _disposed;

        /// <summary>Slow frames written or waiting to be (trim-slow-frames.txt).</summary>
        public long SlowLines { get; private set; }
        public string SlowPath { get; }

        /// <summary>Attempts written or waiting to be; those the trim had dropped before the log was made; write failures.</summary>
        public long Lines { get; private set; }
        public long MissedBefore { get; }
        public int Failures { get; private set; }
        public string Path { get; }
        public CutConvexTrim Trim => _trim;

        /// <param name="split">The world's additional split, for the table of the shapes the trim replaced (split-shape-retries.txt, written with the tallies); may be null.</param>
        public ConvexTrimRecordLog(CutConvexTrim trim, string folder, CutSpatialSplit split = null)
        {
            _trim = trim ?? throw new ArgumentNullException(nameof(trim));
            _split = split;
            _folder = folder;
            Directory.CreateDirectory(folder);
            Path = System.IO.Path.Combine(folder, "trim-records.txt");
            SlowPath = System.IO.Path.Combine(folder, "trim-slow-frames.txt");
            if (!File.Exists(SlowPath)) _heldSlow.AppendLine(ConvexTrimCaptureReplay.SlowFrameHeads);
            foreach (ConvexTrimSlowFrame slow in trim.SlowFrameRecords) HoldSlow(slow);
            trim.SlowFrame += OnSlowFrame;
            MissedBefore = trim.RecordsDropped;
            if (!File.Exists(Path)) _held.AppendLine(ConvexTrimCaptureReplay.TrimRecordHeads);
            _held.AppendLine("# a world's trim from frame " + Time.frameCount + (MissedBefore > 0 ? ": ITS OLDEST " + MissedBefore + " ATTEMPTS WERE NOT KEPT AND ARE NOT HERE" : ": every attempt follows"));
            foreach (ConvexTrimRecord record in trim.Records) Hold(record);
            trim.Recorded += OnRecorded;
            Flush();
        }

        private void OnRecorded(ConvexTrimRecord record)
        {
            Hold(record);
            if (_heldRecords.Count >= HeldMost) Flush();
        }

        private void OnSlowFrame(ConvexTrimSlowFrame slow)
        {
            HoldSlow(slow);
            if (_heldSlowFrames.Count >= HeldMost) Flush();
        }

        // (a record is not changed once its attempt ended: it is kept as it is, and its line made when it is written)
        private void HoldSlow(ConvexTrimSlowFrame slow)
        {
            _heldSlowFrames.Add(slow);
            SlowLines++;
        }

        private void Hold(ConvexTrimRecord record)
        {
            _heldRecords.Add(record);
            Lines++;
        }

        /// <summary>Appends what is held to the file. A failure keeps the lines for the next call and is counted.</summary>
        public void Flush()
        {
            foreach (ConvexTrimRecord record in _heldRecords) _held.AppendLine(ConvexTrimCaptureReplay.TrimRecordLine(record));
            _heldRecords.Clear();
            foreach (ConvexTrimSlowFrame slow in _heldSlowFrames) _heldSlow.AppendLine(ConvexTrimCaptureReplay.SlowFrameLine(slow));
            _heldSlowFrames.Clear();
            try
            {
                if (_held.Length != 0)
                {
                    File.AppendAllText(Path, _held.ToString());
                    _held.Clear();
                }

                if (_heldSlow.Length != 0)
                {
                    File.AppendAllText(SlowPath, _heldSlow.ToString());
                    _heldSlow.Clear();
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Failures++;
            }
        }

        /// <summary>Flushes, and writes the trim's line of tallies as it is now over trim-tallies.txt.</summary>
        public void WriteTallies()
        {
            Flush();
            try
            {
                File.WriteAllText(System.IO.Path.Combine(_folder, "trim-tallies.txt"), "frame " + Time.frameCount + ": attempts in trim-records.txt " + Lines
                    + (MissedBefore > 0 ? " (" + MissedBefore + " older ones missing)" : string.Empty) + (Failures > 0 ? ", write failures " + Failures : string.Empty)
                    + "; slow frames in trim-slow-frames.txt " + SlowLines + Environment.NewLine + _trim.Describe() + Environment.NewLine
                    + (_split != null ? _split.DescribeShapeRetries() + Environment.NewLine + _split.DescribeCandidateWaits() + Environment.NewLine : string.Empty));
                if (_split != null)
                {
                    var retries = new StringBuilder();
                    retries.AppendLine(CutSpatialSplit.ShapeRetryHeads);
                    foreach (SpatialSplitShapeRetry retry in _split.ShapeRetries) retries.AppendLine(CutSpatialSplit.ShapeRetryLine(retry));
                    File.WriteAllText(System.IO.Path.Combine(_folder, "split-shape-retries.txt"), retries.ToString());
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Failures++;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _trim.Recorded -= OnRecorded;
            _trim.SlowFrame -= OnSlowFrame;
            Flush();
        }
    }
}
