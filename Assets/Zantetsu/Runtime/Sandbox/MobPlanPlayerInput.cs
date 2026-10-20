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
    /// <para>
    /// **The live sticks' layout (2026-10-20).** Single, as before: the left stick's vertical axis moves forward and
    /// back, its horizontal axis turns. Dual (play's default): the left stick moves -- forward and back, and sideways --
    /// along the head's horizontal facing and the horizontal at right angles to it, the stick's length at most 1 (a
    /// diagonal is no faster), the reverse factor on the backward part alone; the right stick's horizontal axis turns
    /// (its vertical axis does nothing). Each stick is read on its own, with whether it was read and from where; the
    /// turn's centre counts only from the turn's own stick and source (<see cref="ReadStick"/>), never from the left
    /// stick's or another device's, and an unread right stick never moves the turn to the left. After the input is
    /// given back (the menu), the focus or the application back, the game's pause, or a change of layout, nothing
    /// moves or turns until the sticks the layout uses have been read at their centre. Keys (development): single --
    /// W/S or up/down forward and back, A/D or left/right turn; dual -- W/S forward and back, A/D sideways, left/right
    /// turn. A gamepad: its left stick, as the controller's; dual -- the left stick moves, the right stick's horizontal
    /// axis turns.
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

        [Tooltip("The live sticks' layout: dual (the left stick moves forward, back and sideways, the right stick's horizontal axis turns) or, off, single (the left stick moves forward and back and turns, as before). Play sets it from its settings (dual where none is saved).")]
        [SerializeField] private bool dualStick;

        private readonly SnapTurnTrigger _snap = new SnapTurnTrigger();   // waits for a centre from the start
        private bool _wasLive = true;   // live from the start: the start moves as before (only a snap waits for a centre)

        // Nothing moves or turns until the sticks the layout uses are read at their centre (the input given back, the
        // focus or the application back, the game's pause, the layout changed).
        private bool _heldUntilCentre;

        /// <summary>Whether the live sticks are the dual layout (else the single one). A change holds the move and the turn until the sticks are at their centre.</summary>
        public bool DualStick
        {
            get => dualStick;
            set
            {
                if (dualStick != value) HoldUntilCentre();
                dualStick = value;
            }
        }

        /// <summary>Whether the move and the turn wait for the sticks' centre now.</summary>
        public bool HeldUntilCentre => _heldUntilCentre;

        private void HoldUntilCentre()
        {
            _heldUntilCentre = true;
            _snap.RequireCentre();
        }

        /// <summary>
        /// Whether these sticks are this input's neutral in its layout: the left stick inside the dead zone, and in the
        /// dual layout the right stick's horizontal axis inside it too (<see cref="TurnNeutral"/>). The play menu gives
        /// the live input back only then (DESIGN 4.6).
        /// </summary>
        public bool SticksNeutral(Vector2 left, Vector2 right) =>
            IsNeutral(left) && (!dualStick || TurnNeutral(right));

        /// <summary>
        /// Whether the turn stick turns nothing whichever the way: its horizontal axis inside <see cref="DeadZone"/>,
        /// where the continuous turn starts (and well below the snap's release threshold).
        /// </summary>
        public static bool TurnNeutral(Vector2 stick) => Mathf.Abs(stick.x) < DeadZone;

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
            SubmitAlong(forward, turn, seconds, byHead, null);
        }

        // The move of the single layout (forward only), or of the dual one (stick: sideways and forward, MoveDual).
        private void SubmitAlong(float forward, float turn, float seconds, bool byHead, Vector2? stick)
        {
            if (player == null || crowd == null || !crowd.IsReady || GamePause.IsPaused) return; // (no move or turn while the game is paused, DESIGN 4.6)
            Transform rig = player.transform;
            Quaternion turned = Quaternion.Euler(0, Mathf.Clamp(turn, -1, 1) * turnDegreesPerSecond * seconds, 0);
            // A person's move follows the head's horizontal facing, turned by this frame's turn as the rig is (D-225):
            // the rig's own forward is only the fallback for a head looking straight up or down. A script's follows the rig's.
            Transform h = Head;
            Vector3 facing = FacingOf(byHead, h != null ? h.forward : rig.forward, rig.forward);
            Vector3 move = turned * (stick.HasValue ? MoveDual(facing, stick.Value, speed, reverseFactor, seconds) : Move(facing, forward, speed, reverseFactor, seconds));
            player.TryRequest(new Pose(rig.position + move, turned * rig.rotation));
        }

        /// <summary>
        /// The dual layout's move of one submission (2026-10-20): the stick (x sideways to the right, y forward) at most
        /// 1 long -- a diagonal no faster than straight, a light deflection as slow as it is light -- along
        /// <paramref name="facing"/> and the horizontal at right angles to it on its right; forward and sideways at the
        /// speed, the backward part at the reverse factor of it. Never vertical.
        /// </summary>
        public static Vector3 MoveDual(Vector3 facing, Vector2 stick, float speed, float reverseFactor, float seconds)
        {
            Vector2 v = Vector2.ClampMagnitude(stick, 1f);
            Vector3 right = new Vector3(facing.z, 0f, -facing.x);
            float forward = v.y * speed * (v.y < 0f ? reverseFactor : 1f);
            float sideways = v.x * speed;
            return ((facing * forward) + (right * sideways)) * seconds;
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
            // The single layout's one stick: the move and the turn are its.
            var stick = new StickReading(read, command, source);
            Drive(in stick, in stick, seconds);
        }

        /// <summary>
        /// One frame of the live sticks: <paramref name="move"/> the stick that moves (the left), <paramref name="turn"/>
        /// the one that turns (the left again in the single layout, the right in the dual one), each as read -- whether
        /// it was, its value and its source (see the other overload for the turn's centre).
        /// </summary>
        internal void Drive(in StickReading move, in StickReading turn, float seconds)
        {
            if (!liveInput)
            {
                _wasLive = false;
                return;
            }

            if (!_wasLive || GamePause.IsPaused)
            {
                HoldUntilCentre();   // given back, or paused: a stick still deflected moves and turns nothing
                _wasLive = true;
            }

            // The turn's own stick: not read is not let go, and another source's centre is not this one's -- the next
            // snap waits for a centre read from the turn's stick and source (a change's first frame may be that centre;
            // a deflection then turns nothing, the trigger being unarmed). The left stick's centre is never the right's.
            if (!turn.Read)
            {
                _snap.RequireCentre();
                _turnSource = StickSource.None;
            }
            else if (turn.Source != _turnSource)
            {
                _snap.RequireCentre();
                _turnSource = turn.Source;
            }

            if (_heldUntilCentre)
            {
                // Both sticks of the layout read and at their centre (in the single layout one stick, read once): inside
                // the dead zone, where nothing moves or turns whichever the way -- not the snap's release threshold
                // (0.35), which a continuous turn passes from 0.15 (TL review 2026-10-20: given back at 0.2, it turned).
                bool centred = move.Read && IsNeutral(move.Value) && turn.Read && TurnNeutral(turn.Value);
                if (!centred)
                {
                    Submit(0f, 0f, seconds);
                    return;
                }

                _heldUntilCentre = false;
            }

            Vector2 m = move.Read ? move.Value : Vector2.zero;   // an unread stick moves nothing
            if (IsNeutral(m)) m = Vector2.zero;
            if (!dualStick)
            {
                // As before: one stick, its vertical axis forward and back, its horizontal axis the turn.
                if (!turn.Read)
                {
                    Submit(0f, 0f, seconds);
                    return;
                }

                if (!snapTurn)
                {
                    Submit(m.y, m.x, seconds);
                    return;
                }

                // The turn first, whole, about the head; then the move along the head's facing as it now stands.
                int steps = _snap.Step(m.x);
                if (steps != 0) TrySnapTurn(steps);
                Submit(m.y, 0f, seconds);
                return;
            }

            // Dual: the right stick's horizontal axis turns -- never the left's, however the right was read.
            float axis = turn.Read ? turn.Value.x : 0f;
            if (!snapTurn)
            {
                SubmitAlong(0f, Mathf.Abs(axis) < DeadZone ? 0f : axis, seconds, true, m);
                return;
            }

            int snaps = turn.Read ? _snap.Step(axis) : 0;
            if (snaps != 0) TrySnapTurn(snaps);
            SubmitAlong(0f, 0f, seconds, true, m);
        }

        /// <summary>A turn axis inside this turns nothing continuously (the move's dead zone, √<see cref="DeadZoneSquared"/>).</summary>
        public const float DeadZone = 0.15f;

        /// <summary>Where a frame's stick was read from.</summary>
        public enum StickSource
        {
            None,
            Controller,
            Gamepad,
            Keyboard,
        }

        /// <summary>One stick's frame as read: whether it was read at all, its value, and where it was read from.</summary>
        public readonly struct StickReading
        {
            public readonly bool Read;
            public readonly Vector2 Value;
            public readonly StickSource Source;

            public StickReading(bool read, Vector2 value, StickSource source)
            {
                Read = read && IsFinite(value);
                Value = Read ? value : Vector2.zero;
                Source = Read ? source : StickSource.None;
            }

            public static StickReading NotRead => new StickReading(false, Vector2.zero, StickSource.None);
        }

        private StickSource _turnSource = StickSource.None;

        // The application's focus back (the headset taken off and on, the system's menu): wait for the centre.
        private void OnApplicationFocus(bool focus)
        {
            if (focus) HoldUntilCentre();
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused) HoldUntilCentre();
        }

        private void Update()
        {
            if (!liveInput)
            {
                _wasLive = false;
                return;
            }

            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            Vector2 leftStick = Vector2.zero;
            bool leftRead = left.isValid && left.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out leftStick);
            Gamepad pad = Gamepad.current;
            var keyboard = Keyboard.current;
            bool Key(UnityEngine.InputSystem.Controls.KeyControl key) => key.isPressed;
            if (!dualStick)
            {
                // Single, as before: the left stick (or the gamepad's left stick, or the keys) moves and turns.
                Vector2 keys = keyboard == null ? Vector2.zero : new Vector2(
                    (Key(keyboard.rightArrowKey) || Key(keyboard.dKey) ? 1 : 0) - (Key(keyboard.leftArrowKey) || Key(keyboard.aKey) ? 1 : 0),
                    (Key(keyboard.upArrowKey) || Key(keyboard.wKey) ? 1 : 0) - (Key(keyboard.downArrowKey) || Key(keyboard.sKey) ? 1 : 0));
                bool read = ReadStick(left.isValid, leftRead, leftStick, pad != null, pad != null ? pad.leftStick.ReadValue() : Vector2.zero, keyboard != null, keys,
                    out Vector2 command, out StickSource source);
                Drive(command, Time.deltaTime, read, source);
                return;
            }

            // Dual: the left stick (W/A/S/D) moves, the right stick's horizontal axis (left/right) turns -- each read on its own.
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            Vector2 rightStick = Vector2.zero;
            bool rightRead = right.isValid && right.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out rightStick);
            Vector2 moveKeys = keyboard == null ? Vector2.zero : new Vector2(
                (Key(keyboard.dKey) ? 1 : 0) - (Key(keyboard.aKey) ? 1 : 0), (Key(keyboard.wKey) ? 1 : 0) - (Key(keyboard.sKey) ? 1 : 0));
            Vector2 turnKeys = keyboard == null ? Vector2.zero : new Vector2((Key(keyboard.rightArrowKey) ? 1 : 0) - (Key(keyboard.leftArrowKey) ? 1 : 0), 0f);
            bool moveRead = ReadStick(left.isValid, leftRead, leftStick, pad != null, pad != null ? pad.leftStick.ReadValue() : Vector2.zero, keyboard != null, moveKeys,
                out Vector2 moveValue, out StickSource moveSource);
            bool turnRead = ReadStick(right.isValid, rightRead, rightStick, pad != null, pad != null ? pad.rightStick.ReadValue() : Vector2.zero, keyboard != null, turnKeys,
                out Vector2 turnValue, out StickSource turnSource);
            var move = new StickReading(moveRead, moveValue, moveSource);
            var turn = new StickReading(turnRead, turnValue, turnSource);
            Drive(in move, in turn, Time.deltaTime);
        }

        /// <summary>
        /// This frame's stick, whether it was read at all, and from where (2026-10-20) -- one stick: the left (the
        /// move, and in the single layout the turn too) or the right (the dual layout's turn), each with its own keys,
        /// controller and gamepad stick: keys pressed are read (the
        /// keyboard); else the controller's stick where there is a controller -- read only where its value came
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
