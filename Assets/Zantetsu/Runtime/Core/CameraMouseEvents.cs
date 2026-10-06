using UnityEngine;

namespace Zantetsu.Core
{
    /// <summary>
    /// A camera that sends no <c>OnMouse*</c> messages (TL 2026-10-06). Every frame Unity asks each enabled camera the
    /// pointer is over which collider lies under it -- one physics raycast per camera -- so that it can send
    /// <c>OnMouseEnter</c>, <c>OnMouseDown</c> and the rest to that collider's scripts (PreUpdate.SendMouseEvents).
    /// Nothing in this project, its licensed content or the packages its scenes use answers those messages, so the
    /// cameras of a cut world send none: their <see cref="Camera.eventMask"/> is zero, which Unity reads before it makes
    /// the ray (checked in the engine's own code for the version in use, and the manual recommends it).
    /// <para>
    /// **What this is not.** It is that one mask and nothing else: not the camera's culling mask, not the Input System or
    /// its devices (the head, the hands, the pointer itself), not IMGUI, not a UI event system or its raycasters -- which
    /// carry a mask of their own -- and not any raycast a script asks for itself. The slash's own search and the physics
    /// scene are not touched.
    /// </para>
    /// <para>
    /// **Once, where the camera is taken up.** The mask is not saved with a scene, so it is set when a camera is given to
    /// the world (<c>CutWorldCameraDrawing</c>) or made for it at run time, and never again: no camera is searched for and
    /// nothing is set each frame. A camera that is to answer <c>OnMouse*</c> is simply not given here.
    /// </para>
    /// </summary>
    public static class CameraMouseEvents
    {
        /// <summary>How many times a camera's mask was written here in this domain. Observation: it does not grow with the frames.</summary>
        public static int Writes { get; private set; }

        /// <summary>Makes the camera send no <c>OnMouse*</c> messages. Its culling mask and everything else are left alone.</summary>
        public static void TurnOff(Camera camera)
        {
            if (camera == null || camera.eventMask == 0)
            {
                return;
            }

            camera.eventMask = 0;
            Writes++;
        }

        /// <summary>Whether the camera sends none.</summary>
        public static bool IsOff(Camera camera)
        {
            return camera != null && camera.eventMask == 0;
        }
    }
}
