using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Slash;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The bone level of detail (<see cref="PoseLodDirector"/>): the time goes on while a character is not updated and an
    /// update applies the frame's pose; characters of one level update on frames spread over the cycle; a bone a level
    /// leaves out keeps its last pose (following its parent), and a level that drops applies every bone at once; a table
    /// bone nothing needs is left out without being named.
    /// </summary>
    public class PoseLodDirectorPlayModeTests
    {
        private readonly List<Object> _made = new List<Object>();

        // Bones at the given paths, three samples at 2 Hz over one looping second: bone i at sample k is at (k, i, 0).
        internal static byte[] Table(params string[] paths)
        {
            int n = paths.Length;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Encoding.ASCII.GetBytes("ZPTAB003"));
                foreach (string text in new[] { "c", "clip-id", "clip", "s", "h", "v", "k" }) writer.Write(text);
                writer.Write(1f); writer.Write(2f); writer.Write(3); writer.Write(false); writer.Write(true);
                writer.Write(1f); writer.Write(1f); writer.Write(1e-5f); writer.Write(0f);
                writer.Write(0); writer.Write(0); writer.Write(0);
                writer.Write(3); writer.Write(0f); writer.Write(0.5f); writer.Write(1f);
                writer.Write(n); foreach (string path in paths) writer.Write(path);
                writer.Write(3 * n); for (int k = 0; k < 3; k++) for (int i = 0; i < n; i++) { writer.Write((float)k); writer.Write((float)i); writer.Write(0f); }
                writer.Write(3 * n); for (int k = 0; k < 3 * n; k++) { writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f); }
                writer.Write(0); writer.Write(0); writer.Write(string.Empty); writer.Write(0); writer.Write(0);
                writer.Flush();
                return stream.ToArray();
            }
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            foreach (Object made in _made) if (made != null) Object.Destroy(made);
            _made.Clear();
            yield return null;
        }

        private PoseLodDirector Director()
        {
            var go = new GameObject("pose lod director");
            _made.Add(go);
            var director = go.AddComponent<PoseLodDirector>();
            director.DisplayRateHz = 72f;
            return director;
        }

        // A root with bones a, a/b, a/c and a player of "a", "a/b", "a/c".
        private PoseTablePlayer Character(out Transform a, out Transform b, out Transform c) => Character(null, out a, out b, out c);

        // The same, playing <paramref name="asset"/> when one is given (characters playing one table share its asset).
        private PoseTablePlayer Character(TextAsset asset, out Transform a, out Transform b, out Transform c)
        {
            var root = new GameObject("lod character");
            _made.Add(root);
            a = new GameObject("a").transform;
            a.SetParent(root.transform, false);
            b = new GameObject("b").transform;
            b.SetParent(a, false);
            c = new GameObject("c").transform;
            c.SetParent(a, false);
            if (asset == null)
            {
                asset = new TextAsset(Table("a", "a/b", "a/c"));
                _made.Add(asset);
            }

            PoseTablePlayer player = root.AddComponent<PoseTablePlayer>();
            player.Configure(asset, root.transform, 0.0);
            Assert.That(player.Table, Is.Not.Null, player.BindError);
            return player;
        }

        private static List<(Transform, Bounds)> Box(Transform bone) => new List<(Transform, Bounds)> { (bone, new Bounds(Vector3.zero, Vector3.one * 0.2f)) };

        [UnityTest]
        public IEnumerator ASkippedCharacter_KeepsItsTimeGoing_AndAnUpdateAppliesTheFramesPose()
        {
            PoseLodDirector director = Director();
            PoseTablePlayer player = Character(out Transform a, out Transform b, out Transform _);
            PoseLodCharacter lod = director.Register(player, new[] { a, b }, null, Box(b), out string refused);
            Assert.That(lod, Is.Not.Null, refused);
            lod.LevelOverride = 3;
            Assert.That(director.IntervalOf(3), Is.EqualTo(8), "about 9 Hz at 72 Hz");
            yield return null; // the first update comes at once; the cycle follows

            var updates = new List<(int frame, double target, double time)>();
            for (int i = 0; i < 20; i++)
            {
                yield return null;
                if (player.AppliedFrame == Time.frameCount)
                {
                    updates.Add((Time.frameCount, player.AppliedTargetTime, Time.timeAsDouble));
                }
            }

            Assert.That(updates.Count, Is.InRange(2, 3), "once every 8 frames over 20");
            for (int i = 1; i < updates.Count; i++)
            {
                Assert.That(updates[i].frame - updates[i - 1].frame, Is.EqualTo(8));
                Assert.That(updates[i].target - updates[i - 1].target, Is.EqualTo(updates[i].time - updates[i - 1].time).Within(1e-9),
                    "the time went on while nothing was applied: the update applied the frame's time, not the next sample after the last");
            }

            var positions = new Vector3[3];
            var rotations = new Quaternion[3];
            Assert.That(player.Table.TryEvaluate(player.AppliedSourceTime, positions, rotations), Is.True);
            Assert.That(a.localPosition, Is.EqualTo(positions[0]), "the pose of the frame it was applied in");
        }

        [UnityTest]
        public IEnumerator CharactersOfOneLevel_UpdateOnFramesSpreadOverTheCycle()
        {
            PoseLodDirector director = Director();
            var players = new List<PoseTablePlayer>();
            for (int i = 0; i < 8; i++)
            {
                PoseTablePlayer player = Character(out Transform a, out Transform b, out Transform _);
                PoseLodCharacter lod = director.Register(player, new[] { a, b }, null, Box(b), out string refused);
                Assert.That(lod, Is.Not.Null, refused);
                lod.LevelOverride = 3;
                players.Add(player);
            }

            yield return null; // every character's first scheduled update
            var counts = new int[players.Count];
            for (int f = 0; f < 16; f++)
            {
                yield return null;
                int now = 0;
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i].AppliedFrame == Time.frameCount) { counts[i]++; now++; }
                }

                Assert.That(now, Is.LessThanOrEqualTo(1), "one of the eight per frame, not all eight together");
                Assert.That(director.UpdatedLastFrame, Is.EqualTo(now));
            }

            foreach (int count in counts) Assert.That(count, Is.EqualTo(2), "each once every 8 frames");
        }

        [UnityTest]
        public IEnumerator ALeftOutBone_KeepsItsLastPose_AndALevelThatDrops_AppliesEveryBoneAtOnce()
        {
            PoseLodDirector director = Director();
            PoseTablePlayer player = Character(out Transform a, out Transform b, out Transform c);
            // Drawn with a and b; b is left out from level 1 by name; c is drawn with nothing and named nowhere.
            PoseLodCharacter lod = director.Register(player, new[] { a, b }, new IReadOnlyList<Transform>[] { new[] { b } }, Box(b), out string refused);
            Assert.That(lod, Is.Not.Null, refused);
            Assert.That(lod.AppliedBoneCount(0), Is.EqualTo(2), "level 0: the needed set, a and b (c is needed by nothing)");
            Assert.That(lod.AppliedBoneCount(1), Is.EqualTo(1), "level 1: a alone (b named)");
            Assert.That(lod.OmittedHitBoneCount(1), Is.EqualTo(1), "b's box is left out at level 1: read only after a whole pose");

            lod.LevelOverride = 1;
            Vector3 heldB = b.localPosition, heldC = c.localPosition;
            yield return null; // the first update comes at once; the cycle follows
            Assert.That(b.localPosition, Is.EqualTo(heldB));
            int applied = 0;
            for (int i = 0; i < 12; i++)
            {
                yield return null;
                if (player.AppliedFrame == Time.frameCount) applied++;
                Assert.That(b.localPosition, Is.EqualTo(heldB), "b keeps the pose it had");
                Assert.That(c.localPosition, Is.EqualTo(heldC), "and so does c");
            }

            Assert.That(applied, Is.EqualTo(6), "level 1: every second frame");
            Assert.That(player.LastAppliedSubset, Is.True);

            lod.LevelOverride = 0;
            yield return null;
            Assert.That(player.AppliedFrame, Is.EqualTo(Time.frameCount), "the drop applies at once");
            Assert.That(lod.FullPoseFrame, Is.EqualTo(Time.frameCount), "every needed bone");
            var positions = new Vector3[3];
            var rotations = new Quaternion[3];
            Assert.That(player.Table.TryEvaluate(player.AppliedSourceTime, positions, rotations), Is.True);
            Assert.That(b.localPosition, Is.EqualTo(positions[1]), "b at the frame's pose");
            Assert.That(c.localPosition, Is.EqualTo(heldC), "c, needed by nothing, is never applied");
        }

        [UnityTest]
        public IEnumerator TheNeededBones_StandAsUnderAWholePose_WhatTheUnneededOnesDo()
        {
            PoseLodDirector director = Director();
            PoseTablePlayer player = Character(out Transform a, out Transform b, out Transform c);
            PoseLodCharacter lod = director.Register(player, new[] { b }, null, Box(b), out string refused);
            Assert.That(lod, Is.Not.Null, refused);
            Assert.That(lod.AppliedBoneCount(0), Is.EqualTo(2), "b and its ancestor a");
            c.SetLocalPositionAndRotation(new Vector3(9f, -4f, 2f), Quaternion.Euler(33f, 71f, -12f)); // anything at all
            yield return null;
            Assert.That(lod.FullPoseFrame, Is.EqualTo(Time.frameCount));
            Matrix4x4 needed = b.localToWorldMatrix;
            var positions = new Vector3[3];
            var rotations = new Quaternion[3];
            Assert.That(player.Table.TryEvaluate(player.AppliedSourceTime, positions, rotations), Is.True);
            a.SetLocalPositionAndRotation(positions[0], rotations[0]);
            b.SetLocalPositionAndRotation(positions[1], rotations[1]);
            c.SetLocalPositionAndRotation(positions[2], rotations[2]);
            Assert.That(b.localToWorldMatrix, Is.EqualTo(needed), "b's world pose is the whole pose's, whatever c held");
        }

        [Test]
        public void TheWeightedBones_AreThoseAVertexIsSkinnedToWithAWeight_NotEveryBoneTheRendererLists()
        {
            var root = new GameObject("weights");
            _made.Add(root);
            Transform[] bones = { new GameObject("p").transform, new GameObject("q").transform, new GameObject("r").transform };
            foreach (Transform t in bones) t.SetParent(root.transform, false);
            var skin = root.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 },
                bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity },
                boneWeights = new[]
                {
                    new BoneWeight { boneIndex0 = 1, weight0 = 1f },
                    new BoneWeight { boneIndex0 = 1, weight0 = 0.5f, boneIndex1 = 2, weight1 = 0.5f },
                    new BoneWeight { boneIndex0 = 1, weight0 = 1f },
                },
            };
            _made.Add(mesh);
            skin.sharedMesh = mesh;
            skin.bones = bones;
            var weighted = new List<Transform>();
            PoseLodDirector.CollectWeightedBones(skin, weighted);
            Assert.That(weighted, Is.EquivalentTo(new[] { bones[1], bones[2] }), "p is listed but no vertex is skinned to it");
        }

        // One joint j swinging -80 -> +80 -> -80 degrees about y over the looping second, with b a metre ahead of it: at the
        // samples b stands at x = -+0.98, z = 0.17; between them it sweeps through z = 1.
        internal static byte[] SwingTable()
        {
            string[] paths = { "j", "j/b" };
            Quaternion[] joint = { Quaternion.Euler(0f, -80f, 0f), Quaternion.Euler(0f, 80f, 0f), Quaternion.Euler(0f, -80f, 0f) };
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Encoding.ASCII.GetBytes("ZPTAB003"));
                foreach (string text in new[] { "c", "clip-id", "clip", "s", "h", "v", "k" }) writer.Write(text);
                writer.Write(1f); writer.Write(2f); writer.Write(3); writer.Write(false); writer.Write(true);
                writer.Write(1f); writer.Write(1f); writer.Write(1e-5f); writer.Write(0f);
                writer.Write(0); writer.Write(0); writer.Write(0);
                writer.Write(3); writer.Write(0f); writer.Write(0.5f); writer.Write(1f);
                writer.Write(2); foreach (string path in paths) writer.Write(path);
                writer.Write(6); for (int k = 0; k < 3; k++) { writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f); }
                writer.Write(6);
                for (int k = 0; k < 3; k++)
                {
                    writer.Write(joint[k].x); writer.Write(joint[k].y); writer.Write(joint[k].z); writer.Write(joint[k].w);
                    writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
                }

                writer.Write(0); writer.Write(0); writer.Write(string.Empty); writer.Write(0); writer.Write(0);
                writer.Flush();
                return stream.ToArray();
            }
        }

        [UnityTest]
        public IEnumerator TheRange_HoldsTheHitBoxBetweenSamples_WhereTheSamplesAloneDoNot()
        {
            PoseLodDirector director = Director();
            var root = new GameObject("swing");
            _made.Add(root);
            Transform j = new GameObject("j").transform;
            j.SetParent(root.transform, false);
            Transform b = new GameObject("b").transform;
            b.SetParent(j, false);
            var asset = new TextAsset(SwingTable());
            _made.Add(asset);
            PoseTablePlayer player = root.AddComponent<PoseTablePlayer>();
            player.Configure(asset, root.transform, 0.0);
            var box = new Bounds(Vector3.zero, Vector3.one * 0.1f);
            PoseLodCharacter lod = director.Register(player, new[] { b }, null, new List<(Transform, Bounds)> { (b, box) }, out string refused);
            Assert.That(lod, Is.Not.Null, refused);

            // What the samples alone, with the former fixed 5 cm, would have held.
            var samplesOnly = new Bounds();
            var positions = new Vector3[2];
            var rotations = new Quaternion[2];
            bool any = false;
            void Pose(double t)
            {
                player.Table.TryEvaluate(t, positions, rotations);
                j.SetLocalPositionAndRotation(positions[0], rotations[0]);
                b.SetLocalPositionAndRotation(positions[1], rotations[1]);
            }

            IEnumerable<Vector3> Corners()
            {
                Matrix4x4 m = root.transform.worldToLocalMatrix * b.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    yield return m.MultiplyPoint3x4(new Vector3((c & 1) == 0 ? box.min.x : box.max.x, (c & 2) == 0 ? box.min.y : box.max.y, (c & 4) == 0 ? box.min.z : box.max.z));
                }
            }

            foreach (double t in new[] { 0.0, 0.5, 1.0 })
            {
                Pose(t);
                foreach (Vector3 p in Corners()) { if (any) samplesOnly.Encapsulate(p); else { samplesOnly = new Bounds(p, Vector3.zero); any = true; } }
            }

            samplesOnly.Expand(0.1f);
            Pose(0.25);
            Assert.That(Corners().Any(p => !samplesOnly.Contains(p)), Is.True, "mid-swing the box is outside the samples' union and 5 cm");

            for (int i = 0; i <= 200; i++)
            {
                Pose(i / 200.0);
                foreach (Vector3 p in Corners())
                {
                    Assert.That(lod.RangeBounds.Contains(p), Is.True, "t = " + (i / 200.0) + ": " + p.ToString("F3") + " outside " + lod.RangeBounds);
                }
            }

            Assert.That(lod.LargestBetweenSamples, Is.GreaterThan(0.8f), "the stretch between samples widened it");
            yield return null;
        }

        [UnityTest]
        public IEnumerator AnAncestorOfAnAppliedBone_IsApplied_EvenWhenNamed()
        {
            PoseLodDirector director = Director();
            PoseTablePlayer player = Character(out Transform a, out Transform b, out Transform _);
            // a is named to be left out, but b (drawn, not named) hangs under it.
            PoseLodCharacter lod = director.Register(player, new[] { a, b }, new IReadOnlyList<Transform>[] { new[] { a } }, Box(b), out string refused);
            Assert.That(lod, Is.Not.Null, refused);
            Assert.That(lod.AppliedBoneCount(1), Is.EqualTo(2), "a stays with b");
            yield return null;
        }

        // A one-bone character ("a" at (k, 0, 0), k = 0..2) whose range centre stands at world <paramref name="centre"/>.
        private PoseLodCharacter PlacedCharacter(PoseLodDirector director, Vector3 centre)
        {
            var root = new GameObject("placed lod character");
            _made.Add(root);
            Transform a = new GameObject("a").transform;
            a.SetParent(root.transform, false);
            var asset = new TextAsset(Table("a"));
            _made.Add(asset);
            PoseTablePlayer player = root.AddComponent<PoseTablePlayer>();
            player.Configure(asset, root.transform, 0.0);
            PoseLodCharacter lod = director.Register(player, new[] { a }, null, Box(a), out string refused);
            Assert.That(lod, Is.Not.Null, refused);
            root.transform.position = centre - lod.RangeBounds.center;
            return lod;
        }

        [UnityTest]
        public IEnumerator TheLevel_IsTheStrongerOfViewAndDistance_AndAPartlyVisibleCharacterIsNotReduced()
        {
            PoseLodDirector director = Director();
            var cameraObject = new GameObject("lod view");
            _made.Add(cameraObject);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 60f;
            camera.aspect = 1f;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            director.ViewCamera = camera;
            // The view's sides are 30 degrees off its axis. Seen from 6 m, the range (a sphere of radius r) spans
            // asin(r / 6) either side of its centre; the places below are set from that.
            Vector3 At(float degrees, float metres) => Quaternion.Euler(0f, degrees, 0f) * Vector3.forward * metres;
            PoseLodCharacter ahead = PlacedCharacter(director, At(0f, 3f));
            float r = ahead.RangeBounds.extents.magnitude;
            Assert.That(r, Is.LessThan(4f), "the geometry below needs the range well inside 6 m");
            float half = Mathf.Asin(r / 6f) * Mathf.Rad2Deg;
            PoseLodCharacter straddling = PlacedCharacter(director, At(30f + 0.5f * half, 6f)); // centre outside, part inside
            PoseLodCharacter justOutside = PlacedCharacter(director, At(30f + half + 8f, 6f)); // 8 degrees beyond
            PoseLodCharacter behindSide = PlacedCharacter(director, At(120f, 6f));
            PoseLodCharacter tenMetres = PlacedCharacter(director, At(0f, 10f + r));
            PoseLodCharacter thirtyMetres = PlacedCharacter(director, At(0f, 30f + r));
            yield return null;
            Assert.That(ahead.Level, Is.EqualTo(0), "inside and near");
            Assert.That(straddling.Level, Is.EqualTo(0), "part of the range inside the view: not reduced");
            Assert.That(justOutside.Level, Is.EqualTo(1), "a little beyond the side");
            Assert.That(behindSide.Level, Is.EqualTo(3), "25 degrees or more beyond: the strongest");
            Assert.That(tenMetres.Level, Is.EqualTo(1), "in view, past 5 m: the distance decides");
            Assert.That(thirtyMetres.Level, Is.EqualTo(3), "in view, past 25 m");
        }

        [UnityTest]
        public IEnumerator CharactersWithTheSameInputs_ShareOnePlan_EqualToOneMadeAlone_AndOthersGetTheirOwn()
        {
            PoseLodDirector director = Director();
            var table = new TextAsset(Table("a", "a/b", "a/c"));
            _made.Add(table);
            PoseTablePlayer first = Character(table, out Transform a1, out Transform b1, out Transform _);
            PoseTablePlayer second = Character(table, out Transform a2, out Transform b2, out Transform _);
            PoseTablePlayer other = Character(table, out Transform a3, out Transform b3, out Transform _);
            second.transform.SetPositionAndRotation(new Vector3(7f, 0f, 3f), Quaternion.Euler(0f, 90f, 0f)); // placement is not an input
            PoseLodCharacter made = director.Register(first, new[] { b1 }, null, Box(b1), out string r1);
            PoseLodCharacter shared = director.Register(second, new[] { b2 }, null, Box(b2), out string r2);
            var otherBox = new List<(Transform, Bounds)> { (b3, new Bounds(Vector3.zero, Vector3.one * 0.3f)) };
            PoseLodCharacter own = director.Register(other, new[] { b3 }, null, otherBox, out string r3);
            Assert.That(made, Is.Not.Null, r1);
            Assert.That(shared, Is.Not.Null, r2);
            Assert.That(own, Is.Not.Null, r3);
            Assert.That(made.SharedPlan, Is.False);
            Assert.That(shared.SharedPlan, Is.True, "the same table, bones and hit box");
            Assert.That(own.SharedPlan, Is.False, "another hit box: its own plan");
            Assert.That(shared.RangeBounds, Is.EqualTo(made.RangeBounds));
            Assert.That(shared.AppliedBoneCount(0), Is.EqualTo(made.AppliedBoneCount(0)));

            // The shared plan is what the second character would have been given alone.
            PoseLodDirector alone = Director();
            alone.ShareSameInputs = false;
            PoseTablePlayer third = Character(table, out Transform _, out Transform b4, out Transform _);
            PoseLodCharacter madeAlone = alone.Register(third, new[] { b4 }, null, Box(b4), out string r4);
            Assert.That(madeAlone, Is.Not.Null, r4);
            Assert.That(madeAlone.RangeBounds, Is.EqualTo(shared.RangeBounds));
            Assert.That(madeAlone.AppliedBoneCount(0), Is.EqualTo(shared.AppliedBoneCount(0)));
            yield return null;
        }

        // A root, a Transform the table does not drive ("fixed", the model root, placed as given), then a and a/b under it.
        private PoseTablePlayer FixedCharacter(TextAsset asset, Quaternion fixedRotation, Vector3 fixedScale, out Transform b)
        {
            var root = new GameObject("lod character with a fixed node");
            _made.Add(root);
            Transform fixedNode = new GameObject("fixed").transform;
            fixedNode.SetParent(root.transform, false);
            fixedNode.localPosition = new Vector3(0f, 0.5f, 0f);
            fixedNode.localRotation = fixedRotation;
            fixedNode.localScale = fixedScale;
            Transform a = new GameObject("a").transform;
            a.SetParent(fixedNode, false);
            b = new GameObject("b").transform;
            b.SetParent(a, false);
            Transform c = new GameObject("c").transform;
            c.SetParent(a, false);
            PoseTablePlayer player = root.AddComponent<PoseTablePlayer>();
            player.Configure(asset, fixedNode, 0.0);
            Assert.That(player.Table, Is.Not.Null, player.BindError);
            return player;
        }

        [UnityTest]
        public IEnumerator TheSharedPlan_FollowsTheFixedNodesOnTheHitChain_NotTheRootsPlacement()
        {
            PoseLodDirector director = Director();
            var table = new TextAsset(Table("a", "a/b", "a/c"));
            _made.Add(table);
            PoseTablePlayer first = FixedCharacter(table, Quaternion.identity, Vector3.one, out Transform b1);
            PoseTablePlayer moved = FixedCharacter(table, Quaternion.identity, Vector3.one, out Transform b2);
            moved.transform.SetPositionAndRotation(new Vector3(-12.3f, 0.7f, 41.9f), Quaternion.Euler(3f, 217f, -5f));
            moved.transform.localScale = new Vector3(1f, 1f, 1f);
            PoseTablePlayer turned = FixedCharacter(table, Quaternion.Euler(0f, 30f, 0f), Vector3.one, out Transform b3);
            PoseTablePlayer scaled = FixedCharacter(table, Quaternion.identity, Vector3.one * 1.5f, out Transform b4);

            PoseLodCharacter made = director.Register(first, new[] { b1 }, null, Box(b1), out string r1);
            PoseLodCharacter sameInputs = director.Register(moved, new[] { b2 }, null, Box(b2), out string r2);
            PoseLodCharacter otherTurn = director.Register(turned, new[] { b3 }, null, Box(b3), out string r3);
            PoseLodCharacter otherScale = director.Register(scaled, new[] { b4 }, null, Box(b4), out string r4);
            Assert.That(made, Is.Not.Null, r1);
            Assert.That(sameInputs, Is.Not.Null, r2);
            Assert.That(otherTurn, Is.Not.Null, r3);
            Assert.That(otherScale, Is.Not.Null, r4);

            Assert.That(sameInputs.SharedPlan, Is.True, "only the root's position and rotation differ: shared");
            Assert.That(sameInputs.RangeBounds, Is.EqualTo(made.RangeBounds));
            Assert.That(otherTurn.SharedPlan, Is.False, "the fixed node turned 30 degrees: not shared");
            Assert.That(otherTurn.RangeBounds, Is.Not.EqualTo(made.RangeBounds), "and its range is indeed another");
            Assert.That(otherScale.SharedPlan, Is.False, "the fixed node scaled: not shared");
            Assert.That(otherScale.RangeBounds, Is.Not.EqualTo(made.RangeBounds));

            // A hit box with float noise of the size a character's world scale gives it (1e-7 m) is the same input.
            PoseTablePlayer noisy = FixedCharacter(table, Quaternion.identity, Vector3.one, out Transform b6);
            var noisyBox = new List<(Transform, Bounds)> { (b6, new Bounds(new Vector3(1e-7f, 0f, -1e-7f), Vector3.one * 0.2f)) };
            PoseLodCharacter withNoise = director.Register(noisy, new[] { b6 }, null, noisyBox, out string r6);
            Assert.That(withNoise, Is.Not.Null, r6);
            Assert.That(withNoise.SharedPlan, Is.True, "boxes within 0.1 mm share, inside the range's 1 cm margin");

            // Each one's range is what it would have been given alone.
            PoseLodDirector alone = Director();
            alone.ShareSameInputs = false;
            PoseTablePlayer turnedAlone = FixedCharacter(table, Quaternion.Euler(0f, 30f, 0f), Vector3.one, out Transform b5);
            PoseLodCharacter madeAlone = alone.Register(turnedAlone, new[] { b5 }, null, Box(b5), out string r5);
            Assert.That(madeAlone, Is.Not.Null, r5);
            Assert.That(madeAlone.RangeBounds, Is.EqualTo(otherTurn.RangeBounds));
            yield return null;
        }

        [UnityTest]
        public IEnumerator NeededOnly_AppliesTheNeededBonesEveryFrame_WhateverTheViewOrDistance()
        {
            PoseLodDirector director = Director();
            director.NeededOnly = true;
            PoseTablePlayer player = Character(out Transform a, out Transform b, out Transform c);
            PoseLodCharacter lod = director.Register(player, new[] { b }, new IReadOnlyList<Transform>[] { new[] { b } }, Box(b), out string refused);
            Assert.That(lod, Is.Not.Null, refused);
            player.transform.position = new Vector3(0f, 0f, -500f); // far away and behind any view
            Vector3 heldC = c.localPosition;
            for (int i = 0; i < 5; i++)
            {
                yield return null;
                Assert.That(lod.Level, Is.EqualTo(0));
                Assert.That(lod.FullPoseFrame, Is.EqualTo(Time.frameCount), "every frame, the needed set");
                Assert.That(c.localPosition, Is.EqualTo(heldC), "and nothing that is not needed");
            }
        }

        [UnityTest]
        public IEnumerator TheRange_HoldsTheHitBoxAtEverySampleOfTheClip()
        {
            PoseLodDirector director = Director();
            PoseTablePlayer player = Character(out Transform a, out Transform b, out Transform _);
            PoseLodCharacter lod = director.Register(player, new[] { a, b }, null, Box(b), out string refused);
            Assert.That(lod, Is.Not.Null, refused);
            // a = (k, 0, 0) and b = (k, 1, 0) under a, for k = 0, 1, 2: b's box centre goes from (0, 1, 0) to (4, 1, 0).
            // Between two samples a and b each move 1 m and nothing turns: every box is widened by 2 m, and 1 cm is added.
            Bounds range = lod.RangeBounds;
            Assert.That(lod.LargestBetweenSamples, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(range.min.x, Is.EqualTo(-0.1f - 2f - 0.01f).Within(1e-4f));
            Assert.That(range.max.x, Is.EqualTo(4.1f + 2f + 0.01f).Within(1e-4f));
            Assert.That(range.min.y, Is.EqualTo(0.9f - 2f - 0.01f).Within(1e-4f));
            yield return null;
        }
    }

    // A character's pose on demand, standing in for the level of detail: its range is given, and the current pose is a
    // bone placement it puts on when asked.
    internal sealed class PoseOnDemandStandIn : IPoseOnDemand
    {
        public Transform Root { get; set; }
        public Bounds RangeBounds { get; set; }
        public bool IsLive => true;
        public Transform Bone;
        public Vector3 CurrentPosition;
        public int Asked;
        private int _frame = -1;

        public bool EnsureCurrentFullPose()
        {
            Asked++;
            if (_frame == Time.frameCount) return false;
            _frame = Time.frameCount;
            Bone.localPosition = CurrentPosition;
            return true;
        }
    }

    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        [UnityTest]
        public IEnumerator U8_Lod_ASweepThatMissesTheStaleBoneButMeetsTheCurrentPose_Hits()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);

            // The bone was last updated at the root; the frame's pose has it at z = 5.
            var pose = new PoseOnDemandStandIn
            {
                Root = root.transform, Bone = bone, CurrentPosition = new Vector3(0f, 0f, 5f),
                RangeBounds = new Bounds(new Vector3(0.5f, 0.5f, 3f), new Vector3(2f, 2f, 8f)),
            };
            SlashSweep there = CharacterLevel(1, 0.3f, 4.5f, 6.5f);

            var plain = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            plain.AddCharacter(handle);
            Assert.That(Evaluate(plain, there, 1), Is.Empty, "read as the bones stand (behind), the sweep misses");
            Assert.That(handle.Source.IsSet, Is.False);

            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle, pose);
            List<SlashHitConfirmed> hits = Evaluate(detector, there, 1);
            Assert.That(pose.Asked, Is.EqualTo(1), "the current pose was put on once");
            Assert.That(hits.Count, Is.EqualTo(1), "and with it the sweep hits");
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(coldWorld.Owners.TryGetProvisionalOf(handle.Source, out ProvisionalOwnerPair pair), Is.True);
            pair.PositiveShape.TryLocalBounds(out float3 plo, out float3 phi);
            pair.NegativeShape.TryLocalBounds(out float3 nlo, out float3 nhi);
            Assert.That((math.min(plo.z, nlo.z) + math.max(phi.z, nhi.z)) * 0.5f, Is.EqualTo(5f).Within(0.5f), "the cut read the same current pose");
            yield return UntilCommitted(hits[0].Operation);
        }

        [UnityTest]
        public IEnumerator U8_Lod_TwoSlashesInOneUpdate_ShareOneCurrentPose()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            var pose = new PoseOnDemandStandIn
            {
                Root = root.transform, Bone = bone, CurrentPosition = new Vector3(0f, 0f, 5f),
                RangeBounds = new Bounds(new Vector3(0.5f, 0.5f, 3f), new Vector3(2f, 2f, 8f)),
            };
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle, pose);
            detector.Evaluate(new[] { CharacterLevel(1, 0.3f, 4.5f, 6.5f), CharacterLevel(2, 0.6f, 4.5f, 6.5f) }, new long[] { 1, 2 });
            Assert.That(pose.Asked, Is.EqualTo(1), "one current pose for both Slashes' sweeps");
            Assert.That(detector.HitCount, Is.GreaterThanOrEqualTo(1));
            for (int i = 0; i < detector.HitCount; i++)
            {
                if (detector.HitAt(i).Operation.IsSet) yield return UntilCommitted(detector.HitAt(i).Operation);
            }
        }

        [UnityTest]
        public IEnumerator U8_Lod_ACharacterNoSweepCanMeet_IsNeitherPosedNorTested()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            var pose = new PoseOnDemandStandIn
            {
                Root = root.transform, Bone = bone, CurrentPosition = new Vector3(0f, 0f, 5f),
                RangeBounds = new Bounds(new Vector3(0.5f, 0.5f, 3f), new Vector3(2f, 2f, 8f)),
            };
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle, pose);
            Assert.That(Evaluate(detector, CharacterLevel(1, 0.3f, 20f, 22f), 1), Is.Empty);
            Assert.That(pose.Asked, Is.Zero, "the range meets no sweep: no pose is put on");
            Assert.That(bone.localPosition, Is.EqualTo(Vector3.zero));
        }
    }
}
