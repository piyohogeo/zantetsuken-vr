using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.EditorTools.Quest;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The Quest builds' VP Mesh Surface copies, one per source material AND motion (2026-10-19): a skin's copy takes
    /// URP's object motion, a Mesh's the camera's alone, and a source shared by both gets two copies, so that the skin's
    /// change never reaches the Mesh. No asset is written (an empty root keeps the copies in memory).
    /// </summary>
    public sealed class QuestSurfaceMotionMaterialsTests
    {
        private readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _made) if (o != null) Object.DestroyImmediate(o);
            _made.Clear();
        }

        private Material Source(string name)
        {
            Shader shader = Shader.Find(QuestSurfaceMotionMaterials.SurfaceShaderName);
            Assert.That(shader, Is.Not.Null, "the VP Mesh Surface shader");
            var material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", new Color(0.25f, 0.5f, 0.75f, 1f));
            _made.Add(material);
            return material;
        }

        private T Made<T>(T o) where T : Object { _made.Add(o); return o; }

        [Test]
        public void ASourceSharedByAMeshAndASkin_GetsTwoCopies_EachWithItsMotion_TheSourceUntouched()
        {
            Material source = Source("shared");
            source.EnableKeyword(QuestSurfaceMotionMaterials.CameraOnlyKeyword);   // whatever the source has, each copy gets its own
            var materials = new QuestSurfaceMotionMaterials(string.Empty, "Surface-", "Skin-Object-");
            Material mesh = Made(materials.CopyOf(source, QuestSurfaceMotion.CameraOnly));
            Material skin = Made(materials.CopyOf(source, QuestSurfaceMotion.Object));

            Assert.That(mesh, Is.Not.SameAs(skin), "two copies");
            Assert.That(mesh, Is.Not.SameAs(source)); Assert.That(skin, Is.Not.SameAs(source));
            Assert.That(mesh.IsKeywordEnabled(QuestSurfaceMotionMaterials.CameraOnlyKeyword), Is.True, "the Mesh's copy: camera-only");
            Assert.That(skin.IsKeywordEnabled(QuestSurfaceMotionMaterials.CameraOnlyKeyword), Is.False, "the skin's copy: object motion");
            Assert.That(source.IsKeywordEnabled(QuestSurfaceMotionMaterials.CameraOnlyKeyword), Is.True, "the source is not changed");
            Assert.That(materials.CameraOnlyCount, Is.EqualTo(1)); Assert.That(materials.ObjectCount, Is.EqualTo(1));
            // Asked again: the same copies, none more.
            Assert.That(materials.CopyOf(source, QuestSurfaceMotion.CameraOnly), Is.SameAs(mesh));
            Assert.That(materials.CopyOf(source, QuestSurfaceMotion.Object), Is.SameAs(skin));
            Assert.That(materials.CameraOnlyCount + materials.ObjectCount, Is.EqualTo(2));
        }

        [Test]
        public void TheCopies_DifferOnlyInTheMotionKeyword()
        {
            Material source = Source("coloured");
            source.SetFloat("_VpUsePaletteAtlas", 0f);
            var materials = new QuestSurfaceMotionMaterials(string.Empty, "Surface-", "Skin-Object-");
            Material mesh = Made(materials.CopyOf(source, QuestSurfaceMotion.CameraOnly));
            Material skin = Made(materials.CopyOf(source, QuestSurfaceMotion.Object));
            foreach (Material copy in new[] { mesh, skin })
            {
                Assert.That(copy.shader, Is.SameAs(source.shader));
                Assert.That(copy.GetColor("_BaseColor"), Is.EqualTo(source.GetColor("_BaseColor")));
                Assert.That(copy.GetFloat("_VpUsePaletteAtlas"), Is.EqualTo(0f));
                Assert.That(copy.renderQueue, Is.EqualTo(source.renderQueue));
                for (int pass = 0; pass < copy.passCount; pass++)
                    Assert.That(copy.GetShaderPassEnabled(copy.GetPassName(pass)), Is.EqualTo(source.GetShaderPassEnabled(source.GetPassName(pass))), copy.GetPassName(pass));
            }

            var meshKeywords = new HashSet<string>(mesh.shaderKeywords); meshKeywords.Remove(QuestSurfaceMotionMaterials.CameraOnlyKeyword);
            var skinKeywords = new HashSet<string>(skin.shaderKeywords);
            Assert.That(meshKeywords.SetEquals(skinKeywords), Is.True, "no keyword but the motion one differs");
        }

        [Test]
        public void TheMotion_IsObjectForASkin_AndTheCamerasForAnythingElse()
        {
            var skinObject = Made(new GameObject("skin"));
            var meshObject = Made(new GameObject("mesh"));
            Assert.That(QuestSurfaceMotionMaterials.MotionOf(skinObject.AddComponent<SkinnedMeshRenderer>()), Is.EqualTo(QuestSurfaceMotion.Object));
            Assert.That(QuestSurfaceMotionMaterials.MotionOf(meshObject.AddComponent<MeshRenderer>()), Is.EqualTo(QuestSurfaceMotion.CameraOnly));
        }

        [Test]
        public void TheSkinHistory_IsObjectMotionWithSkinnedVectors_AndWhatItHadBeforeIsCounted()
        {
            var before = new Dictionary<string, int>();
            var a = Made(new GameObject("a")).AddComponent<SkinnedMeshRenderer>();
            var b = Made(new GameObject("b")).AddComponent<SkinnedMeshRenderer>();
            var c = Made(new GameObject("c")).AddComponent<SkinnedMeshRenderer>();
            a.motionVectorGenerationMode = MotionVectorGenerationMode.Camera; a.skinnedMotionVectors = false;
            b.motionVectorGenerationMode = MotionVectorGenerationMode.Camera; b.skinnedMotionVectors = false;
            c.motionVectorGenerationMode = MotionVectorGenerationMode.Object; c.skinnedMotionVectors = true;
            foreach (var skin in new[] { a, b, c }) QuestSurfaceMotionMaterials.EnableSkinHistory(skin, before);

            foreach (var skin in new[] { a, b, c })
            {
                Assert.That(skin.motionVectorGenerationMode, Is.EqualTo(MotionVectorGenerationMode.Object));
                Assert.That(skin.skinnedMotionVectors, Is.True);
            }

            Assert.That(before["mode=Camera/skinnedMotionVectors=False"], Is.EqualTo(2));
            Assert.That(before["mode=Object/skinnedMotionVectors=True"], Is.EqualTo(1));
            Assert.That(QuestSurfaceMotionMaterials.Describe(before), Is.EqualTo("2 mode=Camera/skinnedMotionVectors=False, 1 mode=Object/skinnedMotionVectors=True"));
            Assert.That(QuestSurfaceMotionMaterials.Describe(new Dictionary<string, int>()), Is.EqualTo("none"));
        }

        private static bool SkinMotionHistoryOf(Zantetsu.Sandbox.SandboxNpcCharacter npc) =>
            new UnityEditor.SerializedObject(npc).FindProperty("skinMotionHistory").boolValue;

        /// <summary>
        /// The builds' setting reaches the crowd's spares (TL 2026-10-19): it is set on every character of the scene,
        /// an inactive template's included, and a copy made of the template -- as the crowd makes its spare slots,
        /// Instantiate of the template -- carries it. (The copy's drawn skin is the PlayMode case's.)
        /// </summary>
        [Test]
        public void TheBuildsSkinHistorySetting_IsOnEveryCharacter_TheTemplatesToo_AndACopyOfTheTemplateCarriesIt()
        {
            int others = Object.FindObjectsByType<Zantetsu.Sandbox.SandboxNpcCharacter>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            var template = Made(new GameObject("crowd template"));
            template.SetActive(false);
            var inTemplate = Made(new GameObject("template character"));
            inTemplate.transform.SetParent(template.transform, false);
            var templateCharacter = inTemplate.AddComponent<Zantetsu.Sandbox.SandboxNpcCharacter>();
            var standing = Made(new GameObject("standing character")).AddComponent<Zantetsu.Sandbox.SandboxNpcCharacter>();
            Assert.That(SkinMotionHistoryOf(templateCharacter), Is.False, "off until the build sets it");
            Assert.That(SkinMotionHistoryOf(standing), Is.False);

            Assert.That(QuestSurfaceMotionMaterials.EnableCharacterSkinHistory(), Is.EqualTo(others + 2), "both characters, the inactive template's included");
            Assert.That(SkinMotionHistoryOf(templateCharacter), Is.True);
            Assert.That(SkinMotionHistoryOf(standing), Is.True);

            var spare = Made(Object.Instantiate(template));
            var spareCharacter = spare.GetComponentInChildren<Zantetsu.Sandbox.SandboxNpcCharacter>(true);
            Assert.That(spareCharacter, Is.Not.Null.And.Not.SameAs(templateCharacter));
            Assert.That(SkinMotionHistoryOf(spareCharacter), Is.True, "the spare made from the template carries it");
        }
    }
}
