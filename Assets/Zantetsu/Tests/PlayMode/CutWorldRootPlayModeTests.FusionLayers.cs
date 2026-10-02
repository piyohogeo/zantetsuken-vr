using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The exclusion of a group cut's copies from its crossed members by a lent layer pair (TL, 2026-09-30), against the
    /// fusion: the relation read from real contacts (Physics.ContactEvent) while the Finals are held and the sides move and
    /// turn, two group cuts at once, a partial Final, a pair wanted with none free, the ending and a refused cook -- and the
    /// rest, the hit targets and the display left as they were.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        /// <summary>The contacts the simulation reports between colliders of named kinds (A: crossed, B: copies, V: the others of the group cut).</summary>
        private sealed class ContactTally
        {
            public readonly Dictionary<int, string> kind = new Dictionary<int, string>();
            public readonly Dictionary<string, int> pairs = new Dictionary<string, int>();
            public int events, seen, unknown;

            public void Add(IEnumerable<Collider> colliders, string name)
            {
                foreach (Collider c in colliders) if (c != null) kind[c.GetInstanceID()] = name;
            }

            public void On(PhysicsScene scene, NativeArray<ContactPairHeader>.ReadOnly headers)
            {
                events++;
                for (int h = 0; h < headers.Length; h++)
                {
                    ContactPairHeader header = headers[h];
                    for (int p = 0; p < header.PairCount; p++)
                    {
                        ref readonly ContactPair pair = ref header.GetContactPair(p);
                        seen++;
                        if (pair.IsCollisionExit) continue;
                        if (!kind.ContainsKey(pair.ColliderInstanceID) || !kind.ContainsKey(pair.OtherColliderInstanceID)) unknown++;
                        if (!kind.TryGetValue(pair.ColliderInstanceID, out string a) || !kind.TryGetValue(pair.OtherColliderInstanceID, out string b)) continue;
                        string key = string.CompareOrdinal(a, b) < 0 ? a + "-" + b : b + "-" + a;
                        pairs.TryGetValue(key, out int n);
                        pairs[key] = n + 1;
                    }
                }
            }

            public override string ToString()
            {
                var keys = new List<string>(pairs.Keys);
                keys.Sort(string.CompareOrdinal);
                var line = new System.Text.StringBuilder();
                foreach (string k in keys) line.Append(k).Append(' ').Append(pairs[k]).Append("; ");
                return line.Append("(events ").Append(events).Append(", pairs seen ").Append(seen).Append(", with an unknown collider ").Append(unknown).Append(')').ToString();
            }
        }

        /// <summary>A building on the floor, cut into four (upper/lower by an ordinary cut, left/right by a group cut) and resting again as one group. Returns the four members.</summary>
        private IEnumerator FourMemberGroup(CutWorldRoot root, Vector3 at, long slash, List<LogicalFragmentId> members)
        {
            LogicalFragmentId building = AddBuilding(root, at);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            yield return UntilWithin(() => GroupsOfBuilding(root, halves[0]) is List<FusedGroup> g && g.Count == 1 && g[0].MemberCount == 2 && !g[0].Busy && g[0].Kinematic, 30f, "the halves fused");
            int cuts = root.Fusion.GroupCuts;
            Assert.That(root.TryAsk(SlashAsk(root, halves[0], new float4(1f, 0f, 0f, -(at.x + 0.3f)), slash, 0f)), Is.True);
            yield return UntilWithin(() => root.Fusion.GroupCuts > cuts, 30f, "the vertical group cut");
            yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, 30f, "its Finals");
            yield return UntilWithin(() => GroupOfBuildingAt(root, at) is FusedGroup g && g.MemberCount == 4 && !g.Busy && g.Kinematic && GroupsNear(root, at) == 1, 40f, "the four rest as one group");
            members.Clear();
            members.AddRange(GroupOfBuildingAt(root, at).Fragments);
        }

        private static List<FusedGroup> GroupsOfBuilding(CutWorldRoot root, LogicalFragmentId member)
        {
            var groups = new List<FusedGroup>();
            if (!root.Owners.TryGet(member, out PhysicsFragmentOwner owner) || !owner.IsFused) return groups;
            foreach (FusedGroup g in root.Fusion.Groups) if (g.Key == owner.Group.Key) groups.Add(g);
            return groups;
        }

        private static FusedGroup GroupOfBuildingAt(CutWorldRoot root, Vector3 at)
        {
            foreach (FusedGroup g in root.Fusion.Groups) if (g.Root != null && Mathf.Abs(g.Root.transform.position.x - at.x) < 2.5f) return g;
            return null;
        }

        private static int GroupsNear(CutWorldRoot root, Vector3 at)
        {
            int n = 0;
            foreach (FusedGroup g in root.Fusion.Groups) if (g.Root != null && Mathf.Abs(g.Root.transform.position.x - at.x) < 2.5f) n++;
            return n;
        }

        /// <summary>After a group cut's publication (Finals held): each member of it is crossed (it has a copy) or not; the tally learns A (crossed), B (copies) and V (the others) under a prefix.</summary>
        private static List<LogicalFragmentId> Classify(CutWorldRoot root, List<LogicalFragmentId> members, ContactTally tally, string prefix, List<MeshCollider> ownOut = null, List<MeshCollider> copiesOut = null)
        {
            var crossed = new List<LogicalFragmentId>();
            foreach (LogicalFragmentId f in members)
            {
                Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.IsFused, Is.True);
                var own = new List<MeshCollider>(o.Group.byFragment[f].colliders);
                GameObject shadow = GameObject.Find("Shadow of " + f.value);
                if (shadow != null)
                {
                    crossed.Add(f);
                    MeshCollider[] copies = shadow.GetComponentsInChildren<MeshCollider>();
                    tally.Add(own, prefix + "A");
                    tally.Add(copies, prefix + "B");
                    ownOut?.AddRange(own);
                    copiesOut?.AddRange(copies);
                }
                else
                {
                    tally.Add(own, prefix + "V");
                }
            }

            return crossed;
        }

        private static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        /// <summary>Until the world's physics has stepped n times (frames in a batch run pass faster than its steps).</summary>
        private static IEnumerator Steps(int n)
        {
            long from = CutPhysicsStep.Clock.StepId;
            yield return UntilWithin(() => CutPhysicsStep.Clock.StepId - from >= n, 20f, n + " physics steps");
        }

        /// <summary>
        /// **The copies never touch the crossed members, and everything else touches as before, while the sides move and
        /// turn -- by a layer pair as by pairs.** Four members resting as one group; a horizontal group cut crosses the two
        /// upper ones (A) and leaves the lower two (V); the Finals are held. The simulation reports no A-B contact, and the
        /// A-V contacts stay; the upper side moved into the lower and turned: still no A-B, and A-V. By a layer pair: the
        /// crossed members' collider objects and the copies on the pair's two layers, the Roots unchanged; the rest woke no
        /// piece for a lost support and released no group; the crossed members are still hit targets with their placement.
        /// After the Finals the children stand on the base layer, and the pair is back.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionLayers_TheCopiesNeverTouchTheCrossed_AndTheRestTouchesAsBefore_WhileTheSidesMove([Values(false, true)] bool byPairs)
        {
            CutWorldRoot root = NewFusionWorld();
            root.Fusion.exclusionByPairsForTest = byPairs;
            var members = new List<LogicalFragmentId>();
            yield return FourMemberGroup(root, Vector3.zero, 1, members);
            var tally = new ContactTally();
            Physics.ContactEvent += tally.On;
            try
            {
                int lentBefore = ExclusionLayerPairs.LentNow, byLayersBefore = root.Fusion.ExcludedByLayers;
                int wokenBefore = root.Rest.WokenBySupportLoss, releasedBefore = root.Rest.GroupsReleased, lostBefore = root.Rest.LostGrounds;
                root.Fusion.holdFinals = true;
                int cuts = root.Fusion.GroupCuts;
                Assert.That(root.TryAsk(SlashAsk(root, members[0], new float4(0f, 1f, 0f, -0.5f), 2, 0f)), Is.True, "a horizontal cut through the upper members");
                yield return UntilWithin(() => root.Fusion.GroupCuts > cuts, 30f, "the group cut published");
                var own = new List<MeshCollider>();
                var copies = new List<MeshCollider>();
                List<LogicalFragmentId> crossed = Classify(root, members, tally, "", own, copies);
                Assert.That(crossed.Count, Is.EqualTo(2), "the two upper members were crossed");
                if (!byPairs)
                {
                    Assert.That(ExclusionLayerPairs.LentNow, Is.EqualTo(lentBefore + 1), "one pair lent to the cut");
                    Assert.That(root.Fusion.ExcludedByLayers, Is.EqualTo(byLayersBefore + 1));
                    int la = own[0].gameObject.layer, lb = copies[0].gameObject.layer;
                    Assert.That(la, Is.Not.EqualTo(ExclusionLayerPairs.BaseLayer));
                    Assert.That(Physics.GetIgnoreLayerCollision(la, lb), Is.True, "the pair's layers apart");
                    foreach (MeshCollider c in own) Assert.That(c.gameObject.layer, Is.EqualTo(la));
                    foreach (MeshCollider c in copies) Assert.That(c.gameObject.layer, Is.EqualTo(lb));
                    for (int l = 0; l < 32; l++) if (l != la && l != lb) Assert.That(Physics.GetIgnoreLayerCollision(la, l) == Physics.GetIgnoreLayerCollision(0, l) || l == 30 || l == 31 || IsPoolLayer(l), Is.True, "layer " + la + " meets layer " + l + " as the base does");
                }
                else
                {
                    foreach (MeshCollider c in own) Assert.That(c.gameObject.layer, Is.EqualTo(ExclusionLayerPairs.BaseLayer), "by pairs, the layers stay");
                }

                foreach (LogicalFragmentId f in members)
                {
                    Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.Root.layer == ExclusionLayerPairs.BaseLayer, Is.True, "member " + f.value + "'s Root is untouched");
                    Assert.That(IsHitTarget(root, f), Is.True, "member " + f.value + " is still a hit target");
                    Assert.That(root.Placement.TryGetGeometryLocalToWorld(f, default, 0f, out Matrix4x4 _), Is.Not.EqualTo(VpFragmentPlacementKind.Missing), "and is placed");
                }

                foreach (LogicalFragmentId f in members)
                {
                    root.Owners.TryGet(f, out PhysicsFragmentOwner fo);
                    MeshCollider c0 = fo.Group.byFragment[f].colliders[0];
                    TestContext.Out.WriteLine("member " + f.value + ": providesContacts " + c0.providesContacts + ", enabled " + c0.enabled + ", body " + (c0.attachedRigidbody != null ? c0.attachedRigidbody.name + " kinematic " + c0.attachedRigidbody.isKinematic : "none") + ", layer " + c0.gameObject.layer);
                }

                foreach (MeshCollider c in copies) TestContext.Out.WriteLine("copy: providesContacts " + c.providesContacts + ", enabled " + c.enabled + ", active " + c.gameObject.activeInHierarchy + ", body " + (c.attachedRigidbody != null ? c.attachedRigidbody.name + " kinematic " + c.attachedRigidbody.isKinematic : "none"));
                yield return Steps(10);
                string resting = tally.ToString();
                // The upper side moved down into the lower one and turned.
                FusedGroup upper = null;
                root.Owners.TryGet(crossed[0], out PhysicsFragmentOwner co);
                upper = co.Group;
                upper.Body.position += new Vector3(0.05f, -0.3f, 0.02f);
                upper.Body.rotation = Quaternion.Euler(4f, 20f, 3f) * upper.Body.rotation;
                tally.pairs.Clear();
                yield return Steps(6);
                string moved = tally.ToString();
                TestContext.Out.WriteLine((byPairs ? "by pairs" : "by a layer pair") + ": resting contacts " + resting + "\n  after the move and turn: " + moved
                    + "\n  exclusion calls " + root.Fusion.ExclusionCalls + ", pairs logical " + root.Fusion.IgnoredPairs + ", lent now " + ExclusionLayerPairs.LentNow + " (" + ExclusionLayerPairs.PairCount + " pairs; excluded " + ExclusionLayerPairs.Excluded + ")");
                Assert.That(resting.Contains("A-B"), Is.False, "no copy touches a crossed member: " + resting);
                Assert.That(resting, Does.Contain("A-V"), "the crossed members rest on the others");
                Assert.That(moved.Contains("A-B"), Is.False, "nor after the move and turn: " + moved);
                Assert.That(moved, Does.Contain("A-V"), "and they still meet the others");
                Assert.That(root.Rest.WokenBySupportLoss, Is.EqualTo(wokenBefore), "no piece woken for a lost support by the exclusion");
                Assert.That(root.Rest.GroupsReleased, Is.EqualTo(releasedBefore), "no group released");
                Assert.That(root.Rest.LostGrounds, Is.EqualTo(lostBefore), "no ground lost");

                root.Fusion.holdFinals = false;
                yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, 30f, "the Finals");
                Assert.That(ExclusionLayerPairs.LentNow, Is.EqualTo(lentBefore), "the pair is back after the Finals");
                foreach (FusedGroup g in root.Fusion.Groups)
                {
                    foreach (LogicalFragmentId f in g.Fragments)
                    {
                        foreach (MeshCollider c in g.byFragment[f].colliders) Assert.That(c.gameObject.layer, Is.EqualTo(ExclusionLayerPairs.BaseLayer), "member " + f.value + " stands on the base layer after the Finals");
                    }
                }
            }
            finally
            {
                Physics.ContactEvent -= tally.On;
            }

            yield return EndWorld(root);
        }

        private static bool IsPoolLayer(int layer)
        {
            for (int i = 0; i < ExclusionLayerPairs.PairCount; i++) { (int a, int b) = ExclusionLayerPairs.LayersOf(i); if (a == layer || b == layer) return true; }
            return false;
        }

        /// <summary>
        /// **Two group cuts at once borrow two pairs, and one cut's copies still meet the other cut's crossed members.** Two
        /// four-member buildings apart; a horizontal cut of each in one frame, Finals held: two pairs lent, different. The
        /// second building's lower side is carried onto the first's upper side: its copies (B2) meet the first's crossed
        /// members (A1), while A1-B1 and A2-B2 never meet. Both pairs are back after the Finals.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionLayers_TwoGroupCutsAtOnce_BorrowTwoPairs_AndTheirCopiesStillMeetTheOthersCrossed()
        {
            CutWorldRoot root = NewFusionWorld();
            var first = new List<LogicalFragmentId>();
            var second = new List<LogicalFragmentId>();
            yield return FourMemberGroup(root, Vector3.zero, 1, first);
            yield return FourMemberGroup(root, new Vector3(6f, 0f, 0f), 2, second);
            var tally = new ContactTally();
            Physics.ContactEvent += tally.On;
            try
            {
                int lentBefore = ExclusionLayerPairs.LentNow;
                root.Fusion.holdFinals = true;
                int cuts = root.Fusion.GroupCuts;
                Assert.That(root.TryAsk(SlashAsk(root, first[0], new float4(0f, 1f, 0f, -0.5f), 3, 0f)), Is.True);
                Assert.That(root.TryAsk(SlashAsk(root, second[0], new float4(0f, 1f, 0f, -0.5f), 4, 0f)), Is.True);
                yield return UntilWithin(() => root.Fusion.GroupCuts >= cuts + 2, 30f, "both group cuts published");
                var a1 = new List<MeshCollider>();
                var b1 = new List<MeshCollider>();
                var a2 = new List<MeshCollider>();
                var b2 = new List<MeshCollider>();
                List<LogicalFragmentId> crossed1 = Classify(root, first, tally, "1", a1, b1);
                List<LogicalFragmentId> crossed2 = Classify(root, second, tally, "2", a2, b2);
                Assert.That(crossed1.Count == 2 && crossed2.Count == 2, Is.True);
                Assert.That(ExclusionLayerPairs.LentNow, Is.EqualTo(lentBefore + 2), "two pairs lent");
                Assert.That(a1[0].gameObject.layer, Is.Not.EqualTo(a2[0].gameObject.layer), "not the same pair");
                Assert.That(Physics.GetIgnoreLayerCollision(a1[0].gameObject.layer, b2[0].gameObject.layer), Is.False, "the first's crossed and the second's copies meet");
                // The second building's lower side (its copies among it) carried onto the first's upper side.
                root.Owners.TryGet(second.Find(f => !crossed2.Contains(f)), out PhysicsFragmentOwner lower2);
                root.Owners.TryGet(crossed1[0], out PhysicsFragmentOwner upper1);
                lower2.Group.Body.position = upper1.Group.Body.position + new Vector3(0.1f, 0.2f, 0.05f);
                tally.pairs.Clear();
                yield return Steps(6);
                string met = tally.ToString();
                TestContext.Out.WriteLine("two cuts at once: " + met);
                Assert.That(met, Does.Contain("1A-2B"), "the second's copies meet the first's crossed members");
                Assert.That(met.Contains("1A-1B") || met.Contains("2A-2B"), Is.False, "a cut's copies never meet its own crossed members: " + met);
                root.Fusion.holdFinals = false;
                yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, 30f, "the Finals");
                Assert.That(ExclusionLayerPairs.LentNow, Is.EqualTo(lentBefore), "both pairs are back");
            }
            finally
            {
                Physics.ContactEvent -= tally.On;
            }

            yield return EndWorld(root);
        }

        /// <summary>
        /// **A partial Final keeps the pair until the last member's end.** One of the two crossed members' Finals is held:
        /// after the other is published the pair is still lent, the held member and its copy are still apart and on the
        /// pair's layers, and the finished member's children stand on the base layer; the last Final gives the pair back.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionLayers_APartialFinal_KeepsThePairUntilTheLast()
        {
            CutWorldRoot root = NewFusionWorld();
            var members = new List<LogicalFragmentId>();
            yield return FourMemberGroup(root, Vector3.zero, 1, members);
            int lentBefore = ExclusionLayerPairs.LentNow;
            LogicalFragmentId held = default;
            bool holding = true;
            root.Fusion.holdFinalOfHook = f => holding && f.Equals(held);
            root.Fusion.holdFinals = true;
            int cuts = root.Fusion.GroupCuts, published = root.Fusion.FinalsPublished;
            Assert.That(root.TryAsk(SlashAsk(root, members[0], new float4(0f, 1f, 0f, -0.5f), 2, 0f)), Is.True);
            yield return UntilWithin(() => root.Fusion.GroupCuts > cuts, 30f, "the group cut");
            var tally = new ContactTally();
            var own = new List<MeshCollider>();
            var copies = new List<MeshCollider>();
            List<LogicalFragmentId> crossed = Classify(root, members, tally, "", own, copies);
            held = crossed[1];
            int la = own[0].gameObject.layer, lb = copies[0].gameObject.layer;
            root.Fusion.holdFinals = false;
            yield return UntilWithin(() => root.Fusion.FinalsPublished > published, 30f, "the first Final");
            yield return Frames(3);
            Assert.That(root.Fusion.CutsInProgress, Is.EqualTo(1), "the held one is still in progress");
            Assert.That(ExclusionLayerPairs.LentNow, Is.EqualTo(lentBefore + 1), "the pair is still lent");
            Assert.That(root.Owners.TryGet(held, out PhysicsFragmentOwner ho), Is.True);
            foreach (MeshCollider c in ho.Group.byFragment[held].colliders) Assert.That(c.gameObject.layer, Is.EqualTo(la), "the held member still on the pair's first layer");
            GameObject heldShadow = GameObject.Find("Shadow of " + held.value);
            Assert.That(heldShadow, Is.Not.Null);
            foreach (MeshCollider c in heldShadow.GetComponentsInChildren<MeshCollider>()) Assert.That(c.gameObject.layer, Is.EqualTo(lb), "and its copy on the second");
            foreach (FusedGroup g in root.Fusion.Groups)
            {
                foreach (LogicalFragmentId f in g.Fragments)
                {
                    if (f.Equals(held)) continue;
                    foreach (MeshCollider c in g.byFragment[f].colliders) Assert.That(c.gameObject.layer, Is.EqualTo(ExclusionLayerPairs.BaseLayer), "member " + f.value + " (a finished child or uncut) on the base layer");
                }
            }

            holding = false;
            yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, 30f, "the last Final");
            Assert.That(ExclusionLayerPairs.LentNow, Is.EqualTo(lentBefore), "the pair is back after the last Final");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **With no pair free, the cut waits before anything is admitted, and goes on when one comes back.** Every pair is
        /// lent to another user; a group cut is asked: its preparation waits (Pending, no operation admitted, the wait
        /// named); one pair given back, the cut is published by it.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionLayers_NoFreePair_ThePreparationWaits_AndGoesOnWhenOneComesBack()
        {
            CutWorldRoot root = NewFusionWorld();
            var members = new List<LogicalFragmentId>();
            yield return FourMemberGroup(root, Vector3.zero, 1, members);
            var other = new object();
            ExclusionLayerPairs.Attach(other);
            var taken = new List<int>();
            try
            {
                for (int pair = ExclusionLayerPairs.TryLend(other); pair >= 0; pair = ExclusionLayerPairs.TryLend(other)) taken.Add(pair);
                Assert.That(taken.Count, Is.GreaterThan(0));
                int operations = OperationCount(root), cuts = root.Fusion.GroupCuts, waits = root.Fusion.LayerPairWaits;
                Assert.That(root.TryAsk(SlashAsk(root, members[0], new float4(0f, 1f, 0f, -0.5f), 2, 0f)), Is.True);
                yield return UntilWithin(() => root.Fusion.LayerPairWaits > waits, 20f, "the preparation waits for a pair");
                yield return Frames(5);
                Assert.That(root.Fusion.GroupCuts, Is.EqualTo(cuts), "not published");
                Assert.That(OperationCount(root), Is.EqualTo(operations), "nothing admitted");
                Assert.That(root.Fusion.PreparationsInFlight, Is.EqualTo(1));
                StringAssert.Contains("waiting for a free layer pair 1", root.Fusion.DescribeUnsettled());
                TestContext.Out.WriteLine("waiting: " + root.Fusion.DescribeUnsettled() + "; Steps waited " + (root.Fusion.LayerPairWaits - waits));
                ExclusionLayerPairs.Return(taken[taken.Count - 1], other);
                taken.RemoveAt(taken.Count - 1);
                yield return UntilWithin(() => root.Fusion.GroupCuts > cuts, 20f, "published once a pair came back");
                yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, 30f, "its Finals");
                Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            }
            finally
            {
                foreach (int pair in taken) ExclusionLayerPairs.Return(pair, other);
                ExclusionLayerPairs.Detach(other);
            }

            yield return EndWorld(root);
        }

        /// <summary>
        /// **The ending, and a refused cook, give the pairs back; the last user puts the matrix back.** A group cut whose
        /// cook PhysX refuses for one member: its failure and the other's Final end the cut, and the pair is back. Another
        /// group cut with its Finals held when the world ends: the ending gives its pair back, and with no user left the
        /// pool's layers meet every layer as they did before the world.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionLayers_TheEndingAndARefusedCook_GiveThePairsBack_AndTheMatrixIsPutBack()
        {
            LogAssert.ignoreFailingMessages = true;
            var before = new bool[32, 32];
            for (int a = 0; a < 32; a++) for (int b = 0; b < 32; b++) before[a, b] = Physics.GetIgnoreLayerCollision(a, b);
            CutWorldRoot root = NewFusionWorld();
            var members = new List<LogicalFragmentId>();
            yield return FourMemberGroup(root, Vector3.zero, 1, members);
            int lentBefore = ExclusionLayerPairs.LentNow;
            var serial = new int[1];
            RefuseTheNextCut(root, serial);
            int cuts = root.Fusion.GroupCuts, failed = root.Fusion.FinalsFailed;
            Assert.That(root.TryAsk(SlashAsk(root, members[0], new float4(0f, 1f, 0f, -0.5f), 2, 0f)), Is.True);
            yield return UntilWithin(() => root.Fusion.GroupCuts > cuts, 30f, "the group cut");
            yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, 30f, "its Finals and the failure");
            root.Cook.meshOverrideForTest = null;
            Assert.That(root.Fusion.FinalsFailed, Is.EqualTo(failed + 1), "one member refused by PhysX");
            Assert.That(ExclusionLayerPairs.LentNow, Is.EqualTo(lentBefore), "the pair is back after the refusal and the other Final");

            yield return UntilWithin(() => root.Fusion.IsSettled && root.Fusion.GroupCount >= 1, 40f, "settled");
            FusedGroup any = null;
            foreach (FusedGroup g in root.Fusion.Groups) if (!g.Busy && g.MemberCount > 0) { any = g; break; }
            Assert.That(any, Is.Not.Null);
            root.Fusion.holdFinals = true;
            cuts = root.Fusion.GroupCuts;
            LogicalFragmentId member = any.Representative;
            Bounds bounds = ColliderBoundsOf(root, member);
            Assert.That(root.TryAsk(SlashAsk(root, member, new float4(1f, 0f, 0f, -bounds.center.x), 3, 0f)), Is.True);
            yield return UntilWithin(() => root.Fusion.GroupCuts > cuts, 30f, "a second group cut, its Finals held");
            Assert.That(ExclusionLayerPairs.LentNow, Is.EqualTo(lentBefore + 1), "lent while its Finals are held");
            yield return EndWorld(root);
            Assert.That(ExclusionLayerPairs.LentNow, Is.Zero, "the ending gave the pair back");
            for (int a = 0; a < 32; a++) for (int b = 0; b < 32; b++) Assert.That(Physics.GetIgnoreLayerCollision(a, b), Is.EqualTo(before[a, b]), "the matrix at " + a + "," + b + " is as before the world");
        }

        /// <summary>
        /// **Two users borrow apart, and the matrix is the pool's only while a user is there.** Two users (two worlds) lend
        /// pairs: never the same one; the pool's layers meet every layer as the base layer does, save their partner; one
        /// user's detach returns only its loans; the last detach puts every row back.
        /// </summary>
        [Test]
        public void ExclusionLayerPairs_TwoUsersBorrowApart_AndTheLastPutsTheMatrixBack()
        {
            var before = new bool[32, 32];
            for (int a = 0; a < 32; a++) for (int b = 0; b < 32; b++) before[a, b] = Physics.GetIgnoreLayerCollision(a, b);
            object one = new object(), two = new object();
            ExclusionLayerPairs.Attach(one);
            ExclusionLayerPairs.Attach(two);
            try
            {
                Assert.That(ExclusionLayerPairs.PairCount, Is.GreaterThan(1));
                TestContext.Out.WriteLine("pairs " + ExclusionLayerPairs.PairCount + "; left out: " + ExclusionLayerPairs.Excluded);
                int p1 = ExclusionLayerPairs.TryLend(one), p2 = ExclusionLayerPairs.TryLend(two);
                Assert.That(p1 >= 0 && p2 >= 0 && p1 != p2, Is.True, "two users, two pairs");
                for (int i = 0; i < ExclusionLayerPairs.PairCount; i++)
                {
                    (int a, int b) = ExclusionLayerPairs.LayersOf(i);
                    Assert.That(Physics.GetIgnoreLayerCollision(a, b), Is.True, "pair " + i + " apart");
                    for (int l = 0; l < 32; l++)
                    {
                        if (l == a || l == b) continue;
                        bool expected = IsPoolLayer(l) ? Physics.GetIgnoreLayerCollision(0, 0) : Physics.GetIgnoreLayerCollision(0, l);
                        Assert.That(Physics.GetIgnoreLayerCollision(a, l), Is.EqualTo(expected), "layer " + a + " meets " + l + " as the base does");
                    }
                }

                Assert.That(() => ExclusionLayerPairs.Return(p1, two), Throws.InvalidOperationException, "a pair goes back only by its borrower");
                ExclusionLayerPairs.Detach(one);
                Assert.That(ExclusionLayerPairs.IsLentTo(p1, one), Is.False, "the detached user's loan is back");
                Assert.That(ExclusionLayerPairs.IsLentTo(p2, two), Is.True, "the other's is not");
                Assert.That(Physics.GetIgnoreLayerCollision(ExclusionLayerPairs.LayersOf(0).a, ExclusionLayerPairs.LayersOf(0).b), Is.True, "the matrix stays while a user is there");
                ExclusionLayerPairs.Return(p2, two);
            }
            finally
            {
                ExclusionLayerPairs.Detach(one);
                ExclusionLayerPairs.Detach(two);
            }

            for (int a = 0; a < 32; a++) for (int b = 0; b < 32; b++) Assert.That(Physics.GetIgnoreLayerCollision(a, b), Is.EqualTo(before[a, b]), "the matrix at " + a + "," + b + " is back");
        }
    }
}
