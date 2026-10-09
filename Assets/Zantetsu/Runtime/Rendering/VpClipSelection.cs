using System;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// What one camera's frame keeps of a display's clip list (DESIGN 4.5.9): for every entry of the list, whether the
    /// body is drawn and whether it casts, judged on the CPU by the very conditions that camera's GPU selection was
    /// issued with (<see cref="VpCullConditions"/>) and by the tests the selection makes
    /// (<see cref="VpInstanceCulling"/>), on the entry's bounds as its present transform carries them. The entries are
    /// the clip list's alone: nothing else of the scene is gone through, no transform is asked for and nothing is read
    /// back from the GPU.
    /// <para>
    /// It is made when the camera's selection is issued, after the camera's draws were registered and before its
    /// passes run. Until then -- and for a camera whose selection is never issued in a frame -- it is not valid and
    /// **keeps everything**: whoever draws the list by it then draws every entry, as before there was a selection.
    /// </para>
    /// <para>
    /// The judgements keep on the uncertain side: the selection's own margin and <see cref="ExtraMargin"/> more, since
    /// they are made with other rounding than the GPU's. What is kept here may draw nothing; what is needed is not
    /// to be left out.
    /// </para>
    /// <para>
    /// The casters' per-slice judgement is asked by whoever draws a slice, with that slice's own projection
    /// (<see cref="CasterKeptInSlice"/>); the motion vectors' by whoever draws them, with their own conditions
    /// (<see cref="KeptBy"/>). The carried boxes are kept for those.
    /// </para>
    /// </summary>
    public sealed class VpClipSelection
    {
        /// <summary>World-space tolerance, in metres, added to <see cref="VpInstanceCulling.Margin"/> on the keeping side.</summary>
        public const float ExtraMargin = 1e-3f;

        private const byte BodyBit = 1;
        private const byte CasterBit = 2;

        private Vector3[] _centre = Array.Empty<Vector3>();
        private Vector3[] _axisX = Array.Empty<Vector3>();
        private Vector3[] _axisY = Array.Empty<Vector3>();
        private Vector3[] _axisZ = Array.Empty<Vector3>();
        private byte[] _flags = Array.Empty<byte>();

        /// <summary>How many entries the last judgement was made for.</summary>
        public int Count { get; private set; }

        /// <summary>Whether a judgement stands for the draws registered last. False keeps everything.</summary>
        public bool Valid { get; private set; }

        /// <summary>Of the last judgement: the entries whose body is drawn, and the entries that cast into some slice.</summary>
        public int BodyKeptCount { get; private set; }
        public int CasterKeptCount { get; private set; }

        /// <summary>The draws of a new frame were registered: what was judged before says nothing of them.</summary>
        public void Invalidate()
        {
            Valid = false;
        }

        /// <summary>Begins a judgement of <paramref name="count"/> entries; <see cref="Set"/> each, then <see cref="End"/>.</summary>
        public void Begin(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (_flags.Length < count)
            {
                int room = Math.Max(count, Math.Max(16, _flags.Length * 2));
                _centre = new Vector3[room];
                _axisX = new Vector3[room];
                _axisY = new Vector3[room];
                _axisZ = new Vector3[room];
                _flags = new byte[room];
            }

            Valid = false;
            Count = count;
            BodyKeptCount = 0;
            CasterKeptCount = 0;
        }

        /// <summary>Judges entry <paramref name="entry"/>: its command's bounds in the geometry's frame, carried by its present transform.</summary>
        public void Set(int entry, Bounds local, Matrix4x4 objectToWorld, VpCullConditions conditions)
        {
            VpInstanceCulling.Carry(local, objectToWorld, out Vector3 centre, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ);
            _centre[entry] = centre;
            _axisX[entry] = axisX;
            _axisY[entry] = axisY;
            _axisZ[entry] = axisZ;
            byte flags = 0;
            if (VpInstanceCulling.KeptForBody(centre, axisX, axisY, axisZ, conditions, ExtraMargin))
            {
                flags |= BodyBit;
                BodyKeptCount++;
            }

            if (VpInstanceCulling.KeptAsCaster(centre, axisX, axisY, axisZ, conditions, ExtraMargin))
            {
                flags |= CasterBit;
                CasterKeptCount++;
            }

            _flags[entry] = flags;
        }

        public void End()
        {
            Valid = true;
        }

        /// <summary>Whether entry <paramref name="entry"/>'s body is drawn. True when nothing is known of it.</summary>
        public bool BodyKept(int entry)
        {
            return !Valid || (uint)entry >= (uint)Count || (_flags[entry] & BodyBit) != 0;
        }

        /// <summary>Whether entry <paramref name="entry"/> casts into some slice. True when nothing is known of it.</summary>
        public bool CasterKept(int entry)
        {
            return !Valid || (uint)entry >= (uint)Count || (_flags[entry] & CasterBit) != 0;
        }

        /// <summary>
        /// Whether entry <paramref name="entry"/> is drawn into the slice whose view-projection is
        /// <paramref name="sliceViewProjection"/>: it casts into some slice, and -- when <paramref name="sliceSelection"/>
        /// says the per-slice judgement is made (a directional light's slice, the selection's switch on) -- its box
        /// does not lie wholly outside this one (<see cref="VpInstanceCulling.OutsideSlice(Vector3, Vector3, Vector3, Vector3, Matrix4x4, float)"/>).
        /// </summary>
        public bool CasterKeptInSlice(int entry, Matrix4x4 sliceViewProjection, float bias, bool sliceSelection)
        {
            if (!Valid || (uint)entry >= (uint)Count)
            {
                return true;
            }

            if ((_flags[entry] & CasterBit) == 0)
            {
                return false;
            }

            return !sliceSelection
                   || !VpInstanceCulling.OutsideSlice(_centre[entry], _axisX[entry], _axisY[entry], _axisZ[entry], sliceViewProjection, bias);
        }

        /// <summary>
        /// Whether entry <paramref name="entry"/>'s body is drawn by other conditions than the camera's own -- the
        /// motion vectors', whose eyes end nearer. True when nothing is known of it.
        /// </summary>
        public bool KeptBy(int entry, VpCullConditions conditions)
        {
            if (!Valid || (uint)entry >= (uint)Count || conditions == null)
            {
                return true;
            }

            return VpInstanceCulling.KeptForBody(_centre[entry], _axisX[entry], _axisY[entry], _axisZ[entry], conditions, ExtraMargin);
        }
    }
}
