using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Animation;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The product's Pose Table evaluation (DESIGN 19.3, D-135): a table read from the probe's format, a pose made from
    /// an explicit Source Time with nothing advanced, a target time resolved by the Clip's loop or clamp, and no pose at
    /// all from an input that does not hold together.
    /// </summary>
    public class PoseTableTests
    {
        // Two bones, three samples at 2 Hz over one second: bone i at sample k stands at (k, i, 0), and bone 1 turns 90°
        // about y per sample.
        internal static byte[] Table(bool looping, string magic = "ZPTAB003", int positionsCount = -1)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Encoding.ASCII.GetBytes(magic));
                foreach (string text in new[] { "character", "clip-id", "clip", "source", "sha", "bake-v2", "key" }) writer.Write(text);
                writer.Write(1f);          // duration
                writer.Write(2f);          // sample rate
                writer.Write(3);           // regular samples
                writer.Write(false);       // end sample
                writer.Write(looping);
                writer.Write(1f); writer.Write(1f); writer.Write(1e-5f); writer.Write(0f);
                writer.Write(2); for (int i = 0; i < 2; i++) { writer.Write(1f); writer.Write(1f); writer.Write(1f); }   // scales
                writer.Write(2); writer.Write(-1); writer.Write(0);                                                        // parents
                writer.Write(0);                                                                                          // human
                writer.Write(3); writer.Write(0f); writer.Write(0.5f); writer.Write(1f);                                   // times
                writer.Write(2); writer.Write("a"); writer.Write("a/b");                                                  // paths
                int positions = positionsCount < 0 ? 6 : positionsCount;
                writer.Write(positions);
                for (int k = 0; k < positions / 2; k++) for (int i = 0; i < 2; i++) { writer.Write((float)k); writer.Write((float)i); writer.Write(0f); }
                writer.Write(6);
                for (int k = 0; k < 3; k++)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        Quaternion q = i == 1 ? Quaternion.Euler(0f, 90f * k, 0f) : Quaternion.identity;
                        writer.Write(q.x); writer.Write(q.y); writer.Write(q.z); writer.Write(q.w);
                    }
                }

                writer.Write(3); for (int k = 0; k < 3; k++) { writer.Write(0f); writer.Write(0f); writer.Write((float)k); }  // root
                writer.Write(3); for (int k = 0; k < 3; k++) { writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f); }
                writer.Write(string.Empty); writer.Write(0); writer.Write(0);
                writer.Flush();
                return stream.ToArray();
            }
        }

        [Test]
        public void ATable_IsRead_WithItsBonesSamplesAndClip()
        {
            Assert.That(PoseTable.TryRead(Table(true), out PoseTable table, out string error), Is.True, error);
            Assert.That(table.BoneCount, Is.EqualTo(2));
            Assert.That(table.SampleCount, Is.EqualTo(3));
            Assert.That(table.BonePath(1), Is.EqualTo("a/b"));
            Assert.That(table.ClipId, Is.EqualTo("clip-id"));
            Assert.That(table.DurationSeconds, Is.EqualTo(1f));
            Assert.That(table.IsLooping, Is.True);
        }

        [Test]
        public void APose_IsInterpolatedBetweenTheTwoSamples_WithTheShortestArc()
        {
            PoseTable.TryRead(Table(true), out PoseTable table, out _);
            var positions = new Vector3[2];
            var rotations = new Quaternion[2];
            Assert.That(table.TryEvaluate(0.25, positions, rotations), Is.True);
            Assert.That(Vector3.Distance(positions[0], new Vector3(0.5f, 0f, 0f)), Is.LessThan(1e-6f));
            Assert.That(Vector3.Distance(positions[1], new Vector3(0.5f, 1f, 0f)), Is.LessThan(1e-6f));
            Assert.That(Quaternion.Angle(rotations[1], Quaternion.Euler(0f, 45f, 0f)), Is.LessThan(1e-3f));

            // The end of the Clip is the last sample, exactly.
            Assert.That(table.TryEvaluate(1.0, positions, rotations), Is.True);
            Assert.That(positions[1], Is.EqualTo(new Vector3(2f, 1f, 0f)));
        }

        [Test]
        public void TheSameSourceTime_GivesTheSamePose_WhateverWasAskedBefore()
        {
            PoseTable.TryRead(Table(true), out PoseTable table, out _);
            var first = new Vector3[2];
            var firstRotations = new Quaternion[2];
            var again = new Vector3[2];
            var againRotations = new Quaternion[2];
            table.TryEvaluate(0.7, first, firstRotations);
            table.TryEvaluate(0.2, again, againRotations);
            table.TryEvaluate(0.7, again, againRotations);
            Assert.That(again, Is.EqualTo(first));
            Assert.That(againRotations, Is.EqualTo(firstRotations), "nothing advanced between the two");
        }

        [Test]
        public void ATargetTime_Wraps_ForALoopingClip_AndClamps_ForAnother()
        {
            PoseTable.TryRead(Table(true), out PoseTable looping, out _);
            Assert.That(looping.ResolveSourceTime(1.25), Is.EqualTo(0.25).Within(1e-9));
            Assert.That(looping.ResolveSourceTime(1.0), Is.EqualTo(0.0), "the duration maps back to the start");
            Assert.That(looping.ResolveSourceTime(-0.25), Is.EqualTo(0.75).Within(1e-9));

            PoseTable.TryRead(Table(false), out PoseTable once, out _);
            Assert.That(once.ResolveSourceTime(1.5), Is.EqualTo(1.0), "held at the last sample");
            Assert.That(once.ResolveSourceTime(-1.0), Is.EqualTo(0.0));
            Assert.That(double.IsNaN(once.ResolveSourceTime(double.PositiveInfinity)), Is.True);
        }

        [Test]
        public void AnInputThatDoesNotHoldTogether_GivesNoPose()
        {
            PoseTable.TryRead(Table(true), out PoseTable table, out _);
            var positions = new[] { Vector3.one * 7f, Vector3.one * 7f };
            var rotations = new Quaternion[2];
            Assert.That(table.TryEvaluate(double.NaN, positions, rotations), Is.False);
            Assert.That(table.TryEvaluate(1.5, positions, rotations), Is.False, "outside the Clip: resolve it first");
            Assert.That(table.TryEvaluate(-0.1, positions, rotations), Is.False);
            Assert.That(table.TryEvaluate(0.5, new Vector3[1], new Quaternion[1]), Is.False);
            Assert.That(positions[0], Is.EqualTo(Vector3.one * 7f), "nothing written");

            Assert.That(PoseTable.TryRead(Table(true, "ZPTAB999"), out _, out _), Is.False, "not a table");
            byte[] whole = Table(true);
            byte[] cut = new byte[whole.Length / 2];
            System.Array.Copy(whole, cut, cut.Length);
            Assert.That(PoseTable.TryRead(cut, out _, out _), Is.False, "truncated");
            Assert.That(PoseTable.TryRead(Table(true, positionsCount: 4), out _, out string error), Is.False, "counts that do not match");
            Assert.That(error, Is.Not.Null);
        }
    }
}
