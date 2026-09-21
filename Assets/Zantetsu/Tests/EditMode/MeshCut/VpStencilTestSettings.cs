using System;
using System.Collections.Generic;
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
        public const int Cameras = 4;

        public static readonly Vector2 Margin = new Vector2(0.01f, 0.01f);

        public static VpStencilSettings Create(int colours = Colours, int cameras = Cameras)
        {
            return new VpStencilSettings(colours, FacingEpsilon, PlaneEpsilon, Margin, cameras);
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
    /// Where each fragment's geometry stands, for a test that needs the two sides of a cut really apart -- so that a
    /// section is exposed to a camera at all. A published branch is given a base placement of its own, exactly as a
    /// physics owner would give it through the same lookup (DESIGN 5.6, 7.1.2).
    /// <para>
    /// **This is not a display separation.** Nothing of the renderer moves anything; the branch is simply somewhere
    /// else, and its cap is drawn where its own placement puts it.
    /// </para>
    /// <para>
    /// **Nothing is assumed of a fragment this was not told about**: it answers
    /// <see cref="VpFragmentPlacementKind.Missing"/>, so a case that forgets to say where one of its living branches
    /// stands fails instead of quietly drawing it at the identity. A fragment meant to stand where it was registered
    /// is said so with <see cref="Static"/>. This is a rule for reading test input, not a product rule.
    /// </para>
    /// </summary>
    internal sealed class VpTestPlacements : IVpFragmentPlacement
    {
        private readonly Dictionary<LogicalFragmentId, Matrix4x4> _following =
            new Dictionary<LogicalFragmentId, Matrix4x4>();

        private readonly HashSet<LogicalFragmentId> _static = new HashSet<LogicalFragmentId>();

        /// <summary>This fragment follows a placement of its own, at <paramref name="position"/>.</summary>
        internal VpTestPlacements Put(LogicalFragmentId fragment, Vector3 position)
        {
            return Put(fragment, Matrix4x4.Translate(position));
        }

        /// <summary>This fragment follows <paramref name="placement"/>.</summary>
        internal VpTestPlacements Put(LogicalFragmentId fragment, Matrix4x4 placement)
        {
            _following[fragment] = placement;
            _static.Remove(fragment);
            return this;
        }

        /// <summary>
        /// This fragment is no longer one this lookup answers for, so it is asked about and nothing is said -- an
        /// owner that has gone, as far as a reader can tell.
        /// </summary>
        internal VpTestPlacements Forget(LogicalFragmentId fragment)
        {
            _following.Remove(fragment);
            _static.Remove(fragment);
            return this;
        }

        /// <summary>This fragment is drawn where it was registered, said so on purpose.</summary>
        internal VpTestPlacements Static(LogicalFragmentId fragment)
        {
            _static.Add(fragment);
            _following.Remove(fragment);
            return this;
        }

        public VpFragmentPlacementKind TryGetGeometryLocalToWorld(
            LogicalFragmentId fragment, out Matrix4x4 geometryLocalToWorld)
        {
            if (_following.TryGetValue(fragment, out geometryLocalToWorld))
            {
                return VpFragmentPlacementKind.Following;
            }

            geometryLocalToWorld = Matrix4x4.identity;
            return _static.Contains(fragment)
                ? VpFragmentPlacementKind.Static
                : VpFragmentPlacementKind.Missing;
        }
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

            return new VpCapCompatibilityTarget(VpArrayRange<VpCapConstraint>.Whole(conditions.ToArray()));
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
