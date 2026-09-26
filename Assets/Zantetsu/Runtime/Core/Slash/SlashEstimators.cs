using System;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Core.Slash
{
    /// <summary>
    /// The blade the gesture is read from (DESIGN 19.1.11): the grip-to-katana offset, the blade frame, its length, and
    /// the two points on it the slash uses -- the cut sample point for speed and the emission control point for the
    /// wave. Fixed ratios: the cut sample point at 70% and the emission control point at 50% of the blade.
    /// </summary>
    public readonly struct SlashBlade
    {
        /// <summary>Fraction of the blade length at which the cut sample point sits.</summary>
        public const float CutSampleRatio = 0.7f;

        /// <summary>Fraction of the blade length at which the emission control point sits.</summary>
        public const float EmissionControlPointRatio = 0.5f;

        public SlashBlade(Pose gripToKatana, float bladeLength)
        {
            GripToKatana = gripToKatana;
            BladeLength = bladeLength;

            // cross(BladeAxis, EdgeDirection) == SideNormal: cross(+Z, -Y) == +X.
            Frame = new BladeFrame(
                Vector3.forward, Vector3.down, Vector3.right, Vector3.forward * (bladeLength * CutSampleRatio));
        }

        public Pose GripToKatana { get; }

        public float BladeLength { get; }

        /// <summary>The katana-local blade frame: +Z blade axis, -Y edge direction, +X side normal.</summary>
        public BladeFrame Frame { get; }

        public bool IsValid => float.IsFinite(BladeLength) && BladeLength > 0f;

        /// <summary>The emission control point of a pose: halfway along the blade.</summary>
        public Vector3 Emitter(in EvaluatedBladePose pose)
        {
            return pose.KatanaPose.position + pose.BladeAxis * (BladeLength * EmissionControlPointRatio);
        }
    }

    /// <summary>
    /// The frame a stroke latches with (DESIGN 19.1.3, 19.1.4): the source slash plane, the begin and latest emitter
    /// points on it, the travel and span axes, and the initial span.
    /// </summary>
    public readonly struct SlashFrame
    {
        public SlashFrame(
            Plane sourceSlashPlane, Vector3 beginEmitter, Vector3 latestEmitter, Vector3 travelAxis, Vector3 spanAxis,
            float initialSpan)
        {
            SourceSlashPlane = sourceSlashPlane;
            BeginEmitter = beginEmitter;
            LatestEmitter = latestEmitter;
            TravelAxis = travelAxis;
            SpanAxis = spanAxis;
            InitialSpan = initialSpan;
        }

        public Plane SourceSlashPlane { get; }

        public Vector3 BeginEmitter { get; }

        public Vector3 LatestEmitter { get; }

        public Vector3 TravelAxis { get; }

        public Vector3 SpanAxis { get; }

        public float InitialSpan { get; }
    }

    /// <summary>What one span candidate evaluation gives (DESIGN 19.1.5): whether it may be taken, and its terms.</summary>
    public readonly struct SlashSpanCandidate
    {
        public SlashSpanCandidate(bool valid, float rawSpan, float q, float denominator, bool termsFinite)
        {
            Valid = valid;
            RawSpan = rawSpan;
            Q = q;
            Denominator = denominator;
            TermsFinite = termsFinite;
        }

        public bool Valid { get; }

        public float RawSpan { get; }

        public float Q { get; }

        public float Denominator { get; }

        public bool TermsFinite { get; }
    }

    /// <summary>
    /// Slash Latch Estimator (DESIGN 19.1.2): whether the stroke's accepted samples are ready to latch. It decides
    /// nothing about the plane, the axes, a hit or a target.
    /// <para>
    /// **An instance is shared, and never changes once it is set.** The core hands the current instance to every wave it
    /// publishes, and a wave keeps using it for its whole life; that is how a flying wave keeps the method and settings it
    /// latched with. So an implementation holds its settings in fields set at construction and not changed afterwards,
    /// and a change of setting is a new instance given to the core -- never a change to one already in use.
    /// </para>
    /// </summary>
    public interface ISlashLatchEstimator
    {
        bool IsLatchReady(ReadOnlySpan<EvaluatedBladePose> accepted, in SlashBlade blade);
    }

    /// <summary>
    /// Slash Frame Estimator (DESIGN 19.1.3): the plane and axes a stroke would latch with, or none.
    /// <para>
    /// **An instance is shared, and never changes once it is set.** The core hands the current instance to every wave it
    /// publishes, and a wave keeps using it for its whole life; that is how a flying wave keeps the method and settings it
    /// latched with. So an implementation holds its settings in fields set at construction and not changed afterwards,
    /// and a change of setting is a new instance given to the core -- never a change to one already in use.
    /// </para>
    /// </summary>
    public interface ISlashFrameEstimator
    {
        bool TryEstimate(ReadOnlySpan<EvaluatedBladePose> accepted, in SlashBlade blade, out SlashFrame frame);
    }

    /// <summary>
    /// Slash Span Candidate Estimator (DESIGN 19.1.5): the raw span a wave's current A point and guide give. It knows
    /// nothing of the accepted span, a hit, the VFX or a cut.
    /// <para>
    /// **An instance is shared, and never changes once it is set.** The core hands the current instance to every wave it
    /// publishes, and a wave keeps using it for its whole life; that is how a flying wave keeps the method and settings it
    /// latched with. So an implementation holds its settings in fields set at construction and not changed afterwards,
    /// and a change of setting is a new instance given to the core -- never a change to one already in use.
    /// </para>
    /// </summary>
    public interface ISlashSpanCandidateEstimator
    {
        SlashSpanCandidate Evaluate(Vector3 planeNormal, Vector3 a, Vector3 spanAxis, Vector3 guideOrigin, Vector3 guideDirection);
    }

    /// <summary>
    /// Span Close Estimator (DESIGN 19.1.1, 19.1.6): whether a wave stops taking the current blade now. A wave need not
    /// close before it expires: one that never closes simply ends its open span at its expiry (19.1.1).
    /// <para>
    /// **An instance is shared, and never changes once it is set.** The core hands the current instance to every wave it
    /// publishes, and a wave keeps using it for its whole life; that is how a flying wave keeps the method and settings it
    /// latched with. So an implementation holds its settings in fields set at construction and not changed afterwards,
    /// and a change of setting is a new instance given to the core -- never a change to one already in use.
    /// </para>
    /// </summary>
    public interface ISlashSpanCloseEstimator
    {
        bool ShouldClose(double latchedAt, double nowSeconds);
    }

    /// <summary>
    /// The source slash plane of a stroke (DESIGN 19.1): each consecutive pair of accepted samples contributes the
    /// cross product of the later sample's blade axis with the movement between them, folded onto that sample's side
    /// normal; the plane passes through the begin cut sample point and faces the newest sample's side.
    /// </summary>
    public static class SlashStrokePlane
    {
        private const float MinDerivedVectorLengthSquared = 1e-12f;

        public static bool TryEstimate(ReadOnlySpan<EvaluatedBladePose> accepted, out Plane plane)
        {
            plane = default;
            int count = accepted.Length;
            if (count < 2)
            {
                return false;
            }

            Vector3 sum = Vector3.zero;
            for (int i = 1; i < count; i++)
            {
                Vector3 movement = accepted[i].CutSamplePosition - accepted[i - 1].CutSamplePosition;
                Vector3 candidate = Vector3.Cross(accepted[i].BladeAxis, movement);
                if (!SlashMath.IsFinite(candidate))
                {
                    return false;
                }

                if (Vector3.Dot(candidate, accepted[i].SideNormal) < 0f)
                {
                    candidate = -candidate;
                }

                sum += candidate;
            }

            if (!SlashMath.IsFinite(sum))
            {
                return false;
            }

            float lengthSquared = sum.sqrMagnitude;
            if (!float.IsFinite(lengthSquared) || lengthSquared <= MinDerivedVectorLengthSquared)
            {
                return false;
            }

            float length = Mathf.Sqrt(lengthSquared);
            Vector3 normal = new Vector3(sum.x / length, sum.y / length, sum.z / length);
            if (!SlashMath.IsFinite(normal))
            {
                return false;
            }

            if (Vector3.Dot(normal, accepted[count - 1].SideNormal) < 0f)
            {
                normal = -normal;
            }

            Vector3 origin = accepted[0].CutSamplePosition;
            if (!SlashMath.IsFinite(origin))
            {
                return false;
            }

            // A finite normal and a finite point can still give a distance that overflows, so the constructed plane
            // is what gets checked.
            plane = new Plane(normal, origin);
            if (!SlashMath.IsFinite(plane.normal) || !float.IsFinite(plane.distance))
            {
                plane = default;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// The adopted latch (Phase 0.55): ready once the emitter chord from the stroke's begin sample to its newest one has
    /// reached a fixed distance.
    /// </summary>
    public sealed class EmitterChordLatch : ISlashLatchEstimator
    {
        public EmitterChordLatch(float chordMetres)
        {
            if (!IsUsable(chordMetres))
            {
                throw new ArgumentOutOfRangeException(nameof(chordMetres));
            }

            ChordMetres = chordMetres;
        }

        public float ChordMetres { get; }

        public static bool IsUsable(float chordMetres)
        {
            return float.IsFinite(chordMetres) && chordMetres > 0f;
        }

        public bool IsLatchReady(ReadOnlySpan<EvaluatedBladePose> accepted, in SlashBlade blade)
        {
            if (accepted.Length < 2)
            {
                return false;
            }

            Vector3 chord = blade.Emitter(accepted[accepted.Length - 1]) - blade.Emitter(accepted[0]);
            if (!SlashMath.IsFinite(chord))
            {
                return false;
            }

            float lengthSquared = chord.sqrMagnitude;
            return float.IsFinite(lengthSquared) && lengthSquared >= ChordMetres * ChordMetres;
        }
    }

    /// <summary>
    /// The adopted frame (DESIGN 19.1.5.1, the fixed 150° span axis adopted 2026-09-18): the travel axis is the begin
    /// sample's blade tip direction on the plane, the span axis is at 150° to it on the side the emitter chord shows,
    /// and the initial span is the chord's length.
    /// </summary>
    public sealed class FixedSpanAngleFrame : ISlashFrameEstimator
    {
        public static readonly FixedSpanAngleFrame Instance = new FixedSpanAngleFrame();

        private const float MinDerivedVectorLengthSquared = 1e-12f;

        public bool TryEstimate(ReadOnlySpan<EvaluatedBladePose> accepted, in SlashBlade blade, out SlashFrame frame)
        {
            frame = default;
            if (accepted.Length < 2 || !SlashStrokePlane.TryEstimate(accepted, out Plane candidate))
            {
                return false;
            }

            EvaluatedBladePose begin = accepted[0];
            Vector3 projectedBegin = candidate.ClosestPointOnPlane(blade.Emitter(begin));
            Vector3 projectedLatest = candidate.ClosestPointOnPlane(blade.Emitter(accepted[accepted.Length - 1]));
            if (!SlashMath.IsFinite(projectedBegin) || !SlashMath.IsFinite(projectedLatest))
            {
                return false;
            }

            if (!SlashMath.TryProjectOntoPlane(begin.BladeAxis, candidate.normal, out Vector3 travel))
            {
                return false;
            }

            Vector3 chord = projectedLatest - projectedBegin;
            if (!SlashMath.IsFinite(chord))
            {
                return false;
            }

            float chordLengthSquared = chord.sqrMagnitude;
            if (!float.IsFinite(chordLengthSquared) || chordLengthSquared <= MinDerivedVectorLengthSquared)
            {
                return false;
            }

            float chordLength = Mathf.Sqrt(chordLengthSquared);
            if (!float.IsFinite(chordLength) || !(chordLength > 0f))
            {
                return false;
            }

            Vector3 chordDirection = new Vector3(chord.x / chordLength, chord.y / chordLength, chord.z / chordLength);
            float side = Vector3.Dot(candidate.normal, Vector3.Cross(travel, chordDirection));
            if (!SlashMath.IsFinite(chordDirection) || !float.IsFinite(side) || side == 0f)
            {
                return false;
            }

            // Adopted fixed-angle frame: cos(150) T + sign(side) sin(150) (N x T).
            Vector3 fixedSpan = -0.8660254037844386f * travel
                + (side > 0f ? 0.5f : -0.5f) * Vector3.Cross(candidate.normal, travel);
            if (!SlashMath.TryProjectOntoPlane(fixedSpan, candidate.normal, out fixedSpan))
            {
                return false;
            }

            frame = new SlashFrame(candidate, projectedBegin, projectedLatest, travel, fixedSpan, chordLength);
            return true;
        }
    }

    /// <summary>
    /// The adopted span candidate (DESIGN 19.1.5.1): where the fixed span line through A meets the guide ray. The
    /// signed denominator is what the division uses; only its magnitude decides whether the two are too near parallel.
    /// A candidate is taken only when they are not, and the meeting point lies ahead on both.
    /// </summary>
    public sealed class GuideRaySpanCandidate : ISlashSpanCandidateEstimator
    {
        /// <summary>The provisional near-parallel threshold on the signed denominator (the sine of the angle).</summary>
        public const float DefaultNearParallelDenominator = 1e-3f;

        public static readonly GuideRaySpanCandidate Default = new GuideRaySpanCandidate(DefaultNearParallelDenominator);

        public GuideRaySpanCandidate(float nearParallelDenominator)
        {
            if (!float.IsFinite(nearParallelDenominator) || !(nearParallelDenominator > 0f))
            {
                throw new ArgumentOutOfRangeException(nameof(nearParallelDenominator));
            }

            NearParallelDenominator = nearParallelDenominator;
        }

        public float NearParallelDenominator { get; }

        public SlashSpanCandidate Evaluate(Vector3 planeNormal, Vector3 a, Vector3 spanAxis, Vector3 guideOrigin, Vector3 guideDirection)
        {
            bool termsFinite = TryEvaluateTerms(
                planeNormal, a, spanAxis, guideOrigin, guideDirection, out float r, out float q, out float denominator);

            // The raw term is kept even when the candidate is refused; admission depends on the terms and usability.
            return new SlashSpanCandidate(termsFinite && IsUsable(r, q, denominator), r, q, denominator, termsFinite);
        }

        /// <summary>Whether evaluated terms make a candidate this takes.</summary>
        public bool IsUsable(float r, float q, float denominator)
        {
            return Mathf.Abs(denominator) > NearParallelDenominator && r >= 0f && q >= 0f;
        }

        /// <summary>
        /// The terms for a span line through <paramref name="a"/> and a guide ray on a plane, deciding nothing: r along
        /// the span axis, q along the guide, and the signed denominator. False only when a term is not finite.
        /// </summary>
        public static bool TryEvaluateTerms(
            Vector3 planeNormal,
            Vector3 a,
            Vector3 spanAxis,
            Vector3 guideOrigin,
            Vector3 guideDirection,
            out float r,
            out float q,
            out float denominator)
        {
            r = 0f;
            q = 0f;
            denominator = Vector3.Dot(planeNormal, Vector3.Cross(spanAxis, guideDirection));

            Vector3 fromA = guideOrigin - a;
            if (!float.IsFinite(denominator) || !SlashMath.IsFinite(fromA))
            {
                return false;
            }

            float rNumerator = Vector3.Dot(planeNormal, Vector3.Cross(fromA, guideDirection));
            float qNumerator = Vector3.Dot(planeNormal, Vector3.Cross(fromA, spanAxis));
            if (!float.IsFinite(rNumerator) || !float.IsFinite(qNumerator))
            {
                return false;
            }

            r = rNumerator / denominator;
            q = qNumerator / denominator;
            return float.IsFinite(r) && float.IsFinite(q);
        }
    }

    /// <summary>
    /// The adopted span close (Phase 0.55): a fixed capture timeout after the latch. It has to be finite and positive; it
    /// may be as long as the wave's lifetime or longer, in which case the wave expires with its span still open.
    /// </summary>
    public sealed class CaptureTimeoutSpanClose : ISlashSpanCloseEstimator
    {
        public CaptureTimeoutSpanClose(float timeoutSeconds)
        {
            if (!float.IsFinite(timeoutSeconds) || !(timeoutSeconds > 0f))
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            }

            TimeoutSeconds = timeoutSeconds;
        }

        public float TimeoutSeconds { get; }

        public bool ShouldClose(double latchedAt, double nowSeconds)
        {
            return nowSeconds - latchedAt >= TimeoutSeconds;
        }
    }

    internal static class SlashMath
    {
        private const float MinProjectedLengthSquared = 1e-12f;

        internal static bool IsFinite(Vector3 v)
        {
            return float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        }

        internal static bool TryProjectOntoPlane(Vector3 direction, Vector3 normal, out Vector3 result)
        {
            result = default;

            Vector3 inPlane = direction - Vector3.Dot(direction, normal) * normal;
            if (!IsFinite(inPlane))
            {
                return false;
            }

            float lengthSquared = inPlane.sqrMagnitude;
            if (!float.IsFinite(lengthSquared) || lengthSquared <= MinProjectedLengthSquared)
            {
                return false;
            }

            float length = Mathf.Sqrt(lengthSquared);
            result = new Vector3(inPlane.x / length, inPlane.y / length, inPlane.z / length);
            return IsFinite(result);
        }
    }
}
