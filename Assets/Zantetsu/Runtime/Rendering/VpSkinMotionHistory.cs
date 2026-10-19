using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// A skin's object motion under AppSW keeps Unity's previous skinned vertices (skinned motion vectors), and Unity
    /// refreshes them only in a frame it skins the renderer -- one it draws. A skin that was not drawn the frame before
    /// (hidden, or out of view and not updated off screen) still holds the vertices of the last frame it was drawn, and
    /// its first frame back would write all the bones' motion since then (2026-10-19, measured in the Editor: 0.59-0.75
    /// of the NDC for 5 frames away). For that one frame its skinned motion vectors are off, so the frame writes the
    /// object's motion alone (the object's previous matrix Unity keeps current); from the next frame they are on again.
    /// Unity's own control only: no vertex is read or kept here.
    /// </summary>
    public static class VpSkinMotionHistory
    {
        /// <summary>
        /// Before the frame is drawn: skinned motion vectors on when the renderer was drawn the frame before
        /// (<see cref="Renderer.isVisible"/>, shadows included), off otherwise. True when it changed them.
        /// </summary>
        public static bool Settle(SkinnedMeshRenderer skin)
        {
            bool fresh = skin.isVisible;
            if (skin.skinnedMotionVectors == fresh) return false;
            skin.skinnedMotionVectors = fresh;
            return true;
        }
    }
}
