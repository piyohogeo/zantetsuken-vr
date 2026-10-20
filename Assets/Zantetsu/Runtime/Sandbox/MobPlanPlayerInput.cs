using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using Zantetsu.Core;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The probe's manual speed/turn control with actual XR tracking left untouched. The stick's forward follows the
    /// head's horizontal facing (D-225, 2026-10-13), as the plain input's does: before that it followed the rig's
    /// forward, so a player who had turned in the room, or whose tracking space had been re-centred, went sideways.
    /// A move never has a vertical part: looking down does not move the player down.
    /// <para>
    /// **The live stick's turn (2026-10-20).** Continuous -- the stick's sideways deflection turns the rig at
    /// <see cref="turnDegreesPerSecond"/> about its own origin, as before -- or snap: one deflection turns it at once by
    /// <see cref="SnapTurnDegrees"/> about the vertical line through the head (the head stays where it is), one turn a
    /// deflection (<see cref="SnapTurnTrigger"/>), taken whole (<see cref="PlayerLocomotion.TryRequestWhole"/>); where
    /// the walkable map refuses that, in place about the rig's origin (<see cref="TrySnapTurn"/>). The move is the same either way. A script's stick
    /// (<see cref="Submit"/>, <see cref="SubmitAlongRig"/>) always turns continuously.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class MobPlanPlayerInput : MonoBehaviour
    {
        public MobPlanCrowd crowd;
        public PlayerLocomotion player;
        /// <summary>The tracked head whose horizontal facing a move follows. Unassigned: the main camera's transform.</summary>
        public Transform head;
        public float speed = 1.49f;
        public float turnDegreesPerSecond = 120;
        public float reverseFactor = .5f;
        public bool liveInput = true;
        private Transform _mainCamera;

        [Tooltip("The live stick's turn: snap (one turn of the snap angle a deflection, about the head) or, off, continuous (as before). Play sets it from its settings (snap where none is saved).")]
        [SerializeField] private bool snapTurn;

        [Tooltip("One snap turn, degrees.")]
        [SerializeField] private float snapTurnDegrees = 30f;

        private readonly SnapTurnTrigger _snap = new SnapTurnTrigger();
        private bool _wasLive;

        /// <summary>Whether the live stick snap-turns (else it turns continuously). A change waits for the stick's centre.</summary>
        public bool SnapTurn
        {
            get => snapTurn;
            set
            {
                if (snapTurn != value) _snap.RequireCentre();
                snapTurn = value;
            }
        }

        /// <summary>One snap turn, degrees.</summary>
        public float SnapTurnDegrees
        {
            get => snapTurnDegrees;
            set => snapTurnDegrees = Mathf.Max(0f, value);
        }

        /// <summary>The live snap turns made, and the turns about the head the walkable map refused (made in place then, <see cref="SnapTurnsInPlace"/>).</summary>
        public int SnapTurnsMade { get; private set; }

        public int SnapTurnsRefused { get; private set; }

        /// <summary>A stick whose squared deflection is below this moves and turns nothing.</summary>
        public const float DeadZoneSquared = .0225f;

        /// <summary>Whether this stick is this input's neutral: what <c>Update</c> takes as no command. The play menu gives the live input back only then (DESIGN 4.6).</summary>
        public static bool IsNeutral(Vector2 stick) => stick.sqrMagnitude < DeadZoneSquared;

        /// <summary>
        /// The direction a move follows: <paramref name="headForward"/> laid flat and normalised, or <paramref name="fallbackForward"/>
        /// laid flat where the head looks straight up or down (and the rig's own forward where that is vertical too).
        /// Never has a vertical part.
        /// </summary>
        public static Vector3 HorizontalFacing(Vector3 headForward, Vector3 fallbackForward)
        {
            Vector3 facing = new Vector3(headForward.x, 0, headForward.z);
            if (facing.sqrMagnitude < 1e-8f) facing = new Vector3(fallbackForward.x, 0, fallbackForward.z);
            if (facing.sqrMagnitude < 1e-8f) facing = Vector3.forward;
            return facing.normalized;
        }

        /// <summary>The move of one submission along <paramref name="facing"/>: forward at the speed, back at the reverse factor of it, over <paramref name="seconds"/>.</summary>
        public static Vector3 Move(Vector3 facing, float forward, float speed, float reverseFactor, float seconds)
        {
            float velocity = Mathf.Clamp(forward, -1, 1) * speed * (forward < 0 ? reverseFactor : 1);
            return facing * (velocity * seconds);
        }

        /// <summary>The head a move follows: the one assigned, else the main camera's transform (looked up again when it is gone).</summary>
        public Transform Head
        {
            get
            {
                if (head != null) return head;
                if (_mainCamera == null) _mainCamera = Camera.main != null ? Camera.main.transform : null;
                return _mainCamera;
            }
        }

        /// <summary>A person's stick: the move follows the head's horizontal facing (D-225).</summary>
        public void Submit(float forward, float turn, float seconds)
        {
            SubmitAlong(forward, turn, seconds, true);
        }

        /// <summary>
        /// A scripted stick (the city walk's steps, D-236): the move follows the RIG's horizontal forward, which is
        /// what the script steers by -- its forward and turn are computed from the rig's yaw -- and not the head's,
        /// which a replayed or a looking-around head turns elsewhere. The three scripted walks of 2026-10-15 went
        /// where the replayed head looked, into obstacles, and halted at step 9.
        /// </summary>
        public void SubmitAlongRig(float forward, float turn, float seconds)
        {
            SubmitAlong(forward, turn, seconds, false);
        }

        /// <summary>The facing a submission follows: the head's laid flat (a person's stick), or the rig's own laid flat (a script's).</summary>
        public static Vector3 FacingOf(bool byHead, Vector3 headForward, Vector3 rigForward)
        {
            return byHead ? HorizontalFacing(headForward, rigForward) : HorizontalFacing(rigForward, rigForward);
        }

        private void SubmitAlong(float forward, float turn, float seconds, bool byHead)
        {
            if (player == null || crowd == null || !crowd.IsReady || GamePause.IsPaused) return; // (no move or turn while the game is paused, DESIGN 4.6)
            Transform rig = player.transform;
            Quaternion turned = Quaternion.Euler(0, Mathf.Clamp(turn, -1, 1) * turnDegreesPerSecond * seconds, 0);
            // A person's move follows the head's horizontal facing, turned by this frame's turn as the rig is (D-225):
            // the rig's own forward is only the fallback for a head looking straight up or down. A script's follows the rig's.
            Transform h = Head;
            Vector3 facing = FacingOf(byHead, h != null ? h.forward : rig.forward, rig.forward);
            Vector3 move = turned * Move(facing, forward, speed, reverseFactor, seconds);
            player.TryRequest(new Pose(rig.position + move, turned * rig.rotation));
        }
        /// <summary>
        /// One snap turn of <paramref name="steps"/> × the snap angle (positive to the right) about the vertical line
        /// through the head, taken whole or not at all. Where that is refused -- the walkable map would not have the rig
        /// round the head, at a wall (TL 2026-10-20 on the device: at the wall, facing it or with the back to it, the
        /// turn did not come) -- the turn is made in place instead, about the rig's own origin, as the continuous turn
        /// is: the head moves sideways by as much as it stands off that origin, that time only (the TL's choice, 2026-10-20).
        /// False where nothing turned (no player, the crowd not ready, the game paused, or the turn in place refused too).
        /// </summary>
        public bool TrySnapTurn(int steps)
        {
            if (steps == 0 || player == null || crowd == null || !crowd.IsReady || GamePause.IsPaused) return false;
            Transform rig = player.transform;
            Transform h = Head;
            Vector3 pivot = h != null ? h.position : rig.position;
            Pose root = new Pose(rig.position, rig.rotation);
            if (player.TryRequestWhole(PlayerLocomotion.Candidate(root, pivot, Vector3.zero, steps * snapTurnDegrees)) == LocomotionVerdict.Allowed)
            {
                SnapTurnsMade++;
                return true;
            }

            SnapTurnsRefused++;
            if (player.TryRequestWhole(PlayerLocomotion.Candidate(root, root.position, Vector3.zero, steps * snapTurnDegrees)) == LocomotionVerdict.Allowed)
            {
                SnapTurnsMade++;
                SnapTurnsInPlace++;
                return true;
            }

            return false;
        }

        /// <summary>The live snap turns made in place, about the rig's origin, because the turn about the head was refused (of <see cref="SnapTurnsMade"/>).</summary>
        public int SnapTurnsInPlace { get; private set; }

        /// <summary>
        /// One frame of the live stick (what <c>Update</c> does with the stick it read): the move, and the turn of the
        /// way set. The snap trigger waits for the centre after the input was given back (after the menu), while the
        /// game is paused, after a frame whose stick was not read (<paramref name="read"/> false: no command, and no
        /// centre either -- TL review 2026-10-20: a stick held over a moment the controller was not read would
        /// otherwise turn again when it was read once more), and after the stick's source changed: a centre counts only
        /// from the source the stick is read from since then (TL review 2026-10-20: the controller lost for a moment, a
        /// keyboard's or a gamepad's centre in between, and the controller back still deflected would turn again).
        /// </summary>
        internal void Drive(Vector2 command, float seconds, bool read = true, StickSource source = StickSource.Controller)
        {
            if (!liveInput)
            {
                _wasLive = false;
                return;
            }

            if (!_wasLive || GamePause.IsPaused)
            {
                _snap.RequireCentre();   // given back, or paused: a stick still deflected turns nothing
                _wasLive = true;
            }

            if (!read)
            {
                // Not read is not let go: the next turn waits for a centre that is read -- this frame's zero is no
                // stick at all, so it is not given to the trigger -- and nothing moves, as an unread stick moved nothing before.
                _snap.RequireCentre();
                _source = StickSource.None;
                Submit(0f, 0f, seconds);
                return;
            }

            // Another source's centre is not this one's: a change of source waits for a centre of the new one (its
            // first frame may be that centre; a deflection then turns nothing, the trigger being unarmed).
            if (source != _source)
            {
                _snap.RequireCentre();
                _source = source;
            }

            if (IsNeutral(command)) command = Vector2.zero;
            if (!snapTurn)
            {
                Submit(command.y, command.x, seconds);
                return;
            }

            // The turn first, whole, about the head; then the move along the head's facing as it now stands.
            int steps = _snap.Step(command.x);
            if (steps != 0) TrySnapTurn(steps);
            Submit(command.y, 0f, seconds);
        }

        /// <summary>Where a frame's stick was read from.</summary>
        public enum StickSource
        {
            None,
            Controller,
            Gamepad,
            Keyboard,
        }

        private StickSource _source = StickSource.None;

        // The application's focus back (the headset taken off and on, the system's menu): wait for the centre.
        private void OnApplicationFocus(bool focus)
        {
            if (focus) _snap.RequireCentre();
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused) _snap.RequireCentre();
        }

        private void Update()
        {
            if (!liveInput)
            {
                _wasLive = false;
                return;
            }

            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            Vector2 xr = Vector2.zero;
            bool xrRead = left.isValid && left.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out xr);
            Gamepad pad = Gamepad.current;
            Vector2 padStick = pad != null ? pad.leftStick.ReadValue() : Vector2.zero;
            var keyboard = Keyboard.current;
            Vector2 keys = Vector2.zero;
            if (keyboard != null)
            {
                keys = new Vector2((keyboard.rightArrowKey.isPressed || keyboard.dKey.isPressed ? 1 : 0) - (keyboard.leftArrowKey.isPressed || keyboard.aKey.isPressed ? 1 : 0),
                    (keyboard.upArrowKey.isPressed || keyboard.wKey.isPressed ? 1 : 0) - (keyboard.downArrowKey.isPressed || keyboard.sKey.isPressed ? 1 : 0));
            }

            bool read = ReadStick(left.isValid, xrRead, xr, pad != null, padStick, keyboard != null, keys, out Vector2 command, out StickSource source);
            Drive(command, Time.deltaTime, read, source);
        }

        /// <summary>
        /// This frame's stick, whether it was read at all, and from where (2026-10-20): keys pressed are read (the
        /// keyboard); else the left controller's stick where there is a controller -- read only where its value came
        /// and is finite (a controller whose stick was not given this frame is not read, and is no centre); else the
        /// gamepad's stick; else a keyboard with nothing pressed is the centre (the keyboard). With none of them, nothing
        /// was read. Keys pressed while a controller is there are a change of source each way: a sideways key just
        /// after the change does not snap-turn; once the keyboard's sideways axis has been read at the centre (a W or S
        /// alone, say), the next sideways key does (TL review 2026-10-20).
        /// </summary>
        internal static bool ReadStick(bool controller, bool controllerRead, Vector2 controllerStick, bool gamepad, Vector2 gamepadStick, bool keyboard, Vector2 keys,
            out Vector2 command, out StickSource source)
        {
            command = Vector2.zero;
            source = StickSource.None;
            if (keys != Vector2.zero)
            {
                command = keys;
                source = StickSource.Keyboard;
                return true;
            }

            if (controller)
            {
                if (!controllerRead || !IsFinite(controllerStick)) return false;
                command = controllerStick;
                source = StickSource.Controller;
                return true;
            }

            if (gamepad)
            {
                if (!IsFinite(gamepadStick)) return false;
                command = gamepadStick;
                source = StickSource.Gamepad;
                return true;
            }

            if (!keyboard) return false;
            source = StickSource.Keyboard;
            return true;
        }

        private static bool IsFinite(Vector2 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y);
    }
}
