using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using XRCommonUsages = UnityEngine.XR.CommonUsages;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace Zantetsu.Sandbox
{
    /// <summary>What the menu reads of one frame's input (DESIGN 4.6): the menu button, the pointer and its select, and what the game's own input would read.</summary>
    public struct PlayMenuInputFrame
    {
        /// <summary>The left controller's menu button (or the keyboard's Escape, or a gamepad's start) is down.</summary>
        public bool menuDown;

        /// <summary>The pointer's select is down: the right trigger, or the left mouse button with the mouse pointer.</summary>
        public bool selectDown;

        /// <summary>Whether there is a pointer this frame; without one the menu is still closed by its button.</summary>
        public bool hasPointer;

        /// <summary>The pointer's ray, in world space.</summary>
        public Vector3 pointerOrigin;
        public Vector3 pointerDirection;

        /// <summary>
        /// The sticks as the walking inputs read them: the left controller's (a gamepad's left stick where there is no
        /// left controller) and the right controller's. Whether they are let go is the walking input's own question --
        /// its dead zone, its turn's threshold -- not a threshold of the menu's.
        /// </summary>
        public Vector2 leftStick;
        public Vector2 rightStick;

        /// <summary>The trigger is down or a movement key is pressed: game input that is held whatever the sticks are.</summary>
        public bool otherGameInputHeld;

        /// <summary>
        /// The game's input could not be read this frame: in XR, a controller not valid or not tracked, whose zeros
        /// are no one's letting go (D-241). The menu then waits with a release until a frame whose input is read.
        /// False (the default) where the input is read, in XR and without it alike.
        /// </summary>
        public bool inputUnavailable;

        /// <summary>Why the input is not read (D-241): for the log at a transition, so that a tracking loss is told from a device gone or a value not read.</summary>
        public PlayMenuInputUnavailable unavailableCause;
    }

    /// <summary>The causes of an input not read, as flags.</summary>
    [System.Flags]
    public enum PlayMenuInputUnavailable
    {
        None = 0,
        LeftNotValid = 1,
        RightNotValid = 2,
        RightNotTracked = 4,
        ValueNotRead = 8,      // the menu button, a stick or the trigger
        PoseNotRead = 16,      // the right controller's position or rotation
    }

    /// <summary>The menu's source of input: the devices in a Player, a script of frames in a test.</summary>
    public interface IPlayMenuInput
    {
        /// <param name="trackingSpace">The rig's tracking space (the camera's parent): a controller's tracked pose is in it. Null: the poses are taken as world poses.</param>
        /// <param name="camera">The player's camera, for the mouse pointer where no controller points.</param>
        PlayMenuInputFrame Read(Transform trackingSpace, Camera camera);
    }

    /// <summary>
    /// The devices (DESIGN 4.6): the controllers through the XR input devices the katana and the locomotion read, the
    /// keyboard, mouse and gamepad through the Input System. The right controller points and its trigger selects; with
    /// no tracked right controller the mouse points through the camera and its left button selects.
    /// <para>
    /// **A controller's pose is the rig's, not the world's.** It is tracked in the tracking space, which artificial
    /// movement carries around the world, so the ray is taken through that space's transform as it is this frame.
    /// The aim pose is used where the runtime reports one, the grip pose otherwise.
    /// </para>
    /// </summary>
    public sealed class PlayMenuDeviceInput : IPlayMenuInput
    {
        /// <summary>A trigger pulled farther than this selects.</summary>
        public const float TriggerDown = 0.5f;

        private static readonly InputFeatureUsage<Vector3> PointerPosition = new InputFeatureUsage<Vector3>("PointerPosition");
        private static readonly InputFeatureUsage<Quaternion> PointerRotation = new InputFeatureUsage<Quaternion>("PointerRotation");

        public PlayMenuInputFrame Read(Transform trackingSpace, Camera camera)
        {
            var frame = default(PlayMenuInputFrame);
            XRInputDevice left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            XRInputDevice right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            bool leftMenuRead = false, leftStickRead = false, rightStickRead = false, rightTriggerRead = false, rightPoseRead = false;
            if (left.isValid)
            {
                leftMenuRead = left.TryGetFeatureValue(XRCommonUsages.menuButton, out bool menu);
                if (leftMenuRead && menu)
                {
                    frame.menuDown = true;
                }

                leftStickRead = left.TryGetFeatureValue(XRCommonUsages.primary2DAxis, out Vector2 leftStick);
                if (leftStickRead)
                {
                    frame.leftStick = leftStick;
                }
            }

            bool rightSelect = false;
            if (right.isValid)
            {
                rightStickRead = right.TryGetFeatureValue(XRCommonUsages.primary2DAxis, out Vector2 rightStick);
                if (rightStickRead)
                {
                    frame.rightStick = rightStick;
                }

                bool buttonRead = right.TryGetFeatureValue(XRCommonUsages.triggerButton, out bool pressed);
                bool pullRead = right.TryGetFeatureValue(XRCommonUsages.trigger, out float pull);
                rightTriggerRead = buttonRead || pullRead;
                if ((buttonRead && pressed) || (pullRead && pull > TriggerDown))
                {
                    rightSelect = true;
                    frame.otherGameInputHeld = true;
                }

                rightPoseRead = TryReadPose(right, out Vector3 position, out Quaternion rotation);
                if (rightPoseRead)
                {
                    frame.hasPointer = true;
                    frame.selectDown = rightSelect;
                    ToWorld(trackingSpace, position, rotation, out frame.pointerOrigin, out frame.pointerDirection);
                }
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.escapeKey.isPressed)
                {
                    frame.menuDown = true;
                }

                if (keyboard.wKey.isPressed || keyboard.aKey.isPressed || keyboard.sKey.isPressed || keyboard.dKey.isPressed
                    || keyboard.upArrowKey.isPressed || keyboard.downArrowKey.isPressed
                    || keyboard.leftArrowKey.isPressed || keyboard.rightArrowKey.isPressed)
                {
                    frame.otherGameInputHeld = true;
                }
            }

            Gamepad gamepad = Gamepad.current;
            if (gamepad != null)
            {
                if (gamepad.startButton.isPressed)
                {
                    frame.menuDown = true;
                }

                // (The crowd's input reads a gamepad's left stick where there is no left controller.)
                if (!left.isValid)
                {
                    frame.leftStick = gamepad.leftStick.ReadValue();
                }
            }

            // In XR the controllers are the game's input: one not valid, the right one not tracked, or a value the
            // release is judged by not read, is an input not read this frame -- its zeros and falses are no release
            // (D-241). Without XR the keyboard, mouse and gamepad are read as ever.
            if (XRSettings.isDeviceActive)
            {
                frame.unavailableCause = Unavailable(left.isValid, right.isValid, right.isValid && IsTracked(right), leftMenuRead, leftStickRead, rightStickRead, rightTriggerRead, rightPoseRead);
                frame.inputUnavailable = frame.unavailableCause != PlayMenuInputUnavailable.None;
            }

            Mouse mouse = Mouse.current;
            if (!frame.hasPointer && mouse != null && camera != null)
            {
                Ray ray = camera.ScreenPointToRay(mouse.position.ReadValue());
                frame.hasPointer = true;
                frame.pointerOrigin = ray.origin;
                frame.pointerDirection = ray.direction;
                frame.selectDown = mouse.leftButton.isPressed;
            }

            return frame;
        }

        /// <summary>
        /// Why the game's input is not read this frame, in XR (None: it is read): a controller not valid, the right
        /// one not tracked, a value the release is judged by -- the menu button, both sticks, the trigger -- not read
        /// though its device is valid, or the right controller's pose not read (the pointer then has no select to
        /// judge). A default read as zero or false is then no evidence of anything let go.
        /// </summary>
        public static PlayMenuInputUnavailable Unavailable(bool leftValid, bool rightValid, bool rightTracked, bool leftMenuRead, bool leftStickRead, bool rightStickRead, bool rightTriggerRead, bool rightPoseRead)
        {
            PlayMenuInputUnavailable cause = PlayMenuInputUnavailable.None;
            if (!leftValid) cause |= PlayMenuInputUnavailable.LeftNotValid;
            if (!rightValid) cause |= PlayMenuInputUnavailable.RightNotValid;
            if (rightValid && !rightTracked) cause |= PlayMenuInputUnavailable.RightNotTracked;
            if ((leftValid && (!leftMenuRead || !leftStickRead)) || (rightValid && (!rightStickRead || !rightTriggerRead))) cause |= PlayMenuInputUnavailable.ValueNotRead;
            if (rightValid && rightTracked && !rightPoseRead) cause |= PlayMenuInputUnavailable.PoseNotRead;
            return cause;
        }

        private static bool IsTracked(XRInputDevice device)
        {
            return device.TryGetFeatureValue(XRCommonUsages.trackingState, out InputTrackingState state)
                && (state & InputTrackingState.Position) != 0 && (state & InputTrackingState.Rotation) != 0;
        }

        private static bool TryReadPose(XRInputDevice device, out Vector3 position, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (!device.TryGetFeatureValue(XRCommonUsages.trackingState, out InputTrackingState state)
                || (state & InputTrackingState.Position) == 0 || (state & InputTrackingState.Rotation) == 0)
            {
                position = default;
                return false;
            }

            if (device.TryGetFeatureValue(PointerPosition, out position) && device.TryGetFeatureValue(PointerRotation, out rotation))
            {
                return true;
            }

            return device.TryGetFeatureValue(XRCommonUsages.devicePosition, out position)
                && device.TryGetFeatureValue(XRCommonUsages.deviceRotation, out rotation);
        }

        /// <summary>A pose tracked in the rig's tracking space, as a ray of the world: through that space's transform as it is now.</summary>
        public static void ToWorld(Transform trackingSpace, Vector3 trackedPosition, Quaternion trackedRotation, out Vector3 origin, out Vector3 direction)
        {
            if (trackingSpace == null)
            {
                origin = trackedPosition;
                direction = trackedRotation * Vector3.forward;
                return;
            }

            origin = trackingSpace.TransformPoint(trackedPosition);
            direction = trackingSpace.rotation * (trackedRotation * Vector3.forward);
        }
    }
}
