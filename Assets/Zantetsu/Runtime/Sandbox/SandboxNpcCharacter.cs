using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.Core.Animation;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// One Pose-Table-driven character of a sandbox scene made cuttable (Phase 4.52): at start it prepares the character's
    /// current-pose cut through the world's own entry (<see cref="CutWorldRoot.TryPrepareCharacterCut"/>) -- its fixed-scale
    /// renderer, its bone-local convexes from the authored hulls, the shared cold preparation -- finishes that preparation
    /// on the loading frames, and then gives the prepared cut to the scene's hit detector. From there the character is a
    /// target of real hits only; nothing here asks for a cut.
    /// <para>
    /// **Loaded and prepared once.** The hulls are read, placed in their bones' frames and copied into the prepared cut
    /// here, before any hit; nothing is read or prepared again when a hit comes.
    /// </para>
    /// <para>
    /// **Its bones, first.** Before anything is prepared, the character's Pose Table player is asked to confirm that its
    /// table drives every bone the character is drawn and cut with -- the renderer's bones, which the bone-local convexes
    /// are placed by too. If the table was not bound or a bone is not driven, the player applies nothing, nothing is
    /// prepared, and the character never becomes a target. A table entry with no bone, or a node no one draws or cuts
    /// with, is left out.
    /// </para>
    /// <para>
    /// **Lifetime.** The prepared cut, the cold preparation and the fixed-scale renderer are this component's and go back
    /// when it is destroyed -- after the world, whose cut borrowed them, has ended, or at once when nothing was cut. The
    /// character hierarchy itself is the cut's to withdraw at its publication; it is never destroyed here.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SandboxNpcCharacter : MonoBehaviour
    {
        [SerializeField] private CutWorldRoot world;
        [SerializeField] private SandboxSlashPropHit hit;

        [Tooltip("The character's root: the model, the motion body and the pose player are under it; a cut withdraws it.")]
        [SerializeField] private GameObject characterRoot;

        [SerializeField] private Rigidbody motionBody;
        [SerializeField] private TextAsset intake;
        [SerializeField] private TextAsset hulls;
        [SerializeField] private string family = "character-casual";

        private VpFixedScaleSkinCache _scaleCache;
        private VpFixedScaleSkinInput _fixedInput;
        private VpPhysicsColdPreparation _cold;
        private PoseTablePlayer _pose;

        /// <summary>The prepared cut, once made; null if it could not be.</summary>
        public VpPreparedCharacterCut Handle { get; private set; }

        /// <summary>The renderer the character is drawn and cut with: the fixed-scale copy sharing the model's bones.</summary>
        public SkinnedMeshRenderer Renderer => _fixedInput != null ? _fixedInput.Renderer : null;

        public GameObject CharacterRoot => characterRoot;

        /// <summary>The character's motion body.</summary>
        public Rigidbody MotionBody => motionBody;

        /// <summary>Whether its parts (not the whole root) are withdrawn at its first cut, and why not if not.</summary>
        public bool WithdrawsParts { get; private set; }

        public string WholeRootReason { get; private set; }

        /// <summary>The seconds the preparation's check of the parts took (once, before any cut).</summary>
        public double ConfirmSeconds { get; private set; }

        /// <summary>Whether the preparation has finished and the character is a hit target.</summary>
        public bool IsTarget { get; private set; }

        /// <summary>The frames the preparation took to finish, from Start.</summary>
        public int PreparationFrames { get; private set; }

        /// <summary>Why the character could not be prepared, or null.</summary>
        public string Failure { get; private set; }

        public int HullCount { get; private set; }

        private int _startFrame;

        private void Start()
        {
            _startFrame = Time.frameCount;
            try
            {
                Prepare();
            }
            catch (Exception error)
            {
                Failure = error.Message;
                Debug.LogException(error, this);
            }

            if (Failure != null)
            {
                Debug.LogError(name + ": the character was not prepared: " + Failure, this);
            }
        }

        private void Update()
        {
            if (IsTarget || Handle == null || Handle.IsDisposed || Failure != null || _pose == null || !_pose.BonesConfirmed)
            {
                return;
            }

            // The loading frames: the cold preparation confirms its deferred destruction without waiting.
            if (!Handle.TryFinishPreparation())
            {
                return;
            }

            if (hit == null || hit.Detector == null)
            {
                return;
            }

            hit.Detector.AddCharacter(Handle);
            IsTarget = true;
            PreparationFrames = Time.frameCount - _startFrame;
        }

        private void OnDestroy()
        {
            if (hit != null && hit.Detector != null && Handle != null)
            {
                hit.Detector.RemoveCharacter(Handle);
            }

            Handle?.Dispose();
            Handle = null;
            if (_cold != null)
            {
                _cold.TryFinish();
                _cold.Dispose();
                _cold = null;
            }

            _fixedInput?.Dispose();
            _fixedInput = null;
            _scaleCache?.Dispose();
            _scaleCache = null;
        }

        private unsafe void Prepare()
        {
            if (characterRoot == null || intake == null || hulls == null)
            {
                Failure = "needs the character root, the intake and the hulls";
                return;
            }

            IntakeEntry entry = JsonUtility.FromJson<Intake>(intake.text).assets.SingleOrDefault(e => e.family == family);
            HullFile fixture = JsonUtility.FromJson<HullFile>(hulls.text);
            if (entry == null || fixture == null || fixture.hulls == null || fixture.hulls.Length == 0)
            {
                Failure = "no intake entry or hulls for " + family;
                return;
            }

            SkinnedMeshRenderer original = characterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .SingleOrDefault(s => s.sharedMesh != null && s.sharedMesh.name == entry.objectName);
            if (original == null)
            {
                Failure = "no renderer " + entry.objectName;
                return;
            }

            // The bones the character is drawn and cut with must all be driven by its Pose Table.
            _pose = characterRoot.GetComponent<PoseTablePlayer>();
            if (_pose == null)
            {
                Failure = "no Pose Table player on the character root";
                return;
            }

            if (!_pose.TryRequireBones(original.bones, out string missing))
            {
                Failure = "the Pose Table does not drive the character's bones: " + missing;
                return;
            }

            if (world == null || !world.IsReady || motionBody == null)
            {
                Failure = "needs a built world and the character's motion body";
                return;
            }

            _scaleCache = new VpFixedScaleSkinCache();
            _fixedInput = _scaleCache.Prepare(original, characterRoot.transform);
            SkinnedMeshRenderer skin = _fixedInput.Renderer;
            Transform[] bones = skin.bones;
            Matrix4x4[] binds = skin.sharedMesh.bindposes;

            // The authored hulls, renderer-bind-local, into their bones' frames: one bank, one range per hull.
            var points = new List<float3>();
            var offsets = new List<int>();
            var indices = new List<int>();
            var faceEdges = new List<int>();
            var edges = new List<BrepEdge>();
            var ranges = new List<ConvexBrepRange>();
            var convexBones = new Transform[fixture.hulls.Length];
            for (int c = 0; c < fixture.hulls.Length; c++)
            {
                Hull h = fixture.hulls[c];
                int bone = Array.FindIndex(bones, b => b != null && b.name == h.boneName);
                if (bone < 0)
                {
                    Failure = "no bone " + h.boneName;
                    return;
                }

                convexBones[c] = bones[bone];
                BuildEdges(h.faceOffsets, h.faceIndices, out int[] localFaceEdges, out BrepEdge[] localEdges);
                int maxLoop = 0;
                for (int f = 0; f + 1 < h.faceOffsets.Length; f++) maxLoop = Math.Max(maxLoop, h.faceOffsets[f + 1] - h.faceOffsets[f]);
                ranges.Add(new ConvexBrepRange
                {
                    vertexBase = points.Count, vertexCount = h.rendererBindVertices.Length / 3,
                    faceBase = offsets.Count, faceCount = h.faceOffsets.Length - 1,
                    faceIndexBase = indices.Count, faceIndexCount = h.faceIndices.Length,
                    edgeBase = edges.Count, edgeCount = localEdges.Length, maxFaceLoop = maxLoop,
                });
                for (int v = 0; v < h.rendererBindVertices.Length / 3; v++)
                {
                    var p = new Vector3((float)h.rendererBindVertices[3 * v], (float)h.rendererBindVertices[3 * v + 1], (float)h.rendererBindVertices[3 * v + 2]);
                    points.Add(binds[bone].MultiplyPoint3x4(_fixedInput.PrepareBindPoint(p)));
                }

                offsets.AddRange(h.faceOffsets);
                indices.AddRange(h.faceIndices);
                faceEdges.AddRange(localFaceEdges);
                edges.AddRange(localEdges);
            }

            HullCount = ranges.Count;
            using var vp = new NativeArray<float3>(points.ToArray(), Allocator.Temp);
            using var fo = new NativeArray<int>(offsets.ToArray(), Allocator.Temp);
            using var fi = new NativeArray<int>(indices.ToArray(), Allocator.Temp);
            using var fe = new NativeArray<int>(faceEdges.ToArray(), Allocator.Temp);
            using var et = new NativeArray<BrepEdge>(edges.ToArray(), Allocator.Temp);
            var bank = new ConvexBrepBank
            {
                vertices = (float3*)vp.GetUnsafePtr(), faceOffsets = (int*)fo.GetUnsafePtr(), faceIndices = (int*)fi.GetUnsafePtr(),
                faceEdges = (int*)fe.GetUnsafePtr(), edges = (BrepEdge*)et.GetUnsafePtr(),
            };

            _cold = new VpPhysicsColdPreparation();
            if (!world.TryPrepareCharacterCut(skin, entry.topologyMap, entry.topologyCount, bank, ranges, convexBones, _cold,
                    characterRoot, motionBody, out VpPreparedCharacterCut handle))
            {
                Failure = "the world did not prepare the character's cut";
                return;
            }

            Handle = handle;

            // Its withdrawal at the first cut: the renderer, the Pose Table's update and the motion body, if the hierarchy
            // is confirmed to hold nothing else live; the whole root otherwise.
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            WithdrawsParts = handle.TryWithdrawParts(new Behaviour[] { _pose }, out string whyNot);
            ConfirmSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            WholeRootReason = whyNot;
        }

        private static void BuildEdges(int[] offsets, int[] indices, out int[] faceEdges, out BrepEdge[] edges)
        {
            var map = new Dictionary<long, int>();
            var table = new List<BrepEdge>();
            faceEdges = new int[indices.Length];
            for (int f = 0; f < offsets.Length - 1; f++)
            {
                for (int k = offsets[f]; k < offsets[f + 1]; k++)
                {
                    int a = indices[k];
                    int b = indices[k + 1 == offsets[f + 1] ? offsets[f] : k + 1];
                    int lo = Math.Min(a, b);
                    int hi = Math.Max(a, b);
                    long key = ((long)lo << 32) | (uint)hi;
                    if (!map.TryGetValue(key, out int at))
                    {
                        at = table.Count;
                        map.Add(key, at);
                        table.Add(new BrepEdge { v0 = lo, v1 = hi, f0 = -1, f1 = -1 });
                    }

                    BrepEdge e = table[at];
                    if (a == lo) e.f0 = f; else e.f1 = f;
                    table[at] = e;
                    faceEdges[k] = at;
                }
            }

            edges = table.ToArray();
        }

        [Serializable] private sealed class Intake { public IntakeEntry[] assets; }

        [Serializable]
        private sealed class IntakeEntry
        {
            public string family;
            public string objectName;
            public int[] topologyMap;
            public int topologyCount;
        }

        [Serializable] private sealed class HullFile { public Hull[] hulls; }

        [Serializable]
        private sealed class Hull
        {
            public string boneName;
            public double[] rendererBindVertices;
            public int[] faceOffsets;
            public int[] faceIndices;
        }
    }
}
