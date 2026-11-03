using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.Rendering;
using Zantetsu.Core.Animation;
using Zantetsu.Core.MobPlan;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using Zantetsu.Rendering.Urp;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The GPU bake probe (DESIGN 4.5.14, skin-bake-probe): started by name in its own scene, it (1) prepares the drawn
    /// mesh's own GPU buffers for reading (<see cref="SkinBakeProbeMeshInput"/>), (2) takes one renderer-local palette
    /// on the main thread, (3) bakes the 16-byte render vertices by the probe's compute kernel from those buffers and
    /// that palette, (4) compares them with the float-input CPU reference (<see cref="SkinBakeProbeCpuBake"/>) through
    /// the probe's own readback, over the known small rig and one NPC of the scene's templates, under the bind pose, a
    /// bend, a strong bend, with the output at an unaligned offset between guards, after the bones and the root have
    /// moved away from the taken pose, and after the source renderer was disabled; (5) where the scene has a cut world,
    /// shows the CPU-baked vertices and the GPU-baked ones through the existing 16-byte display route in the same frame
    /// -- the GPU ones written into the display's vertex buffer after the display's own upload of a poison placeholder
    /// -- and takes pictures. The display draws a shown fragment only where its placement lookup answers for it, so the
    /// probe answers for its own fragments (as the scene draw comparison does) and moves them by those answers; it never
    /// retires a shown fragment (that halts the display). The record goes to the probe's directory. The product's cut
    /// acceptance and publication are not touched; the renderers of the scene keep drawing as they are.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public sealed class SkinBakeProbeRunner : MonoBehaviour
    {
        public const string Prefix = "SKIN BAKE PROBE: ";
        public const int ExitAllPassed = 0;
        /// <summary>The probe ran to its end, but a case failed (the record says which).</summary>
        public const int ExitCasesFailed = 23;
        public const int ExitFailed = 14;

        [Tooltip("The scene's cut world (optional): with it, the drawing cases show the CPU-baked and the GPU-baked vertices through its display.")]
        [SerializeField] private CutWorldRoot world;
        [Tooltip("The slot templates (optional), one a model family: the first one gives the NPC subject.")]
        [SerializeField] private GameObject[] templates = Array.Empty<GameObject>();
        [Tooltip("The crowd's assets: the pose table the NPC subject is posed with.")]
        [SerializeField] private MobPlanAssets assets;
        [SerializeField] private Light mainLight;
        [SerializeField] private Transform rig;
        [SerializeField] private Camera eye;

        /// <summary>The fixed view: the eye held at this world pose facing +Z by counter-moving the rig every frame (as npc-skin-bench's fixed view does), so that the pictures show the subjects (placed 2.5..3 m ahead) whatever the tracked head does.</summary>
        public static readonly Vector3 FixedEye = new Vector3(0f, 1.6f, 0f);
        private bool _fixedViewOn;
        private float _rigMoveMax, _rigTurnMax;

        private static SkinBakeProbeSettings s_settings;
        private static bool s_armed;

        /// <summary>The PC entry (SandboxScenario): the runner in the scene is armed with the launch's settings; a failure in words.</summary>
        public static string StartForScenario(string scene)
        {
            SkinBakeProbeRunner runner = FindAnyObjectByType<SkinBakeProbeRunner>(FindObjectsInactive.Include);
            if (runner == null) return "the scene " + scene + " has no skin bake probe runner";
            if (!SkinBakeProbeSettings.TryRead(Environment.GetCommandLineArgs(), out SkinBakeProbeSettings settings, out string failure)) return failure ?? SkinBakeProbeSettings.Argument + " <directory> is needed";
            Arm(settings);
            return null;
        }

        public static void Arm(SkinBakeProbeSettings settings)
        {
            s_settings = settings ?? throw new ArgumentNullException(nameof(settings));
            s_armed = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { s_settings = null; s_armed = false; }

        /// <summary>For a test: the runner with what the test made, the settings given directly; it does not quit the application.</summary>
        public void ConfigureForTest(CutWorldRoot targetWorld, GameObject[] slotTemplates, MobPlanAssets crowdAssets, Light light, Transform xrRig, Camera camera, SkinBakeProbeSettings settings)
        {
            world = targetWorld; templates = slotTemplates ?? Array.Empty<GameObject>(); assets = crowdAssets; mainLight = light; rig = xrRig; eye = camera;
            _settings = settings; _armedForTest = true;
        }

        private bool _armedForTest;
        private SkinBakeProbeSettings _settings;
        private SkinBakeProbeRecord _record;
        private SkinBakeProbeGpuBake _gpu;
        private readonly List<GameObject> _made = new List<GameObject>();
        private readonly List<SkinBakeProbeMeshInput> _inputs = new List<SkinBakeProbeMeshInput>();
        private Material _rigMaterial;
        private int _ownOffset;
        private Placement _placement;

        // A subject whose numbers were taken: what the drawing stage draws.
        private sealed class DrawnSubject { public string name; public SkinnedMeshRenderer renderer; public SkinBakeProbeMeshInput input; public Action<int> setPose; }
        private readonly List<DrawnSubject> _subjects = new List<DrawnSubject>();
        // The NPC as made for the bench (no cases): its renderer and its pose setter.
        private SkinnedMeshRenderer _npcRenderer;
        private Action<int> _npcSetPose;
        private Bounds _boundsSink;   // the bench's bounds computation lands here, so that it is not optimised away
        private static double Ms(long from, long to) => (to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public enum Phase { Waiting, Running, Done, Failed }
        public Phase Current { get; private set; } = Phase.Waiting;
        public string Failure { get; private set; }
        public int ExitCode { get; private set; } = -1;
        public SkinBakeProbeRecord Record => _record;
        public IReadOnlyList<GameObject> Made => _made;

        private void Start()
        {
            if (!_armedForTest)
            {
                if (!s_armed) { Log("not started: the launch's scenario is not skin-bake-probe"); enabled = false; return; }
                _settings = s_settings;
            }

            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            Current = Phase.Running;
            _record = new SkinBakeProbeRecord();
            Log("armed: " + _settings.Describe());
            try
            {
                if (!string.IsNullOrEmpty(_settings.directory)) Directory.CreateDirectory(_settings.directory);
                _gpu = new SkinBakeProbeGpuBake();
            }
            catch (Exception e) { Fail("the probe could not be set up: " + e.Message); yield break; }

            _record.Fact("device", SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceName + "; compute " + SystemInfo.supportsComputeShaders + "; async readback " + SystemInfo.supportsAsyncGPUReadback + "; max compute buffer inputs " + SystemInfo.maxComputeBufferInputsCompute);
            _record.Fact("platform", Application.platform + " " + Application.unityVersion + " " + (Debug.isDebugBuild ? "Development" : "Release"));
            _record.Fact("settings", _settings.Describe());
            _record.Fact("tolerances", "position " + SkinBakeProbeCompare.PositionTolerance.ToString("E1", CultureInfo.InvariantCulture) + " m; normal boundary = each oct byte within 1 and decoded angle within " + SkinBakeProbeCompare.BoundaryAngleDegrees + " deg; uv boundary = each byte within 1");
            if (world != null) _record.Fact("world", "body route " + world.BodyRoute + "; skin route " + world.SkinRoute + "; ready " + world.IsReady);
            if (rig != null) rig.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            if (eye != null) _record.Fact("eye at start", eye.transform.position.ToString("F3") + " yaw " + eye.transform.eulerAngles.y.ToString("F1", CultureInfo.InvariantCulture));
            if (rig != null && eye != null)
            {
                _fixedViewOn = true;
                Application.onBeforeRender += FixView;
                FixView();
                _record.Fact("fixed view", "the eye held at " + FixedEye.ToString("F3") + " facing +Z by counter-moving the rig every frame (LateUpdate and onBeforeRender); the subjects stand 2.5..3 m ahead");
            }
            else _record.Fact("fixed view", "off: no rig or no eye in the scene (the pictures show the tracked view)");

            // ---- the capture-method check in place of the cases (MOTION-CAPTURE-1)
            if (_settings.IsMotionCheck)
            {
                yield return MotionCheck();
                if (Current == Phase.Failed) yield break;
                yield return End();
                yield break;
            }

            // ---- the cut bench in place of the cases (SKIN-GPU-FIRST-1): the product's prepared cuts under the launch's skin bake route
            if (_settings.IsCutBench)
            {
                yield return CutBench();
                if (Current == Phase.Failed) yield break;
                yield return End();
                yield break;
            }

            // ---- the performance comparison in place of the cases (SKIN-BAKE-PROBE-2)
            if (_settings.IsBench)
            {
                yield return Bench();
                if (Current == Phase.Failed) yield break;
                yield return End();
                yield break;
            }

            // ---- the small rig: the numbers
            if (_settings.small)
            {
                _rigMaterial = RigMaterial();
                SkinBakeProbeRig rigA = null;
                try
                {
                    rigA = new SkinBakeProbeRig("Skin bake probe rig A", _rigMaterial);
                    rigA.Root.transform.position = new Vector3(-0.8f, 0.2f, 2.5f);
                    _made.Add(rigA.Root);
                }
                catch (Exception e) { Fail("the small rig could not be made: " + e.Message); yield break; }

                yield return null;   // a frame drawn by the renderer before anything is prepared
                yield return Subject("small", rigA.Renderer, rigA.Mesh, pose => rigA.SetPose(pose), () => rigA.Disturb(), 3);

                // A second individual of the same mesh: the input is shared, prepared once.
                var rigB = new SkinBakeProbeRig("Skin bake probe rig B", _rigMaterial);
                rigB.Root.transform.position = new Vector3(0.8f, 0.2f, 2.5f);
                _made.Add(rigB.Root);
                yield return null;
                SkinBakeProbeMeshInput sharedInput = _inputs.FirstOrDefault(i => i.Mesh == rigA.Mesh);
                _record.Other("small", "shared mesh", "observation", sharedInput != null, Time.frameCount, sharedInput != null
                    ? "the second rig shares the first's input (users " + sharedInput.Users + "): no second preparation"
                    : "no input of the rig's mesh was kept");
                if (sharedInput != null)
                {
                    sharedInput.Hold();
                    rigB.SetPose(1);
                    yield return Bake("small", "shared second individual bent", rigB.Renderer, sharedInput, null);
                    sharedInput.Release();
                }

                rigB.Root.SetActive(false);   // out of the pictures
            }

            // ---- the NPC: the numbers
            if (_settings.npc)
            {
                if (templates == null || templates.Length == 0 || assets == null || assets.initialPose == null) _record.Other("npc", "subject", "refusal", null, Time.frameCount, "the scene gives no NPC template or no pose table: the NPC subject is skipped");
                else yield return NpcSubject();
            }

            // ---- the drawing through the display: the NPC where its numbers were taken, else the small rig.
            if (_settings.draw)
            {
                if (world == null || !world.IsReady) _record.Other("draw", "world", "observation", null, Time.frameCount, "no ready cut world in the scene: nothing is drawn through the display");
                else if (_subjects.Count == 0) _record.Other("draw", "subject", "observation", null, Time.frameCount, "no subject with a prepared input: nothing is drawn through the display");
                else
                {
                    DrawnSubject chosen = _subjects.FirstOrDefault(s => s.name == "npc") ?? _subjects[0];
                    yield return Draw(chosen);
                }
            }
            else _record.Other("draw", "drawing", "observation", null, Time.frameCount, "drawing not asked");

            yield return End();
        }

        // ---- the end: the pending readbacks completed, the facts, the record written, the exit
        private IEnumerator End()
        {
            while (_gpu.PendingCount > 0) { _gpu.Update(); yield return null; }
            _record.Fact("gpu", "dispatches " + _gpu.Dispatches + ", index dispatches " + _gpu.IndexDispatches + ", palette bytes uploaded " + _gpu.PaletteBytesUploaded + ", own bytes allocated " + _gpu.OwnBytesAllocated + ", last bake " + (_gpu.LastBakeSeconds * 1000.0).ToString("F4", CultureInfo.InvariantCulture) + " ms (main thread)");
            _record.Fact("inputs", string.Join(" || ", _inputs.Select(i => i.Describe())));
            if (_fixedViewOn) _record.Fact("fixed view rig counter-move", "largest move " + _rigMoveMax.ToString("F3", CultureInfo.InvariantCulture) + " m, largest turn " + _rigTurnMax.ToString("F1", CultureInfo.InvariantCulture) + " deg in one correction");
            if (world != null && world.Display != null) _record.Fact("display at the end", "halted " + world.Display.IsHalted + " (" + world.Display.HaltReason + "), broken " + world.Display.IsBroken + ", shown " + world.Display.ShownCount);
            Current = Phase.Done;
            ExitCode = _record.Failed == 0 ? ExitAllPassed : ExitCasesFailed;
            WriteRecord();
            Log("done: cases passed " + _record.Passed + ", failed " + _record.Failed + "; exit " + ExitCode);
            if (!_armedForTest && !_settings.keepOpen) Application.Quit(ExitCode);
        }

        // The numeric cases over one subject: the renderer's mesh prepared, the three poses, the offset and guards, the moved pose, the disabled renderer, the index copy.
        private IEnumerator Subject(string subject, SkinnedMeshRenderer renderer, Mesh mesh, Action<int> setPose, Action disturb, int poses)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!SkinBakeProbeMeshInput.TryPrepare(mesh, out SkinBakeProbeMeshInput input, out string refusal))
            {
                _record.Other(subject, "prepare", "refusal", false, Time.frameCount, refusal);
                yield break;
            }

            input.Hold();
            _inputs.Add(input);
            _record.Other(subject, "prepare", "observation", true, Time.frameCount, input.Describe());
            _gpu.EnsureOwnOutput(Math.Max(1024, input.VertexCount * 8 + 64), Math.Max(4096, input.IndexCount * 2 + 64));
            _ownOffset = 3;   // the first region begins at an unaligned, non-zero element

            // The renderer keeps drawing after the preparation: the handles stay valid over a few frames (no recreation seen by them).
            bool valid = true;
            for (int f = 0; f < 3; f++) { yield return null; valid &= input.Streams.Where(s => s != null).All(s => s.IsValid()) && input.Weights.IsValid() && input.Indices.IsValid(); }
            _record.Other(subject, "renderer after Raw", "observation", valid, Time.frameCount, "renderer enabled " + renderer.enabled + ", drawn by Unity (isVisible " + renderer.isVisible + "); mesh buffer handles valid over 3 frames: " + valid
                + "; vertex target " + mesh.vertexBufferTarget + ", index target " + mesh.indexBufferTarget);

            for (int pose = 0; pose < poses; pose++)
            {
                setPose(pose);
                yield return null;   // the bones' transforms settle; the renderer draws this pose too
                yield return Bake(subject, "pose " + pose + (pose == 0 ? " (bind)" : pose == 1 ? " (bend)" : " (strong bend)"), renderer, input, null);
            }

            // The pose taken, then the bones and the root moved: the bake must follow the taken palette, not the bones.
            {
                setPose(1);
                yield return null;
                float4x4[] palette = SkinBakeProbeCpuBake.Palette(renderer, out int missing);
                SkinBakeProbeCpuBake.Inputs cpuInputs = SkinBakeProbeCpuBake.Read(input);
                var reference = new VpRenderVertex[input.VertexCount];
                int invalid = SkinBakeProbeCpuBake.Bake(cpuInputs, palette, reference, out _, out _);
                disturb();
                yield return null;
                yield return BakeWith(subject, "pose 1 taken, bones and root moved", input, palette, reference, invalid, "missing bones " + missing + "; the bones were moved after the palette was taken");
            }

            // The renderer disabled after the preparation: the mesh's buffers still serve the bake.
            {
                setPose(2);
                yield return null;
                float4x4[] palette = SkinBakeProbeCpuBake.Palette(renderer, out int missing);
                SkinBakeProbeCpuBake.Inputs cpuInputs = SkinBakeProbeCpuBake.Read(input);
                var reference = new VpRenderVertex[input.VertexCount];
                int invalid = SkinBakeProbeCpuBake.Bake(cpuInputs, palette, reference, out _, out _);
                bool wasEnabled = renderer.enabled;
                renderer.enabled = false;
                yield return null;
                yield return null;
                bool handles = input.Streams.Where(s => s != null).All(s => s.IsValid()) && input.Weights.IsValid() && input.Indices.IsValid();
                yield return BakeWith(subject, "renderer disabled after the preparation", input, palette, reference, invalid, "missing bones " + missing + "; handles valid " + handles);
                renderer.enabled = wasEnabled;
            }

            // The indices, copied with a base vertex.
            {
                SkinBakeProbeCpuBake.Inputs cpuInputs = SkinBakeProbeCpuBake.Read(input);
                const uint baseVertex = 1000003u;
                _gpu.CopyIndices(input, _gpu.OutputIndices, 5, baseVertex);
                SkinBakeProbeCompare.IndexResult result = null;
                _gpu.VerifyIndices(subject + " indices", _gpu.OutputIndices, 5, input.IndexCount, cpuInputs.indices, baseVertex, r => result = r);
                while (result == null) { _gpu.Update(); yield return null; }
                _record.Indices(subject, "indices + base vertex", result, input.IndexFormat + " -> uint, " + input.IndexCount + " indices, submeshes " + input.SubmeshCount);
            }

            double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            _record.Fact(subject + " subject seconds", seconds.ToString("F2", CultureInfo.InvariantCulture));
            _subjects.Add(new DrawnSubject { name = subject, renderer = renderer, input = input, setPose = setPose });
        }

        // One bake of the renderer's current pose: the palette taken now, the CPU reference made now, the GPU bake into the own buffer, verified.
        private IEnumerator Bake(string subject, string caseName, SkinnedMeshRenderer renderer, SkinBakeProbeMeshInput input, string notes)
        {
            float4x4[] palette = SkinBakeProbeCpuBake.Palette(renderer, out int missing);
            SkinBakeProbeCpuBake.Inputs cpuInputs = SkinBakeProbeCpuBake.Read(input);
            var reference = new VpRenderVertex[input.VertexCount];
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            int invalid = SkinBakeProbeCpuBake.Bake(cpuInputs, palette, reference, out _, out _);
            double cpuSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            yield return BakeWith(subject, caseName, input, palette, reference, invalid, (notes != null ? notes + "; " : "") + "missing bones " + missing + "; cpu reference " + (cpuSeconds * 1000.0).ToString("F3", CultureInfo.InvariantCulture) + " ms");
        }

        private IEnumerator BakeWith(string subject, string caseName, SkinBakeProbeMeshInput input, float4x4[] palette, VpRenderVertex[] reference, int cpuInvalid, string notes)
        {
            const int guard = 2;
            int start = _ownOffset + guard;
            if (start + input.VertexCount + guard > _gpu.Output.count) { _ownOffset = 3; start = _ownOffset + guard; _gpu.EnsureOwnOutput(_gpu.Output.count, _gpu.OutputIndices.count); }
            _gpu.Bake(input, palette, _gpu.Output, start);
            double bakeSeconds = _gpu.LastBakeSeconds;
            SkinBakeProbeCompare.Result result = null;
            _gpu.Verify(subject + " " + caseName, _gpu.Output, start, input.VertexCount, guard, guard, reference, r => result = r);
            _ownOffset = start + input.VertexCount + guard + 1;
            while (result == null) { _gpu.Update(); yield return null; }
            _record.Bake(subject, caseName, result, "own", start, bakeSeconds, (notes != null ? notes + "; " : "") + "cpu invalid " + cpuInvalid + "; weights " + input.WeightLayout + " max " + input.MaxInfluences);
        }

        // ---- the NPC subject: one template instantiated and activated as the benchmark does; posed by the pose table at fixed times, then bent by hand.
        private IEnumerator NpcSubject()
        {
            GameObject templateHolder = null;
            SandboxNpcCharacter template = null;
            foreach (GameObject t in templates) { template = t != null ? t.GetComponentInChildren<SandboxNpcCharacter>(true) : null; if (template != null) { templateHolder = t; break; } }
            if (template == null) { _record.Other("npc", "subject", "refusal", false, Time.frameCount, "no template carries a SandboxNpcCharacter"); yield break; }
            GameObject holder = Instantiate(templateHolder);
            holder.name = "skin bake probe npc";
            _made.Add(holder);
            SandboxNpcCharacter npc = holder.GetComponentInChildren<SandboxNpcCharacter>(true);
            PoseTablePlayer pose = npc.CharacterRoot.GetComponent<PoseTablePlayer>();
            if (pose == null) { _record.Other("npc", "subject", "refusal", false, Time.frameCount, "the template's character root has no PoseTablePlayer"); yield break; }
            TextAsset clipAsset = assets.initialPose;
            if (!PoseTable.TryRead(clipAsset.bytes, out PoseTable clip, out string clipError)) { _record.Other("npc", "subject", "refusal", false, Time.frameCount, "the pose table could not be read: " + clipError); yield break; }
            pose.Configure(clipAsset, pose.ModelRoot, 0.0, true);
            pose.Managed = true;
            double sourceTime = 0.0;
            pose.PlanSource = (double target, out PoseTable table, out double source) => { table = clip; source = clip.ResolveSourceTime(sourceTime); return true; };
            var share = new SandboxNpcCharacter.SlotShare();
            npc.PrepareAsSlot(share);
            npc.CharacterRoot.SetActive(true);
            holder.SetActive(true);
            npc.enabled = true;
            float by = Time.realtimeSinceStartup + 120f;
            while (!npc.IsPrepared && npc.Failure == null && Time.realtimeSinceStartup < by) yield return null;
            if (!npc.IsPrepared)
            {
                _record.Other("npc", "subject", "refusal", false, Time.frameCount, "the NPC was not prepared: " + (npc.Failure ?? "not within 120 s") + (world != null && world.Display != null ? "; display halted " + world.Display.IsHalted + " (" + world.Display.HaltReason + ")" : ""));
                yield break;
            }

            npc.CharacterRoot.transform.SetPositionAndRotation(new Vector3(0f, 0f, 3f), Quaternion.Euler(0f, 180f, 0f));
            if (!npc.Activate()) { _record.Other("npc", "subject", "refusal", false, Time.frameCount, "the NPC could not be activated: " + npc.Failure); yield break; }
            yield return null;
            SkinnedMeshRenderer renderer = npc.Renderer;
            if (renderer == null || renderer.sharedMesh == null) { _record.Other("npc", "subject", "refusal", false, Time.frameCount, "the NPC has no renderer with a mesh"); yield break; }
            _record.Fact("npc", npc.Family + ": mesh '" + renderer.sharedMesh.name + "' vertices " + renderer.sharedMesh.vertexCount + ", bones " + renderer.bones.Length + ", bind poses " + renderer.sharedMesh.bindposes.Length + "; drawn by the skin draw " + npc.DrawnBySkinDraw + (npc.SkinDrawRefusal != null ? " (" + npc.SkinDrawRefusal + ")" : ""));
            if (!renderer.enabled) _record.Other("npc", "subject", "observation", null, Time.frameCount, "the NPC's renderer is disabled (the skin draw draws it): the probe reads its mesh all the same");

            // The poses: the table at 0 s, the table at 0.8 s, then a strong bend of the three bones with the most weighted vertices.
            int[] heavy = HeaviestBones(renderer.sharedMesh, 3);
            void SetPose(int id)
            {
                if (id == 0) { sourceTime = 0.0; pose.ApplyNow(null, 0); }
                else if (id == 1) { sourceTime = 0.8; pose.ApplyNow(null, 0); }
                else
                {
                    sourceTime = 0.3; pose.ApplyNow(null, 0);
                    Transform[] bones = renderer.bones;
                    for (int k = 0; k < heavy.Length; k++) if (bones[heavy[k]] != null) bones[heavy[k]].localRotation *= Quaternion.Euler(70f, -40f * k, 25f);
                }
            }

            void Disturb()
            {
                sourceTime = 1.5; pose.ApplyNow(null, 0);
                Transform[] bones = renderer.bones;
                for (int k = 0; k < heavy.Length; k++) if (bones[heavy[k]] != null) bones[heavy[k]].localRotation *= Quaternion.Euler(-50f, 60f, 10f * k);
                npc.CharacterRoot.transform.position += new Vector3(0.9f, 0.1f, -0.4f);
            }

            _npcRenderer = renderer;
            _npcSetPose = SetPose;
            if (_settings.IsBench) yield break;   // the bench prepares the mesh itself and runs no case
            yield return Subject("npc", renderer, renderer.sharedMesh, SetPose, Disturb, 3);
        }

        private static int[] HeaviestBones(Mesh mesh, int count)
        {
            var weight = new Dictionary<int, float>();
            using (var all = mesh.GetAllBoneWeights())
            {
                for (int i = 0; i < all.Length; i++) { weight.TryGetValue(all[i].boneIndex, out float w); weight[all[i].boneIndex] = w + all[i].weight; }
            }

            return weight.OrderByDescending(p => p.Value).Take(count).Select(p => p.Key).ToArray();
        }

        // ---- the drawing through the display (SKIN-BAKE-PROBE-2): the CPU-baked vertices and the GPU-baked ones shown once each,
        // placed by the probe's answers. The GPU one is appended as a placeholder with the conservative bounds of the pose (from the
        // mesh bounds and the palette, no vertex of the bake), baked in the same Update, and its first frame is captured without any
        // readback of the region before the frame's drawing; the pictures at the windows' centres are kept apart from that one.
        // Then both side by side, each alone near the right and the left edge of the view, and the subject's own renderer.
        private IEnumerator Draw(DrawnSubject subject)
        {
            string name = subject.name;
            SkinnedMeshRenderer renderer = subject.renderer;
            SkinBakeProbeMeshInput input = subject.input;
            subject.setPose(1);
            yield return null;
            float4x4[] palette = SkinBakeProbeCpuBake.Palette(renderer, out _);
            Matrix4x4 root = renderer.transform.localToWorldMatrix;
            SkinBakeProbeCpuBake.Inputs cpuInputs = SkinBakeProbeCpuBake.Read(input);
            var cpuVertices = new VpRenderVertex[input.VertexCount];
            SkinBakeProbeCpuBake.Bake(cpuInputs, palette, cpuVertices, out float3 cpuMin, out float3 cpuMax);

            // The conservative bounds of this pose: the mesh bounds through the palette alone (the bones' count, not the vertices').
            long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Bounds conservative = SkinBakeProbeBounds.Conservative(input.Mesh.bounds, palette, palette.Length, 0.01f);
            double boundsMs = Ms(b0, System.Diagnostics.Stopwatch.GetTimestamp());
            bool contains = SkinBakeProbeBounds.Contains(conservative, cpuVertices, out int outside);
            _record.Other(name, "conservative bounds contain the baked shape", "bounds", contains, Time.frameCount,
                "conservative " + conservative.min.ToString("F3") + " .. " + conservative.max.ToString("F3") + " from the mesh bounds " + input.Mesh.bounds.min.ToString("F3") + " .. " + input.Mesh.bounds.max.ToString("F3")
                + " through " + palette.Length + " palette matrices and the origin, +1 cm, in " + boundsMs.ToString("F4", Inv) + " ms; the CPU bake measures " + ((Vector3)cpuMin).ToString("F3") + " .. " + ((Vector3)cpuMax).ToString("F3") + "; vertices outside " + outside);

            // The poison: a 1 cm speck at the mesh's own bounds centre, valid normals -- not the character, not the reference; it shows only if a bake was late.
            Vector3 centre = input.Mesh.bounds.center;
            var poison = new VpRenderVertex[input.VertexCount];
            for (int i = 0; i < poison.Length; i++) poison[i] = new VpRenderVertex { position = centre + 0.005f * new Vector3((i % 3) - 1, ((i / 3) % 3) - 1, ((i / 9) % 3) - 1), normalX = 0, normalY = 0, u = 0, v = 0 };
            var topology = new int[input.VertexCount];
            for (int i = 0; i < topology.Length; i++) topology[i] = i;
            var submeshes = new VpGeometrySubmesh[cpuInputs.submeshIndexStart.Length];
            for (int s = 0; s < submeshes.Length; s++) submeshes[s] = new VpGeometrySubmesh(cpuInputs.submeshIndexStart[s], cpuInputs.submeshIndexCount[s], 0);
            Matrix4x4 away = Matrix4x4.Translate(new Vector3(0f, -100f, 0f)) * root;
            Matrix4x4 left = Matrix4x4.Translate(new Vector3(-0.7f, 0f, 0f)) * root, right = Matrix4x4.Translate(new Vector3(0.7f, 0f, 0f)) * root;
            float edgeRightMetres = _settings.edgeRight, edgeLeftMetres = _settings.edgeLeft;
            Matrix4x4 edgeRight = Matrix4x4.Translate(new Vector3(edgeRightMetres, 0f, 0f)) * root, edgeLeft = Matrix4x4.Translate(new Vector3(edgeLeftMetres, 0f, 0f)) * root;
            bool wasEnabled = renderer.enabled;
            renderer.enabled = false;   // the subject's own renderer is hidden during the drawing windows: only the display's two show
            int frames = _settings.drawFrames;
            _placement = new Placement(world.Display.Placement);
            world.Display.Placement = _placement;
            GraphicsBuffer displayVertices = world.Display.GpuVertexBufferForDiagnosis;

            // The CPU bake, shown at the centre: the frame that showed it (the display's upload inside TryShow) captured in the same Update, then the window.
            if (!Show(cpuVertices, cpuInputs.indices, topology, submeshes, root, null, out LogicalFragmentId cpu, out VpStoredGeometry cpuGeometry, out string why))
            { _record.Other(name, "draw cpu", "refusal", false, Time.frameCount, why + HaltNote()); EndDraw(renderer, wasEnabled); yield break; }
            int cpuShowFrame = Time.frameCount;
            if (_settings.picture) TakePicture(name + "-cpu-first-frame");
            world.Storage.TryGetPublishedExtent(cpuGeometry, out _, out _, out Bounds cpuRecorded);
            _record.Other(name, "draw cpu first frame", "drawing", true, cpuShowFrame, "shown at frame " + cpuShowFrame + " (vertex start " + cpuGeometry.vertexStart + "; recorded bounds, measured by the storage, " + cpuRecorded.min.ToString("F3") + " .. " + cpuRecorded.max.ToString("F3") + "); the frame's picture asked in the same Update" + HaltNote());
            yield return Hold(frames, name + "-cpu-alone");
            bool cpuDrawn = world.Display.TryGetDrawSlots(cpu, out _, out int cpuCommands, out _, out _, out _);
            _record.Other(name, "draw cpu alone", "observation", cpuDrawn, Time.frameCount, "draw slots held " + cpuDrawn + " (commands " + cpuCommands + "); display shown " + world.Display.ShownCount + ", placement answers " + _placement.Answers + HaltNote());

            // The GPU bake, its first frame without a readback before it: the placeholder shown with the conservative bounds (the
            // display uploads the placeholder inside TryShow), then in the same Update the bake dispatched into the display's vertex
            // buffer and the frame's picture asked; nothing reads the region before this frame's cameras. The CPU one moved away.
            _placement.Move(cpu, away);
            if (!Show(poison, cpuInputs.indices, topology, submeshes, root, conservative, out LogicalFragmentId gpu, out VpStoredGeometry gpuGeometry, out why))
            { _record.Other(name, "draw gpu", "refusal", false, Time.frameCount, why + HaltNote()); EndDraw(renderer, wasEnabled); yield break; }
            int showFrame = Time.frameCount;
            _gpu.Bake(input, palette, displayVertices, gpuGeometry.vertexStart);
            double bakeSeconds = _gpu.LastBakeSeconds;
            if (_settings.picture) TakePicture(name + "-gpu-first-frame");
            bool extentTaken = world.Storage.TryGetPublishedExtent(gpuGeometry, out _, out _, out Bounds gpuRecorded);
            _record.Other(name, "draw gpu first frame", "drawing", extentTaken && gpuRecorded.min == conservative.min && gpuRecorded.max == conservative.max, showFrame,
                "placeholder shown with the conservative bounds (recorded " + gpuRecorded.min.ToString("F3") + " .. " + gpuRecorded.max.ToString("F3") + "), the bake dispatched into the display's vertex buffer at element " + gpuGeometry.vertexStart
                + " in frame " + showFrame + " (Update: after TryShow's upload, before the cameras), the frame's picture asked in the same Update; NO readback of the region before this frame's drawing; the CPU one moved away" + HaltNote());
            yield return null;
            yield return null;   // two more frames drawn from the region as baked, before anything reads it
            SkinBakeProbeCompare.Result verified = null;
            int verifyFrame = Time.frameCount;
            _gpu.Verify(name + " display region", displayVertices, gpuGeometry.vertexStart, input.VertexCount, 0, 0, cpuVertices, r => verified = r);
            yield return Hold(frames, name + "-gpu-alone");
            while (verified == null) { _gpu.Update(); yield return null; }
            _record.Bake(name, "display region after the first frames", verified, "display", gpuGeometry.vertexStart, bakeSeconds, "read back from the display's vertex buffer: what the route draws; requested in frame " + verifyFrame + ", after the first-frame picture (dispatch frame " + showFrame + "), not before it");
            bool gpuDrawn = world.Display.TryGetDrawSlots(gpu, out _, out int gpuCommands, out _, out _, out _);
            _record.Other(name, "draw gpu alone", "observation", gpuDrawn, Time.frameCount, "draw slots held " + gpuDrawn + " (commands " + gpuCommands + "); placement answers " + _placement.Answers + HaltNote());
            SkinBakeProbeCompare.Result later = null;
            _gpu.Verify(name + " display region after " + frames + " frames", displayVertices, gpuGeometry.vertexStart, input.VertexCount, 0, 0, cpuVertices, r => later = r);
            while (later == null) { _gpu.Update(); yield return null; }
            _record.Bake(name, "display region after the window", later, "display", gpuGeometry.vertexStart, 0.0, later.Pass ? "the region kept the bake over the window: no upload wrote it again" : "THE REGION CHANGED during the window");

            // Both, side by side, in the same frames: the CPU bake left, the GPU bake right.
            _placement.Move(cpu, left);
            _placement.Move(gpu, right);
            int bothFrame = Time.frameCount;
            _record.Other(name, "draw both", "drawing", true, bothFrame, "CPU bake left, GPU bake right, from frame " + bothFrame + HaltNote());
            yield return Hold(frames, name + "-both");

            // The edge of the view: each alone near the right edge, then near the left edge; the picture pairs say whether the GPU
            // one (its conservative bounds) is drawn wherever the CPU one (its measured bounds) is.
            int half = Math.Max(2, frames / 2);
            _placement.Move(gpu, away); _placement.Move(cpu, edgeRight);
            yield return Hold(half, name + "-cpu-edge-right");
            _placement.Move(cpu, away); _placement.Move(gpu, edgeRight);
            yield return Hold(half, name + "-gpu-edge-right");
            bool gpuDrawnRight = world.Display.TryGetDrawSlots(gpu, out _, out int gpuCommandsRight, out _, out _, out _);
            _placement.Move(gpu, away); _placement.Move(cpu, edgeLeft);
            yield return Hold(half, name + "-cpu-edge-left");
            _placement.Move(cpu, away); _placement.Move(gpu, edgeLeft);
            yield return Hold(half, name + "-gpu-edge-left");
            bool gpuDrawnLeft = world.Display.TryGetDrawSlots(gpu, out _, out int gpuCommandsLeft, out _, out _, out _);
            _record.Other(name, "draw edge", "drawing", gpuDrawnRight && gpuDrawnLeft, Time.frameCount, "CPU then GPU alone at x " + edgeRightMetres.ToString("F2", Inv) + " m and x " + edgeLeftMetres.ToString("F2", Inv)
                + " m of the root, " + half + " frames each, a picture at each window's centre; GPU draw slots held right/left " + gpuDrawnRight + "/" + gpuDrawnLeft + " (commands " + gpuCommandsRight + "/" + gpuCommandsLeft + "); whether the shape was drawn while visible is read from the picture pairs" + HaltNote());

            // The subject's own renderer again (the SMR, after Raw was added), the display's two moved away, for the comparison picture.
            _placement.Move(cpu, away);
            _placement.Move(gpu, away);
            renderer.enabled = wasEnabled;
            yield return Hold(half, name + "-renderer");
            EndDraw(renderer, wasEnabled);
        }

        // ---- the performance comparison (SKIN-BAKE-PROBE-2/3): a series of conditions in one launch over the NPC, posed once, the
        // palette fixed once; the display regions appended and shown before the warmup (a placeholder with the conservative bounds
        // each, uploaded once); then, condition by condition, every frame one bake of every body into its region by the condition's
        // route: 'product' = the product's 24-byte Burst kernel (VpDirectSkinInput, its own captured palette: the skinning, the
        // topology copy and the bounds in one kernel, then the index copy) and the 16-byte upload through the display's owner;
        // 'burst' = the float-input synchronous Burst kernel, the conservative bounds apart, the upload through the owner; 'gpu' =
        // the compute bake's issue (the palette's upload, the bindings, the dispatch; repeated 'repeat' times for the GPU time's
        // slope), the conservative bounds apart; 'none' = no bake (the frame's baseline). Every frame's whole times come from
        // FrameTimingManager and the XR display (main, render, GPU, present wait, XR app GPU), beside the local sections. Nothing
        // diagnostic runs in a measured frame: no reference, no readback, no counts, no picture. After each condition the outputs
        // are compared with the non-Burst reference (the product's with the quantisation of its 24-byte input told apart).
        private struct BenchFrame
        {
            public string condition, route; public int bodies, repeat, frame, invalid, writesRefused; public bool burstRan;
            public double kernelMs, boundsMs, uploadMs, indicesMs, issueMs, totalMs, dtMs;
            public Zantetsu.Core.Statistics.FrameTimeSample ft;
        }

        private readonly List<GameObject> _loadNpcs = new List<GameObject>();

        private IEnumerator Bench()
        {
            List<SkinBakeProbeSettings.BenchCondition> conditions = _settings.benchSeries;
            int maxBodies = conditions.Max(c => c.bodies);
            bool anyBurst = conditions.Any(c => c.route == "burst"), anyProduct = conditions.Any(c => c.route == "product"), anyGpu = conditions.Any(c => c.route == "gpu");
            if (templates == null || templates.Length == 0 || assets == null || assets.initialPose == null) { Fail("bench: the scene gives no NPC template or no pose table"); yield break; }
            yield return NpcSubject();
            if (_npcRenderer == null) { Fail("bench: the NPC was not made (see the npc rows)"); yield break; }
            if (world == null || !world.IsReady || world.Display == null) { Fail("bench: no ready cut world in the scene"); yield break; }
            SkinnedMeshRenderer renderer = _npcRenderer;
            Mesh mesh = renderer.sharedMesh;
            if (!SkinBakeProbeMeshInput.TryPrepare(mesh, out SkinBakeProbeMeshInput input, out string refusal)) { Fail("bench: the mesh was not prepared: " + refusal); yield break; }
            input.Hold();
            _inputs.Add(input);
            if (_settings.benchLoad > 0) yield return LoadNpcs(_settings.benchLoad);
            _npcSetPose(1);
            yield return null;
            float4x4[] palette = SkinBakeProbeCpuBake.Palette(renderer, out int missing);
            Matrix4x4 root = renderer.transform.localToWorldMatrix;
            SkinBakeProbeCpuBake.Inputs cpuInputs = SkinBakeProbeCpuBake.Read(input);
            _record.Fact("build", Application.platform + " " + (Debug.isDebugBuild ? "Development" : "Release") + "; buildGUID " + Application.buildGUID + "; unity " + Application.unityVersion + "; graphics " + SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceName + "; cpu " + SystemInfo.processorType);
            _record.Fact("bench", "series " + _settings.SeriesText + "; warmup " + _settings.benchWarmup + " frames and measured " + _settings.benchFrames + " frames a condition; mesh '" + mesh.name + "' vertices " + input.VertexCount + ", indices " + input.IndexCount + ", bones " + palette.Length + " (missing " + missing + "); pose 1 (the table at 0.8 s), the palette fixed once (the float routes' from SkinBakeProbeCpuBake.Palette, the product's from its own TryCapturePose, both at this frame); counts off (PROBE_NO_COUNTS); render load " + _settings.benchLoad + " NPCs; fixed view " + _fixedViewOn);
            RenderSettingsFact();

            // The preparations (not measured).
            SkinBakeProbeBurstBake.Prepared prepared = null;
            if (anyBurst)
            {
                long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
                prepared = SkinBakeProbeBurstBake.Prepare(cpuInputs, palette.Length);
                SkinBakeProbeBurstBake.SetPalette(prepared, palette);
                double prepareMs = Ms(p0, System.Diagnostics.Stopwatch.GetTimestamp());
                long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
                SkinBakeProbeBurstBake.Run(prepared, out bool firstBurstRan);
                _record.Fact("bench burst preparation", "native inputs " + prepared.BytesAllocated + " bytes made in " + prepareMs.ToString("F3", Inv) + " ms; first call " + Ms(f0, System.Diagnostics.Stopwatch.GetTimestamp()).ToString("F3", Inv) + " ms (Burst entry ran " + firstBurstRan + "; BurstCompiler.IsEnabled " + Unity.Burst.BurstCompiler.IsEnabled + ")");
            }

            VpDirectSkinInput direct = null;
            NativeArray<VpRenderVertex> productOut = default; NativeArray<int> productTopology = default; NativeArray<uint> productIndices = default;
            bool productReady = false;
            if (anyProduct)
            {
                SandboxNpcCharacter npc = renderer.GetComponentInParent<SandboxNpcCharacter>();
                string why = null;
                long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (npc == null || !npc.TryGetTopologyForDiagnosis(out int[] topologyMap, out int topologyCount)) why = "the NPC gives no cut intake topology";
                else if (!VpDirectSkinInput.TryCreate(renderer, topologyMap, topologyCount, world.CutInputConnectivity, out direct)) why = "VpDirectSkinInput.TryCreate refused the renderer (the product's cold input gate)";
                else if (!direct.TryCapturePose()) why = "VpDirectSkinInput.TryCapturePose refused (frame, weights or a missing bone)";
                double prepareMs = Ms(p0, System.Diagnostics.Stopwatch.GetTimestamp());
                if (why == null)
                {
                    productOut = new NativeArray<VpRenderVertex>(input.VertexCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    productTopology = new NativeArray<int>(input.VertexCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    productIndices = new NativeArray<uint>(Math.Max(1, direct.IndexCount), Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool firstOk = direct.TryBakeCaptured(productOut, productTopology, out float3 pmin, out float3 pmax) && direct.TryCopyIndices(productIndices, 0);
                    productReady = firstOk && direct.VertexCount == input.VertexCount;
                    _record.Fact("bench product preparation", "VpDirectSkinInput made the product's way (the slot's intake topology, the world's connectivity; model vertices " + direct.VertexCount + ", indices " + direct.IndexCount + ", topology " + direct.TopologyCount + ", model native " + direct.Model.NativeBytes + " bytes) and its pose captured in " + prepareMs.ToString("F3", Inv) + " ms; first bake " + Ms(f0, System.Diagnostics.Stopwatch.GetTimestamp()).ToString("F3", Inv) + " ms, ok " + firstOk + ", kernel bounds " + ((Vector3)pmin).ToString("F3") + " .. " + ((Vector3)pmax).ToString("F3") + (productReady ? "" : "; VERTEX COUNTS DIFFER: the product conditions are skipped"));
                }
                else _record.Fact("bench product preparation", "NOT READY: " + why + " (the product conditions are skipped)");
            }

            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Bounds conservative = SkinBakeProbeBounds.Conservative(mesh.bounds, palette, palette.Length, 0.01f);
            _record.Fact("bench bounds", "conservative " + conservative.min.ToString("F3") + " .. " + conservative.max.ToString("F3") + " in " + Ms(c0, System.Diagnostics.Stopwatch.GetTimestamp()).ToString("F4", Inv) + " ms (the first computation); the float Burst and GPU routes compute it every bake, timed apart; the product kernel measures its own bounds inside its kernel time");

            // The display regions: a placeholder each with the conservative bounds, shown once before any warmup; the subject's renderer hidden.
            bool wasEnabled = renderer.enabled;
            renderer.enabled = false;
            _placement = new Placement(world.Display.Placement);
            world.Display.Placement = _placement;
            GraphicsBuffer displayVertices = world.Display.GpuVertexBufferForDiagnosis;
            Vector3 centre = mesh.bounds.center;
            var poison = new VpRenderVertex[input.VertexCount];
            for (int i = 0; i < poison.Length; i++) poison[i] = new VpRenderVertex { position = centre + 0.005f * new Vector3((i % 3) - 1, ((i / 3) % 3) - 1, ((i / 9) % 3) - 1), normalX = 0, normalY = 0, u = 0, v = 0 };
            var topology = new int[input.VertexCount];
            for (int i = 0; i < topology.Length; i++) topology[i] = i;
            var submeshes = new VpGeometrySubmesh[cpuInputs.submeshIndexStart.Length];
            for (int s = 0; s < submeshes.Length; s++) submeshes[s] = new VpGeometrySubmesh(cpuInputs.submeshIndexStart[s], cpuInputs.submeshIndexCount[s], 0);
            var regions = new int[maxBodies];
            var fragments = new LogicalFragmentId[maxBodies];
            for (int b = 0; b < maxBodies; b++)
            {
                Matrix4x4 at = Matrix4x4.Translate(new Vector3((b - (maxBodies - 1) * 0.5f) * 0.8f, 0f, 0f)) * root;
                if (!Show(poison, cpuInputs.indices, topology, submeshes, at, conservative, out fragments[b], out VpStoredGeometry geometry, out string why))
                { Fail("bench: body " + b + " was not shown: " + why + HaltNote()); EndBench(renderer, wasEnabled, prepared, direct, productOut, productTopology, productIndices); yield break; }
                regions[b] = geometry.vertexStart;
            }

            _record.Fact("bench regions", string.Join(", ", regions.Select(r => r.ToString(Inv))) + " (vertex starts in the display's buffer; " + input.VertexCount + " elements each; shown once with the placeholder before the warmup; before every condition all " + maxBodies + " regions receive the reference shape (pose 1) through the owner, so every condition draws " + maxBodies + " bodies of the same shape; a condition of n bodies bakes the first n)");
            _gpu.CountsEnabled = false;
            var sampler = new Zantetsu.Core.Statistics.FrameTimeSampler { XrDisplay = XrDisplay() };
            _record.Fact("bench frame timing", "FrameTimingManager enabled " + Zantetsu.Core.Statistics.FrameTimeSampler.FrameTimingEnabled + "; XR display " + (sampler.XrDisplay != null ? "running (xrAppGpuMs = the display's app GPU time of the last frame)" : "none (no xrAppGpuMs)") + "; a frame's result may describe an earlier frame (the sampler's lag), duplicates and absences are counted and left empty, never 0");
            uint generationBefore = world.Display.GpuVertexGenerationForDiagnosis;
            yield return null;

            // The conditions, each a warmup then the measured frames; one bake of every body (times the repeat) a frame.
            int warm = _settings.benchWarmup, measured = _settings.benchFrames;
            var allRows = new List<BenchFrame>(measured * conditions.Count);
            var reference = new VpRenderVertex[input.VertexCount];
            int referenceInvalid = SkinBakeProbeCpuBake.Bake(cpuInputs, palette, reference, out _, out _);
            var referenceNative = new NativeArray<VpRenderVertex>(reference, Allocator.Persistent);
            long prefillWrites = 0, prefillRefused = 0;
            foreach (SkinBakeProbeSettings.BenchCondition condition in conditions)
            {
                string route = condition.route;
                int bodies = condition.bodies, repeat = condition.repeat;
                if (route == "product" && !productReady) { _record.Other("bench " + condition.Name, "condition", "observation", null, Time.frameCount, "skipped: the product input is not ready (see 'bench product preparation')"); continue; }
                // Every condition starts from the same picture: the reference shape (pose 1) in every region, written through the owner, then a
                // frame. Without this, 'none' drew the placeholders (first in the series) or whatever the previous condition left, so the frame's
                // GPU difference against 'none' carried a drawing difference (bodies, shape) that is not the bake's (the SKIN-BAKE-PROBE-3 review).
                for (int b = 0; b < maxBodies; b++) { if (world.Display.TryWriteGpuVerticesForDiagnosis(referenceNative, regions[b], input.VertexCount)) prefillWrites++; else prefillRefused++; }
                yield return null;
                var rows = new List<BenchFrame>(measured);
                for (int f = 0; f < warm + measured; f++)
                {
                    var row = new BenchFrame { condition = condition.Name, route = route, bodies = bodies, repeat = repeat, frame = Time.frameCount, burstRan = true, dtMs = Time.unscaledDeltaTime * 1000.0 };
                    for (int b = 0; b < bodies; b++)
                    {
                        if (route == "burst")
                        {
                            long a = System.Diagnostics.Stopwatch.GetTimestamp();
                            row.invalid += SkinBakeProbeBurstBake.Run(prepared, out bool ran);
                            long k = System.Diagnostics.Stopwatch.GetTimestamp();
                            _boundsSink = SkinBakeProbeBounds.Conservative(mesh.bounds, palette, palette.Length, 0.01f);
                            long c = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (!world.Display.TryWriteGpuVerticesForDiagnosis(prepared.Output, regions[b], input.VertexCount)) row.writesRefused++;
                            long u = System.Diagnostics.Stopwatch.GetTimestamp();
                            row.burstRan &= ran;
                            row.kernelMs += Ms(a, k); row.boundsMs += Ms(k, c); row.uploadMs += Ms(c, u); row.totalMs += Ms(a, k) + Ms(c, u);
                        }
                        else if (route == "product")
                        {
                            long a = System.Diagnostics.Stopwatch.GetTimestamp();
                            bool ok = direct.TryBakeCaptured(productOut, productTopology, out _, out _);   // the skinning, the topology copy and the bounds: the product kernel's one call
                            long k = System.Diagnostics.Stopwatch.GetTimestamp();
                            direct.TryCopyIndices(productIndices, 0);
                            long c = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (!world.Display.TryWriteGpuVerticesForDiagnosis(productOut, regions[b], input.VertexCount)) row.writesRefused++;
                            long u = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (!ok) row.invalid++;
                            row.kernelMs += Ms(a, k); row.indicesMs += Ms(k, c); row.uploadMs += Ms(c, u); row.totalMs += Ms(a, k) + Ms(c, u);
                        }
                        else if (route == "gpu")
                        {
                            long a = System.Diagnostics.Stopwatch.GetTimestamp();
                            for (int r = 0; r < repeat; r++) _gpu.Bake(input, palette, displayVertices, regions[b]);
                            long k = System.Diagnostics.Stopwatch.GetTimestamp();
                            _boundsSink = SkinBakeProbeBounds.Conservative(mesh.bounds, palette, palette.Length, 0.01f);
                            long c = System.Diagnostics.Stopwatch.GetTimestamp();
                            row.issueMs += Ms(a, k); row.boundsMs += Ms(k, c); row.totalMs += Ms(a, k);
                        }
                    }

                    row.ft = sampler.Sample();
                    if (f >= warm) rows.Add(row);
                    yield return null;
                }

                allRows.AddRange(rows);
                foreach (BenchFrame r in rows)
                {
                    _record.BenchFrame(r.condition, r.route, r.bodies.ToString(Inv), r.repeat.ToString(Inv), r.frame.ToString(Inv), r.kernelMs.ToString("F4", Inv), r.boundsMs.ToString("F4", Inv), r.uploadMs.ToString("F4", Inv), r.indicesMs.ToString("F4", Inv),
                        r.issueMs.ToString("F4", Inv), r.totalMs.ToString("F4", Inv), r.invalid.ToString(Inv), r.burstRan ? "true" : "false", r.writesRefused.ToString(Inv), r.dtMs.ToString("F3", Inv),
                        r.ft.HasMain ? r.ft.MainMs.ToString("F3", Inv) : "", r.ft.HasRender ? r.ft.RenderMs.ToString("F3", Inv) : "", r.ft.HasGpu ? r.ft.GpuMs.ToString("F3", Inv) : "", r.ft.HasPresentWait ? r.ft.PresentWaitMs.ToString("F3", Inv) : "", r.ft.HasGpuXr ? r.ft.GpuXrMs.ToString("F3", Inv) : "");
                }

                Stat(condition, "kernelMs", rows.Select(r => (true, r.kernelMs)), route == "burst" || route == "product");
                Stat(condition, "boundsMs", rows.Select(r => (true, r.boundsMs)), route == "burst" || route == "gpu");
                Stat(condition, "uploadMs", rows.Select(r => (true, r.uploadMs)), route == "burst" || route == "product");
                Stat(condition, "indicesMs", rows.Select(r => (true, r.indicesMs)), route == "product");
                Stat(condition, "issueMs", rows.Select(r => (true, r.issueMs)), route == "gpu");
                Stat(condition, "totalMs", rows.Select(r => (true, r.totalMs)), route != "none");
                Stat(condition, "frameDtMs", rows.Select(r => (true, r.dtMs)), true);
                Stat(condition, "ftMainMs", rows.Select(r => (r.ft.HasMain, (double)r.ft.MainMs)), true);
                Stat(condition, "ftRenderMs", rows.Select(r => (r.ft.HasRender, (double)r.ft.RenderMs)), true);
                Stat(condition, "ftGpuMs", rows.Select(r => (r.ft.HasGpu, (double)r.ft.GpuMs)), true);
                Stat(condition, "ftPresentWaitMs", rows.Select(r => (r.ft.HasPresentWait, (double)r.ft.PresentWaitMs)), true);
                Stat(condition, "xrAppGpuMs", rows.Select(r => (r.ft.HasGpuXr, (double)r.ft.GpuXrMs)), true);
                if (route == "burst") _record.Fact("bench " + condition.Name + " burst ran", (rows.All(r => r.burstRan) ? "every measured call through the Burst entry" : "SOME MEASURED CALL RAN MANAGED") + "; invalid " + rows.Sum(r => r.invalid) + "; writes refused " + rows.Sum(r => r.writesRefused));
                if (route == "product") _record.Fact("bench " + condition.Name + " product", "kernel not ok " + rows.Sum(r => r.invalid) + " calls; writes refused " + rows.Sum(r => r.writesRefused));

                // The equality after the condition's measured frames (with the measurement's settings): the outputs against the non-Burst reference.
                if (route == "burst")
                {
                    using (var data = new NativeArray<VpRenderVertex>(prepared.Output, Allocator.Temp))
                    {
                        SkinBakeProbeCompare.Result d = SkinBakeProbeCompare.Compare("bench " + condition.Name + " burst output", data.Reinterpret<uint4>(), 0, input.VertexCount, 0, reference, 0, 0, 0, SkinBakeProbeGpuBake.Poison);
                        d.countsKnown = false; d.frameRequested = d.frameCompleted = Time.frameCount;
                        _record.Bake("bench " + condition.Name, "burst output vs reference", d, "native", 0, 0.0, "the prepared output after the last measured call; reference invalid " + referenceInvalid);
                    }
                }

                if (route == "product")
                {
                    using (var data = new NativeArray<VpRenderVertex>(productOut, Allocator.Temp))
                    {
                        SkinBakeProbeCompare.Result d = SkinBakeProbeCompare.Compare("bench " + condition.Name + " product output vs float reference", data.Reinterpret<uint4>(), 0, input.VertexCount, 0, reference, 0, 0, 0, SkinBakeProbeGpuBake.Poison);
                        d.countsKnown = false; d.frameRequested = d.frameCompleted = Time.frameCount;
                        // The 24-byte input quantises the normal (oct bytes) and the weights (uint8): the differences are the input's, not a defect; reported, not judged.
                        _record.Other("bench " + condition.Name, "product output vs float reference (quantisation of the 24-byte input)", "quantisation", null, Time.frameCount, d.Describe());
                    }
                }

                if (route != "none")
                {
                    for (int b = 0; b < bodies; b++)
                    {
                        VpRenderVertex[] expected = route == "product" ? productOut.ToArray() : reference;
                        SkinBakeProbeCompare.Result verified = null;
                        _gpu.Verify("bench " + condition.Name + " region " + b, displayVertices, regions[b], input.VertexCount, 0, 0, expected, r => verified = r);
                        while (verified == null) { _gpu.Update(); yield return null; }
                        _record.Bake("bench " + condition.Name, "region " + b + " after the measured frames", verified, "display", regions[b], 0.0, "read back after the condition's measured frames, with the measurement's settings (counts off): what the route draws; compared with " + (route == "product" ? "the product kernel's own output (the upload)" : "the non-Burst reference"));
                    }
                }

                _record.Other("bench " + condition.Name, "condition", "observation", null, Time.frameCount, "route " + route + ", bodies " + bodies + ", repeat " + repeat + ", measured " + rows.Count + " frames after " + warm + " warmup; sampler reads " + sampler.Reads + ", duplicates " + sampler.Duplicates + ", absent " + sampler.Absent + ", gpu absent " + sampler.GpuAbsent + "; " + LagText(sampler));
            }

            referenceNative.Dispose();
            uint generationAfter = world.Display.GpuVertexGenerationForDiagnosis;
            long cpuWrites = conditions.Where(c => c.route == "burst" || (c.route == "product" && productReady)).Sum(c => (long)c.bodies * (warm + measured));
            _record.Fact("bench owner writes", "display vertex generation " + generationBefore + " -> " + generationAfter + " (one owner write a body a frame on the CPU routes expected: " + cpuWrites + ", plus the pre-fill of every region before each condition: " + prefillWrites + " written, " + prefillRefused + " refused; expected total " + (cpuWrites + prefillWrites) + "); writes refused " + allRows.Sum(r => r.writesRefused) + " in the measured frames");
            _record.Fact("bench frames", "the sums over a frame's bodies (and the GPU repeat); kernelMs = the Burst call (burst: skinning only; product: skinning + topology copy + bounds); boundsMs = the conservative box (burst, gpu); indicesMs = the product's index copy; uploadMs = the 16-byte write through the display's owner (SetData + vertex generation); issueMs = palette upload + bindings + dispatch x repeat (gpu); totalMs = kernel + upload (CPU routes) or issue (gpu); ftMainMs/ftRenderMs/ftGpuMs/ftPresentWaitMs from FrameTimingManager, xrAppGpuMs from the XR display, empty where absent; GPU execution time of the bake NOT measured directly (the gpu repeat conditions give its slope from ftGpuMs/xrAppGpuMs)");
            if (_settings.picture) { yield return Hold(2, "bench-series"); }
            EndBench(renderer, wasEnabled, prepared, direct, productOut, productTopology, productIndices);
        }

        private void EndBench(SkinnedMeshRenderer renderer, bool wasEnabled, SkinBakeProbeBurstBake.Prepared prepared, VpDirectSkinInput direct, NativeArray<VpRenderVertex> productOut, NativeArray<int> productTopology, NativeArray<uint> productIndices)
        {
            _gpu.CountsEnabled = true;
            prepared?.Dispose();
            direct?.Dispose();
            if (productOut.IsCreated) productOut.Dispose();
            if (productTopology.IsCreated) productTopology.Dispose();
            if (productIndices.IsCreated) productIndices.Dispose();
            EndDraw(renderer, wasEnabled);
        }

        // n (present values), median, p95 (nearest rank), mean, min, max of a bench column of one condition: a fact and a summary row; "not this route's" where the column is not measured here.
        private void Stat(SkinBakeProbeSettings.BenchCondition condition, string column, IEnumerable<(bool has, double value)> values, bool measured)
        {
            string name = "bench " + condition.Name + " " + column;
            if (!measured) { _record.Fact(name, "not this route's (empty)"); return; }
            double[] v = values.Where(x => x.has).Select(x => x.value).ToArray();
            if (v.Length == 0) { _record.Fact(name, "no value (absent in every measured frame)"); _record.BenchSummary(condition.Name, condition.route, condition.bodies.ToString(Inv), condition.repeat.ToString(Inv), column, "0", "", "", "", "", ""); return; }
            Array.Sort(v);
            double median = v.Length % 2 == 1 ? v[v.Length / 2] : 0.5 * (v[v.Length / 2 - 1] + v[v.Length / 2]);
            double p95 = v[Math.Min(v.Length - 1, Math.Max(0, (int)Math.Ceiling(0.95 * v.Length) - 1))];
            _record.Fact(name, "median " + median.ToString("F4", Inv) + "; p95 " + p95.ToString("F4", Inv) + "; n " + v.Length + "; mean " + v.Average().ToString("F4", Inv) + "; min " + v[0].ToString("F4", Inv) + "; max " + v[v.Length - 1].ToString("F4", Inv));
            _record.BenchSummary(condition.Name, condition.route, condition.bodies.ToString(Inv), condition.repeat.ToString(Inv), column, v.Length.ToString(Inv), median.ToString("F4", Inv), p95.ToString("F4", Inv), v.Average().ToString("F4", Inv), v[0].ToString("F4", Inv), v[v.Length - 1].ToString("F4", Inv));
        }

        // The sampler's lag observation exists in the Editor and the Development Player only (ZANTETSU_OBSERVE).
        private static string LagText(Zantetsu.Core.Statistics.FrameTimeSampler sampler)
        {
#if ZANTETSU_OBSERVE
            return sampler.DescribeLag();
#else
            return "lag not observed (not a ZANTETSU_OBSERVE build)";
#endif
        }

        private static UnityEngine.XR.XRDisplaySubsystem XrDisplay()
        {
            var displays = new List<UnityEngine.XR.XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            foreach (UnityEngine.XR.XRDisplaySubsystem d in displays) if (d.running) return d;
            return null;
        }

        // The render settings a bench ran under: the eye texture, the render scale, the pipeline asset's shadow and MSAA fields (read by name, as the NPC bench does), the target frame rate.
        private void RenderSettingsFact()
        {
            var t = new System.Text.StringBuilder();
            t.Append("eye texture ").Append(UnityEngine.XR.XRSettings.enabled ? UnityEngine.XR.XRSettings.eyeTextureWidth + "x" + UnityEngine.XR.XRSettings.eyeTextureHeight : "no XR")
                .Append("; eyeTextureResolutionScale ").Append(UnityEngine.XR.XRSettings.eyeTextureResolutionScale.ToString("R", Inv))
                .Append("; renderViewportScale ").Append(UnityEngine.XR.XRSettings.renderViewportScale.ToString("R", Inv))
                .Append("; stereo ").Append(UnityEngine.XR.XRSettings.stereoRenderingMode).Append("; targetFrameRate ").Append(Application.targetFrameRate).Append("; vSyncCount ").Append(QualitySettings.vSyncCount);
            RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
            t.Append("; pipeline asset ").Append(asset != null ? asset.name : "none");
            if (asset != null)
            {
                foreach (string field in new[] { "m_MainLightShadowsSupported", "m_ShadowCascadeCount", "m_ShadowDistance", "m_MainLightShadowmapResolution", "m_SoftShadowsSupported", "m_ShadowDepthBias", "m_ShadowNormalBias", "m_MSAA", "m_RenderScale", "m_SupportsHDR" })
                {
                    System.Reflection.FieldInfo f = asset.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    t.Append("; ").Append(field).Append(' ').Append(f != null ? Convert.ToString(f.GetValue(asset), Inv) : "?");
                }
            }

            if (mainLight != null) t.Append("; main light shadows ").Append(mainLight.shadows).Append(" strength ").Append(mainLight.shadowStrength.ToString("F2", Inv)).Append(" intensity ").Append(mainLight.intensity.ToString("F2", Inv));
            t.Append("; cut display casters ").Append(VpLogicalCutDisplay.CastersOffForDiagnosis ? "WITHHELD (diagnosis)" : "issued");
            _record.Fact("render settings", t.ToString());
        }

        /// <summary>Verification runs that picture every frame of the cut bench (SKIN-LINES-1): pictures on, a directory, and -zantetsuSkinBakeProbeCutPictureFrames above 0.</summary>
        private bool FramePictures => FrameWindow && _settings.picture;
        /// <summary>MOTION-CAPTURE-1 step 6: the window of frames whose state rows, motion captures and view rows are kept (pre..+N after the last request) -- with or without pictures (-zantetsuSkinBakeProbeNoPicture keeps the raw motion and the metadata only, as the TL asked: no per-frame PNG as the main means).</summary>
        private bool FrameWindow => !_settings.cutNoVerify && !string.IsNullOrEmpty(_settings.directory) && _settings.cutPictureFrames > 0;

        // The shadow condition of the silhouette-line comparison (SKIN-LINES-1), set once before the cut bench's first
        // frame: the cut display's casters withheld (the bodies drawn as before; the SkinnedMeshRenderers and the scene
        // still cast), the main light's shadow strength 0 (the map still rendered, no surface receives it), or the main
        // light without shadows. Nothing of it is in a performance run's path: "both" changes nothing.
        private void ApplyShadowsSetting()
        {
            string how;
            switch (_settings.shadows)
            {
                case "nocast":
                    VpLogicalCutDisplay.CastersOffForDiagnosis = true;
                    how = "the cut display issues no shadow casters (VpLogicalCutDisplay.CastersOffForDiagnosis); the SkinnedMeshRenderers and the scene still cast, every surface still receives";
                    break;
                case "noreceive":
                    if (mainLight != null) mainLight.shadowStrength = 0f;
                    how = mainLight != null ? "main light shadowStrength 0: the shadow map is still rendered (casters issued), no surface receives it" : "NO MAIN LIGHT in the scene: nothing changed";
                    break;
                case "none":
                    if (mainLight != null) mainLight.shadows = LightShadows.None;
                    how = mainLight != null ? "main light shadows None: no shadow map" : "NO MAIN LIGHT in the scene: nothing changed";
                    break;
                default:
                    how = "the scene's shadows as they are";
                    break;
            }

            _record.Fact("shadows", _settings.shadows + ": " + how + (mainLight != null ? "; main light now shadows " + mainLight.shadows + ", strength " + mainLight.shadowStrength.ToString("F2", Inv) : ""));
        }

        // Extra NPC slots drawn by their SkinnedMeshRenderers, as render load: the templates round-robin, prepared and activated as the subject was, posed once by the table (phases apart), in rows behind the subject.
        private IEnumerator LoadNpcs(int count)
        {
            // A share a family, as the NPC bench prepares its slots (a share holds one family's model).
            var shares = new Dictionary<string, SandboxNpcCharacter.SlotShare>(StringComparer.Ordinal);
            if (!PoseTable.TryRead(assets.initialPose.bytes, out PoseTable clip, out string clipError)) { _record.Fact("bench load", "no load: the pose table could not be read: " + clipError); yield break; }
            var holders = new List<GameObject>();
            var failures = new List<string>();
            int made = 0, visible = 0;
            for (int i = 0; i < count; i++)
            {
                GameObject templateHolder = null;
                for (int k = 0; k < templates.Length && templateHolder == null; k++)
                {
                    GameObject t = templates[(i + k) % templates.Length];
                    if (t != null && t.GetComponentInChildren<SandboxNpcCharacter>(true) != null) templateHolder = t;
                }

                if (templateHolder == null) break;
                GameObject holder = Instantiate(templateHolder);
                holder.name = "skin bake probe load " + i;
                _made.Add(holder); _loadNpcs.Add(holder);
                SandboxNpcCharacter npc = holder.GetComponentInChildren<SandboxNpcCharacter>(true);
                PoseTablePlayer pose = npc.CharacterRoot.GetComponent<PoseTablePlayer>();
                if (pose == null) continue;
                pose.Configure(assets.initialPose, pose.ModelRoot, 0.0, true);
                pose.Managed = true;
                double phase = (i * 0.618) % 1.0 * 2.0;
                pose.PlanSource = (double target, out PoseTable table, out double source) => { table = clip; source = clip.ResolveSourceTime(phase); return true; };
                if (!shares.TryGetValue(npc.Family ?? "", out SandboxNpcCharacter.SlotShare share)) shares[npc.Family ?? ""] = share = new SandboxNpcCharacter.SlotShare();
                npc.PrepareAsSlot(share);
                npc.CharacterRoot.SetActive(true);
                holder.SetActive(true);
                npc.enabled = true;
                holders.Add(holder);
            }

            float by = Time.realtimeSinceStartup + 120f;
            foreach (GameObject holder in holders)
            {
                SandboxNpcCharacter npc = holder.GetComponentInChildren<SandboxNpcCharacter>(true);
                while (!npc.IsPrepared && npc.Failure == null && Time.realtimeSinceStartup < by) yield return null;
                if (!npc.IsPrepared) { failures.Add(npc.Family + ": " + (npc.Failure ?? "not prepared in time")); continue; }
                int row = made / 8, col = made % 8;
                npc.CharacterRoot.transform.SetPositionAndRotation(new Vector3((col - 3.5f) * 0.9f, 0f, 5f + row * 1.5f), Quaternion.Euler(0f, 180f, 0f));
                if (!npc.Activate()) { failures.Add(npc.Family + ": not activated: " + npc.Failure); continue; }
                npc.CharacterRoot.GetComponent<PoseTablePlayer>().ApplyNow(null, 0);
                made++;
            }

            yield return null;
            foreach (GameObject holder in holders) { SandboxNpcCharacter npc = holder.GetComponentInChildren<SandboxNpcCharacter>(true); if (npc != null && npc.Renderer != null && npc.Renderer.isVisible) visible++; }
            _record.Fact("bench load", count + " asked; " + made + " NPC slots activated and drawn by their SkinnedMeshRenderers (posed once, phases apart), " + visible + " visible to a camera after a frame; placed in rows of 8 from z 5 m behind the subject; families " + shares.Count + (failures.Count > 0 ? "; FAILED " + failures.Count + ": " + string.Join(" | ", failures.Take(4)) : ""));
        }

        private string HaltNote() => world != null && world.Display != null && world.Display.IsHalted ? "; DISPLAY HALTED (" + world.Display.HaltReason + ")" : "";

        private void EndDraw(SkinnedMeshRenderer renderer, bool wasEnabled)
        {
            renderer.enabled = wasEnabled;
            if (_placement != null && world != null && world.Display != null && !world.Display.IsDisposed && ReferenceEquals(world.Display.Placement, _placement)) world.Display.Placement = _placement.Inner;
        }

        // extent: the producer's conservative bounds in place of the measure of the appended vertices (a placeholder whose region the GPU bakes into); null keeps the measure.
        private bool Show(VpRenderVertex[] vertices, uint[] indices, int[] topology, VpGeometrySubmesh[] submeshes, Matrix4x4 objectToWorld, Bounds? extent, out LogicalFragmentId fragment, out VpStoredGeometry geometry, out string why)
        {
            fragment = default; why = null;
            if (!world.Storage.TryAppendPrepared(vertices, indices, topology, vertices.Length, submeshes, out geometry)) { why = "the storage refused the geometry"; return false; }
            if (extent.HasValue && !world.Storage.TryReplaceExtentBounds(geometry, extent.Value)) { why = "the storage did not take the conservative bounds (the appended geometry stays unshown)"; return false; }
            fragment = world.Ledger.AddFragment();
            _placement.Move(fragment, objectToWorld);
            bool shown;
            try { shown = world.Display.TryShow(fragment, geometry, objectToWorld, Matrix4x4.identity, Array.Empty<VpClipBoundary>()); }
            catch (Exception e) { _placement.Forget(fragment); world.Ledger.Retire(fragment); why = "the display threw: " + e.Message; return false; }
            if (!shown) { _placement.Forget(fragment); world.Ledger.Retire(fragment); why = "the display refused to show the geometry (not live, already on a shown lineage, outside the input contract, no room, or the frame already settled)"; return false; }
            return true;
        }

        // ---- the cut bench (SKIN-GPU-FIRST-1): N prepared NPCs cut through the product's own prepared cut (SandboxNpcCharacter.Handle.TryCut) in
        // one frame (or staggered), under the launch's skin bake route (-zantetsuSkinBake cpu|gpu|float, the world's VP SKIN BAKE line). Recorded:
        // every request's outcome and main-thread time at the request, the frames to its acceptance and to its commit, the fallback or cancel
        // reason; every frame's whole times with the pending counts; the switch's equality (the display's GPU region read back, verification
        // only, against the storage's CPU geometry once the cut is admitted); pictures at the request frame and the switch frame.
        private sealed class CutRequestState
        {
            public int body, requestFrame = -1, acceptedFrame = -1, physicsFrame = -1, publishedFrame = -1, clipFrame = -1, readyFrame = -1, committedFrame = -1;
            // SKIN-GPU-FIRST-STARTUP-1: Stopwatch ticks of the request's call and of the moment each event was first observed (0 = not observed).
            public long requestBeganTick, requestEndedTick, acceptedTick, physicsTick, publishedTick, clipTick, readyTick, committedTick;
            // SKIN-GPU-FIRST-EVAL-1: the events themselves -- the product's diagnosis hooks, frame and tick taken inside the hook -- apart from the
            // bench's observations above (physicsFrame/physicsTick are the driver's hook; physicsObserved* is the transaction's PublishedFrame seen
            // at the step), and the clip state seen at onBeforeRender. readyOutcome = (int)VpSkinBakeCollectOutcome of the collect that ended the bake.
            public int physicsObservedFrame = -1, ledgerEventFrame = -1, clipBeforeRenderFrame = -1, readyEventFrame = -1, readyEndedFrame = -1, committedEventFrame = -1, readyOutcome = -1;
            public long physicsObservedTick, ledgerEventTick, clipBeforeRenderTick, readyEventTick, readyEndedTick, committedEventTick;
            // EVAL-1 review 1 (P2-3): "valid", or "invalid: ..." when a reset before this round did not return to the initial state (the round is no trial of the same condition).
            public string roundValid = "valid";
            public string ReadyOutcomeName => !gpuFirst ? "synchronous" : readyEventFrame >= 0 ? "completed" : readyOutcome >= 0 ? ((VpSkinBakeCollectOutcome)readyOutcome).ToString().ToLowerInvariant() : "pending";
            public int round;
            public VpPreparedCharacterCut handle;
            public SandboxNpcCharacter npc;
            public VpCharacterCutOutcome outcome;
            public ProvisionalCutAcceptance acceptance;
            public double requestMs, cutKernelMs = -1;
            public LogicalFragmentId source, positive, negative;
            public CutOperationId operation;
            public string switchCompare = "", fallbackReason = "";
            // CPU-SKIN-FLOAT-1: the route asked for and the route taken (from the handle right after the call), and the bake's times.
            public string requestedRoute, effectiveRoute;
            public double bakeKernelMs, bakeAppendMs, showMs;
            public bool compareAsked, pictureTaken, gpuFirst;
            public VpStoredGeometry gpuGeometry;
            public long gpuRequestId;
            // PLANE-1: the plane the request was made with (the renderer's frame), the same in world, the anchor, the placement, and how the sides check came out.
            public float4 planeLocal, planeWorld;
            public float3 anchor;
            public Matrix4x4 rendererToWorld;
            public string planeNote = "";
            public bool planeFailed;
            public string BudgetWait => outcome == VpCharacterCutOutcome.Requested && acceptance == ProvisionalCutAcceptance.Pending ? "shared Main budget: physics +" + F(physicsFrame, requestFrame) : "none";
            public string GeometryWait => gpuFirst ? "gpu route: cpu ready +" + F(readyFrame, requestFrame) + ", commit +" + F(committedFrame, requestFrame) : "none (synchronous geometry)";
            public static string F(int frame, int request) => frame >= 0 ? (frame - request).ToString(Inv) : "-1";
        }

        private IEnumerator CutBench()
        {
            int n = _settings.cutBodies, stagger = _settings.cutStagger, warm = _settings.benchWarmup, measured = _settings.benchFrames;
            if (templates == null || templates.Length == 0 || assets == null || assets.initialPose == null) { Fail("cut bench: the scene gives no NPC template or no pose table"); yield break; }
            if (world == null || !world.IsReady || world.Display == null || world.Driver == null) { Fail("cut bench: no ready cut world in the scene"); yield break; }
            _record.Fact("build", Application.platform + " " + (Debug.isDebugBuild ? "Development" : "Release") + "; buildGUID " + Application.buildGUID + "; unity " + Application.unityVersion + "; graphics " + SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceName + "; cpu " + SystemInfo.processorType);
            _record.Fact("cut bench", "bodies " + n + "; stagger " + stagger + " frames; rounds " + _settings.cutRounds + "; " + measured + " frames observed after the last request, " + warm + " warmup; load " + _settings.benchLoad + " NPCs; skin bake route " + world.SkinBakeRoute + " (" + world.SkinBakeReason + "); mode asked " + VpSkinBakeSetup.Name(VpSkinBakeSetup.Mode) + "; fixed view " + _fixedViewOn
                + "; cut plane " + _settings.cutPlane + ": " + (_settings.cutPlane == SkinBakeProbeCutPlane.Horizontal
                    ? "the world's horizontal plane through the renderer's local bounds centre taken to world, the world's up positive, converted to the renderer's frame by VpCutPlane.TryWorldToGeometryLocal (a refusal fails the request)"
                    : "float4(0, 1, 0, -localBounds.center.y) in the renderer's frame (every earlier cut bench's plane; it splits this project's Z-up NPC meshes front/back)")
                + "; anchor the bounds centre in the world; the requests are made from the probe's coroutine (after the Updates, before the frame's display collection)"
                + (_settings.holdCpuReady > 0 ? "; DIAGNOSIS: the gpu route's CPU bake kept out of the dispatcher " + _settings.holdCpuReady + " frames after the hit (no performance figure of this run is a measurement)" : "")
                + (FramePictures ? "; a picture of every frame from the last warmup frame (pre) to +" + _settings.cutPictureFrames + " after the last request (k = the step of the bench), no per-body request or ready picture" : ""));
            ApplyShadowsSetting();
            ApplyDiagnosisSettings();
            if (_settings.warm == "draw" || _settings.warm == "draw+skin-dispatch")
            {
                // SKIN-GPU-FIRST-STARTUP-1: the preparation before the bench, provided by the platform host (the Quest's QuestDrawPrewarm); none on the PC.
                if (WarmBeforeBench != null)
                {
                    int warmBegan = Time.frameCount; long warmT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    yield return WarmBeforeBench();
                    _record.Fact("warm before the bench (draw)", _settings.warm + ": frames " + warmBegan + ".." + Time.frameCount + ", " + Ms(warmT0, System.Diagnostics.Stopwatch.GetTimestamp()).ToString("F1", Inv) + " ms wall; report: " + (WarmReport != null ? string.Join(" | ", WarmReport) : "none"));
                }
                else _record.Fact("warm before the bench (draw)", _settings.warm + " asked, but this platform provides no such preparation (the PC has no QuestDrawPrewarm): NOT warmed");
            }
            else if (_settings.warm == "none") _record.Fact("warm before the bench", "none (the probe does not run the product's start-up preparation; the first cut is a cold one for the drawing, the bake and their resources)");
            // skin-prepare / skin-dispatch run in round 0 once the bodies exist (WarmSkin), before the warmup frames.
            HookReactionRecord(true);
            _record.Fact("cut bench reaction record", "numeric, no picture (SKIN-GPU-FIRST-EVAL-1): EVENTS from the product's diagnosis hooks with the frame and the Stopwatch tick taken inside the hook -- provisional physics (ProvisionalCutDriver.PublishedForDiagnosis, the pair in the scene), "
                + "ledger publication (LogicalCutLedger.PublishedForDiagnosis, the children issued), CPU ready (VpSkinBakePending.CollectedForDiagnosis = Completed: the basis published; Failed/Dropped/Abandoned/Withdrawn kept apart as 'ready ended'), commit (CutDag.CommittedForDiagnosis) -- "
                + "matched to the request by operation id or bake request id, each kept once (a repeated report of the same event is counted, not recorded twice), in preallocated buffers written after the window; OBSERVATIONS apart: the transaction's PublishedFrame, the ledger state, the storage's pending flag "
                + "and the geometry stage at the bench's step, and the display's clip state (ProvisionalSplit of the source or a child) at onBeforeRender -- a display state, never a pixel's time; -1 = not reported / not observed, never 'did not happen'. "
                + "The budget wait (acceptance Pending) and the gpu route's geometry wait are recorded apart (budgetWait, geometryWait).");
            RenderSettingsFact();
            if (_settings.benchLoad > 0) yield return LoadNpcs(_settings.benchLoad);
            _gpu.CountsEnabled = false;
            var sampler = new Zantetsu.Core.Statistics.FrameTimeSampler { XrDisplay = XrDisplay() };
            // The frame a timing result describes: its start (the FrameTimingManager's CPU timer, taken as Stopwatch's domain) matched to
            // the NEAREST sampled frame start, so that a request frame's row is read from the result that describes it, not from the
            // result read in it (TL review 2026-11-01, P2-3). The sample tick is taken in the frame's Update, a little after the
            // frame's start: "the latest sample at or before" named the frame before (cut4-*), so the nearest is taken and the signed
            // residual (the result's start minus the matched sample tick, ms) is written beside it; a small negative value is the
            // expected one, a value near a frame's length means the match is doubtful.
            var sampledFrames = new List<(int frame, long ticks)>(4096);
            (string frame, string residual) FtFrame(Zantetsu.Core.Statistics.FrameTimeSample sample)
            {
                if (!sample.HasMain && !sample.HasGpu) return ("", "");
                ulong frequency = FrameTimingManager.GetCpuTimerFrequency();
                if (frequency == 0) return ("", "");
                long ticks = (long)((double)sample.FrameStart * System.Diagnostics.Stopwatch.Frequency / frequency);
                int best = -1; long bestDistance = long.MaxValue, bestTicks = 0;
                for (int i = sampledFrames.Count - 1; i >= 0 && i >= sampledFrames.Count - 64; i--)
                {
                    long distance = Math.Abs(sampledFrames[i].ticks - ticks);
                    if (distance < bestDistance) { bestDistance = distance; bestTicks = sampledFrames[i].ticks; best = sampledFrames[i].frame; }
                }
                if (best < 0) return ("", "");
                return (best.ToString(Inv), ((ticks - bestTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F3", Inv));
            }
            _record.Fact("cut bench frame timing", "FrameTimingManager enabled " + Zantetsu.Core.Statistics.FrameTimeSampler.FrameTimingEnabled + "; XR display " + (sampler.XrDisplay != null ? "running (xrAppGpuMs = the display's app GPU time of the last frame)" : "none (no xrAppGpuMs)")
                + "; ftFrame = the frame a result describes (its start matched to the nearest sampled frame start; the timer domain assumed Stopwatch's), ftStartOffsetMs = the result's start minus that frame's sample tick" + (_settings.cutNoVerify ? "; PERFORMANCE RUN: no picture and no readback in the measured frames" : "; VERIFICATION RUN: pictures at the request and switch frames, a readback at the switch"));
            // EVAL-1 review 1 (P2-3): the initial state every round must return to -- no live cut fragment, owner, pending bake, pending storage or
            // pending basis beyond what the scene holds before the first round. A reset that does not return to it makes the rounds after it invalid.
            _baselineLiveFragments = world.Ledger.LiveCutFragmentCount; _baselineOwners = world.Owners.Count; _resetFailure = null; _invalidRounds = 0;
            _record.Fact("cut bench initial state", "live cut fragments " + _baselineLiveFragments + ", owners " + _baselineOwners + ", pending bakes " + (world.SkinBakePending != null ? world.SkinBakePending.Count : 0) + ", storage pending " + world.Storage.DeferredPendingCount + ", DAG pending bases " + world.Geometry.PendingBasisCount + " -- every round's reset must return to this, or the rounds after it are invalid (roundValid)");
            for (int round = 0; round < _settings.cutRounds; round++)
            {
                var npcs = new List<SandboxNpcCharacter>();
                yield return MakeCutBodies(n, round, npcs);
                if (npcs.Count < n) { Fail("cut bench round " + round + ": " + npcs.Count + " of " + n + " bodies were made (see the 'cut bench bodies' fact)"); yield break; }
                var states = new CutRequestState[n];
                for (int b = 0; b < n; b++) states[b] = new CutRequestState { body = b, round = round, npc = npcs[b], handle = npcs[b].Handle, roundValid = _resetFailure == null ? "valid" : "invalid: " + _resetFailure };
                if (_resetFailure != null) _invalidRounds++;
                if (round == 0) RecordSkinInputs(states);
                lastStates = states;
                _activeStates = states;
                _allStates.AddRange(states);
                BeginMarks(round, warm + lastRequestAtOf(n, stagger) + measured + 8);
                if (round == 0 && (_settings.warm == "skin-prepare" || _settings.warm == "skin-dispatch" || _settings.warm == "draw+skin-dispatch")) yield return WarmSkin(states, _settings.warm != "skin-prepare");
                for (int f = 0; f < warm; f++) { sampledFrames.Add((Time.frameCount, System.Diagnostics.Stopwatch.GetTimestamp())); MarkStep(Time.frameCount, f - warm); sampler.Sample(); RecordFtmRaw(Time.frameCount); _gpu.Update(); if (f == warm - 2 && FrameWindow) { OpenMotionWindow(true); RecordPictureState(round, -2, "no-picture cut-r" + round + "-pre2", states); }   // the window one frame before pre: its first frame's motion pass is already past when it opens (mv1 runs, 2026-11-02)
                    if (f == warm - 1 && FrameWindow) { OpenMotionWindow(true); if (_settings.motionCapture) foreach (CutRequestState st in states) if (st.npc != null && st.npc.Renderer != null) DumpBodyMesh(round, st.body, st.npc.Renderer); StartMotionView(round); if (FramePictures) TakePicture("cut-r" + round + "-pre"); RecordPictureState(round, -1, (FramePictures ? "" : "no-picture ") + "cut-r" + round + "-pre", states); } yield return null; }
                int lastRequestAt = (n - 1) * stagger;
                for (int k = 0; k <= lastRequestAt + measured; k++)
                {
                    sampledFrames.Add((Time.frameCount, System.Diagnostics.Stopwatch.GetTimestamp()));
                    MarkStep(Time.frameCount, k);
                    for (int b = 0; b < n; b++) if (k == b * stagger) Request(round, states[b]);
                    if (FrameWindow && k <= lastRequestAt + _settings.cutPictureFrames) { if (FramePictures) TakePicture("cut-r" + round + "-k" + k); RecordPictureState(round, k, (FramePictures ? "" : "no-picture ") + "cut-r" + round + "-k" + k, states); }
                    if (FrameWindow && k == lastRequestAt + _settings.cutPictureFrames + 1) OpenMotionWindow(false);
                    // The listing's synchronous readbacks the frame AFTER the last picture: in the pictured frame they made the picture a copy of the frame before (plane1, 2026-11-02).
                    if (FrameWindow && k == lastRequestAt + _settings.cutPictureFrames + 1) ListSceneAround("round " + round + " k" + k, states);
                    Zantetsu.Core.Statistics.FrameTimeSample ft = sampler.Sample();
                    RecordFtmRaw(Time.frameCount);   // every result FrameTimingManager holds now, raw, with the frame it was read in (after the sampler's capture)
                    (string ftFrame, string ftOffset) = FtFrame(ft);
                    _record.CutFrame(round.ToString(Inv), Time.frameCount.ToString(Inv), k.ToString(Inv), (world.SkinBakePending != null ? world.SkinBakePending.Count : 0).ToString(Inv), world.Storage.DeferredPendingCount.ToString(Inv), world.Display.GpuRegionCount.ToString(Inv),
                        (Time.unscaledDeltaTime * 1000.0).ToString("F3", Inv), ft.HasMain ? ft.MainMs.ToString("F3", Inv) : "", ft.HasRender ? ft.RenderMs.ToString("F3", Inv) : "", ft.HasGpu ? ft.GpuMs.ToString("F3", Inv) : "", ft.HasPresentWait ? ft.PresentWaitMs.ToString("F3", Inv) : "", ft.HasGpuXr ? ft.GpuXrMs.ToString("F3", Inv) : "", ftFrame, ftOffset);
                    for (int b = 0; b < n; b++) Observe(round, states[b]);
                    _gpu.Update();
                    yield return null;
                }

                // EVAL-1 (TL 4): back to the same initial state before the next round -- outside the marks window.
                if (round + 1 < _settings.cutRounds) yield return ResetRound(round, states);
            }
            _activeStates = null;
            // EVAL-1 review 1 (P2-4): an event that reached a buffer after its round's last observation (the hooks keep running through the reset
            // and the later rounds) is resolved into the request's state before anything is written -- the rows are written here, after every round.
            int resolvedLate = 0;
            foreach (CutRequestState s in _allStates) resolvedLate += ResolveEventsFinal(s);
            _record.Fact("cut bench events final resolve", _allStates.Count + " requests re-resolved from the event buffers before the rows were written; events resolved after their round's last observation: " + resolvedLate + "; rounds invalid after a failed reset: " + _invalidRounds + (_resetFailure != null ? " (" + _resetFailure + ")" : ""));
            foreach (CutRequestState s in _allStates)
            {
                int round = s.round, b = s.body;
                {
                    string basisOutcome = "", readyMainMs = "", bakeWorkerMs = "";
                    if (s.gpuFirst)
                    {
                        basisOutcome = "pending (never collected)";
                        foreach (VpSkinBakeStats.Record r in VpSkinBakeStats.Records)
                        {
                            if (r.requestId != s.gpuRequestId) continue;
                            basisOutcome = r.basisOutcome ?? ""; readyMainMs = MsOrBlank(r.readyMainSeconds * 1000.0); bakeWorkerMs = MsOrBlank(r.workerSeconds * 1000.0);   // NaN (not collected) = blank (review 2)
                            break;
                        }
                    }
                    int rq = s.requestFrame;
                    _record.CutRequest(round.ToString(Inv), b.ToString(Inv), s.effectiveRoute ?? world.SkinBakeRoute, rq.ToString(Inv), s.outcome.ToString(), s.acceptance.ToString(), s.requestMs.ToString("F4", Inv),
                        s.acceptedFrame.ToString(Inv), s.physicsFrame.ToString(Inv), s.publishedFrame.ToString(Inv), s.clipFrame.ToString(Inv), s.readyFrame.ToString(Inv), s.committedFrame.ToString(Inv),
                        CutRequestState.F(s.acceptedFrame, rq), CutRequestState.F(s.physicsFrame, rq), CutRequestState.F(s.publishedFrame, rq), CutRequestState.F(s.clipFrame, rq), CutRequestState.F(s.readyFrame, rq), CutRequestState.F(s.committedFrame, rq),
                        s.cutKernelMs >= 0 ? s.cutKernelMs.ToString("F4", Inv) : "", readyMainMs, bakeWorkerMs, s.fallbackReason.Replace(',', ';'), basisOutcome.Replace(',', ';'), s.switchCompare,
                        _settings.cutPlane, SkinBakeProbeCutPlane.Plane(s.planeLocal), SkinBakeProbeCutPlane.Plane(s.planeWorld), SkinBakeProbeCutPlane.Point(s.anchor), s.BudgetWait, s.GeometryWait,
                        s.physicsObservedFrame.ToString(Inv), s.ledgerEventFrame.ToString(Inv), s.clipBeforeRenderFrame.ToString(Inv), s.readyEventFrame.ToString(Inv), s.ReadyOutcomeName, s.committedEventFrame.ToString(Inv),
                        CutRequestState.F(s.ledgerEventFrame, rq), CutRequestState.F(s.clipBeforeRenderFrame, rq), CutRequestState.F(s.readyEventFrame, rq), CutRequestState.F(s.committedEventFrame, rq), s.roundValid.Replace(',', ';'),
                        s.requestedRoute ?? world.SkinBakeRoute, MsOrBlank(s.bakeKernelMs), MsOrBlank(s.bakeAppendMs), MsOrBlank(s.showMs));
                    _record.Other("cut bench", "round " + round + " body " + b, "observation", SkinBakeProbeReactionEvents.CaseSucceeded(s.operation.IsSet, s.committedEventFrame), Time.frameCount, "outcome " + s.outcome + ", acceptance " + s.acceptance + ", request " + s.requestMs.ToString("F4", Inv) + " ms at frame " + rq
                        + ", accepted +" + CutRequestState.F(s.acceptedFrame, rq) + ", physics +" + CutRequestState.F(s.physicsFrame, rq) + ", ledger published +" + CutRequestState.F(s.publishedFrame, rq) + ", clip shown +" + CutRequestState.F(s.clipFrame, rq) + ", cpu ready +" + CutRequestState.F(s.readyFrame, rq) + ", committed +" + CutRequestState.F(s.committedFrame, rq) + " frames (-1 = not observed by the bench, never 'did not happen': an event between two observations; the verdict is the Commit EVENT below, not this observation)"
                        + (s.cutKernelMs >= 0 ? ", cut kernel " + s.cutKernelMs.ToString("F4", Inv) + " ms" : "") + (s.fallbackReason.Length > 0 ? "; fallback: " + s.fallbackReason : "") + (basisOutcome.Length > 0 ? "; basis: " + basisOutcome : "")
                        + (s.switchCompare.Length > 0 ? "; switch: " + s.switchCompare : "")
                        + "; events (hooks): physics +" + CutRequestState.F(s.physicsFrame, rq) + ", ledger +" + CutRequestState.F(s.ledgerEventFrame, rq) + ", ready +" + CutRequestState.F(s.readyEventFrame, rq) + " (" + s.ReadyOutcomeName + (s.readyEndedFrame >= 0 ? " +" + CutRequestState.F(s.readyEndedFrame, rq) : "") + "), commit +" + CutRequestState.F(s.committedEventFrame, rq)
                        + "; clip state at onBeforeRender +" + CutRequestState.F(s.clipBeforeRenderFrame, rq)
                        + "; budget wait: " + s.BudgetWait + "; geometry wait: " + s.GeometryWait + "; plane " + s.planeNote + "; round " + s.roundValid
                        + "; route asked " + (s.requestedRoute ?? world.SkinBakeRoute) + ", taken " + (s.effectiveRoute ?? "-") + (s.fallbackReason.Length > 0 ? " (fallback: " + s.fallbackReason + ")" : "")
                        + "; bake kernel " + (double.IsNaN(s.bakeKernelMs) ? "-" : s.bakeKernelMs.ToString("F4", Inv) + " ms") + ", append " + (double.IsNaN(s.bakeAppendMs) ? "-" : s.bakeAppendMs.ToString("F4", Inv) + " ms") + ", show " + (double.IsNaN(s.showMs) ? "-" : s.showMs.ToString("F4", Inv) + " ms") + " (- = not measured on this route)");
                }
            }
            _record.Fact("cut bench reaction events", "buffers: physics " + _events.Count(SkinBakeProbeReactionEvents.Kind.Physics) + ", ledger " + _events.Count(SkinBakeProbeReactionEvents.Kind.Ledger) + ", commit " + _events.Count(SkinBakeProbeReactionEvents.Kind.Commit) + ", collect " + _events.Count(SkinBakeProbeReactionEvents.Kind.Collect) + "; repeated reports not recorded twice: " + _events.Duplicates + "; not kept (buffer full): " + _events.NotKept + "; verdict by the Commit event (observed frames may stay -1); clip observations at onBeforeRender: " + _clipObservations);

            _record.Fact("skin bake stats", VpSkinBakeStats.Describe());
            _record.ExtraFile("skin-bake-stats.csv", VpSkinBakeStats.CsvLines());
            _record.Fact("skin bake resources", world.SkinBake != null ? world.SkinBake.Describe() : "none (the cpu route)");
            _record.Fact("display gpu regions", "now " + world.Display.GpuRegionCount + ", max " + world.Display.MaxGpuRegions + ", dispatches " + world.Display.GpuRegionDispatches + ", rebakes " + world.Display.GpuRegionRebakes + ", lost " + world.Display.GpuRegionsLost + "; room growths " + world.Display.RoomGrowths);
            _record.Fact("storage deferred", "reserved " + world.Storage.DeferredReserved + ", completed " + world.Storage.DeferredCompleted + ", abandoned " + world.Storage.DeferredAbandoned + ", pending " + world.Storage.DeferredPendingCount);
            _record.Fact("skin bake pending", world.SkinBakePending != null ? world.SkinBakePending.Describe() : "none (not the gpu route)");
            _record.Fact("dag pending bases", "registered " + world.Geometry.PendingBasesRegistered + ", completed " + world.Geometry.PendingBasesCompleted + ", failed " + world.Geometry.PendingBasesFailed + ", dropped " + world.Geometry.PendingBasesDropped + ", now " + world.Geometry.PendingBasisCount);
            _gpu.CountsEnabled = true;
            // MOTION-CAPTURE-1 step 6: what the motion pass used in each captured frame (its constants read back at the issue), and the view rows.
            WriteMarks();
            WriteCutEvents();
            // The window closed and its readbacks collected before the results are written (review 2: a window that never reached its
            // closing step, or a flush before the last readback arrived, would lose captures or keep capturing under keepOpen).
            OpenMotionWindow(false);
            // Both kinds of readback are waited for: the textures (Requested vs captured + failed + kept) and the issues' constants
            // (IssuesRequested vs IssuesReported). Time out or a failure fails the diagnostic run (review 3): never a Fact alone.
            int PendingTextures() => (int)(VpAppSwMotionCapture.Requested - _motionCaptures - _motionFailures - _deferredCaptures.Count);
            int PendingIssues() => VpAppSwMotionCapture.IssuesRequested - VpAppSwMotionCapture.IssuesReported;
            for (int f = 0; f < 12 && (PendingTextures() > 0 || PendingIssues() > 0); f++) yield return null;
            string captureFailure = null;
            if (_settings.motionCapture)
            {
                _record.Fact("motion capture window", "closed at the end of the bench; after the wait: texture readbacks pending " + PendingTextures() + " of " + VpAppSwMotionCapture.Requested + ", issue reports pending " + PendingIssues() + " of " + VpAppSwMotionCapture.IssuesRequested);
                if (PendingTextures() > 0 || PendingIssues() > 0) captureFailure = "motion capture incomplete: " + PendingTextures() + " texture readbacks and " + PendingIssues() + " issue reports still pending 12 frames after the window closed";
            }
            FlushMotionCaptures();
            if (_settings.motionCapture && captureFailure == null)
            {
                int constantFailures = 0, decodeFailures = 0;
                foreach (VpAppSwMotionCapture.Issue issue in _motionIssues.Values) { if (issue.issues > 0 && issue.constants == null) constantFailures++; else if (issue.issues > 0 && !issue.decoded) decodeFailures++; }
                if (_motionFailures > 0 || constantFailures > 0 || decodeFailures > 0) captureFailure = "motion capture failed: " + _motionFailures + " texture readbacks failed, " + constantFailures + " issues without their constants, " + decodeFailures + " issues not decoded" + (VpAppSwMotionCapture.LastRefusal != null ? "; last refusal: " + VpAppSwMotionCapture.LastRefusal : "");
            }
            ProvisionalCutDriver.PublishedForDiagnosis = null;
            if (_settings.motionCapture) _record.ExtraFile("motion-check-issues.json", MotionIssuesJson());
            if (_settings.motionView) { _motionCheckActive = false; _record.ExtraFile("motion-view-frames.csv", new[] { MotionViewHeader }.Concat(_motionViewFrames)); _record.Fact("motion view", "rounds started " + _motionViewRounds + "; view rows " + _motionViewFrames.Count + " (frame, k = index - hold, index, phase, target x offset, the eye at onBeforeRender)"); }
            if (_settings.motionCapture) _record.Fact("motion capture", "asked for the pictured frames; requested " + VpAppSwMotionCapture.Requested + " readbacks (in-pass eyes 0/1 and end-of-frame eyes 10/11; end-of-frame copies " + VpAppSwMotionCapture.EndOfFrameRequested + "), captured " + _motionCaptures + ", failed " + _motionFailures + (VpAppSwMotionCapture.LastRefusal != null ? "; last refusal: " + VpAppSwMotionCapture.LastRefusal : "") + (VpAppSwMotionCapture.Requested == 0 ? "; NONE REQUESTED: no motion pass ran for the camera (no AppSW motion target on this platform or route)" : ""));
            if (world.Display.NativeDrawSink is VpNativeDrawRoute route)
            {
                _record.Fact("native route", "recorded " + route.Recorded + "; bodies issued " + route.BodiesIssued + ", dropped " + route.BodiesDropped + "; motion taken " + route.MotionTaken + ", issued " + route.MotionIssued + "; skin motion taken " + route.SkinMotionTaken + ", issued " + route.SkinMotionIssued
                    + "; casters issued " + route.CastersIssued + "; direct clip motion taken " + route.DirectClipMotionTaken + ", kept " + route.DirectClipMotionKept + "; xr frames not drawn " + route.XrFramesNotDrawn + "; last failure " + (route.LastFailure ?? "none") + "; last body variant " + (route.LastBodyVariant ?? "?"));
            }
            if (FrameWindow && lastStates != null) ListSceneAround("end", lastStates);
            if (_settings.picture) yield return Hold(2, "cut-bench-end");
            if (captureFailure != null) { Fail(captureFailure); yield break; }
        }

        private CutRequestState[] lastStates;

        // SKIN-LINES-1 step 2: the cut-surface debug colours and the stencil cap gate, set once before the first frame.
        private void ApplyDiagnosisSettings()
        {
            if (_settings.cutSurfaceDebug) VpCutSurfaceColour.SetDebugEnabled(true);
            if (_settings.capsOff) VpStencilCapBatch.CapsOffForDiagnosis = true;
            VpSkinBakePending.HoldSubmitFramesForDiagnosis = _settings.holdCpuReady;
            _record.Fact("diagnosis", "gpu route CPU bake hold " + (_settings.holdCpuReady > 0 ? _settings.holdCpuReady + " frames after the hit (VpSkinBakePending.HoldSubmitFramesForDiagnosis)" : "none") + "; cut surface debug colours " + (VpCutSurfaceColour.DebugEnabled ? "ON (real caps " + VpCutSurfaceColour.Current + ", temporary faces " + VpCutSurfaceColour.CurrentProvisional + ")" : "off (cut surface colour " + VpCutSurfaceColour.Current + ")")
                + "; stencil cap pass " + (VpStencilCapBatch.CapsOffForDiagnosis ? "WITHHELD (VpStencilCapBatch.CapsOffForDiagnosis)" : "drawn"));
        }

        // SKIN-LINES-1 step 2: what stands around the cut bodies -- every enabled renderer near them, each body's own renderer
        // and root, the source owner and the provisional sides with their bodies' positions and velocities, and where the display
        // places the source whole, its two sides and its children -- so that a picture's extra body can be named.
        private void ListSceneAround(string when, CutRequestState[] states)
        {
            var t = new System.Text.StringBuilder();
            string V(Vector3 v) => "(" + v.x.ToString("F3", Inv) + ", " + v.y.ToString("F3", Inv) + ", " + v.z.ToString("F3", Inv) + ")";
            Vector3 centre = new Vector3(0f, 1f, 3f);
            int listed = 0;
            foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (Vector3.Distance(r.bounds.center, centre) > 4f) continue;
                listed++;
                t.Append(" | renderer '").Append(r.name).Append("' ").Append(r.GetType().Name).Append(" at ").Append(V(r.transform.position)).Append(" bounds ").Append(V(r.bounds.center)).Append(" size ").Append(V(r.bounds.size))
                    .Append(" shadows ").Append(r.shadowCastingMode).Append(" materials ");
                foreach (Material m in r.sharedMaterials) t.Append(m != null ? (m.shader != null ? m.shader.name : "no shader") : "null").Append(';');
            }

            t.Insert(0, "frame " + Time.frameCount + "; enabled renderers within 4 m of " + V(centre) + ": " + listed);
            for (int b = 0; b < states.Length; b++)
            {
                CutRequestState s = states[b];
                t.Append(" || body ").Append(b).Append(": npc renderer ").Append(s.npc != null && s.npc.Renderer != null ? (s.npc.Renderer.enabled ? "enabled" : "disabled") + " at " + V(s.npc.Renderer.transform.position) : "none")
                    .Append("; character root ").Append(s.npc != null && s.npc.CharacterRoot != null ? (s.npc.CharacterRoot.activeInHierarchy ? "active" : "inactive") + " at " + V(s.npc.CharacterRoot.transform.position) : "none");
                if (s.source.IsSet && world.Owners.TryGet(s.source, out PhysicsFragmentOwner owner))
                {
                    t.Append("; source owner root ").Append(owner.Root != null ? "'" + owner.Root.name + "' " + (owner.Root.activeInHierarchy ? "active" : "inactive") + " at " + V(owner.Root.transform.position) : "none")
                        .Append(" body ").Append(owner.Body != null ? V(owner.Body.position) + " v " + V(owner.Body.linearVelocity) : "none");
                }
                else t.Append("; source owner: none");
                if (s.operation.IsSet && world.Owners.TryGetProvisional(s.operation, out ProvisionalOwnerPair pair))
                {
                    t.Append("; provisional +side ").Append(pair.Positive != null && pair.Positive.Body != null ? V(pair.Positive.Body.position) + " v " + V(pair.Positive.Body.linearVelocity) : "none")
                        .Append(" -side ").Append(pair.Negative != null && pair.Negative.Body != null ? V(pair.Negative.Body.position) + " v " + V(pair.Negative.Body.linearVelocity) : "none");
                }
                else t.Append("; provisional pair: none");
                IVpFragmentPlacement placement = world.Display.Placement;
                if (placement != null)
                {
                    void P(string label, LogicalFragmentId f, CutOperationId op, float side)
                    {
                        if (!f.IsSet) { t.Append("; ").Append(label).Append(" unset"); return; }
                        VpFragmentPlacementKind kind = placement.TryGetGeometryLocalToWorld(f, op, side, out Matrix4x4 m);
                        t.Append("; ").Append(label).Append(' ').Append(kind).Append(' ').Append(V(m.GetColumn(3)));
                    }
                    P("placement source whole", s.source, default, 0f);
                    P("source +side", s.source, s.operation, 1f);
                    P("source -side", s.source, s.operation, -1f);
                    P("child positive", s.positive, default, 0f);
                    P("child negative", s.negative, default, 0f);
                }
                t.Append("; display state source ").Append(s.source.IsSet ? world.Display.StateOf(s.source).ToString() : "-").Append(" positive ").Append(s.positive.IsSet ? world.Display.StateOf(s.positive).ToString() : "-");
                void G(string label, LogicalFragmentId f)
                {
                    if (!f.IsSet) { t.Append("; dag ").Append(label).Append(" unset"); return; }
                    t.Append("; dag ").Append(label).Append(' ').Append(f.value).Append(' ').Append(world.Geometry.TryGetGeometry(f, out VpStoredGeometry g) ? "v" + g.vertexStart + "+" + g.vertexCount + " topology " + g.hasTopology + " stage " + world.Geometry.StageOf(s.operation) : "none");
                }
                G("source", s.source); G("positive", s.positive); G("negative", s.negative);
                if (s.gpuFirst) t.Append("; gpu-first geometry v").Append(s.gpuGeometry.vertexStart).Append('+').Append(s.gpuGeometry.vertexCount);
            }

            t.Append(" || display: ").Append(world.Display.DescribeShownForDiagnosis());
            _record.Fact("scene listing " + when, t.ToString());
        }

        // The bodies of one round: fresh NPC slots as the load NPCs are made, in a row 3 m ahead of the fixed eye, facing it, posed apart by the table.
        private IEnumerator MakeCutBodies(int count, int round, List<SandboxNpcCharacter> into)
        {
            var shares = new Dictionary<string, SandboxNpcCharacter.SlotShare>(StringComparer.Ordinal);
            if (!PoseTable.TryRead(assets.initialPose.bytes, out PoseTable clip, out string clipError)) { _record.Fact("cut bench bodies", "none: the pose table could not be read: " + clipError); yield break; }
            var holders = new List<GameObject>();
            var failures = new List<string>();
            for (int i = 0; i < count; i++)
            {
                GameObject templateHolder = null;
                for (int k = 0; k < templates.Length && templateHolder == null; k++)
                {
                    GameObject t = templates[(i + k) % templates.Length];
                    if (t != null && t.GetComponentInChildren<SandboxNpcCharacter>(true) != null) templateHolder = t;
                }

                if (templateHolder == null) break;
                GameObject holder = Instantiate(templateHolder);
                holder.name = "skin bake probe cut body r" + round + " b" + i;
                _made.Add(holder);
                SandboxNpcCharacter npc = holder.GetComponentInChildren<SandboxNpcCharacter>(true);
                PoseTablePlayer pose = npc.CharacterRoot.GetComponent<PoseTablePlayer>();
                if (pose == null) { failures.Add("body " + i + ": no PoseTablePlayer"); continue; }
                pose.Configure(assets.initialPose, pose.ModelRoot, 0.0, true);
                pose.Managed = true;
                double phase = ((i + 1) * 0.37 + round * 0.11) % 1.0 * 2.0;
                pose.PlanSource = (double target, out PoseTable table, out double source) => { table = clip; source = clip.ResolveSourceTime(phase); return true; };
                if (!shares.TryGetValue(npc.Family ?? "", out SandboxNpcCharacter.SlotShare share)) shares[npc.Family ?? ""] = share = new SandboxNpcCharacter.SlotShare();
                npc.PrepareAsSlot(share);
                npc.CharacterRoot.SetActive(true);
                holder.SetActive(true);
                npc.enabled = true;
                holders.Add(holder);
            }

            float by = Time.realtimeSinceStartup + 120f;
            int made = 0;
            foreach (GameObject holder in holders)
            {
                SandboxNpcCharacter npc = holder.GetComponentInChildren<SandboxNpcCharacter>(true);
                while (!npc.IsPrepared && npc.Failure == null && Time.realtimeSinceStartup < by) yield return null;
                if (!npc.IsPrepared) { failures.Add(npc.Family + ": " + (npc.Failure ?? "not prepared in time")); continue; }
                npc.CharacterRoot.transform.SetPositionAndRotation(new Vector3((made - (count - 1) * 0.5f) * 0.8f, 0f, 3f + round * 0.25f), Quaternion.Euler(0f, 180f, 0f));
                if (!npc.Activate()) { failures.Add(npc.Family + ": not activated: " + npc.Failure); continue; }
                npc.CharacterRoot.GetComponent<PoseTablePlayer>().ApplyNow(null, 0);
                into.Add(npc);
                made++;
            }

            yield return null;
            var ready = new System.Text.StringBuilder();
            foreach (SandboxNpcCharacter npc in into)
                ready.Append(npc.Family).Append(':').Append(npc.Handle != null && npc.Handle.IsReady ? "ready" : npc.Handle == null ? "NO HANDLE" : "not ready").Append(npc.SharedSkinInputRefusal != null ? " (shared input refused: " + npc.SharedSkinInputRefusal + ")" : "").Append(' ');
            _record.Fact("cut bench bodies" + (round > 0 ? " round " + round : ""), count + " asked; " + made + " NPC slots prepared, activated and drawn by their SkinnedMeshRenderers, posed apart, in a row 3 m ahead; handles: " + ready
                + (failures.Count > 0 ? "; FAILED " + failures.Count + ": " + string.Join(" | ", failures.Take(4)) : ""));
        }

        private void Request(int round, CutRequestState s)
        {
            s.requestFrame = Time.frameCount;
            if (s.handle == null || !s.handle.IsReady) { s.outcome = VpCharacterCutOutcome.Unavailable; s.requestMs = 0; return; }
            // The plane (PLANE-1): through the centre of the renderer's local bounds, in the renderer's frame -- legacy: the +y
            // plane of that frame (every earlier cut bench's; it splits this project's Z-up NPC meshes front/back); horizontal:
            // the world's horizontal plane through the centre's world point, converted by VpCutPlane.TryWorldToGeometryLocal.
            // A refusal is a failed request and a failed case: no other plane is cut with.
            SkinnedMeshRenderer renderer = s.npc.Renderer;
            if (renderer == null) { s.outcome = VpCharacterCutOutcome.Unavailable; s.requestMs = 0; s.planeNote = "no renderer"; return; }
            Matrix4x4 rendererToWorld = renderer.transform.localToWorldMatrix;
            Vector3 localCentre = renderer.localBounds.center;
            if (!SkinBakeProbeCutPlane.TryMake(_settings.cutPlane, rendererToWorld, localCentre, out float4 plane, out float4 worldPlane, out float3 anchor, out string planeFailure))
            {
                s.outcome = VpCharacterCutOutcome.Unavailable; s.requestMs = 0; s.planeFailed = true;
                s.planeNote = "PLANE REFUSED (" + _settings.cutPlane + "): " + planeFailure;
                _record.Other("cut plane", "round " + round + " body " + s.body, "verification", false, Time.frameCount, s.planeNote + "; local centre " + SkinBakeProbeCutPlane.Point((float3)localCentre) + "; renderer local->world " + SkinBakeProbeCutPlane.Matrix(rendererToWorld));
                return;
            }

            s.planeLocal = plane; s.planeWorld = worldPlane; s.anchor = anchor; s.rendererToWorld = rendererToWorld;
            bool sides = SkinBakeProbeCutPlane.CheckWorldUpSides(plane, rendererToWorld, anchor, out string sideDetail);
            s.planeNote = _settings.cutPlane + ": local " + SkinBakeProbeCutPlane.Plane(plane) + ", world " + SkinBakeProbeCutPlane.Plane(worldPlane) + ", anchor " + SkinBakeProbeCutPlane.Point(anchor) + ", local centre " + SkinBakeProbeCutPlane.Point((float3)localCentre)
                + "; world-up sides " + (sides ? "OK (the positive side is the world's up)" : "NOT up/down") + " [" + sideDetail + "]";
            // Horizontal must separate the world's up from its down (a verdict); legacy is noted with the same numbers (it is what it is).
            _record.Other("cut plane", "round " + round + " body " + s.body, "verification", _settings.cutPlane == SkinBakeProbeCutPlane.Horizontal ? sides : (bool?)null, Time.frameCount,
                s.planeNote + "; renderer local->world " + SkinBakeProbeCutPlane.Matrix(rendererToWorld));
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            VpCharacterCutResult result = s.handle.TryCut(plane, anchor);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            s.requestMs = Ms(t0, t1);
            s.requestBeganTick = t0; s.requestEndedTick = t1;
            MarkHit(Time.frameCount, t0, t1);
            s.outcome = result.Outcome; s.acceptance = result.Acceptance; s.source = result.Source; s.operation = result.Operation;
            // Read right after the call: the handle ends with its cut (as any handle), so what it knew is kept here.
            s.fallbackReason = s.handle.DeferredFallbackReason ?? "";
            s.requestedRoute = s.handle.LastRequestedRoute ?? world.SkinBakeRoute; s.effectiveRoute = s.handle.LastRoute;
            s.bakeKernelMs = s.handle.LastBakeKernelSeconds * 1000.0; s.bakeAppendMs = s.handle.LastBakeAppendSeconds * 1000.0; s.showMs = s.handle.LastShowSeconds * 1000.0;   // NaN = not measured (gpu / float routes): blank in the CSV
            s.gpuFirst = s.handle.GpuFirstRequestId != 0 && s.fallbackReason.Length == 0 && result.Outcome == VpCharacterCutOutcome.Requested;
            if (s.gpuFirst) { s.gpuGeometry = s.handle.GpuFirstGeometry; s.gpuRequestId = s.handle.GpuFirstRequestId; }
            if (result.Outcome == VpCharacterCutOutcome.Requested && result.Operation.IsSet) { s.acceptedFrame = Time.frameCount; s.acceptedTick = t1; }
            // The provisional physics: a Published acceptance put the pair into the scene in this call; its frame and time are the driver's hook's,
            // taken inside the hook (EVAL-1, TL 3: never TryCut's end), resolved from the event buffer by Observe below.
            if (!s.gpuFirst) s.readyFrame = s.acceptedFrame;   // the synchronous routes' geometry is in the storage at the hit
            Observe(round, s);
            if (!_settings.cutNoVerify && _settings.picture && !string.IsNullOrEmpty(_settings.directory) && !FramePictures) TakePicture("cut-r" + round + "-b" + s.body + "-request");
        }

        // Each frame (and once in the request's own frame): the provisional physics publication (the pair in the scene: the
        // transaction's PublishedFrame, a Pending acceptance's budget wait ends later), the ledger's publication (the operation
        // Published: the two children issued), the clip display (the source drawn split by the pending sides before the ledger
        // publishes, or either child below the parent's body after), the CPU ready (the GPU-first geometry no longer pending in
        // the storage; the switch's equality and picture then, verification runs only) and the geometry commit. Seen at the
        // bench's step of the frame: a display collection later in the frame is seen the frame after.
        private void Observe(int round, CutRequestState s)
        {
            if (s.requestFrame < 0 || !s.operation.IsSet) return;
            int frame = Time.frameCount;
            // EVAL-1: the events (the hooks' buffers, resolved by the operation or the bake request id) and the observations, apart.
            if (s.physicsFrame < 0) ResolveEvent(SkinBakeProbeReactionEvents.Kind.Physics, s.operation, ref s.physicsFrame, ref s.physicsTick);
            if (s.ledgerEventFrame < 0) ResolveEvent(SkinBakeProbeReactionEvents.Kind.Ledger, s.operation, ref s.ledgerEventFrame, ref s.ledgerEventTick);
            if (s.committedEventFrame < 0) ResolveEvent(SkinBakeProbeReactionEvents.Kind.Commit, s.operation, ref s.committedEventFrame, ref s.committedEventTick);
            if (s.gpuFirst && s.readyEventFrame < 0 && s.readyEndedFrame < 0) ResolveCollect(s);
            if (s.physicsObservedFrame < 0)
            {
                ProvisionalCutTransaction transaction = world.Driver.TransactionOf(s.operation);
                if (transaction != null && transaction.PublishedFrame > 0) { s.physicsObservedFrame = transaction.PublishedFrame; s.physicsObservedTick = System.Diagnostics.Stopwatch.GetTimestamp(); }
            }

            if (world.Ledger.TryGetOperation(s.operation, out LogicalCutOperation record))
            {
                if (!s.positive.IsSet && record.positive.IsSet) { s.positive = record.positive; s.negative = record.negative; }
                if (s.publishedFrame < 0 && (record.state == LogicalCutOperationState.Published || record.state == LogicalCutOperationState.Completed || record.state == LogicalCutOperationState.Terminated)) { s.publishedFrame = frame; s.publishedTick = System.Diagnostics.Stopwatch.GetTimestamp(); }
            }

            if (s.clipFrame < 0
                && (world.Display.StateOf(s.source) == LogicalCutDisplayState.ProvisionalSplit
                    || (s.positive.IsSet && (world.Display.StateOf(s.positive) == LogicalCutDisplayState.ProvisionalSplit || world.Display.StateOf(s.negative) == LogicalCutDisplayState.ProvisionalSplit))))
            {
                s.clipFrame = frame; s.clipTick = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            if (s.readyFrame < 0 && s.gpuFirst && !world.Storage.IsDeferredPending(s.gpuGeometry)) { s.readyFrame = frame; s.readyTick = System.Diagnostics.Stopwatch.GetTimestamp(); }
            if (s.readyFrame >= 0 && !s.compareAsked)
            {
                s.compareAsked = true;
                if (_settings.cutNoVerify) { s.switchCompare = "not verified (performance run)"; }
                else if (!s.gpuFirst) { s.switchCompare = "not applicable (synchronous route)"; }
                else
                {
                    if (_settings.picture && !string.IsNullOrEmpty(_settings.directory) && !s.pictureTaken && !FramePictures) { s.pictureTaken = true; TakePicture("cut-r" + round + "-b" + s.body + "-ready"); }
                    // The GPU region (what was drawn) against the storage's CPU geometry (what the cuts below take), read back for the verification only.
                    VpStoredGeometry geometry = s.gpuGeometry;
                    if (world.Storage.TryGetCommittedVertices(geometry.vertexStart, geometry.vertexCount, out NativeArray<VpRenderVertex> cpu))
                    {
                        VpRenderVertex[] reference = cpu.ToArray();
                        GraphicsBuffer vb = world.Display.GpuVertexBufferForDiagnosis;
                        if (vb != null && geometry.vertexStart + geometry.vertexCount <= vb.count && SystemInfo.supportsAsyncGPUReadback)
                        {
                            int body = s.body;
                            _gpu.Verify("cut r" + round + " b" + body + " ready", vb, geometry.vertexStart, geometry.vertexCount, 0, 0, reference, r => s.switchCompare = r.Describe().Replace(',', ';'));
                        }
                        else s.switchCompare = "no readback (no buffer or no async readback)";
                    }
                    else s.switchCompare = "no committed vertices in the storage for the geometry";
                }
            }

            if (s.committedFrame < 0 && world.Geometry.StageOf(s.operation) == CutGeometryStage.Committed)
            {
                s.committedFrame = frame; s.committedTick = System.Diagnostics.Stopwatch.GetTimestamp();
                double kernel = world.Geometry.KernelSecondsOf(s.operation);
                if (kernel >= 0) s.cutKernelMs = kernel * 1000.0;
            }
        }

        // PLANE-1: one row a body for a picture -- the events observed so far at the bench's step of the pictured frame (the
        // request's own acceptance and physics are known in the request frame; what a later part of the frame does is seen the
        // frame after), the display states, and where the physics stands: the provisional pair while it is provisional, the
        // children's owners once published, the source's owner while it is registered.
        // The placements are read in onBeforeRender of the pictured frame: the world steps physics in its LateUpdate, after the
        // bench's step, so what the frame renders is the post-step placement (plane2, 2026-11-02: the bench-step values lagged
        // the picture by one physics step).
        private (int round, int k, string picture, CutRequestState[] states)? _pendingPictureState;
        private bool _pictureStateHooked;
        // The body's shape (MOTION-CAPTURE-1 review P2-1, 2026-11-02; baked in the pre frame, the pose static until the hit): SkinnedMeshRenderer.BakeMesh in the renderer's local frame
        // (= the geometry's frame the plane is made in; useScale), written as body-r<round>-b<body>-mesh.bin: int32 vertex count, int32
        // index count, float3 positions, int32 indices. Unity's skinning of the same pose, independent of the probe's routes and of any
        // motion value: the evaluator projects it with the display's placements to know which texels the body must cover.
        private void DumpBodyMesh(int round, int body, SkinnedMeshRenderer renderer)
        {
            string file = "body-r" + round + "-b" + body + "-mesh.bin";
            Mesh mesh = null;
            try
            {
                mesh = new Mesh();
                renderer.BakeMesh(mesh, true);
                Vector3[] vertices = mesh.vertices; int[] indices = mesh.triangles;
                var bytes = new byte[8 + vertices.Length * 12 + indices.Length * 4];
                BitConverter.GetBytes(vertices.Length).CopyTo(bytes, 0); BitConverter.GetBytes(indices.Length).CopyTo(bytes, 4);
                int at = 8;
                for (int i = 0; i < vertices.Length; i++) { BitConverter.GetBytes(vertices[i].x).CopyTo(bytes, at); BitConverter.GetBytes(vertices[i].y).CopyTo(bytes, at + 4); BitConverter.GetBytes(vertices[i].z).CopyTo(bytes, at + 8); at += 12; }
                for (int i = 0; i < indices.Length; i++) { BitConverter.GetBytes(indices[i]).CopyTo(bytes, at); at += 4; }
                File.WriteAllBytes(Path.Combine(_settings.directory, file), bytes);
                _record.Fact("body mesh round " + round + " body " + body, file + ": " + vertices.Length + " vertices, " + (indices.Length / 3) + " triangles; BakeMesh(useScale) in the renderer's local frame at the pre frame " + Time.frameCount + " (the pose is static before the hit); local bounds centre " + SkinBakeProbeCutPlane.Point((float3)mesh.bounds.center) + " size " + SkinBakeProbeCutPlane.Point((float3)mesh.bounds.size));
            }
            catch (Exception e) { _record.Fact("body mesh round " + round + " body " + body, file + " NOT WRITTEN: " + e.Message); }
            finally { if (mesh != null) Destroy(mesh); }
        }

        // ---- SKIN-GPU-FIRST-STARTUP-1: per-frame marks of the cut bench, in memory, written after the bench. Every tick is Stopwatch's
        // (monotonic). The frame start is a PlayerLoop system inserted first in Initialization; the bench step is the coroutine's
        // step (after the Updates); hit begin/end bracket TryCut; LateUpdate is the probe's (after the world's, order -200 vs -60);
        // onBeforeRender and begin/endContextRendering are the render pipeline's main-thread points. The bake marks come from
        // VpSkinBakeMarks (shared-input, snapshot-allocate, palette-upload, dispatch: main-thread recording, not GPU time).
        /// <summary>The platform host's preparation before the bench (the Quest's QuestDrawPrewarm), or null.</summary>
        public static Func<IEnumerator> WarmBeforeBench;
        public static List<string> WarmReport;
        private static int lastRequestAtOf(int n, int stagger) => (n - 1) * stagger;
        private struct FrameMark { public int frame, k, round; public long init, step, hitBegan, hitEnded, late, beforeRender, beginRender, endRender; public long motionIssued; public int bakeMarks; public double bakeMs; }
        private struct BakeMark { public int frame; public string name; public long began, ended; }
        private struct FtmRaw { public int readFrame; public ulong frameStart, firstSubmit, presentCalled, frameComplete; public double main, render, gpu, presentWait; }
        private sealed class SkinBakeProbeFrameStartMark { }
        private FrameMark[] _marks; private int _marksFirst = -1, _marksCapacity;
        private readonly List<FrameMark[]> _marksRounds = new List<FrameMark[]>();
        private readonly List<int> _marksRoundFirst = new List<int>();
        private BakeMark[] _bakeMarks = new BakeMark[2048]; private int _bakeMarkCount;
        private FtmRaw[] _ftmRaw = new FtmRaw[4096]; private int _ftmCount; private ulong _ftmLastStart;
        private readonly FrameTiming[] _ftmBuffer = new FrameTiming[8];
        private bool _marksHooked;
        private static SkinBakeProbeRunner s_marksOwner;
        private readonly List<CutRequestState> _allStates = new List<CutRequestState>();

        private void BeginMarks(int round, int capacity)
        {
            _marks = new FrameMark[Mathf.Max(capacity, 16)]; _marksCapacity = _marks.Length; _marksFirst = Time.frameCount;
            for (int i = 0; i < _marks.Length; i++) { _marks[i].frame = _marksFirst + i; _marks[i].round = round; _marks[i].k = int.MinValue; }
            _marksRounds.Add(_marks); _marksRoundFirst.Add(_marksFirst);
            if (_marksHooked) return;
            _marksHooked = true; s_marksOwner = this;
            Application.onBeforeRender += MarkBeforeRender;
            RenderPipelineManager.beginContextRendering += MarkBeginRender;
            RenderPipelineManager.endContextRendering += MarkEndRender;
            VpSkinBakeMarks.Mark = OnBakeMark;
            try { HookFrameStart(true); } catch (Exception e) { _record.Fact("frame marks", "the PlayerLoop frame-start mark could not be installed: " + e.Message); }
        }

        private void EndMarks()
        {
            if (!_marksHooked) return;
            _marksHooked = false; s_marksOwner = null;
            Application.onBeforeRender -= MarkBeforeRender;
            RenderPipelineManager.beginContextRendering -= MarkBeginRender;
            RenderPipelineManager.endContextRendering -= MarkEndRender;
            VpSkinBakeMarks.Mark = null;
            try { HookFrameStart(false); } catch (Exception) { }
        }

        private static void HookFrameStart(bool install)
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(UnityEngine.PlayerLoop.Initialization)) continue;
                var list = new List<PlayerLoopSystem>(loop.subSystemList[i].subSystemList ?? Array.Empty<PlayerLoopSystem>());
                list.RemoveAll(x => x.type == typeof(SkinBakeProbeFrameStartMark));
                if (install) list.Insert(0, new PlayerLoopSystem { type = typeof(SkinBakeProbeFrameStartMark), updateDelegate = MarkFrameStart });
                loop.subSystemList[i].subSystemList = list.ToArray();
            }
            PlayerLoop.SetPlayerLoop(loop);
        }

        private static void MarkFrameStart() { SkinBakeProbeRunner owner = s_marksOwner; if (owner != null) owner.Set(Time.frameCount, 0, System.Diagnostics.Stopwatch.GetTimestamp()); }
        private int Slot(int frame) { int i = frame - _marksFirst; return _marks != null && i >= 0 && i < _marksCapacity ? i : -1; }
        private void Set(int frame, int which, long tick)
        {
            int i = Slot(frame); if (i < 0) return;
            switch (which)
            {
                case 0: if (_marks[i].init == 0) _marks[i].init = tick; break;
                case 1: _marks[i].step = tick; break;
                case 2: _marks[i].late = tick; break;
                case 3: _marks[i].beforeRender = tick; break;
                case 4: if (_marks[i].beginRender == 0) _marks[i].beginRender = tick; break;
                case 5: _marks[i].endRender = tick; break;
            }
        }
        private void MarkStep(int frame, int k) { int i = Slot(frame); if (i < 0) return; _marks[i].step = System.Diagnostics.Stopwatch.GetTimestamp(); _marks[i].k = k; }
        private void MarkHit(int frame, long began, long ended) { int i = Slot(frame); if (i < 0) return; if (_marks[i].hitBegan == 0) _marks[i].hitBegan = began; _marks[i].hitEnded = ended; }
        private void MarkLate() { Set(Time.frameCount, 2, System.Diagnostics.Stopwatch.GetTimestamp()); }
        private void MarkBeforeRender()
        {
            Set(Time.frameCount, 3, System.Diagnostics.Stopwatch.GetTimestamp());
            int i = Slot(Time.frameCount); if (i >= 0 && world != null && world.Display != null && !world.Display.IsDisposed && world.Display.NativeDrawSink is VpNativeDrawRoute route) _marks[i].motionIssued = route.MotionIssued;
        }
        private void MarkBeginRender(ScriptableRenderContext context, List<Camera> cameras) { Set(Time.frameCount, 4, System.Diagnostics.Stopwatch.GetTimestamp()); }
        private void MarkEndRender(ScriptableRenderContext context, List<Camera> cameras) { Set(Time.frameCount, 5, System.Diagnostics.Stopwatch.GetTimestamp()); }
        private void OnBakeMark(string name, long began, long ended)
        {
            int frame = Time.frameCount;
            if (_bakeMarkCount < _bakeMarks.Length) _bakeMarks[_bakeMarkCount++] = new BakeMark { frame = frame, name = name, began = began, ended = ended };
            int i = Slot(frame); if (i < 0) return;
            _marks[i].bakeMarks++; _marks[i].bakeMs += (ended - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private void RecordFtmRaw(int readFrame)
        {
            int got = (int)FrameTimingManager.GetLatestTimings((uint)_ftmBuffer.Length, _ftmBuffer);
            // the newest is index 0; kept oldest first, each frame start once
            for (int i = got - 1; i >= 0; i--)
            {
                FrameTiming t = _ftmBuffer[i];
                if (t.frameStartTimestamp == 0 || t.frameStartTimestamp <= _ftmLastStart) continue;
                _ftmLastStart = t.frameStartTimestamp;
                if (_ftmCount < _ftmRaw.Length) _ftmRaw[_ftmCount++] = new FtmRaw { readFrame = readFrame, frameStart = t.frameStartTimestamp, firstSubmit = t.firstSubmitTimestamp, presentCalled = t.cpuTimePresentCalled, frameComplete = t.cpuTimeFrameComplete, main = t.cpuMainThreadFrameTime, render = t.cpuRenderThreadFrameTime, gpu = t.gpuFrameTime, presentWait = t.cpuMainThreadPresentWaitTime };
            }
        }

        private void WriteMarks()
        {
            EndMarks();
            double f = System.Diagnostics.Stopwatch.Frequency;
            string Ms0(long tick, long origin) => tick == 0 ? "" : ((tick - origin) * 1000.0 / f).ToString("F3", Inv);
            var lines = new List<string> { "round,frame,k,initTick,initMs,dtInitMs,stepMs,hitBeginMs,hitEndMs,hitMs,lateMs,beforeRenderMs,beginRenderMs,endRenderMs,bakeMarks,bakeMs,motionIssuedTotal" };
            var allInit = new List<(long tick, int frame)>();
            for (int r = 0; r < _marksRounds.Count; r++)
            {
                FrameMark[] marks = _marksRounds[r];
                long origin = 0; long previous = 0;
                for (int i = 0; i < marks.Length; i++)
                {
                    FrameMark m = marks[i];
                    if (m.init == 0 && m.step == 0) continue;
                    if (origin == 0) origin = m.init != 0 ? m.init : m.step;
                    if (m.init != 0) allInit.Add((m.init, m.frame));
                    string dt = m.init != 0 && previous != 0 ? ((m.init - previous) * 1000.0 / f).ToString("F3", Inv) : "";
                    if (m.init != 0) previous = m.init;
                    lines.Add(m.round + "," + m.frame + "," + (m.k == int.MinValue ? "" : m.k.ToString(Inv)) + "," + m.init.ToString(Inv) + "," + Ms0(m.init, origin) + "," + dt + "," + Ms0(m.step, origin) + "," + Ms0(m.hitBegan, origin) + "," + Ms0(m.hitEnded, origin) + ","
                        + (m.hitBegan != 0 ? ((m.hitEnded - m.hitBegan) * 1000.0 / f).ToString("F3", Inv) : "") + "," + Ms0(m.late, origin) + "," + Ms0(m.beforeRender, origin) + "," + Ms0(m.beginRender, origin) + "," + Ms0(m.endRender, origin) + "," + m.bakeMarks + "," + m.bakeMs.ToString("F3", Inv) + "," + m.motionIssued);
                }
            }
            _record.ExtraFile("skin-bake-frame-marks.csv", lines);
            var bake = new List<string> { "frame,name,beganTick,ms" };
            for (int i = 0; i < _bakeMarkCount; i++) bake.Add(_bakeMarks[i].frame + "," + _bakeMarks[i].name + "," + _bakeMarks[i].began.ToString(Inv) + "," + ((_bakeMarks[i].ended - _bakeMarks[i].began) * 1000.0 / f).ToString("F3", Inv));
            _record.ExtraFile("skin-bake-bake-marks.csv", bake);
            // FrameTimingManager raw, with the clock-base check: the frame start converted to Stopwatch ticks by the CPU timer frequency, the nearest frame-start mark and the offset.
            ulong cpuFrequency = FrameTimingManager.GetCpuTimerFrequency(), gpuFrequency = FrameTimingManager.GetGpuTimerFrequency();
            allInit.Sort((a, b) => a.tick.CompareTo(b.tick));
            var ftm = new List<string> { "readFrame,frameStartTimestamp,firstSubmitTimestamp,cpuTimePresentCalled,cpuTimeFrameComplete,cpuMainThreadFrameTimeMs,cpuRenderThreadFrameTimeMs,gpuFrameTimeMs,cpuMainThreadPresentWaitTimeMs,frameStartAsStopwatchTick,nearestInitFrame,offsetToNearestInitMs" };
            for (int i = 0; i < _ftmCount; i++)
            {
                FtmRaw t = _ftmRaw[i];
                string nearest = "", offset = "", asTick = "";
                if (cpuFrequency > 0 && allInit.Count > 0)
                {
                    long tick = (long)((double)t.frameStart * f / cpuFrequency); asTick = tick.ToString(Inv);
                    int best = -1; long bestDistance = long.MaxValue;
                    for (int j = 0; j < allInit.Count; j++) { long d = Math.Abs(allInit[j].tick - tick); if (d < bestDistance) { bestDistance = d; best = j; } }
                    if (best >= 0) { nearest = allInit[best].frame.ToString(Inv); offset = ((tick - allInit[best].tick) * 1000.0 / f).ToString("F3", Inv); }
                }
                ftm.Add(t.readFrame + "," + t.frameStart + "," + t.firstSubmit + "," + t.presentCalled + "," + t.frameComplete + "," + t.main.ToString("F3", Inv) + "," + t.render.ToString("F3", Inv) + "," + t.gpu.ToString("F3", Inv) + "," + t.presentWait.ToString("F3", Inv) + "," + asTick + "," + nearest + "," + offset);
            }
            _record.ExtraFile("skin-bake-ftm-raw.csv", ftm);
            _record.Fact("frame marks", "rounds " + _marksRounds.Count + ", frame rows " + (lines.Count - 1) + ", bake marks " + _bakeMarkCount + ", FrameTimingManager results " + _ftmCount + " (enabled " + FrameTimingManager.IsFeatureEnabled() + "); Stopwatch frequency " + System.Diagnostics.Stopwatch.Frequency + " Hz, FTM cpu timer " + cpuFrequency + " Hz, gpu timer " + gpuFrequency + " Hz; the clock-base check is the offset of each FTM frame start (as Stopwatch ticks by the cpu frequency) to the nearest frame-start mark: small and steady = the same clock, otherwise the attribution stays undetermined; the frame-start mark is a PlayerLoop Initialization system, the step the bench coroutine, hit = TryCut, late = the probe's LateUpdate, before/begin/end render = onBeforeRender and begin/endContextRendering; bake marks = VpSkinBakeMarks (main-thread recording, not GPU time); motionIssuedTotal = the native route's counter at onBeforeRender");
        }

        private void WriteCutEvents()
        {
            double f = System.Diagnostics.Stopwatch.Frequency;
            // EVAL-1: an EVENT row is the product's hook (frame and tick taken inside it); an *-observed row is the bench's first observation of a
            // state (at its step, or at onBeforeRender for the clip). -1 / an empty tick = not reported / not observed, never "did not happen".
            var lines = new List<string> { "round,body,event,frame,tick,msAfterRequestBegin,note" };
            foreach (CutRequestState s in _allStates)
            {
                void E(string name, int frame, long tick, string note) { lines.Add(s.round + "," + s.body + "," + name + "," + frame + "," + (tick == 0 ? "" : tick.ToString(Inv)) + "," + (tick == 0 || s.requestBeganTick == 0 ? "" : ((tick - s.requestBeganTick) * 1000.0 / f).ToString("F3", Inv)) + "," + note); }
                E("request-begin", s.requestFrame, s.requestBeganTick, "TryCut called"); E("request-end", s.requestFrame, s.requestEndedTick, "TryCut returned");
                E("accepted", s.acceptedFrame, s.acceptedTick, s.acceptedFrame >= 0 ? "acceptance " + s.acceptance : "");
                E("physics-published", s.physicsFrame, s.physicsTick, "event: the driver's hook (the pair in the scene)"); E("physics-published-observed", s.physicsObservedFrame, s.physicsObservedTick, "observed: the transaction's PublishedFrame at the step");
                E("ledger-published", s.ledgerEventFrame, s.ledgerEventTick, "event: the ledger's hook (the children issued)"); E("ledger-published-observed", s.publishedFrame, s.publishedTick, "observed at the step");
                E("clip-observed-step", s.clipFrame, s.clipTick, "observed: display state ProvisionalSplit at the step"); E("clip-observed-beforeRender", s.clipBeforeRenderFrame, s.clipBeforeRenderTick, "observed: display state ProvisionalSplit at onBeforeRender (not a pixel)");
                E("cpu-ready", s.readyEventFrame, s.readyEventTick, s.gpuFirst ? "event: the collect completed (the basis published)" : "synchronous route: no deferred bake"); E("cpu-ready-ended", s.readyEndedFrame, s.readyEndedTick, s.readyOutcome >= 0 ? "event: the collect ended as " + ((VpSkinBakeCollectOutcome)s.readyOutcome) : "");
                E("cpu-ready-observed", s.readyFrame, s.readyTick, s.gpuFirst ? "observed: the storage no longer pending at the step" : "synchronous route: the request frame");
                E("committed", s.committedEventFrame, s.committedEventTick, "event: the DAG's hook"); E("committed-observed", s.committedFrame, s.committedTick, "observed: geometry stage Committed at the step");
            }
            _record.ExtraFile("skin-bake-cut-events.csv", lines);
        }

        // SKIN-GPU-FIRST-STARTUP-1 (TL part 4/5): the bake's resources warmed without a cut. skin-prepare: the shared input prepared, a
        // snapshot rented (its allocation), the real pose's palette captured and uploaded, the snapshot given back. skin-dispatch: the
        // same, then the product's VpSkinGpuBake.Dispatch (shader, kernel, keywords) into a PRIVATE 16-byte output; completion is an
        // async readback of that output (the GPU ran the dispatch) -- never Load/FindKernel or a frame count; the output and the snapshot
        // are held until then. Nothing of the shown vertex buffer, the Ledger or the physics is touched; no cut is made.
        private IEnumerator WarmSkin(CutRequestState[] states, bool dispatch)
        {
            string kind = dispatch ? "skin-dispatch" : "skin-prepare";
            VpSkinBakeResources resources = world != null ? world.SkinBake : null;
            if (resources == null) { _record.Fact("warm before the bench (skin)", kind + ": no skin bake resources (route " + (world != null ? world.SkinBakeRoute : "?") + "): NOT warmed"); yield break; }
            if (dispatch && resources.Bake == null) { _record.Fact("warm before the bench (skin)", kind + ": the route has no GPU bake (" + world.SkinBakeRoute + "): NOT warmed"); yield break; }
            VpSkinModel model = states.Length > 0 && states[0].handle != null ? states[0].handle.SkinModelForDiagnosis : null;
            SkinnedMeshRenderer renderer = states.Length > 0 && states[0].npc != null ? states[0].npc.Renderer : null;
            if (model == null || renderer == null) { _record.Fact("warm before the bench (skin)", kind + ": body 0 has no skin model or renderer: NOT warmed"); yield break; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); int f0 = Time.frameCount;
            string before = resources.Describe();
            if (dispatch)
            {
                // The product's own warm (VpSkinBakeWarm: identity palette, private output, readback completion), as play's preparation runs it.
                var productWarm = new VpSkinBakeWarm(resources, model);
                yield return productWarm.Run();
                _record.Fact("warm before the bench (skin)", kind + " (VpSkinBakeWarm): " + (productWarm.Completed ? "completed" : "FAILED: " + productWarm.Failure) + "; frames " + productWarm.BeganFrame + ".." + productWarm.FinishedFrame + "; " + string.Join(" | ", productWarm.Report)
                    + "; resources before: " + before + "; after: " + resources.Describe() + "; no cut made, the display's buffer, the Ledger and the physics untouched");
                yield break;
            }
            if (!model.TryPrepareSharedInput(out string refusal)) { _record.Fact("warm before the bench (skin)", kind + ": shared input refused: " + refusal + ": NOT warmed"); yield break; }
            long tInput = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!resources.TryRent(model, model.SharedInput, out VpSkinBakeSnapshot snapshot, out refusal)) { _record.Fact("warm before the bench (skin)", kind + ": snapshot refused: " + refusal + ": NOT warmed"); yield break; }
            long tRent = System.Diagnostics.Stopwatch.GetTimestamp();
            GraphicsBuffer output = null; bool done = false, error = false; int waited = 0; long tCapture = 0, tDispatch = 0;
            try
            {
                if (!snapshot.TryCapture(renderer.transform.worldToLocalMatrix, renderer.bones)) { _record.Fact("warm before the bench (skin)", kind + ": the pose could not be captured: NOT warmed"); yield break; }
                snapshot.UploadPalette();
                tCapture = System.Diagnostics.Stopwatch.GetTimestamp();
                if (dispatch)
                {
                    output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, model.VertexCount, 16) { name = "Skin bake warm output" };
                    resources.Bake.Dispatch(model.SharedInput, snapshot.PaletteBuffer, snapshot.BoneCount, output, 0);
                    tDispatch = System.Diagnostics.Stopwatch.GetTimestamp();
                    AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(output, 16, 0);
                    while (!request.done && waited < 600) { waited++; yield return null; }
                    done = request.done; error = request.hasError;
                }
                else { yield return null; done = true; }
            }
            finally
            {
                // The snapshot goes back (free from the next frame); the output is freed only when the GPU is known to be done with it.
                resources.Return(snapshot);
                if (output != null && done) output.Dispose();
            }
            double ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _record.Fact("warm before the bench (skin)", kind + ": " + (done && !error ? "completed" : error ? "the readback reported an error" : "NOT completed within 600 frames (the output is kept, not freed)")
                + "; frames " + f0 + ".." + Time.frameCount + " (" + (Time.frameCount - f0) + ")" + (dispatch ? ", readback waited " + waited + " frames" : "")
                + "; main-thread ms: shared input " + ms(t0, tInput).ToString("F3", Inv) + ", rent " + ms(tInput, tRent).ToString("F3", Inv) + ", capture+upload " + (tCapture > 0 ? ms(tRent, tCapture).ToString("F3", Inv) : "-") + (dispatch ? ", dispatch " + (tDispatch > 0 ? ms(tCapture, tDispatch).ToString("F3", Inv) : "-") : "")
                + "; model " + model.VertexCount + " vertices, " + model.BoneCount + " bones" + (dispatch ? "; private output " + ((long)model.VertexCount * 16) + " bytes" : "")
                + "; resources before: " + before + "; after: " + resources.Describe() + "; no cut made, the display's buffer, the Ledger and the physics untouched");
        }

        private void RecordPictureState(int round, int k, string picture, CutRequestState[] states)
        {
            if (states == null) return;
            if (!_pictureStateHooked) { Application.onBeforeRender += WritePendingPictureState; _pictureStateHooked = true; }
            _pendingPictureState = (round, k, picture, states);
        }

        private void WritePendingPictureState()
        {
            if (_pendingPictureState == null) return;
            (int round, int k, string picture, CutRequestState[] states) = _pendingPictureState.Value;
            _pendingPictureState = null;
            WritePictureState(round, k, picture, states);
        }

        private void WritePictureState(int round, int k, string picture, CutRequestState[] states)
        {
            if (states == null || world == null || world.Owners == null || world.Display == null || world.Display.IsDisposed) return;
            string P(Rigidbody b) => b == null ? "" : SkinBakeProbeCutPlane.Point((float3)b.position);
            string R(Rigidbody b) => b == null ? "" : SkinBakeProbeCutPlane.Point((float3)b.rotation.eulerAngles);
            // The display's own answer of where a shape stands (geometry local -> world), as Kind(16 row-major numbers); "" when unasked.
            IVpFragmentPlacement placementLookup = world.Display.Placement;
            string M(string kind, Matrix4x4 m)
            {
                var t = new System.Text.StringBuilder(kind).Append('(');
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) { if (r > 0 || c > 0) t.Append(", "); t.Append(m[r, c].ToString("R", Inv)); }
                return t.Append(')').ToString();
            }
            string Placed(LogicalFragmentId f, CutOperationId op, float side)
            {
                if (!f.IsSet || placementLookup == null) return "";
                VpFragmentPlacementKind kind = placementLookup.TryGetGeometryLocalToWorld(f, op, side, out Matrix4x4 m);
                return M(kind.ToString(), m);
            }
            foreach (CutRequestState s in states)
            {
                Rigidbody sourceBody = null, positiveBody = null, negativeBody = null;
                if (s.source.IsSet && world.Owners.TryGet(s.source, out PhysicsFragmentOwner sourceOwner)) sourceBody = sourceOwner.Body;
                if (s.operation.IsSet && world.Owners.TryGetProvisional(s.operation, out ProvisionalOwnerPair pair))
                {
                    positiveBody = pair.Positive != null ? pair.Positive.Body : null;
                    negativeBody = pair.Negative != null ? pair.Negative.Body : null;
                }
                // The children straight from the ledger: the bench's own view of them (Observe) is a step behind the picture.
                LogicalFragmentId positive = s.positive, negative = s.negative;
                if ((!positive.IsSet || !negative.IsSet) && s.operation.IsSet && world.Ledger.TryGetOperation(s.operation, out LogicalCutOperation record) && record.positive.IsSet) { positive = record.positive; negative = record.negative; }
                if (positive.IsSet && world.Owners.TryGet(positive, out PhysicsFragmentOwner positiveOwner) && positiveOwner.Body != null) positiveBody = positiveOwner.Body;
                if (negative.IsSet && world.Owners.TryGet(negative, out PhysicsFragmentOwner negativeOwner) && negativeOwner.Body != null) negativeBody = negativeOwner.Body;
                // The clip shown is seen here at onBeforeRender too (review 2): the bench's observation at its step precedes the frame's display collection.
                if (s.clipFrame < 0 && s.operation.IsSet && s.source.IsSet && (world.Display.StateOf(s.source) == LogicalCutDisplayState.ProvisionalSplit
                    || (positive.IsSet && (world.Display.StateOf(positive) == LogicalCutDisplayState.ProvisionalSplit || world.Display.StateOf(negative) == LogicalCutDisplayState.ProvisionalSplit)))) { s.clipFrame = Time.frameCount; s.clipTick = System.Diagnostics.Stopwatch.GetTimestamp(); }
                string note = (s.requestFrame < 0 ? "before the request" : (s.planeFailed ? "plane refused" : "")) + "; placements at onBeforeRender";
                // Before the request the uncut renderer draws the body: its localToWorld is the geometry's frame (the plane is made in it).
                string placementSource = s.source.IsSet ? Placed(s.source, default, 0f) : (s.npc != null && s.npc.Renderer != null ? M("Renderer", s.npc.Renderer.transform.localToWorldMatrix) : "");
                string placementPositiveSide = s.operation.IsSet ? Placed(s.source, s.operation, 1f) : "", placementNegativeSide = s.operation.IsSet ? Placed(s.source, s.operation, -1f) : "";
                _record.CutPicture(round.ToString(Inv), k.ToString(Inv), Time.frameCount.ToString(Inv), picture, s.body.ToString(Inv), s.requestFrame.ToString(Inv),
                    s.requestFrame < 0 ? "" : s.acceptance.ToString(), s.acceptedFrame.ToString(Inv), s.physicsFrame.ToString(Inv), s.publishedFrame.ToString(Inv), s.readyFrame.ToString(Inv), s.committedFrame.ToString(Inv),
                    s.source.IsSet ? world.Display.StateOf(s.source).ToString() : "", positive.IsSet ? world.Display.StateOf(positive).ToString() : "", negative.IsSet ? world.Display.StateOf(negative).ToString() : "",
                    P(sourceBody), R(sourceBody), P(positiveBody), R(positiveBody), P(negativeBody), R(negativeBody), note,
                    eye != null ? SkinBakeProbeCutPlane.Point((float3)eye.transform.position) : "", eye != null ? SkinBakeProbeCutPlane.Point((float3)eye.transform.rotation.eulerAngles) : "",
                    placementSource, placementPositiveSide, placementNegativeSide, Placed(positive, default, 0f), Placed(negative, default, 0f));
            }
        }

        // PLANE-1: the AppSW motion output of the pictured frames (the native route's motion pass copies the XR motion target
        // and reads it back; nothing where there is no motion pass). The window is open from the pre picture to the last
        // pictured frame; every capture is written beside the pictures as raw halves and a magnitude picture, with a row of
        // statistics. The decode assumes two 16-bit floats a pixel (R16G16_SFloat), the format the Quest's AppSW target has;
        // another format is written raw and noted.
        private bool _motionWindow;
        private int _motionCaptures, _motionFailures;
        // The cut bench's captures are kept as they arrive and processed after the window (statistics, files): their Main work in the
        // window's frames (~140 ms a frame with 4 copies) spent the provisional physics' shared Main budget (mv2 runs, 2026-11-02:
        // acceptance Pending, physics +28). The capture-method check (quads, no cut) processes at once as before.
        private readonly List<VpAppSwMotionCapture.Result> _deferredCaptures = new List<VpAppSwMotionCapture.Result>();
        private bool _deferCaptures;
        // SKIN-GPU-FIRST-EVAL-1 (TL 3): the product's diagnosis hooks, recorded into preallocated buffers with the frame and the Stopwatch
        // tick taken inside the hook, resolved to the requests by operation id (physics, ledger, commit) or bake request id (collect) at the
        // bench's step and right after the request. Each event is kept once (a repeated report is counted); nothing allocates per event;
        // the files are written after the window. -1 in the event columns means "not reported" / "not observed", never "did not happen".
        private readonly SkinBakeProbeReactionEvents _events = new SkinBakeProbeReactionEvents(4096);   // review 2: the buffers and the verdict, testable apart
        private int _clipObservations;
        private CutRequestState[] _activeStates;
        private bool _clipHooked;
        // EVAL-1 review 1 (P2-3): the initial state (before round 0) and the first reset that did not return to it (null = none so far).
        private int _baselineLiveFragments, _baselineOwners, _invalidRounds;
        private string _resetFailure;

        private void HookReactionRecord(bool on)
        {
            if (on)
            {
                _events.Reset(); _clipObservations = 0;
                ProvisionalCutDriver.PublishedForDiagnosis = (operation, frame) => _events.Add(SkinBakeProbeReactionEvents.Kind.Physics, operation, 0, frame, 0);
                LogicalCutLedger.PublishedForDiagnosis = operation => _events.Add(SkinBakeProbeReactionEvents.Kind.Ledger, operation, 0, Time.frameCount, 0);
                CutDag.CommittedForDiagnosis = operation => _events.Add(SkinBakeProbeReactionEvents.Kind.Commit, operation, 0, Time.frameCount, 0);
                VpSkinBakePending.CollectedForDiagnosis = (requestId, outcome) => _events.Add(SkinBakeProbeReactionEvents.Kind.Collect, default, requestId, Time.frameCount, (int)outcome);
                if (!_clipHooked) { Application.onBeforeRender += ObserveClipBeforeRender; _clipHooked = true; }
            }
            else
            {
                ProvisionalCutDriver.PublishedForDiagnosis = null; LogicalCutLedger.PublishedForDiagnosis = null; CutDag.CommittedForDiagnosis = null; VpSkinBakePending.CollectedForDiagnosis = null;
                if (_clipHooked) { Application.onBeforeRender -= ObserveClipBeforeRender; _clipHooked = false; }
                _activeStates = null;
            }
        }

        private void ResolveEvent(SkinBakeProbeReactionEvents.Kind kind, CutOperationId operation, ref int frame, ref long tick)
        {
            if (_events.TryResolve(kind, operation, out int f, out long t)) { frame = f; tick = t; }
        }

        private void ResolveCollect(CutRequestState s)
        {
            if (!_events.TryResolveCollect(s.gpuRequestId, out int frame, out long tick, out int code)) return;
            if (code == (int)VpSkinBakeCollectOutcome.Completed) { s.readyEventFrame = frame; s.readyEventTick = tick; }
            else { s.readyEndedFrame = frame; s.readyEndedTick = tick; s.readyOutcome = code; }
        }

        // CPU-SKIN-FLOAT-1 review 1 (P2): a time the route did not measure (NaN) is a blank field, never a zero.
        private static string MsOrBlank(double ms) => double.IsNaN(ms) ? "" : ms.ToString("F4", Inv);

        // CPU-SKIN-FLOAT-1 (TL 5): the skin inputs the bench's bodies hold, per distinct model -- what the product candidate needs (the model's
        // 24-byte input, which the skin draw and the 24-byte bake read; the CPU-only float input beside it, with its preparation time; the GPU
        // shared input of the gpu/float routes) -- and how many bodies share each. The probe's reference allocations are the probe cases'
        // (not made by the cut bench). Facts "cut bench skin inputs" and one "cut bench skin model <k>" a model.
        private void RecordSkinInputs(CutRequestState[] states)
        {
            var models = new List<VpSkinModel>(); var bodiesOf = new List<int>();
            foreach (CutRequestState s in states)
            {
                VpSkinModel m = s.handle != null ? s.handle.SkinModelForWarm : null;
                if (m == null) continue;
                int at = models.IndexOf(m);
                if (at < 0) { models.Add(m); bodiesOf.Add(1); } else bodiesOf[at]++;
            }

            long packedTotal = 0, floatTotal = 0, sharedTotal = 0;
            for (int k = 0; k < models.Count; k++)
            {
                VpSkinModel m = models[k];
                long shared = m.SharedInput != null ? m.SharedInput.NativeBytes : 0;
                packedTotal += m.NativeBytes; floatTotal += m.FloatInputBytes; sharedTotal += shared;
                _record.Fact("cut bench skin model " + k, "bodies " + bodiesOf[k] + "; vertices " + m.VertexCount + ", indices " + m.IndexCount + ", bones " + m.BoneCount + " (used " + m.UsedBoneCount + ")"
                    + "; 24-byte model (vertices, indices, topology, bone boxes) " + m.NativeBytes + " bytes"
                    + "; cpu-float input " + (m.FloatInput != null ? m.FloatInputBytes + " bytes (" + VpSkinFloatVertex.Stride + " a vertex), prepared in " + (m.FloatInput.PreparationSeconds * 1000.0).ToString("F3", Inv) + " ms" : "none" + (m.FloatInputRefusal != null ? " (refused: " + m.FloatInputRefusal + ")" : ""))
                    + "; gpu shared input " + (m.SharedInput != null ? shared + " bytes" : "none" + (m.SharedInputRefusal != null ? " (refused: " + m.SharedInputRefusal + ")" : "")));
            }

            _record.Fact("cut bench skin inputs", "models " + models.Count + " for " + states.Length + " bodies (one input a model, shared by its bodies; a body holds its palette block only: " + (states.Length > 0 && states[0].handle != null && states[0].handle.SkinModelForWarm != null ? (states[0].handle.SkinModelForWarm.BoneCount * 64 + 48) + " bytes of matrices, bounds and the valid flag (VpDirectSkinInput's block: 64 a bone, 32 for the bounds, 16 for the flag)" : "?") + ")"
                + "; totals: 24-byte models " + packedTotal + " bytes, cpu-float inputs " + floatTotal + " bytes, gpu shared inputs " + sharedTotal + " bytes; the product candidate under cpu-float holds the 24-byte model AND the float input (" + (packedTotal + floatTotal) + " bytes); under cpu the 24-byte model alone (" + packedTotal + "); the probe's reference arrays are the probe cases', none here");
        }

        // EVAL-1 review 1 (P2-4): the events of a request resolved once more from the buffers, with no observation; returns how many were new.
        private int ResolveEventsFinal(CutRequestState s)
        {
            if (!s.operation.IsSet) return 0;
            int before = (s.physicsFrame >= 0 ? 1 : 0) + (s.ledgerEventFrame >= 0 ? 1 : 0) + (s.committedEventFrame >= 0 ? 1 : 0) + (s.readyEventFrame >= 0 || s.readyEndedFrame >= 0 ? 1 : 0);
            if (s.physicsFrame < 0) ResolveEvent(SkinBakeProbeReactionEvents.Kind.Physics, s.operation, ref s.physicsFrame, ref s.physicsTick);
            if (s.ledgerEventFrame < 0) ResolveEvent(SkinBakeProbeReactionEvents.Kind.Ledger, s.operation, ref s.ledgerEventFrame, ref s.ledgerEventTick);
            if (s.committedEventFrame < 0) ResolveEvent(SkinBakeProbeReactionEvents.Kind.Commit, s.operation, ref s.committedEventFrame, ref s.committedEventTick);
            if (s.gpuFirst && s.readyEventFrame < 0 && s.readyEndedFrame < 0) ResolveCollect(s);
            int after = (s.physicsFrame >= 0 ? 1 : 0) + (s.ledgerEventFrame >= 0 ? 1 : 0) + (s.committedEventFrame >= 0 ? 1 : 0) + (s.readyEventFrame >= 0 || s.readyEndedFrame >= 0 ? 1 : 0);
            return after - before;
        }

        // The clip state at onBeforeRender (TL 3): the display's state of the source or a child -- ProvisionalSplit -- read for the round's
        // requests, once a request (the first frame it is seen), with no picture, no readback, no mesh and no string. A display state, not
        // a pixel's time; a frame it is not seen is not "not displayed".
        private void ObserveClipBeforeRender()
        {
            CutRequestState[] states = _activeStates;
            if (states == null || world == null || world.Display == null || world.Display.IsDisposed || world.Ledger == null) return;
            int frame = Time.frameCount;
            for (int i = 0; i < states.Length; i++)
            {
                CutRequestState s = states[i];
                if (s.clipBeforeRenderFrame >= 0 || !s.operation.IsSet || !s.source.IsSet) continue;
                LogicalFragmentId positive = s.positive, negative = s.negative;
                if (!positive.IsSet && world.Ledger.TryGetOperation(s.operation, out LogicalCutOperation record) && record.positive.IsSet) { positive = record.positive; negative = record.negative; }
                if (world.Display.StateOf(s.source) == LogicalCutDisplayState.ProvisionalSplit
                    || (positive.IsSet && (world.Display.StateOf(positive) == LogicalCutDisplayState.ProvisionalSplit || world.Display.StateOf(negative) == LogicalCutDisplayState.ProvisionalSplit)))
                {
                    s.clipBeforeRenderFrame = frame; s.clipBeforeRenderTick = System.Diagnostics.Stopwatch.GetTimestamp(); _clipObservations++;
                }
            }
        }

        // EVAL-1 (TL 4): between two rounds, back to the same initial state, outside the marks window -- the unfinished requests settle
        // (bounded), every piece of each request's lineage is retired by the product's single retirement (CutFragmentLifetime.TryRetire:
        // the ledger, the owner, the DAG's memory; the display drops a retired fragment at its collection), the round's bodies are destroyed,
        // and three frames pass before the next round's bodies are made. What was retired, refused and left is a fact of the record.
        private IEnumerator ResetRound(int round, CutRequestState[] states)
        {
            EndMarks();
            _activeStates = null;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); int f0 = Time.frameCount;
            int waited = 0;
            while (waited < 600 && !RoundSettled(states)) { waited++; yield return null; }
            bool settled = RoundSettled(states);
            int liveBefore = world.Ledger.LiveCutFragmentCount, ownersBefore = world.Owners.Count;
            int retired = 0, refused = 0; string lastRefusal = "";
            if (world.Lifetime != null)
            {
                foreach (CutRequestState s in states)
                {
                    if (!s.source.IsSet) continue;
                    world.Lifetime.MarkLineage(s.source);
                    int at = 0, guard = 0;
                    while (guard++ < 512 && world.Ledger.TryGetLiveCutFragmentAt(s.source, at, out LogicalFragmentId piece))
                    {
                        if (world.Lifetime.TryRetire(piece, out string why)) retired++;
                        else { refused++; lastRefusal = why ?? ""; at++; }
                    }
                }
            }
            string prefix = "skin bake probe cut body r" + round + " ";
            int destroyed = 0;
            for (int i = _made.Count - 1; i >= 0; i--)
            {
                GameObject g = _made[i];
                if (g == null) { _made.RemoveAt(i); continue; }
                if (!g.name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                _made.RemoveAt(i); Destroy(g); destroyed++;
            }
            yield return null; yield return null; yield return null;
            // EVAL-1 review 1 (P2-3): back to the initial state, or not -- settled, nothing refused, nothing pending, and the live cut fragments and
            // owners at their count before round 0. Not back: the rounds after this one are invalid (roundValid), the bench goes on and says so.
            int pendingBakes = world.SkinBakePending != null ? world.SkinBakePending.Count : 0;
            var notBack = new List<string>();
            if (!settled) notBack.Add("not settled");
            if (refused > 0) notBack.Add("retirement refused " + refused);
            if (world.Lifetime == null) notBack.Add("no lifetime (nothing retired)");
            if (pendingBakes > 0 || world.Storage.DeferredPendingCount > 0 || world.Geometry.PendingBasisCount > 0) notBack.Add("pending left (bakes " + pendingBakes + ", storage " + world.Storage.DeferredPendingCount + ", bases " + world.Geometry.PendingBasisCount + ")");
            if (world.Ledger.LiveCutFragmentCount != _baselineLiveFragments || world.Owners.Count != _baselineOwners) notBack.Add("residue: live cut fragments " + world.Ledger.LiveCutFragmentCount + " (initial " + _baselineLiveFragments + "), owners " + world.Owners.Count + " (initial " + _baselineOwners + ")");
            bool backToInitial = notBack.Count == 0;
            if (!backToInitial && _resetFailure == null) _resetFailure = "the reset of round " + round + " did not return to the initial state (" + string.Join(", ", notBack) + ")";
            _record.Fact("cut bench round " + round + " reset", (settled ? "settled" : "NOT SETTLED") + " after " + waited + " frames (pending bakes " + pendingBakes + ", storage pending " + world.Storage.DeferredPendingCount + ", DAG pending bases " + world.Geometry.PendingBasisCount + ")"
                + "; pieces retired " + retired + ", refused " + refused + (refused > 0 ? " (last: " + lastRefusal + ")" : "") + (world.Lifetime == null ? " (NO LIFETIME: nothing retired)" : "") + "; live cut fragments " + liveBefore + " -> " + world.Ledger.LiveCutFragmentCount + ", owners " + ownersBefore + " -> " + world.Owners.Count
                + "; bodies destroyed " + destroyed + "; frames " + f0 + ".." + Time.frameCount + " (" + (Time.frameCount - f0) + "), " + Ms(t0, System.Diagnostics.Stopwatch.GetTimestamp()).ToString("F1", Inv) + " ms wall; outside the marks window"
                + "; back to the initial state: " + (backToInitial ? "yes" : "NO (" + string.Join(", ", notBack) + ") -- the rounds after this one are INVALID"));
        }

        private bool RoundSettled(CutRequestState[] states)
        {
            if (world.SkinBakePending != null && world.SkinBakePending.Count > 0) return false;
            if (world.Storage.DeferredPendingCount > 0 || world.Geometry.PendingBasisCount > 0) return false;
            foreach (CutRequestState s in states)
            {
                if (!s.operation.IsSet) continue;
                CutGeometryStage stage = world.Geometry.StageOf(s.operation);
                if (stage != CutGeometryStage.Committed && stage != CutGeometryStage.Reclaimed) return false;
            }
            return true;
        }

        private void FlushMotionCaptures()
        {
            _deferCaptures = false;
            foreach (VpAppSwMotionCapture.Result r in _deferredCaptures) ProcessMotionCapture(r);
            if (_deferredCaptures.Count > 0) _record.Fact("motion capture processing", _deferredCaptures.Count + " captures kept in memory during the window and processed after it (no statistics, PNG or file write in the window's frames)");
            _deferredCaptures.Clear();
        }
        private void OpenMotionWindow(bool open)
        {
            if (!_settings.motionCapture) return;
            if (open && VpAppSwMotionCapture.WantsFrame == null)
            {
                _deferCaptures = _settings.IsCutBench;   // the cut bench's captures wait for the end of the window (review 2: with the window, whatever frame opens it)
                VpAppSwMotionCapture.WantsFrame = frame => _motionWindow;
                VpAppSwMotionCapture.Captured = OnMotionCaptured;
                VpAppSwMotionCapture.Issued = issue => { _motionIssues[issue.frame] = issue; };   // MOTION-CAPTURE-1 step 6: the pass's matrices per captured frame
            }
            _motionWindow = open;
        }

        // The motion output decoded by its stride: two halves a pixel (R16G16_SFloat) or four (R16G16B16A16_SFloat: xy the vector,
        // w the alpha the fragment writes as 1); any other format is written raw and its statistics left EMPTY (decoded no), so
        // that a readback that succeeded is never read as "zero motion" (TL review, 2026-11-02). The magnitude is |xy|.
        private void OnMotionCaptured(VpAppSwMotionCapture.Result r)
        {
            if (_deferCaptures) { _deferredCaptures.Add(r); return; }
            ProcessMotionCapture(r);
        }

        private void ProcessMotionCapture(VpAppSwMotionCapture.Result r)
        {
            if (r.failure != null || r.data == null)
            {
                _motionFailures++;
                _record.Motion(r.frame.ToString(Inv), r.eye.ToString(Inv), r.width.ToString(Inv), r.height.ToString(Inv), r.format.ToString(), "no", "", "", "", "", "", "", "", "", "", "", r.failure ?? "no data");
                return;
            }

            _motionCaptures++;
            string raw = "motion-f" + r.frame + "-e" + r.eye + ".bin", picture = _settings.picture ? "motion-f" + r.frame + "-e" + r.eye + ".png" : "";
            try { File.WriteAllBytes(Path.Combine(_settings.directory, raw), r.data); } catch (Exception e) { raw = "NOT WRITTEN: " + e.Message; }
            int pixels = r.width * r.height;
            int stride = r.format == UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16_SFloat ? 4 : r.format == UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat ? 8 : 0;
            bool decodable = stride > 0 && r.data.Length >= pixels * stride;
            if (!decodable)
            {
                _record.Motion(r.frame.ToString(Inv), r.eye.ToString(Inv), r.width.ToString(Inv), r.height.ToString(Inv), r.format.ToString(), "no", pixels.ToString(Inv), "", "", "", "", "", "", "", raw, "not decoded (format " + r.format + ", " + r.data.Length + " bytes)", "");
                return;
            }

            int nonzero = 0, nan = 0, alphaPixels = 0, alphaRowMin = int.MaxValue, alphaRowMax = -1;
            double sum = 0, max = 0;
            var magnitudes = new float[pixels];
            var xs = new float[pixels]; var ys = new float[pixels];
            for (int i = 0; i < pixels; i++)
            {
                float x = Mathf.HalfToFloat(BitConverter.ToUInt16(r.data, i * stride)), y = Mathf.HalfToFloat(BitConverter.ToUInt16(r.data, i * stride + 2));
                xs[i] = x; ys[i] = y;
                if (stride == 8 && Mathf.HalfToFloat(BitConverter.ToUInt16(r.data, i * stride + 6)) > 0.5f) { alphaPixels++; int row = i / r.width; if (row < alphaRowMin) alphaRowMin = row; if (row > alphaRowMax) alphaRowMax = row; }
                if (float.IsNaN(x) || float.IsNaN(y)) { nan++; continue; }
                float m = Mathf.Sqrt(x * x + y * y);
                if (m > 0f) nonzero++;
                sum += m; if (m > max) max = m;
                magnitudes[i] = m;
            }

            if (_settings.picture) try
            {
                var tex = new Texture2D(r.width, r.height, TextureFormat.RGB24, false);
                var colours = new Color32[pixels];
                float scale = max > 0 ? 255f / (float)max : 0f;
                for (int i = 0; i < pixels; i++)
                {
                    byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(magnitudes[i] * scale), 0, 255);
                    colours[i] = float.IsNaN(xs[i]) || float.IsNaN(ys[i]) ? new Color32(255, 0, 255, 255) : new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(128f + xs[i] * 64f / Mathf.Max(1e-6f, (float)max)), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(128f + ys[i] * 64f / Mathf.Max(1e-6f, (float)max)), 0, 255), g, 255);
                }
                tex.SetPixels32(colours); tex.Apply(false);
                File.WriteAllBytes(Path.Combine(_settings.directory, picture), tex.EncodeToPNG());
                Destroy(tex);
            }
            catch (Exception e) { picture = "NOT WRITTEN: " + e.Message; }

            var sorted = (float[])magnitudes.Clone(); Array.Sort(sorted);
            float p99 = sorted.Length > 0 ? sorted[Mathf.Clamp((int)(sorted.Length * 0.99f), 0, sorted.Length - 1)] : 0f;
            _record.Motion(r.frame.ToString(Inv), r.eye.ToString(Inv), r.width.ToString(Inv), r.height.ToString(Inv), r.format.ToString(), "yes", pixels.ToString(Inv), nonzero.ToString(Inv), nan.ToString(Inv),
                pixels > 0 ? (sum / pixels).ToString("G6", Inv) : "", max.ToString("G6", Inv), p99.ToString("G6", Inv),
                stride == 8 ? ((double)alphaPixels / Mathf.Max(1, pixels)).ToString("F4", Inv) : "", stride == 8 && alphaPixels > 0 ? alphaRowMin + ".." + alphaRowMax : (stride == 8 ? "none" : ""), raw, picture, "");
        }

        // ---- MOTION-CAPTURE-1: the capture-method check. Two small quads above the horizon (the sky behind them, nothing else
        // in the way): the left one a Unity MeshRenderer (URP's own camera-only motion), the right one the native 16B route
        // (a geometry shown through the cut display, as a cut body is). The fixed eye moves along x by the frame's index in the
        // sequence (SkinBakeProbeMotionCheck.OffsetX: hold, right, hold, left, hold); the AppSW motion output of the wanted
        // frames is read back (both copy points) together with what the motion pass used (VpAppSwMotionCapture.Issue), the
        // eye's actual pose at onBeforeRender and the quads' world corners. Raw and metadata only; the evaluation is
        // post-processing (Evaluate-SkinBakeMotionCheck.py). Nothing of the cut path runs.
        public const float MotionCheckQuadSize = 0.3f, MotionCheckQuadHeight = 2.3f, MotionCheckQuadDistance = 2.5f, MotionCheckQuadApart = 0.35f;
        private int _motionCheckStart = -1;
        private bool _motionCheckActive;
        private readonly List<string> _motionCheckFrames = new List<string>();
        private readonly Dictionary<int, VpAppSwMotionCapture.Issue> _motionIssues = new Dictionary<int, VpAppSwMotionCapture.Issue>();

        private Vector3 MotionCheckEyeOffset()
        {
            if (!_motionCheckActive || _motionCheckStart < 0) return Vector3.zero;
            return new Vector3(SkinBakeProbeMotionCheck.OffsetX(Time.frameCount - _motionCheckStart, _settings.motionCheckHold, _settings.motionCheckFrames, _settings.motionCheckStepCm * 0.01f), 0f, 0f);
        }

        private IEnumerator MotionCheck()
        {
            int hold = _settings.motionCheckHold, frames = _settings.motionCheckFrames;
            float step = _settings.motionCheckStepCm * 0.01f;
            if (world == null || !world.IsReady || world.Display == null) { Fail("motion check: no ready cut world in the scene"); yield break; }
            if (!_fixedViewOn) { Fail("motion check: the fixed view is off (no rig or no eye): the sequence cannot move the view"); yield break; }
            _record.Fact("build", Application.platform + " " + (Debug.isDebugBuild ? "Development" : "Release") + "; buildGUID " + Application.buildGUID + "; unity " + Application.unityVersion + "; graphics " + SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceName);
            RenderSettingsFact();
            // The quads: the Unity one left of centre, the native one right of centre, both facing the eye, above the horizon.
            var unityCentre = new Vector3(-MotionCheckQuadApart, MotionCheckQuadHeight, MotionCheckQuadDistance);
            var nativeCentre = new Vector3(MotionCheckQuadApart, MotionCheckQuadHeight, MotionCheckQuadDistance);
            float h = MotionCheckQuadSize * 0.5f;
            Vector3[] corners = { new Vector3(-h, -h, 0f), new Vector3(h, -h, 0f), new Vector3(h, h, 0f), new Vector3(-h, h, 0f) };
            var unityQuad = new GameObject("motion check quad (Unity renderer)");
            _made.Add(unityQuad);
            unityQuad.transform.position = unityCentre;
            var mesh = new Mesh { name = "motion check quad" };
            mesh.vertices = corners;
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.uv = new[] { new Vector2(0.1f, 0.1f), new Vector2(0.9f, 0.1f), new Vector2(0.9f, 0.9f), new Vector2(0.1f, 0.9f) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };   // both windings: visible from either side, whatever the cull
            mesh.RecalculateBounds();
            unityQuad.AddComponent<MeshFilter>().sharedMesh = mesh;
            var unityRenderer = unityQuad.AddComponent<MeshRenderer>();
            Shader surface = Shader.Find("Zantetsu/VP Mesh Surface");
            var material = new Material(surface != null ? surface : Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.9f, 0.3f, 0.2f) };
            unityRenderer.sharedMaterial = material;
            unityRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // The native quad: the same shape through the display, as the probe shows its baked geometries.
            var vertices = new VpRenderVertex[4];
            for (int i = 0; i < 4; i++) vertices[i] = new VpRenderVertex { position = corners[i], normal = Vector3.back, uv0 = mesh.uv[i] };
            uint[] indices = { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
            int[] topology = { 0, 1, 2, 3 };
            var submeshes = new[] { new VpGeometrySubmesh(0, indices.Length, 0) };
            _placement = new Placement(world.Display.Placement);
            world.Display.Placement = _placement;
            if (!Show(vertices, indices, topology, submeshes, Matrix4x4.Translate(nativeCentre), null, out LogicalFragmentId nativeFragment, out VpStoredGeometry nativeGeometry, out string why))
            { Fail("motion check: the native quad was not shown: " + why + HaltNote()); yield break; }
            _record.Fact("motion check", "quads " + MotionCheckQuadSize + " m, " + MotionCheckQuadDistance + " m ahead at height " + MotionCheckQuadHeight + " m (above the horizon of the eye at " + FixedEye.y + " m: the sky behind), Unity renderer at " + unityCentre.ToString("F3") + " (shader " + material.shader.name + "), native 16B route at " + nativeCentre.ToString("F3") + " (fragment " + nativeFragment.value + ", geometry v" + nativeGeometry.vertexStart + "+" + nativeGeometry.vertexCount + ")"
                + "; sequence: hold " + hold + " / right " + frames + " x " + step.ToString("F4", Inv) + " m / hold " + hold + " / left " + frames + " / hold " + hold + " = " + SkinBakeProbeMotionCheck.Length(hold, frames) + " frames, the eye's x offset by the frame's index (FixView target; LateUpdate and onBeforeRender compute the same value); captured frames: the last 2 of hold1, every moving frame, the first 2 and the last of hold2, the first 3 of hold3; copies in-pass (eyes 0/1) and end-of-frame (eyes 10/11); the pass's constants read back per captured frame");
            // Four frames with the quads shown and the view still, then the sequence.
            for (int f = 0; f < 4; f++) yield return null;
            _motionCheckStart = Time.frameCount + 1;
            _motionCheckActive = true;
            VpAppSwMotionCapture.WantsFrame = frame => _motionCheckActive && SkinBakeProbeMotionCheck.Wanted(frame - _motionCheckStart, hold, frames);
            VpAppSwMotionCapture.Captured = OnMotionCaptured;
            VpAppSwMotionCapture.Issued = issue => { _motionIssues[issue.frame] = issue; };
            Application.onBeforeRender += MotionCheckBeforeRender;
            int length = SkinBakeProbeMotionCheck.Length(hold, frames);
            yield return null;   // the first frame of the sequence begins
            for (int i = 0; i < length + 3; i++) yield return null;
            _motionCheckActive = false;
            Application.onBeforeRender -= MotionCheckBeforeRender;
            // The readbacks of the last frames come a few frames later.
            for (int f = 0; f < 6; f++) yield return null;
            _record.ExtraFile("motion-check-frames.csv", _motionCheckFrames.Count > 0 ? new[] { "frame,index,phase,targetOffsetX,eyePos,eyeRot,unityCentre,nativeCentre,quadHalf" }.Concat(_motionCheckFrames) : new[] { "frame,index,phase,targetOffsetX,eyePos,eyeRot,unityCentre,nativeCentre,quadHalf" });
            _record.ExtraFile("motion-check-issues.json", MotionIssuesJson());
            int decoded = 0; foreach (var issue in _motionIssues.Values) if (issue.decoded) decoded++;
            _record.Fact("motion check capture", "frames wanted " + CountWanted(hold, frames, length) + "; texture readbacks requested " + VpAppSwMotionCapture.Requested + " (end-of-frame copies " + VpAppSwMotionCapture.EndOfFrameRequested + "), captured " + _motionCaptures + ", failed " + _motionFailures
                + "; issues reported " + VpAppSwMotionCapture.IssuesReported + ", with constants decoded " + decoded + (VpAppSwMotionCapture.Requested == 0 ? "; NONE: no motion pass ran for the camera (no AppSW motion target on this platform or route): the sequence and the eye poses are recorded, nothing is compared" : ""));
            _record.Other("motion check", "sequence", "observation", null, Time.frameCount, "the eye offset target returned to 0 after the sequence; " + _motionCheckFrames.Count + " frame rows; evaluate with Evaluate-SkinBakeMotionCheck.py (the pass/fail of the method is the evaluation's, not this run's)");
            VpAppSwMotionCapture.Release();
            world.Display.Placement = _placement.Inner;
            Destroy(material);
        }

        private static int CountWanted(int hold, int frames, int length) { int n = 0; for (int i = 0; i < length; i++) if (SkinBakeProbeMotionCheck.Wanted(i, hold, frames)) n++; return n; }

        private void MotionCheckBeforeRender()
        {
            if (!_motionCheckActive || eye == null) return;
            int index = Time.frameCount - _motionCheckStart;
            float h = MotionCheckQuadSize * 0.5f;
            _motionCheckFrames.Add(Time.frameCount + "," + index + "," + SkinBakeProbeMotionCheck.PhaseOf(index, _settings.motionCheckHold, _settings.motionCheckFrames) + "," + MotionCheckEyeOffset().x.ToString("F5", Inv) + ","
                + SkinBakeProbeCutPlane.Point((float3)eye.transform.position) + "," + SkinBakeProbeCutPlane.Point((float3)eye.transform.rotation.eulerAngles) + ","
                + SkinBakeProbeCutPlane.Point(new float3(-MotionCheckQuadApart, MotionCheckQuadHeight, MotionCheckQuadDistance)) + "," + SkinBakeProbeCutPlane.Point(new float3(MotionCheckQuadApart, MotionCheckQuadHeight, MotionCheckQuadDistance)) + "," + h.ToString("F3", Inv));
        }

        private IEnumerable<string> MotionIssuesJson()
        {
            string M(Matrix4x4 m) { var t = new System.Text.StringBuilder("["); for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) { if (r > 0 || c > 0) t.Append(','); t.Append(m[r, c].ToString("R", Inv)); } return t.Append(']').ToString(); }
            string S(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var lines = new List<string> { "[" };
            var frames = new List<int>(_motionIssues.Keys); frames.Sort();
            for (int i = 0; i < frames.Count; i++)
            {
                VpAppSwMotionCapture.Issue issue = _motionIssues[frames[i]];
                var t = new System.Text.StringBuilder("  {");
                t.Append("\"frame\":").Append(issue.frame).Append(",\"index\":").Append(_motionCheckStart >= 0 ? issue.frame - _motionCheckStart : -1).Append(",\"camera\":").Append(S(issue.camera)).Append(",\"variant\":").Append(S(issue.variant)).Append(",\"copyPoint\":").Append(S(issue.copyPoint))
                    .Append(",\"viewport\":[").Append(issue.viewport.x.ToString("R", Inv)).Append(',').Append(issue.viewport.y.ToString("R", Inv)).Append(',').Append(issue.viewport.width.ToString("R", Inv)).Append(',').Append(issue.viewport.height.ToString("R", Inv)).Append(']')
                    .Append(",\"stereo\":").Append(issue.stereo ? "true" : "false").Append(",\"grouped\":").Append(issue.grouped ? "true" : "false").Append(",\"width\":").Append(issue.width).Append(",\"height\":").Append(issue.height).Append(",\"slices\":").Append(issue.slices).Append(",\"format\":").Append(S(issue.format.ToString()))
                    .Append(",\"issues\":").Append(issue.issues).Append(",\"passVP\":[").Append(M(issue.passVP[0])).Append(',').Append(M(issue.passVP[1])).Append("],\"passV\":[").Append(M(issue.passV[0])).Append(',').Append(M(issue.passV[1])).Append("],\"passP\":[").Append(M(issue.passP[0])).Append(',').Append(M(issue.passP[1])).Append(']')
                    .Append(",\"blocks\":[");
                for (int b = 0; b < issue.blocks.Count; b++) { if (b > 0) t.Append(','); t.Append("{\"stage\":").Append(S(issue.blocks[b].stage)).Append(",\"name\":").Append(S(issue.blocks[b].name)).Append(",\"offset\":").Append(issue.blocks[b].offset).Append(",\"size\":").Append(issue.blocks[b].size).Append('}'); }
                t.Append("],\"constantsOffset\":").Append(issue.constantsOffset).Append(",\"constantsSize\":").Append(issue.constantsSize).Append(",\"constantsRead\":").Append(issue.constants != null ? "true" : "false").Append(",\"constantsFailure\":").Append(S(issue.constantsFailure))
                    .Append(",\"decoded\":").Append(issue.decoded ? "true" : "false").Append(",\"decodeFailure\":").Append(S(issue.decodeFailure))
                    .Append(",\"previousVP\":[").Append(M(issue.previousVP[0])).Append(',').Append(M(issue.previousVP[1])).Append("],\"currentVP\":[").Append(M(issue.currentVP[0])).Append(',').Append(M(issue.currentVP[1])).Append("],\"ndcModifier\":").Append(float.IsNaN(issue.ndcModifier) ? "null" : issue.ndcModifier.ToString("R", Inv))
                    .Append('}').Append(i + 1 < frames.Count ? "," : "");
                lines.Add(t.ToString());
            }

            lines.Add("]");
            return lines;
        }

        // ---- MOTION-CAPTURE-1 step 6: the cut bench's eye follows the check's sequence. The sequence's index is anchored so that
        // its first MOVING frame (index = hold) is the bench's k 0, the first request's frame: the frames before it are its first
        // hold (offset 0: the fixed view as it was), the right move covers the hit, the provisional clip, the CPU Ready and the
        // Commit (frames >= those), then the middle hold, the left move and the last hold. The offset is a function of the frame's
        // index only (SkinBakeProbeMotionCheck.OffsetX), folded into FixView's target like the check's. A row a frame of the window.
        public const string MotionViewHeader = "round,frame,k,index,phase,targetOffsetX,eyePos,eyeRot";
        private readonly List<string> _motionViewFrames = new List<string>();
        private int _motionViewRound = -1, _motionViewRounds;
        private bool _motionViewHooked;

        private void StartMotionView(int round)
        {
            if (!_settings.motionView) return;
            if (!_fixedViewOn) { _record.Other("motion view", "round " + round, "failure", false, Time.frameCount, "the fixed view is off (no rig or no eye): the sequence cannot move the eye"); return; }
            int hold = _settings.motionCheckHold, frames = _settings.motionCheckFrames;
            _motionCheckStart = Time.frameCount + 1 - hold;   // the next frame is k 0 = index hold
            _motionCheckActive = true; _motionViewRound = round; _motionViewRounds++;
            if (!_motionViewHooked) { Application.onBeforeRender += MotionViewBeforeRender; _motionViewHooked = true; }
            _record.Fact("motion view round " + round, "the eye's x offset follows the capture check's sequence by the frame's index: index " + hold + " (the first moving frame) = the bench's k 0 (the first request's frame) = frame " + (Time.frameCount + 1)
                + "; right " + frames + " x " + (_settings.motionCheckStepCm * 0.01f).ToString("F4", Inv) + " m from k 0, hold " + hold + " from k " + frames + ", left " + frames + " from k " + (frames + hold) + ", hold " + hold + "; LateUpdate and onBeforeRender compute the same target from the same index (no delta time, no accumulation)");
        }

        private void MotionViewBeforeRender()
        {
            if (!_motionCheckActive || eye == null || _motionCheckStart < 0 || !_motionWindow) return;
            int index = Time.frameCount - _motionCheckStart, hold = _settings.motionCheckHold, frames = _settings.motionCheckFrames;
            _motionViewFrames.Add(_motionViewRound + "," + Time.frameCount + "," + (index - hold) + "," + index + "," + SkinBakeProbeMotionCheck.PhaseOf(index, hold, frames) + "," + MotionCheckEyeOffset().x.ToString("F5", Inv) + ","
                + SkinBakeProbeCutPlane.Point((float3)eye.transform.position) + "," + SkinBakeProbeCutPlane.Point((float3)eye.transform.rotation.eulerAngles));
        }

        private IEnumerator Hold(int frames, string picture)
        {
            for (int f = 0; f < frames; f++)
            {
                _gpu.Update();
                if (f == frames / 2 && _settings.picture && !string.IsNullOrEmpty(_settings.directory)) TakePicture(picture);
                yield return null;
            }
        }

        private void TakePicture(string name)
        {
            string path = Path.Combine(_settings.directory, "picture-" + name + ".png");
            string persistent = Application.persistentDataPath.Replace('\\', '/');
            if (Application.platform == RuntimePlatform.Android && path.Replace('\\', '/').StartsWith(persistent, StringComparison.Ordinal)) path = path.Replace('\\', '/').Substring(persistent.Length).TrimStart('/');
            ScreenCapture.CaptureScreenshot(path);
            _record.Other("picture", name, "picture", null, Time.frameCount, path);
        }

        /// <summary>
        /// The probe's answer for its own fragments (the scene draw comparison's way, SceneDrawVpDisplay): each stands
        /// where the probe last put it (Following), and is not vouched fixed -- the probe moves them by telling. Others
        /// go to the world's own lookup.
        /// </summary>
        private sealed class Placement : IVpFragmentPlacement, IVpFixedPlacementSource
        {
            public readonly IVpFragmentPlacement Inner;
            private readonly Dictionary<LogicalFragmentId, Matrix4x4> _own = new Dictionary<LogicalFragmentId, Matrix4x4>();
            public long Answers;

            public Placement(IVpFragmentPlacement inner) { Inner = inner; }

            public void Move(LogicalFragmentId fragment, Matrix4x4 geometryLocalToWorld)
            {
                _own[fragment] = geometryLocalToWorld;
                CutPhysicsStep.NotePlacementInputChanged(fragment);
            }

            public void Forget(LogicalFragmentId fragment) { _own.Remove(fragment); }

            public bool IsPlacementFixed(LogicalFragmentId fragment)
            {
                if (_own.ContainsKey(fragment)) return false;
                return Inner is IVpFixedPlacementSource vouching && vouching.IsPlacementFixed(fragment);
            }

            public VpFragmentPlacementKind TryGetGeometryLocalToWorld(LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 geometryLocalToWorld)
            {
                if (_own.TryGetValue(fragment, out geometryLocalToWorld)) { Answers++; return VpFragmentPlacementKind.Following; }
                if (Inner == null) { geometryLocalToWorld = default; return VpFragmentPlacementKind.Static; }
                return Inner.TryGetGeometryLocalToWorld(fragment, operation, side, out geometryLocalToWorld);
            }
        }

        private Material RigMaterial()
        {
            Material source = null;
            foreach (GameObject t in templates)
            {
                SkinnedMeshRenderer r = t != null ? t.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
                if (r != null && r.sharedMaterial != null) { source = r.sharedMaterial; break; }
            }

            if (source == null && GraphicsSettings.currentRenderPipeline != null) source = GraphicsSettings.currentRenderPipeline.defaultMaterial;
            if (source == null) source = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var m = new Material(source) { name = "skin bake probe rig material" };
            return m;
        }

        private void WriteRecord()
        {
            if (string.IsNullOrEmpty(_settings.directory)) return;
            try { _record.Write(_settings.directory, "Skin bake probe " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + SystemInfo.graphicsDeviceType); }
            catch (Exception e) { Debug.LogError(Prefix + "the record could not be written: " + e); }
        }

        private void Fail(string why)
        {
            Current = Phase.Failed; Failure = why; ExitCode = ExitFailed;
            Debug.LogError(Prefix + "FAILED: " + why);
            _record?.Other("probe", "run", "failure", false, Time.frameCount, why);
            WriteRecord();
            if (!_armedForTest && !_settings.keepOpen) Application.Quit(ExitCode);
        }

        private void LateUpdate() { MarkLate(); FixView(); }   // the Update-phase tracking countered here; onBeforeRender counters the late one

        // The fixed view (npc-skin-bench, SKIN-BENCH-2 section 2): the rig placed so that the tracked eye's world pose is FixedEye
        // facing +Z. The camera's transform is what every pass reads, so all see one view; the rig moves against the head.
        private void FixView()
        {
            if (!_fixedViewOn || eye == null || rig == null) return;
            Quaternion rigRotation = rig.rotation;
            Vector3 rigPosition = rig.position;
            Vector3 local = Quaternion.Inverse(rigRotation) * (eye.transform.position - rigPosition);
            Quaternion localRotation = Quaternion.Inverse(rigRotation) * eye.transform.rotation;
            Quaternion wanted = Quaternion.Inverse(localRotation);
            Vector3 wantedPosition = FixedEye + MotionCheckEyeOffset() - wanted * local;
            if (wanted == rigRotation && wantedPosition == rigPosition) return;
            rig.SetPositionAndRotation(wantedPosition, wanted);
            _rigMoveMax = Mathf.Max(_rigMoveMax, Vector3.Distance(wantedPosition, rigPosition));
            _rigTurnMax = Mathf.Max(_rigTurnMax, Quaternion.Angle(wanted, rigRotation));
        }

        private void OnDestroy()
        {
            VpLogicalCutDisplay.CastersOffForDiagnosis = false;
            VpStencilCapBatch.CapsOffForDiagnosis = false;
            VpSkinBakePending.HoldSubmitFramesForDiagnosis = 0;
            HookReactionRecord(false);
            EndMarks();
            WarmBeforeBench = null; WarmReport = null;
            if (_record != null && _deferredCaptures.Count > 0) FlushMotionCaptures();
            if (_settings != null && (_settings.motionCapture || _settings.motionCheck)) VpAppSwMotionCapture.Release();
            if (_motionCheckActive) { _motionCheckActive = false; Application.onBeforeRender -= MotionCheckBeforeRender; }
            if (_motionViewHooked) { Application.onBeforeRender -= MotionViewBeforeRender; _motionViewHooked = false; }
            if (_pictureStateHooked) { Application.onBeforeRender -= WritePendingPictureState; _pictureStateHooked = false; }
            if (_settings != null && _settings.cutSurfaceDebug) VpCutSurfaceColour.SetDebugEnabled(false);
            if (_fixedViewOn) { Application.onBeforeRender -= FixView; _fixedViewOn = false; }
            if (_placement != null && world != null && world.Display != null && !world.Display.IsDisposed && ReferenceEquals(world.Display.Placement, _placement)) world.Display.Placement = _placement.Inner;
            _gpu?.Dispose();
            foreach (SkinBakeProbeMeshInput i in _inputs) i.Dispose();
            _inputs.Clear();
            if (_rigMaterial != null) Destroy(_rigMaterial);
        }

        private static void Log(string text) => Debug.Log(Prefix + text);
    }
}
