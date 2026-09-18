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
            public float SpanCaptureTimeout;

            // NaN until the span closes; there is no separate open/closed flag
            // to keep in step with it.
            public double SpanClosedAt;
            public Vector3 FrozenGuideOrigin;
            public Vector3 FrozenGuideDirection;

            // What the last candidate evaluation of this wave saw, so that a
            // development readout or a capture can report it. 19.1.12 asks for
            // the live and frozen guide, r, q and the signed denominator with
            // its near-parallel judgement to be observable; only the frozen
            // ones could be read before, because the live terms were computed
            // inside the update and discarded. Nothing here decides anything:
            // the span still comes from the running maximum below.
            public double LastCandidateAt;
            public bool LastCandidateEvaluated;
            public bool LastCandidateFromFrozenGuide;
            public Vector3 LastGuideOrigin;
            public Vector3 LastGuideDirection;
            public float LastRawSpan;
            public float LastQ;
            public float LastDenominator;
            public bool LastTermsFinite;
            public bool LastCandidateUsable;
            public bool LastCandidateWidenedSpan;
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
        /// A wave whose capture window has run out freezes the guide it was
        /// given in that update and stops looking at the current pose; the
        /// update it closes on still keeps the candidate that guide produced.
        /// Closing does not end the wave or fix its span: the frozen guide goes
        /// on giving candidates, and a wider one is still taken.
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

                // While the span is open the current pose steers it; once
                // closed the guide frozen at that moment does, and the current
                // pose is ignored. Either way the guide is projected once and
                // the candidate evaluated once.
                bool hasCandidate = false;
                float rawSpan = 0f;
                float candidateQ = 0f;
                float candidateDenominator = 0f;
                bool candidateTermsFinite = false;
                bool candidateEvaluated = false;
                bool candidateFromFrozenGuide = false;
                Vector3 candidateGuideOrigin = default;
                Vector3 candidateGuideDirection = default;
                if (double.IsNaN(waves[i].SpanClosedAt))
                {
                    if (hasGuide
                        && TryProjectGuide(waves[i], guideEmitter, guideBladeAxis,
                            out Vector3 liveOrigin, out Vector3 liveDirection))
                    {
                        hasCandidate = TryEvaluateRawSpanCandidate(
                            waves[i], a, liveOrigin, liveDirection,
                            out rawSpan, out candidateQ, out candidateDenominator, out candidateTermsFinite);
                        candidateEvaluated = true;
                        candidateGuideOrigin = liveOrigin;
                        candidateGuideDirection = liveDirection;

                        // Closing keeps this update's live candidate; it just
                        // stops later updates from taking a new guide.
                        if (elapsed >= waves[i].SpanCaptureTimeout)
                        {
                            waves[i].FrozenGuideOrigin = liveOrigin;
                            waves[i].FrozenGuideDirection = liveDirection;
                            waves[i].SpanClosedAt = nowSeconds;
                        }
                    }
                }
                else
                {
                    hasCandidate = TryEvaluateRawSpanCandidate(
                        waves[i], a, waves[i].FrozenGuideOrigin, waves[i].FrozenGuideDirection,
                        out rawSpan, out candidateQ, out candidateDenominator, out candidateTermsFinite);
                    candidateEvaluated = true;
                    candidateFromFrozenGuide = true;
                    candidateGuideOrigin = waves[i].FrozenGuideOrigin;
                    candidateGuideDirection = waves[i].FrozenGuideDirection;
                }

                float acceptedSpan = waves[i].AcceptedSpan;
                bool widened = hasCandidate && rawSpan > acceptedSpan && CanCarrySpan(waves[i], a, rawSpan);
                if (widened)
                {
                    acceptedSpan = rawSpan;
                }

                // Written after the decision and read by nothing that decides.
                waves[i].LastCandidateAt = nowSeconds;
                waves[i].LastCandidateEvaluated = candidateEvaluated;
                waves[i].LastCandidateFromFrozenGuide = candidateFromFrozenGuide;
                waves[i].LastGuideOrigin = candidateGuideOrigin;
                waves[i].LastGuideDirection = candidateGuideDirection;
                waves[i].LastRawSpan = rawSpan;
                waves[i].LastQ = candidateQ;
                waves[i].LastDenominator = candidateDenominator;
                waves[i].LastTermsFinite = candidateTermsFinite;
                waves[i].LastCandidateUsable = hasCandidate;
                waves[i].LastCandidateWidenedSpan = widened;

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
        // The emission control point and blade tip direction put onto this
        // wave's plane: the guide it sees. Done once per wave per update and
        // shared by the candidate and the freeze.
        private static bool TryProjectGuide(
            in Wave wave,
            Vector3 guideEmitter,
            Vector3 guideBladeAxis,
            out Vector3 guideOrigin,
            out Vector3 guideDirection)
        {
            guideOrigin = default;
            guideDirection = default;

            if (!IsFinite(guideEmitter) || !IsFinite(guideBladeAxis))
            {
                return false;
            }

            Vector3 normal = wave.SourceSlashPlane.normal;
            if (!IsFinite(normal) || !float.IsFinite(wave.SourceSlashPlane.distance))
            {
                return false;
            }

            guideOrigin = wave.SourceSlashPlane.ClosestPointOnPlane(guideEmitter);
            if (!IsFinite(guideOrigin))
            {
                return false;
            }

            return TryProjectDirectionOntoPlane(guideBladeAxis, normal, out guideDirection);
        }

        private static bool TryEvaluateRawSpanCandidate(
            in Wave wave,
            Vector3 a,
            Vector3 guideOrigin,
            Vector3 guideDirection,
            out float rawSpan,
            out float q,
            out float denominator,
            out bool termsFinite)
        {
            rawSpan = 0f;
            q = 0f;
            denominator = 0f;
            termsFinite = false;

            if (!IsFinite(a) || !IsFinite(guideOrigin) || !IsFinite(guideDirection))
            {
                return false;
            }

            Vector3 normal = wave.SourceSlashPlane.normal;
            if (!IsFinite(normal) || !float.IsFinite(wave.SourceSlashPlane.distance))
            {
                return false;
            }

            termsFinite = TryEvaluateRawSpanTerms(
                normal, a, wave.SpanAxis, guideOrigin, guideDirection, out float r, out q, out denominator);
            // Preserve the raw term even when the candidate is refused.
            // Admission still depends on termsFinite and IsUsableRawSpan.
            rawSpan = r;
            if (!termsFinite || !IsUsableRawSpan(r, q, denominator))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// The terms of the raw span candidate for a span line through
        /// <paramref name="a"/> and a guide ray on a plane, without deciding
        /// anything: r along the span axis, q along the guide, and the signed
        /// denominator. False only when a term is not finite. The candidate
        /// above is built from this, and development readouts show it.
        /// </summary>
        internal static bool TryEvaluateRawSpanTerms(
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
            if (!float.IsFinite(denominator) || !IsFinite(fromA))
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

        /// <summary>
        /// Whether evaluated terms make a candidate the store takes: the span
        /// axis and guide are not near parallel, and the meeting point lies
        /// ahead on both.
        /// </summary>
        internal static bool IsUsableRawSpan(float r, float q, float denominator)
        {
            return Mathf.Abs(denominator) > NearParallelDenominator && r >= 0f && q >= 0f;
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
        ///
        /// <paramref name="spanCaptureTimeoutSeconds"/> is how long the span
        /// keeps following the blade: after that the guide is frozen where it
        /// was and the live pose stops steering the span. It is copied into
        /// the wave, so a later change to the caller's value only reaches
        /// waves latched after it. It has to be positive and shorter than the
        /// wave's lifetime.
        /// </summary>
        internal bool TryLatch(
            double nowSeconds,
            in Plane sourceSlashPlane,
            Vector3 beginEmitter,
            Vector3 latestEmitter,
            Vector3 travelAxis,
            Vector3 spanAxis,
            float acceptedSpan,
            float spanCaptureTimeoutSeconds)
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
                || !float.IsFinite(WaveLifetimeSeconds) || !(WaveLifetimeSeconds > 0f)
                || !float.IsFinite(spanCaptureTimeoutSeconds) || !(spanCaptureTimeoutSeconds > 0f)
                || !(spanCaptureTimeoutSeconds < WaveLifetimeSeconds))
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
                SpanCaptureTimeout = spanCaptureTimeoutSeconds,
                SpanClosedAt = double.NaN,
                FrozenGuideOrigin = default,
                FrozenGuideDirection = default,
                LastCandidateAt = double.NaN,
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

        /// <summary>
        /// The moment a wave's span closed and the guide it froze then, by
        /// value. False while the span is still open, or for an index outside
        /// the live waves -- whether it is closed is only ever the presence of
        /// that moment.
        /// </summary>
        internal bool TryGetWaveSpanClose(
            int index,
            out double spanClosedAt,
            out Vector3 frozenGuideOrigin,
            out Vector3 frozenGuideDirection)
        {
            spanClosedAt = 0.0;
            frozenGuideOrigin = default;
            frozenGuideDirection = default;

            if (index < 0 || index >= count || double.IsNaN(waves[index].SpanClosedAt))
            {
                return false;
            }

            spanClosedAt = waves[index].SpanClosedAt;
            frozenGuideOrigin = waves[index].FrozenGuideOrigin;
            frozenGuideDirection = waves[index].FrozenGuideDirection;
            return true;
        }

        /// <summary>
        /// Reads back what this wave's last candidate evaluation saw: which
        /// guide it used, the terms of the intersection, whether they were
        /// usable and whether they widened the span. Observation only -- the
        /// numbers are the ones the update already used, recorded after it had
        /// decided. False when the index is outside the live waves or the wave
        /// has not been through an update since it latched (a wave latched in
        /// this update has not).
        /// </summary>
        internal bool TryGetWaveCandidate(
            int index,
            out double candidateAt,
            out bool evaluated,
            out bool fromFrozenGuide,
            out Vector3 guideOrigin,
            out Vector3 guideDirection,
            out float rawSpan,
            out float q,
            out float denominator,
            out bool termsFinite,
            out bool usable,
            out bool widenedSpan)
        {
            candidateAt = 0.0;
            evaluated = false;
            fromFrozenGuide = false;
            guideOrigin = default;
            guideDirection = default;
            rawSpan = 0f;
            q = 0f;
            denominator = 0f;
            termsFinite = false;
            usable = false;
            widenedSpan = false;

            if (index < 0 || index >= count || double.IsNaN(waves[index].LastCandidateAt))
            {
                return false;
            }

            candidateAt = waves[index].LastCandidateAt;
            evaluated = waves[index].LastCandidateEvaluated;
            fromFrozenGuide = waves[index].LastCandidateFromFrozenGuide;
            guideOrigin = waves[index].LastGuideOrigin;
            guideDirection = waves[index].LastGuideDirection;
            rawSpan = waves[index].LastRawSpan;
            q = waves[index].LastQ;
            denominator = waves[index].LastDenominator;
            termsFinite = waves[index].LastTermsFinite;
            usable = waves[index].LastCandidateUsable;
            widenedSpan = waves[index].LastCandidateWidenedSpan;
            return true;
        }

        /// <summary>The near-parallel threshold the candidate judgement uses, for readouts.</summary>
        internal static float NearParallelDenominatorThreshold => NearParallelDenominator;

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
