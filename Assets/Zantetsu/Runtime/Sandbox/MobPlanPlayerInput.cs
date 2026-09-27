using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using Zantetsu.Core;

namespace Zantetsu.Sandbox
{
    /// <summary>The probe's manual speed/turn control with actual XR tracking left untouched.</summary>
    [DefaultExecutionOrder(-100)]
    public sealed class MobPlanPlayerInput : MonoBehaviour
    {
        public MobPlanCrowd crowd;
        public PlayerLocomotion player;
        public float speed = 1.49f;
        public float turnDegreesPerSecond = 120;
        public float reverseFactor = .5f;
        public bool liveInput = true;
        public void Submit(float forward, float turn, float seconds)
        {
            if (player == null || crowd == null || !crowd.IsReady) return;
            Quaternion rotation = Quaternion.Euler(0, Mathf.Clamp(turn, -1, 1) * turnDegreesPerSecond * seconds, 0) * player.transform.rotation;
            float velocity = Mathf.Clamp(forward, -1, 1) * speed * (forward < 0 ? reverseFactor : 1);
            Vector3 move = rotation * Vector3.forward * (velocity * seconds);
            player.TryRequest(new Pose(player.transform.position + move, rotation));
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
            if (command.sqrMagnitude < .0225f) command = Vector2.zero;
            Submit(command.y, command.x, Time.deltaTime);
        }
    }
}
