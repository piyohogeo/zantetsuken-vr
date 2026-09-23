using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace Zantetsu.Rendering.Urp
{
    /// <summary>
    /// The explicit opt-in to VP3C for one display set. The owner of a VP3 batch (<see cref="VpIndexedIndirectDrawBatch"/>)
    /// and a VP3C set (<see cref="VpCulledInstanceSet"/>), uploaded with the same commands and transforms over the same
    /// geometry buffers, issues the display set through this route once per frame (<see cref="Issue"/>) instead of through
    /// the batch. By default the route is the VP3 single batch: Issue queues the batch for every camera, as the batch's
    /// Render with no camera does, and the route holds no render pipeline callback, cascade pass or VP3C issue.
    /// <para>
    /// <see cref="TryEnable"/> routes one camera through VP3C. It subscribes one RenderPipelineManager.beginCameraRendering
    /// callback and creates one <see cref="VpCascadeShadowPass"/>. In a frame whose Issue found the route enabled, the
    /// callback issues the display set for each camera render: for the enabled camera, the forward selection for its eyes
    /// (uploaded only when it changed), the culled forward call and the shadow caster bounds call, and it enqueues the
    /// cascade pass on the camera's Universal Renderer; for any other camera, the batch for that camera only.
    /// <see cref="Disable"/> unsubscribes and drops the pass. Enabling or disabling again does nothing; enabling another
    /// camera while enabled is refused.
    /// </para>
    /// <para>
    /// A draw queued for one camera applies to that camera's next render only, while a draw queued for every camera
    /// applies to every render until the frame ends (Time.frameCount). Issue therefore records its frame's path, and later
    /// changes do not draw the set twice or drop it: enabling after this frame's Issue leaves the single batch queued for
    /// the rest of the frame, and disabling after an enabled Issue queues the single batch for the renders still to come.
    /// A second Issue in a frame does nothing.
    /// </para>
    /// <para>
    /// TryEnable refuses, giving the reason, what is not verified or cannot show VP3C's shadows: a render pipeline other
    /// than URP, URP's compatibility mode, a Unity, URP or graphics API version other than Unity
    /// <see cref="VerifiedUnityVersion"/>, URP 17.3.0 and Direct3D 11, main light shadows off or per-vertex in the URP
    /// asset, an overlay camera, a camera not rendering shadows, a renderer other than the Universal Renderer, a stereo mode
    /// other than Single Pass Instanced, a set uploaded for another stereo mode than the camera renders, and a batch and set
    /// whose uploads differ. The callback checks the same every frame and, when a check fails, issues the batch for the
    /// enabled camera instead and reports <see cref="LastFallbackReason"/> (logging a warning when the reason appears).
    /// When the cascade pass was enqueued but not recorded, or skipped although URP rendered its shadow atlas
    /// (<see cref="VpCascadeShadowPass.LastSkipLosesShadows"/>), that frame lost the set's shadows; the route logs an error
    /// and issues the batch for the enabled camera until it is disabled and enabled again.
    /// </para>
    /// <para>
    /// The route lives in Zantetsu.Rendering.Urp because the pass and the renderer lookup depend on URP. It owns only its
    /// callback subscription, the cascade pass and its property blocks. The owner keeps the batch, the set, the geometry
    /// buffers and the materials, keeps both uploads equal, and disposes the route before them; disposing the route
    /// unsubscribes and queues nothing. Nothing here waits for the GPU. The route must not be used from inside a camera's
    /// rendering other than through its own callback.
    /// </para>
    /// </summary>
    public sealed class VpCulledCameraRoute : IDisposable
    {
        /// <summary>The only Unity version this route has been verified with.</summary>
        public const string VerifiedUnityVersion = "6000.3.22f1";

        private const string CullBackPassName = "ShadowCasterCullBack";
        private const string NotUrp = "the active render pipeline is not URP";
        private const string CompatibilityMode = "URP compatibility mode is on; the cascade shadow pass records only through the render graph";
        private const string UnverifiedUrp = "unverified URP version; VP3C is verified with URP 17.3.0 only";
        private const string UnverifiedUnity = "unverified Unity version; VP3C is verified with Unity " + VerifiedUnityVersion + " only";
        private const string UnverifiedGraphicsApi = "unverified graphics API; VP3C is verified with Direct3D 11 only";
        private const string AssetShadowsOff = "main light shadows are off, unsupported or per-vertex in the URP asset";
        private const string OverlayCamera = "an overlay camera renders no main light shadow map of its own";
        private const string CameraShadowsOff = "the camera does not render shadows";
        private const string NotUniversalRenderer = "the camera's renderer is not the Universal Renderer";
        private const string UnverifiedStereo = "the camera renders a stereo mode other than Single Pass Instanced";
        private const string StereoMismatch = "the set was uploaded for another stereo mode than the camera renders";
        private const string UploadMismatch = "the batch and the set hold different uploads";
        private const string NotRecorded = "the cascade shadow pass was enqueued but not recorded, so the set cast no shadow in that frame";
        private const string LostShadows = "the cascade shadow pass could not draw into URP's shadow atlas, so the set cast no shadow in that frame";

        private readonly VpIndexedIndirectDrawBatch _batch;
        private readonly VpCulledInstanceSet _set;
        private readonly VpGpuIndexedGeometryBuffers _buffers;
        private readonly Material _forwardMaterial;
        private readonly Material _shadowMaterial;
        private readonly Material _culledForwardMaterial;
        private readonly Material _culledShadowMaterial;
        private readonly int _layer;
        private readonly MaterialPropertyBlock _batchProperties;
        private readonly MaterialPropertyBlock _culledProperties;
        private readonly Plane[] _eyePlanes = new Plane[2 * VpInstanceCulling.EyePlaneCount];
        private VpCascadeShadowPass _pass;
        private Camera _camera;
        private bool _enabled;
        private bool _subscribed;
        private int _issuedFrame = -1;
        private bool _issuedCulled;
        private int _expectedRecordCount = -1;
        private int _earlierPassRecords;
        private string _fault;
        private bool _disposed;

        /// <summary>
        /// A route over the owner's VP3 batch and VP3C set. <paramref name="culledShadowMaterial"/> must use
        /// "Zantetsu/VP Culled Indexed Shadow Caster". The route owns none of the arguments.
        /// </summary>
        public VpCulledCameraRoute(
            VpIndexedIndirectDrawBatch batch,
            VpCulledInstanceSet set,
            VpGpuIndexedGeometryBuffers buffers,
            Material forwardMaterial,
            Material shadowMaterial,
            Material culledForwardMaterial,
            Material culledShadowMaterial,
            int layer)
        {
            _batch = batch ?? throw new ArgumentNullException(nameof(batch));
            _set = set ?? throw new ArgumentNullException(nameof(set));
            _buffers = buffers ?? throw new ArgumentNullException(nameof(buffers));
            _forwardMaterial = forwardMaterial != null ? forwardMaterial : throw new ArgumentNullException(nameof(forwardMaterial));
            _shadowMaterial = shadowMaterial != null ? shadowMaterial : throw new ArgumentNullException(nameof(shadowMaterial));
            _culledForwardMaterial = culledForwardMaterial != null ? culledForwardMaterial : throw new ArgumentNullException(nameof(culledForwardMaterial));
            _culledShadowMaterial = culledShadowMaterial != null ? culledShadowMaterial : throw new ArgumentNullException(nameof(culledShadowMaterial));
            if (culledShadowMaterial.FindPass(CullBackPassName) != 0)
            {
                throw new ArgumentException("The culled shadow material needs the VP culled shadow caster's " + CullBackPassName + " pass first.", nameof(culledShadowMaterial));
            }

            _layer = layer;
            _batchProperties = new MaterialPropertyBlock();
            _culledProperties = new MaterialPropertyBlock();
        }

        public bool IsEnabled => _enabled;

        /// <summary>The camera routed through VP3C, or null when disabled.</summary>
        public Camera EnabledCamera => _enabled ? _camera : null;

        /// <summary>Why the enabled camera's last render in an enabled frame got the single batch; null when it got VP3C.</summary>
        public string LastFallbackReason { get; private set; }

        /// <summary>Diagnostics: the beginCameraRendering callbacks received.</summary>
        public int CallbackCount { get; private set; }

        /// <summary>Diagnostics: the single batch issues, for every camera or for one.</summary>
        public int SingleIssueCount { get; private set; }

        /// <summary>Diagnostics: the single batch issues for the enabled camera in place of VP3C.</summary>
        public int FallbackIssueCount { get; private set; }

        /// <summary>Diagnostics: the VP3C issues (culled forward call and shadow caster bounds call) for the enabled camera.</summary>
        public int CulledIssueCount { get; private set; }

        /// <summary>Diagnostics: the times the cascade passes of this route were recorded, drawn or skipped.</summary>
        public int ShadowRecordCount => _earlierPassRecords + (_pass != null ? _pass.RecordCount : 0);

        /// <summary>Diagnostics of the current cascade pass's last recorded frame; 0, null or false when disabled.</summary>
        public int LastShadowSplitCount => _pass != null ? _pass.LastSplitCount : 0;

        public string LastShadowSkipReason => _pass?.LastSkipReason;

        public bool LastShadowCullFront => _pass != null && _pass.LastCullFront;

        /// <summary>
        /// Queues the display set for this frame: the single batch for every camera when disabled; when enabled, the
        /// callback issues it per camera. Does nothing more when called again in the same frame.
        /// </summary>
        public void Issue()
        {
            ThrowIfDisposed();
            int frame = Time.frameCount;
            if (_issuedFrame == frame)
            {
                return;
            }

            DisableIfCameraDestroyed();
            _issuedFrame = frame;
            _issuedCulled = _enabled;
            if (!_enabled)
            {
                IssueBatch(null);
            }
        }

        /// <summary>
        /// Routes <paramref name="camera"/> through VP3C from the next frame's <see cref="Issue"/>. Returns true when enabled,
        /// also when already enabled for this camera; false, with <paramref name="refusal"/> and no change, otherwise.
        /// </summary>
        public bool TryEnable(Camera camera, out string refusal)
        {
            ThrowIfDisposed();
            if (camera == null)
            {
                throw new ArgumentNullException(nameof(camera));
            }

            DisableIfCameraDestroyed();
            if (_enabled)
            {
                if (_camera == camera)
                {
                    refusal = null;
                    return true;
                }

                refusal = "VP3C is already enabled for camera '" + _camera.name + "'; disable it first";
                return false;
            }

            refusal = CheckEnvironment() ?? CheckCamera(camera, out _) ?? CheckUploads();
            if (refusal != null)
            {
                return false;
            }

            _camera = camera;
            _enabled = true;
            _fault = null;
            _expectedRecordCount = -1;
            LastFallbackReason = null;
            _pass = new VpCascadeShadowPass(_set, _buffers, _culledShadowMaterial);
            if (!_subscribed)
            {
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
                _subscribed = true;
            }

            return true;
        }

        /// <summary>
        /// Returns to the single batch: unsubscribes the callback and drops the cascade pass. When this frame's Issue was
        /// enabled, queues the single batch for the cameras still to render. Does nothing when disabled or disposed.
        /// </summary>
        public void Disable()
        {
            if (_disposed || !_enabled)
            {
                return;
            }

            Release();
            if (_issuedCulled && _issuedFrame == Time.frameCount)
            {
                _issuedCulled = false;
                IssueBatch(null);
            }
        }

        /// <summary>Unsubscribes and drops the pass without queuing anything. The batch, set, buffers and materials stay the owner's.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_enabled)
            {
                Release();
            }

            _issuedCulled = false;
            _disposed = true;
        }

        private void Release()
        {
            if (_subscribed)
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
                _subscribed = false;
            }

            _earlierPassRecords += _pass != null ? _pass.RecordCount : 0;
            _pass = null;
            _camera = null;
            _enabled = false;
            _expectedRecordCount = -1;
        }

        private void DisableIfCameraDestroyed()
        {
            if (_enabled && _camera == null)
            {
                Debug.LogWarning("VpCulledCameraRoute: the camera routed through VP3C was destroyed; the route returns to the VP3 single batch.");
                Disable();
            }
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            CallbackCount++;
            if (!_enabled || !_issuedCulled || _issuedFrame != Time.frameCount)
            {
                return;
            }

            if (camera != _camera)
            {
                IssueBatch(camera);
                return;
            }

            CheckLastRecord();
            string reason = _fault ?? CheckEnvironment() ?? CheckUploads();
            ScriptableRenderer renderer = null;
            if (reason == null)
            {
                reason = CheckCamera(camera, out renderer);
            }

            if (reason != null)
            {
                if (LastFallbackReason == null && _fault == null)
                {
                    Debug.LogWarning("VpCulledCameraRoute: camera '" + camera.name + "' gets the VP3 single batch instead of VP3C: " + reason + ".");
                }

                LastFallbackReason = reason;
                FallbackIssueCount++;
                IssueBatch(camera);
                return;
            }

            LastFallbackReason = null;
            int eyes = VpInstanceCulling.GetEyePlanes(camera, _eyePlanes);
            _set.SelectForward(_eyePlanes, eyes);
            _set.UploadForward();
            _set.RenderForward(_culledForwardMaterial, _culledProperties, _buffers, _layer, camera);
            _set.RenderShadowCasterBounds(_culledShadowMaterial, _buffers, _layer, camera);
            CulledIssueCount++;
            renderer.EnqueuePass(_pass);
            _expectedRecordCount = _pass.RecordCount + 1;
        }

        /// <summary>Turns a shadow loss of the enabled camera's previous VP3C render into a lasting fallback.</summary>
        private void CheckLastRecord()
        {
            if (_expectedRecordCount < 0 || _fault != null)
            {
                return;
            }

            if (_pass.RecordCount < _expectedRecordCount)
            {
                _fault = NotRecorded;
            }
            else if (_pass.LastSkipLosesShadows)
            {
                _fault = LostShadows;
            }

            _expectedRecordCount = -1;
            if (_fault != null)
            {
                Debug.LogError("VpCulledCameraRoute: " + _fault + " (" + (_pass.LastSkipReason ?? "not recorded") + "); camera '" + _camera.name
                    + "' gets the VP3 single batch until VP3C is disabled and enabled again.");
            }
        }

        private void IssueBatch(Camera camera)
        {
            _batch.Render(_forwardMaterial, _shadowMaterial, _batchProperties, _buffers, _layer, camera);
            SingleIssueCount++;
        }

        private static string CheckEnvironment()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset))
            {
                return NotUrp;
            }

            if (GraphicsSettings.TryGetRenderPipelineSettings(out RenderGraphSettings renderGraphSettings) && renderGraphSettings.enableRenderCompatibilityMode)
            {
                return CompatibilityMode;
            }

#if !ZANTETSU_URP_17_3_0
            return UnverifiedUrp;
#else
            if (Application.unityVersion != VerifiedUnityVersion)
            {
                return UnverifiedUnity;
            }

            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
            {
                return UnverifiedGraphicsApi;
            }

            if (!SystemInfo.supportsShadows || !asset.supportsMainLightShadows || asset.mainLightRenderingMode != LightRenderingMode.PerPixel)
            {
                return AssetShadowsOff;
            }

            return null;
#endif
        }

        /// <summary>Checks the camera and resolves the renderer URP uses for it, as URP does.</summary>
        private string CheckCamera(Camera camera, out ScriptableRenderer renderer)
        {
            renderer = null;
            camera.TryGetComponent(out UniversalAdditionalCameraData cameraData);
            if (cameraData != null && cameraData.renderType != CameraRenderType.Base)
            {
                return OverlayCamera;
            }

            if (cameraData != null && !cameraData.renderShadows)
            {
                return CameraShadowsOff;
            }

            renderer = cameraData != null ? cameraData.scriptableRenderer : null;
            if (renderer == null || camera.cameraType == CameraType.SceneView)
            {
                renderer = (GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset)?.scriptableRenderer;
            }

            if (!(renderer is UniversalRenderer))
            {
                renderer = null;
                return NotUniversalRenderer;
            }

            bool stereo = camera.stereoEnabled;
            if (stereo && XRSettings.stereoRenderingMode != XRSettings.StereoRenderingMode.SinglePassInstanced)
            {
                renderer = null;
                return UnverifiedStereo;
            }

            if (stereo != _set.SinglePassInstanced)
            {
                renderer = null;
                return StereoMismatch;
            }

            return null;
        }

        private string CheckUploads()
        {
            if (_batch.CommandCount != _set.CommandCount || _batch.InstanceCount != _set.InstanceCount || _batch.SinglePassInstanced != _set.SinglePassInstanced
                || _batch.WorldBounds != _set.WorldBounds)
            {
                return UploadMismatch;
            }

            return null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpCulledCameraRoute));
            }
        }
    }
}
