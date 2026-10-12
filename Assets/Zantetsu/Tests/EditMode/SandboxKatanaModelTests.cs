using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The katana's model (DESIGN 19.1.11): the prefab under Resources is the sword of three meshes, it stands in the
    /// katana root's own frame -- the blade's chord along +Z from the origin, the edge toward -Y, the grip behind --
    /// and putting it under a root switches the root's own parts off and adds the model once.
    /// </summary>
    public sealed class SandboxKatanaModelTests
    {
        private readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object made in _made)
            {
                if (made != null)
                {
                    Object.DestroyImmediate(made);
                }
            }

            _made.Clear();
        }

        private GameObject NewModel()
        {
            GameObject prefab = Resources.Load<GameObject>(SandboxKatanaModel.ResourceName);
            Assert.That(prefab, Is.Not.Null, "the katana model's prefab is under Resources as " + SandboxKatanaModel.ResourceName);
            GameObject model = Object.Instantiate(prefab);
            _made.Add(model);
            model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            return model;
        }

        // A part's vertices in the model root's space.
        private static List<Vector3> Points(GameObject model, string part)
        {
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.name.StartsWith(part, System.StringComparison.Ordinal))
                {
                    continue;
                }

                var points = new List<Vector3>();
                Matrix4x4 toRoot = model.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                foreach (Vector3 vertex in filter.sharedMesh.vertices)
                {
                    points.Add(toRoot.MultiplyPoint3x4(vertex));
                }

                return points;
            }

            Assert.Fail("the model has no part named " + part + "...");
            return null;
        }

        private static Bounds BoundsOf(List<Vector3> points)
        {
            var bounds = new Bounds(points[0], Vector3.zero);
            foreach (Vector3 p in points)
            {
                bounds.Encapsulate(p);
            }

            return bounds;
        }

        [Test]
        public void TheModel_IsTheSwordOfThreeMeshes_EachWithItsOwnMaterial()
        {
            GameObject model = NewModel();
            MeshRenderer[] renderers = model.GetComponentsInChildren<MeshRenderer>(true);
            Assert.That(renderers.Length, Is.EqualTo(3), "the blade, the grip and the habaki");
            int triangles = 0;
            var materials = new HashSet<string>();
            foreach (MeshRenderer renderer in renderers)
            {
                Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(mesh, Is.Not.Null, renderer.name);
                triangles += mesh.triangles.Length / 3;
                Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(1), renderer.name);
                Material material = renderer.sharedMaterial;
                Assert.That(material, Is.Not.Null, renderer.name + " has its material");
                Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"), material.name);
                materials.Add(material.name);
                Assert.That(renderer.shadowCastingMode, Is.EqualTo(UnityEngine.Rendering.ShadowCastingMode.On), renderer.name + " casts its shadow, as the boxes did");
                Assert.That(renderer.GetComponent<Collider>(), Is.Null, "the shown katana has no collider");
            }

            Assert.That(triangles, Is.EqualTo(900), "the sword's triangles, as the .blend has them");
            Assert.That(materials, Is.EquivalentTo(new[] { "AikuchiBlade", "AikuchiHabaki", "AikuchiWood" }));
            Assert.That(model.GetComponentsInChildren<Collider>(true), Is.Empty);

            Material blade = System.Array.Find(renderers, r => r.name.StartsWith("Blade", System.StringComparison.Ordinal)).sharedMaterial;
            Assert.That(blade.GetTexture("_BaseMap"), Is.Not.Null, "the steel's colour");
            Assert.That(blade.GetTexture("_BumpMap"), Is.Not.Null, "the brushing's normal");
            Assert.That(blade.GetTexture("_MetallicGlossMap"), Is.Not.Null, "the steel's metallic and smoothness");
            Assert.That(blade.IsKeywordEnabled("_NORMALMAP") && blade.IsKeywordEnabled("_METALLICSPECGLOSSMAP"), Is.True);
        }

        [Test]
        public void TheModel_StandsInTheKatanasFrame_TheBladesChordAlongItsAxis_TheEdgeDown_TheGripBehind()
        {
            GameObject model = NewModel();
            List<Vector3> blade = Points(model, "Blade"), grip = Points(model, "Grip"), habaki = Points(model, "Habaki");
            Bounds bladeBounds = BoundsOf(blade), gripBounds = BoundsOf(grip), habakiBounds = BoundsOf(habaki);
            TestContext.Out.WriteLine("blade " + bladeBounds.min.ToString("F4") + " .. " + bladeBounds.max.ToString("F4")
                + "; grip " + gripBounds.min.ToString("F4") + " .. " + gripBounds.max.ToString("F4") + "; habaki " + habakiBounds.min.ToString("F4") + " .. " + habakiBounds.max.ToString("F4"));

            // +Z along the blade, from the origin: the blade begins at the origin and its tip is on the axis.
            Assert.That(bladeBounds.min.z, Is.EqualTo(0f).Within(0.005f), "the blade's base is at the origin");
            Assert.That(bladeBounds.max.z, Is.EqualTo(0.75f).Within(0.005f), "the blade is 0.75 m from its base to its tip");
            Vector3 tip = blade[0];
            foreach (Vector3 p in blade)
            {
                if (p.z > tip.z) tip = p;
            }

            Assert.That(Mathf.Abs(tip.y), Is.LessThan(0.003f), "the tip is on the katana's axis: the blade's chord is the axis");
            Assert.That(Mathf.Abs(tip.x), Is.LessThan(0.003f));

            // +X the side normal: the sword is thin across X.
            foreach (Bounds each in new[] { bladeBounds, gripBounds, habakiBounds })
            {
                Assert.That(Mathf.Max(Mathf.Abs(each.min.x), Mathf.Abs(each.max.x)), Is.LessThan(0.013f));
            }

            Assert.That(bladeBounds.size.x, Is.LessThan(0.007f), "the blade's thickness is across X");

            // -Y the edge. Two readings that do not depend on each other: the blade's thin side is its lower side, and
            // the curved blade stands out from its chord on the edge's side (a katana's edge is its outer curve).
            float middle = bladeBounds.center.z, slab = 0.05f * bladeBounds.size.z;
            float lowY = float.PositiveInfinity, highY = float.NegativeInfinity;
            foreach (Vector3 p in blade)
            {
                if (Mathf.Abs(p.z - middle) > slab) continue;
                lowY = Mathf.Min(lowY, p.y);
                highY = Mathf.Max(highY, p.y);
            }

            float widthMiddle = 0.5f * (lowY + highY);
            double lower = 0, upper = 0;
            int lowerCount = 0, upperCount = 0;
            foreach (Vector3 p in blade)
            {
                if (Mathf.Abs(p.z - middle) > slab) continue;
                if (p.y < widthMiddle) { lower += Mathf.Abs(p.x); lowerCount++; }
                else { upper += Mathf.Abs(p.x); upperCount++; }
            }

            Assert.That(lowerCount, Is.GreaterThan(3));
            Assert.That(upperCount, Is.GreaterThan(3));
            Assert.That(lower / lowerCount, Is.LessThan(upper / upperCount), "about its middle the blade is thinner on its -Y side: the edge");
            Assert.That(widthMiddle, Is.LessThan(-0.005f), "and about its middle the blade stands below its chord: the curve's outer side is the edge's");
            Assert.That(bladeBounds.min.y, Is.GreaterThan(-0.05f), "within its own curvature of the axis");
            Assert.That(bladeBounds.max.y, Is.LessThan(0.02f));

            // The grip behind the origin, 0.26 m of it, leaning a few degrees from the axis.
            Assert.That(gripBounds.max.z, Is.LessThan(0.01f), "the grip ends where the blade begins");
            Assert.That(gripBounds.min.z, Is.EqualTo(-0.26f).Within(0.01f));
            Assert.That(gripBounds.max.y, Is.LessThan(0.06f));
            Assert.That(habakiBounds.min.z, Is.GreaterThan(-0.01f), "the habaki sits on the blade's base");
            Assert.That(habakiBounds.max.z, Is.LessThan(0.03f));
        }

        [Test]
        public void Apply_SwitchesOffWhatTheRootsOwnPartsDraw_AndPutsTheModelThere_Once_OnTheRootsLayer()
        {
            var root = new GameObject("Katana") { layer = 8 };
            _made.Add(root);
            var parts = new List<MeshRenderer>();
            foreach (string name in new[] { "Handle", "Guard", "Blade", "Edge Stripe", "Cut Sample Point" })
            {
                GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
                part.name = name;
                part.layer = 8;
                part.transform.SetParent(root.transform, false);
                parts.Add(part.GetComponent<MeshRenderer>());
            }

            GameObject model = SandboxKatanaModel.Apply(root.transform);
            Assert.That(model, Is.Not.Null);
            Assert.That(model.name, Is.EqualTo(SandboxKatanaModel.InstanceName));
            Assert.That(model.transform.parent, Is.SameAs(root.transform));
            Assert.That(model.transform.localPosition, Is.EqualTo(Vector3.zero), "the model's root is the katana root's frame");
            Assert.That(Quaternion.Angle(model.transform.localRotation, Quaternion.identity), Is.LessThan(1e-3f));
            Assert.That(model.transform.localScale, Is.EqualTo(Vector3.one));
            foreach (MeshRenderer part in parts)
            {
                Assert.That(part.enabled, Is.False, part.name + " is not drawn");
                Assert.That(part.gameObject.activeSelf, Is.True, part.name + " stays in the scene");
            }

            MeshRenderer[] shown = model.GetComponentsInChildren<MeshRenderer>(true);
            Assert.That(shown.Length, Is.EqualTo(3));
            foreach (MeshRenderer renderer in shown)
            {
                Assert.That(renderer.enabled, Is.True, renderer.name);
                Assert.That(renderer.gameObject.layer, Is.EqualTo(8), renderer.name + " is on the root's layer");
            }

            // A second time: the same model, nothing more, and the model's own renderers are not switched off.
            Assert.That(SandboxKatanaModel.Apply(root.transform), Is.SameAs(model));
            Assert.That(root.transform.childCount, Is.EqualTo(parts.Count + 1));
            foreach (MeshRenderer renderer in shown)
            {
                Assert.That(renderer.enabled, Is.True, renderer.name);
            }
        }

        [Test]
        public void Apply_WithNoRoot_DoesNothing()
        {
            Assert.That(SandboxKatanaModel.Apply(null), Is.Null);
        }

        [Test]
        public void TheKatanaComponent_InAnEditedScene_LeavesItsRootAsItIs()
        {
            // The model is a running game's: an edit-mode katana keeps the parts its scene saved (the scene's own tests
            // read them).
            var rig = new GameObject("Rig");
            _made.Add(rig);
            var root = new GameObject("Katana");
            _made.Add(root);
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = "Blade";
            part.transform.SetParent(root.transform, false);
            SandboxRightHandKatana katana = rig.AddComponent<SandboxRightHandKatana>();
            katana.Katana = root.transform;
            Assert.That(Application.isPlaying, Is.False);
            Assert.That(root.transform.Find(SandboxKatanaModel.InstanceName), Is.Null);
            Assert.That(part.GetComponent<MeshRenderer>().enabled, Is.True);
        }
    }
}
