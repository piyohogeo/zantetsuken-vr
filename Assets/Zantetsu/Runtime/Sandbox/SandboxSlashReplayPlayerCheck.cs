using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A Player check of the product SlashWave Core in Sandbox.unity (Phase 4.50), on when the Player is started with
    /// <see cref="Argument"/> and a directory for its pictures. It loads recorded device grip poses and views from a
    /// capture's input.csv (<see cref="InputArgument"/>, rows from <see cref="StartArgument"/>, as many as the Recorder
    /// holds) into the sandbox Recorder and replays them: the Recorder's own Update feeds one sample per frame into the
    /// katana's Update path, which is the product core's entrance. It writes every latch -- SlashId, the update, the
    /// input row and its recorded time, the replay time and the wave's latch time -- takes pictures while waves fly and
    /// after they have expired, and ends the Player with 0 when the latches are the expected rows
    /// (<see cref="ExpectArgument"/>), their SlashIds run from 1, and every wave has gone by the end.
    /// <para>
    /// What is replayed is the gesture input -- tracking-space grip poses and views -- not a world path of the player.
    /// </para>
    /// </summary>
    public static class SandboxSlashReplayPlayerCheck
    {
        public const string Argument = "-zantetsuSlashReplay";
        public const string InputArgument = "-zantetsuSlashInput";
        public const string StartArgument = "-zantetsuSlashStart";
        public const string ExpectArgument = "-zantetsuSlashExpect";
        public const string Prefix = "SLASH REPLAY: ";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfAsked()
        {
            string directory = Value(Argument);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var host = new GameObject("Slash replay player check");
            UnityEngine.Object.DontDestroyOnLoad(host);
            Walk walk = host.AddComponent<Walk>();
            walk.directory = directory;
            walk.input = Value(InputArgument);
            walk.start = int.TryParse(Value(StartArgument), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : 0;
            walk.expect = Value(ExpectArgument);
        }

        private static string Value(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[i + 1];
                }
            }

            return null;
        }

        private static void Log(string line)
        {
            Debug.Log(Prefix + line);
        }

        [DefaultExecutionOrder(400)]
        private sealed class Walk : MonoBehaviour
        {
            internal string directory;
            internal string input;
            internal int start;
            internal string expect;

            private readonly CultureInfo _inv = CultureInfo.InvariantCulture;
            private readonly List<double> _rowTimes = new List<double>();
            private readonly List<int> _latchRows = new List<int>();
            private readonly List<long> _latchIds = new List<long>();
            private int _failures;

            private IEnumerator Start()
            {
                Directory.CreateDirectory(directory);
                yield return null;
                SandboxRightHandKatana katana = FindAnyObjectByType<SandboxRightHandKatana>();
                SandboxSlashPoseRecorder recorder = FindAnyObjectByType<SandboxSlashPoseRecorder>();
                if (katana == null || recorder == null || string.IsNullOrEmpty(input) || !File.Exists(input))
                {
                    Log("FAILED: no katana, no recorder, or no input file: " + input);
                    Application.Quit(12);
                    yield break;
                }

                // The rows, as the capture wrote them: tracking-space grip pose and view.
                string[] lines = File.ReadAllLines(input);
                recorder.BeginRecording();
                int loaded = 0;
                for (int i = start + 1; i < lines.Length && loaded < SandboxSlashPoseRecorder.Capacity; i++, loaded++)
                {
                    string[] c = lines[i].Split(',');
                    float F(int k) => float.Parse(c[k], _inv);
                    double t = double.Parse(c[2], _inv);
                    var sample = new BladePoseSample(long.Parse(c[1], _inv), t, new Vector3(F(3), F(4), F(5)),
                        new Quaternion(F(6), F(7), F(8), F(9)), (BladeTrackingState)int.Parse(c[10], _inv));
                    if (!recorder.TryAppendRecordedSample(sample, new Vector3(F(13), F(14), F(15))))
                    {
                        Log("FAILED: the recorder refused row " + (start + loaded));
                        _failures++;
                    }

                    _rowTimes.Add(t);
                }

                SetUpView();
                double clockStart = Time.unscaledTimeAsDouble;
                bool begun = recorder.TryBeginReplay(clockStart);
                Log("loaded rows " + start + ".." + (start + loaded - 1) + " from " + Path.GetFileName(Path.GetDirectoryName(input))
                    + "; replay begun=" + begun + " at clock " + clockStart.ToString("F4", _inv)
                    + " (a row's replay time = clock start + its recorded time - " + _rowTimes[0].ToString("R", _inv) + ")"
                    + " xr deviceActive=" + UnityEngine.XR.XRSettings.isDeviceActive);

                long lastId = 0;
                int maxAlive = 0;
                bool firstShot = false;
                bool twoShot = false;
                int shootAtFrame = -1;
                string shootName = null;
                float deadline = Time.realtimeSinceStartup + 90f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    int fed = recorder.ReplayIndex;
                    int alive = katana.WaveCount;
                    maxAlive = Math.Max(maxAlive, alive);
                    for (int w = 0; w < alive; w++)
                    {
                        long id = katana.SlashIdAt(w);
                        if (id <= lastId) continue;
                        lastId = id;
                        katana.TryGetWave(w, out double latchedAt, out _, out _, out _, out _, out float span, out _, out _, out _, out _);
                        int update = fed - 1;
                        int row = start + update;
                        double replayTime = clockStart + (_rowTimes[update] - _rowTimes[0]);
                        _latchRows.Add(row);
                        _latchIds.Add(id);
                        Log("latch slashId=" + id + " update=" + update + " row=" + row
                            + " recordedTime=" + _rowTimes[update].ToString("R", _inv)
                            + " replayTime=" + replayTime.ToString("F6", _inv) + " latchedAt=" + latchedAt.ToString("F6", _inv)
                            + " initialSpan=" + span.ToString("F4", _inv) + " alive=" + alive + " frame=" + Time.frameCount);
                    }

                    CheckVisuals(katana, alive);
                    if (!firstShot && alive >= 1 && shootAtFrame < 0)
                    {
                        firstShot = true;
                        shootAtFrame = Time.frameCount + 8;
                        shootName = "1-first-wave-flying";
                    }
                    else if (!twoShot && alive >= 2 && shootAtFrame < 0)
                    {
                        twoShot = true;
                        shootAtFrame = Time.frameCount + 4;
                        shootName = "2-two-waves-flying";
                    }

                    if (shootAtFrame >= 0 && Time.frameCount >= shootAtFrame)
                    {
                        shootAtFrame = -1;
                        yield return Picture(shootName, katana);
                    }

                    // The recording has been fed through and every wave has gone.
                    if (!recorder.IsReplaying && fed >= loaded && alive == 0 && lastId > 0)
                    {
                        break;
                    }
                }

                yield return Picture("3-all-expired", katana);
                bool gone = katana.WaveCount == 0;
                Log("replay done: fed=" + recorder.ReplayIndex + "/" + loaded + " latches=" + _latchRows.Count + " rows=[" + string.Join(",", _latchRows)
                    + "] slashIds=[" + string.Join(",", _latchIds) + "] maxAlive=" + maxAlive + " wavesAtEnd=" + katana.WaveCount);
                Expect(gone, "every wave expired by the end");
                Expect(maxAlive >= 2, "two waves were alive at once");
                bool idsRun = true;
                for (int i = 0; i < _latchIds.Count; i++) idsRun &= _latchIds[i] == i + 1;
                Expect(idsRun, "SlashIds run from 1 in latch order");
                if (!string.IsNullOrEmpty(expect))
                {
                    Expect(string.Join(",", _latchRows) == expect, "the latches are the expected rows " + expect);
                }

                int code = _failures == 0 ? 0 : 12;
                Log("finished with code " + code);
                yield return null;
                Application.Quit(code);
            }

            // Each live wave is shown by one slot; slots past the live count are hidden.
            private void CheckVisuals(SandboxRightHandKatana katana, int alive)
            {
                int shown = 0;
                foreach (Transform visual in katana.WaveVisualsForReadout)
                {
                    if (visual != null && visual.gameObject.activeSelf) shown++;
                }

                if (shown != alive)
                {
                    Expect(false, "frame " + Time.frameCount + ": " + shown + " wave quads shown for " + alive + " live waves");
                }
            }

            private void Expect(bool held, string what)
            {
                if (!held) _failures++;
                Log((held ? "ok: " : "FAILED: ") + what);
            }

            private Camera _view;
            private RenderTexture _target;

            // A camera of the check's own, in front of the sandbox's back wall and behind the player, drawing into its own
            // target so the development GUI is not in its pictures.
            private void SetUpView()
            {
                _view = new GameObject("Slash replay check view").AddComponent<Camera>();
                _view.transform.SetPositionAndRotation(new Vector3(0f, 2.3f, -1.9f), Quaternion.Euler(10f, 0f, 0f));
                _view.fieldOfView = 80f;
                _view.stereoTargetEye = StereoTargetEyeMask.None;
                _target = new RenderTexture(960, 600, 24);
                _target.Create();
                _view.targetTexture = _target;
            }

            private IEnumerator Picture(string name, SandboxRightHandKatana katana)
            {
                string file = Path.Combine(directory, name + ".png");
                yield return new WaitForEndOfFrame();
                var picture = new Texture2D(_target.width, _target.height, TextureFormat.RGB24, false);
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = _target;
                picture.ReadPixels(new Rect(0f, 0f, _target.width, _target.height), 0, 0);
                RenderTexture.active = previous;
                picture.Apply(false);
                File.WriteAllBytes(file, picture.EncodeToPNG());
                Destroy(picture);
                Log("picture " + name + " -> " + file + " alive=" + katana.WaveCount + " frame=" + Time.frameCount);
            }
        }
    }
}
