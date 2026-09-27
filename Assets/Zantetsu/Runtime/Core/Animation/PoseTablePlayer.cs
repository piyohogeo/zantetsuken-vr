using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace Zantetsu.Core.Animation
{
    /// <summary>
    /// Current playback of one <see cref="PoseTable"/> on one character (DESIGN 19.3, D-135): once per frame, in the
    /// update phase before anything reads the bones for a hit, it resolves the frame's target time to the Clip's Source
    /// Time and applies that pose to the bone Transforms. Normal drawing stays the character's SkinnedMeshRenderer,
    /// which draws the bones as they were left here.
    /// <para>
    /// **Time.** The target time is the frame's game time (<see cref="Time.timeAsDouble"/>) since
    /// <see cref="Restart"/>, read once per frame; the Source Time is what the table resolves it to. Neither is advanced
    /// anywhere else, and the evaluation itself advances nothing. The frame's (target, Source Time, Clip) are kept for
    /// whoever needs to say which pose a hit met.
    /// </para>
    /// <para>
    /// **Bones.** A table bone is a Transform at the same path under <see cref="ModelRoot"/>, resolved once when the
    /// table is bound; a table entry with no such Transform is not applied, and the count is kept. A table that cannot
    /// be read, or a time that does not evaluate, leaves the bones as they are -- no pose is made up.
    /// </para>
    /// <para>
    /// **Required bones.** Whether an entry without a Transform, or a Transform without an entry, matters is not this
    /// player's to say: the character that draws and cuts with the bones does. A player marked to require them
    /// (<see cref="RequiresBones"/>) plays nothing until <see cref="TryRequireBones"/> has confirmed that every bone its
    /// character names is one the table drives; if one is not, or the table was not bound, the table is let go and
    /// nothing is ever applied -- no partial pose. What is not named (an auxiliary node, an entry with no Transform) may
    /// be left out. The check is made once, when it is asked; it is not repeated each frame.
    /// </para>
    /// <para>
    /// It sits under the character's root: when a cut withdraws that root, this stops with it, so the pose the cut took
    /// is the last one applied.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class PoseTablePlayer : MonoBehaviour
    {
        private static readonly ProfilerMarker s_evaluate = new ProfilerMarker("Zantetsu.PoseTable.Evaluate");
        private static readonly ProfilerMarker s_apply = new ProfilerMarker("Zantetsu.PoseTable.Apply");
        private static readonly ProfilerMarker s_bind = new ProfilerMarker("Zantetsu.PoseTable.Bind");

        [SerializeField] private TextAsset table;
        [SerializeField] private Transform modelRoot;

        [Tooltip("Added to the target time before it is resolved: where in the Clip this character starts.")]
        [SerializeField] private double sourceTimeOffset;

        [Tooltip("Plays nothing until the character has confirmed, through TryRequireBones, that the table drives every bone it needs.")]
        [SerializeField] private bool requiresBones;

        // Set once when a bank with an identical ordered rig layout is installed.
        public FuncPoseSource PlanSource { get; set; }
        public delegate bool FuncPoseSource(double target, out PoseTable pose, out double source);
        public System.Collections.Generic.IReadOnlyList<PoseTable> TableBank { get; set; }
        private PoseTable _table;
        private Transform[] _bones;
        private Vector3[] _positions;
        private Quaternion[] _rotations;
        private double _startTime;
        private bool _started;
        private bool _bonesConfirmed;

        /// <summary>The table this plays, once bound; null when there is none or it could not be read.</summary>
        public PoseTable Table => _table;

        /// <summary>The table asset this was configured with (what two players playing the same table share).</summary>
        public TextAsset TableAsset => table;

        public Transform ModelRoot => modelRoot;

        /// <summary>Why the table could not be bound, or null.</summary>
        public string BindError { get; private set; }

        public int ResolvedBoneCount { get; private set; }

        public int UnresolvedBoneCount { get; private set; }

        /// <summary>The frame the pose was last applied in, and the target and Source Time it was evaluated at.</summary>
        public int AppliedFrame { get; private set; } = -1;

        public double AppliedTargetTime { get; private set; } = double.NaN;

        public double AppliedSourceTime { get; private set; } = double.NaN;

        /// <summary>The seconds the last bind took: the table read and the bones resolved, once, before any playback.</summary>
        public double BindSeconds { get; private set; }

        /// <summary>Whether this plays nothing until <see cref="TryRequireBones"/> has confirmed the bones.</summary>
        public bool RequiresBones => requiresBones;

        /// <summary>Whether <see cref="TryRequireBones"/> has confirmed the bones since the table was last bound.</summary>
        public bool BonesConfirmed => _bonesConfirmed;

        /// <summary>Sets what to play: the table bytes and the model root its bone paths are relative to.</summary>
        public void Configure(TextAsset poseTable, Transform root, double offset, bool requireBones = false)
        {
            table = poseTable;
            modelRoot = root;
            sourceTimeOffset = offset;
            requiresBones = requireBones;
            Bind();
        }

        /// <summary>
        /// Confirms that the bound table drives every one of <paramref name="required"/> -- the bones the character draws
        /// and cuts with. If the table was not bound, or a required bone is null or not driven by it, the table is let go
        /// (nothing is applied from then on), <see cref="BindError"/> names what was missing, and false is returned.
        /// </summary>
        public bool TryRequireBones(IReadOnlyList<Transform> required, out string missing)
        {
            _bonesConfirmed = false;
            if (_table == null)
            {
                missing = "the table is not bound: " + (BindError ?? "no table");
                return false;
            }

            var driven = new HashSet<Transform>();
            foreach (Transform bone in _bones)
            {
                if (bone != null)
                {
                    driven.Add(bone);
                }
            }

            int count = 0;
            var names = new StringBuilder();
            for (int i = 0; i < (required != null ? required.Count : 0); i++)
            {
                Transform bone = required[i];
                if (bone != null && driven.Contains(bone))
                {
                    continue;
                }

                if (count++ < 8)
                {
                    names.Append(count > 1 ? ", " : "").Append(bone != null ? bone.name : "(null at " + i + ")");
                }
            }

            if (required == null || count > 0)
            {
                missing = required == null ? "no required bones given" : count + " required bone(s) the table does not drive: " + names + (count > 8 ? ", ..." : "");
                BindError = missing;
                _table = null;
                return false;
            }

            missing = null;
            _bonesConfirmed = true;
            return true;
        }

        /// <summary>Starts the Clip over: this frame's game time becomes target time 0.</summary>
        public void Restart()
        {
            _startTime = Time.timeAsDouble;
            _started = true;
        }

        private void Awake()
        {
            Bind();
        }

        private void Bind()
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            using (s_bind.Auto())
            {
                BindTable();
            }

            BindSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
        }

        private void BindTable()
        {
            BindError = null;
            _table = null;
            _bonesConfirmed = false;
            if (table == null || modelRoot == null)
            {
                BindError = "no table or model root";
                return;
            }

            if (!PoseTable.TryRead(table.bytes, out PoseTable read, out string error))
            {
                BindError = error;
                return;
            }

            _bones = new Transform[read.BoneCount];
            int resolved = 0;
            for (int i = 0; i < read.BoneCount; i++)
            {
                _bones[i] = modelRoot.Find(read.BonePath(i));
                resolved += _bones[i] != null ? 1 : 0;
            }

            ResolvedBoneCount = resolved;
            UnresolvedBoneCount = read.BoneCount - resolved;
            _positions = new Vector3[read.BoneCount];
            _rotations = new Quaternion[read.BoneCount];
            _table = read;
        }

        /// <summary>
        /// Whether something else decides when this plays (a <see cref="PoseLodDirector"/>): while set, this does not
        /// update on its own, and the pose is applied only through <see cref="ApplyNow"/>. The time goes on either way.
        /// </summary>
        public bool Managed { get; set; }

        /// <summary>Whether a pose can be applied: a bound table, its bones confirmed if required, and this live.</summary>
        public bool IsPlaying => _table != null && (!requiresBones || _bonesConfirmed) && isActiveAndEnabled;

        /// <summary>The table bones' Transforms, by table index (null where the table's path has none).</summary>
        public int BoneCount => _bones != null ? _bones.Length : 0;

        public Transform BoneAt(int index) => _bones[index];

        /// <summary>The frame every table bone was last applied in (a whole pose, not a subset), or -1.</summary>
        public int FullPoseFrame { get; private set; } = -1;

        /// <summary>Whether the last application was a subset of the bones.</summary>
        public bool LastAppliedSubset { get; private set; }

        /// <summary>
        /// Applies this frame's pose now: the Source Time of the frame's target time, as the ordinary update does, to
        /// every table bone (<paramref name="bones"/> null) or only to the table bones in <paramref name="bones"/>[0..count)
        /// -- evaluated for those alone; the others keep what they had. False, applying nothing, when no pose can be.
        /// </summary>
        public bool ApplyNow(int[] bones, int count)
        {
            if (!IsPlaying)
            {
                return false;
            }

            if (!_started)
            {
                Restart();
            }

            double target = Time.timeAsDouble - _startTime;
            double source = _table.ResolveSourceTime(target + sourceTimeOffset);
            if (PlanSource != null)
            {
                target = Time.timeAsDouble;
                if (!PlanSource(target, out var planned, out source)) return false;
                _table = planned;
            }
            bool evaluated;
            using (s_evaluate.Auto())
            {
                evaluated = bones == null
                    ? _table.TryEvaluate(source, _positions, _rotations)
                    : _table.TryEvaluate(source, bones, count, _positions, _rotations);
            }

            if (!evaluated)
            {
                return false;
            }

            using (s_apply.Auto())
            {
                if (bones == null)
                {
                    for (int i = 0; i < _bones.Length; i++)
                    {
                        Transform bone = _bones[i];
                        if (bone != null)
                        {
                            bone.SetLocalPositionAndRotation(_positions[i], _rotations[i]);
                        }
                    }
                }
                else
                {
                    for (int k = 0; k < count; k++)
                    {
                        int i = bones[k];
                        Transform bone = _bones[i];
                        if (bone != null)
                        {
                            bone.SetLocalPositionAndRotation(_positions[i], _rotations[i]);
                        }
                    }
                }
            }

            AppliedFrame = Time.frameCount;
            AppliedTargetTime = target;
            AppliedSourceTime = source;
            LastAppliedSubset = bones != null;
            if (bones == null)
            {
                FullPoseFrame = Time.frameCount;
            }

            return true;
        }

        // Every table bone at one Source Time, recording nothing as applied: a preparation's sweep over the Clip (the
        // next ordinary application puts the frame's pose back).
        internal bool ApplySourceForPreparation(double sourceTime)
        {
            if (_table == null || !_table.TryEvaluate(sourceTime, _positions, _rotations))
            {
                return false;
            }

            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] != null)
                {
                    _bones[i].SetLocalPositionAndRotation(_positions[i], _rotations[i]);
                }
            }

            return true;
        }

        private void Update()
        {
            if (!Managed)
            {
                ApplyNow(null, 0);
            }
        }
    }
}
