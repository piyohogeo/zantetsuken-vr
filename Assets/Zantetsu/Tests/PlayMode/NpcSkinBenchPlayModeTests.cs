using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Animation;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        // A template as the benchmark scene holds one: an inactive holder with its SandboxNpcCharacter, the character
        // root (the needle rig with its pose player) beneath it, not prepared. The runner instantiates it.
        private GameObject BenchTemplate(string family)
        {
            var (points, faces) = NeedleHull();
            string intakeJson = "{\"assets\":[{\"family\":\"" + family + "\",\"objectName\":\"needle mesh\",\"topologyMap\":[" + string.Join(",", Enumerable.Range(0, points.Length))
                                + "],\"topologyCount\":" + points.Length + "}]}";
            var offsets = new List<int> { 0 };
            foreach (int[] f in faces) offsets.Add(offsets[offsets.Count - 1] + f.Length);
            string hullJson = "{\"hulls\":[{\"boneName\":\"Lent character bone\",\"rendererBindVertices\":[" + string.Join(",", points.Select(p => p.x.ToString("R") + "," + p.y.ToString("R") + "," + p.z.ToString("R")))
                              + "],\"faceOffsets\":[" + string.Join(",", offsets) + "],\"faceIndices\":[" + string.Join(",", faces.SelectMany(f => f)) + "]}]}";
            SkinnedMeshRenderer r = LentBonedRig(out Transform _, out GameObject root, out Rigidbody motion);
            Mesh m = r.sharedMesh;
            m.Clear();
            double3 centre = points.Aggregate((x, y) => x + y) / points.Length;
            m.vertices = points.Select(p => new Vector3((float)p.x, (float)p.y, (float)p.z)).ToArray();
            m.normals = points.Select(p => ((Vector3)(float3)(p - centre)).normalized).ToArray();
            m.uv = Enumerable.Repeat(new Vector2(.5f, .5f), points.Length).ToArray();
            m.boneWeights = Enumerable.Repeat(new BoneWeight { weight0 = 1 }, points.Length).ToArray();
            m.bindposes = new[] { Matrix4x4.identity };
            m.triangles = faces.SelectMany(f => f).ToArray();
            m.name = "needle mesh";
            root.AddComponent<PoseTablePlayer>().Configure(_benchTable, root.transform, 0.0, true);
            GameObject holder = ColdTrack(new GameObject("bench template " + family));
            holder.SetActive(false);
            root.transform.SetParent(holder.transform, true);
            var character = holder.AddComponent<SandboxNpcCharacter>();
            void Set(string field, object value) => typeof(SandboxNpcCharacter).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(character, value);
            Set("world", coldWorld);
            Set("characterRoot", root);
            Set("motionBody", motion);
            Set("intake", ColdTrack(new TextAsset(intakeJson) { name = "bench intake " + family }));
            Set("hulls", ColdTrack(new TextAsset(hullJson) { name = "bench hulls " + family }));
            Set("family", family);
            return holder;
        }

        private TextAsset _benchTable;
        private NpcSkinBenchRunner _bench;
        private string _benchDirectory;

        // Stands in for the camera drawing: no camera draws the cold world in a test, so the skin display's frame update
        // (the palette gather) is called every frame here, after the runner's LateUpdate has read its counters.
        private sealed class BenchTestDrawer : MonoBehaviour
        {
            public CutWorldRoot world;

            private void LateUpdate()
            {
                if (world != null && world.DrawsSkins) world.SkinDisplay.UpdateFrame(false);
            }
        }

        private IEnumerator RunBench(VpSkinDrawRequest request, NpcSkinBenchPose pose, int count, VpSkinGatherMode gather = VpSkinGatherMode.Sync, int updateCount = -1)
        {
            VpSkinDrawRequest before = VpSkinDrawSetup.Request;
            VpSkinGatherMode gatherBefore = VpSkinGatherSetup.Mode;
            VpSkinDrawSetup.Request = request;
            VpSkinGatherSetup.Mode = gather;   // read when the world makes its skin display (SKIN-BENCH-3)
            try
            {
                ColdWorld();
                _benchTable = ColdTrack(new TextAsset(PoseTablePlayerPlayModeTests.Table("Lent character bone")) { name = "bench table" });
                GameObject template = BenchTemplate("needle");
                var eye = ColdTrack(new GameObject("bench eye")).AddComponent<Camera>();
                eye.transform.position = new Vector3(0f, 1.7f, 0f);
                Transform rig = ColdTrack(new GameObject("bench rig")).transform;
                _benchDirectory = Path.Combine(Application.temporaryCachePath, "npc-skin-bench-test-" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
                var settings = new NpcSkinBenchSettings { directory = _benchDirectory, count = count, families = 1, pose = pose, shadows = NpcSkinBenchShadows.Off, updateCount = updateCount, warmupSeconds = 0.15f, measureSeconds = 0.3f, measureFrames = 60, settleFrames = 5, picture = false, keepOpen = true };
                GameObject runnerObject = ColdTrack(new GameObject("bench runner"));
                runnerObject.SetActive(false);
                _bench = runnerObject.AddComponent<NpcSkinBenchRunner>();
                _bench.ConfigureForTest(coldWorld, new[] { template }, _benchTable, null, rig, eye, settings);
                ColdTrack(new GameObject("bench test drawer")).AddComponent<BenchTestDrawer>().world = coldWorld;
                runnerObject.SetActive(true);
                float deadline = Time.realtimeSinceStartup + 60f;
                while (_bench.Current != NpcSkinBenchRunner.Phase.Done && _bench.Current != NpcSkinBenchRunner.Phase.Failed && Time.realtimeSinceStartup < deadline) yield return null;
                TestContext.Out.WriteLine("bench " + request + " " + pose + ": phase " + _bench.Current + ", failure " + _bench.Failure + ", valid " + _bench.Valid + " [" + string.Join("; ", _bench.Invalidities) + "], rows " + _bench.Rows.Count
                    + ", poses applied " + _bench.PosesAppliedInMeasurement + ", palettes gathered " + _bench.PalettesGatheredInMeasurement + ", route " + _bench.RequestedRoute + "->" + _bench.ActualRoute);
            }
            finally
            {
                VpSkinDrawSetup.Request = before;
                VpSkinGatherSetup.Mode = gatherBefore;
            }
        }

        private IEnumerator EndBench()
        {
            if (_bench != null) foreach (GameObject made in _bench.Made.ToArray()) if (made != null) Object.Destroy(made);
            yield return null;
            if (_bench != null) Object.Destroy(_bench.gameObject);
            _bench = null;
            yield return null;
            if (_benchDirectory != null && Directory.Exists(_benchDirectory)) Directory.Delete(_benchDirectory, true);
            _benchDirectory = null;
        }

        /// <summary>
        /// **The benchmark over the skin draw, poses updated (DESIGN 3.4, npc-skin-bench).** Asked for the skin draw by
        /// name, the runner makes the count of slots from the template, prepares and activates them, finds the world's
        /// skin draw and every individual in it with its renderer off, applies every individual's pose each frame of the
        /// measurement (the palette gathered as often), writes the table and the result, and judges the run valid.
        /// </summary>
        [UnityTest]
        public IEnumerator TheBench_OverTheSkinDraw_UpdatingPoses_MeasuresAndIsValid()
        {
            yield return RunBench(VpSkinDrawRequest.SkinDraw, NpcSkinBenchPose.Update, 4);
            Assert.That(_bench.Current, Is.EqualTo(NpcSkinBenchRunner.Phase.Done), _bench.Failure);
            Assert.That(_bench.RequestedRoute, Is.EqualTo("SKIN24"));
            Assert.That(_bench.ActualRoute, Is.EqualTo("SKIN24-UNITY"), coldWorld.SkinRouteReason);
            Assert.That(_bench.Slots.Count, Is.EqualTo(4));
            Assert.That(_bench.Slots.All(s => s.DrawnBySkinDraw), Is.True, "every individual in the skin draw: " + string.Join("; ", _bench.Slots.Select(s => s.SkinDrawRefusal)));
            Assert.That(_bench.Slots.All(s => s.Renderer != null && !s.Renderer.enabled), Is.True, "their renderers off: the skin draw draws them");
            Assert.That(_bench.Slots.Select(s => s.CharacterRoot.transform.position).Distinct().Count(), Is.EqualTo(4), "placed apart");
            Assert.That(coldWorld.SkinDisplay.IndividualCount, Is.EqualTo(4));
            int measured = _bench.Rows.Count(r => r.phase == 1);
            Assert.That(measured, Is.EqualTo(60), "measured by frames: exactly the frames asked (SKIN-BENCH-2)");
            Assert.That(_bench.Rows.Count(r => r.phase == 2), Is.EqualTo(5), "the settle span after the pose clock's resynchronisation");
            Assert.That(_bench.MeasureStepStart, Is.EqualTo(6), "the pose clock began again at the resynchronisation: steps 1..5 settled, 6.. measured");
            Assert.That(_bench.MeasureStepEnd, Is.EqualTo(65));
            Assert.That(_bench.ComparisonValid, Is.True, string.Join("; ", _bench.ComparisonIssues));
            Assert.That(_bench.PosesAppliedInMeasurement, Is.EqualTo(measured * 4), "every individual posed every measured frame");
            Assert.That(_bench.PalettesGatheredInMeasurement, Is.InRange(measured * 4 - 8, measured * 4 + 8), "a pose change gathers its palette (the window's edges may differ by a frame)");
            Assert.That(_bench.Valid, Is.True, string.Join("; ", _bench.Invalidities));
            Assert.That(_bench.ExitCode, Is.EqualTo(NpcSkinBenchRunner.ExitValid));
            Assert.That(File.Exists(Path.Combine(_benchDirectory, "bench-frames.csv")), Is.True);
            string json = File.ReadAllText(Path.Combine(_benchDirectory, "bench-result.json"));
            Assert.That(json, Does.Contain("\"valid\": true").And.Contain("\"requestedRoute\": \"SKIN24\"").And.Contain("\"actualRoute\": \"SKIN24-UNITY\"").And.Contain("\"individuals\": 4").And.Contain("\"gpuBreakdown\": \"not measured")
                .And.Contain("\"comparisonValid\": true").And.Contain("\"measureStepStart\": 6").And.Contain("\"measureStepEnd\": 65").And.Contain("\"eyeStart\": {").And.Contain("\"headAtSettle\": {"));
            Assert.That(_bench.Rows.Where(r => r.phase == 1).All(r => !double.IsNaN(r.skinFrameMs) && !double.IsNaN(r.poseEvaluateMs)), Is.True, "the markers are recorded a frame");
            string[] csv = File.ReadAllLines(Path.Combine(_benchDirectory, "bench-frames.csv"));
            Assert.That(csv[0], Is.EqualTo(NpcSkinBenchReport.Header));
            Assert.That(csv.Length - 1, Is.EqualTo(_bench.Rows.Count));
            yield return EndBench();
        }

        /// <summary>
        /// **The worker gather, and the update count (SKIN-BENCH-3).** Asked for the worker gather, the world's display gathers by
        /// the jobs (every measured frame used them: the drawer's update precedes the late-update scheduler here, so each frame is
        /// scheduled from the update itself, counted as late), the palettes equal in number to the poses applied; with an update
        /// count of 2 of 4, two individuals are posed and gathered each frame and the run is valid and comparable.
        /// </summary>
        [UnityTest]
        public IEnumerator TheBench_OverTheSkinDraw_WithTheWorkerGather_AndTwoOfFourUpdated_IsValid()
        {
            yield return RunBench(VpSkinDrawRequest.SkinDraw, NpcSkinBenchPose.Update, 4, VpSkinGatherMode.Worker, updateCount: 2);
            Assert.That(_bench.Current, Is.EqualTo(NpcSkinBenchRunner.Phase.Done), _bench.Failure);
            Assert.That(coldWorld.SkinDisplay.GatherModeActual, Is.EqualTo("worker"), coldWorld.SkinDisplay.WorkerFallbackReason);
            Assert.That(coldWorld.SkinGatherScheduler, Is.Not.Null, "the late-update scheduler stands with the display");
            int measured = _bench.Rows.Count(r => r.phase == 1);
            Assert.That(measured, Is.EqualTo(60));
            Assert.That(_bench.PosesAppliedInMeasurement, Is.EqualTo(measured * 2), "two of four posed each frame");
            Assert.That(_bench.PalettesGatheredInMeasurement, Is.InRange(measured * 2 - 4, measured * 2 + 4));
            Assert.That(coldWorld.SkinDisplay.JobsScheduled, Is.GreaterThan(0));
            Assert.That(coldWorld.SkinDisplay.JobFailures, Is.Zero);
            Assert.That(coldWorld.SkinDisplay.LateRegathers, Is.Zero, "no pose is written after the schedule here");
            Assert.That(_bench.Rows.Count(r => r.phase == 1 && r.skinJobs == 1), Is.GreaterThanOrEqualTo(measured - 1), "the jobs gathered every measured frame");
            Assert.That(_bench.Rows.Where(r => r.phase == 1 && r.skinJobs == 1).All(r => r.skinJobIndividuals == 2), Is.True, "two individuals a frame by the jobs");
            Assert.That(_bench.ComparisonValid, Is.True, string.Join("; ", _bench.ComparisonIssues));
            Assert.That(_bench.Valid, Is.True, string.Join("; ", _bench.Invalidities));
            string json = File.ReadAllText(Path.Combine(_benchDirectory, "bench-result.json"));
            Assert.That(json, Does.Contain("\"skinGatherActual\": \"worker\"").And.Contain("\"updateCount\": 2").And.Contain("\"jobsScheduledInMeasurement\": "));
            yield return EndBench();
        }

        /// <summary>**Poses stopped.** The pose is applied once at the activation and never in the measurement: no pose applied, no palette gathered; the run is valid.</summary>
        [UnityTest]
        public IEnumerator TheBench_OverTheSkinDraw_WithPosesStopped_GathersNoPalette()
        {
            yield return RunBench(VpSkinDrawRequest.SkinDraw, NpcSkinBenchPose.Stop, 3);
            Assert.That(_bench.Current, Is.EqualTo(NpcSkinBenchRunner.Phase.Done), _bench.Failure);
            Assert.That(_bench.Slots.All(s => s.DrawnBySkinDraw), Is.True);
            Assert.That(_bench.Slots.All(s => s.CharacterRoot.GetComponent<PoseTablePlayer>().AppliedFrame >= 0), Is.True, "posed once at the activation");
            Assert.That(_bench.Slots.All(s => s.CharacterRoot.GetComponent<PoseTablePlayer>().ApplyRevision >= 1), Is.True, "the revision counts the applications (SKIN-BENCH-3)");
            Assert.That(_bench.PosesAppliedInMeasurement, Is.Zero);
            Assert.That(_bench.PalettesGatheredInMeasurement, Is.Zero, "no pose version change: no palette gathered");
            Assert.That(_bench.Rows.Count(r => r.phase == 1), Is.GreaterThan(0));
            Assert.That(_bench.Valid, Is.True, string.Join("; ", _bench.Invalidities));
            yield return EndBench();
        }

        /// <summary>**The renderers' route.** Asked for the renderers, the world makes no skin draw; every individual draws by its enabled renderer and none is in a skin draw; the run is valid.</summary>
        [UnityTest]
        public IEnumerator TheBench_OverTheRenderers_DrawsByTheRenderersAlone()
        {
            yield return RunBench(VpSkinDrawRequest.Renderers, NpcSkinBenchPose.Update, 3);
            Assert.That(_bench.Current, Is.EqualTo(NpcSkinBenchRunner.Phase.Done), _bench.Failure);
            Assert.That(_bench.RequestedRoute, Is.EqualTo("SMR"));
            Assert.That(_bench.ActualRoute, Is.EqualTo("RENDERERS"));
            Assert.That(coldWorld.DrawsSkins, Is.False);
            Assert.That(_bench.Slots.All(s => s.Renderer != null && s.Renderer.enabled && !s.DrawnBySkinDraw), Is.True);
            Assert.That(_bench.PosesAppliedInMeasurement, Is.EqualTo(_bench.Rows.Count(r => r.phase == 1) * 3));
            Assert.That(_bench.PalettesGatheredInMeasurement, Is.Zero, "no skin draw: nothing gathers");
            Assert.That(_bench.Rows.Where(r => r.phase == 1).All(r => r.visibleRenderers >= 0), Is.True, "the renderers' visibility is counted a frame");
            Assert.That(_bench.Valid, Is.True, string.Join("; ", _bench.Invalidities));
            yield return EndBench();
        }

        /// <summary>**No route named.** The launch must choose: with neither argument the run fails before any slot is made.</summary>
        [UnityTest]
        public IEnumerator TheBench_WithoutASkinRouteNamed_Fails()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("names no skin route"));
            yield return RunBench(VpSkinDrawRequest.Default, NpcSkinBenchPose.Update, 2);
            Assert.That(_bench.Current, Is.EqualTo(NpcSkinBenchRunner.Phase.Failed));
            Assert.That(_bench.Failure, Does.Contain("names no skin route").And.Contain(VpSkinDrawSetup.RenderersArgument).And.Contain(VpSkinDrawSetup.SkinDrawArgument));
            Assert.That(_bench.ExitCode, Is.EqualTo(NpcSkinBenchRunner.ExitFailed));
            Assert.That(_bench.Slots.Count, Is.Zero);
            Assert.That(File.ReadAllText(Path.Combine(_benchDirectory, "bench-result.txt")), Does.StartWith("FAILED: "));
            yield return EndBench();
        }
    }
}
