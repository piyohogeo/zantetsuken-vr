using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Zantetsu.Core.Animation
{
    /// <summary>
    /// One retargeted bone Pose Table of one Clip (DESIGN 19.3, D-135): the local position and rotation of every table
    /// bone at each sample, read once from the animation probe's <c>.ptab</c> format (ZPTAB002/003) and not changed
    /// afterwards. It is the product's evaluation of Current poses; Editor/offline tools make the tables.
    /// <para>
    /// **Evaluation takes a resolved Source Time and advances nothing.** <see cref="TryEvaluate"/> reads the two samples
    /// around that time and interpolates -- linearly for positions, normalized shortest-arc lerp for rotations, as the
    /// probe that baked the table does -- so the same Source Time gives the same pose whatever order it is asked in.
    /// Mapping a target time to a Source Time is <see cref="ResolveSourceTime"/>: a looping Clip wraps (duration maps
    /// back to 0), any other clamps and holds its last sample. A time that is not finite, or a table whose arrays do
    /// not hold together, gives no pose at all.
    /// </para>
    /// <para>
    /// The root track the table also carries is kept but not evaluated here: where the character stands is its scene's
    /// placement, and applying the root motion is not part of the Current bone pose.
    /// </para>
    /// </summary>
    public sealed class PoseTable
    {
        private const string MagicV3 = "ZPTAB003";
        private const string MagicV2 = "ZPTAB002";

        private float[] _sampleTimes;
        private string[] _bonePaths;
        private Vector3[] _localPositions;
        private Quaternion[] _localRotations;

        private PoseTable()
        {
        }

        public string CharacterId { get; private set; }

        public string ClipId { get; private set; }

        public string ClipName { get; private set; }

        public string BakeKey { get; private set; }

        public float DurationSeconds { get; private set; }

        public float SampleRate { get; private set; }

        public int RegularSampleCount { get; private set; }

        public bool HasEndSample { get; private set; }

        public bool IsLooping { get; private set; }

        public int BoneCount => _bonePaths.Length;

        public int SampleCount => _sampleTimes.Length;

        /// <summary>The path of table bone <paramref name="index"/>, relative to the character's model root.</summary>
        public string BonePath(int index) => _bonePaths[index];

        /// <summary>
        /// Reads one table. False, with the reason, for anything that is not a whole, consistent table: a wrong magic,
        /// a truncated file, arrays whose lengths do not match the sample and bone counts, or values that are not finite.
        /// </summary>
        public static bool TryRead(byte[] bytes, out PoseTable table, out string error)
        {
            table = null;
            error = null;
            if (bytes == null || bytes.Length < MagicV3.Length)
            {
                error = "no table";
                return false;
            }

            try
            {
                using (var reader = new BinaryReader(new MemoryStream(bytes, false), Encoding.UTF8))
                {
                    string magic = Encoding.ASCII.GetString(reader.ReadBytes(MagicV3.Length));
                    if (magic != MagicV3 && magic != MagicV2)
                    {
                        error = "not a pose table (" + magic + ")";
                        return false;
                    }

                    var made = new PoseTable
                    {
                        CharacterId = reader.ReadString(),
                        ClipId = reader.ReadString(),
                        ClipName = reader.ReadString(),
                    };
                    reader.ReadString(); // source asset path
                    reader.ReadString(); // source file sha256
                    reader.ReadString(); // bake version
                    made.BakeKey = reader.ReadString();
                    made.DurationSeconds = reader.ReadSingle();
                    made.SampleRate = reader.ReadSingle();
                    made.RegularSampleCount = reader.ReadInt32();
                    made.HasEndSample = reader.ReadBoolean();
                    made.IsLooping = reader.ReadBoolean();
                    reader.ReadSingle(); // human scale
                    reader.ReadSingle(); // last sample evaluated at
                    reader.ReadSingle(); // end epsilon
                    reader.ReadSingle(); // max scale deviation
                    Skip(reader, 12);    // local scales (bind, not animated)
                    Skip(reader, 4);     // parent indices
                    Skip(reader, 4);     // human bone indices
                    made._sampleTimes = new float[Count(reader)];
                    for (int i = 0; i < made._sampleTimes.Length; i++) made._sampleTimes[i] = reader.ReadSingle();
                    made._bonePaths = new string[Count(reader)];
                    for (int i = 0; i < made._bonePaths.Length; i++) made._bonePaths[i] = reader.ReadString();
                    made._localPositions = new Vector3[Count(reader)];
                    for (int i = 0; i < made._localPositions.Length; i++)
                        made._localPositions[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    made._localRotations = new Quaternion[Count(reader)];
                    for (int i = 0; i < made._localRotations.Length; i++)
                        made._localRotations[i] = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                    // The root track and the contact tail follow; neither is part of the Current bone pose.
                    if (!made.HoldsTogether(out error))
                    {
                        return false;
                    }

                    table = made;
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                error = "truncated table";
                return false;
            }
        }

        /// <summary>
        /// The Source Time a target time resolves to: wrapped for a looping Clip (so the duration maps to 0), clamped for
        /// any other (so it holds its last sample). NaN for a time that is not finite.
        /// </summary>
        public double ResolveSourceTime(double targetTime)
        {
            if (!double.IsFinite(targetTime))
            {
                return double.NaN;
            }

            double duration = DurationSeconds;
            if (IsLooping)
            {
                double wrapped = targetTime - Math.Floor(targetTime / duration) * duration;
                return wrapped >= duration ? 0.0 : wrapped;
            }

            return Math.Min(Math.Max(targetTime, 0.0), duration);
        }

        /// <summary>
        /// The pose at <paramref name="sourceTime"/>, which must already be resolved into [0, duration]. Writes one
        /// local position and rotation per table bone. False, writing nothing, for a time outside that range or not
        /// finite, or buffers smaller than the bone count.
        /// </summary>
        public bool TryEvaluate(double sourceTime, Vector3[] positions, Quaternion[] rotations)
        {
            if (!double.IsFinite(sourceTime) || sourceTime < 0.0 || sourceTime > DurationSeconds
                || positions == null || rotations == null || positions.Length < BoneCount || rotations.Length < BoneCount)
            {
                return false;
            }

            float time = (float)sourceTime;
            int first = Math.Min((int)Math.Floor(time * SampleRate), RegularSampleCount - 1);
            int second = first;
            float weight = 0f;
            if (first < SampleCount - 1)
            {
                second = first + 1;
                float span = _sampleTimes[second] - _sampleTimes[first];
                weight = span > 0f ? Mathf.Clamp01((time - _sampleTimes[first]) / span) : 0f;
            }

            int a = first * BoneCount;
            int b = second * BoneCount;
            for (int bone = 0; bone < BoneCount; bone++)
            {
                positions[bone] = Vector3.LerpUnclamped(_localPositions[a + bone], _localPositions[b + bone], weight);
                Quaternion from = _localRotations[a + bone];
                Quaternion to = _localRotations[b + bone];
                if (Quaternion.Dot(from, to) < 0f)
                {
                    to = new Quaternion(-to.x, -to.y, -to.z, -to.w);
                }

                rotations[bone] = Quaternion.Normalize(Quaternion.LerpUnclamped(from, to, weight));
            }

            return true;
        }

        /// <summary>
        /// The pose at <paramref name="sourceTime"/> for the table bones in <paramref name="bones"/>[0..count) only: the
        /// same interpolation as <see cref="TryEvaluate(double, Vector3[], Quaternion[])"/>, written at each named bone's
        /// own index and nowhere else, so a caller that applies only those bones evaluates only those. False, writing
        /// nothing, for the same times and buffers, or an index outside the table.
        /// </summary>
        public bool TryEvaluate(double sourceTime, int[] bones, int count, Vector3[] positions, Quaternion[] rotations)
        {
            if (!double.IsFinite(sourceTime) || sourceTime < 0.0 || sourceTime > DurationSeconds || bones == null
                || count < 0 || count > bones.Length || positions == null || rotations == null
                || positions.Length < BoneCount || rotations.Length < BoneCount)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if ((uint)bones[i] >= (uint)BoneCount)
                {
                    return false;
                }
            }

            float time = (float)sourceTime;
            int first = Math.Min((int)Math.Floor(time * SampleRate), RegularSampleCount - 1);
            int second = first;
            float weight = 0f;
            if (first < SampleCount - 1)
            {
                second = first + 1;
                float span = _sampleTimes[second] - _sampleTimes[first];
                weight = span > 0f ? Mathf.Clamp01((time - _sampleTimes[first]) / span) : 0f;
            }

            int a = first * BoneCount;
            int b = second * BoneCount;
            for (int i = 0; i < count; i++)
            {
                int bone = bones[i];
                positions[bone] = Vector3.LerpUnclamped(_localPositions[a + bone], _localPositions[b + bone], weight);
                Quaternion from = _localRotations[a + bone];
                Quaternion to = _localRotations[b + bone];
                if (Quaternion.Dot(from, to) < 0f)
                {
                    to = new Quaternion(-to.x, -to.y, -to.z, -to.w);
                }

                rotations[bone] = Quaternion.Normalize(Quaternion.LerpUnclamped(from, to, weight));
            }

            return true;
        }

        /// <summary>The sample times (seconds), in order: what a caller that sweeps the whole Clip once walks over.</summary>
        public float SampleTime(int index) => _sampleTimes[index];

        /// <summary>Table bone <paramref name="bone"/>'s local position at sample <paramref name="sample"/>, as stored.</summary>
        public Vector3 SampleLocalPosition(int sample, int bone) => _localPositions[sample * BoneCount + bone];

        /// <summary>Table bone <paramref name="bone"/>'s local rotation at sample <paramref name="sample"/>, as stored.</summary>
        public Quaternion SampleLocalRotation(int sample, int bone) => _localRotations[sample * BoneCount + bone];

        private bool HoldsTogether(out string error)
        {
            error = null;
            int expectedSamples = RegularSampleCount + (HasEndSample ? 1 : 0);
            if (!float.IsFinite(DurationSeconds) || DurationSeconds <= 0f || !float.IsFinite(SampleRate) || SampleRate <= 0f
                || RegularSampleCount <= 0 || _sampleTimes.Length != expectedSamples || _bonePaths.Length == 0
                || _localPositions.Length != expectedSamples * _bonePaths.Length
                || _localRotations.Length != expectedSamples * _bonePaths.Length)
            {
                error = "the table's counts do not hold together";
                return false;
            }

            for (int i = 0; i < _sampleTimes.Length; i++)
            {
                if (!float.IsFinite(_sampleTimes[i]) || (i > 0 && _sampleTimes[i] < _sampleTimes[i - 1]))
                {
                    error = "sample times are not finite and ordered";
                    return false;
                }
            }

            for (int i = 0; i < _localPositions.Length; i++)
            {
                Vector3 p = _localPositions[i];
                Quaternion q = _localRotations[i];
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)
                    || !float.IsFinite(q.x) || !float.IsFinite(q.y) || !float.IsFinite(q.z) || !float.IsFinite(q.w))
                {
                    error = "a sample is not finite";
                    return false;
                }
            }

            return true;
        }

        private static int Count(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > reader.BaseStream.Length)
            {
                throw new EndOfStreamException();
            }

            return count;
        }

        private static void Skip(BinaryReader reader, int elementBytes)
        {
            long bytes = (long)Count(reader) * elementBytes;
            if (reader.BaseStream.Position + bytes > reader.BaseStream.Length)
            {
                throw new EndOfStreamException();
            }

            reader.BaseStream.Seek(bytes, SeekOrigin.Current);
        }
    }
}
