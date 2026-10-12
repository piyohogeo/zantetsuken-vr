using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The katana's model in a running game (DESIGN 19.1.11): a katana component that is enabled puts the model under
    /// its root and stops the root's own parts being drawn, and a camera draws the sword -- its picture is saved for
    /// the record.
    /// </summary>
    public sealed partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator KatanaModel_AKatanaThatIsEnabled_ShowsTheModelInPlaceOfItsRootsParts_AndACameraDrawsIt()
        {
            GameObject root = TrackActor(new GameObject("Katana"));
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "Blade";
            box.transform.SetParent(root.transform, false);
            box.transform.localPosition = new Vector3(0f, 0.004f, 0.45f);
            box.transform.localScale = new Vector3(0.01f, 0.026f, 0.9f);
            Object.Destroy(box.GetComponent<Collider>());

            GameObject rig = TrackActor(new GameObject("Katana Rig"));
            rig.SetActive(false);
            SandboxRightHandKatana katana = rig.AddComponent<SandboxRightHandKatana>();
            katana.LiveInputEnabled = false;
            katana.Katana = root.transform;
            rig.SetActive(true);
            yield return null;

            Transform model = root.transform.Find(SandboxKatanaModel.InstanceName);
            Assert.That(model, Is.Not.Null, "the enabled katana put the model under its root");
            Assert.That(box.GetComponent<MeshRenderer>().enabled, Is.False, "and its root's own part is not drawn");
            Assert.That(model.GetComponentsInChildren<MeshRenderer>(true).Length, Is.EqualTo(3));

            // The picture: the sword laid across the view (its blade along +X, its flat side to the camera, its edge
            // down), lit from the camera's side.
            katana.enabled = false;
            root.SetActive(true);
            root.transform.SetPositionAndRotation(new Vector3(500f, 20f, 500f), Quaternion.Euler(0f, 90f, 0f));
            var target = Track(new RenderTexture(1280, 480, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 });
            target.Create();
            Camera camera = TrackActor(new GameObject("Katana Picture Camera")).AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.16f, 0.18f, 0.22f, 1f);
            camera.orthographic = true;
            camera.orthographicSize = 0.21f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 5f;
            camera.targetTexture = target;
            Vector3 middle = root.transform.TransformPoint(new Vector3(0f, 0f, 0.245f));
            camera.transform.SetPositionAndRotation(middle + new Vector3(0f, 0f, -1.5f), Quaternion.identity);
            Light light = TrackActor(new GameObject("Katana Picture Light")).AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.6f;
            light.transform.rotation = Quaternion.Euler(35f, 20f, 0f);
            yield return null;
            yield return null;
            yield return null;

            Color32[] pixels = ReadGpuCullTarget(target);
            int drawn = 0, left = target.width, right = -1;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                if (Mathf.Abs(p.r - 41) + Mathf.Abs(p.g - 46) + Mathf.Abs(p.b - 56) <= 18) continue;
                drawn++;
                int x = i % target.width;
                left = Mathf.Min(left, x);
                right = Mathf.Max(right, x);
            }

            string folder = System.IO.Path.Combine(Application.temporaryCachePath, "katana-model-pictures");
            System.IO.Directory.CreateDirectory(folder);
            var picture = Track(new Texture2D(target.width, target.height, TextureFormat.RGBA32, false));
            picture.SetPixels32(pixels);
            picture.Apply(false);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "katana.png"), picture.EncodeToPNG());
            float metresAPixel = 2f * camera.orthographicSize * camera.aspect / target.width;
            TestContext.Out.WriteLine("katana picture: " + target.width + " x " + target.height + ", drawn pixels " + drawn + ", from x " + left + " to x " + right
                + " = " + ((right - left + 1) * metresAPixel).ToString("F3") + " m across; on " + SystemInfo.graphicsDeviceType + "; in " + folder);
            Assert.That(drawn, Is.GreaterThan(4000), "the sword is in the picture");
            Assert.That((right - left + 1) * metresAPixel, Is.EqualTo(1.01f).Within(0.03f), "from the grip's end to the tip: the sword's whole length, about 1.01 m");
        }
    }
}
