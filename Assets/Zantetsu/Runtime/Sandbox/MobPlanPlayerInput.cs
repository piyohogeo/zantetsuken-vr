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
        private void Update()
        {
            if (!liveInput) return;
            Vector2 command = Vector2.zero;
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (left.isValid) left.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out command);
            else if (Gamepad.current != null) command = Gamepad.current.leftStick.ReadValue();
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                var keys = new Vector2((keyboard.rightArrowKey.isPressed || keyboard.dKey.isPressed ? 1 : 0) - (keyboard.leftArrowKey.isPressed || keyboard.aKey.isPressed ? 1 : 0),
                    (keyboard.upArrowKey.isPressed || keyboard.wKey.isPressed ? 1 : 0) - (keyboard.downArrowKey.isPressed || keyboard.sKey.isPressed ? 1 : 0));
                if (keys != Vector2.zero) command = keys;
            }
            if (IsNeutral(command)) command = Vector2.zero;
            Submit(command.y, command.x, Time.deltaTime);
        }
    }
}
