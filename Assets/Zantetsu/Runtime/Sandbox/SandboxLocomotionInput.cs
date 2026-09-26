using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using Zantetsu.Core;
using XRCommonUsages = UnityEngine.XR.CommonUsages;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace Zantetsu.Sandbox
{
    /// <summary>One frame of artificial movement input: a planar move and a count of snap-turn steps.</summary>
    public readonly struct SandboxLocomotionCommand
    {
        /// <summary>The move, x to the right and y forward, each in [-1, 1], relative to the HMD's horizontal facing.</summary>
        public readonly Vector2 Move;

        /// <summary>Snap turns this frame: positive to the right.</summary>
        public readonly int TurnSteps;

        public SandboxLocomotionCommand(Vector2 move, int turnSteps)
        {
            Move = move;
            TurnSteps = turnSteps;
        }

        public bool IsEmpty => Move == Vector2.zero && TurnSteps == 0;
    }

    /// <summary>
    /// The Sandbox's minimal input adapter for artificial movement: the left thumbstick (or W/A/S/D) moves, the right
    /// thumbstick (or the left/right arrow keys) snap-turns. It only turns input into a candidate Root pose and hands it to
    /// <see cref="PlayerLocomotion.TryRequest(Pose)"/>; the judgement is not here. A fixed-input replay goes through
    /// the same <see cref="Submit"/>.
    /// <para>
    /// It runs before the katana (default order), so each frame's artificial movement is applied before the katana
    /// judges that frame's gesture and latches from the player's new placement.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public sealed class SandboxLocomotionInput : MonoBehaviour
    {
        [SerializeField] private PlayerLocomotion locomotion;

        [Tooltip("The tracked HMD whose horizontal facing a move follows and whose vertical line a turn pivots on. Only read.")]
        [SerializeField] private Transform hmd;

        [Tooltip("Move speed at full stick, m/s.")]
        [SerializeField] private float moveSpeed = 1.5f;

        [Tooltip("One snap turn, degrees.")]
        [SerializeField] private float snapTurnDegrees = 30f;

        [Tooltip("Stick deflection a snap turn needs; the stick must return below half of it before the next.")]
        [SerializeField] private float snapTurnThreshold = 0.7f;

        [Tooltip("Stick deflection below which a move is ignored.")]
        [SerializeField] private float moveDeadZone = 0.15f;

        [Tooltip("Read the controllers and keyboard. Off: only Submit moves the player.")]
        [SerializeField] private bool liveInputEnabled = true;

        private bool _turnArmed = true;

        public PlayerLocomotion Locomotion => locomotion;

        public bool LiveInputEnabled
        {
            get => liveInputEnabled;
            set => liveInputEnabled = value;
        }

        public void Configure(PlayerLocomotion target, Transform trackedHmd)
        {
            locomotion = target;
            hmd = trackedHmd;
        }

        /// <summary>
        /// Turns one command into a candidate Root pose and requests it. An empty command requests nothing and
        /// returns null.
        /// </summary>
        public LocomotionVerdict? Submit(in SandboxLocomotionCommand command, float deltaSeconds)
        {
            if (locomotion == null || command.IsEmpty)
            {
                return null;
            }

            Transform root = locomotion.transform;
            Vector3 head = hmd != null ? hmd.position : root.position;
            Vector3 facing = hmd != null ? hmd.forward : root.forward;
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-8f)
            {
                facing = root.forward;
                facing.y = 0f;
            }

            facing.Normalize();
            Vector3 right = new Vector3(facing.z, 0f, -facing.x);
            Vector2 move = Vector2.ClampMagnitude(command.Move, 1f);
            Vector3 worldMove = ((right * move.x) + (facing * move.y)) * (moveSpeed * Mathf.Max(0f, deltaSeconds));
            Pose candidate = PlayerLocomotion.Candidate(
                new Pose(root.position, root.rotation), head, worldMove, command.TurnSteps * snapTurnDegrees);
            return locomotion.TryRequest(candidate);
        }

        /// <summary>Reads this frame's command from the controllers and the keyboard.</summary>
        public SandboxLocomotionCommand ReadLiveCommand()
        {
            Vector2 move = Vector2.zero;
            float turnAxis = 0f;

            XRInputDevice left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (left.isValid && left.TryGetFeatureValue(XRCommonUsages.primary2DAxis, out Vector2 leftStick))
            {
                move = leftStick;
            }

            XRInputDevice right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (right.isValid && right.TryGetFeatureValue(XRCommonUsages.primary2DAxis, out Vector2 rightStick))
            {
                turnAxis = rightStick.x;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                Vector2 keys = new Vector2(
                    (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f),
                    (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f));
                if (keys != Vector2.zero)
                {
                    move = keys;
                }

                if (keyboard.rightArrowKey.isPressed)
                {
                    turnAxis = 1f;
                }
                else if (keyboard.leftArrowKey.isPressed)
                {
                    turnAxis = -1f;
                }
            }

            if (move.magnitude < moveDeadZone)
            {
                move = Vector2.zero;
            }

            int turnSteps = 0;
            if (_turnArmed && Mathf.Abs(turnAxis) >= snapTurnThreshold)
            {
                turnSteps = turnAxis > 0f ? 1 : -1;
                _turnArmed = false;
            }
            else if (!_turnArmed && Mathf.Abs(turnAxis) < snapTurnThreshold * 0.5f)
            {
                _turnArmed = true;
            }

            return new SandboxLocomotionCommand(move, turnSteps);
        }

        private void Update()
        {
            if (!liveInputEnabled)
            {
                return;
            }

            Submit(ReadLiveCommand(), Time.deltaTime);
        }
    }
}
