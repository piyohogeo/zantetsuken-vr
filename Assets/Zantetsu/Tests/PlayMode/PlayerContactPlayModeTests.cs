using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The player gives no physics impulse (Phase 4.2 / T-088, DESIGN 7.2.3): a body and a blade on the Player layer
    /// pass through a prop without moving it, the same body on the Default layer pushes it, and props keep their
    /// contact with each other and with the ground. Run in a physics scene of its own, stepped here, so the session's
    /// step has no part in it.
    /// </summary>
    public class PlayerContactPlayModeTests
    {
        private const float Step = 1f / 90f;

        private Scene _scene;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_scene.IsValid() && _scene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_scene);
            }
        }

        [Test]
        public void APlayerBodyAndBlade_PassThroughAProp_AndPropsKeepTheirContacts()
        {
            Scene scene = _scene = SceneManager.CreateScene("PlayerContact", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            {
                int player = LayerMask.NameToLayer("Player");
                Assert.That(player, Is.GreaterThanOrEqualTo(0));
                PhysicsScene physics = scene.GetPhysicsScene();

                MakeStatic(scene, "ground", new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));
                Rigidbody prop = Make(scene, "prop", PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f), Vector3.one, 0, dynamic: true);
                Rigidbody stacked = Make(scene, "stacked", PrimitiveType.Cube, new Vector3(4f, 0.5f, 0f), Vector3.one, 0, dynamic: true);
                Rigidbody onTop = Make(scene, "on top", PrimitiveType.Cube, new Vector3(4f, 1.5f, 0f), Vector3.one, 0, dynamic: true);
                Rigidbody body = Make(scene, "player body", PrimitiveType.Capsule, new Vector3(-2f, 1f, 0f), new Vector3(0.5f, 1f, 0.5f), player, dynamic: false);
                Rigidbody blade = Make(scene, "katana", PrimitiveType.Cube, new Vector3(0f, 0.5f, -2f), new Vector3(0.05f, 0.05f, 0.9f), player, dynamic: false);

                Settle(physics, 45);
                Vector3 propRest = prop.position;
                Vector3 onTopRest = onTop.position;
                Assert.That(propRest.y, Is.EqualTo(0.5f).Within(0.02f), "a prop rests on the ground");
                Assert.That(onTopRest.y, Is.EqualTo(1.5f).Within(0.03f), "a prop rests on another");

                // The body walks through the prop and the blade sweeps through it, on the Player layer.
                for (int i = 0; i <= 90; i++)
                {
                    body.MovePosition(new Vector3(-2f + (4f * i / 90f), 1f, 0f));
                    blade.MovePosition(new Vector3(0f, 0.5f, -2f + (4f * i / 90f)));
                    physics.Simulate(Step);
                }

                Assert.That(Vector3.Distance(prop.position, propRest), Is.LessThan(1e-3f), "the Player layer moved the prop");
                Assert.That(prop.linearVelocity.magnitude, Is.LessThan(1e-3f));
                Assert.That(Vector3.Distance(onTop.position, onTopRest), Is.LessThan(1e-3f));
                Assert.That(stacked.position.y, Is.EqualTo(0.5f).Within(0.02f));

                // The control: the same walk on the Default layer does push it, so the check above can see a push.
                SetLayer(body.gameObject, 0);
                body.position = new Vector3(-2f, 1f, 0f);
                physics.Simulate(Step);
                for (int i = 0; i <= 90; i++)
                {
                    body.MovePosition(new Vector3(-2f + (4f * i / 90f), 1f, 0f));
                    physics.Simulate(Step);
                }

                Assert.That(Vector3.Distance(prop.position, propRest), Is.GreaterThan(0.1f), "the Default layer did not push the prop");
            }
        }

        private static void Settle(PhysicsScene physics, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                physics.Simulate(Step);
            }
        }

        private static void SetLayer(GameObject made, int layer)
        {
            made.layer = layer;
        }

        private static void MakeStatic(Scene scene, string name, Vector3 position, Vector3 scale)
        {
            GameObject made = GameObject.CreatePrimitive(PrimitiveType.Cube);
            made.name = name;
            SceneManager.MoveGameObjectToScene(made, scene);
            made.transform.position = position;
            made.transform.localScale = scale;
        }

        private static Rigidbody Make(Scene scene, string name, PrimitiveType type, Vector3 position, Vector3 scale, int layer, bool dynamic)
        {
            GameObject made = GameObject.CreatePrimitive(type);
            made.name = name;
            SceneManager.MoveGameObjectToScene(made, scene);
            made.layer = layer;
            made.transform.position = position;
            made.transform.localScale = scale;
            Rigidbody body = made.AddComponent<Rigidbody>();
            body.isKinematic = !dynamic;
            body.interpolation = RigidbodyInterpolation.None;
            return body;
        }
    }
}
