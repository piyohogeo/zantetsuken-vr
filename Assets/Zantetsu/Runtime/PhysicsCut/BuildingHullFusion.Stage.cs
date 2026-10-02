using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The staged stop of a hull cut's sides (TL, 2026-09-30; a limited prototype, off unless the profile gives a motion
    /// time): the two sides of one cut move for a short real time -- held by a short sibling constraint when the settings
    /// ask for it, their collision with each other excluded in both cases -- and are then fixed for show (velocity zero,
    /// kinematic, followed by the rest as ground, never released by a contact's end), at that time or when a next Slash's
    /// hit on the building is accepted, whichever comes first. The constraint is owned by the pair and removed before any
    /// body of the pair goes (a re-cut, a merge, the end); a new cut makes a new pair and never inherits an old one. The
    /// group's anchors mean what they meant.
    /// </summary>
    public sealed partial class BuildingHullFusion
    {
        private sealed class SiblingPair
        {
            public HullGroup positive, negative;
            public int building;
            public ConfigurableJoint joint;
            public Collider a, b;   // their collision with each other excluded while the pair moves
            public double publishedAt, physicsAt;
            public long stepAt;
            public float3 normalGroup;   // the plane normal in the positive side's frame (its Root's local space)
            public float4x4 relativeAtPublish;   // the negative side's Root in the positive side's frame
            public float3 positiveAt, negativeAt;   // the Roots' world positions at the publication
            public float maxSpeed;
        }

        private readonly List<SiblingPair> _pairs = new List<SiblingPair>();
        private readonly Dictionary<int, double> _lastStopAt = new Dictionary<int, double>();
        private readonly List<double> _stopToFusion = new List<double>();

        /// <summary>Real seconds from a building's last staged stop to the adoption of its next candidate, one a fusion that followed a stop.</summary>
        public IReadOnlyList<double> StopToFusionSeconds => _stopToFusion;
        private readonly List<string> _stageRecords = new List<string>();

        /// <summary>Pairs of sides moving now; constraints live now; made in all.</summary>
        public int PairsMoving => _pairs.Count;
        public int ConstraintsLive { get; private set; }
        public int PairsMade { get; private set; }
        public int ConstraintsMade { get; private set; }
        public int StopsByDeadline { get; private set; }
        public int StopsByNextSlash { get; private set; }
        public int StopsOther { get; private set; }
        public int GroupsStaged { get; private set; }
        public float MaxOpening { get; private set; }
        /// <summary>The least opening along the normal at a stop (negative: the sides sank into each other, their collision excluded).</summary>
        public float MinOpening { get; private set; }
        public float MaxInPlane { get; private set; }
        public float MaxRelativeRotationDegrees { get; private set; }
        public float MaxPairSpeed { get; private set; }
        public float MaxFall { get; private set; }
        public double MaxStageRealSeconds { get; private set; }

        /// <summary>One line a stop: the pair, why, the real time, steps and physics time since the publication, the opening along the normal, the move along the plane, the relative turn, the fastest speed, the fall.</summary>
        public IReadOnlyList<string> StageRecords => _stageRecords;

        private bool HasMovingPair(int building)
        {
            foreach (SiblingPair p in _pairs) if (p.building == building) return true;
            return false;
        }

        /// <summary>The publication's pair: the sides' collision with each other excluded; the sibling constraint when asked for (not when both sides are fixed).</summary>
        private void BeginPair(HullGroup positive, HullGroup negative, float3 normalWorld, float4 planeGroup)
        {
            var p = new SiblingPair
            {
                positive = positive, negative = negative, building = positive.Building,
                publishedAt = _realSeconds(), physicsAt = _physicsSeconds(), stepAt = CutPhysicsStep.Clock != null ? CutPhysicsStep.Clock.StepId : -1,
                normalGroup = math.normalize(planeGroup.xyz),
                relativeAtPublish = math.mul(math.inverse((float4x4)positive.Root.transform.localToWorldMatrix), (float4x4)negative.Root.transform.localToWorldMatrix),
                positiveAt = positive.Root.transform.position, negativeAt = negative.Root.transform.position,
                a = positive.Collider, b = negative.Collider,
            };
            if (p.a != null && p.b != null) Physics.IgnoreCollision(p.a, p.b, true);
            if (_settings.siblingD6)
            {
                p.joint = ProvisionalSeparation.Configure(positive.Root, negative.Body, p.normalGroup, (float)(_settings.siblingOpeningMetres * 0.5));
                ConstraintsLive++;
                ConstraintsMade++;
            }

            _pairs.Add(p);
            PairsMade++;
        }

        /// <summary>Each Step: the pairs' fastest speed noted; a pair past its motion time fixed.</summary>
        private void StepPairs()
        {
            double now = _realSeconds();
            for (int i = _pairs.Count - 1; i >= 0; i--)
            {
                SiblingPair p = _pairs[i];
                foreach (HullGroup g in new[] { p.positive, p.negative }) if (g.Body != null && !g.Body.isKinematic) p.maxSpeed = Mathf.Max(p.maxSpeed, g.Body.linearVelocity.magnitude);
                if (now - p.publishedAt >= _settings.stageSeconds) StopPair(p, "deadline");
            }
        }

        private void StopPairsOf(int building, string why)
        {
            for (int i = _pairs.Count - 1; i >= 0; i--) if (i < _pairs.Count && _pairs[i].building == building) StopPair(_pairs[i], why);
        }

        private void StopPairsOf(HullGroup g, string why)
        {
            for (int i = _pairs.Count - 1; i >= 0; i--) if (i < _pairs.Count && (ReferenceEquals(_pairs[i].positive, g) || ReferenceEquals(_pairs[i].negative, g))) StopPair(_pairs[i], why);
        }

        /// <summary>A pair fixed: measured, each moving side stopped (velocity zero, kinematic, pinned by the rest as ground), then the constraint and the exclusion removed; the building looked at again.</summary>
        private void StopPair(SiblingPair p, string why)
        {
            if (!_pairs.Remove(p)) return;
            double real = _realSeconds() - p.publishedAt, physics = _physicsSeconds() - p.physicsAt;
            long steps = CutPhysicsStep.Clock != null && p.stepAt >= 0 ? CutPhysicsStep.Clock.StepId - p.stepAt : -1;
            string measured = "(the sides gone)";
            if (p.positive.Root != null && p.negative.Root != null)
            {
                float4x4 relative = math.mul(math.inverse((float4x4)p.positive.Root.transform.localToWorldMatrix), (float4x4)p.negative.Root.transform.localToWorldMatrix);
                float3 d = relative.c3.xyz - p.relativeAtPublish.c3.xyz;
                float opening = -math.dot(d, p.normalGroup);   // the negative side away from the positive one along the normal
                float inPlane = math.length(d - math.dot(d, p.normalGroup) * p.normalGroup);
                quaternion turn = math.mul(new quaternion(new float3x3(relative.c0.xyz, relative.c1.xyz, relative.c2.xyz)), math.inverse(new quaternion(new float3x3(p.relativeAtPublish.c0.xyz, p.relativeAtPublish.c1.xyz, p.relativeAtPublish.c2.xyz))));
                float degrees = math.degrees(2f * math.acos(math.min(1f, math.abs(math.normalize(turn).value.w))));
                float fall = math.max(math.length((float3)p.positive.Root.transform.position - p.positiveAt), math.length((float3)p.negative.Root.transform.position - p.negativeAt));
                MaxOpening = Mathf.Max(MaxOpening, opening);
                MinOpening = Mathf.Min(MinOpening, opening);
                MaxInPlane = Mathf.Max(MaxInPlane, inPlane);
                MaxRelativeRotationDegrees = Mathf.Max(MaxRelativeRotationDegrees, degrees);
                MaxFall = Mathf.Max(MaxFall, fall);
                measured = "opening " + opening.ToString("F4") + " m, along the plane " + inPlane.ToString("F4") + " m, relative turn " + degrees.ToString("F2") + " deg, largest move " + fall.ToString("F3") + " m";
            }

            MaxPairSpeed = Mathf.Max(MaxPairSpeed, p.maxSpeed);
            MaxStageRealSeconds = Math.Max(MaxStageRealSeconds, real);
            if (why == "deadline") StopsByDeadline++; else if (why.StartsWith("the next Slash")) StopsByNextSlash++; else StopsOther++;
            foreach (HullGroup g in new[] { p.positive, p.negative })
            {
                if (g.State == HullGroupState.Gone || g.Body == null || g.Kinematic) continue;
                g.Body.linearVelocity = Vector3.zero;
                g.Body.angularVelocity = Vector3.zero;
                g.Body.isKinematic = true;
                g.Kinematic = true;
                g.Staged = true;
                _rest.PinGroup(g.Body, g.Collider);
                GroupsStaged++;
            }

            RemoveConstraint(p);
            _lastStopAt[p.building] = _realSeconds();
            if (_stageRecords.Count < 1024)
                _stageRecords.Add("groups " + p.positive.Id + "/" + p.negative.Id + " fixed (" + why + ") after " + real.ToString("F3") + " s real, " + steps + " steps, " + physics.ToString("F3") + " s of physics: " + measured + ", fastest " + p.maxSpeed.ToString("F2") + " m/s; constraint " + (p.joint != null || _settings.siblingD6 ? "removed" : "none"));
            Record("groups " + p.positive.Id + "/" + p.negative.Id + " fixed for show (" + why + ") t " + _physicsSeconds().ToString("F3") + ": " + measured);
            Reevaluate(p.building, "the sides fixed for show");
        }

        /// <summary>The pair's constraint and its collision exclusion removed, at once (never left for a later frame: a body of the pair may go next).</summary>
        private void RemoveConstraint(SiblingPair p)
        {
            if (p.joint != null)
            {
                UnityEngine.Object.DestroyImmediate(p.joint);
                p.joint = null;
                ConstraintsLive--;
            }

            if (p.a != null && p.b != null) Physics.IgnoreCollision(p.a, p.b, false);
            p.a = null; p.b = null;
        }
    }
}
