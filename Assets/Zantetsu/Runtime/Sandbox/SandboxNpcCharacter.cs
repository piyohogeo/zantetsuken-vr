using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
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
    public sealed class SandboxNpcCharacter : MonoBehaviour, IMobPlanSlot
    {
        [SerializeField] private CutWorldRoot world;
        [SerializeField] private SandboxSlashPropHit hit;

        [Tooltip("The character's root: the model, the motion body and the pose player are under it; a cut withdraws it.")]
        [SerializeField] private GameObject characterRoot;

        [SerializeField] private Rigidbody motionBody;
        [SerializeField] private TextAsset intake;
        [SerializeField] private TextAsset hulls;
        [SerializeField] private string family = "character-casual";

        [Tooltip("The scene's bone level of detail; used only while it is enabled when the character is prepared.")]
        [SerializeField] private PoseLodDirector poseLod;

        [Tooltip("The wrist bones (hull bones): below them is left out from level 1, they themselves from level 2.")]
        [SerializeField] private string[] wristBones = { "DEF-hand.L", "DEF-hand.R" };

        [Tooltip("The ankle bones (hull bones): below them is left out from level 1, they themselves from level 3.")]
        [SerializeField] private string[] ankleBones = { "DEF-foot.L", "DEF-foot.R" };

        /// <summary>The character under the bone level of detail, when there is one.</summary>
        public PoseLodCharacter Lod { get; private set; }

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

        // ----- a prepared slot of a crowd (MobPlan) ---------------------------------------------------------------------
        // A slot is prepared once, before the scenario, and then kept dormant: not drawn, not posed, no motion body, no
        // hit target, no level of detail, no plan. The crowd activates it for an individual -- placed and posed first --
        // and, once that individual's cut no longer refers to anything of it, prepares a new cut for it again, so the same
        // objects can carry a new individual. Outside a crowd (PrepareAsSlot never called) nothing of this applies.

        /// <summary>What the slots of one crowd share: the parsed intake and hulls, and the fixed-scale mesh cache.</summary>
        public sealed class SlotShare
        {
            internal IntakeEntry entry;
            internal HullFile fixture;
            internal VpFixedScaleSkinCache scaleCache;
            internal int users;

            /// <summary>How many slots read the parsed intake and hulls from here rather than parsing them.</summary>
            public int SharedReads { get; internal set; }
        }

        private SlotShare _share;
        private bool Pooled => _share != null;

        /// <summary>Whether this dormant slot is prepared and may be activated.</summary>
        public bool IsPrepared { get; private set; }

        /// <summary>How many times this slot was activated for an individual, and prepared again after one.</summary>
        public int Activations { get; private set; }

        public int Reprepared { get; private set; }

        /// <summary>How many full preparations (Start) have run in this process: a crowd's slots prepare only before its scenario.</summary>
        public static int FullPreparations { get; private set; }

        // What a new cut of the same character is made from again: the hull bank and its bones, and what the level of
        // detail is registered with.
        private IntakeEntry _entry;
        private float3[] _bankPoints;
        private int[] _bankOffsets, _bankIndices, _bankFaceEdges;
        private BrepEdge[] _bankEdges;
        private List<ConvexBrepRange> _ranges;
        private Transform[] _convexBones;
        private List<(Transform bone, Bounds box)> _hitBoxes;
        private List<Transform> _lodReferences;
        private IReadOnlyList<Transform>[] _lodOmissions;
        private static readonly ProfilerMarker s_activate = new ProfilerMarker("Zantetsu.Npc.Activate");
        private static readonly ProfilerMarker s_slotDirect = new ProfilerMarker("Zantetsu.Npc.Prepare.SlotDirectSkin");

        // A slot's own direct skin input: made once for its renderer, mesh and topology, lent to each prepared cut the
        // slot makes, and disposed once, with the slot. Made again only when one of those has changed.
        private VpDirectSkinInput _direct;
        private int[] _directTopology;

        /// <summary>How many direct skin inputs this slot has made (one, unless its renderer, mesh or topology changed).</summary>
        public int DirectCreations { get; private set; }
        private static readonly ProfilerMarker s_reprepare = new ProfilerMarker("Zantetsu.Npc.Reprepare");

        /// <summary>
        /// Which crowd share this slot reads from: slots of one model (the same intake, hull fixture and family) share one;
        /// a crowd of several models keeps one share per key.
        /// </summary>
        public string SlotShareKey => family + "|" + (intake != null ? intake.name : "") + "|" + (hulls != null ? hulls.name : "");

        /// <summary>The character's model family in its intake (observation; the crowd's share key is built from it).</summary>
        public string Family => family;

        /// <summary>Makes this character a slot of a crowd. Before its Start only.</summary>
        public void PrepareAsSlot(SlotShare share)
        {
            if (_startFrame != 0 || Handle != null) throw new InvalidOperationException("a slot is made before it is prepared");
            _share = share ?? throw new ArgumentNullException(nameof(share));
        }

        private int _startFrame;

        // The preparation's main-thread stages, for measurement (a character added while playing prepares in its Start).
        private static readonly ProfilerMarker s_prepare = new ProfilerMarker("Zantetsu.Npc.Prepare");
        private static readonly ProfilerMarker s_prepareRead = new ProfilerMarker("Zantetsu.Npc.Prepare.Read");
        private static readonly ProfilerMarker s_prepareBones = new ProfilerMarker("Zantetsu.Npc.Prepare.Bones");
        private static readonly ProfilerMarker s_prepareScale = new ProfilerMarker("Zantetsu.Npc.Prepare.Scale");
        private static readonly ProfilerMarker s_prepareHulls = new ProfilerMarker("Zantetsu.Npc.Prepare.Hulls");
        private static readonly ProfilerMarker s_prepareCut = new ProfilerMarker("Zantetsu.Npc.Prepare.Cut");
        private static readonly ProfilerMarker s_prepareWithdraw = new ProfilerMarker("Zantetsu.Npc.Prepare.Withdraw");
        private static readonly ProfilerMarker s_prepareLod = new ProfilerMarker("Zantetsu.Npc.Prepare.Lod");
        private static readonly ProfilerMarker s_prepareFinish = new ProfilerMarker("Zantetsu.Npc.FinishPreparation");

        private void Start()
        {
            _startFrame = Time.frameCount;
            try
            {
                using (s_prepare.Auto())
                {
                    Prepare();
                }
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
            if (Pooled)
            {
                // A slot finishes its preparation on the loading frames and stays dormant until activated.
                if (IsPrepared || IsTarget || Handle == null || Handle.IsDisposed || Failure != null || _pose == null || !_pose.BonesConfirmed)
                {
                    return;
                }

                using (s_prepareFinish.Auto())
                {
                    if (!Handle.TryFinishPreparation()) return;
                }

                IsPrepared = true;
                if (PreparationFrames == 0) PreparationFrames = Time.frameCount - _startFrame;
                return;
            }

            if (IsTarget || Handle == null || Handle.IsDisposed || Failure != null || _pose == null || !_pose.BonesConfirmed)
            {
                return;
            }

            // The loading frames: the cold preparation confirms its deferred destruction without waiting.
            bool finished;
            using (s_prepareFinish.Auto())
            {
                finished = Handle.TryFinishPreparation();
            }

            if (!finished)
            {
                return;
            }

            if (hit == null || hit.Detector == null)
            {
                return;
            }

            // Under the needed-bones-only mode every needed bone is current every frame: the hit goes as before, with no
            // range test and no pose asked for.
            hit.Detector.AddCharacter(Handle, poseLod != null && poseLod.NeededOnly ? null : Lod);
            IsTarget = true;
            PreparationFrames = Time.frameCount - _startFrame;
        }

        /// <summary>
        /// Leaves the bone level of detail once its cut has withdrawn the character: its pose is no longer updated, so it
        /// no longer belongs among the characters the director schedules. Only the registration goes; the prepared cut,
        /// the pose player and the inputs stay the cut's and this component's, and go back as before. False, changing
        /// nothing, while the character is not withdrawn or has no registration.
        /// </summary>
        public bool LeaveLevelOfDetail()
        {
            if (poseLod == null || Lod == null || Handle == null || !Handle.IsWithdrawn)
            {
                return false;
            }

            poseLod.Unregister(Lod);
            Lod = null;
            return true;
        }

        private void OnDestroy()
        {
            if (poseLod != null && Lod != null)
            {
                poseLod.Unregister(Lod);
                Lod = null;
            }

            if (hit != null && hit.Detector != null && Handle != null)
            {
                hit.Detector.RemoveCharacter(Handle);
            }

            Handle?.Dispose();
            Handle = null;
            if (_direct != null)
            {
                // Once: now, or -- if a handle whose end was put off still borrows it -- when that handle gives it back.
                _direct.DisposeWhenReturned();
                _direct = null;
            }

            if (_cold != null)
            {
                _cold.TryFinish();
                _cold.Dispose();
                _cold = null;
            }

            _fixedInput?.Dispose();
            _fixedInput = null;
            if (Pooled)
            {
                // The shared cache goes with the last slot that used it.
                if (_scaleCache != null && --_share.users == 0)
                {
                    _share.scaleCache.Dispose();
                    _share.scaleCache = null;
                }
            }
            else
            {
                _scaleCache?.Dispose();
            }

            _scaleCache = null;
        }

        private unsafe void Prepare()
        {
            if (characterRoot == null || intake == null || hulls == null)
            {
                Failure = "needs the character root, the intake and the hulls";
                return;
            }

            FullPreparations++;
            IntakeEntry entry;
            HullFile fixture;
            using (s_prepareRead.Auto())
            {
                if (Pooled && _share.entry != null && _share.entry.family != family)
                {
                    // A share is one model's: another model's intake and hulls are never read for this character.
                    Failure = "the slot share holds " + _share.entry.family + ", not " + family;
                    return;
                }

                if (Pooled && _share.entry != null)
                {
                    entry = _share.entry;
                    fixture = _share.fixture;
                    _share.SharedReads++;
                }
                else
                {
                    entry = JsonUtility.FromJson<Intake>(intake.text).assets.SingleOrDefault(e => e.family == family);
                    fixture = JsonUtility.FromJson<HullFile>(hulls.text);
                    if (Pooled && entry != null && fixture != null)
                    {
                        _share.entry = entry;
                        _share.fixture = fixture;
                    }
                }
            }

            _entry = entry;

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

            var requiredBones = new HashSet<Transform>();
            var drivenBones = new HashSet<Transform>();
            for (int i = 0; i < _pose.BoneCount; i++) if (_pose.BoneAt(i) != null) drivenBones.Add(_pose.BoneAt(i));
            void Require(Transform t)
            {
                if (t == null || t == _pose.ModelRoot) return;
                requiredBones.Add(t);
                for (t = t.parent; t != null && t != _pose.ModelRoot; t = t.parent)
                    if (drivenBones.Contains(t)) requiredBones.Add(t);
            }
            bool driven;
            string missing;
            using (s_prepareBones.Auto())
            {
                // The renderer's bones once: each read of SkinnedMeshRenderer.bones copies the whole array.
                Transform[] originalBones = original.bones;
                using (var weights = original.sharedMesh.GetAllBoneWeights())
                    foreach (var weight in weights) if (weight.weight > 0) Require(originalBones[weight.boneIndex]);
                // A constant renderer/container need not be animated. If the table drives it, retain its channel.
                if (drivenBones.Contains(original.transform)) Require(original.transform);
                if (original.rootBone != null && drivenBones.Contains(original.rootBone)) Require(original.rootBone);
                foreach (var hull in fixture.hulls) Require(originalBones.First(b => b != null && b.name == hull.boneName));
                driven = _pose.TryRequireBones(requiredBones.ToArray(), out missing);
            }

            if (!driven)
            {
                Failure = "the Pose Table does not drive the character's bones: " + missing;
                return;
            }

            if (world == null || !world.IsReady || motionBody == null)
            {
                Failure = "needs a built world and the character's motion body";
                return;
            }

            if (Pooled)
            {
                // One fixed-scale mesh for every slot of the crowd.
                _share.scaleCache ??= new VpFixedScaleSkinCache();
                _share.users++;
                _scaleCache = _share.scaleCache;
            }
            else
            {
                _scaleCache = new VpFixedScaleSkinCache();
            }

            using (s_prepareScale.Auto())
            {
                _fixedInput = _scaleCache.Prepare(original, characterRoot.transform);
            }

            s_prepareHulls.Begin();
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
            var hitBoxes = new List<(Transform bone, Bounds box)>(fixture.hulls.Length);
            for (int c = 0; c < fixture.hulls.Length; c++)
            {
                Hull h = fixture.hulls[c];
                int bone = Array.FindIndex(bones, b => b != null && b.name == h.boneName);
                if (bone < 0)
                {
                    s_prepareHulls.End();
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
                var box = new Bounds();
                for (int v = 0; v < h.rendererBindVertices.Length / 3; v++)
                {
                    var p = new Vector3((float)h.rendererBindVertices[3 * v], (float)h.rendererBindVertices[3 * v + 1], (float)h.rendererBindVertices[3 * v + 2]);
                    Vector3 local = binds[bone].MultiplyPoint3x4(_fixedInput.PrepareBindPoint(p));
                    points.Add(local);
                    if (v == 0) box = new Bounds(local, Vector3.zero); else box.Encapsulate(local);
                }

                hitBoxes.Add((bones[bone], box));

                offsets.AddRange(h.faceOffsets);
                indices.AddRange(h.faceIndices);
                faceEdges.AddRange(localFaceEdges);
                edges.AddRange(localEdges);
            }

            HullCount = ranges.Count;
            _bankPoints = points.ToArray();
            _bankOffsets = offsets.ToArray();
            _bankIndices = indices.ToArray();
            _bankFaceEdges = faceEdges.ToArray();
            _bankEdges = edges.ToArray();
            _ranges = ranges;
            _convexBones = convexBones;
            _hitBoxes = hitBoxes;
            s_prepareHulls.End();
            if (!CreateHandle())
            {
                return;
            }

            // The bone level of detail, when the scene has one enabled: the bone sets by level, the range for a hit.
            if (poseLod != null && poseLod.isActiveAndEnabled)
            {
                using ProfilerMarker.AutoScope lodScope = s_prepareLod.Auto();
                // What the character is drawn and placed with: the bones with a skin weight (not every bone the renderer
                // lists), and both renderers' own Transforms and root bones.
                var references = new List<Transform>();
                PoseLodDirector.CollectWeightedBones(original, references);
                foreach (SkinnedMeshRenderer r in new[] { original, skin })
                {
                    references.Add(r.transform);
                    if (r.rootBone != null) references.Add(r.rootBone);
                }

                _lodReferences = references;
                _lodOmissions = LodOmissions(bones);
                if (Pooled)
                {
                    Dormant();
                    return;
                }

                Lod = poseLod.Register(_pose, references, _lodOmissions, hitBoxes, out string refused);
                if (Lod == null)
                {
                    Failure = "the bone level of detail did not take the character: " + refused;
                }
                else
                {
                    Debug.Log("POSE LOD: " + characterRoot.name + " table bones " + _pose.BoneCount + ", applied by level "
                        + Lod.AppliedBoneCount(0) + "/" + Lod.AppliedBoneCount(1) + "/" + Lod.AppliedBoneCount(2) + "/" + Lod.AppliedBoneCount(3)
                        + ", hull bones left out by level 0/" + Lod.OmittedHitBoneCount(1) + "/" + Lod.OmittedHitBoneCount(2) + "/" + Lod.OmittedHitBoneCount(3)
                        + ", range (root frame) centre " + Lod.RangeBounds.center.ToString("F3") + " size " + Lod.RangeBounds.size.ToString("F3")
                        + ", widened between samples by up to " + Lod.LargestBetweenSamples.ToString("F3") + " m"
                        + ", registration " + (Lod.RegisterSeconds * 1000.0).ToString("F2") + " ms (range " + (Lod.RangeSeconds * 1000.0).ToString("F2") + " ms)"
                        + (Lod.SharedPlan ? ", plan shared" : ", plan made")
                        + ", phase " + Lod.Phase);
                }
            }

            if (Pooled)
            {
                Dormant();
            }
        }

        // A new prepared cut of this character from the kept bank: the handle, and its withdrawal of the parts.
        private unsafe bool CreateHandle()
        {
            using var vp = new NativeArray<float3>(_bankPoints, Allocator.Temp);
            using var fo = new NativeArray<int>(_bankOffsets, Allocator.Temp);
            using var fi = new NativeArray<int>(_bankIndices, Allocator.Temp);
            using var fe = new NativeArray<int>(_bankFaceEdges, Allocator.Temp);
            using var et = new NativeArray<BrepEdge>(_bankEdges, Allocator.Temp);
            var bank = new ConvexBrepBank
            {
                vertices = (float3*)vp.GetUnsafePtr(), faceOffsets = (int*)fo.GetUnsafePtr(), faceIndices = (int*)fi.GetUnsafePtr(),
                faceEdges = (int*)fe.GetUnsafePtr(), edges = (BrepEdge*)et.GetUnsafePtr(),
            };

            VpDirectSkinInput lent = null;
            if (Pooled)
            {
                SkinnedMeshRenderer skin = _fixedInput.Renderer;
                if (_direct != null && (_direct.IsDisposed || !ReferenceEquals(_direct.SourceRenderer, skin) || _direct.SourceMesh != skin.sharedMesh
                    || !ReferenceEquals(_directTopology, _entry.topologyMap) || _direct.TopologyCount != _entry.topologyCount))
                {
                    // The renderer, its mesh or the topology changed: the input is made again, as the cold contract asks --
                    // but not while the old one is still lent: then no new cut is prepared, and the old input stays owned.
                    if (_direct.Borrower != null)
                    {
                        Failure = "the slot's direct skin input changed while it was still lent";
                        return false;
                    }

                    if (!_direct.IsDisposed) _direct.Dispose();
                    _direct = null;
                }

                if (_direct == null)
                {
                    bool made;
                    using (s_slotDirect.Auto())
                    {
                        made = VpDirectSkinInput.TryCreate(skin, _entry.topologyMap, _entry.topologyCount, world.CutInputConnectivity, out _direct);
                    }

                    if (!made)
                    {
                        Failure = "the slot's direct skin input could not be made";
                        return false;
                    }

                    _directTopology = _entry.topologyMap;
                    DirectCreations++;
                }

                lent = _direct;
            }

            _cold = new VpPhysicsColdPreparation();
            bool prepared;
            VpPreparedCharacterCut handle;
            using (s_prepareCut.Auto())
            {
                prepared = world.TryPrepareCharacterCut(_fixedInput.Renderer, _entry.topologyMap, _entry.topologyCount, bank, _ranges, _convexBones,
                    _cold, lent, characterRoot, motionBody, out handle);
            }

            if (!prepared)
            {
                Failure = "the world did not prepare the character's cut";
                return false;
            }

            Handle = handle;

            // Its withdrawal at the first cut: the renderer, the Pose Table's update and the motion body, if the hierarchy
            // is confirmed to hold nothing else live; the whole root otherwise.
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            string whyNot;
            using (s_prepareWithdraw.Auto())
            {
                WithdrawsParts = handle.TryWithdrawParts(new Behaviour[] { _pose }, out whyNot);
            }

            ConfirmSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            WholeRootReason = whyNot;
            return true;
        }

        // Dormant: not drawn, not posed, no motion body. A hit target it is not, and nothing registers it.
        private void Dormant()
        {
            if (_fixedInput != null && _fixedInput.Renderer != null) _fixedInput.Renderer.enabled = false;
            if (_pose != null) _pose.enabled = false;
            if (motionBody != null) motionBody.gameObject.SetActive(false);
        }

        /// <summary>
        /// Activates a prepared dormant slot for a new individual. The caller has placed the character root where the
        /// individual stands; here its current pose is applied, and only then is it drawn, given its level of detail and
        /// made a hit target. Its plan is the caller's to register after this. False, changing nothing, unless prepared.
        /// </summary>
        public bool Activate()
        {
            if (!Pooled || !IsPrepared || IsTarget || Failure != null || Handle == null || !Handle.IsReady)
            {
                return false;
            }

            using (s_activate.Auto())
            {
                if (motionBody != null) motionBody.gameObject.SetActive(true);
                _pose.enabled = true;
                _pose.ApplyNow(null, 0);
                _fixedInput.Renderer.enabled = true;
                if (poseLod != null && poseLod.isActiveAndEnabled && _lodReferences != null)
                {
                    Lod = poseLod.Register(_pose, _lodReferences, _lodOmissions, _hitBoxes, out string refused);
                    if (Lod == null) Debug.LogWarning(characterRoot.name + ": the bone level of detail did not take the slot: " + refused, this);
                }

                if (hit != null && hit.Detector != null)
                {
                    hit.Detector.AddCharacter(Handle, poseLod != null && poseLod.NeededOnly ? null : Lod);
                }

                IsPrepared = false;
                IsTarget = true;
                Activations++;
            }

            return true;
        }

        /// <summary>
        /// Whether the individual this slot carried has let go of it: its prepared cut was used and has ended (the handle
        /// is disposed, so nothing is held or pending on it), the character was withdrawn at that cut's publication, and
        /// the cut is published and its geometry committed. Only then is anything of the slot prepared again.
        /// </summary>
        public bool IsReturnReady
        {
            get
            {
                if (!Pooled || Handle == null || !Handle.IsDisposed || !Handle.IsWithdrawn || !Handle.Operation.IsSet || world == null) return false;
                if (_direct != null && _direct.Borrower != null) return false;   // its input not yet given back
                if (!world.Ledger.TryGetOperation(Handle.Operation, out LogicalCutOperation operation) || operation.state == LogicalCutOperationState.Admitted)
                {
                    return false;
                }

                // Committed: the cut is published and its geometry committed. Or the cut failed and was aborted (DESIGN
                // 7.1.1): the ledger ended it, the geometry was reclaimed rather than committed, and the driver holds no
                // record of it any more (its work has come back). A reclaimed stage alone is not enough.
                CutGeometryStage stage = world.Geometry.StageOf(Handle.Operation);
                return stage == CutGeometryStage.Committed
                    || (operation.state == LogicalCutOperationState.Aborted && stage == CutGeometryStage.Reclaimed && world.Driver.IsSettled(Handle.Operation));
            }
        }

        /// <summary>
        /// Prepares a returned slot again for a new individual: the ended cut leaves the hit detector, its cold
        /// preparation goes back, and a new prepared cut is made from the kept bank -- a new fragment on its first hit,
        /// nothing of the old lineage, plan or hit consumption. The slot is dormant until that preparation finishes.
        /// False, changing nothing, unless <see cref="IsReturnReady"/>.
        /// </summary>
        public bool TryReprepare()
        {
            if (!IsReturnReady)
            {
                return false;
            }

            using (s_reprepare.Auto())
            {
                if (hit != null && hit.Detector != null) hit.Detector.RemoveCharacter(Handle);
                if (_cold != null)
                {
                    _cold.TryFinish();
                    _cold.Dispose();
                    _cold = null;
                }

                Handle = null;
                IsTarget = false;
                IsPrepared = false;
                if (!characterRoot.activeSelf) characterRoot.SetActive(true);
                Dormant();
                if (!CreateHandle())
                {
                    return false;
                }

                Dormant();
                Reprepared++;
            }

            return true;
        }

        // What each level leaves out, from the named wrist and ankle bones (hull bones of this character): below both
        // from level 1 (fingers, toes), the wrists from level 2, the ankles from level 3. The helpers nothing draws are
        // the director's to find; nothing else is named.
        private IReadOnlyList<Transform>[] LodOmissions(Transform[] bones)
        {
            var level1 = new List<Transform>();
            var level2 = new List<Transform>();
            var level3 = new List<Transform>();
            foreach (Transform bone in bones)
            {
                if (bone == null) continue;
                bool wrist = Array.IndexOf(wristBones, bone.name) >= 0;
                bool ankle = Array.IndexOf(ankleBones, bone.name) >= 0;
                if (!wrist && !ankle) continue;
                foreach (Transform below in bone.GetComponentsInChildren<Transform>(true))
                {
                    if (below != bone) level1.Add(below);
                }

                (wrist ? level2 : level3).Add(bone);
            }

            return new IReadOnlyList<Transform>[] { level1, level2, level3 };
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

        [Serializable] internal sealed class Intake { public IntakeEntry[] assets; }

        [Serializable]
        internal sealed class IntakeEntry
        {
            public string family;
            public string objectName;
            public int[] topologyMap;
            public int topologyCount;
        }

        [Serializable] internal sealed class HullFile { public Hull[] hulls; }

        [Serializable]
        internal sealed class Hull
        {
            public string boneName;
            public double[] rendererBindVertices;
            public int[] faceOffsets;
            public int[] faceIndices;
        }
    }
}
