using System;
using UnityEngine;

namespace Zantetsu.Observability
{
    /// <summary>
    /// A captured pose for a single capture frame (head or a controller). A
    /// value type with no reference-type fields and no Unity static API,
    /// logging, string generation, or heap allocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Availability is explicit: <see cref="default"/> and
    /// <see cref="Unavailable"/> both mean "no pose", reporting
    /// <see cref="IsAvailable"/> == false and <see cref="Pose"/> == default.
    /// Only values produced by the public constructor report
    /// <see cref="IsAvailable"/> == true. An unavailable pose is never
    /// auto-completed to the identity pose or any other substitute.
    /// </para>
    /// <para>
    /// The rotation is normalized inside the constructor. The squared
    /// magnitude is promoted to double so the squaring step cannot overflow
    /// float and tiny magnitudes are not crushed to zero before validation.
    /// NaN, infinity, overflow, zero, and sufficiently tiny magnitudes are all
    /// rejected explicitly; the normalized result is re-checked to be finite.
    /// </para>
    /// </remarks>
    public readonly struct CapturePoseSample
    {
        /// <summary>Minimum accepted squared rotation magnitude (1e-12).</summary>
        private const double MinSquaredMagnitude = 1e-12;

        /// <summary>Maximum accepted deviation of the squared magnitude from 1.0 for a canonical unit quaternion (1e-4).</summary>
        private const double UnitSquaredMagnitudeTolerance = 1e-4;

        public bool IsAvailable { get; }

        public Pose Pose { get; }

        public Vector3 Position => Pose.position;

        public Quaternion Rotation => Pose.rotation;

        /// <summary>The unavailable sample: <see cref="default"/>.</summary>
        public static CapturePoseSample Unavailable => default;

        public CapturePoseSample(Vector3 position, Quaternion rotation)
        {
            if (!IsFinite(position))
            {
                throw new ArgumentException("Position components must all be finite.", nameof(position));
            }

            if (!IsFinite(rotation))
            {
                throw new ArgumentException("Rotation components must all be finite.", nameof(rotation));
            }

            double squaredMagnitude =
                (double)rotation.x * rotation.x +
                (double)rotation.y * rotation.y +
                (double)rotation.z * rotation.z +
                (double)rotation.w * rotation.w;

            if (double.IsNaN(squaredMagnitude) ||
                double.IsInfinity(squaredMagnitude) ||
                squaredMagnitude > float.MaxValue)
            {
                throw new ArgumentException("Rotation squared magnitude overflows.", nameof(rotation));
            }

            if (squaredMagnitude <= MinSquaredMagnitude)
            {
                throw new ArgumentException("Rotation squared magnitude must be sufficiently non-zero.", nameof(rotation));
            }

            double magnitude = Math.Sqrt(squaredMagnitude);
            Quaternion normalized = new Quaternion(
                (float)(rotation.x / magnitude),
                (float)(rotation.y / magnitude),
                (float)(rotation.z / magnitude),
                (float)(rotation.w / magnitude));

            if (!IsFinite(normalized))
            {
                throw new ArgumentException("Normalized rotation components must all be finite.", nameof(rotation));
            }

            IsAvailable = true;
            Pose = new Pose(position, normalized);
        }

        /// <summary>
        /// Restores an already-normalized unit quaternion from canonical JSON
        /// without re-normalizing it, so the serialized float values round-trip
        /// byte-for-byte. The position and rotation must be finite, and the
        /// rotation must already be a unit quaternion.
        /// </summary>
        internal static CapturePoseSample RestoreCanonical(Vector3 position, Quaternion normalizedRotation)
        {
            if (!IsFinite(position))
            {
                throw new ArgumentException("Position components must all be finite.", nameof(position));
            }

            if (!IsFinite(normalizedRotation))
            {
                throw new ArgumentException("Rotation components must all be finite.", nameof(normalizedRotation));
            }

            double squaredMagnitude =
                (double)normalizedRotation.x * normalizedRotation.x +
                (double)normalizedRotation.y * normalizedRotation.y +
                (double)normalizedRotation.z * normalizedRotation.z +
                (double)normalizedRotation.w * normalizedRotation.w;

            double deviation = squaredMagnitude - 1.0;
            if (deviation < 0.0)
            {
                deviation = -deviation;
            }

            if (double.IsNaN(squaredMagnitude) || double.IsInfinity(squaredMagnitude) || deviation > UnitSquaredMagnitudeTolerance)
            {
                throw new ArgumentException("Rotation must be a unit quaternion.", nameof(normalizedRotation));
            }

            return new CapturePoseSample(position, normalizedRotation, true);
        }

        // The bool distinguishes this private constructor from the public one.
        private CapturePoseSample(Vector3 position, Quaternion rotation, bool restoreCanonical)
        {
            IsAvailable = true;
            Pose = new Pose(position, rotation);
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);
        }
    }
}
