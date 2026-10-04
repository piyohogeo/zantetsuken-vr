using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A scenario's own render pipeline settings for as long as its scene runs (2026-10-03: the city walk draws without
    /// SSAO, which neither the VP display nor the VP mesh surface takes part in, so that nothing keeps an occlusion the cut
    /// pieces cannot have). The shared settings are not changed: the asset given here -- the scenario's copy -- is set as the
    /// current quality level's pipeline when this is enabled, and what was there before is put back when it is disabled.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class ScenarioRenderPipeline : MonoBehaviour
    {
        public RenderPipelineAsset pipeline;

        private RenderPipelineAsset _before;
        private bool _applied;

        /// <summary>Whether the scenario's pipeline is the current one because of this.</summary>
        public bool Applied => _applied;

        private void OnEnable() => Apply();

        private void OnDisable() => Restore();

        internal void Apply()
        {
            if (_applied || pipeline == null) return;
            _before = QualitySettings.renderPipeline;
            QualitySettings.renderPipeline = pipeline;
            _applied = true;
            Debug.Log("SCENARIO RENDERING: " + pipeline.name + " in place of " + (_before != null ? _before.name : "the default pipeline") + " while " + gameObject.scene.name + " runs");
            Debug.Log("SCENARIO RENDERING: in effect " + Describe(GraphicsSettings.currentRenderPipeline));
        }

        // The pipeline in effect and, from its own serialized fields (this assembly does not reference the pipeline's, and
        // serialized fields stay in a stripped Player), each renderer's features with their active flags: what is drawn
        // with, read back at run time rather than assumed from the asset.
        internal static string Describe(RenderPipelineAsset asset)
        {
            if (asset == null) return "no render pipeline asset (the built-in renderer)";
            var text = new StringBuilder(asset.name);
            object renderers = Field(asset, "m_RendererDataList");
            object chosen = Field(asset, "m_DefaultRendererIndex");
            text.Append("; default renderer ").Append(chosen ?? "?");
            if (!(renderers is IEnumerable list)) return text.Append("; renderers not readable").ToString();
            int i = 0;
            foreach (object data in list)
            {
                text.Append("; renderer ").Append(i++).Append(' ').Append(data is Object o && o != null ? o.name : "none");
                if (data == null) continue;
                if (!(Field(data, "m_RendererFeatures") is IEnumerable features)) { text.Append(" (features not readable)"); continue; }
                string each = string.Join(", ", features.Cast<object>().Select(f => f is Object fo && fo != null
                    ? fo.name + "=" + (Field(fo, "m_Active") is bool on ? (on ? "active" : "inactive") : "?") : "none"));
                text.Append(" features [").Append(each).Append(']');
            }

            return text.ToString();
        }

        private static object Field(object owner, string name)
        {
            for (System.Type t = owner.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(owner);
            }

            return null;
        }

        internal void Restore()
        {
            if (!_applied) return;
            QualitySettings.renderPipeline = _before;
            _applied = false;
            _before = null;
        }
    }
}
