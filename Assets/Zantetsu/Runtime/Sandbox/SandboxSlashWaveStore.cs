using System;
using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The slash waves one sandbox katana has published, and the whole of
    /// their state. Storage is a fixed number of slots allocated once: a latch
    /// with no slot free is simply not published, and nothing grows, queues or
    /// gets registered anywhere to make room for it.
    ///
    /// A wave holds only what Latch fixes: when it latched, the plane it was
    /// cut on, its origin and axes, the span it started with, the speed and
    /// lifetime it was given, and its previous and current segments -- which
    /// at latch are the same degenerate sweep. When it runs out follows from
    /// the latch time and the lifetime, so it is worked out rather than kept,
    /// and so does where it has travelled to: every position comes from the
    /// latch snapshot and the time asked about, never from adding to the last
    /// one, so a wave that missed an update is in the right place anyway.
    ///
    /// Waves never leave: they are read back a field at a time, so no caller
    /// can hold the array or write through it.
    /// </summary>
    internal sealed class SandboxSlashWaveStore
    {
        /// <summary>How many waves can be alive at once.</summary>
        internal const int Capacity = 4;

        /// <summary>Provisional wave speed in m/s, fixed at latch.</summary>
        internal const float WaveSpeed = 12f;

        /// <summary>Provisional wave lifetime in seconds, fixed at latch.</summary>
        internal const float WaveLifetimeSeconds = 1.5f;

        // Provisional near-parallel threshold on the signed denominator, which
        // for unit in-plane vectors is the sine of the angle between the span
        // axis and the guide. Below this the intersection is too far out to
        // mean anything. Phase 0.55 tunes it.
        private const float NearParallelDenominator = 1e-3f;

        // A projected guide direction shorter than this has no direction left.
        private const float MinGuideLengthSquared = 1e-12f;

        private struct Wave
        {
            public double LatchedAt;
            public Plane SourceSlashPlane;
            public Vector3 WaveOrigin;
            public Vector3 TravelAxis;
            public Vector3 SpanAxis;
            public float AcceptedSpan;
            public Vector3 PreviousSegmentStart;
            public Vector3 PreviousSegmentEnd;
            public Vector3 CurrentSegmentStart;
            public Vector3 CurrentSegmentEnd;
            public float Speed;
            public float LifetimeSeconds;
        }

        private readonly Wave[] waves = new Wave[Capacity];
        private int count;

        internal int Count => count;

        /// <summary>
        /// Drops every wave whose lifetime has run out at this time, freeing
        /// its slot. Called before anything else in an update, so a latch in
        /// the same update can use what was freed.
        /// </summary>
        internal void RemoveExpired(double nowSeconds)
        {
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
            {
                return;
            }

            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                if (nowSeconds >= waves[i].LatchedAt + waves[i].LifetimeSeconds)
                {
                    continue;
                }

                if (kept != i)
                {
                    waves[kept] = waves[i];
                }

                kept++;
            }

            for (int i = kept; i < count; i++)
            {
                waves[i] = default;
            }

            count = kept;
        }

        /// <summary>
        /// Moves the first <paramref name="waveCount"/> waves to where they are
        /// at this time, and lets each take a wider accepted span from the live
        /// guide if the current blade pose gives it one. A(t) comes from the
        /// latch snapshot alone -- origin plus speed times the time since latch,
        /// along the travel axis -- and B(t) is the accepted span further along
        /// the span axis, so nothing accumulates and no travelled distance is
        /// kept. The segment a wave was showing becomes its previous one.
        ///
        /// The count is the caller's way of saying which waves were already
        /// there when the update began; anything published since is left at its
        /// initial segment without needing a flag or an id to mark it.
        ///
        /// Each wave projects the guide onto its own plane. The span candidate
        /// is 19.1.5.1's intersection of the fixed span line through A with the
        /// guide ray, and only a valid candidate wider than the accepted span
        /// is taken -- so the accepted span is a running maximum and never
        /// shrinks. A rejected candidate costs the wave nothing: it still flies.
        ///
        /// A wave is left exactly as it was when the time is not usable for it:
        /// not finite, before its latch, behind where the wave has already
        /// travelled to, or unable to give finite ends. Nothing is clamped,
        /// expired early, or marked as recovering.
        /// </summary>
        internal void Advance(
            double nowSeconds,
            int waveCount,
            bool hasGuide,
            Vector3 guideEmitter,
            Vector3 guideBladeAxis)
        {
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
            {
                return;
            }

            int limit = waveCount < count ? waveCount : count;
            for (int i = 0; i < limit; i++)
            {
                double elapsed = nowSeconds - waves[i].LatchedAt;
                if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0.0)
                {
                    continue;
                }

                double travelDistance = waves[i].Speed * elapsed;
                if (double.IsNaN(travelDistance) || double.IsInfinity(travelDistance)
                    || Math.Abs(travelDistance) > float.MaxValue)
                {
                    continue;
                }

                Vector3 a = waves[i].WaveOrigin + waves[i].TravelAxis * (float)travelDistance;
                if (!IsFinite(a))
                {
                    continue;
                }

                // A time that is after the latch can still be before the last
                // one this wave was shown at -- a sample the pose history
                // refuses, say. A wave never travels backwards, so leave it
                // exactly where it is rather than clamping or correcting.
                if (Vector3.Dot(a - waves[i].CurrentSegmentStart, waves[i].TravelAxis) < 0f)
                {
                    continue;
                }

                float acceptedSpan = waves[i].AcceptedSpan;
                if (hasGuide
                    && TryEvaluateRawSpanCandidate(waves[i], a, guideEmitter, guideBladeAxis, out float rawSpan)
                    && rawSpan > acceptedSpan
                    && CanCarrySpan(waves[i], a, rawSpan))
                {
                    acceptedSpan = rawSpan;
                }

                Vector3 b = a + waves[i].SpanAxis * acceptedSpan;
                if (!IsFinite(b))
                {
                    continue;
                }

                waves[i].AcceptedSpan = acceptedSpan;
                waves[i].PreviousSegmentStart = waves[i].CurrentSegmentStart;
                waves[i].PreviousSegmentEnd = waves[i].CurrentSegmentEnd;
                waves[i].CurrentSegmentStart = a;
                waves[i].CurrentSegmentEnd = b;
            }
        }

        // 19.1.5.1's first candidate: where the fixed span line through A meets
        // the live guide ray. The signed denominator is what the division uses;
        // only its magnitude decides whether the two are too near parallel.
        private static bool TryEvaluateRawSpanCandidate(
            in Wave wave,
            Vector3 a,
            Vector3 guideEmitter,
            Vector3 guideBladeAxis,
            out float rawSpan)
        {
            rawSpan = 0f;

            if (!IsFinite(guideEmitter) || !IsFinite(guideBladeAxis) || !IsFinite(a))
            {
                return false;
            }

            Vector3 normal = wave.SourceSlashPlane.normal;
            if (!IsFinite(normal) || !float.IsFinite(wave.SourceSlashPlane.distance))
            {
                return false;
            }

            Vector3 guideOrigin = wave.SourceSlashPlane.ClosestPointOnPlane(guideEmitter);
            if (!IsFinite(guideOrigin))
            {
                return false;
            }

            if (!TryProjectDirectionOntoPlane(guideBladeAxis, normal, out Vector3 guideDirection))
            {
                return false;
            }

            float denominator = Vector3.Dot(normal, Vector3.Cross(wave.SpanAxis, guideDirection));
            if (!float.IsFinite(denominator) || Mathf.Abs(denominator) <= NearParallelDenominator)
            {
                return false;
            }

            Vector3 fromA = guideOrigin - a;
            if (!IsFinite(fromA))
            {
                return false;
            }

            float rNumerator = Vector3.Dot(normal, Vector3.Cross(fromA, guideDirection));
            float qNumerator = Vector3.Dot(normal, Vector3.Cross(fromA, wave.SpanAxis));
            if (!float.IsFinite(rNumerator) || !float.IsFinite(qNumerator))
            {
                return false;
            }

            float r = rNumerator / denominator;
            float q = qNumerator / denominator;
            if (!float.IsFinite(r) || !float.IsFinite(q) || r < 0f || q < 0f)
            {
                return false;
            }

            rawSpan = r;
            return true;
        }

        // A wider span is only worth taking if the wave can still show both
        // ends now and at the end of the life it was given.
        private static bool CanCarrySpan(in Wave wave, Vector3 a, float span)
        {
            if (!float.IsFinite(span) || !IsFinite(a + wave.SpanAxis * span))
            {
                return false;
            }

            double travelDistance = wave.Speed * (double)wave.LifetimeSeconds;
            if (double.IsNaN(travelDistance) || double.IsInfinity(travelDistance)
                || Math.Abs(travelDistance) > float.MaxValue)
            {
                return false;
            }

            Vector3 travelEnd = wave.WaveOrigin + wave.TravelAxis * (float)travelDistance;
            return IsFinite(travelEnd) && IsFinite(travelEnd + wave.SpanAxis * span);
        }

        private static bool TryProjectDirectionOntoPlane(Vector3 direction, Vector3 normal, out Vector3 result)
        {
            result = default;

            Vector3 inPlane = direction - Vector3.Dot(direction, normal) * normal;
            if (!IsFinite(inPlane))
            {
                return false;
            }

            float lengthSquared = inPlane.sqrMagnitude;
            if (!float.IsFinite(lengthSquared) || lengthSquared <= MinGuideLengthSquared)
            {
                return false;
            }

            float length = Mathf.Sqrt(lengthSquared);
            result = new Vector3(inPlane.x / length, inPlane.y / length, inPlane.z / length);
            return IsFinite(result);
        }

        /// <summary>
        /// Publishes one wave from a latch. Returns false, changing nothing at
        /// all, when no slot is free or when the state it was handed cannot
        /// make a wave that stays finite for its whole lifetime.
        /// </summary>
        internal bool TryLatch(
            double nowSeconds,
            in Plane sourceSlashPlane,
            Vector3 beginEmitter,
            Vector3 latestEmitter,
            Vector3 travelAxis,
            Vector3 spanAxis,
            float acceptedSpan)
        {
            if (count >= Capacity)
            {
                return false;
            }

            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
            {
                return false;
            }

            if (!IsFinite(sourceSlashPlane.normal) || !float.IsFinite(sourceSlashPlane.distance))
            {
                return false;
            }

            if (!IsFinite(beginEmitter) || !IsFinite(latestEmitter) || !IsFinite(travelAxis) || !IsFinite(spanAxis))
            {
                return false;
            }

            if (!float.IsFinite(acceptedSpan) || acceptedSpan < 0f)
            {
                return false;
            }

            if (!float.IsFinite(WaveSpeed) || !(WaveSpeed > 0f)
                || !float.IsFinite(WaveLifetimeSeconds) || !(WaveLifetimeSeconds > 0f))
            {
                return false;
            }

            double expiresAt = nowSeconds + WaveLifetimeSeconds;
            if (double.IsNaN(expiresAt) || double.IsInfinity(expiresAt) || !(expiresAt > nowSeconds))
            {
                return false;
            }

            // Both ends of the segment have to survive the whole lifetime, not
            // just A: a wave whose B end overflows on the way cannot be
            // carried to the end of the life it was just given.
            Vector3 travel = travelAxis * (WaveSpeed * WaveLifetimeSeconds);
            if (!IsFinite(travel) || !IsFinite(beginEmitter + travel) || !IsFinite(latestEmitter + travel))
            {
                return false;
            }

            waves[count] = new Wave
            {
                LatchedAt = nowSeconds,
                SourceSlashPlane = sourceSlashPlane,
                WaveOrigin = beginEmitter,
                TravelAxis = travelAxis,
                SpanAxis = spanAxis,
                AcceptedSpan = acceptedSpan,
                PreviousSegmentStart = beginEmitter,
                PreviousSegmentEnd = latestEmitter,
                CurrentSegmentStart = beginEmitter,
                CurrentSegmentEnd = latestEmitter,
                Speed = WaveSpeed,
                LifetimeSeconds = WaveLifetimeSeconds,
            };
            count++;
            return true;
        }

        /// <summary>
        /// Reads one wave back by value. False when the index is outside the
        /// live waves.
        /// </summary>
        internal bool TryGetWave(
            int index,
            out double latchedAt,
            out Plane sourceSlashPlane,
            out Vector3 waveOrigin,
            out Vector3 travelAxis,
            out Vector3 spanAxis,
            out float acceptedSpan,
            out Vector3 previousSegmentStart,
            out Vector3 previousSegmentEnd,
            out Vector3 currentSegmentStart,
            out Vector3 currentSegmentEnd)
        {
            latchedAt = 0.0;
            sourceSlashPlane = default;
            waveOrigin = default;
            travelAxis = default;
            spanAxis = default;
            acceptedSpan = 0f;
            previousSegmentStart = default;
            previousSegmentEnd = default;
            currentSegmentStart = default;
            currentSegmentEnd = default;

            if (index < 0 || index >= count)
            {
                return false;
            }

            Wave wave = waves[index];
            latchedAt = wave.LatchedAt;
            sourceSlashPlane = wave.SourceSlashPlane;
            waveOrigin = wave.WaveOrigin;
            travelAxis = wave.TravelAxis;
            spanAxis = wave.SpanAxis;
            acceptedSpan = wave.AcceptedSpan;
            previousSegmentStart = wave.PreviousSegmentStart;
            previousSegmentEnd = wave.PreviousSegmentEnd;
            currentSegmentStart = wave.CurrentSegmentStart;
            currentSegmentEnd = wave.CurrentSegmentEnd;
            return true;
        }

        /// <summary>Ends every wave at once, without freeing the storage.</summary>
        internal void Clear()
        {
            Array.Clear(waves, 0, waves.Length);
            count = 0;
        }

        private static bool IsFinite(Vector3 v)
        {
            return float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        }
    }
}
