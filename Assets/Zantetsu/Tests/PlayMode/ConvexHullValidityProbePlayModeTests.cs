using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.LowLevelPhysics;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// What this Unity (6000.3.22f1) reports for a convex MeshCollider whose mesh PhysX refused to cook, against ones it
    /// accepted (2026-09-30). Physics.BakeMesh returns nothing, so the public answer is the collider's
    /// GeometryHolder.Type: ConvexMesh for an accepted mesh and Invalid for a refused one -- but only for an enabled
    /// collider on an active object; a disabled collider, or one on an inactive object, reports Invalid for every mesh.
    /// That is why the cook's hull check enables its probe for the read. Degenerate inputs that span fewer than three
    /// axes are not refused: the cooking falls back to a thin shape (the flat and point cases below). The refused cases
    /// are four vertices of which only three are distinct (a corner repeated, or within 1e-7 m) spanning all three axes.
    /// </summary>
    public class ConvexHullValidityProbePlayModeTests
    {
        private const MeshColliderCookingOptions Cooking = MeshColliderCookingOptions.CookForFasterSimulation
            | MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices
            | MeshColliderCookingOptions.UseFastMidphase;

        private static Mesh Make(string name, Vector3[] v)
        {
            var m = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            m.vertices = v;
            m.triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };
            return m;
        }

        // Accepted by PhysX (a convex shape, possibly a fallback one): the name and the vertices.
        private static IEnumerable<(Mesh mesh, bool refused)> Cases()
        {
            yield return (Make("valid tetrahedron 1 m", new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f) }), false);
            yield return (Make("valid tetrahedron 1 cm", new[] { Vector3.zero, new Vector3(0.01f, 0f, 0f), new Vector3(0f, 0.01f, 0f), new Vector3(0f, 0f, 0.01f) }), false);
            yield return (Make("three distinct of four in a plane", new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0f) }), false);
            yield return (Make("all four at one point", new[] { Vector3.one, Vector3.one, Vector3.one, Vector3.one }), false);
            yield return (Make("four coplanar", new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(1f, 1f, 0f) }), false);
            yield return (Make("sliver 1e-6 m high", new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0.3f, 0.3f, 1e-6f) }), false);
            yield return (Make("tilted triangle, fourth on an edge midpoint", new[] { Vector3.zero, new Vector3(1f, 0.3f, 0.2f), new Vector3(0.2f, 1f, 0.4f), new Vector3(0.6f, 0.65f, 0.3f) }), false);
            yield return (Make("tilted sliver 1e-5 m thick", new[] { Vector3.zero, new Vector3(1f, 0.3f, 0.2f), new Vector3(0.2f, 1f, 0.4f), new Vector3(0.4f, 0.43f, 0.2f) + new Vector3(-0.1f, -0.36f, 0.83f) * 1e-5f }), false);
            yield return (Make("four within 1e-6 m", new[] { Vector3.zero, new Vector3(1e-6f, 0f, 0f), new Vector3(0f, 1e-6f, 0f), new Vector3(0f, 0f, 1e-6f) }), false);
            yield return (Make("tilted triangle, fourth on a corner", new[] { Vector3.zero, new Vector3(1f, 0.3f, 0.2f), new Vector3(0.2f, 1f, 0.4f), new Vector3(0.2f, 1f, 0.4f) }), true);
            yield return (Make("tilted triangle, fourth 1e-7 m from a corner", new[] { Vector3.zero, new Vector3(1f, 0.3f, 0.2f), new Vector3(0.2f, 1f, 0.4f), new Vector3(0.2f, 1f, 0.4f + 1e-7f) }), true);
        }

        [UnityTest]
        public IEnumerator TheGeometryHolder_TellsARefusedConvexFromAnAcceptedOne_OnlyForAnEnabledColliderOnAnActiveObject()
        {
            LogAssert.ignoreFailingMessages = true;   // PhysX's errors for the refused cases are expected, and counted below
            var errors = new List<string>();
            Application.LogCallback capture = (message, stack, type) => { if (type == LogType.Error || type == LogType.Exception) errors.Add(message); };
            Application.logMessageReceivedThreaded += capture;
            var made = new List<Object>();
            try
            {
                foreach ((Mesh mesh, bool refused) in Cases())
                {
                    made.Add(mesh);
                    lock (errors) errors.Clear();
                    Physics.BakeMesh(mesh.GetInstanceID(), true, Cooking);
                    int bakeErrors;
                    lock (errors) bakeErrors = errors.Count;

                    var active = new GameObject("probe active");
                    made.Add(active);
                    var c = active.AddComponent<MeshCollider>();
                    c.cookingOptions = Cooking;
                    c.convex = true;
                    c.sharedMesh = mesh;
                    GeometryType enabled = c.GeometryHolder.Type;
                    c.enabled = false;
                    GeometryType disabled = c.GeometryHolder.Type;

                    var inactive = new GameObject("probe inactive");
                    made.Add(inactive);
                    inactive.SetActive(false);
                    var e = inactive.AddComponent<MeshCollider>();
                    e.cookingOptions = Cooking;
                    e.convex = true;
                    e.sharedMesh = mesh;
                    GeometryType inactiveType = e.GeometryHolder.Type;

                    TestContext.Out.WriteLine(mesh.name + ": bake errors " + bakeErrors + "; enabled " + enabled + ", disabled " + disabled + ", inactive " + inactiveType);
                    Assert.That(enabled, Is.EqualTo(refused ? GeometryType.Invalid : GeometryType.ConvexMesh), mesh.name + ": an enabled collider tells it");
                    Assert.That(bakeErrors, refused ? Is.GreaterThan(0) : Is.Zero, mesh.name + ": PhysX logs a refusal at the bake exactly when it refused");
                    Assert.That(disabled, Is.EqualTo(GeometryType.Invalid), mesh.name + ": a disabled collider tells nothing");
                    Assert.That(inactiveType, Is.EqualTo(GeometryType.Invalid), mesh.name + ": a collider on an inactive object tells nothing");
                }
            }
            finally
            {
                Application.logMessageReceivedThreaded -= capture;
                foreach (Object o in made) Object.Destroy(o);
            }

            yield return null;
        }
    }
}
