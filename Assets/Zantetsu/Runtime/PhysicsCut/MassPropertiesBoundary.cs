using System;
using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>Why <see cref="MassPropertiesBoundary"/> did not give a body the mass properties asked.</summary>
    public enum MassPropertiesRefusal
    {
        None = 0,

        /// <summary>There is no body.</summary>
        NoBody = 1,

        /// <summary>The body's automatic centre of mass or inertia is on: a mass written to it would recompute the inertia, which a refusal could not take back.</summary>
        AutomaticMassProperties = 2,

        /// <summary>The mass asked is not a positive finite number.</summary>
        MassNotPositiveFinite = 3,

        /// <summary>The mass asked is not a positive finite float (it overflows to infinity or underflows to zero there).</summary>
        MassNotRepresentable = 4,

        /// <summary>A principal moment asked is not a positive finite number.</summary>
        InertiaNotPositiveFinite = 5,

        /// <summary>The centre of mass asked is not finite.</summary>
        CentreNotFinite = 6,

        /// <summary>The principal axes asked are not a rotation: not finite, or not of unit length (a zero quaternion included).</summary>
        AxesNotRotation = 7,

        /// <summary>The mass the body reads back after the write is not a positive finite number.</summary>
        EffectiveMassNotPositiveFinite = 8,

        /// <summary>A principal moment scaled to the mass the body holds is not a positive finite float.</summary>
        AppliedInertiaNotRepresentable = 9,

        /// <summary>Refused by <see cref="MassPropertiesBoundary.refuseAfterMassWriteForTest"/>, after the mass write (tests only).</summary>
        RefusedForTest = 10,
    }

    /// <summary>
    /// Where a cut side's body is given the mass properties computed for it (TL, 2026-10-03). The engine keeps a
    /// Rigidbody's mass at no less than its floor (1e-7 kg) and keeps an inertia tensor as written; a side whose computed
    /// mass is below the floor therefore held the floor's mass with the inertia of its own, far smaller mass -- an
    /// inertia far too small for the mass its body has (w5's piece 231: 1564 times), with which a light contact sent it to
    /// tens of thousands of rad/s in the limited replay of its capture, and through the floor.
    /// <para>
    /// The mass asked is written and read back; when the body holds another mass than the one asked, each principal
    /// moment asked is scaled to it, formed in double as inertia x effective / asked and judged only as the float that is
    /// written. The centre of mass and the principal axes are written as asked. The scale is worked out from the values
    /// asked on every application, so applying again never compounds it; when the body holds the mass asked, the inertia
    /// is written exactly as asked (bodies above the floor are given what they were given before). Nothing here asks for
    /// the automatic centre or inertia.
    /// </para>
    /// <para>
    /// <b>Refusal, not replacement.</b> Every value asked is checked before anything is written (the body, its automatic
    /// flags off, the mass as a double and as a float, each moment, the centre, the axes as a rotation). What can only be
    /// known from the engine -- the mass it keeps, and the moments scaled to it -- is checked after the mass is written; a
    /// refusal there writes the mass the body held before back, and nothing else has been written by then. No value is ever
    /// substituted: a refused application leaves the body's mass, centre, inertia, axes, flags and velocities as they were.
    /// </para>
    /// <para>
    /// <see cref="TryBegin"/> / <see cref="Commit"/> / <see cref="Revert"/> split one application around a point the
    /// caller cannot go back from (the Final handoff's switch): the begun mass is the only thing written until the commit,
    /// and the commit's writes are not refused by any check of the numbers. That is a statement about the numbers only: a
    /// commit can still fail the way any engine call can (an exception, a body destroyed in between).
    /// </para>
    /// <para>
    /// <b>Where it is used</b>: the cut sides (PhysicsOwnerSide: the build, the Provisional and Final publications, the
    /// Final handoff). <b>Not used</b> by the building hull groups and the fusion aggregate, which still write their
    /// bodies directly (with the old inconsistency below the floor); that is a separate unit.
    /// </para>
    /// <para>
    /// At the floor the mass is no longer conserved exactly: such a body holds more than its share of its parent's mass,
    /// and a cut of it later takes that floored mass as its parent's (the parent's Rigidbody mass is the authoritative
    /// total, DESIGN 7.2), so the increase is carried into every later split of that piece. The separation impulse is not
    /// touched here: its strength and its velocity change are both worked out from the mass asked, as before. With the
    /// inertia consistent, a sliver can still turn above the angular speed cap for a step or two in contact (456 rad/s in
    /// one replay); the cap and contact stability in general are not addressed here. A body whose mass and inertia are
    /// copied from another Rigidbody (a prepared character's actor, a placed prop's free source) takes that body's values
    /// as they are; this is not a check of mass properties that come from outside.
    /// </para>
    /// </summary>
    public static class MassPropertiesBoundary
    {
        /// <summary>A rotation's length may differ from one by this much (the axes the kernel gives are normalized).</summary>
        public const float AxesLengthTolerance = 1e-3f;

        /// <summary>What a body was asked to hold and what it holds after an application.</summary>
        public readonly struct Applied
        {
            public readonly double askedMass;

            /// <summary>The mass the body read back right after it was written (the engine keeps its floor, in the scene or out of it).</summary>
            public readonly float effectiveMass;

            /// <summary>effective / asked when the body holds another mass than the one asked; exactly 1 otherwise. A record: the moments are not computed from it.</summary>
            public readonly double inertiaScale;

            public readonly Vector3 askedInertia;

            /// <summary>
            /// The principal moments worked out and written: the value applied, not one read back -- out of the scene the
            /// body reads its inertia as zero.
            /// </summary>
            public readonly Vector3 appliedInertia;

            public Applied(double askedMass, float effectiveMass, double inertiaScale, Vector3 askedInertia, Vector3 appliedInertia)
            {
                this.askedMass = askedMass;
                this.effectiveMass = effectiveMass;
                this.inertiaScale = inertiaScale;
                this.askedInertia = askedInertia;
                this.appliedInertia = appliedInertia;
            }

            /// <summary>Whether the body holds another mass than the one asked (and its inertia was scaled to it).</summary>
            public bool Adjusted => inertiaScale != 1.0;
        }

        /// <summary>
        /// A begun application: the mass is written and every check has passed; the centre, inertia and axes are not yet
        /// written. Held by the caller and ended exactly once, by <see cref="Commit"/> or <see cref="Revert"/>.
        /// </summary>
        public struct Pending
        {
            internal Rigidbody body;
            internal float previousMass;
            internal Vector3 centre;
            internal Vector3 inertiaToWrite;
            internal Quaternion axes;
            internal Applied applied;
            internal bool open;

            /// <summary>Begun and not yet committed or reverted.</summary>
            public bool IsOpen => open;
        }

        /// <summary>The applications whose mass the body did not hold as asked (and whose inertia was scaled), since the session began.</summary>
        public static int AdjustedCount { get; private set; }

        /// <summary>The refused applications since the session began, and the reason of the last one.</summary>
        public static int RefusedCount { get; private set; }

        public static MassPropertiesRefusal LastRefusal { get; private set; }

        /// <summary>
        /// Tests only: asked after the mass is written; true refuses there, as an engine read-back that cannot be used
        /// would, so that the rollback and the callers' refusal paths can be exercised.
        /// </summary>
        internal static Func<Rigidbody, bool> refuseAfterMassWriteForTest;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            AdjustedCount = 0;
            RefusedCount = 0;
            LastRefusal = MassPropertiesRefusal.None;
        }

        /// <summary>
        /// The checks that need no body and write nothing: the mass as a double and as a float, each principal moment, the
        /// centre, the axes as a rotation.
        /// </summary>
        public static MassPropertiesRefusal Check(double mass, Vector3 centre, Vector3 inertia, Quaternion axes)
        {
            if (!(mass > 0.0) || double.IsInfinity(mass))
            {
                return MassPropertiesRefusal.MassNotPositiveFinite;
            }

            float asked = (float)mass;
            if (!(asked > 0f) || float.IsInfinity(asked))
            {
                return MassPropertiesRefusal.MassNotRepresentable;
            }

            if (!PositiveFinite(inertia.x) || !PositiveFinite(inertia.y) || !PositiveFinite(inertia.z))
            {
                return MassPropertiesRefusal.InertiaNotPositiveFinite;
            }

            if (!Finite(centre.x) || !Finite(centre.y) || !Finite(centre.z))
            {
                return MassPropertiesRefusal.CentreNotFinite;
            }

            if (!Finite(axes.x) || !Finite(axes.y) || !Finite(axes.z) || !Finite(axes.w))
            {
                return MassPropertiesRefusal.AxesNotRotation;
            }

            double length = Math.Sqrt((double)axes.x * axes.x + (double)axes.y * axes.y + (double)axes.z * axes.z + (double)axes.w * axes.w);
            if (!(Math.Abs(length - 1.0) <= AxesLengthTolerance))
            {
                return MassPropertiesRefusal.AxesNotRotation;
            }

            return MassPropertiesRefusal.None;
        }

        /// <summary>
        /// Gives <paramref name="body"/> the mass properties asked (<see cref="TryBegin"/> then <see cref="Commit"/>). On
        /// a refusal the body is left as it was and <paramref name="refusal"/> says why.
        /// </summary>
        public static bool TryApply(
            Rigidbody body, double mass, Vector3 centre, Vector3 inertia, Quaternion axes,
            out Applied applied, out MassPropertiesRefusal refusal)
        {
            if (!TryBegin(body, mass, centre, inertia, axes, out Pending pending, out refusal))
            {
                applied = default;
                return false;
            }

            applied = Commit(ref pending);
            return true;
        }

        /// <summary>
        /// Checks everything asked, writes the mass and reads it back, and works out the moments to write. On success only
        /// the mass has been written and <paramref name="pending"/> holds the rest; on a refusal the body is left as it was.
        /// </summary>
        public static bool TryBegin(
            Rigidbody body, double mass, Vector3 centre, Vector3 inertia, Quaternion axes,
            out Pending pending, out MassPropertiesRefusal refusal)
        {
            pending = default;
            refusal = body == null ? MassPropertiesRefusal.NoBody
                : body.automaticCenterOfMass || body.automaticInertiaTensor ? MassPropertiesRefusal.AutomaticMassProperties
                : Check(mass, centre, inertia, axes);
            if (refusal != MassPropertiesRefusal.None)
            {
                return Refused(refusal);
            }

            float asked = (float)mass;
            float previous = body.mass;
            body.mass = asked;
            float effective;
            Vector3 written = inertia;
            double scale = 1.0;
            try
            {
                effective = body.mass;
                if (!PositiveFinite(effective))
                {
                    body.mass = previous;
                    refusal = MassPropertiesRefusal.EffectiveMassNotPositiveFinite;
                    return Refused(refusal);
                }

                if (refuseAfterMassWriteForTest != null && refuseAfterMassWriteForTest(body))
                {
                    body.mass = previous;
                    refusal = MassPropertiesRefusal.RefusedForTest;
                    return Refused(refusal);
                }

                if (effective != asked)
                {
                    // Each moment scaled in double and judged only as the float that is written: no intermediate scale
                    // is rounded or refused on its own. With the checks above the product stays far inside the double
                    // range.
                    double e = effective;
                    float x = (float)(inertia.x * e / mass), y = (float)(inertia.y * e / mass), z = (float)(inertia.z * e / mass);
                    if (!PositiveFinite(x) || !PositiveFinite(y) || !PositiveFinite(z))
                    {
                        body.mass = previous;
                        refusal = MassPropertiesRefusal.AppliedInertiaNotRepresentable;
                        return Refused(refusal);
                    }

                    written = new Vector3(x, y, z);
                    scale = e / mass;
                }
            }
            catch
            {
                // An exception after the write is not a refusal, and is passed on as it is -- after the mass the body
                // held is written back, so that the begin leaves nothing behind however it ends.
                body.mass = previous;
                throw;
            }

            pending = new Pending
            {
                body = body,
                previousMass = previous,
                centre = centre,
                inertiaToWrite = written,
                axes = axes,
                applied = new Applied(mass, effective, scale, inertia, written),
                open = true,
            };
            return true;
        }

        /// <summary>
        /// Writes the centre, the inertia worked out and the axes. No check of the numbers refuses it (they were all made
        /// at the begin); it can still fail as any engine call can. It ends the application before writing: a commit that
        /// throws part way is not reverted (the body keeps the begun mass and whatever of the rest was written), and the
        /// caller's own exception handling decides what becomes of that owner (the Final handoff ends the pair).
        /// </summary>
        public static Applied Commit(ref Pending pending)
        {
            if (!pending.open)
            {
                throw new InvalidOperationException("a mass properties application that is not open cannot be committed");
            }

            pending.open = false;
            Rigidbody body = pending.body;
            body.centerOfMass = pending.centre;
            body.inertiaTensor = pending.inertiaToWrite;
            body.inertiaTensorRotation = pending.axes;
            if (pending.applied.Adjusted)
            {
                AdjustedCount++;
            }

            return pending.applied;
        }

        /// <summary>Writes the mass the body held before the begin back; nothing else had been written. Does nothing for one that is not open.</summary>
        public static void Revert(ref Pending pending)
        {
            if (!pending.open)
            {
                return;
            }

            pending.open = false;
            if (pending.body != null)
            {
                pending.body.mass = pending.previousMass;
            }
        }

        private static bool Refused(MassPropertiesRefusal refusal)
        {
            RefusedCount++;
            LastRefusal = refusal;
            return false;
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static bool PositiveFinite(float v) => v > 0f && !float.IsInfinity(v);
    }
}
