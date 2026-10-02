using UnityEngine;

namespace Zantetsu.Sandbox
{
    // The building check's expectation of a World D6's linear limit as the joint reports it back (DESIGN 7.2.2). Observed in
    // Unity 6000.3.22f1 (Editor probe, 2026-10-02): a ConfigurableJoint asked for a linear limit 0 < L < 0.001 m reports
    // 0.001 m; 0 reports 0, and 0.001 m or more reports the request. That is the engine's, accepted as it is: the product
    // requests L(d) and nothing raises it. What the joint reports is not the distance it holds a body to, which is not
    // measured here. The check keeps the request L(d) and the expected read-back apart, compares what it reads with the
    // expected read-back within its tolerance, and counts apart the reads the engine's minimum applied to.
    public static partial class SandboxPropSlashPlayerCheck
    {
        /// <summary>The smallest positive linear limit the joint reports (Unity 6000.3.22f1 observation).</summary>
        internal const float EngineLinearLimitMinimum = 0.001f;

        /// <summary>How far a read-back may be from the expected one (the check's tolerance, unchanged).</summary>
        internal const float LinearLimitTolerance = 1e-6f;

        /// <summary>The read-back expected for a request: 0.001 for 0 &lt; L &lt; 0.001, else the request itself.</summary>
        internal static float ExpectedLinearReadback(float requested)
        {
            return requested > 0f && requested < EngineLinearLimitMinimum ? EngineLinearLimitMinimum : requested;
        }

        /// <summary>Whether the engine's minimum applies to a request (its expected read-back is not the request).</summary>
        internal static bool LinearMinimumApplies(float requested) => ExpectedLinearReadback(requested) != requested;

        /// <summary>Whether a read-back is the expected one within the tolerance (a comparison within 1e-6, not an exact one).</summary>
        internal static bool LinearReadbackMatches(float read, float requested)
        {
            return Mathf.Abs(read - ExpectedLinearReadback(requested)) <= LinearLimitTolerance;
        }
    }
}
