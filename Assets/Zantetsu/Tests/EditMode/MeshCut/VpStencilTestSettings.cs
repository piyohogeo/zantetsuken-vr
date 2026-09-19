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
