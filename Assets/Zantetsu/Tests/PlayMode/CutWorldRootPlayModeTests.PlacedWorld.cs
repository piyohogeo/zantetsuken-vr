using System.IO;
using System.Linq;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The placed cuttables' test world (2026-10-03): the hull trial's world with the source material 0 bound as well (a
    /// Megacity input draws with its first material, as a city's world binds it), the licensed prop input the cases read,
    /// and the registrar components a case placed -- whatever they registered handed to the fixture's collection in the
    /// case's finally, so a case that fails part way leaves no registered actor behind.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const string PlacedPropInputPath = "Assets/Licensed/WalkCity/Inputs/bench_001.json";

        private CutWorldRoot NewPlacedHullWorld()
        {
            return NewHullWorld(0.9f, null, 0.3f, root =>
            {
                var field = typeof(CutWorldRoot).GetField("materials", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var bound = (CutWorldRoot.MaterialBinding[])field.GetValue(root);
                var first = new CutWorldRoot.MaterialBinding { sourceIndex = 0, material = Track(new Material(Shader.Find("Zantetsu/VP Indexed Indirect Unlit")) { name = "first" }) };
                field.SetValue(root, bound.Concat(new[] { first }).ToArray());
            });
        }

        private readonly System.Collections.Generic.List<PlayableCityCuttable> _placedRegistrars = new System.Collections.Generic.List<PlayableCityCuttable>();

        private void TrackPlacedRegistrars()
        {
            foreach (PlayableCityCuttable c in _placedRegistrars)
            {
                if (c != null && c.IsRegistered && c.Registration.Actor != null && !_actors.Contains(c.Registration.Actor)) _actors.Add(c.Registration.Actor);
            }

            _placedRegistrars.Clear();
        }

        private PlayableCityCuttable PlaceRegistrar(CutWorldRoot root, TextAsset input, string name, Vector3 at, float yaw, Vector3 scale)
        {
            GameObject instance = TrackActor(new GameObject(name));
            instance.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
            instance.transform.localScale = scale;
            GameObject place = TrackActor(new GameObject("prop-" + name));
            var c = place.AddComponent<PlayableCityCuttable>();
            c.world = root;
            c.input = input;
            c.target = instance.transform;
            c.mass = 50f;
            c.building = false;
            _placedRegistrars.Add(c);
            return c;
        }
    }
}
