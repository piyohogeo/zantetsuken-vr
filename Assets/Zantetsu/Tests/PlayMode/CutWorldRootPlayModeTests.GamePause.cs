using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The game's pause on a cut world (DESIGN 4.6): with a cut in flight the world stands -- no step, no hand-over,
    /// no Geometry Commit, no new cut taken -- while its display goes on collecting for the cameras; after the pause
    /// the same cut completes as it would have; and a world ended while paused is released all the same.
    /// </summary>
    public sealed partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator Paused_WithACutInFlight_NothingProgresses_TheDisplayGoesOn_AndTheCutCompletesAfter()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            LogicalFragmentId other = AddBody(root, new Vector3(0f, 0f, 20f));
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);
            float timeScaleBefore = Time.timeScale;
            try
            {
                // A hit, published as a Provisional pair, and the pause in the same frame: its work is with the workers
                // and nothing of it has come back.
                Evaluate(detector, Level(1, 0.3f, -3f, 3f), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1));
                CutOperationId operation = hits[0].Operation;
                Assert.That(OperationOf(root, operation).state, Is.EqualTo(LogicalCutOperationState.Admitted));
                GamePause.SetManual(true);
                Assert.That(Time.timeScale, Is.Zero);
                Assert.That(CutPhysicsStep.Clock.IsPaused, Is.True, "the manual pause pauses the step");

                long stepBefore = CutPhysicsStep.Clock.StepId;
                long turnsBefore = root.Driver.PausedTurns;
                int transactionsBefore = root.Driver.Transactions.Count;
                int pairsBefore = root.Owners.ProvisionalPairCount;
                CutGeometryStage stageBefore = root.Geometry.StageOf(operation);
                Assert.That(stageBefore, Is.Not.EqualTo(CutGeometryStage.Committed));
                // Right after each frame's collection: whether the display opened this frame for the cameras.
                int openFrames = 0;
                void CountOpen()
                {
                    if (root.Display.IsFrameOpen)
                    {
                        openFrames++;
                    }
                }

                const int Frames = 90;
                CutPhysicsStep.JoinAfterCollect(CountOpen);
                try
                {
                    for (int i = 0; i < Frames; i++)
                    {
                        yield return null;
                        Assert.That(OperationOf(root, operation).state, Is.EqualTo(LogicalCutOperationState.Admitted), "not handed over, frame " + i);
                        Assert.That(root.Geometry.StageOf(operation), Is.EqualTo(stageBefore), "no Geometry Commit, frame " + i);
                    }
                }
                finally
                {
                    CutPhysicsStep.LeaveAfterCollect(CountOpen);
                }

                Assert.That(CutPhysicsStep.Clock.StepId, Is.EqualTo(stepBefore), "no physics step");
                Assert.That(root.Driver.Transactions.Count, Is.EqualTo(transactionsBefore), "the cut in flight is neither ended nor aborted");
                Assert.That(root.Owners.ProvisionalPairCount, Is.EqualTo(pairsBefore), "the pair published before the pause stands");
                Assert.That(root.Driver.PausedTurns - turnsBefore, Is.GreaterThanOrEqualTo(2L * (Frames - 1)), "the driver was called and took no turn");
                Assert.That(openFrames, Is.GreaterThanOrEqualTo(Frames - 2), "the display collected for the cameras in the paused frames");
                Assert.That(root.IsReady && root.Driver.enabled, Is.True, "nothing was disabled for the pause");

                // No cut is taken while paused: a hit on the other body changes nothing.
                Evaluate(detector, Level(2, 0.3f, -3f, 3f, 17f, 23f), 2);
                foreach (SlashHitConfirmed hit in Hits(detector))
                {
                    Assert.That(hit.Acceptance, Is.EqualTo(ProvisionalCutAcceptance.NotAccepted), "a hit made during the pause is not accepted");
                }

                Assert.That(root.Driver.Transactions.Count, Is.EqualTo(transactionsBefore));
                Assert.That(root.Owners.TryGet(other, out PhysicsFragmentOwner _), Is.True, "the other body is as it was");

                // The pause ends: the same cut is handed over and committed, and the physics steps again.
                GamePause.SetManual(false);
                Assert.That(Time.timeScale, Is.EqualTo(timeScaleBefore));
                Assert.That(CutPhysicsStep.Clock.IsPaused, Is.False);
                yield return Until(() => OperationOf(root, operation).state != LogicalCutOperationState.Admitted, "the children were handed over after the pause");
                yield return Until(() => root.Geometry.StageOf(operation) == CutGeometryStage.Committed, "committed after the pause");
                yield return Until(() => CutPhysicsStep.Clock.StepId > stepBefore, "the physics steps again");
                Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner _), Is.False, "the source was retired by its own cut, as without a pause");
            }
            finally
            {
                GamePause.ResetForTest();
                Time.timeScale = timeScaleBefore;
            }

            yield return Until(() => root.Driver.Transactions.Count == 0 || root.Owners.ProvisionalPairCount == 0, "settled");
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator EndedWhilePaused_WithACutInFlight_TheWorldIsReleased()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            AddBody(root, Vector3.zero);
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);
            float timeScaleBefore = Time.timeScale;
            try
            {
                Evaluate(detector, Level(1, 0.3f, -3f, 3f), 1);
                Assert.That(Hits(detector).Count, Is.EqualTo(1));
                GamePause.SetManual(true);
                for (int i = 0; i < 5; i++)
                {
                    yield return null;
                }

                Assert.That(root.Driver.Transactions.Count, Is.GreaterThan(0), "the cut is still in flight");

                // The ending does not pass through the paused entrances: it collects what is out and frees the world.
                yield return EndWorld(root);
                Assert.That(GamePause.IsPaused, Is.True, "still paused when it was released");
            }
            finally
            {
                GamePause.ResetForTest();
                Time.timeScale = timeScaleBefore;
            }
        }

        private sealed class CoroutineHost : MonoBehaviour
        {
        }

        /// <summary>
        /// A script's wait bounded in frames (the Prop Slash check's wait for its NPCs and for its building): paused
        /// for many times its bound while what it waits for is not there, it has not given up; what it waits for
        /// arriving during the pause does not end it either; and after the pause it ends at once. Without a pause the
        /// same bound runs out.
        /// </summary>
        [UnityTest]
        public IEnumerator AScriptsFrameBoundedWait_IsNotUsedUpByAPause_AndDoesNotEndDuringOne()
        {
            float timeScaleBefore = Time.timeScale;
            var host = TrackActor(new GameObject("Script wait host")).AddComponent<CoroutineHost>();
            try
            {
                // A wait whose condition is over already takes no frame: the script goes straight on, as it did.
                bool immediate = false;
                IEnumerator Immediate()
                {
                    // Stepped by the script itself, as the check does: yielded whole to Unity it would cost a frame.
                    IEnumerator wait = Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.WaitWhileUntilGameFrame(() => false, GamePause.FrameCount + 600);
                    while (wait.MoveNext()) yield return wait.Current;
                    immediate = true;
                }

                host.StartCoroutine(Immediate());
                Assert.That(immediate, Is.True, "no frame waited for a condition that does not hold");

                // Not paused: twenty frames of bound are twenty frames, and the wait gives up.
                const int Bound = 20;
                bool prepared = false, ended = false;
                int endedAt = -1, beganAt = Time.frameCount;
                IEnumerator Wait()
                {
                    IEnumerator wait = Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.WaitWhileUntilGameFrame(() => !prepared, GamePause.FrameCount + Bound);
                    while (wait.MoveNext()) yield return wait.Current;
                    ended = true;
                    endedAt = Time.frameCount;
                }

                host.StartCoroutine(Wait());
                for (int i = 0; i < Bound + 5 && !ended; i++)
                {
                    yield return null;
                }

                Assert.That(ended, Is.True, "the bound ran out");
                Assert.That(endedAt - beganAt, Is.EqualTo(Bound), "after its twenty frames");
                Assert.That(prepared, Is.False);

                // Paused for five times the bound while the preparation is not done: not given up.
                ended = false;
                host.StartCoroutine(Wait());
                yield return null;
                yield return null;
                GamePause.SetManual(true);
                int gameFrameAtPause = GamePause.FrameCount;
                for (int i = 0; i < Bound * 5; i++)
                {
                    yield return null;
                    Assert.That(ended, Is.False, "paused frame " + i + " of a bound of " + Bound);
                }

                Assert.That(GamePause.FrameCount, Is.EqualTo(gameFrameAtPause), "no game frame went by");

                // The preparation finishes during the pause: the script's next stage still waits for the pause's end.
                prepared = true;
                for (int i = 0; i < 10; i++)
                {
                    yield return null;
                    Assert.That(ended, Is.False, "no stage of a script goes on while paused");
                }

                GamePause.SetManual(false);
                yield return null;
                Assert.That(ended, Is.True, "and it goes on with the pause's end");

                // Paused for five times the bound and never prepared: after the pause the rest of the bound is still there.
                prepared = false;
                ended = false;
                host.StartCoroutine(Wait());
                yield return null;
                yield return null;
                GamePause.SetManual(true);
                for (int i = 0; i < Bound * 5; i++)
                {
                    yield return null;
                }

                GamePause.SetManual(false);
                for (int i = 0; i < Bound - 6; i++)
                {
                    yield return null;
                    Assert.That(ended, Is.False, "the bound's frames after the pause, frame " + i);
                }

                for (int i = 0; i < 12 && !ended; i++)
                {
                    yield return null;
                }

                Assert.That(ended, Is.True, "and then it runs out as it would have");
            }
            finally
            {
                GamePause.ResetForTest();
                Time.timeScale = timeScaleBefore;
            }
        }

        /// <summary>
        /// The Prop Slash check's own runner, ended while the game is paused and never resumed: the walk's end is seen,
        /// the run is closed, its completion is told and the runner takes its last frame -- where a Player that does not
        /// keep the scene is asked to end. (The scene here is not the check's, so the walk ends early, with its fixed
        /// code; what follows that end is the runner's ordinary end.)
        /// </summary>
        [UnityTest]
        public IEnumerator ThePropSlashRunner_EndedWhilePaused_ClosesTheRunAndTakesItsLastFrame_WithoutAResume()
        {
            float timeScaleBefore = Time.timeScale;
            bool runInBackgroundBefore = Application.runInBackground;
            string directory = System.IO.Path.Combine(Application.temporaryCachePath, "prop-slash-ending-" + System.Guid.NewGuid().ToString("N"));
            bool pausedAtTheEnd = false;
            void PauseAtTheWalksEnd(string condition, string stackTrace, LogType type)
            {
                // The walk says that the scene is not its own in the very frame it ends: the pause begins there, before
                // the runner has seen the end.
                if (!pausedAtTheEnd && condition.Contains("FAILED: the scene is not wired"))
                {
                    pausedAtTheEnd = true;
                    GamePause.SetManual(true);
                }
            }

            Application.logMessageReceived += PauseAtTheWalksEnd;
            Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.PreparedRun run = null;
            try
            {
                Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.ResetPreparedLaunch();
                run = Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.StartPrepared(
                    new[] { "test", Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.Argument, directory, Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.IterationsArgument, "1" },
                    keepOpen: true);
                Assert.That(run, Is.Not.Null, "the run was started");
                for (int i = 0; i < 60 && !run.Ended; i++)
                {
                    yield return null;
                }

                Assert.That(pausedAtTheEnd, Is.True, "the walk ended as an unwired scene's does, and the pause began in that frame");
                Assert.That(GamePause.Manual && GamePause.IsPaused, Is.True, "nobody ended the pause");
                Assert.That(run.Done, Is.True, "the run's completion was told while paused");
                Assert.That(run.Ended, Is.True, "and the runner took its last frame while paused");
                Assert.That(run.Code, Is.EqualTo(13), "with the early end's code");
                Assert.That(run.Phase, Is.EqualTo("Retained"));
                Assert.That(System.IO.Directory.Exists(directory), Is.True, "the run's own records were written");
            }
            finally
            {
                Application.logMessageReceived -= PauseAtTheWalksEnd;
                GamePause.ResetForTest();
                Time.timeScale = timeScaleBefore;
                Application.runInBackground = runInBackgroundBefore;
                GameObject host = GameObject.Find("Prop slash player check");
                if (host != null)
                {
                    Object.Destroy(host);
                }

                Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.ResetPreparedLaunch();
                try
                {
                    if (System.IO.Directory.Exists(directory))
                    {
                        System.IO.Directory.Delete(directory, true);
                    }
                }
                catch (System.IO.IOException)
                {
                }
            }
        }

        /// <summary>
        /// The ending pause check asked for on a run that ends early (the scene is not the check's): the argument is
        /// read, nothing is paused -- an early end is not the ordinary ending -- and the check's file says that it was
        /// not run, written at the runner's end beside the run's records.
        /// </summary>
        [UnityTest]
        public IEnumerator ThePropSlashRunner_AskedForTheEndingPauseCheck_OnAnEarlyEnd_PausesNothing_AndWritesThatItWasNotRun()
        {
            float timeScaleBefore = Time.timeScale;
            bool runInBackgroundBefore = Application.runInBackground;
            string directory = System.IO.Path.Combine(Application.temporaryCachePath, "prop-slash-ending-check-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.ResetPreparedLaunch();
                Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.PreparedRun run = Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.StartPrepared(
                    new[]
                    {
                        "test", Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.Argument, directory, Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.IterationsArgument, "1",
                        Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.PauseAtEndingArgument,
                    },
                    keepOpen: true);
                Assert.That(run, Is.Not.Null, "the run was started");
                for (int i = 0; i < 60 && !run.Ended; i++)
                {
                    yield return null;
                }

                Assert.That(run.Ended, Is.True, "the runner took its last frame");
                Assert.That(run.Code, Is.EqualTo(13), "the early end's code");
                Assert.That(GamePause.Manual || GamePause.IsPaused, Is.False, "the check paused nothing: this was no ordinary ending");
                string file = System.IO.Path.Combine(directory, Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.EndingPauseCheck.FileName);
                Assert.That(System.IO.File.Exists(file), Is.True, "the check's file is written at the runner's end");
                string[] lines = System.IO.File.ReadAllLines(file);
                Assert.That(lines.Length, Is.EqualTo(1));
                StringAssert.Contains("NOT RUN", lines[0]);
            }
            finally
            {
                GamePause.ResetForTest();
                Time.timeScale = timeScaleBefore;
                Application.runInBackground = runInBackgroundBefore;
                GameObject host = GameObject.Find("Prop slash player check");
                if (host != null)
                {
                    Object.Destroy(host);
                }

                Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.ResetPreparedLaunch();
                try
                {
                    if (System.IO.Directory.Exists(directory))
                    {
                        System.IO.Directory.Delete(directory, true);
                    }
                }
                catch (System.IO.IOException)
                {
                }
            }
        }

        /// <summary>
        /// The completion's own stages of the ending pause check, where the final save is made in the completion itself
        /// (every platform but the device's retained runs): the check is begun by hand in the frame the walk ends -- the
        /// scene here is not the check's, so the ordinary ending, where it begins by itself, is not reached -- and the
        /// completion that follows says the final save and the log's release, then the completion, then the runner's
        /// end, each with the game still paused. The summary and the world's release belong to the ordinary ending
        /// alone: the result names that stage as not reached, and is not "held".
        /// </summary>
        [UnityTest]
        public IEnumerator ThePropSlashRunner_TheEndingPauseCheckBegunAsTheWalkEnds_SaysTheFinalSaveTheCompletionAndTheEnd_EachStillPaused()
        {
            float timeScaleBefore = Time.timeScale;
            bool runInBackgroundBefore = Application.runInBackground;
            string directory = System.IO.Path.Combine(Application.temporaryCachePath, "prop-slash-ending-stages-" + System.Guid.NewGuid().ToString("N"));
            bool begun = false;
            void BeginAsTheWalkEnds(string condition, string stackTrace, LogType type)
            {
                // The walk says that the scene is not its own just before it completes: the check begins there.
                if (!begun && condition.Contains("FAILED: the scene is not wired"))
                {
                    begun = true;
                    Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.EndingPause.Begin(null);
                }
            }

            Application.logMessageReceived += BeginAsTheWalkEnds;
            try
            {
                Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.ResetPreparedLaunch();
                Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.PreparedRun run = Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.StartPrepared(
                    new[]
                    {
                        "test", Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.Argument, directory, Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.IterationsArgument, "1",
                        Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.PauseAtEndingArgument,
                    },
                    keepOpen: true);
                Assert.That(run, Is.Not.Null, "the run was started");
                Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.EndingPauseCheck check = Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.EndingPause;
                Assert.That(check != null && check.Asked, Is.True, "the launch's argument asks for the check");
                for (int i = 0; i < 60 && !run.Ended; i++)
                {
                    yield return null;
                }

                Assert.That(begun, Is.True, "the walk ended as an unwired scene's does, and the check was begun in that frame");
                Assert.That(run.Ended, Is.True, "the runner took its last frame");
                Assert.That(GamePause.Manual && GamePause.IsPaused, Is.True, "nobody ended the pause");
                Assert.That(check.Stages, Is.EqualTo(3), "the final save, the completion and the end");
                Assert.That(check.StagesPaused, Is.EqualTo(3), "each with the game still paused");
                Assert.That(check.Held, Is.False, "the ordinary ending's first stage was not part of this end");
                string[] lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(directory, Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.EndingPauseCheck.FileName));
                TestContext.Out.WriteLine(string.Join("\n", lines));
                Assert.That(lines.Length, Is.EqualTo(5), "the pause, three stages, the result");
                StringAssert.Contains("no menu: the manual pause set", lines[0]);
                StringAssert.Contains("the records' close, the final save and the log's release (made in the completion)", lines[1]);
                StringAssert.Contains("reached with the game still paused", lines[1]);
                StringAssert.Contains("the completion told, code 13: reached with the game still paused", lines[2]);
                StringAssert.Contains("the run's last frame", lines[3]);
                StringAssert.Contains("reached with the game still paused", lines[3]);
                StringAssert.EndsWith("3 of 4 stages reached after the pause, 3 with the game still paused: NOT HELD; not reached: SummaryAndWorld", lines[4]);
            }
            finally
            {
                Application.logMessageReceived -= BeginAsTheWalkEnds;
                GamePause.ResetForTest();
                Time.timeScale = timeScaleBefore;
                Application.runInBackground = runInBackgroundBefore;
                GameObject host = GameObject.Find("Prop slash player check");
                if (host != null)
                {
                    Object.Destroy(host);
                }

                Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.ResetPreparedLaunch();
                try
                {
                    if (System.IO.Directory.Exists(directory))
                    {
                        System.IO.Directory.Delete(directory, true);
                    }
                }
                catch (System.IO.IOException)
                {
                }
            }
        }

        /// <summary>
        /// The ending's waits of the Prop Slash check, each run under a pause that is never ended: a report's write is
        /// collected when it is done, and the held log is written out over frames. The same write waited for inside the
        /// script stands still while paused, as a script's stage does.
        /// </summary>
        [UnityTest]
        public IEnumerator AnEndingsSavesAndLogRelease_GoOnWhilePaused_AndAScriptsSaveStandsStill()
        {
            float timeScaleBefore = Time.timeScale;
            var host = TrackActor(new GameObject("Ending wait host")).AddComponent<CoroutineHost>();
            int released = 0;
            void CountReleased(string condition, string stackTrace, LogType type)
            {
                if (condition.StartsWith(Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.Prefix + "held line "))
                {
                    released++;
                }
            }

            Application.logMessageReceived += CountReleased;
            try
            {
                GamePause.SetManual(true);

                // An ending's write: done on its worker, collected over frames, with the game paused throughout.
                bool endingCollected = false, scriptCollected = false;
                var endingWrite = new Zantetsu.Sandbox.CheckReportWrite(() => System.Threading.Thread.Sleep(30));
                var scriptWrite = new Zantetsu.Sandbox.CheckReportWrite(() => System.Threading.Thread.Sleep(30));
                IEnumerator Collect(Zantetsu.Sandbox.CheckReportWrite write, bool ending, System.Action collected)
                {
                    IEnumerator wait = Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.WaitForWrite(write, ending);
                    while (wait.MoveNext()) yield return wait.Current;
                    collected();
                }

                host.StartCoroutine(Collect(endingWrite, true, () => endingCollected = true));
                host.StartCoroutine(Collect(scriptWrite, false, () => scriptCollected = true));
                for (int i = 0; i < 600 && !(endingWrite.Done && scriptWrite.Done && endingCollected); i++)
                {
                    yield return null;
                }

                Assert.That(endingWrite.Done && scriptWrite.Done, Is.True, "both writes finished on their workers");
                Assert.That(endingCollected, Is.True, "the ending's write was collected while paused");
                for (int i = 0; i < 20; i++)
                {
                    yield return null;
                }

                Assert.That(scriptCollected, Is.False, "the script's write is done and waits for the pause's end: no stage of a script goes on");

                // The held log: forty lines, written out over frames while paused.
                Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.HoldLog();
                for (int i = 0; i < 40; i++)
                {
                    Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.Log("held line " + i);
                }

                Assert.That(released, Is.Zero, "held: nothing written yet");
                bool logReleased = false;
                IEnumerator Release()
                {
                    IEnumerator release = Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.ReleaseLogAcrossFrames();
                    while (release.MoveNext()) yield return release.Current;
                    logReleased = true;
                }

                int frameBefore = Time.frameCount;
                host.StartCoroutine(Release());
                for (int i = 0; i < 60 && !logReleased; i++)
                {
                    yield return null;
                }

                Assert.That(logReleased, Is.True, "the log's release finished while paused");
                Assert.That(released, Is.EqualTo(40), "every held line was written, once");
                Assert.That(Time.frameCount - frameBefore, Is.GreaterThanOrEqualTo(2), "over several frames, as it is budgeted");
                Assert.That(GamePause.Manual && GamePause.IsPaused, Is.True, "nobody ended the pause");

                GamePause.SetManual(false);
                yield return null;
                Assert.That(scriptCollected, Is.True, "and the script's write is collected with the pause's end");
            }
            finally
            {
                Application.logMessageReceived -= CountReleased;
                Zantetsu.Sandbox.SandboxPropSlashPlayerCheck.ReleaseLog();
                GamePause.ResetForTest();
                Time.timeScale = timeScaleBefore;
            }
        }

        [UnityTest]
        public IEnumerator Paused_AnAskMadeBeforeIt_IsTakenUpAfterIt_NotDuringIt()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            float timeScaleBefore = Time.timeScale;
            try
            {
                // Paused first; then an ask through the world's own entrance, which the driver takes up in its update.
                GamePause.SetManual(true);
                Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner owner), Is.True);
                var ask = new ProvisionalCutAsk
                {
                    source = body,
                    plane = new Unity.Mathematics.float4(0f, 1f, 0f, -0.3f),
                    renderAnchor = new Unity.Mathematics.float3(0f, 0.3f, 0f),
                };
                Assert.That(root.TryAsk(in ask), Is.True, "asking is not refused: the ask waits");
                for (int i = 0; i < 30; i++)
                {
                    yield return null;
                    Assert.That(root.Driver.Transactions.Count, Is.Zero, "not taken up while paused, frame " + i);
                }

                Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner _), Is.True);
                GamePause.SetManual(false);
                yield return Until(() => root.Driver.Transactions.Count > 0 || !root.Owners.TryGet(body, out PhysicsFragmentOwner _), "the ask was taken up after the pause");
            }
            finally
            {
                GamePause.ResetForTest();
                Time.timeScale = timeScaleBefore;
            }

            yield return Until(() => root.Driver.Transactions.Count == 0 || root.Owners.ProvisionalPairCount == 0, "settled");
            yield return EndWorld(root);
        }
    }
}
