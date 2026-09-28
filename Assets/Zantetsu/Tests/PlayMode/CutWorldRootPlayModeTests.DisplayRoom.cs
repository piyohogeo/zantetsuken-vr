using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The display's room through the product's own world: a profile whose display starts at two branches, two
    /// instances and two commands. Cuts carried by the update loop take it past that room, which grows while every
    /// piece -- shown before and cut since -- is drawn; a limit at the first room is the common termination, once.
    /// </summary>
    public partial class CutWorldRootPlayModeTests
    {
        private static Action<CutWorldProfile> SmallDisplayRoom(int branchLimit)
        {
            return profile =>
            {
                SetPrivate(profile, "drawCommandCapacity", 2);
                SetPrivate(profile, "drawInstanceCapacity", 2);
                SetPrivate(profile, "branchCapacity", 2);
                SetPrivate(profile, "displayInstanceCapacity", 2);
                SetPrivate(profile, "branchCapacityLimit", branchLimit);
            };
        }

        /// <summary>
        /// **Past the display's first room, the pieces shown before and the new ones are drawn together.** Two bodies
        /// fill the first room; each is cut, and a child cut again, by the world's own update loop. The room grows, the
        /// world goes on, and every live fragment is drawn in the one adopted snapshot; the moving sides are drawn where
        /// their actors are.
        /// </summary>
        [UnityTest]
        public IEnumerator PastTheDisplaysFirstRoom_EveryPieceIsDrawn_OldAndNew()
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, SmallDisplayRoom(1024));
            root.Driver.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId first = AddBody(root, new Vector3(0f, 0f, 0f));
            LogicalFragmentId second = AddBody(root, new Vector3(3f, 0f, 0f));
            yield return null;
            yield return null;
            // The box has two submeshes: two bodies already take four commands and instances, grown to; the branches
            // are still at their first room.
            Assert.That(root.Display.BranchCapacity, Is.EqualTo(2), "the layout: two bodies fit the first branches");
            int growthsBefore = root.Display.RoomGrowths;
            Assert.That(IsDrawn(root, first) && IsDrawn(root, second), Is.True);

            var taken = new CutOperationId[1];
            yield return CutAndCommit(root, first, new float4(0f, 1f, 0f, 0f), taken);
            LogicalCutOperation cutA = OperationOf(root, taken[0]);
            yield return CutAndCommit(root, second, new float4(1f, 0f, 0f, 0f), taken);
            CutOperationId b = taken[0];
            yield return CutAndCommit(root, cutA.positive, new float4(0f, 0f, 1f, 0f), taken);
            CutOperationId c = taken[0];
            yield return null;
            yield return null;

            Assert.That(root.TerminationRequested, Is.False);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.Display.LastRoomFailure, Is.Null);
            Assert.That(root.Display.RoomGrowths, Is.GreaterThan(growthsBefore), "the room grew for the cuts");
            Assert.That(root.Display.BranchCapacity, Is.GreaterThan(2));
            var live = new List<LogicalFragmentId>
            {
                cutA.negative,
                OperationOf(root, b).positive, OperationOf(root, b).negative,
                OperationOf(root, c).positive, OperationOf(root, c).negative,
            };
            foreach (LogicalFragmentId fragment in live)
            {
                Assert.That(IsDrawn(root, fragment), Is.True, "fragment " + fragment.value + " is drawn");
            }

            Assert.That(root.Display.RenderFragmentCount, Is.EqualTo(live.Count), "and nothing else");
            TestContext.WriteLine(root.Display.DescribeRoom());
            yield return EndWorld(root);
            Assert.That(root.Display.IsDisposed, Is.True);
            Assert.That(root.Display.RetiredGpuObjects, Is.Zero, "every replaced GPU object released at the ending");
        }

        /// <summary>
        /// **A need past the display's limit is the common termination, once.** The branch limit is the first room: the
        /// first cut needs a third branch. The record names what was short, the need, what was held and the limit; the
        /// termination API is called once, the display stops rather than keep drawing the older snapshot, and nothing
        /// the refused collection took is left taken.
        /// </summary>
        [UnityTest]
        public IEnumerator PastTheDisplaysLimit_IsTheTermination_Once()
        {
            ExpectTermination();
            int terminations = 0;
            CutWorldRoot root = NewWorld(out Shader _, null, () => terminations++, SmallDisplayRoom(2));
            root.Driver.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId first = AddBody(root, new Vector3(0f, 0f, 0f));
            AddBody(root, new Vector3(3f, 0f, 0f));
            yield return null;
            yield return null;
            Assert.That(root.TerminationRequested, Is.False, "the layout: two bodies fit");
            int references = root.References.LiveDisplayInstanceCount;

            LogAssert.Expect(LogType.Error, new Regex("the Player is being ended -- the display's branches could not be given room"));
            ProvisionalCutAsk ask = Ask(first, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return Until(() => root.TerminationRequested, "the display's shortfall requested the termination");
            Assert.That(root.Display.IsHalted, Is.True);
            Assert.That(root.Display.HaltReason, Is.EqualTo(LogicalCutDisplayHaltReason.RoomNotEstablished));
            StringAssert.Contains("branches", root.Display.LastRoomFailure);
            StringAssert.Contains("limit 2", root.Display.LastRoomFailure);
            Assert.That(root.Display.IsFrameOpen, Is.False, "nothing older is drawn");
            Assert.That(root.References.LiveDisplayInstanceCount, Is.EqualTo(references), "nothing the refused collection took is held");
            yield return null;
            yield return null;
            Assert.That(terminations, Is.EqualTo(1), "the Player's termination API was called once");
            Assert.That(root.TerminationCalls, Is.EqualTo(1));
            TestContext.WriteLine(root.Display.LastRoomFailure);
        }

        /// <summary>Asks for one cut, waits for its geometry commit, and says which operation it was in <paramref name="taken"/>.</summary>
        private static IEnumerator CutAndCommit(CutWorldRoot root, LogicalFragmentId source, float4 plane, CutOperationId[] taken)
        {
            ProvisionalCutAsk ask = Ask(source, plane);
            Assert.That(root.TryAsk(in ask), Is.True, "the cut was asked");
            yield return null;
            List<CutOperationId> admitted = AdmittedFor(root, new[] { source });
            Assert.That(admitted.Count, Is.EqualTo(1), "the cut was accepted");
            CutOperationId operation = admitted[0];
            yield return Until(() => root.Geometry.StageOf(operation) == CutGeometryStage.Committed, "op " + operation.value + " committed");
            taken[0] = operation;
        }
    }
}
