using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Input;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The city walk's attack on an NPC (TL, 2026-10-03) feeds a short chunk of the recorded Slash input -- rows 2275+85, one
    /// stroke and the wave it makes -- through the same recorder replay and katana input the planned Slashes use. Here,
    /// with no world: the chunk replayed into a fresh katana makes at least one wave, the replay ends, and the katana is
    /// idle again (no wave flying) within the chunk's length and a wave's lifetime -- what the attack waits for before the
    /// walk goes on. The plan's own chunk (2227+512) for comparison. Ignored when the recorded input is not on this machine.
    /// </summary>
    public sealed class SlashChunkPlayModeTests
    {
        private const string DefaultInput = "C:/log/zantetsuken-vr/SlashSpan/20260918-153438-normal/input.csv";

        private static IEnumerator Replay(string[] lines, int start, int rows, List<string> result)
        {
            var rig = new GameObject("Slash Chunk Rig");
            var blade = new GameObject("Slash Chunk Blade");
            var view = new GameObject("Slash Chunk View");
            try
            {
                view.transform.SetPositionAndRotation(new Vector3(0f, 1.7f, 0f), Quaternion.identity);
                var katana = rig.AddComponent<SandboxRightHandKatana>();
                katana.Katana = blade.transform;
                typeof(SandboxRightHandKatana).GetField("viewForwardReference", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(katana, view.transform);
                var recorder = rig.AddComponent<SandboxSlashPoseRecorder>();
                typeof(SandboxSlashPoseRecorder).GetField("katana", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(recorder, katana);
                yield return null;
                recorder.BeginRecording();
                int loaded = 0;
                for (int i = start + 1; i < lines.Length && loaded < rows; i++, loaded++)
                {
                    string[] c = lines[i].Split(',');
                    float F(int k) => float.Parse(c[k], CultureInfo.InvariantCulture);
                    var sample = new BladePoseSample(long.Parse(c[1], CultureInfo.InvariantCulture), double.Parse(c[2], CultureInfo.InvariantCulture), new Vector3(F(3), F(4), F(5)),
                        new Quaternion(F(6), F(7), F(8), F(9)), (BladeTrackingState)int.Parse(c[10], CultureInfo.InvariantCulture));
                    Assert.That(recorder.TryAppendRecordedSample(sample, new Vector3(F(13), F(14), F(15))), Is.True, "row " + (start + loaded));
                }

                Assert.That(recorder.TryBeginReplay(Time.unscaledTimeAsDouble), Is.True);
                float began = Time.realtimeSinceStartup;
                int maxWaves = 0;
                var latched = new HashSet<double>();
                float fedAt = -1f;
                while (Time.realtimeSinceStartup - began < rows / 90f + 10f)
                {
                    yield return null;
                    maxWaves = Mathf.Max(maxWaves, katana.WaveCount);
                    for (int w = 0; w < katana.WaveCount; w++)
                    {
                        if (katana.TryGetWave(w, out double latchedAt, out _, out _, out _, out _, out _, out _, out _, out _, out _)) latched.Add(latchedAt);
                    }

                    if (!recorder.IsReplaying && fedAt < 0f) fedAt = Time.realtimeSinceStartup - began;
                    if (!recorder.IsReplaying && katana.WaveCount == 0) break;
                }

                float idleAt = Time.realtimeSinceStartup - began;
                result.Add("rows " + start + "+" + rows + ": waves made " + latched.Count + " (at most " + maxWaves + " flying at once); replay fed in " + fedAt.ToString("F2") + " s, the katana idle after " + idleAt.ToString("F2") + " s");
                result.Add(latched.Count.ToString(CultureInfo.InvariantCulture));
                result.Add(idleAt.ToString("R", CultureInfo.InvariantCulture));
            }
            finally
            {
                Object.Destroy(rig);
                Object.Destroy(blade);
                Object.Destroy(view);
            }
        }

        [UnityTest]
        public IEnumerator TheAttacksChunk_MakesAWave_ThroughTheKatanasInput_AndTheKatanaIsIdleSoonAfter()
        {
            string path = System.Environment.GetEnvironmentVariable("ZANTETSU_SLASH_INPUT");
            if (string.IsNullOrEmpty(path)) path = DefaultInput;
            if (!File.Exists(path)) Assert.Ignore("the recorded Slash input is not on this machine: " + path);
            string[] lines = File.ReadAllLines(path);
            var attack = new List<string>();
            yield return Replay(lines, 2275, 85, attack);
            var plan = new List<string>();
            yield return Replay(lines, 2227, 512, plan);
            TestContext.Out.WriteLine(attack[0] + "\n" + plan[0]);
            Assert.That(int.Parse(attack[1]), Is.GreaterThanOrEqualTo(1), "the attack's chunk made a wave");
            Assert.That(float.Parse(attack[2], CultureInfo.InvariantCulture), Is.LessThan(85 / 90f + 1.5f + 1.5f), "idle within the chunk, a wave's lifetime and a margin");
        }
    }
}
