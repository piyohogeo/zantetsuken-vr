using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The AppSW motion of a skin (2026-10-19): the VP Mesh Surface's XRMotionVectors pass without VP_CAMERA_ONLY_ASW
    /// (URP's ObjectMotionVectors.hlsl) writes the object's and the bones' motion -- this and the previous frame's object
    /// matrix and skinned vertices -- while the camera-only variant (the ordinary Mesh copies) writes none of it. The
    /// pass is drawn as URP's XRDepthMotionPass draws it: a renderer list of the XRMotionVectors tag with the per-object
    /// motion data and every opaque object forced in, into a float target, with the camera fixed (this and the previous
    /// view-projection the same), and read back. Only the motion is compared; a moving skin's motion is not expected
    /// to be any exact value, only present (or absent) and of the right size.
    /// </summary>
    public sealed class SkinMotionVectorsPlayModeTests
    {
        private const int Size = 96;
        private const int Layer = 30;   // a layer nothing else in a test scene draws on
        private readonly List<Object> _made = new List<Object>();
        private Camera _camera;
        private RenderTexture _colour, _motion, _motionDepth, _depthCopy;
        private Material _depthCopier;
        private readonly Dictionary<int, float[]> _frames = new Dictionary<int, float[]>();
        private readonly Dictionary<int, float[]> _depths = new Dictionary<int, float[]>();
        private int _readErrors;
        private bool _hooked;

        // Other cameras of the scene, off while a case runs: "out of view" and Renderer.isVisible are this view's alone.
        private readonly List<Camera> _others = new List<Camera>();

        [SetUp]
        public void SetUp()
        {
            foreach (Camera other in Camera.allCameras) { other.enabled = false; _others.Add(other); }
            _colour = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { name = "skin MV test colour" };
            _motion = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGBFloat) { name = "skin MV test motion" };
            _motionDepth = new RenderTexture(Size, Size, 24, RenderTextureFormat.Depth) { name = "skin MV test motion depth" };
            _depthCopy = new RenderTexture(Size, Size, 0, RenderTextureFormat.RFloat) { name = "skin MV test motion depth copy" };
            _colour.Create(); _motion.Create(); _motionDepth.Create(); _depthCopy.Create();
            Shader copier = Shader.Find("Hidden/Zantetsu/Test Skin Motion Depth Copy");
            Assert.That(copier, Is.Not.Null, "the test's depth copy shader");
            _depthCopier = new Material(copier);
            _made.Add(_depthCopier);
            var go = new GameObject("skin MV test camera");
            _made.Add(go);
            _camera = go.AddComponent<Camera>();
            _camera.transform.SetPositionAndRotation(new Vector3(0f, 1f, -3f), Quaternion.identity);
            _camera.fieldOfView = 60f; _camera.nearClipPlane = 0.1f; _camera.farClipPlane = 20f;
            _camera.cullingMask = 1 << Layer;
            _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = Color.black;
            _camera.targetTexture = _colour;
            _camera.depthTextureMode |= DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            RenderPipelineManager.endCameraRendering += DrawMotion;
            _hooked = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (_hooked) RenderPipelineManager.endCameraRendering -= DrawMotion;
            _hooked = false;
            AsyncGPUReadback.WaitAllRequests();
            foreach (Object o in _made) if (o != null) Object.Destroy(o);
            _made.Clear();
            foreach (RenderTexture rt in new[] { _colour, _motion, _motionDepth, _depthCopy }) if (rt != null) { rt.Release(); Object.Destroy(rt); }
            _frames.Clear(); _depths.Clear(); _readErrors = 0;
            foreach (Camera other in _others) if (other != null) other.enabled = true;
            _others.Clear();
        }

        // URP's XRDepthMotionPass's renderer list (XRMotionVectors, PerObjectData.MotionVectors, every opaque object
        // forced in), drawn into the float target with the fixed camera's view-projection as this and the previous one.
        private void DrawMotion(ScriptableRenderContext context, Camera camera)
        {
            if (camera != _camera || !camera.TryGetCullingParameters(out ScriptableCullingParameters parameters)) return;
            CullingResults culled = context.Cull(ref parameters);
            var drawing = new DrawingSettings(new ShaderTagId("XRMotionVectors"), new SortingSettings(camera) { criteria = SortingCriteria.CommonOpaque })
            {
                perObjectData = PerObjectData.MotionVectors,
            };
            var filtering = new FilteringSettings(RenderQueueRange.opaque, 1 << Layer) { forceAllMotionVectorObjects = true };
            var listParams = new RendererListParams(culled, drawing, filtering);
            var list = context.CreateRendererList(ref listParams);
            Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix;
            var cmd = new CommandBuffer { name = "skin MV test" };
            cmd.SetRenderTarget(_motion.colorBuffer, _motionDepth.depthBuffer);
            cmd.ClearRenderTarget(true, true, new Color(0f, 0f, 0f, 0f));
            cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, GL.GetGPUProjectionMatrix(camera.projectionMatrix, true));
            cmd.SetGlobalMatrix("_NonJitteredViewProjMatrix", viewProjection);
            cmd.SetGlobalMatrix("_PrevViewProjMatrix", viewProjection);
            cmd.DrawRendererList(list);
            // The pass's depth (what AppSW's depth target gets), into a float colour target to be read back.
            cmd.SetRenderTarget(_depthCopy);
            cmd.SetGlobalTexture("_SkinMvTestDepth", _motionDepth, RenderTextureSubElement.Depth);
            cmd.DrawProcedural(Matrix4x4.identity, _depthCopier, 0, MeshTopology.Triangles, 3);
            context.ExecuteCommandBuffer(cmd);
            cmd.Release();
            context.Submit();
            // Kept in the frame the read completes (its data is not kept past that frame).
            int frame = Time.frameCount;
            AsyncGPUReadback.Request(_motion, 0, TextureFormat.RGBAFloat, read =>
            {
                if (read.hasError) _readErrors++;
                else _frames[frame] = read.GetData<float>().ToArray();
            });
            AsyncGPUReadback.Request(_depthCopy, 0, TextureFormat.RFloat, read =>
            {
                if (read.hasError) _readErrors++;
                else _depths[frame] = read.GetData<float>().ToArray();
            });
        }

        // The frame's motion pass depth as read back (one float a pixel; the cleared value where nothing was drawn).
        private IEnumerator DepthOf(int frame, Action<float[]> take)
        {
            for (int wait = 0; wait < 30 && !_depths.ContainsKey(frame); wait++) yield return null;
            Assert.That(_depths.ContainsKey(frame), Is.True, "the depth of frame " + frame + " was read back (read errors " + _readErrors + ")");
            take(_depths[frame]);
        }

        // The frame's motion target as read back: RGBA floats, alpha 1 where the pass drew.
        private IEnumerator MotionOf(int frame, Action<float[]> take)
        {
            for (int wait = 0; wait < 30 && !_frames.ContainsKey(frame); wait++) yield return null;
            Assert.That(_frames.ContainsKey(frame), Is.True, "the motion of frame " + frame + " was read back (read errors " + _readErrors + ")");
            take(_frames[frame]);
        }

        // The largest |motion.xy| where the pass drew, and how many pixels it drew.
        private static (float largest, int drawn) Largest(float[] rgba)
        {
            float largest = 0f; int drawn = 0;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i + 3] < 0.5f) continue;
                drawn++;
                largest = Mathf.Max(largest, Mathf.Sqrt((rgba[i] * rgba[i]) + (rgba[i + 1] * rgba[i + 1])));
            }

            return (largest, drawn);
        }

        private Material Surface(bool cameraOnly)
        {
            Shader shader = Shader.Find("Zantetsu/VP Mesh Surface");
            Assert.That(shader, Is.Not.Null, "the VP Mesh Surface shader");
            var material = new Material(shader) { name = cameraOnly ? "camera-only copy" : "object-motion copy" };
            if (cameraOnly) material.EnableKeyword("VP_CAMERA_ONLY_ASW"); else material.DisableKeyword("VP_CAMERA_ONLY_ASW");
            _made.Add(material);
            return material;
        }

        // A two-bone skin facing the camera: a 1 x 2 m sheet, the lower half on the root bone, the upper on the top bone
        // (one-sided, facing the camera). Object motion and skinned motion vectors on.
        private SkinnedMeshRenderer Skin(Vector3 at, Material material, out Transform root, out Transform top)
        {
            var go = new GameObject("skin under test") { layer = Layer };
            _made.Add(go);
            go.transform.position = at;
            root = new GameObject("root bone").transform; root.SetParent(go.transform, false);
            top = new GameObject("top bone").transform; top.SetParent(root, false); top.localPosition = new Vector3(0f, 1f, 0f);
            var mesh = new Mesh { name = "skin MV test sheet" };
            _made.Add(mesh);
            var vertices = new List<Vector3>(); var weights = new List<BoneWeight>();
            for (int row = 0; row <= 4; row++)
                for (int column = 0; column <= 1; column++)
                {
                    vertices.Add(new Vector3(column - 0.5f, row * 0.5f, 0f));
                    weights.Add(new BoneWeight { boneIndex0 = row <= 2 ? 0 : 1, weight0 = 1f });
                }

            var triangles = new List<int>();
            for (int row = 0; row < 4; row++)
            {
                int a = row * 2, b = a + 1, c = a + 2, d = a + 3;
                triangles.AddRange(new[] { a, c, b, b, c, d });   // facing the camera (-z), as the passes cull the back
            }

            mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.boneWeights = weights.ToArray();
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            mesh.bindposes = new[] { root.worldToLocalMatrix * go.transform.localToWorldMatrix, top.worldToLocalMatrix * go.transform.localToWorldMatrix };
            var skin = go.AddComponent<SkinnedMeshRenderer>();
            skin.sharedMesh = mesh; skin.bones = new[] { root, top }; skin.rootBone = root;
            skin.sharedMaterial = material; skin.updateWhenOffscreen = true;
            skin.localBounds = new Bounds(new Vector3(0f, 1f, 0f), new Vector3(4f, 4f, 4f));
            skin.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            skin.skinnedMotionVectors = true;
            return skin;
        }

        private MeshRenderer Sheet(Vector3 at, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _made.Add(go);
            go.layer = Layer; go.transform.position = at; go.transform.localScale = new Vector3(1f, 2f, 1f);
            Object.Destroy(go.GetComponent<Collider>());
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            return renderer;
        }

        // Settle a few frames (the history fills), then the motion of the frame after the change.
        private IEnumerator Settle(int frames = 4) { for (int i = 0; i < frames; i++) yield return null; }

        [UnityTest]
        public IEnumerator ASkinsBonesMoving_ObjectAndCameraFixed_WriteMotion_TheCameraOnlyVariantWritesNone()
        {
            foreach (bool cameraOnly in new[] { false, true })
            {
                SkinnedMeshRenderer skin = Skin(new Vector3(0f, 0f, 0f), Surface(cameraOnly), out _, out Transform top);
                yield return Settle();
                // Still: no motion.
                float still = -1f; int drawn = 0;
                yield return MotionOf(Time.frameCount - 1, d => { (still, drawn) = Largest(d); });
                Assert.That(drawn, Is.GreaterThan(50), "the pass drew the skin");
                Assert.That(still, Is.LessThan(1e-4f), "a still skin writes no motion (" + skin.sharedMaterial.name + ")");
                // The top bone turned 10 degrees a frame, the object and the camera fixed.
                float moving = -1f;
                for (int i = 0; i < 3; i++) { top.localRotation *= Quaternion.Euler(0f, 0f, 10f); yield return null; }
                yield return MotionOf(Time.frameCount - 1, d => { (moving, _) = Largest(d); });
                TestContext.Out.WriteLine(skin.sharedMaterial.name + ": still " + still + ", bones turning " + moving);
                if (cameraOnly) Assert.That(moving, Is.LessThan(1e-4f), "camera-only: the bones' motion is not written");
                else Assert.That(moving, Is.GreaterThan(0.02f), "object motion: the bones' motion is written");
                Object.Destroy(skin.gameObject);
                yield return Settle(2);
            }
        }

        [UnityTest]
        public IEnumerator ASkinMoving_BonesStill_WritesTheObjectsMotion_OfItsSize()
        {
            SkinnedMeshRenderer skin = Skin(Vector3.zero, Surface(false), out _, out _);
            yield return Settle();
            const float step = 0.05f;   // metres a frame, sideways
            for (int i = 0; i < 3; i++) { skin.transform.position += new Vector3(step, 0f, 0f); yield return null; }
            float moving = -1f;
            yield return MotionOf(Time.frameCount - 1, d => { (moving, _) = Largest(d); });
            // A sideways move of 5 cm at 3 m with a 60 degree vertical field: about 0.05 / (3 tan 30) = 0.029 of the NDC
            // half-width -- in NDC units 0.029 (the ASW encoding is the NDC difference). Allow the sheet's own depth range.
            TestContext.Out.WriteLine("skin moving 5 cm a frame: largest motion " + moving);
            Assert.That(moving, Is.InRange(0.015f, 0.06f), "the object's motion, of about its size");
        }

        [UnityTest]
        public IEnumerator AnOrdinaryMeshsCameraOnlyCopy_MovingOrNot_WritesNoMotion_AsBefore()
        {
            MeshRenderer sheet = Sheet(new Vector3(0f, 1f, 0f), Surface(true));
            yield return Settle();
            for (int i = 0; i < 3; i++) { sheet.transform.position += new Vector3(0.05f, 0f, 0f); yield return null; }
            float moving = -1f; int drawn = 0;
            yield return MotionOf(Time.frameCount - 1, d => { (moving, drawn) = Largest(d); });
            Assert.That(drawn, Is.GreaterThan(50), "the pass drew the Mesh (depth and stencil for AppSW)");
            Assert.That(moving, Is.LessThan(1e-4f), "the ordinary Mesh's path is the camera's alone, unchanged");
        }

        /// <summary>
        /// A skin shown again after frames hidden (the crowd's reuse and its display coming back), and one moved far in
        /// one frame (a teleport): what motion the first frame writes. Recorded for the report; asserted only to be
        /// read (whether Unity's history makes a stale motion here is what this case tells).
        /// </summary>
        [UnityTest]
        public IEnumerator ASkinShownAgain_OrMovedFarInOneFrame_TheFirstFramesMotion_IsRecorded()
        {
            SkinnedMeshRenderer skin = Skin(Vector3.zero, Surface(false), out _, out Transform top);
            yield return Settle();
            // Hidden for 5 frames while its bones and its object move; then shown again, still.
            skin.enabled = false;
            for (int i = 0; i < 5; i++) { top.localRotation *= Quaternion.Euler(0f, 0f, 15f); skin.transform.position += new Vector3(0.1f, 0f, 0f); yield return null; }
            skin.enabled = true;
            yield return null;
            float resumed = -1f;
            yield return MotionOf(Time.frameCount - 1, d => { (resumed, _) = Largest(d); });
            yield return Settle(2);
            float resumedAfter = -1f;
            yield return MotionOf(Time.frameCount - 1, d => { (resumedAfter, _) = Largest(d); });
            // Moved 2 m in one frame, still before and after.
            skin.transform.position += new Vector3(-0.5f, 0f, 0f);
            yield return Settle(3);
            skin.transform.position += new Vector3(1.0f, 0f, 0f);
            yield return null;
            float teleported = -1f;
            yield return MotionOf(Time.frameCount - 1, d => { (teleported, _) = Largest(d); });
            yield return Settle(2);
            float teleportedAfter = -1f;
            yield return MotionOf(Time.frameCount - 1, d => { (teleportedAfter, _) = Largest(d); });
            TestContext.Out.WriteLine("SKIN MV HISTORY: shown again after 5 hidden frames (still): first frame " + resumed + ", two frames later " + resumedAfter
                + "; moved 1 m in one frame: that frame " + teleported + ", two frames later " + teleportedAfter);
            Assert.That(resumedAfter, Is.LessThan(1e-4f), "still after it is shown again: no motion once the history is this frame's");
            Assert.That(teleportedAfter, Is.LessThan(1e-4f), "still after the move: no motion");
        }

        // How a skin is taken out of the picture and brought back: the crowd's reuse (Renderer.enabled), its root
        // switched off and on, and the view turned away (frustum culling, not updated off screen).
        private enum Away { RendererDisabled, RootInactive, OutOfView }

        /// <summary>
        /// Unity's own history control for the first frame back (2026-10-19): the skin away for 5 frames while its bones
        /// and its object move, then back, still, with its first frame's motion mode Object (as it is), ForceNoMotion or
        /// Camera, and Object again from the next frame. The first and the next two frames' largest motion recorded per
        /// case; asserted only that the frames after the first are still (the history is this frame's again).
        /// </summary>
        [UnityTest]
        public IEnumerator ASkinBack_AfterFramesAway_TheFirstFramesMotion_ByTheFirstFramesMode_IsRecorded()
        {
            var lines = new List<string>();
            foreach (Away away in new[] { Away.RendererDisabled, Away.RootInactive, Away.OutOfView })
                foreach (MotionVectorGenerationMode first in new[] { MotionVectorGenerationMode.Object, MotionVectorGenerationMode.ForceNoMotion, MotionVectorGenerationMode.Camera })
                {
                    SkinnedMeshRenderer skin = Skin(Vector3.zero, Surface(false), out _, out Transform top);
                    skin.updateWhenOffscreen = false;
                    yield return Settle();
                    switch (away)
                    {
                        case Away.RendererDisabled: skin.enabled = false; break;
                        case Away.RootInactive: skin.gameObject.SetActive(false); break;
                        case Away.OutOfView: _camera.transform.rotation = Quaternion.Euler(0f, 180f, 0f); break;
                    }

                    for (int i = 0; i < 5; i++) { top.localRotation *= Quaternion.Euler(0f, 0f, 15f); skin.transform.position += new Vector3(0.1f, 0f, 0f); yield return null; }
                    skin.motionVectorGenerationMode = first;
                    switch (away)
                    {
                        case Away.RendererDisabled: skin.enabled = true; break;
                        case Away.RootInactive: skin.gameObject.SetActive(true); break;
                        case Away.OutOfView: _camera.transform.rotation = Quaternion.identity; break;
                    }

                    yield return null;
                    skin.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                    int back = Time.frameCount - 1;
                    yield return Settle(2);
                    float[] largest = new float[3]; int drawn = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        int at = k;
                        yield return MotionOf(back + k, d => { (largest[at], drawn) = Largest(d); });
                    }

                    lines.Add(away + " first=" + first + ": back " + largest[0] + ", +1 " + largest[1] + ", +2 " + largest[2] + " (drawn " + drawn + ")");
                    Assert.That(drawn, Is.GreaterThan(50), away + " " + first + ": drawn again");
                    Assert.That(largest[2], Is.LessThan(1e-4f), away + " " + first + ": still two frames after it is back");
                    Object.Destroy(skin.gameObject);
                    yield return Settle(2);
                }

            TestContext.Out.WriteLine("SKIN MV BACK: " + string.Join(" | ", lines));
        }

        // What moves while the skin is away, and what of Unity's own history control is tried for its first frame back.
        private enum WhileAway { BonesOnly, ObjectOnly }
        private enum FirstBack { AsItIs, NoSkinnedVectorsTheFirstFrame, ForceNoMotionFromAFrameBefore }

        /// <summary>
        /// The stale first frame back, taken apart (2026-10-19): only the bones or only the object moved while away, and
        /// Unity's controls tried -- skinned motion vectors off for the first frame (the previous skinned vertices not
        /// read), and ForceNoMotion set a frame before it is back. Recorded per case; asserted only that two frames
        /// after it is back it is still.
        /// </summary>
        [UnityTest]
        public IEnumerator TheStaleFirstFrameBack_ItsSourceAndUnitysControls_AreRecorded()
        {
            var lines = new List<string>();
            foreach (Away away in new[] { Away.RendererDisabled, Away.OutOfView })
                foreach (WhileAway moved in new[] { WhileAway.BonesOnly, WhileAway.ObjectOnly })
                    foreach (FirstBack control in new[] { FirstBack.AsItIs, FirstBack.NoSkinnedVectorsTheFirstFrame, FirstBack.ForceNoMotionFromAFrameBefore })
                    {
                        SkinnedMeshRenderer skin = Skin(Vector3.zero, Surface(false), out _, out Transform top);
                        skin.updateWhenOffscreen = false;
                        yield return Settle();
                        if (away == Away.RendererDisabled) skin.enabled = false; else _camera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                        for (int i = 0; i < 5; i++)
                        {
                            if (moved == WhileAway.BonesOnly) top.localRotation *= Quaternion.Euler(0f, 0f, 15f);
                            else skin.transform.position += new Vector3(0.1f, 0f, 0f);
                            if (i == 4 && control == FirstBack.ForceNoMotionFromAFrameBefore) skin.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                            yield return null;
                        }

                        if (control == FirstBack.NoSkinnedVectorsTheFirstFrame) skin.skinnedMotionVectors = false;
                        if (away == Away.RendererDisabled) skin.enabled = true; else _camera.transform.rotation = Quaternion.identity;
                        yield return null;
                        skin.skinnedMotionVectors = true;
                        skin.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                        int back = Time.frameCount - 1;
                        yield return Settle(2);
                        float[] largest = new float[3];
                        for (int k = 0; k < 3; k++)
                        {
                            int at = k;
                            yield return MotionOf(back + k, d => { (largest[at], _) = Largest(d); });
                        }

                        lines.Add(away + "/" + moved + "/" + control + ": back " + largest[0] + ", +1 " + largest[1] + ", +2 " + largest[2]);
                        Assert.That(largest[2], Is.LessThan(1e-4f), away + " " + moved + " " + control + ": still two frames after it is back");
                        Object.Destroy(skin.gameObject);
                        yield return Settle(2);
                    }

            TestContext.Out.WriteLine("SKIN MV STALE: " + string.Join(" | ", lines));
        }

        // Turns the top bone 10 degrees every frame and, when asked, settles the skin's history as an NPC character does
        // in its Update. The angle can be set, so that two runs stand at one pose in a chosen frame.
        private sealed class TurnsAndSettles : MonoBehaviour
        {
            internal Transform top;
            internal SkinnedMeshRenderer skin;
            internal bool settles;
            internal int changes;
            internal float angle;

            internal void Pose(float degrees)
            {
                angle = degrees;
                top.localRotation = Quaternion.Euler(0f, 0f, angle);
            }

            private void Update()
            {
                Pose(angle + 10f);
                if (settles && VpSkinMotionHistory.Settle(skin)) changes++;
            }
        }

        // Where the pass drew (alpha 1) in a frame's motion target.
        private static bool[] Covered(float[] rgba)
        {
            var covered = new bool[rgba.Length / 4];
            for (int i = 0; i < covered.Length; i++) covered[i] = rgba[(i * 4) + 3] > 0.5f;
            return covered;
        }

        /// <summary>
        /// VpSkinMotionHistory, as the NPC characters use it: a skin whose bones turn 10 degrees every frame, away for 5
        /// frames (renderer off as a reused slot is, its root off, out of view and not skinned), then back. Settled, its
        /// first frame back writes no bone motion (the object is still: none at all) and from the next frame the bones'
        /// motion of one frame, as before it went; not settled, the first frame carries the motion since it went.
        /// The first frame back is drawn all the same (TL 2026-10-19: a zero must not be a frame not drawn): both runs
        /// stand at one pose in it, and the settled one covers the same pixels at the same depth as the one as it is --
        /// AppSW's depth and coverage are kept, only the stale bone motion is left out.
        /// </summary>
        [UnityTest]
        public IEnumerator ASettledSkin_BackAfterFramesAway_WritesNoStaleBoneMotion_AndItsBonesMotionFromTheNextFrame()
        {
            const float BackPose = 40f;   // the top bone's angle in the first frame back, in both runs
            var lines = new List<string>();
            var asItIs = new Dictionary<Away, (bool[] covered, float[] depth)>();
            foreach (Away away in new[] { Away.RendererDisabled, Away.RootInactive, Away.OutOfView })
                foreach (bool settles in new[] { false, true })
                {
                    SkinnedMeshRenderer skin = Skin(Vector3.zero, Surface(false), out _, out Transform top);
                    skin.updateWhenOffscreen = false;
                    var driver = new GameObject("skin MV test driver").AddComponent<TurnsAndSettles>();
                    _made.Add(driver.gameObject);
                    driver.top = top; driver.skin = skin; driver.settles = settles;
                    yield return Settle(5);
                    float steady = -1f;
                    yield return MotionOf(Time.frameCount - 1, d => { (steady, _) = Largest(d); });
                    switch (away)
                    {
                        case Away.RendererDisabled: skin.enabled = false; break;
                        case Away.RootInactive: skin.gameObject.SetActive(false); break;
                        case Away.OutOfView: _camera.transform.rotation = Quaternion.Euler(0f, 180f, 0f); break;
                    }

                    yield return Settle(5);
                    switch (away)
                    {
                        case Away.RendererDisabled: skin.enabled = true; break;
                        case Away.RootInactive: skin.gameObject.SetActive(true); break;
                        case Away.OutOfView: _camera.transform.rotation = Quaternion.identity; break;
                    }

                    driver.Pose(BackPose);   // this frame's Update has run: the frame is drawn at this pose
                    yield return null;
                    int back = Time.frameCount - 1;
                    yield return Settle(2);
                    float[] largest = new float[3];
                    int[] drawn = new int[3];
                    bool[] covered = null;
                    float[] depth = null;
                    for (int k = 0; k < 3; k++)
                    {
                        int at = k;
                        yield return MotionOf(back + k, d => { (largest[at], drawn[at]) = Largest(d); if (at == 0) covered = Covered(d); });
                    }

                    yield return DepthOf(back, d => depth = d);
                    // The depth was written where it was drawn: not the cleared value an undrawn pixel keeps.
                    int cleared = Array.IndexOf(covered, false), written = 0;
                    Assert.That(cleared, Is.GreaterThanOrEqualTo(0), "some pixel is not covered");
                    for (int i = 0; i < covered.Length; i++) if (covered[i] && depth[i] != depth[cleared]) written++;
                    Assert.That(written, Is.GreaterThan(50), away + (settles ? " settled" : " as it is") + ": the first frame back writes its depth");
                    int coverDiffer = 0, depthDiffer = 0;
                    float depthLargest = 0f;
                    if (settles)
                    {
                        (bool[] covered, float[] depth) reference = asItIs[away];
                        for (int i = 0; i < covered.Length; i++)
                        {
                            if (covered[i] != reference.covered[i]) coverDiffer++;
                            float d = Mathf.Abs(depth[i] - reference.depth[i]);
                            if (d > 1e-6f) depthDiffer++;
                            depthLargest = Mathf.Max(depthLargest, d);
                        }
                    }
                    else
                    {
                        asItIs[away] = (covered, depth);
                    }

                    lines.Add(away + (settles ? " settled" : " as it is") + ": steady " + steady + "; back " + largest[0] + " (drawn " + drawn[0] + ", depth written " + written + ")"
                        + ", +1 " + largest[1] + ", +2 " + largest[2]
                        + (settles ? " (changes " + driver.changes + "; back vs as it is: coverage differs at " + coverDiffer + " px, depth at " + depthDiffer + " px, largest " + depthLargest + ")" : ""));
                    Assert.That(steady, Is.GreaterThan(0.05f), away + ": the bones' motion of a frame, before it went");
                    Assert.That(drawn[0], Is.GreaterThan(50), away + (settles ? " settled" : " as it is") + ": the first frame back is drawn");
                    if (settles)
                    {
                        Assert.That(largest[0], Is.LessThan(1e-4f), away + ": settled, the first frame back writes no bone motion");
                        Assert.That(coverDiffer, Is.Zero, away + ": settled, the first frame back covers the pixels the one as it is covers");
                        Assert.That(depthDiffer, Is.Zero, away + ": settled, at the same depth (largest difference " + depthLargest + ")");
                        Assert.That(largest[1], Is.InRange(steady * 0.5f, steady * 1.5f), away + ": settled, the next frame writes the bones' motion of one frame");
                        Assert.That(largest[2], Is.InRange(steady * 0.5f, steady * 1.5f), away + ": and the one after");
                    }

                    Object.Destroy(driver.gameObject);
                    Object.Destroy(skin.gameObject);
                    yield return Settle(2);
                }

            TestContext.Out.WriteLine("SKIN MV SETTLED: " + string.Join(" | ", lines));
        }

        /// <summary>A skin's first frame drawn at all (made and shown at once, still): its motion, by the first frame's mode.</summary>
        [UnityTest]
        public IEnumerator ANewSkin_ItsFirstFramesMotion_IsRecorded()
        {
            var lines = new List<string>();
            foreach (MotionVectorGenerationMode first in new[] { MotionVectorGenerationMode.Object, MotionVectorGenerationMode.ForceNoMotion })
            {
                yield return Settle(2);
                SkinnedMeshRenderer skin = Skin(Vector3.zero, Surface(false), out _, out Transform top);
                top.localRotation = Quaternion.Euler(0f, 0f, 40f);   // posed away from the bind pose before its first frame
                skin.motionVectorGenerationMode = first;
                yield return null;
                skin.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                int shown = Time.frameCount - 1;
                yield return Settle(2);
                float[] largest = new float[2];
                for (int k = 0; k < 2; k++)
                {
                    int at = k;
                    yield return MotionOf(shown + k, d => { (largest[at], _) = Largest(d); });
                }

                lines.Add("first=" + first + ": first frame " + largest[0] + ", +1 " + largest[1]);
                Object.Destroy(skin.gameObject);
            }

            TestContext.Out.WriteLine("SKIN MV NEW: " + string.Join(" | ", lines));
        }

        [UnityTest]
        public IEnumerator TheColour_IsTheSame_WithEitherMotionVariant()
        {
            // Lit as the product is (the shading's colour scale is a global the product sets), by one light.
            Vector4 scaleWas = Shader.GetGlobalVector("_VpSurfaceColorScale");
            Shader.SetGlobalVector("_VpSurfaceColorScale", Vector4.one);
            var lightObject = new GameObject("skin MV test light");
            _made.Add(lightObject);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1f; light.shadows = LightShadows.None;
            lightObject.transform.rotation = Quaternion.Euler(30f, 20f, 0f);
            var pictures = new List<Color32[]>();
            foreach (bool cameraOnly in new[] { true, false })
            {
                SkinnedMeshRenderer skin = Skin(Vector3.zero, Surface(cameraOnly), out _, out _);
                yield return Settle();
                var request = AsyncGPUReadback.Request(_colour, 0, TextureFormat.RGBA32);
                request.WaitForCompletion();
                pictures.Add(request.GetData<Color32>().ToArray());
                Object.Destroy(skin.gameObject);
                yield return Settle(2);
            }

            int differing = 0, lit = 0;
            for (int i = 0; i < pictures[0].Length; i++)
            {
                Color32 a = pictures[0][i], b = pictures[1][i];
                if (a.r + a.g + a.b > 0) lit++;
                if (a.r != b.r || a.g != b.g || a.b != b.b) differing++;
            }

            Shader.SetGlobalVector("_VpSurfaceColorScale", scaleWas);
            Assert.That(lit, Is.GreaterThan(50), "the skin was drawn");
            Assert.That(differing, Is.Zero, "the colour pass is the same with either variant (only the motion pass's keyword differs)");
        }
    }
}
