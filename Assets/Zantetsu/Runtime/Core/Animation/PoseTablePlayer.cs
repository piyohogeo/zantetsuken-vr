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

        private PoseTable _table;
        private Transform[] _bones;
        private Vector3[] _positions;
        private Quaternion[] _rotations;
        private double _startTime;
        private bool _started;
        private bool _bonesConfirmed;

        /// <summary>The table this plays, once bound; null when there is none or it could not be read.</summary>
        public PoseTable Table => _table;

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

        private void Update()
        {
            if (_table == null || (requiresBones && !_bonesConfirmed))
            {
                return;
            }

            if (!_started)
            {
                Restart();
            }

            double target = Time.timeAsDouble - _startTime;
            double source = _table.ResolveSourceTime(target + sourceTimeOffset);
            bool evaluated;
            using (s_evaluate.Auto())
            {
                evaluated = _table.TryEvaluate(source, _positions, _rotations);
            }

            if (!evaluated)
            {
                return;
            }

            using (s_apply.Auto())
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

            AppliedFrame = Time.frameCount;
            AppliedTargetTime = target;
            AppliedSourceTime = source;
        }
    }
}
