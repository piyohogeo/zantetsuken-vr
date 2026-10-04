using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// SSAO against the VP surfaces, generation and reception apart (TL, 2026-10-03), limited: one 2 m box standing on a URP
    /// Lit ground, seen from the front and above with the light's shadows off, so that only the screen-space ambient
    /// occlusion darkens anything. The box drawn four ways in turn -- none, a URP Lit cube (the control: SSAO is on and
    /// visible), a cube of "Zantetsu/VP Mesh Surface" (an uncut object), the cut world's display of the same box (a cut
    /// one) -- and two bands read: the ground just in front of the box's foot against the same pixels without the box
    /// (generation: the box is in the depth and normals the occlusion is computed from) and the box's own face just above
    /// its foot against higher up the same face (reception: the box's shading takes the occlusion). The project's renderer
    /// is used as it is (PC_Renderer: SSAO from DepthNormals, radius 0.3 m, intensity 0.4); nothing of it is changed.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private ShadowStage NewSsaoStage()
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            Assert.That(lit, Is.Not.Null);
            Material groundSurface = Track(new Material(lit) { name = "ssao ground" });
            groundSurface.SetColor("_BaseColor", Color.white);
            GameObject ground = TrackActor(GameObject.CreatePrimitive(PrimitiveType.Plane));
            Object.Destroy(ground.GetComponent<Collider>());
            ground.GetComponent<MeshRenderer>().sharedMaterial = groundSurface;

            Light light = TrackActor(new GameObject("SSAO Stage Sun")).AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
            light.intensity = 1f;
            light.transform.rotation = Quaternion.Euler(50f, 20f, 0f);

            var target = Track(new RenderTexture(ShadowImageSize, ShadowImageSize, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
            target.Create();
            Camera camera = TrackActor(new GameObject("SSAO Stage Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.fieldOfView = 50f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 40f;
            camera.targetTexture = target;
            camera.transform.position = new Vector3(0f, 1.6f, -3.6f);
            camera.transform.LookAt(new Vector3(0f, 0.4f, -1f));
            return new ShadowStage { camera = camera, light = light, target = target };
        }

        // The mean grey of the image at the pixels the given world points fall on.
        private static float Band(Camera camera, Color32[] image, IEnumerable<Vector3> points)
        {
            float sum = 0f;
            int n = 0;
            foreach (Vector3 p in points)
            {
                Vector3 s = camera.WorldToScreenPoint(p);
                int x = Mathf.FloorToInt(s.x), y = Mathf.FloorToInt(s.y);
                if (s.z <= 0f || x < 0 || y < 0 || x >= ShadowImageSize || y >= ShadowImageSize) continue;
                Color32 c = image[y * ShadowImageSize + x];
                sum += (c.r + c.g + c.b) / 3f;
                n++;
            }

            Assert.That(n, Is.GreaterThan(10), "the band is in the image");
            return sum / n;
        }

        private static IEnumerable<Vector3> GroundBand() =>
            from x in Enumerable.Range(-8, 17) from d in new[] { 0.04f, 0.08f, 0.12f, 0.16f } select new Vector3(x * 0.1f, 0f, -1f - d);

        private static IEnumerable<Vector3> FaceBand(float low, float high) =>
            from x in Enumerable.Range(-8, 17) from h in Enumerable.Range(0, 4) select new Vector3(x * 0.1f, low + (high - low) * h / 3f, -1.001f);

        [UnityTest]
        public IEnumerator Ssao_TheLitControlOccludesAndIsOccluded_TheVpSurfacesNeither()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            ShadowStage stage = NewSsaoStage();
            var results = new Dictionary<string, (float ground, float faceNear, float faceFar)>();
            Color32[] image = null;

            yield return DrawStageAfterTheCollection(root, stage, LightShadows.None, d => image = d);
            float empty = Band(stage.camera, image, GroundBand());

            GameObject Cube(Material m)
            {
                GameObject c = TrackActor(GameObject.CreatePrimitive(PrimitiveType.Cube));
                Object.Destroy(c.GetComponent<Collider>());
                c.transform.SetPositionAndRotation(new Vector3(0f, 1f, 0f), Quaternion.identity);
                c.transform.localScale = Vector3.one * 2f;
                c.GetComponent<MeshRenderer>().sharedMaterial = m;
                return c;
            }

            void Read(string what)
            {
                results[what] = (Band(stage.camera, image, GroundBand()), Band(stage.camera, image, FaceBand(0.04f, 0.16f)), Band(stage.camera, image, FaceBand(0.7f, 0.9f)));
            }

            Material litBox = Track(new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "ssao lit box" });
            litBox.SetColor("_BaseColor", new Color(0.7f, 0.7f, 0.7f));
            GameObject lit = Cube(litBox);
            yield return DrawStageAfterTheCollection(root, stage, LightShadows.None, d => image = d);
            Read("URP Lit cube (control)");
            Object.Destroy(lit);
            yield return null;

            Material vpSurface = Track(new Material(Shader.Find("Zantetsu/VP Mesh Surface")) { name = "ssao vp mesh surface" });
            vpSurface.SetFloat("_VpUsePaletteAtlas", 0f);
            vpSurface.SetColor("_BaseColor", new Color(0.7f, 0.7f, 0.7f));
            GameObject mesh = Cube(vpSurface);
            yield return DrawStageAfterTheCollection(root, stage, LightShadows.None, d => image = d);
            Read("VP Mesh Surface cube (uncut)");
            Object.Destroy(mesh);
            yield return null;

            AddBody(root, new Vector3(0f, 1f, 0f));
            yield return null;
            yield return DrawStageAfterTheCollection(root, stage, LightShadows.None, d => image = d);
            Read("the cut world's display (cut)");

            TestContext.Out.WriteLine("ground band without a box: " + empty.ToString("F2"));
            foreach (var r in results)
            {
                TestContext.Out.WriteLine(r.Key + ": ground band " + r.Value.ground.ToString("F2") + " (" + (r.Value.ground / empty).ToString("F3") + " of without), face near the foot "
                    + r.Value.faceNear.ToString("F2") + " against higher up " + r.Value.faceFar.ToString("F2") + " (" + (r.Value.faceNear / r.Value.faceFar).ToString("F3") + ")");
            }

            var control = results["URP Lit cube (control)"];
            if (!(control.ground / empty < 0.97f && control.faceNear / control.faceFar < 0.97f))
            {
                Assert.Inconclusive("the control shows no occlusion: SSAO is not visible in this environment");
            }

            foreach (string vp in new[] { "VP Mesh Surface cube (uncut)", "the cut world's display (cut)" })
            {
                Assert.That(results[vp].ground / empty, Is.GreaterThan(0.99f), vp + ": not in the depth and normals the occlusion is computed from (generation)");
                Assert.That(results[vp].faceNear / results[vp].faceFar, Is.GreaterThan(0.99f), vp + ": its shading does not take the occlusion (reception)");
            }

            yield return EndWorld(root);
        }
    }
}
