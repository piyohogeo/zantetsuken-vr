using System.Globalization;
using System.Text;
using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Development overlay that shows, in the Game View, what the sandbox
    /// katana's slash pipeline holds right now: tracking, the stroke, the plane
    /// and frame candidates, and the live waves.
    ///
    /// It only reads gameplay state. The displayed text is rebuilt from
    /// <see cref="SandboxRightHandKatana"/> each time it is drawn, and nothing
    /// in gameplay ever reads anything back from here. It is not a trace, a
    /// log, or a saved format, and it has no say in any decision.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SandboxSlashDiagnosticsOverlay : MonoBehaviour
    {
        [Tooltip("The katana component whose slash state is shown.")]
        [SerializeField] private SandboxRightHandKatana katana;

        [SerializeField] private bool visible = true;

        // Reused across draws: OnGUI runs more than once per frame.
        private readonly StringBuilder text = new StringBuilder(768);
        private GUIStyle style;

        private void OnGUI()
        {
            if (!visible)
            {
                return;
            }

            text.Clear();
            AppendDiagnostics(text, katana);

            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = 14,
                    richText = false,
                    wordWrap = false,
                };
            }

            GUILayout.BeginArea(new Rect(10f, 10f, 640f, Mathf.Max(0f, Screen.height - 20f)));
            GUILayout.Label(text.ToString(), style);
            GUILayout.EndArea();
        }

        /// <summary>
        /// Writes the diagnostics text for one katana. Tolerates a missing
        /// katana, and calls nothing on it that changes state.
        /// </summary>
        internal static void AppendDiagnostics(StringBuilder text, SandboxRightHandKatana katana)
        {
            text.Append("Slash diagnostics (live)\n");
            if (katana == null)
            {
                text.Append("No SandboxRightHandKatana assigned.\n");
                return;
            }

            Transform katanaTransform = katana.Katana;
            text.Append("Katana        ")
                .Append(katanaTransform == null ? "unassigned" : katanaTransform.gameObject.activeSelf ? "shown" : "hidden")
                .Append('\n');

            text.Append("Poses         recorded ").Append(katana.RecordedPoseCount)
                .Append("  accepted ").Append(katana.AcceptedSampleCount).Append('\n');
            text.Append("Latch         ").Append(katana.IsLatchReady ? "ready" : "waiting").Append('\n');
            text.Append("Stroke begin  ").Append(katana.TryGetStrokeBeginSample(out _) ? "yes" : "no").Append('\n');
            text.Append("Plane         ").Append(katana.TryGetSourceSlashPlaneCandidate(out _) ? "yes" : "no").Append('\n');

            text.Append("Frame         ");
            if (katana.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out float frameSpan))
            {
                text.Append("yes  span ");
                AppendMetres(text, frameSpan);
                text.Append('\n');
            }
            else
            {
                text.Append("no\n");
            }

            int waveCount = katana.WaveCount;
            text.Append("Waves         ").Append(waveCount).Append(" / ").Append(SandboxSlashWaveStore.Capacity).Append('\n');
            for (int i = 0; i < waveCount; i++)
            {
                if (!katana.TryGetWave(i, out double latchedAt, out _, out _, out _, out _, out float acceptedSpan,
                        out _, out _, out Vector3 currentStart, out Vector3 currentEnd))
                {
                    continue;
                }

                text.Append("  #").Append(i).Append("  latched ");
                AppendSeconds(text, latchedAt);
                text.Append("  span ");
                AppendMetres(text, acceptedSpan);
                text.Append("  ");
                if (katana.TryGetWaveSpanClose(i, out double closedAt, out _, out _))
                {
                    text.Append("closed at ");
                    AppendSeconds(text, closedAt);
                }
                else
                {
                    text.Append("open");
                }

                text.Append("\n      A ");
                AppendPoint(text, currentStart);
                text.Append("  B ");
                AppendPoint(text, currentEnd);
                text.Append('\n');
            }
        }

        private static void AppendSeconds(StringBuilder text, double seconds)
        {
            text.Append(seconds.ToString("F3", CultureInfo.InvariantCulture)).Append(" s");
        }

        private static void AppendMetres(StringBuilder text, float metres)
        {
            text.Append(metres.ToString("F3", CultureInfo.InvariantCulture)).Append(" m");
        }

        private static void AppendPoint(StringBuilder text, Vector3 point)
        {
            text.Append('(')
                .Append(point.x.ToString("F2", CultureInfo.InvariantCulture)).Append(", ")
                .Append(point.y.ToString("F2", CultureInfo.InvariantCulture)).Append(", ")
                .Append(point.z.ToString("F2", CultureInfo.InvariantCulture)).Append(')');
        }
    }
}
