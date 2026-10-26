#if ZANTETSU_ANDROID_FLAT && UNITY_ANDROID
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>Explicit, bounded phone diagnosis. Observes the world's public counters without changing its draws.</summary>
    public sealed class AndroidFlatCutDrawProbe : MonoBehaviour
    {
        public const string Argument = "-zantetsuVpWorldProbe";
        public const string ReadCullArgument = "-zantetsuVpWorldProbeReadCull";
        private const string Prefix = "VP WORLD PROBE: ";
        private const int SampleLimit = 30;
        private const double DeadlineSeconds = 60;
        private StreamWriter _writer;
        private bool _allowCullRead, _readCullAttempted;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallIfAsked()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, Argument) < 0) return;
            var host = new GameObject("Android cut drawing diagnosis");
            DontDestroyOnLoad(host);
            var probe = host.AddComponent<AndroidFlatCutDrawProbe>();
            probe._allowCullRead = Array.IndexOf(arguments, ReadCullArgument) >= 0;
        }

        private IEnumerator Start()
        {
            double began = Time.realtimeSinceStartupAsDouble;
            double deadline = began + DeadlineSeconds;
            string path = Path.Combine(Application.persistentDataPath, "vp-world-probe.txt");
            try
            {
                Directory.CreateDirectory(Application.persistentDataPath);
                _writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception error)
            {
                Debug.LogWarning(Prefix + "file unavailable: " + error.GetType().Name + ": " + error.Message);
            }
            Write("started; file=" + path + "; samples<=30; lifetime<=60s; interval=1s; readCull=" + _allowCullRead
                + "; ordinary GPU args can be zero for a command drawn separately by the Clip list");
            CutWorldRoot world = null;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (world == null) world = FindAnyObjectByType<CutWorldRoot>();
                if (world != null && world.IsReady && world.Display != null && !world.Display.IsDisposed) break;
                yield return null;
            }
            if (world == null || !world.IsReady || world.Display == null || world.Display.IsDisposed)
            {
                Finish("world was not ready before the deadline");
                yield break;
            }

            var interval = new WaitForSecondsRealtime(1f);
            int samples = 0;
            while (samples < SampleLimit && Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (world == null || !world.IsReady || world.Display == null || world.Display.IsDisposed)
                {
                    Finish("world ended; samples=" + samples);
                    yield break;
                }
                Write(Sample(world, samples++, began));
                if (_allowCullRead && !_readCullAttempted && world.Display.ClipDrawCount > 0 && Camera.main != null)
                {
                    // This second argument explicitly permits one synchronous read. Ordinary probe launches never
                    // read back any GPU data; counts here exclude instances drawn separately by the Clip list.
                    _readCullAttempted = true;
                    try
                    {
                        bool read = world.Display.TryReadCullCountsForDiagnosis(Camera.main, out int instances, out int body, out int shadow);
                        Write("one-time ordinary selection readback: available=" + read + "; instances=" + instances
                            + "; forwardKept=" + body + "; shadowKept=" + shadow + "; excludes separate Clip draws");
                    }
                    catch (Exception error)
                    {
                        Write("one-time ordinary selection readback failed: " + error.GetType().Name + ": " + error.Message);
                    }
                }
                if (samples < SampleLimit) yield return interval;
            }
            Finish("completed; samples=" + samples + "; readCullAttempted=" + _readCullAttempted);
        }

        private static string Sample(CutWorldRoot world, int sample, double began)
        {
            var display = world.Display;
            var ledger = world.Ledger;
            var drawing = world.GetComponent<CutWorldCameraDrawing>();
            var text = new StringBuilder(1024);
            text.Append("sample=").Append(sample).Append(" elapsed=")
                .Append((Time.realtimeSinceStartupAsDouble - began).ToString("F2", CultureInfo.InvariantCulture))
                .Append(" frame=").Append(Time.frameCount).Append(" route=").Append(world.BodyRoute)
                .Append(" shown=").Append(display.ShownCount).Append(" sides=").Append(display.SideCount)
                .Append(" drawCommands=").Append(display.DrawCommandCount).Append(" drawInstances=").Append(display.DrawInstanceCount)
                .Append(" renderFragments=").Append(display.RenderFragmentCount)
                .Append(" clipRouteCommands=").Append(display.ClipRouteCommandCount).Append(" clipDraws=").Append(display.ClipDrawCount)
                .Append(" clipBodyIssues=").Append(display.ClipBodyIssues).Append(" clipBodyKept=").Append(display.ClipCullBodyKept)
                .Append(" clipArgumentWrites=").Append(display.ClipArgumentWrites)
                .Append(" cullDispatches=").Append(display.CullDispatches)
                .Append(" ledgerFragments=").Append(ledger != null ? ledger.FragmentCount : -1)
                .Append(" liveCutFragments=").Append(ledger != null ? ledger.LiveCutFragmentCount : -1)
                .Append(" cutOperations=").Append(ledger != null ? ledger.OperationCount : -1)
                .Append(" geometryFaults=").Append(world.GeometryFaults).Append(" terminating=").Append(world.TerminationRequested);
            if (drawing == null) text.Append(" drawing=missing");
            else
            {
                text.Append(" drawnFrames=").Append(drawing.DrawnFrames).Append(" emptyFrames=").Append(drawing.FramesWithNothingToDraw)
                    .Append(" lastRefusedFrame=").Append(drawing.LastRefusedFrame)
                    .Append(" cullRefusals=").Append(drawing.CullRefusals).Append(" nativeRefusals=").Append(drawing.NativeRefusals);
                if (drawing.CullRoute != null)
                    text.Append(" cullEnqueued=").Append(drawing.CullRoute.Enqueued)
                        .Append(" cullRecorded=").Append(drawing.CullRoute.Recorded).Append(" cullTargetRefused=").Append(drawing.CullRoute.Refused);
                string native = drawing.DescribeNativeRoute(out bool drewEverything);
                if (native != null)
                    text.Append(" nativeComplete=").Append(drewEverything).Append(" native=").Append(Bounded(native, 512));
            }
            return text.ToString();
        }

        private static string Bounded(string text, int limit)
        {
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            return text.Length <= limit ? text : text.Substring(0, limit);
        }

        private void Write(string text)
        {
            string line = Prefix + Bounded(text, 2048);
            Debug.Log(line);
            if (_writer == null) return;
            try { _writer.WriteLine(line); }
            catch (Exception error)
            {
                Debug.LogWarning(Prefix + "file write failed: " + error.GetType().Name);
                CloseFile();
            }
        }

        private void Finish(string reason)
        {
            Write(reason);
            CloseFile();
            Destroy(gameObject);
        }

        private void OnDestroy() { CloseFile(); }
        private void CloseFile()
        {
            StreamWriter writer = _writer;
            _writer = null;
            if (writer != null)
            {
                try { writer.Dispose(); }
                catch (Exception error) { Debug.LogWarning(Prefix + "file close failed: " + error.GetType().Name); }
            }
        }
    }
}
#endif
