using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A scenario's own render pipeline (ScenarioRenderPipeline, 2026-10-03: the city walk draws without SSAO): its asset is
    /// the current quality level's pipeline while the component is enabled, and what was there before is put back when it
    /// is disabled or destroyed (the scenario's scene ending); the shared default is never changed, and a component without
    /// an asset changes nothing.
    /// </summary>
    public sealed class ScenarioRenderPipelinePlayModeTests
    {
        [UnityTest]
        public IEnumerator ScenarioRendering_InEffectWhileEnabled_PutBackWhenDisabledOrDestroyed_TheSharedSettingUntouched()
        {
            RenderPipelineAsset before = QualitySettings.renderPipeline;
            RenderPipelineAsset shared = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset basis = before != null ? before : shared;
            if (basis == null) Assert.Ignore("no render pipeline asset in this project's settings");
            RenderPipelineAsset scenario = Object.Instantiate(basis);
            scenario.name = "scenario copy";
            var host = new GameObject("scenario rendering");
            host.SetActive(false);
            try
            {
                var component = host.AddComponent<ScenarioRenderPipeline>();
                component.pipeline = scenario;
                Assert.That(component.Applied, Is.False, "nothing before it is enabled");
                Assert.That(QualitySettings.renderPipeline, Is.SameAs(before));

                host.SetActive(true);
                Assert.That(component.Applied, Is.True);
                Assert.That(QualitySettings.renderPipeline, Is.SameAs(scenario), "the scenario's asset is the quality level's pipeline");
                Assert.That(GraphicsSettings.defaultRenderPipeline, Is.SameAs(shared), "the shared default untouched");
                yield return null;
                Assert.That(GraphicsSettings.currentRenderPipeline, Is.SameAs(scenario), "in effect");

                component.enabled = false;
                Assert.That(component.Applied, Is.False);
                Assert.That(QualitySettings.renderPipeline, Is.SameAs(before), "put back when disabled");
                component.enabled = true;
                Assert.That(QualitySettings.renderPipeline, Is.SameAs(scenario), "in effect again when enabled again");

                Object.Destroy(host);
                host = null;
                yield return null;
                Assert.That(QualitySettings.renderPipeline, Is.SameAs(before), "put back when destroyed (the scenario's scene ending)");
                Assert.That(GraphicsSettings.defaultRenderPipeline, Is.SameAs(shared), "the shared default still untouched");

                var empty = new GameObject("scenario rendering without an asset").AddComponent<ScenarioRenderPipeline>();
                Assert.That(empty.Applied, Is.False, "without an asset nothing is applied");
                Assert.That(QualitySettings.renderPipeline, Is.SameAs(before));
                Object.Destroy(empty.gameObject);
                yield return null;
                Assert.That(QualitySettings.renderPipeline, Is.SameAs(before));
            }
            finally
            {
                if (host != null) Object.Destroy(host);
                QualitySettings.renderPipeline = before;
                Object.Destroy(scenario);
            }
        }
    }
}
