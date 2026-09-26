using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The manual physics update of DESIGN 4.4 as the player loop runs it: the physics scene is advanced by
    /// <see cref="CutPhysicsStep"/> alone, at most once per frame, after every late update and before the display
    /// collects; only a real simulation moves the physics time, the step id and the bodies; a pause owes nothing.
    /// The arithmetic itself is <c>ManualPhysicsClockTests</c>'.
    /// </summary>
    public class CutPhysicsStepPlayModeTests
    {
        private const string ScenePath = "Assets/Scenes/CutWorldSandbox.unity";

        private readonly List<GameObject> _objects = new List<GameObject>();

        // The scene a case of this class loaded, and the world that scene built: what this class leaves behind it.
        private Scene _loaded;
        private CutWorldRoot _loadedWorld;

        /// <summary>
        /// Takes away the scene a case loaded, and only that: its world is ended the ordinary way first, if the case did
        /// not get as far as ending it, and the scene is unloaded once nothing of the world is out any more. An empty
        /// scene of this class's own stands in as the active one, so that the next fixture does not run inside this one's
        /// floor and bodies.
        /// </summary>
        [UnityTearDown]
        public IEnumerator UnloadTheSceneThisCaseLoaded()
        {
            if (!_loaded.IsValid() || !_loaded.isLoaded)
            {
                yield break;
            }

            if (_loadedWorld != null && !_loadedWorld.IsReleased)
            {
                _loadedWorld.Shutdown();
                float deadline = Time.realtimeSinceStartup + 30f;
                while (_loadedWorld != null && !_loadedWorld.IsReleased && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    _loadedWorld?.Shutdown();
                }

                Assert.That(_loadedWorld == null || _loadedWorld.IsReleased, Is.True,
                    "the loaded scene's world ended before its scene was taken away");
            }

            Scene empty = SceneManager.CreateScene("After " + nameof(CutPhysicsStepPlayModeTests) + " " + Time.frameCount);
            SceneManager.SetActiveScene(empty);
            yield return SceneManager.UnloadSceneAsync(_loaded);
            _loaded = default;
            _loadedWorld = null;
        }

        [TearDown]
        public void Cleanup()
        {
            CutPhysicsStep.SetPaused(false);
            foreach (GameObject go in _objects)
            {
                if (go != null)
                {
                    Object.Destroy(go);
                }
            }

            _objects.Clear();
        }

        /// <summary>Reads, in each frame, what has happened before the frame's updates and after its late updates.</summary>
        [DefaultExecutionOrder(-30000)]
        private sealed class EarlyReader : MonoBehaviour
        {
            internal readonly List<(int frame, int decided, long step, float x)> seen = new List<(int, int, long, float)>();
            internal Rigidbody body;

            private void Update()
            {
                seen.Add((Time.frameCount, CutPhysicsStep.LastDecidedFrame, CutPhysicsStep.Clock.StepId, body.position.x));
            }
        }

        [DefaultExecutionOrder(30000)]
        private sealed class LateReader : MonoBehaviour
        {
            internal readonly List<(int frame, int decided, long step)> seen = new List<(int, int, long)>();

            private void LateUpdate()
            {
                seen.Add((Time.frameCount, CutPhysicsStep.LastDecidedFrame, CutPhysicsStep.Clock.StepId));
            }
        }

        [UnityTest]
        public IEnumerator TheStep_IsDecidedOncePerFrameAfterTheLateUpdates_AndOnlyARealSimulationMovesTheWorld()
        {
            Assert.That(Physics.simulationMode, Is.EqualTo(SimulationMode.Script), "the project steps by script");
            var go = new GameObject("Stepped body");
            _objects.Add(go);
            go.transform.position = new Vector3(0f, 800f, 0f);
            go.AddComponent<SphereCollider>();
            Rigidbody body = go.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.linearVelocity = new Vector3(3f, 0f, 0f);
            body.linearDamping = 0f;
            body.sleepThreshold = 0f;

            var readers = new GameObject("Step readers");
            _objects.Add(readers);
            var early = readers.AddComponent<EarlyReader>();
            early.body = body;
            var late = readers.AddComponent<LateReader>();

            // The simulation is recorded where the profiler can see it (DESIGN 4.4: its duration is kept there).
            long stepsBefore = CutPhysicsStep.Clock.StepId;
            using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.CutPhysicsStep.Simulate", 256);
            for (int i = 0; i < 90; i++)
            {
                yield return null;
            }

            long recorded = 0;
            for (int i = 0; i < recorder.Count; i++)
            {
                recorded += recorder.GetSample(i).Count;
            }

            long stepsTaken = CutPhysicsStep.Clock.StepId - stepsBefore;
            Assert.That(stepsTaken, Is.GreaterThan(0));
            Assert.That(recorded, Is.EqualTo(stepsTaken).Within(2),
                "each real simulation is one sample of the profiler marker (the last frames may not be collected yet)");
            Assert.That(CutPhysicsStep.LastSimulateSeconds, Is.GreaterThan(0.0), "and its duration is kept for the next decision");

            Assert.That(late.seen.Count, Is.GreaterThan(30));
            foreach (var (frame, decided, _) in late.seen)
            {
                Assert.That(decided, Is.LessThan(frame), "after the late updates of frame " + frame + " it is not decided yet");
            }

            int stepped = 0;
            for (int i = 1; i < early.seen.Count; i++)
            {
                var previous = early.seen[i - 1];
                var now = early.seen[i];
                Assert.That(now.decided, Is.EqualTo(now.frame - 1), "by the next frame, the previous frame has decided");
                long steps = now.step - previous.step;
                Assert.That(steps, Is.InRange(0L, 1L), "at most one simulation in a frame");
                if (steps == 0)
                {
                    Assert.That(now.x, Is.EqualTo(previous.x), "a frame that did not simulate does not move the body");
                }
                else
                {
                    stepped++;
                    Assert.That(now.x - previous.x, Is.EqualTo(3f * (float)CutPhysicsStep.Clock.StepSeconds).Within(1e-4f),
                        "a real step moves the body by one fixed step");
                }
            }

            Assert.That(stepped, Is.GreaterThan(0), "the physics did advance");
            Assert.That(CutPhysicsStep.Clock.PhysicsSeconds,
                Is.EqualTo(CutPhysicsStep.Clock.StepId * CutPhysicsStep.Clock.StepSeconds).Within(1e-9),
                "the physics time is the real steps, and only them");
        }

        [UnityTest]
        public IEnumerator WhilePaused_NothingIsOwedAndNothingMoves_AndTheStepResumesAfterwards()
        {
            var go = new GameObject("Paused body");
            _objects.Add(go);
            go.transform.position = new Vector3(0f, 900f, 0f);
            go.AddComponent<SphereCollider>();
            Rigidbody body = go.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.linearVelocity = new Vector3(3f, 0f, 0f);
            body.sleepThreshold = 0f;
            yield return null;

            CutPhysicsStep.SetPaused(true);
            yield return null;
            long pausedAt = CutPhysicsStep.Clock.StepId;
            double owedAt = CutPhysicsStep.Clock.UnsimulatedSeconds;
            float x = body.position.x;
            float until = Time.realtimeSinceStartup + 0.3f;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(CutPhysicsStep.Clock.UnsimulatedSeconds, Is.EqualTo(owedAt), "the pause is not owed, and what was owed is kept");
            Assert.That(CutPhysicsStep.Clock.StepId, Is.EqualTo(pausedAt), "nothing is simulated while paused");
            Assert.That(body.position.x, Is.EqualTo(x), "and nothing moves");

            CutPhysicsStep.SetPaused(false);
            until = Time.realtimeSinceStartup + 0.3f;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(body.position.x, Is.GreaterThan(x), "the step resumes");
        }

        [UnityTest]
        public IEnumerator TheDisplayCollects_AfterTheLateUpdatesAndTheDecision_NotInTheDriversLateUpdate()
        {
            SceneManager.LoadScene(ScenePath, LoadSceneMode.Single);
            yield return null;
            _loaded = SceneManager.GetSceneByPath(ScenePath);
            var world = Object.FindFirstObjectByType<CutWorldRoot>();
            _loadedWorld = world;
            Assert.That(world, Is.Not.Null);
            Assert.That(world.IsReady, Is.True);

            var readers = new GameObject("Collection readers");
            _objects.Add(readers);
            var probe = readers.AddComponent<CollectionReader>();
            probe.world = world;
            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            Assert.That(probe.seen.Count, Is.GreaterThan(5));
            for (int i = 1; i < probe.seen.Count; i++)
            {
                var previous = probe.seen[i - 1];
                var now = probe.seen[i];
                Assert.That(previous.openAtLate, Is.False, "after the late updates the frame is not collected yet");
                Assert.That(now.collectionsAtEarly, Is.GreaterThan(previous.collectionsAtLate),
                    "between the late updates of one frame and the next frame, the display collected");
                Assert.That(now.decidedAtEarly, Is.EqualTo(now.frame - 1), "and the step was decided for that frame");
            }

            world.Shutdown();
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!world.IsReleased && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(world.IsReleased, Is.True, "the world ended on the ordinary frames");
        }

        [DefaultExecutionOrder(30000)]
        private sealed class CollectionReader : MonoBehaviour
        {
            internal readonly List<(int frame, bool openAtLate, int collectionsAtLate, int collectionsAtEarly, int decidedAtEarly)> seen =
                new List<(int, bool, int, int, int)>();

            internal CutWorldRoot world;
            private int _earlyCollections;
            private int _earlyDecided;

            private void Update()
            {
                _earlyCollections = world.Display.SettledCollections;
                _earlyDecided = CutPhysicsStep.LastDecidedFrame;
            }

            private void LateUpdate()
            {
                seen.Add((Time.frameCount, world.Display.IsFrameOpen, world.Display.SettledCollections, _earlyCollections,
                    _earlyDecided));
            }
        }
    }
}
