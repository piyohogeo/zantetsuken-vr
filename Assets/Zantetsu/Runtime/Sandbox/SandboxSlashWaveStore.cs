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
        /// Moves every live wave to where it is at this time. A(t) comes from
        /// the latch snapshot alone -- origin plus speed times the time since
        /// latch, along the travel axis -- and B(t) is a span further along
        /// the span axis, so nothing accumulates and no travelled distance is
        /// kept. The segment it was showing becomes the previous one.
        ///
        /// A wave is left exactly as it was when the time is not usable for
        /// it: not finite, before its latch, or unable to give finite ends.
        /// Nothing is clamped, expired early, or marked as recovering.
        /// </summary>
        internal void Advance(double nowSeconds)
        {
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
            {
                return;
            }

            for (int i = 0; i < count; i++)
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
                Vector3 b = a + waves[i].SpanAxis * waves[i].AcceptedSpan;
                if (!IsFinite(a) || !IsFinite(b))
                {
                    continue;
                }

                waves[i].PreviousSegmentStart = waves[i].CurrentSegmentStart;
                waves[i].PreviousSegmentEnd = waves[i].CurrentSegmentEnd;
                waves[i].CurrentSegmentStart = a;
                waves[i].CurrentSegmentEnd = b;
            }
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
