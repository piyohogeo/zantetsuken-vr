using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.Core
{
    /// <summary>
    /// The fixed `PlayerLocomotionOccupancy` of DESIGN 7.2.3 / D-166: a small set of world-space boxes and capsules,
    /// settled once when the level initialises and never changed afterwards.
    /// <para>
    /// **Copied, not referenced.** Each primitive's world position, rotation and size are copied from its explicit
    /// authoring (<see cref="PlayerLocomotionOccupancyVolume"/>) at the moment of building; nothing of the source
    /// object is kept. So whatever later happens to those objects -- moved, cut, replaced by fragments, retired,
    /// destroyed -- the set stays exactly as it was built, which is what DESIGN asks for. Nothing is ever inferred
    /// from bounds, names, materials or colliders, and the dynamic physics scene is not queried.
    /// </para>
    /// </summary>
    public sealed class PlayerLocomotionOccupancy
    {
        private readonly LocomotionBox[] _boxes;
        private readonly LocomotionCapsule[] _capsules;

        public PlayerLocomotionOccupancy(IReadOnlyList<LocomotionBox> boxes, IReadOnlyList<LocomotionCapsule> capsules)
        {
            _boxes = new LocomotionBox[boxes?.Count ?? 0];
            for (int i = 0; i < _boxes.Length; i++)
            {
                _boxes[i] = boxes[i];
            }

            _capsules = new LocomotionCapsule[capsules?.Count ?? 0];
            for (int i = 0; i < _capsules.Length; i++)
            {
                _capsules[i] = capsules[i];
            }
        }

        /// <summary>An occupancy with nothing in it: every request is allowed.</summary>
        public static readonly PlayerLocomotionOccupancy Empty = new PlayerLocomotionOccupancy(null, null);

        public int BoxCount => _boxes.Length;

        public int CapsuleCount => _capsules.Length;

        public LocomotionBox Box(int index) => _boxes[index];

        public LocomotionCapsule Capsule(int index) => _capsules[index];

        /// <summary>Builds the set from the given authoring volumes as they stand now.</summary>
        public static PlayerLocomotionOccupancy Build(IReadOnlyList<PlayerLocomotionOccupancyVolume> volumes)
        {
            var boxes = new List<LocomotionBox>();
            var capsules = new List<LocomotionCapsule>();
            if (volumes != null)
            {
                for (int i = 0; i < volumes.Count; i++)
                {
                    PlayerLocomotionOccupancyVolume volume = volumes[i];
                    if (volume == null)
                    {
                        continue;
                    }

                    if (volume.Kind == PlayerLocomotionOccupancyVolume.Shape.Capsule)
                    {
                        capsules.Add(volume.WorldCapsule());
                    }
                    else
                    {
                        boxes.Add(volume.WorldBox());
                    }
                }
            }

            return new PlayerLocomotionOccupancy(boxes, capsules);
        }

        /// <summary>Whether <paramref name="capsule"/> overlaps any primitive of the set.</summary>
        public bool Overlaps(in LocomotionCapsule capsule)
        {
            for (int i = 0; i < _boxes.Length; i++)
            {
                if (LocomotionOverlap.Overlaps(capsule, _boxes[i]))
                {
                    return true;
                }
            }

            for (int i = 0; i < _capsules.Length; i++)
            {
                if (LocomotionOverlap.Overlaps(capsule, _capsules[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
