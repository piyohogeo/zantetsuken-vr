using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.XR;

namespace Zantetsu.Core.Animation
{
    /// <summary>The tuning of <see cref="PoseLodDirector"/>. Every value is an adjustment, not a rule.</summary>
    [Serializable]
    public struct PoseLodSettings
    {
        [Tooltip("Degrees outside both eyes' view at which the view alone gives the strongest level.")]
        public float viewFullAngleDegrees;

        [Tooltip("Metres (to the nearest point of the character's range) within which the distance asks for nothing.")]
        public float nearMetres;

        [Tooltip("Metres at which the distance alone gives the strongest level.")]
        public float farMetres;

        [Tooltip("The update rate of level 2, in Hz (every n frames at the display rate).")]
        public float level2RateHz;

        [Tooltip("The update rate of level 3, in Hz.")]
        public float level3RateHz;

        [Tooltip("Metres added around the range for rounding only; what the pose does between samples is bounded apart.")]
        public float rangeMargin;

        public static PoseLodSettings Default => new PoseLodSettings
        {
            viewFullAngleDegrees = 25f,
            nearMetres = 5f,
            farMetres = 25f,
            level2RateHz = 18f,
            level3RateHz = 9f,
            rangeMargin = 0.01f,
        };
    }

    /// <summary>
    /// One character under <see cref="PoseLodDirector"/>: its bone sets per level, made once at registration, its range
    /// for a hit (<see cref="IPoseOnDemand"/>), and where its updates stand.
    /// </summary>
    /// <summary>
    /// A character's plan made ahead of its registration (<see cref="PoseLodDirector.Prepare"/>): its bone sets by level
    /// and its range, for one player and the table bank it was made with. Read only.
    /// </summary>
    public sealed class PoseLodPlan
    {
        internal PoseLodPlan(PoseTablePlayer player, object tableBank, int[][] levelBones, Bounds range, float largest, int[] omittedHit,
            bool shared, double rangeSeconds, double prepareSeconds)
        {
            Player = player;
            TableBank = tableBank;
            LevelBones = levelBones;
            Range = range;
            Largest = largest;
            OmittedHit = omittedHit;
            Shared = shared;
            RangeSeconds = rangeSeconds;
            PrepareSeconds = prepareSeconds;
        }

        public PoseTablePlayer Player { get; }
        internal object TableBank { get; }
        internal int[][] LevelBones { get; }
        internal Bounds Range { get; }
        internal float Largest { get; }
        internal int[] OmittedHit { get; }

        /// <summary>Whether the plan was one already made for the same inputs.</summary>
        public bool Shared { get; }

        public double RangeSeconds { get; }

        /// <summary>How long the preparation took (the key, and the plan when it was not shared).</summary>
        public double PrepareSeconds { get; }
    }

    public sealed class PoseLodCharacter : IPoseOnDemand
    {
        private static readonly ProfilerMarker s_force = new ProfilerMarker("Zantetsu.PoseLod.Force");

        internal PoseLodCharacter(PoseTablePlayer player, int phase, int[][] levelBones, Bounds range, int[] omittedHitBones)
        {
            Player = player;
            Phase = phase;
            LevelBones = levelBones;
            RangeBounds = range;
            _omittedHitBones = omittedHitBones;
        }

        private readonly int[] _omittedHitBones;

        public PoseTablePlayer Player { get; }

        /// <summary>This character's offset in the update cycle, so that characters of one level update on different frames.</summary>
        public int Phase { get; }

        // The table bones applied at each level: level 0 is the needed set (the whole pose as far as anything reads it),
        // each higher level fewer. A table bone nothing needs is in none of them.
        internal int[][] LevelBones { get; }

        public Transform Root => Player != null ? Player.transform : null;

        public Bounds RangeBounds { get; }

        public bool IsLive => Player != null && Player.IsPlaying;

        /// <summary>The level of the last decision (0 = every frame, every needed bone .. 3 = the fewest).</summary>
        public int Level { get; internal set; }

        /// <summary>A level to use instead of the decided one, or -1. For tests and diagnostics.</summary>
        public int LevelOverride { get; set; } = -1;

        /// <summary>The frame of the last scheduled application (not counting a forced one), or -1.</summary>
        public int LastUpdateFrame { get; internal set; } = -1;

        /// <summary>The frame every needed bone was last put at the frame's pose (level 0, scheduled or forced), or -1.</summary>
        public int FullPoseFrame { get; internal set; } = -1;

        /// <summary>How many times a hit asked for the whole current pose and it was applied.</summary>
        public int ForcedCount { get; private set; }

        /// <summary>The table bones applied at <paramref name="level"/> (at level 0, the needed set).</summary>
        public int AppliedBoneCount(int level) => LevelBones[level].Length;

        /// <summary>The bones a hit shape is placed by that <paramref name="level"/> leaves out (read only after a whole pose).</summary>
        public int OmittedHitBoneCount(int level) => _omittedHitBones[level];

        /// <summary>What the registration took: the whole of it, and the range's part of it (seconds).</summary>
        public double RegisterSeconds { get; internal set; }

        public double RangeSeconds { get; internal set; }

        /// <summary>The largest distance the range was widened by, between two samples, for what the pose does between them.</summary>
        public float LargestBetweenSamples { get; internal set; }

        /// <summary>Whether the bone sets and the range came from an earlier character with the same inputs.</summary>
        public bool SharedPlan { get; internal set; }

        internal bool Due; // set by the decision pass, read by the application pass

        internal bool Apply(int level)
        {
            int[] bones = LevelBones[level];
            if (!Player.ApplyNow(bones, bones.Length))
            {
                return false;
            }

            if (level == 0)
            {
                FullPoseFrame = Time.frameCount;
            }

            return true;
        }

        public bool EnsureCurrentFullPose()
        {
            if (Player == null || !Player.IsPlaying || FullPoseFrame == Time.frameCount)
            {
                return false;
            }

            using (s_force.Auto())
            {
                if (!Apply(0))
                {
                    return false;
                }
            }

            ForcedCount++;
            return true;
        }
    }

    /// <summary>
    /// A level of detail for Pose-Table characters' bones, from the view and the distance in one decision: each gives a
    /// strength (0 none .. 3 strongest) and the stronger is used -- never the two stacked below the lowest rate.
    /// <list type="bullet">
    /// <item>**View**: the view alone asks for nothing while any part of the character's range is inside either eye's
    /// view (the distance may still ask for a level); outside both, stronger the farther it is outside, the strongest from
    /// <see cref="PoseLodSettings.viewFullAngleDegrees"/>. Not the angle from the centre of the screen: the eyes' own
    /// frusta and the character's range (a sphere) are what is compared.</item>
    /// <item>**Distance**: nothing within <see cref="PoseLodSettings.nearMetres"/> of the nearest point of the range,
    /// then in steps to the strongest at <see cref="PoseLodSettings.farMetres"/>, whether in view or not.</item>
    /// </list>
    /// A level sets how often the bones are updated -- every frame, every second frame, about
    /// <see cref="PoseLodSettings.level2RateHz"/>, about <see cref="PoseLodSettings.level3RateHz"/> (in frames of the
    /// display rate) -- and which bones. Characters of one level update on frames spread over the cycle, not together.
    /// The time goes on while a character is not updated, and an update always applies the pose of the frame's time; a
    /// level that drops applies at once.
    /// <para>
    /// **Needed bones.** A table bone that nothing reads is never evaluated or applied, at any level: level 0 -- and the
    /// whole pose a hit asks for -- is the needed set, the bones the caller says the character is drawn and placed with
    /// (the bones with a skin weight, the renderer's own Transform and root bone), its hit shapes' bones, what else hangs
    /// under the model, and their ancestors. Higher levels leave out more of it (the caller names what, by level).
    /// </para>
    /// <para>
    /// **A hit reads the current pose.** A registered character is also an <see cref="IPoseOnDemand"/>: a hit that may
    /// meet it asks for its whole current pose first, whatever its level (<see cref="PoseLodCharacter.EnsureCurrentFullPose"/>).
    /// Its range holds the hit shape at every time of the Clip, between samples too (see <see cref="Register"/>).
    /// </para>
    /// <para>
    /// Only the bones: the root's placement, a motion body and anything after a cut are not this director's.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class PoseLodDirector : MonoBehaviour
    {
        private static readonly ProfilerMarker s_decide = new ProfilerMarker("Zantetsu.PoseLod.Decide");

        public const int Levels = 4;

        [SerializeField] private PoseLodSettings settings = PoseLodSettings.Default;

        [Tooltip("The view the levels are decided for; the main camera when none is given.")]
        [SerializeField] private Camera viewCamera;

        [Tooltip("The display rate in Hz the update intervals are counted in; 0 reads the XR display's (or 72 without one).")]
        [SerializeField] private float displayRateHz;

        private readonly List<PoseLodCharacter> _characters = new List<PoseLodCharacter>(32);

        /// <summary>How many characters are registered now.</summary>
        public int CharacterCount => _characters.Count;
        private readonly Plane[][] _eyePlanes = { new Plane[6], new Plane[6] };
        private readonly Vector3[] _eyePositions = new Vector3[2];
        private readonly List<XRDisplaySubsystem> _displays = new List<XRDisplaySubsystem>(1);
        private readonly Dictionary<string, Plan> _plans = new Dictionary<string, Plan>();
        private int _eyes;
        private float _rate;

        /// <summary>Whether characters with the same inputs share one plan (bone sets, range) made for the first of them.</summary>
        public bool ShareSameInputs { get; set; } = true;

        /// <summary>
        /// Every character at level 0 every frame: only the needed bones, with no decision by view or distance. (What a
        /// caller compares against: the needed-bone saving without the level of detail.)
        /// </summary>
        public bool NeededOnly { get; set; }

        public PoseLodSettings Settings
        {
            get => settings;
            set => settings = value;
        }

        public Camera ViewCamera
        {
            get => viewCamera;
            set => viewCamera = value;
        }

        public float DisplayRateHz
        {
            get => displayRateHz;
            set => displayRateHz = value;
        }

        public IReadOnlyList<PoseLodCharacter> Characters => _characters;

        /// <summary>The characters whose bones were updated (by the schedule) in the last update.</summary>
        public int UpdatedLastFrame { get; private set; }

        /// <summary>The frames between updates at <paramref name="level"/>, at the display rate in use.</summary>
        public int IntervalOf(int level)
        {
            float rate = _rate > 0f ? _rate : ResolveRate();
            switch (level)
            {
                case 0: return 1;
                case 1: return 2;
                case 2: return Math.Max(1, (int)Math.Round(rate / settings.level2RateHz));
                default: return Math.Max(1, (int)Math.Round(rate / settings.level3RateHz));
            }
        }

        /// <summary>
        /// The bones of <paramref name="renderer"/> that some vertex is skinned to with a non-zero weight -- not every
        /// bone the renderer lists -- added to <paramref name="into"/>. Read once, from the mesh's weights (the mesh must
        /// be readable).
        /// </summary>
        public static void CollectWeightedBones(SkinnedMeshRenderer renderer, List<Transform> into)
        {
            if (renderer == null || renderer.sharedMesh == null)
            {
                return;
            }

            Transform[] bones = renderer.bones;
            var weighted = new bool[bones.Length];
            var weights = renderer.sharedMesh.GetAllBoneWeights();
            for (int k = 0; k < weights.Length; k++)
            {
                if (weights[k].weight > 0f && (uint)weights[k].boneIndex < (uint)bones.Length)
                {
                    weighted[weights[k].boneIndex] = true;
                }
            }

            for (int i = 0; i < bones.Length; i++)
            {
                if (weighted[i] && bones[i] != null)
                {
                    into.Add(bones[i]);
                }
            }
        }

        /// <summary>
        /// Takes <paramref name="player"/> under this director (it stops updating on its own) and returns its handle, or
        /// null with the reason when it cannot be: a player with no bound table, or no hit boxes to make its range from.
        /// <para>
        /// **Bone sets.** The needed set is <paramref name="references"/> (what the character is drawn and placed with:
        /// its weighted bones, its renderer's Transform and root bone), the bones of <paramref name="hitBoxes"/>, anything
        /// else attached under the model (a node that is no table bone and holds a component), and every ancestor of
        /// these; it is level 0 and the whole pose a hit asks for. The bones named in <paramref name="omitFrom"/>[k] are
        /// left out from level k+1 on; whatever is left out, an ancestor of a bone applied at that level is applied too.
        /// Nothing here reads a name.
        /// </para>
        /// <para>
        /// **Range.** Each of <paramref name="hitBoxes"/> (a box in its bone's frame) is taken into the root's frame at
        /// every sample of the Clip, and each box is widened, for the stretch to the next sample, by how far the pose can
        /// move it in between: for every table bone on its chain, the angle that bone turns between the two samples times
        /// the most the box's corners can lie from it (the chain's link lengths, each at its longest in the table, plus the
        /// box's own reach), and the distance its local position moves. No pose between samples is evaluated.
        /// </para>
        /// </summary>
        /// <summary>
        /// Registers a character: <see cref="Prepare"/> and then <see cref="Register(PoseLodPlan)"/> in one call.
        /// </summary>
        public PoseLodCharacter Register(
            PoseTablePlayer player, IReadOnlyList<Transform> references, IReadOnlyList<Transform>[] omitFrom,
            IReadOnlyList<(Transform bone, Bounds box)> hitBoxes, out string refused)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            PoseLodPlan plan = Prepare(player, references, omitFrom, hitBoxes, out refused);
            if (plan == null) return null;
            PoseLodCharacter character = Register(plan);
            if (character != null) character.RegisterSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            return character;
        }

        /// <summary>
        /// A character's plan -- its bone sets by level and its range -- made (or taken from the plans already made for
        /// the same inputs) without registering it: the costly part of a registration, to be done ahead of time for a
        /// character registered later (a crowd's slot before its scenario). The plan holds for this player while its
        /// table bank is the one it was made with.
        /// </summary>
        public PoseLodPlan Prepare(
            PoseTablePlayer player, IReadOnlyList<Transform> references, IReadOnlyList<Transform>[] omitFrom,
            IReadOnlyList<(Transform bone, Bounds box)> hitBoxes, out string refused)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            refused = null;
            if (player == null || player.Table == null || player.BoneCount == 0)
            {
                refused = "no player with a bound table";
                return null;
            }

            if (hitBoxes == null || hitBoxes.Count == 0)
            {
                refused = "no hit boxes to make the range from";
                return null;
            }

            int n = player.BoneCount;
            var index = new Dictionary<Transform, int>(n);
            for (int i = 0; i < n; i++)
            {
                if (player.BoneAt(i) != null && !index.ContainsKey(player.BoneAt(i)))
                {
                    index.Add(player.BoneAt(i), i);
                }
            }

            // The needed set: the references, the hit bones, what hangs under the model, and all their ancestors.
            var needed = new bool[n];
            Transform top = player.ModelRoot != null ? player.ModelRoot.parent : null;
            void NeedWithAncestors(Transform t)
            {
                for (; t != null && t != top; t = t.parent)
                {
                    if (index.TryGetValue(t, out int i)) needed[i] = true;
                }
            }

            if (references != null)
            {
                foreach (Transform t in references) NeedWithAncestors(t);
            }

            foreach ((Transform bone, Bounds _) in hitBoxes) NeedWithAncestors(bone);
            foreach (Component component in player.ModelRoot.GetComponentsInChildren<Component>(true))
            {
                if (!(component is Transform) && !index.ContainsKey(component.transform))
                {
                    NeedWithAncestors(component.transform.parent);
                }
            }

            // The same inputs give the same plan (bone sets, range): reuse one made for an earlier character.
            string key = ShareSameInputs ? PlanKey(player, index, needed, omitFrom, hitBoxes) : null;
            if (key != null && _plans.TryGetValue(key, out Plan shared))
            {
                return new PoseLodPlan(player, player.TableBank, shared.levelBones, shared.range, shared.largest, shared.omittedHit, true, 0.0,
                    (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency);
            }

            var levelBones = new int[Levels][];
            var omittedHit = new int[Levels];
            var omitted = new bool[n];
            for (int level = 0; level < Levels; level++)
            {
                if (level > 0 && omitFrom != null && level - 1 < omitFrom.Length && omitFrom[level - 1] != null)
                {
                    foreach (Transform bone in omitFrom[level - 1])
                    {
                        if (bone != null && index.TryGetValue(bone, out int i)) omitted[i] = true;
                    }
                }

                var applied = new bool[n];
                for (int i = 0; i < n; i++) applied[i] = needed[i] && !omitted[i];

                // An ancestor of an applied bone is applied.
                for (int i = 0; i < n; i++)
                {
                    if (!applied[i]) continue;
                    for (Transform t = player.BoneAt(i).parent; t != null; t = t.parent)
                    {
                        if (index.TryGetValue(t, out int p)) applied[p] = true;
                    }
                }

                var list = new List<int>(n);
                for (int i = 0; i < n; i++) if (applied[i] && player.BoneAt(i) != null) list.Add(i);
                levelBones[level] = list.ToArray();
                foreach ((Transform bone, Bounds _) in hitBoxes)
                {
                    if (bone != null && index.TryGetValue(bone, out int i) && !applied[i]) omittedHit[level]++;
                }
            }

            long rangeBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            Bounds range = RangeOf(player, index, hitBoxes, settings.rangeMargin, out float largest);
            double rangeSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - rangeBegan) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (key != null)
            {
                _plans[key] = new Plan { levelBones = levelBones, range = range, largest = largest, omittedHit = omittedHit };
            }

            return new PoseLodPlan(player, player.TableBank, levelBones, range, largest, omittedHit, false, rangeSeconds,
                (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency);
        }

        /// <summary>
        /// Registers the character a prepared plan is for: its bones applied at level 0 and the character scheduled. Null,
        /// registering nothing, when the player's table bank is no longer the one the plan was made with (the caller
        /// prepares again or registers in full).
        /// </summary>
        public PoseLodCharacter Register(PoseLodPlan plan)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (plan == null || plan.Player == null || !ReferenceEquals(plan.Player.TableBank, plan.TableBank))
            {
                return null;
            }

            plan.Player.Managed = true;
            var character = new PoseLodCharacter(plan.Player, _characters.Count, plan.LevelBones, plan.Range, plan.OmittedHit)
            {
                RangeSeconds = plan.RangeSeconds,
                LargestBetweenSamples = plan.Largest,
                SharedPlan = plan.Shared,
            };
            character.Apply(0);
            _characters.Add(character);
            character.RegisterSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            return character;
        }

        // A plan made once and shared by the characters with the same inputs. Read only once made.
        private sealed class Plan
        {
            public int[][] levelBones;
            public Bounds range;
            public float largest;
            public int[] omittedHit;
        }

        // Everything the plan is made from, written out from local inputs (so a character's own placement in the world
        // cannot tell equal inputs apart; only the caller's hit boxes are rounded, see below): the table asset; for each table bone, whether it is
        // needed and its nearest ancestor the table drives (what the level sets follow); what each level names; the hit
        // boxes; and, for the range, every Transform from each hit bone up to the root with its local scale, and -- for one
        // the table does not drive -- its local position and rotation. Two characters with the same text get the same
        // plan. Made once per registration, not per frame.
        private static string PlanKey(PoseTablePlayer player, Dictionary<Transform, int> index, bool[] needed,
            IReadOnlyList<Transform>[] omitFrom, IReadOnlyList<(Transform bone, Bounds box)> hitBoxes)
        {
            if (player.TableAsset == null)
            {
                return null;
            }

            var text = new System.Text.StringBuilder(8 * player.BoneCount);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            text.Append(player.TableAsset.GetInstanceID()).Append('|').Append(player.BoneCount).Append('|');
            if (player.TableBank != null) text.Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(player.TableBank)).Append('|');
            for (int i = 0; i < player.BoneCount; i++)
            {
                Transform bone = player.BoneAt(i);
                int parent = -1;
                for (Transform t = bone != null ? bone.parent : null; t != null && parent < 0; t = t.parent)
                {
                    if (index.TryGetValue(t, out int p)) parent = p;
                }

                text.Append(bone == null ? 'x' : needed[i] ? 'n' : 'u').Append(parent).Append(',');
            }

            text.Append('|');
            if (omitFrom != null)
            {
                foreach (IReadOnlyList<Transform> named in omitFrom)
                {
                    if (named != null)
                    {
                        foreach (Transform t in named) text.Append(t != null && index.TryGetValue(t, out int i) ? i : -1).Append(',');
                    }

                    text.Append(';');
                }
            }

            // The range's inputs, all local (nothing goes through the world, so the root's own placement cannot enter): the
            // hit boxes, and every Transform from each hit bone up to the root -- its local scale (the table animates none),
            // and, for one the table does not drive, its local position and rotation (the table gives the others').
            // The hit boxes are the caller's, and may carry float noise from where each character stands (a character's box
            // made through its renderer's world scale): they are written to 0.1 mm. Two characters share a range only when
            // their boxes differ by less than that, which the range's margin (settings.rangeMargin, 1 cm) holds; boxes
            // that round apart just get their own plan.
            Transform root = player.transform;
            foreach ((Transform bone, Bounds box) in hitBoxes)
            {
                text.Append('|').Append(bone != null && index.TryGetValue(bone, out int b) ? b : -1).Append(':');
                AppendRounded(text, box.min, inv);
                AppendRounded(text, box.max, inv);
                for (Transform t = bone; t != null && t != root; t = t.parent)
                {
                    text.Append('/');
                    Append(text, t.localScale, inv);
                    if (!index.ContainsKey(t))
                    {
                        text.Append('s');
                        Append(text, t.localPosition, inv);
                        Quaternion q = t.localRotation;
                        text.Append(q.x.ToString("R", inv)).Append(',').Append(q.y.ToString("R", inv)).Append(',')
                            .Append(q.z.ToString("R", inv)).Append(',').Append(q.w.ToString("R", inv)).Append(',');
                    }
                }
            }

            return text.ToString();
        }

        private static void Append(System.Text.StringBuilder text, Vector3 v, System.Globalization.CultureInfo inv)
        {
            text.Append(v.x.ToString("R", inv)).Append(',').Append(v.y.ToString("R", inv)).Append(',').Append(v.z.ToString("R", inv)).Append(',');
        }

        private static void AppendRounded(System.Text.StringBuilder text, Vector3 v, System.Globalization.CultureInfo inv)
        {
            text.Append(v.x.ToString("F4", inv)).Append(',').Append(v.y.ToString("F4", inv)).Append(',').Append(v.z.ToString("F4", inv)).Append(',');
        }

        /// <summary>Lets <paramref name="character"/>'s player go back to updating on its own.</summary>
        public void Unregister(PoseLodCharacter character)
        {
            if (character != null && _characters.Remove(character) && character.Player != null)
            {
                character.Player.Managed = false;
            }
        }

        // The range: the hit boxes at every sample, each widened for the stretch to the next sample by a bound on how far
        // the pose can carry it in between (see Register). Distances in the root's frame; the table animates no scale.
        private static Bounds RangeOf(PoseTablePlayer player, Dictionary<Transform, int> index,
            IReadOnlyList<(Transform bone, Bounds box)> hitBoxes, float margin, out float largest)
        {
            if (player.TableBank != null && player.TableBank.Count > 1)
                return BankRange(player, index, hitBoxes, margin, out largest);
            Transform root = player.transform;
            PoseTable table = player.Table;
            int samples = table.SampleCount;
            int hits = hitBoxes.Count;
            largest = 0f;

            // Each hit bone's chain of table bones (itself first, then up), with each chain bone's parent's scale in the
            // root's frame, its longest local position in the table, and the box's reach from its bone.
            var chains = new int[hits][];
            var parentScale = new float[hits][];
            var longest = new float[hits][];
            var reach = new float[hits];
            Matrix4x4 worldToRoot0 = root.worldToLocalMatrix;
            for (int h = 0; h < hits; h++)
            {
                (Transform bone, Bounds box) = hitBoxes[h];
                // Every Transform from the bone up to the root: a table bone turns and moves as the table says; any other
                // stands still but still lengthens the reach below the bones above it.
                var chain = new List<int>();
                var scales = new List<float>();
                var lengths = new List<float>();
                for (Transform t = bone; t != null && t != root; t = t.parent)
                {
                    Transform parent = t.parent;
                    scales.Add(parent != null ? MaxScale(worldToRoot0 * parent.localToWorldMatrix) : MaxScale(worldToRoot0));
                    if (index.TryGetValue(t, out int i))
                    {
                        chain.Add(i);
                        float most = 0f;
                        for (int s = 0; s < samples; s++) most = Mathf.Max(most, table.SampleLocalPosition(s, i).magnitude);
                        lengths.Add(most);
                    }
                    else
                    {
                        chain.Add(-1);
                        lengths.Add(t.localPosition.magnitude);
                    }
                }

                chains[h] = chain.ToArray();
                parentScale[h] = scales.ToArray();
                longest[h] = lengths.ToArray();

                float corner = Mathf.Max(box.min.magnitude, box.max.magnitude, new Vector3(box.min.x, box.max.y, box.min.z).magnitude,
                    new Vector3(box.max.x, box.min.y, box.max.z).magnitude);
                corner = Mathf.Max(corner, new Vector3(box.min.x, box.min.y, box.max.z).magnitude, new Vector3(box.max.x, box.max.y, box.min.z).magnitude,
                    new Vector3(box.min.x, box.max.y, box.max.z).magnitude, new Vector3(box.max.x, box.min.y, box.min.z).magnitude);
                reach[h] = corner * (bone != null ? MaxScale(worldToRoot0 * bone.localToWorldMatrix) : 1f);
            }

            // Every box at every sample, in the root's frame.
            var boxes = new Bounds[samples, hits];
            for (int s = 0; s < samples; s++)
            {
                player.ApplySourceForPreparation(table.SampleTime(s));
                Matrix4x4 worldToRoot = root.worldToLocalMatrix;
                for (int h = 0; h < hits; h++)
                {
                    (Transform bone, Bounds box) = hitBoxes[h];
                    Matrix4x4 m = worldToRoot * bone.localToWorldMatrix;
                    Vector3 lo = box.min, hi = box.max;
                    var b = new Bounds(m.MultiplyPoint3x4(lo), Vector3.zero);
                    for (int c = 1; c < 8; c++)
                    {
                        b.Encapsulate(m.MultiplyPoint3x4(new Vector3((c & 1) == 0 ? lo.x : hi.x, (c & 2) == 0 ? lo.y : hi.y, (c & 4) == 0 ? lo.z : hi.z)));
                    }

                    boxes[s, h] = b;
                }
            }

            var range = boxes[0, 0];
            for (int s = 0; s < samples; s++)
            {
                for (int h = 0; h < hits; h++)
                {
                    Bounds at = boxes[s, h];
                    range.Encapsulate(at);
                    if (s + 1 >= samples)
                    {
                        continue;
                    }

                    // How far the pose can carry a corner between samples s and s+1: each chain bone's turn times the
                    // longest reach below it, plus each chain bone's local move.
                    float bound = 0f;
                    int[] chain = chains[h];
                    for (int c = 0; c < chain.Length; c++)
                    {
                        if (chain[c] < 0)
                        {
                            continue; // not a table bone: it neither turns nor moves
                        }

                        float below = reach[h];
                        for (int d = 0; d < c; d++) below += parentScale[h][d] * longest[h][d];
                        Quaternion q0 = table.SampleLocalRotation(s, chain[c]);
                        Quaternion q1 = table.SampleLocalRotation(s + 1, chain[c]);
                        float dot = Mathf.Min(1f, Mathf.Abs(Quaternion.Dot(q0, q1)) / Mathf.Max(1e-6f, Mathf.Sqrt(Quaternion.Dot(q0, q0) * Quaternion.Dot(q1, q1))));
                        float turn = 2f * Mathf.Acos(dot);
                        bound += turn * below;
                        bound += parentScale[h][c] * (table.SampleLocalPosition(s + 1, chain[c]) - table.SampleLocalPosition(s, chain[c])).magnitude;
                    }

                    largest = Mathf.Max(largest, bound);
                    Bounds widened = at;
                    widened.Expand(2f * bound);
                    range.Encapsulate(widened);
                    Bounds next = boxes[s + 1, h];
                    next.Expand(2f * bound);
                    range.Encapsulate(next);
                }
            }

            range.Expand(2f * margin);
            return range;
        }

        // Rotation-independent bound of every clip, including interpolation. Built once per shared bank/rig.
        private static Bounds BankRange(PoseTablePlayer player, Dictionary<Transform, int> index,
            IReadOnlyList<(Transform bone, Bounds box)> hitBoxes, float margin, out float largest)
        {
            var lengths = new float[player.BoneCount];
            foreach (var table in player.TableBank)
                for (int i = 0; i < lengths.Length; i++)
                    for (int s = 0; s < table.SampleCount; s++)
                        lengths[i] = Mathf.Max(lengths[i], table.SampleLocalPosition(s, i).magnitude);
            float radius = 0;
            Matrix4x4 inverse = player.transform.worldToLocalMatrix;
            foreach (var hit in hitBoxes)
            {
                float reach = (hit.box.center.magnitude + hit.box.extents.magnitude) * MaxScale(inverse * hit.bone.localToWorldMatrix);
                for (Transform t = hit.bone; t != null && t != player.transform; t = t.parent)
                    reach += (index.TryGetValue(t, out int i) ? lengths[i] : t.localPosition.magnitude)
                        * MaxScale(t.parent != null ? inverse * t.parent.localToWorldMatrix : inverse);
                radius = Mathf.Max(radius, reach);
            }
            largest = radius;
            return new Bounds(Vector3.zero, Vector3.one * (2 * (radius + margin)));
        }

        private static float MaxScale(Matrix4x4 m)
        {
            return Mathf.Max(((Vector3)m.GetColumn(0)).magnitude, Mathf.Max(((Vector3)m.GetColumn(1)).magnitude, ((Vector3)m.GetColumn(2)).magnitude));
        }

        private float ResolveRate()
        {
            if (displayRateHz > 0f)
            {
                return displayRateHz;
            }

            if (XRSettings.isDeviceActive)
            {
                SubsystemManager.GetSubsystems(_displays);
                for (int i = 0; i < _displays.Count; i++)
                {
                    if (_displays[i].running && _displays[i].TryGetDisplayRefreshRate(out float rate) && rate > 0f)
                    {
                        return rate;
                    }
                }
            }

            return 72f;
        }

        private void Update()
        {
            int frame = Time.frameCount;
            UpdatedLastFrame = 0;
            using (s_decide.Auto())
            {
                if (_rate <= 0f)
                {
                    _rate = ResolveRate();
                }

                if (!NeededOnly)
                {
                    ReadEyes();
                }

                for (int c = 0; c < _characters.Count; c++)
                {
                    PoseLodCharacter character = _characters[c];
                    character.Due = false;
                    if (!character.IsLive)
                    {
                        continue;
                    }

                    if (NeededOnly)
                    {
                        character.Level = 0;
                        character.Due = true;
                        continue;
                    }

                    int level = character.LevelOverride >= 0 ? Math.Min(character.LevelOverride, Levels - 1) : Decide(character);
                    bool dropped = level < character.Level;
                    character.Level = level;
                    int interval = IntervalOf(level);
                    character.Due = dropped || character.LastUpdateFrame < 0 || (frame + character.Phase) % interval == 0;
                }
            }

            for (int c = 0; c < _characters.Count; c++)
            {
                PoseLodCharacter character = _characters[c];
                if (character.Due && character.Apply(character.Level))
                {
                    character.LastUpdateFrame = frame;
                    UpdatedLastFrame++;
                }
            }
        }

        // Both eyes' frusta and positions (one when the camera is not stereo), once per frame.
        private void ReadEyes()
        {
            Camera camera = viewCamera != null ? viewCamera : Camera.main;
            _eyes = 0;
            if (camera == null)
            {
                return;
            }

            if (camera.stereoEnabled)
            {
                for (int e = 0; e < 2; e++)
                {
                    var eye = e == 0 ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;
                    Matrix4x4 view = camera.GetStereoViewMatrix(eye);
                    GeometryUtility.CalculateFrustumPlanes(camera.GetStereoProjectionMatrix(eye) * view, _eyePlanes[e]);
                    _eyePositions[e] = view.inverse.GetColumn(3);
                }

                _eyes = 2;
            }
            else
            {
                GeometryUtility.CalculateFrustumPlanes(camera.projectionMatrix * camera.worldToCameraMatrix, _eyePlanes[0]);
                _eyePositions[0] = camera.transform.position;
                _eyes = 1;
            }
        }

        private int Decide(PoseLodCharacter character)
        {
            if (_eyes == 0)
            {
                return 0;
            }

            Transform root = character.Root;
            Bounds range = character.RangeBounds;
            Vector3 centre = root.TransformPoint(range.center);
            Vector3 scale = root.lossyScale;
            float radius = range.extents.magnitude * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));

            float outside = float.PositiveInfinity; // degrees outside the nearer eye's view
            float nearest = float.PositiveInfinity; // metres from an eye to the range's surface
            for (int e = 0; e < _eyes; e++)
            {
                float distance = Vector3.Distance(centre, _eyePositions[e]);
                nearest = Mathf.Min(nearest, Mathf.Max(0f, distance - radius));
                outside = Mathf.Min(outside, DegreesOutside(_eyePlanes[e], centre, radius, distance));
            }

            int view = outside <= 0f ? 0 : Mathf.Clamp(Mathf.CeilToInt(3f * outside / settings.viewFullAngleDegrees), 1, 3);
            float span = Mathf.Max(1e-3f, settings.farMetres - settings.nearMetres);
            int far = nearest <= settings.nearMetres ? 0 : Mathf.Clamp(Mathf.CeilToInt(3f * (nearest - settings.nearMetres) / span), 1, 3);
            return Math.Max(view, far);
        }

        // 0 when the sphere meets the frustum's four sides (near and far are not the view's edge); otherwise how many
        // degrees, seen from the eye, the sphere stands beyond the side it is farthest outside.
        private static float DegreesOutside(Plane[] planes, Vector3 centre, float radius, float distance)
        {
            if (distance <= radius)
            {
                return 0f;
            }

            float worst = 0f;
            for (int p = 0; p < 4; p++)
            {
                float s = planes[p].GetDistanceToPoint(centre);
                if (s >= -radius)
                {
                    continue;
                }

                float beyond = Mathf.Asin(Mathf.Clamp(-s / distance, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(radius / distance, -1f, 1f));
                worst = Mathf.Max(worst, beyond * Mathf.Rad2Deg);
            }

            return worst;
        }
    }
}
