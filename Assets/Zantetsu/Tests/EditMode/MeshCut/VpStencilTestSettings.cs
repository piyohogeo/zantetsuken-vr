using System;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The stencil settings the logical cut display tests are made with. **Test values only**: the colour limit, the
    /// epsilons and the margin are DESIGN O-034's to decide, and nothing here is a product value or a default.
    /// </summary>
    internal static class VpStencilTestSettings
    {
        public const int Colours = 4;
        public const float FacingEpsilon = 0.01f;
        public const float PlaneEpsilon = 1e-4f;
        public const float OffsetEpsilon = 1e-4f;
        public const int Cameras = 4;

        public static readonly Vector2 Margin = new Vector2(0.01f, 0.01f);

        public static VpStencilSettings Create(int colours = Colours, int cameras = Cameras)
        {
            return new VpStencilSettings(colours, FacingEpsilon, PlaneEpsilon, OffsetEpsilon, Margin, cameras);
        }
    }

    /// <summary>
    /// The logical room the display tests make a display with: **test values only**, stated here once, not product
    /// values. Each is well past what any test's ledger reaches, except where a test passes its own.
    /// </summary>
    internal static class VpDisplayTestCapacities
    {
        /// <summary>Logical branches over every registration together.</summary>
        public const int Branches = 64;

        /// <summary>Clip candidates over every branch together.</summary>
        public const int Candidates = 512;

        /// <summary>The longest chain of boundaries one fragment may have.</summary>
        public const int ChainDepth = 32;
    }

    /// <summary>
    /// A real display's classifier targets, made by a test from what the display publishes -- its cap records, their
    /// vertices and its render fragments -- the way its own classification reads the snapshot: one target per render
    /// fragment, with **every** cap record of that render fragment as a condition, seen or not, and only the caps the
    /// display's visibility test keeps as its caps. Test code only; the display itself does not go through this.
    /// </summary>
    internal static class VpDisplayRecordTargets
    {
        /// <summary>The conditions of the render fragment that cap <paramref name="capIndex"/> closes.</summary>
        public static VpCapCompatibilityTarget Conditions(VpLogicalCutDisplay display, LogicalCutLedger ledger, int capIndex)
        {
            NUnit.Framework.Assert.That(display.TryGetCapRecord(capIndex, out LogicalCutCapRecord cap), NUnit.Framework.Is.True);
            var conditions = new System.Collections.Generic.List<VpCapConstraint>();
            for (int i = 0; i < display.CapRecordCount; i++)
            {
                display.TryGetCapRecord(i, out LogicalCutCapRecord record);
                if (record.renderFragment == cap.renderFragment)
                {
                    conditions.Add(new VpCapConstraint(new VpCapFace(ledger, record.operation), record.side, record.worldPlane));
                }
            }

            return new VpCapCompatibilityTarget(VpArrayRange<VpCapConstraint>.Whole(conditions.ToArray()), cap.offset);
        }

        /// <summary>
        /// The projection target of the render fragment that cap <paramref name="capIndex"/> closes, for these eyes: its
        /// conditions, its registration's box and placement, copies of its caps the visibility test keeps, and whether
        /// every non-empty one was kept.
        /// </summary>
        public static VpCapProjectionTarget Projection(
            VpLogicalCutDisplay display, LogicalCutLedger ledger, int capIndex, in VpCapEye left, in VpCapEye right,
            float facingEpsilon)
        {
            NUnit.Framework.Assert.That(display.TryGetCapRecord(capIndex, out LogicalCutCapRecord cap), NUnit.Framework.Is.True);
            NUnit.Framework.Assert.That(
                display.TryGetRenderFragment(cap.renderFragment, out VpMultiCutRenderFragment rf), NUnit.Framework.Is.True);
            var seen = new System.Collections.Generic.List<Vector3[]>();
            int nonEmpty = 0;
            for (int i = 0; i < display.CapRecordCount; i++)
            {
                display.TryGetCapRecord(i, out LogicalCutCapRecord record);
                if (record.renderFragment != cap.renderFragment || record.vertexCount == 0)
                {
                    continue;
                }

                nonEmpty++;
                NUnit.Framework.Assert.That(
                    VpCapVisibility.TryClassify(display, i, left, right, facingEpsilon, out VpCapVisibilityVerdict verdict),
                    NUnit.Framework.Is.True);
                if (!verdict.Keep)
                {
                    continue;
                }

                var polygon = new Vector3[record.vertexCount];
                for (int v = 0; v < polygon.Length; v++)
                {
                    display.TryGetCapVertex(i, v, out polygon[v]);
                }

                seen.Add(polygon);
            }

            return new VpCapProjectionTarget(
                Conditions(display, ledger, capIndex), rf.localBounds, rf.geometryLocalToWorld, seen,
                nonEmpty > 0 && seen.Count == nonEmpty);
        }
    }

    /// <summary>
    /// Ends the frame, then disposes: a display is not disposed while a camera's draws are registered for the current
    /// frame. The tests render every camera they register draws for before this, so the frame has been drawn by then;
    /// advancing the counter is the frame boundary, and nothing about the GPU is waited on.
    /// </summary>
    internal sealed class AfterTheFrame : IDisposable
    {
        private readonly Action _endFrame;
        private readonly IDisposable _inner;

        public AfterTheFrame(Action endFrame, IDisposable inner)
        {
            _endFrame = endFrame;
            _inner = inner;
        }

        public void Dispose()
        {
            _endFrame();
            _inner.Dispose();
        }
    }
}
