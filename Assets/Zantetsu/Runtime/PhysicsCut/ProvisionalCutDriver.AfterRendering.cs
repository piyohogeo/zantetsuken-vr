using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// One more turn of the driver's frame, after the frame has been rendered (DESIGN 4.4: a frame may take several
    /// non-blocking turns sharing one budget; where and how many is the implementation's).
    /// <para>
    /// **Why after rendering.** After the late updates, <see cref="CutPhysicsStep"/> decides the frame's simulation
    /// and lets the display settle this frame's snapshot (<see cref="CollectSnapshot"/>), which the cameras draw. A
    /// turn between that settling and the drawing would change the published state under a snapshot already taken; a
    /// turn after the drawing does not: what it hands off or commits is simulated by the next frame's decision and
    /// collected by the next frame's <see cref="VpLogicalCutDisplay.TryBeginFrame"/>, exactly as if the next frame's
    /// update had done it, and what the display retires is kept, as always, until that next adoption. So the turn sits
    /// right after <see cref="PostLateUpdate.FinishFrameRendering"/>, where the frame's cameras have rendered.
    /// </para>
    /// <para>
    /// **The same frame, the same budget.** The turn is <see cref="Advance"/> on this frame's own id, so the dispatcher
    /// continues the frame and refills nothing. It takes back only what has already ended and offers only what is
    /// ready: nothing is waited for, completed by force or polled again, and a turn with nothing to do ends at once.
    /// Once per frame per driver.
    /// </para>
    /// <para>
    /// **Only a driving driver takes it.** A driver takes part while it is enabled and active -- the same condition
    /// under which Unity runs its <c>Update</c> and <c>LateUpdate</c> -- so the ending and the termination, which both
    /// stop the driver by disabling it, stop this turn too.
    /// </para>
    /// </summary>
    public sealed partial class ProvisionalCutDriver
    {
        private static readonly List<ProvisionalCutDriver> s_afterRendering = new List<ProvisionalCutDriver>(2);

        private int _afterRenderingFrame = int.MinValue;

        /// <summary>How many turns after rendering this driver has taken. For tests.</summary>
        public int AfterRenderingTurns { get; private set; }

        /// <summary>The frame of the last turn after rendering, or <see cref="int.MinValue"/> before the first. For tests.</summary>
        public int LastAfterRenderingFrame => _afterRenderingFrame;

        private void OnEnable()
        {
            AfterRenderingLoop.EnsureInstalled();
            if (!s_afterRendering.Contains(this))
            {
                s_afterRendering.Add(this);
            }

            // The display collects after the frame's decision to simulate, not in this driver's late update.
            CutPhysicsStep.Join(this);
        }

        private void OnDisable()
        {
            s_afterRendering.Remove(this);
            CutPhysicsStep.Leave(this);
        }

        /// <summary>
        /// The turn after rendering: <see cref="Advance"/> on this frame's id, at most once per frame, and only while
        /// this driver is enabled and active.
        /// </summary>
        public void DriveAfterRendering()
        {
            if (!enabled || !gameObject.activeInHierarchy)
            {
                return;
            }

            int frame = CurrentFrame;
            if (_afterRenderingFrame == frame)
            {
                return;
            }

            _afterRenderingFrame = frame;
            AfterRenderingTurns++;
            using (s_afterRenderingTurn.Auto())
            {
                Advance(frame);
            }
        }

        /// <summary>What the player loop runs after rendering: each driving driver's turn.</summary>
        private static void RunAfterRendering()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            // Backwards, and bounds-checked each time: a turn may disable a driver (the termination latch), which takes
            // it out of the list; a driver visited twice in one frame does nothing the second time.
            for (int i = s_afterRendering.Count - 1; i >= 0; i--)
            {
                if (i < s_afterRendering.Count)
                {
                    s_afterRendering[i].DriveAfterRendering();
                }
            }
        }

        /// <summary>
        /// The one place in the player loop, installed once: right after <see cref="PostLateUpdate.FinishFrameRendering"/>.
        /// A loop without that stage gets nothing installed and the driver works as it did without this turn.
        /// </summary>
        private static class AfterRenderingLoop
        {
            /// <summary>The installed system's type, which is how it is found again.</summary>
            private struct ProvisionalCutAfterRendering
            {
            }

            internal static void EnsureInstalled()
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                if (root.subSystemList == null)
                {
                    return;
                }

                for (int i = 0; i < root.subSystemList.Length; i++)
                {
                    PlayerLoopSystem stage = root.subSystemList[i];
                    if (stage.type != typeof(PostLateUpdate) || stage.subSystemList == null)
                    {
                        continue;
                    }

                    int after = -1;
                    for (int s = 0; s < stage.subSystemList.Length; s++)
                    {
                        Type type = stage.subSystemList[s].type;
                        if (type == typeof(ProvisionalCutAfterRendering))
                        {
                            return;
                        }

                        if (type == typeof(PostLateUpdate.FinishFrameRendering))
                        {
                            after = s;
                        }
                    }

                    if (after < 0)
                    {
                        return;
                    }

                    var systems = new List<PlayerLoopSystem>(stage.subSystemList);
                    systems.Insert(after + 1, new PlayerLoopSystem
                    {
                        type = typeof(ProvisionalCutAfterRendering),
                        updateDelegate = RunAfterRendering,
                    });
                    stage.subSystemList = systems.ToArray();
                    root.subSystemList[i] = stage;
                    PlayerLoop.SetPlayerLoop(root);
                    return;
                }
            }
        }
    }
}
