using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Zantetsu.Rendering.Urp
{
    /// <summary>Opt-in URP 17.3 experiment: bounds culling for XR motion only; colour and depth encoding stay intact.</summary>
    public sealed class VpAppSwDistanceExperiment : IDisposable
    {
        private static readonly Dictionary<Camera, VpAppSwDistanceExperiment> Active = new();
        private readonly Camera _camera;
        private readonly ScriptableRenderer _renderer;
        private readonly FieldInfo _stockField;
        private readonly XRDepthMotionPass _stock;
        private readonly Action<RenderGraph, ContextContainer> _record;
        private readonly Pass _pass;
        private bool _disposed;
        public float FarMeters { get; }
        public long CulledFrames { get; private set; }
        public static float DistanceFor(Camera camera) => camera != null && Active.TryGetValue(camera, out var value) ? value.FarMeters : 0f;

        public VpAppSwDistanceExperiment(Camera camera, float farMeters)
        {
#if !ZANTETSU_URP_17_3_0
            throw new NotSupportedException("AppSW distance experiment is pinned to URP 17.3.0.");
#else
            if (camera == null || camera.orthographic || !float.IsFinite(farMeters) || farMeters < 0f
                || (farMeters > 0f && (farMeters <= camera.nearClipPlane || farMeters > camera.farClipPlane)))
                throw new ArgumentException("A perspective camera and a distance within its original near/far are required.");
            if (Active.ContainsKey(camera)) throw new InvalidOperationException("Camera already has an AppSW distance experiment.");
            _camera = camera; FarMeters = farMeters;
            _renderer = camera.GetUniversalAdditionalCameraData().scriptableRenderer;
            if (!(_renderer is UniversalRenderer)) throw new InvalidOperationException("UniversalRenderer is required.");
            // URP has no public hook for the XR renderer list. Reuse the installed stock pass (history, targets,
            // background and encoding included), record it with a temporary CullingResults, then restore the frame.
            // This compatibility shim is confined to the opt-in experiment; it does not edit PackageCache.
            _stockField = typeof(UniversalRenderer).GetField("m_XRDepthMotionPass", BindingFlags.Instance | BindingFlags.NonPublic);
            _stock = _stockField?.GetValue(_renderer) as XRDepthMotionPass;
            var method = typeof(XRDepthMotionPass).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(RenderGraph), typeof(ContextContainer) }, null);
            if (_stock == null || method == null) throw new NotSupportedException("Installed URP XR motion hook does not match.");
            _record = (Action<RenderGraph, ContextContainer>)method.CreateDelegate(typeof(Action<RenderGraph, ContextContainer>), _stock);
            _pass = new Pass(this);
            Active.Add(camera, this);
            _stockField.SetValue(_renderer, null);
#endif
        }

        public void Enqueue(ScriptableRenderContext context)
        {
            if (_disposed) return;
            _pass.Context = context;
            _renderer.EnqueuePass(_pass);
        }

        private void Record(RenderGraph graph, ContextContainer frame)
        {
            var camera = frame.Get<UniversalCameraData>();
            if (camera.xr == null || !camera.xr.enabled || !camera.xr.singlePassEnabled || !camera.xr.hasMotionVectorPass) return;
            var rendering = frame.Get<UniversalRenderingData>();
            var original = rendering.cullResults;
            // The outer section (VpAppSwMarkers): the parameters, the Cull (its own section inside), the stock update and recording.
            using var recordSection = VpAppSwMarkers.Record.Auto();
            try
            {
                if (ReferenceEquals(camera.camera, _camera) && FarMeters > 0f)
                {
                    var parameters = camera.xr.cullingParams;
                    // Add a forward-facing distance half-space to the existing stereo union frustum. An extra plane
                    // avoids assuming that XR's existing planes follow GeometryUtility's indices.
                    if (parameters.cullingPlaneCount >= ScriptableCullingParameters.maximumCullingPlaneCount)
                        throw new InvalidOperationException("XR culling planes have no room for the experiment's far plane.");
                    var view = camera.GetViewMatrix(0);
                    var row = view.GetRow(2); // view z is negative in front of the eye.
                    var plane = new Plane(new Vector3(row.x, row.y, row.z), row.w + FarMeters);
                    int slot = parameters.cullingPlaneCount++;
                    parameters.SetCullingPlane(slot, plane);
                    parameters.cullingOptions &= ~(CullingOptions.ShadowCasters | CullingOptions.NeedsLighting | CullingOptions.NeedsReflectionProbes);
                    parameters.maximumVisibleLights = 0;
                    using (VpAppSwMarkers.UnityCull.Auto()) rendering.cullResults = _pass.Context.Cull(ref parameters);
                    CulledFrames++;
                }
                _stock.Update(ref camera);
                _record(graph, frame);
            }
            finally { rendering.cullResults = original; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; Active.Remove(_camera);
            // Ownership of the stock material/targets returns to UniversalRenderer.
            if (_stockField.GetValue(_renderer) == null) _stockField.SetValue(_renderer, _stock);
        }

        private sealed class Pass : ScriptableRenderPass
        {
            private readonly VpAppSwDistanceExperiment _owner;
            public ScriptableRenderContext Context;
            public Pass(VpAppSwDistanceExperiment owner) { _owner = owner; renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses; }
            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame) => _owner.Record(graph, frame);
        }
    }
}
