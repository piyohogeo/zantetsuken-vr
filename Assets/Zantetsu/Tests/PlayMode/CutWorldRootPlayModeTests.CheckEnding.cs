using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The check's ending (Sandbox/CheckEnding, 2026-09-30) driven to its end with a world of fused members: a failing
    /// summary part is recorded and the later parts run, the world is reclaimed, a failing closing step is recorded and
    /// the later steps run, the held log is released and the code is settled. The world and its fusion are the fusion tests'.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        /// <summary>
        /// **The check's ending with fused members, to the end: a failed part is recorded as FAILED, the later parts run,
        /// the world is reclaimed, a failed closing step is recorded and the later steps run, the held log is released,
        /// the failure code is settled and Done is reached.** A world with two fused members, one of them under a group
        /// cut whose Final is held (a shadow, a cook's products waiting), ends through the check's own ending code
        /// (CheckEnding.Run, then CheckEnding.Complete with the held log's release): of five summary parts, one throws
        /// on a fused member's missing body and one throws an injected exception; of three closing steps the timeline's
        /// save throws an injected IOException; every failure is logged FAILED and counted, everything after it still
        /// runs, the log written after all of it holds every line in order, the code is 14 and the completion notice
        /// arrives with it. A completion with a failing close alone is 14 as well, and one without any failure is 0.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionCut_TheCheckEnding_WithFusedMembers_RecordsTheFailedParts_GoesOn_ReleasesTheLog_AndReclaimsTheWorld()
        {
            CutWorldRoot root = NewFusionWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            yield return UntilFused(root, 2, "two fused");
            root.Fusion.holdFinals = true;
            ProvisionalCutAsk ask = Ask(upper, InShapeFrame(root, upper, new float4(0f, 1f, 0f, -0.5f)));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (ask)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1));
            yield return Until(() => root.Fusion.AllCooksOver, "the cook");
            Assert.That(root.Fusion.CutsInProgress, Is.EqualTo(1), "a fused member is under a cut whose Final is held");

            var all = new List<LogicalFragmentId>();
            root.Owners.CopyFragmentsTo(all);
            var written = new List<string>();
            Application.LogCallback capture = (message, stack, type) => { if (message.StartsWith(SandboxPropSlashPlayerCheck.Prefix)) written.Add(message); };   // the check's own lines (the world's own end line is not held)
            Application.logMessageReceived += capture;
            var ran = new List<string>();
            try
            {
                SandboxPropSlashPlayerCheck.HoldLog();
                var parts = new List<CheckEnding.Part>
                {
                    new CheckEnding.Part("owners: mass and root", () =>
                    {
                        foreach (LogicalFragmentId f in all) { root.Owners.TryGet(f, out PhysicsFragmentOwner o); Assert.That(o.Mass > 0f && o.Root != null, Is.True); }
                        ran.Add("owners: mass and root");
                        SandboxPropSlashPlayerCheck.Log("owners: " + all.Count + " read");
                    }),
                    new CheckEnding.Part("owners: body mass", () =>
                    {
                        foreach (LogicalFragmentId f in all) { root.Owners.TryGet(f, out PhysicsFragmentOwner o); float m = o.Body.mass; }   // throws on a fused member: no body
                        ran.Add("owners: body mass");
                    }),
                    new CheckEnding.Part("fusion summary", () =>
                    {
                        BuildingFusion f = root.Fusion;
                        SandboxPropSlashPlayerCheck.Log("fusion summary: groups " + f.GroupCount + ", cuts in progress " + f.CutsInProgress + ", finals " + f.FinalsPublished);
                        ran.Add("fusion summary");
                    }),
                    new CheckEnding.Part("injected", () =>
                    {
                        SandboxPropSlashPlayerCheck.Log("injected: about to throw");
                        throw new InvalidOperationException("injected");
                    }),
                    new CheckEnding.Part("rest summary", () =>
                    {
                        int described = 0;
                        foreach (LogicalFragmentId f in all) if (root.Rest.TryDescribe(f, out _)) described++;
                        SandboxPropSlashPlayerCheck.Log("rest summary: " + described + " described");
                        ran.Add("rest summary");
                    }),
                };
                var ending = new CheckEnding(SandboxPropSlashPlayerCheck.Log);
                yield return ending.Run(root, parts, 15f);
                int heldCount = written.Count;
                int runFailures = ending.Failures;
                Assert.That(ending.WorldReleased, Is.True, "the world was reclaimed by the ending");
                Assert.That(root.IsReleased, Is.True);

                // The completion, as the check's Finish does it: the closing steps (one fails), the code, the log's release, Done.
                var closes = new List<CheckEnding.Part>
                {
                    new CheckEnding.Part("records close", () => ran.Add("records close")),
                    new CheckEnding.Part("timeline", () => throw new System.IO.IOException("injected save failure")),
                    new CheckEnding.Part("view target", () => ran.Add("view target")),
                };
                int doneCode = -1, doneCalls = 0;
                ending.Complete(closes, SandboxPropSlashPlayerCheck.ReleaseLog, closingFailures => CheckEnding.CodeOf(runFailures + closingFailures, false), code => { doneCode = code; doneCalls++; });
                TestContext.Out.WriteLine("parts ran: " + string.Join(", ", ran) + "; failed: " + string.Join(", ", ending.FailedParts) + "; lines held " + heldCount + ", written " + written.Count + "; code " + ending.Code + " done " + ending.Done);
                foreach (string line in written) TestContext.Out.WriteLine("  " + line);

                Assert.That(ending.Failures, Is.EqualTo(3), "two summary parts and one closing step failed");
                Assert.That(ending.FailedParts, Is.EqualTo(new[] { "owners: body mass", "injected", "timeline" }), "the failed parts, in order");
                Assert.That(ran, Is.EqualTo(new[] { "owners: mass and root", "fusion summary", "rest summary", "records close", "view target" }), "everything after a failure still ran");
                Assert.That(doneCalls == 1 && doneCode == 14 && ending.Code == 14 && ending.Done, Is.True, "the completion notice arrived once, with the failure code");
                Assert.That(heldCount, Is.Zero, "nothing was written while the log was held");
                int failedBody = written.FindIndex(l => l.Contains("FAILED: summary part 'owners: body mass' threw NullReferenceException"));
                int fusion = written.FindIndex(l => l.Contains("fusion summary: groups"));
                int failedInjected = written.FindIndex(l => l.Contains("FAILED: summary part 'injected' threw InvalidOperationException: injected"));
                int rest = written.FindIndex(l => l.Contains("rest summary: "));
                int world = written.FindIndex(l => l.Contains("ok: the world ended the ordinary way and gave everything back"));
                int failedTimeline = written.FindIndex(l => l.Contains("FAILED: summary part 'timeline' threw IOException: injected save failure"));
                int finished = written.FindIndex(l => l.Contains("finished with code 14"));
                Assert.That(failedBody, Is.GreaterThanOrEqualTo(0), "the body-mass failure is recorded as FAILED");
                Assert.That(failedInjected, Is.GreaterThan(failedBody), "the injected failure is recorded after it");
                Assert.That(fusion, Is.GreaterThan(failedBody), "the fusion summary follows the first failure");
                Assert.That(rest, Is.GreaterThan(failedInjected), "the rest summary follows the second");
                Assert.That(world, Is.GreaterThan(rest), "the world's reclaim is recorded next");
                Assert.That(failedTimeline, Is.GreaterThan(world), "the closing failure is recorded after the reclaim");
                Assert.That(finished, Is.GreaterThan(failedTimeline), "and the code after it, before the release");
                Assert.That(finished, Is.EqualTo(written.Count - 1), "the code's line is the last one held");

                // A completion with a failing close alone: 14, released, Done. One without any failure: 0.
                var order = new List<string>();
                var closeOnly = new CheckEnding(l => order.Add(l));
                int closeOnlyCode = -1;
                closeOnly.Complete(new[] { new CheckEnding.Part("records close", () => throw new System.IO.IOException("injected close failure")), new CheckEnding.Part("view target", () => order.Add("view target ran")) },
                    () => order.Add("log released"), n => CheckEnding.CodeOf(n, false), code => { closeOnlyCode = code; order.Add("done " + code); });
                TestContext.Out.WriteLine("close-only: " + string.Join(" | ", order));
                Assert.That(closeOnlyCode == 14 && closeOnly.Done && closeOnly.Failures == 1, Is.True, "a failing close alone is the failure code, and Done");
                Assert.That(order.FindIndex(l => l.Contains("FAILED: summary part 'records close'")) < order.IndexOf("view target ran")
                    && order.IndexOf("view target ran") < order.IndexOf("finished with code 14")
                    && order.IndexOf("finished with code 14") < order.IndexOf("log released")
                    && order.IndexOf("log released") < order.IndexOf("done 14"), Is.True, "the order: the failure, the later step, the code, the release, Done");
                var clean = new CheckEnding(l => { });
                int cleanCode = -1;
                clean.Complete(new[] { new CheckEnding.Part("records close", () => { }) }, () => { }, n => CheckEnding.CodeOf(n, false), code => cleanCode = code);
                Assert.That(cleanCode == 0 && clean.Done && clean.Failures == 0, Is.True, "no failure: code 0, Done");
            }
            finally
            {
                Application.logMessageReceived -= capture;
                SandboxPropSlashPlayerCheck.ReleaseLog();
            }

            yield return null;
            Assert.That(root.Fusion, Is.Null, "the fusion is gone with the world");
            Assert.That(GameObject.Find("Fused Group 1") == null && GameObject.Find("Fused Group 1 +") == null && GameObject.Find("Fused Group 1 -") == null, Is.True, "no group object remains");
            Assert.That(GameObject.Find("Shadow of " + upper.value), Is.Null, "no shadow remains");
            yield return EndWorld(root);
        }
    }
}
