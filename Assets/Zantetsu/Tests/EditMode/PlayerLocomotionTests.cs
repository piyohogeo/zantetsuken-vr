using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Artificial movement against the fixed occupancy (Phase 4.2 / T-088, DESIGN 7.2.3 / D-131 / D-166).
    /// </summary>
    public class PlayerLocomotionTests
    {
        private static readonly PlayerCapsuleAuthoring RootCapsule =
            new PlayerCapsuleAuthoring(new Vector3(0f, 0.9f, 0f), Vector3.zero, 0.25f, 1.6f);

        private static readonly PlayerCapsuleAuthoring HmdCapsule =
            new PlayerCapsuleAuthoring(Vector3.zero, Vector3.zero, 0.15f, 0.3f);

        private readonly List<GameObject> _made = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject made in _made)
            {
                if (made != null)
                {
                    Object.DestroyImmediate(made);
                }
            }

            _made.Clear();
        }

        [Test]
        public void ACandidateThatOverlapsNothing_IsAllowed_AndTheRootTakesIt()
        {
            PlayerLocomotion player = Player(Level(Box("wall", new Vector3(3f, 1f, 0f), new Vector3(0.2f, 2f, 4f))), out _);
            var candidate = new Pose(new Vector3(1f, 0f, 0.5f), Quaternion.Euler(0f, 45f, 0f));

            LocomotionVerdict verdict = player.TryRequest(candidate, new Pose(new Vector3(0f, 1.6f, 0f), Quaternion.identity));

            Assert.That(verdict, Is.EqualTo(LocomotionVerdict.Allowed));
            Assert.That(player.transform.position, Is.EqualTo(candidate.position));
            Assert.That(Quaternion.Angle(player.transform.rotation, candidate.rotation), Is.LessThan(1e-3f));
            Assert.That(player.AllowedCount, Is.EqualTo(1));
        }

        [Test]
        public void OnlyTheRootCapsuleOverlapping_RejectsTheWholeRequest_MoveAndTurn()
        {
            // A low block: the Root capsule reaches it, the HMD 1.6 m up does not.
            PlayerLocomotion player = Player(Level(Box("block", new Vector3(1f, 0.3f, 0f), new Vector3(0.4f, 0.6f, 0.4f))), out _);
            var start = new Pose(player.transform.position, player.transform.rotation);

            LocomotionVerdict verdict = player.TryRequest(
                new Pose(new Vector3(0.7f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f)),
                new Pose(new Vector3(0f, 1.6f, 0f), Quaternion.identity));

            Assert.That(verdict, Is.EqualTo(LocomotionVerdict.RejectedRoot));
            AssertUnchanged(player, start);
        }

        [Test]
        public void OnlyTheHmdCapsuleOverlapping_RejectsTheWholeRequest_MoveAndTurn()
        {
            // A beam at head height: the HMD reaches it, the Root capsule below does not.
            PlayerLocomotion player = Player(Level(Box("beam", new Vector3(1f, 1.9f, 0f), new Vector3(0.4f, 0.2f, 4f))), out _);
            var start = new Pose(player.transform.position, player.transform.rotation);

            LocomotionVerdict verdict = player.TryRequest(
                new Pose(new Vector3(0.9f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f)),
                new Pose(new Vector3(0f, 1.8f, 0f), Quaternion.identity));

            Assert.That(verdict, Is.EqualTo(LocomotionVerdict.RejectedHmd));
            AssertUnchanged(player, start);
        }

        [Test]
        public void TheCandidateRotation_AndAnHmdFarFromTheRoot_AreBothCarriedIntoTheJudgement()
        {
            // The HMD is 1.2 m to the Root's right. Turned 90 degrees, that is 1.2 m behind the Root in the world,
            // where a post stands; unturned it is clear. The Root capsule is clear either way.
            PlayerLocomotion player = Player(Level(Capsule("post", new Vector3(0f, 1.25f, -1.2f), 0.2f, 2.5f)), out _);
            var farRight = new Pose(new Vector3(1.2f, 1.6f, 0f), Quaternion.identity);

            Assert.That(
                PlayerLocomotion.Judge(player.Occupancy, player.Body, new Pose(Vector3.zero, Quaternion.Euler(0f, 90f, 0f)), farRight),
                Is.EqualTo(LocomotionVerdict.RejectedHmd));
            Assert.That(
                PlayerLocomotion.Judge(player.Occupancy, player.Body, new Pose(Vector3.zero, Quaternion.identity), farRight),
                Is.EqualTo(LocomotionVerdict.Allowed));

            // The same through the entry, with the post also reaching the Root once the Root itself moves onto it.
            Assert.That(
                player.TryRequest(new Pose(new Vector3(0f, 0f, -1.2f), Quaternion.identity), farRight),
                Is.EqualTo(LocomotionVerdict.RejectedRoot));
        }

        [Test]
        public void TheSettledOccupancy_IsUnchanged_WhenItsSourceMoves_IsReplaced_OrIsRetired()
        {
            PlayerLocomotionOccupancyVolume wall = Box("wall", new Vector3(1f, 1f, 0f), new Vector3(0.2f, 2f, 2f));
            PlayerLocomotionLevel level = Level(wall);
            PlayerLocomotion player = Player(level, out _);
            LocomotionBox settled = level.Occupancy.Box(0);
            var intoWall = new Pose(new Vector3(0.9f, 0f, 0f), Quaternion.identity);
            var toWhereItMoved = new Pose(new Vector3(-3f, 0f, 0f), Quaternion.identity);
            var head = new Pose(new Vector3(0f, 1.6f, 0f), Quaternion.identity);

            // Moved, resized, turned.
            wall.transform.SetPositionAndRotation(new Vector3(-3f, 1f, 0f), Quaternion.Euler(0f, 30f, 0f));
            wall.Set(PlayerLocomotionOccupancyVolume.Shape.Box, Vector3.zero, Vector3.zero, new Vector3(4f, 4f, 4f), 0f, 0f);
            AssertSame(level.Occupancy.Box(0), settled);
            Assert.That(player.TryRequest(intoWall, head), Is.EqualTo(LocomotionVerdict.RejectedBoth));
            Assert.That(PlayerLocomotion.Judge(level.Occupancy, player.Body, toWhereItMoved, head), Is.EqualTo(LocomotionVerdict.Allowed));

            // Replaced the way a cut replaces its source: the source retired, pieces standing elsewhere.
            Object.DestroyImmediate(wall.gameObject);
            Box("fragment", new Vector3(-3f, 1f, 0f), new Vector3(1f, 2f, 1f));
            AssertSame(level.Occupancy.Box(0), settled);
            Assert.That(level.Occupancy.BoxCount, Is.EqualTo(1));
            Assert.That(player.TryRequest(intoWall, head), Is.EqualTo(LocomotionVerdict.RejectedBoth));
            Assert.That(player.TryRequest(toWhereItMoved, head), Is.EqualTo(LocomotionVerdict.Allowed));
        }

        [Test]
        public void ARealSpaceLean_IsNeitherHeldBackNorWritten_AndOnlyAnOverlappingArtificialMoveIsRejected()
        {
            PlayerLocomotion player = Player(Level(Box("wall", new Vector3(1f, 1.25f, 0f), new Vector3(0.2f, 2.5f, 4f))), out Transform hmd);

            // The player leans 0.8 m right in real space: the tracked HMD is inside the wall. Nothing reacts.
            hmd.localPosition = new Vector3(0.8f, 1.6f, 0f);
            Assert.That(hmd.localPosition, Is.EqualTo(new Vector3(0.8f, 1.6f, 0f)));
            Assert.That(player.transform.position, Is.EqualTo(Vector3.zero));

            // An artificial move while leaning in -- even a step away that leaves the HMD in -- is rejected.
            Assert.That(player.TryRequest(new Pose(new Vector3(-0.02f, 0f, 0f), Quaternion.identity)), Is.EqualTo(LocomotionVerdict.RejectedHmd));
            Assert.That(player.transform.position, Is.EqualTo(Vector3.zero));

            // A step that takes the HMD out is allowed. The tracked pose is left as tracked.
            Assert.That(player.TryRequest(new Pose(new Vector3(-0.4f, 0f, 0f), Quaternion.identity)), Is.EqualTo(LocomotionVerdict.Allowed));
            Assert.That(hmd.localPosition, Is.EqualTo(new Vector3(0.8f, 1.6f, 0f)));
            Assert.That(player.transform.position, Is.EqualTo(new Vector3(-0.4f, 0f, 0f)));
        }

        [Test]
        public void ThePlayerLayer_TouchesNoLayer_AndEveryOtherPairIsLeftAsItWas()
        {
            int player = LayerMask.NameToLayer("Player");
            Assert.That(player, Is.EqualTo(8));
            for (int layer = 0; layer < 32; layer++)
            {
                Assert.That(Physics.GetIgnoreLayerCollision(player, layer), Is.True, "Player with layer " + layer);
                for (int other = 0; other < 32; other++)
                {
                    if (layer != player && other != player)
                    {
                        Assert.That(Physics.GetIgnoreLayerCollision(layer, other), Is.False, layer + " with " + other);
                    }
                }
            }
        }

        [Test]
        public void TheSandbox_IsWired_ForArtificialMovement()
        {
            try
            {
                UnityEngine.SceneManagement.Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/Sandbox.unity", OpenSceneMode.Single);
                GameObject origin = GameObject.Find("XR Origin");
                PlayerLocomotion locomotion = origin.GetComponent<PlayerLocomotion>();
                SandboxLocomotionInput input = origin.GetComponent<SandboxLocomotionInput>();
                Assert.That(locomotion, Is.Not.Null);
                Assert.That(input, Is.Not.Null);
                Assert.That(input.Locomotion, Is.SameAs(locomotion));

                var serialized = new SerializedObject(locomotion);
                PlayerLocomotionLevel level = (PlayerLocomotionLevel)serialized.FindProperty("level").objectReferenceValue;
                Assert.That(serialized.FindProperty("hmd").objectReferenceValue, Is.SameAs(GameObject.Find("XR Origin/Camera Offset/Main Camera").transform));
                Assert.That(level, Is.Not.Null);
                Assert.That(level.Occupancy.BoxCount + level.Occupancy.CapsuleCount, Is.GreaterThanOrEqualTo(2));
                Assert.That(level.GetComponentsInChildren<Collider>(true), Is.Empty, "the walls are display and authoring only");

                SandboxRightHandKatana katana = GameObject.Find("Sandbox Katana Rig").GetComponent<SandboxRightHandKatana>();
                Assert.That(new SerializedObject(katana).FindProperty("trackingSpace").objectReferenceValue,
                    Is.SameAs(GameObject.Find("XR Origin/Camera Offset").transform));

                int playerLayer = LayerMask.NameToLayer("Player");
                // The katana root starts inactive, so the roots are found among the scene's.
                var names = new List<string> { "XR Origin", "Sandbox Katana Rig", "Katana" };
                int found = 0;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (!names.Contains(root.name))
                    {
                        continue;
                    }

                    found++;
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        Assert.That(t.gameObject.layer, Is.EqualTo(playerLayer), t.name);
                    }
                }

                Assert.That(found, Is.EqualTo(names.Count));
            }
            finally
            {
                // Leave no scene behind for the tests after this one, as the other scene tests do.
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            }
        }

        private static void AssertUnchanged(PlayerLocomotion player, Pose start)
        {
            Assert.That(player.transform.position, Is.EqualTo(start.position));
            Assert.That(player.transform.rotation, Is.EqualTo(start.rotation));
            Assert.That(player.AllowedCount, Is.EqualTo(0));
            Assert.That(player.RejectedCount, Is.EqualTo(1));
        }

        private static void AssertSame(LocomotionBox actual, LocomotionBox expected)
        {
            Assert.That(actual.center, Is.EqualTo(expected.center));
            Assert.That(actual.rotation, Is.EqualTo(expected.rotation));
            Assert.That(actual.halfExtents, Is.EqualTo(expected.halfExtents));
        }

        private GameObject Make(string name)
        {
            var made = new GameObject(name);
            _made.Add(made);
            return made;
        }

        private PlayerLocomotionOccupancyVolume Box(string name, Vector3 center, Vector3 size)
        {
            GameObject made = Make(name);
            made.transform.position = center;
            PlayerLocomotionOccupancyVolume volume = made.AddComponent<PlayerLocomotionOccupancyVolume>();
            volume.Set(PlayerLocomotionOccupancyVolume.Shape.Box, Vector3.zero, Vector3.zero, size, 0f, 0f);
            return volume;
        }

        private PlayerLocomotionOccupancyVolume Capsule(string name, Vector3 center, float radius, float height)
        {
            GameObject made = Make(name);
            made.transform.position = center;
            PlayerLocomotionOccupancyVolume volume = made.AddComponent<PlayerLocomotionOccupancyVolume>();
            volume.Set(PlayerLocomotionOccupancyVolume.Shape.Capsule, Vector3.zero, Vector3.zero, Vector3.zero, radius, height);
            return volume;
        }

        private PlayerLocomotionLevel Level(params PlayerLocomotionOccupancyVolume[] volumes)
        {
            PlayerLocomotionLevel level = Make("level").AddComponent<PlayerLocomotionLevel>();
            level.SetVolumes(volumes);
            return level;
        }

        private PlayerLocomotion Player(PlayerLocomotionLevel level, out Transform hmd)
        {
            GameObject root = Make("player");
            hmd = new GameObject("hmd").transform;
            hmd.SetParent(root.transform, false);
            hmd.localPosition = new Vector3(0f, 1.6f, 0f);
            PlayerLocomotion player = root.AddComponent<PlayerLocomotion>();
            player.Configure(level, hmd, RootCapsule, HmdCapsule);
            return player;
        }
    }
}
