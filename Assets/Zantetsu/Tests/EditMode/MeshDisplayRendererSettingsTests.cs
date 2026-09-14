using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The renderer setup Phase 0.91 adopted for Unity Mesh display: the PC
    /// renderer draws with the Forward path, and the PC pipeline keeps the GPU
    /// Resident Drawer off. Read through serialized properties so the test
    /// assembly needs no URP reference.
    /// </summary>
    public class MeshDisplayRendererSettingsTests
    {
        private const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        private const string PipelinePath = "Assets/Settings/PC_RPAsset.asset";

        // UnityEngine.Rendering.Universal.RenderingMode.Forward
        private const int ForwardRenderingMode = 0;

        // UnityEngine.Rendering.GPUResidentDrawerMode.Disabled
        private const int ResidentDrawerDisabled = 0;

        private static SerializedProperty Property(string assetPath, string propertyName)
        {
            Object asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            Assert.That(asset, Is.Not.Null, assetPath + " exists");
            SerializedProperty property = new SerializedObject(asset).FindProperty(propertyName);
            Assert.That(property, Is.Not.Null, assetPath + " has " + propertyName);
            return property;
        }

        [Test]
        public void PcRenderer_UsesTheForwardRenderingPath()
        {
            Assert.That(Property(RendererPath, "m_RenderingMode").intValue, Is.EqualTo(ForwardRenderingMode));
        }

        [Test]
        public void PcPipeline_KeepsTheGpuResidentDrawerAndItsOcclusionCullingOff()
        {
            Assert.That(Property(PipelinePath, "m_GPUResidentDrawerMode").intValue, Is.EqualTo(ResidentDrawerDisabled));
            Assert.That(Property(PipelinePath, "m_GPUResidentDrawerEnableOcclusionCullingInCameras").boolValue, Is.False);
        }
    }
}
