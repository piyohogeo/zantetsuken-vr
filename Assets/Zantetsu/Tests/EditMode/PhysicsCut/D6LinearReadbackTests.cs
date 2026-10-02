using System;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The World D6's linear limit as the joint reports it back (DESIGN 7.2.2, observed in Unity 6000.3.22f1): 0 reports 0,
    /// 0 &lt; L &lt; 0.001 m reports 0.001 m, 0.001 m or more reports the request. The engine's rule is checked here on joints
    /// made as the product makes them, right after the set (inactive) and after a Step once published, so that an engine
    /// whose rule differs fails this rather than the building check's expectation silently going wrong. Only what the
    /// joint reports is checked; the distance it holds a body to is not. And the check's expectation of it: the expected
    /// read-back, a comparison within the check's tolerance 1e-6 (not an exact one), and when the minimum applies.
    /// </summary>
    public class D6LinearReadbackTests
    {
        private static float Bits(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

        private static int BitsOf(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

        private static readonly float JustBelow = Bits(BitsOf(0.001f) - 1);   // 0x3A83126E
        private static readonly float JustAbove = Bits(BitsOf(0.001f) + 1);   // 0x3A831270

        private static readonly float[] Requests =
        {
            0f, 1e-7f, 1e-5f, 0.0005f, 0.0009765625f, 0.00099f, JustBelow, 0.001f, JustAbove, 0.0011f, 0.002f,
        };

        // The engine's rule as observed: what the joint reports for a request.
        private static float Observed(float requested) => requested > 0f && requested < 0.001f ? 0.001f : requested;

        private static ConfigurableJoint MakeAsTheProduct(float limit, Vector3 at)
        {
            var root = new GameObject("d6 linear readback " + limit.ToString("R"));
            root.SetActive(false);
            root.transform.SetPositionAndRotation(at, Quaternion.identity);
            var shape = new GameObject("Shape Frame");
            shape.transform.SetParent(root.transform, false);
            shape.AddComponent<BoxCollider>();
            Rigidbody body = root.AddComponent<Rigidbody>();
            body.mass = 500f;
            ConfigurableJoint joint = root.AddComponent<ConfigurableJoint>();
            joint.connectedBody = null;
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = Vector3.zero;
            joint.xMotion = ConfigurableJointMotion.Limited;
            joint.yMotion = ConfigurableJointMotion.Free;
            joint.zMotion = ConfigurableJointMotion.Limited;
            joint.linearLimit = new SoftJointLimit { limit = limit, bounciness = 0f, contactDistance = 0f };
            joint.angularXMotion = ConfigurableJointMotion.Limited;
            joint.angularYMotion = ConfigurableJointMotion.Limited;
            joint.angularZMotion = ConfigurableJointMotion.Limited;
            joint.axis = Vector3.right;
            joint.secondaryAxis = Vector3.up;
            joint.connectedAnchor = at;
            return joint;
        }

        [Test]
        public void TheEngineReportsTheLinearLimit_ZeroAsZero_BelowAMillimetreAsAMillimetre_ElseAsRequested()
        {
            SimulationMode mode = Physics.simulationMode;
            ConfigurableJoint joint = null;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                for (int i = 0; i < Requests.Length; i++)
                {
                    float requested = Requests[i];
                    joint = MakeAsTheProduct(requested, new Vector3(i * 10f, 0f, 0f));
                    float afterSet = joint.linearLimit.limit;
                    joint.gameObject.SetActive(true);
                    Physics.Simulate(1f / 45f);
                    float afterStep = joint.linearLimit.limit;
                    string what = "requested " + requested.ToString("R") + " (0x" + BitsOf(requested).ToString("X8") + ")";
                    Assert.That(BitsOf(afterSet), Is.EqualTo(BitsOf(Observed(requested))), what + ": right after the set, read " + afterSet.ToString("R"));
                    Assert.That(BitsOf(afterStep), Is.EqualTo(BitsOf(Observed(requested))), what + ": after a Step, read " + afterStep.ToString("R"));
                    UnityEngine.Object.DestroyImmediate(joint.gameObject);
                    joint = null;
                }
            }
            finally
            {
                Physics.simulationMode = mode;
                if (joint != null) UnityEngine.Object.DestroyImmediate(joint.gameObject);
            }
        }

        // The U-series check (SandboxPlayerCheck.LinearLimitAsAsked) on real joints at the boundary: what each joint reports
        // passes for its own request, and a joint compared with another request beyond the tolerance fails.
        [Test]
        public void TheUCheck_PassesEachJointForItsOwnRequest_AndFailsAnotherRequest_AtTheBoundary()
        {
            SimulationMode mode = Physics.simulationMode;
            ConfigurableJoint joint = null;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                for (int i = 0; i < Requests.Length; i++)
                {
                    float requested = Requests[i];
                    joint = MakeAsTheProduct(requested, new Vector3(i * 10f, 0f, 30f));
                    joint.gameObject.SetActive(true);
                    Physics.Simulate(1f / 45f);
                    Assert.That(SandboxPlayerCheck.LinearLimitAsAsked(joint, requested), Is.True,
                        "requested " + requested.ToString("R") + ", read " + joint.linearLimit.limit.ToString("R"));
                    UnityEngine.Object.DestroyImmediate(joint.gameObject);
                    joint = null;
                }

                (float made, float asked, bool passes, string why)[] cases =
                {
                    (0.0009765625f, 0.0009765625f, true, "2^-10 reads 0.001: its own request passes"),
                    (0.0011f, 0.001f, false, "0.0011 is not what 0.001 reads back as"),
                    (0.0009765625f, 0.0011f, false, "0.001 is not what 0.0011 reads back as"),
                    (0f, 0.0005f, false, "0 is not what 0.0005 reads back as (0.001)"),
                    (0.0005f, 0f, false, "0.001 is not what 0 reads back as (0)"),
                    (0.002f, 0.001f, false, "0.002 is not what 0.001 reads back as"),
                    (JustAbove, JustBelow, true, "0x3A831270 against the expected 0.001: within the tolerance 1e-6 (a comparison, not an exact match)"),
                };
                foreach ((float made, float asked, bool passes, string why) in cases)
                {
                    joint = MakeAsTheProduct(made, new Vector3(0f, 0f, 60f));
                    joint.gameObject.SetActive(true);
                    Physics.Simulate(1f / 45f);
                    Assert.That(SandboxPlayerCheck.LinearLimitAsAsked(joint, asked), Is.EqualTo(passes), why);
                    UnityEngine.Object.DestroyImmediate(joint.gameObject);
                    joint = null;
                }
            }
            finally
            {
                Physics.simulationMode = mode;
                if (joint != null) UnityEngine.Object.DestroyImmediate(joint.gameObject);
            }
        }

        [Test]
        public void TheCheckExpectsTheEnginesReadback_AndComparesWithinItsTolerance()
        {
            const float twoToMinusTen = 0.0009765625f;
            Assert.That(SandboxPropSlashPlayerCheck.ExpectedLinearReadback(twoToMinusTen), Is.EqualTo(0.001f));
            Assert.That(SandboxPropSlashPlayerCheck.ExpectedLinearReadback(JustBelow), Is.EqualTo(0.001f));
            Assert.That(SandboxPropSlashPlayerCheck.ExpectedLinearReadback(0f), Is.EqualTo(0f), "0 is not raised");
            Assert.That(SandboxPropSlashPlayerCheck.ExpectedLinearReadback(0.001f), Is.EqualTo(0.001f));
            Assert.That(SandboxPropSlashPlayerCheck.ExpectedLinearReadback(JustAbove), Is.EqualTo(JustAbove));
            Assert.That(SandboxPropSlashPlayerCheck.ExpectedLinearReadback(0.002f), Is.EqualTo(0.002f));

            Assert.That(SandboxPropSlashPlayerCheck.LinearMinimumApplies(twoToMinusTen), Is.True);
            Assert.That(SandboxPropSlashPlayerCheck.LinearMinimumApplies(JustBelow), Is.True);
            Assert.That(SandboxPropSlashPlayerCheck.LinearMinimumApplies(0f), Is.False);
            Assert.That(SandboxPropSlashPlayerCheck.LinearMinimumApplies(0.001f), Is.False);
            Assert.That(SandboxPropSlashPlayerCheck.LinearMinimumApplies(0.002f), Is.False);

            // The right read-backs pass.
            Assert.That(SandboxPropSlashPlayerCheck.LinearReadbackMatches(0.001f, twoToMinusTen), Is.True, "2^-10 read back as 0.001");
            Assert.That(SandboxPropSlashPlayerCheck.LinearReadbackMatches(0f, 0f), Is.True);
            Assert.That(SandboxPropSlashPlayerCheck.LinearReadbackMatches(0.002f, 0.002f), Is.True);
            Assert.That(SandboxPropSlashPlayerCheck.LinearReadbackMatches(JustAbove, JustAbove), Is.True);
            // Within the tolerance (a comparison, not an exact match): passes.
            Assert.That(SandboxPropSlashPlayerCheck.LinearReadbackMatches(0.001f + 5e-7f, twoToMinusTen), Is.True, "0.5e-6 from the expected read-back");
            // Wrong read-backs beyond the tolerance fail.
            Assert.That(SandboxPropSlashPlayerCheck.LinearReadbackMatches(twoToMinusTen, twoToMinusTen), Is.False, "the request itself is not what the engine reports");
            Assert.That(SandboxPropSlashPlayerCheck.LinearReadbackMatches(0.001f + 2e-6f, twoToMinusTen), Is.False, "2e-6 from the expected read-back");
            Assert.That(SandboxPropSlashPlayerCheck.LinearReadbackMatches(0.001f, 0f), Is.False, "0 is not raised");
            Assert.That(SandboxPropSlashPlayerCheck.LinearReadbackMatches(0.001f, 0.002f), Is.False);
            Assert.That(SandboxPropSlashPlayerCheck.LinearReadbackMatches(0.002f + 2e-6f, 0.002f), Is.False);
            Assert.That(SandboxPropSlashPlayerCheck.LinearLimitTolerance, Is.EqualTo(1e-6f), "the check's tolerance is unchanged");
        }
    }
}
