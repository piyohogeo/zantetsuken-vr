using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// What Unity itself does with a SkinnedMeshRenderer that is not updated when offscreen (TL, 2026-10-05; DESIGN 9,
    /// D-196): the facts the uncut characters' "skinned only when seen" rests on, looked at in this Unity, with the
    /// characters' own surface shader. Unity's skinning is counted by its own Profiler marker
    /// (MeshSkinning.CalcMatrices: once a renderer it skins), and what is drawn is read back from the camera's target.
    /// <list type="bullet">
    /// <item>A renderer no camera sees is not skinned; one updated when offscreen is, every frame.</item>
    /// <item>The first drawing that sees it again shows the bones as they stand then, not as they stood when last skinned.</item>
    /// <item>A renderer brought into view by moving it (its bounds are its own, carried by its Transform) is drawn.</item>
    /// <item>A renderer no eye sees whose shadow falls into view still casts it, and Unity still skins it for that.</item>
    /// </list>
    /// </summary>
    public class SkinnedRendererUnseenPlayModeTests
    {
        private const int Size = 128;
        private readonly List<Object> _made = new List<Object>();
        private ProfilerRecorder _calc;
        private Camera _camera;
        private Light _light;
        private RenderTexture _target;
        private Material _surface;

        private T Made<T>(T o) where T : Object { _made.Add(o); return o; }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (ProfilerRecorderHandle handle in handles)
            {
                if (ProfilerRecorderHandle.GetDescription(handle).Name == "MeshSkinning.CalcMatrices")
                {
                    _calc = new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.Default);
                    _calc.Start();
                    break;
                }
            }

            Assert.That(_calc.Valid, Is.True, "Unity's marker MeshSkinning.CalcMatrices is among the Profiler's recorders");
            Shader shader = Shader.Find("Zantetsu/VP Mesh Surface");
            Assert.That(shader, Is.Not.Null);
            _surface = Made(new Material(shader) { name = "unseen surface" });
            _surface.SetFloat("_VpUsePaletteAtlas", 0f);
            _surface.SetColor("_BaseColor", Color.white);
            _light = Made(new GameObject("unseen sun")).AddComponent<Light>();
            _light.type = LightType.Directional;
            _light.shadows = LightShadows.Hard;
            _light.intensity = 1f;
            _light.transform.rotation = Quaternion.LookRotation(new Vector3(0f, -1f, 1f).normalized);   // down and forward (+Z)
            _target = Made(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
            _target.Create();
            _camera = Made(new GameObject("unseen camera")).AddComponent<Camera>();
            _camera.enabled = false;   // drawn by this test, once a frame
            _camera.fieldOfView = 60f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 60f;
            _camera.targetTexture = _target;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_calc.Valid) _calc.Dispose();
            foreach (Object o in _made) if (o != null) Object.Destroy(o is Component c ? c.gameObject : o);
            _made.Clear();
            yield return null;
        }

        // A cube of one metre, every vertex on one bone under a root; the renderer beside the bone.
        private (SkinnedMeshRenderer renderer, Transform root, Transform bone) Skin(string name, Vector3 at)
        {
            GameObject root = Made(new GameObject(name));
            root.transform.position = at;
            Transform bone = new GameObject("bone").transform;
            bone.SetParent(root.transform, false);
            var go = new GameObject("renderer");
            go.transform.SetParent(root.transform, false);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = Made(Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh));
            Object.Destroy(cube);
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
            mesh.bindposes = new[] { Matrix4x4.identity };
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bone };
            renderer.sharedMaterial = _surface;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = false;
            return (renderer, root.transform, bone);
        }

        private Color32[] Draw()
        {
            var request = new RenderPipeline.StandardRequest { destination = _target };
            Assert.That(RenderPipeline.SupportsRenderRequest(_camera, request), Is.True, "render request");
            RenderPipeline.SubmitRenderRequest(_camera, request);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _target;
            var read = Made(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
            read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            read.Apply(false);
            RenderTexture.active = previous;
            return read.GetPixels32();
        }

        // The calls of Unity's CalcMatrices in the frame before this one (the recorder holds a frame once it has ended).
        private long CallsLastFrame() => _calc.Count > 0 ? _calc.GetSample(_calc.Count - 1).Count : 0;

        // Draws for some frames, one drawing a frame, and gives the CalcMatrices calls of each of those frames.
        private IEnumerator Frames(int frames, List<long> calls)
        {
            for (int f = 0; f < frames; f++)
            {
                Draw();
                yield return null;
                calls.Add(CallsLastFrame());
            }
        }

        private static bool Lit(Color32 c) => c.r > 24 || c.g > 24 || c.b > 24;

        private static int LitIn(Color32[] pixels, int x0, int x1, int y0, int y1)
        {
            int n = 0;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) if (Lit(pixels[y * Size + x])) n++;
            return n;
        }

        [UnityTest]
        public IEnumerator ARendererNoCameraSees_IsNotSkinned_AndTheFirstDrawingThatSeesItShowsTheBonesAsTheyStand()
        {
            // Six cubes ten metres in front of the origin; the camera at the origin looks the other way.
            var skins = new List<(SkinnedMeshRenderer renderer, Transform root, Transform bone)>();
            for (int i = 0; i < 6; i++) skins.Add(Skin("cube " + i, new Vector3(0f, -4f + 1.6f * i, 10f)));
            foreach (var s in skins)
            {
                s.renderer.updateWhenOffscreen = false;
                s.renderer.localBounds = new Bounds(Vector3.zero, new Vector3(10f, 1.2f, 1.2f));   // wide enough for where the bone will go
            }

            _camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.LookRotation(Vector3.back));
            var calls = new List<long>();
            yield return Frames(3, calls);   // settle
            calls.Clear();
            yield return Frames(6, calls);
            TestContext.Out.WriteLine("six renderers no camera sees, not updated when offscreen: CalcMatrices calls a frame " + string.Join(" ", calls));
            Assert.That(calls.Sum(), Is.EqualTo(0), "Unity skins none of them");
            Assert.That(skins.Any(s => s.renderer.isVisible), Is.False, "none is visible to Unity");

            // The bones move while nothing sees them: every cube three metres to the right.
            foreach (var s in skins) s.bone.localPosition = new Vector3(3f, 0f, 0f);
            calls.Clear();
            yield return Frames(4, calls);
            Assert.That(calls.Sum(), Is.EqualTo(0), "moving the bones of an unseen renderer skins nothing");

            // The camera turns to them: the very first drawing shows the cubes where the bones are now, not where they
            // were when last skinned (the middle of the picture), and Unity has skinned them for it.
            _camera.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            Color32[] first = Draw();
            int right = LitIn(first, 84, 110, 0, Size), middle = LitIn(first, 52, 76, 0, Size);
            TestContext.Out.WriteLine("the first drawing after turning: lit pixels where the bones are now " + right + ", where they were " + middle);
            Assert.That(right, Is.GreaterThan(300), "the cubes are drawn where their bones stand now");
            Assert.That(middle, Is.EqualTo(0), "and not where they stood when last skinned");
            yield return null;
            long turned = CallsLastFrame();
            calls.Clear();
            yield return Frames(4, calls);
            TestContext.Out.WriteLine("seen: CalcMatrices calls in the frame of the turn " + turned + ", then a frame " + string.Join(" ", calls));
            Assert.That(turned, Is.GreaterThanOrEqualTo(6), "all six were skinned in the frame they were first seen");
            Assert.That(calls.All(c => c >= 6), Is.True, "and every frame while seen");
            Assert.That(skins.All(s => s.renderer.isVisible), Is.True);

            // Turned away again, then updated when offscreen as before: skinned every frame though unseen.
            _camera.transform.rotation = Quaternion.LookRotation(Vector3.back);
            yield return Frames(3, calls);
            calls.Clear();
            yield return Frames(4, calls);
            Assert.That(calls.Sum(), Is.EqualTo(0), "unseen again: not skinned");
            foreach (var s in skins) s.renderer.updateWhenOffscreen = true;
            yield return Frames(2, calls);
            calls.Clear();
            yield return Frames(6, calls);
            TestContext.Out.WriteLine("the same six, updated when offscreen: CalcMatrices calls a frame " + string.Join(" ", calls));
            Assert.That(calls.All(c => c >= 6), Is.True, "updated when offscreen, all six are skinned every frame");
        }

        [UnityTest]
        public IEnumerator ARendererMovedIntoView_IsDrawnWhereItIs_ItsBoundsCarriedByItsTransform()
        {
            // One cube far behind the camera, not updated when offscreen, with fixed bounds of its own (no root bone).
            (SkinnedMeshRenderer renderer, Transform root, Transform bone) = Skin("mover", new Vector3(0f, 0f, -30f));
            renderer.updateWhenOffscreen = false;
            renderer.localBounds = new Bounds(Vector3.zero, new Vector3(1.2f, 1.2f, 1.2f));
            Assert.That(renderer.rootBone, Is.Null);
            _camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.LookRotation(Vector3.forward));
            var calls = new List<long>();
            yield return Frames(3, calls);
            calls.Clear();
            yield return Frames(4, calls);
            Assert.That(calls.Sum(), Is.EqualTo(0), "behind the camera: not skinned");
            Assert.That(LitIn(Draw(), 0, Size, 0, Size), Is.EqualTo(0), "and nothing of it is drawn");

            // Its root is put in front of the camera, and its bone has turned meanwhile: the next drawing shows it there.
            bone.localRotation = Quaternion.Euler(0f, 45f, 0f);
            root.position = new Vector3(0f, 0f, 8f);
            Color32[] moved = Draw();
            int lit = LitIn(moved, 44, 84, 44, 84);
            TestContext.Out.WriteLine("moved into view: lit pixels at the middle " + lit + "; bounds centre " + renderer.bounds.center.ToString("F2"));
            Assert.That(lit, Is.GreaterThan(100), "the cube is drawn where its root now is");
            Assert.That(Vector3.Distance(renderer.bounds.center, root.position), Is.LessThan(0.01f), "its bounds went with its Transform");

            // And moved out again behind: not drawn, not skinned.
            root.position = new Vector3(0f, 0f, -30f);
            yield return Frames(3, calls);
            calls.Clear();
            yield return Frames(3, calls);
            Assert.That(calls.Sum(), Is.EqualTo(0));
            Assert.That(LitIn(Draw(), 0, Size, 0, Size), Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator ARendererNoEyeSees_WhoseShadowFallsIntoView_StillCastsIt()
        {
            // The ground in front of the camera; a cube above and behind the camera, out of its view, whose shadow the sun
            // (shining down and forward) throws onto the ground in view. A second cube far behind, its shadow out of view.
            GameObject ground = Made(GameObject.CreatePrimitive(PrimitiveType.Plane));
            Object.Destroy(ground.GetComponent<Collider>());
            ground.transform.localScale = new Vector3(6f, 1f, 6f);
            MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
            groundRenderer.sharedMaterial = _surface;
            groundRenderer.shadowCastingMode = ShadowCastingMode.Off;
            groundRenderer.receiveShadows = true;
            _camera.transform.SetPositionAndRotation(new Vector3(0f, 3f, 0f), Quaternion.LookRotation(new Vector3(0f, -0.6f, 1f).normalized));
            (SkinnedMeshRenderer caster, Transform casterRoot, Transform casterBone) = Skin("caster", new Vector3(0f, 7f, -2.5f));
            casterRoot.localScale = Vector3.one * 2f;
            (SkinnedMeshRenderer far, Transform _, Transform _) = Skin("far behind", new Vector3(0f, 1f, -45f));
            foreach (SkinnedMeshRenderer r in new[] { caster, far }) r.localBounds = new Bounds(Vector3.zero, new Vector3(1.2f, 1.2f, 1.2f));

            int[] shadow = new int[2];
            long[] callsAFrame = new long[2];
            bool[] visible = new bool[2];
            for (int mode = 0; mode < 2; mode++)
            {
                bool whenOffscreen = mode == 0;
                caster.updateWhenOffscreen = far.updateWhenOffscreen = whenOffscreen;
                var calls = new List<long>();
                yield return Frames(3, calls);
                calls.Clear();
                yield return Frames(4, calls);
                callsAFrame[mode] = calls.Min();
                visible[mode] = caster.isVisible;
                // The cube itself is not in the picture: with and without the sun's shadows, what differs is its shadow on the ground.
                _light.shadows = LightShadows.Hard;
                Color32[] with = Draw();
                _light.shadows = LightShadows.None;
                Color32[] without = Draw();
                _light.shadows = LightShadows.Hard;
                for (int p = 0; p < with.Length; p++) if (with[p].g + 24 < without[p].g) shadow[mode]++;
                yield return null;
            }

            TestContext.Out.WriteLine("a cube no eye sees, its shadow in view: updated when offscreen -- shadow pixels " + shadow[0] + ", CalcMatrices calls a frame at least " + callsAFrame[0]
                + ", visible to Unity " + visible[0] + "; not updated when offscreen -- shadow pixels " + shadow[1] + ", CalcMatrices calls a frame at least " + callsAFrame[1] + ", visible to Unity " + visible[1]);
            Assert.That(shadow[0], Is.GreaterThan(40), "updated when offscreen, the unseen cube's shadow is on the ground in view");
            Assert.That(shadow[1], Is.GreaterThan(40), "not updated when offscreen, the shadow is still there");
            Assert.That(shadow[1], Is.EqualTo(shadow[0]).Within(shadow[0] / 10 + 4), "and of the same size");
            Assert.That(callsAFrame[0], Is.GreaterThanOrEqualTo(2), "updated when offscreen: both cubes skinned every frame");
            Assert.That(callsAFrame[1], Is.LessThan(2), "not updated when offscreen: the cube far behind, whose shadow nothing sees, is not skinned");
        }
    }
}
