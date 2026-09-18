using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Whether a render target's depth-stencil **format** carries a stencil byte.
    /// <para>
    /// **This is a format check and nothing more.** It reads the format of the target it is handed and says whether
    /// that format has eight stencil bits. It does not know which attachment a pass will really draw into, whether the
    /// pipeline substitutes an intermediate target, or whether anything else in the frame also writes the stencil. It
    /// therefore does not establish that the eight bits are this path's alone to use — DESIGN 5.6 requires that, and
    /// establishing it is a matter of the renderer configuration and of what else is drawn, settled where those are
    /// known and recorded there. Reading this as "the exclusive use is confirmed" would be wrong.
    /// </para>
    /// <para>
    /// It holds nothing, watches nothing and is not consulted per frame.
    /// </para>
    /// </summary>
    public static class VpStencilAttachment
    {
        /// <summary>
        /// Whether <paramref name="target"/>'s depth-stencil format carries eight stencil bits. Returns false with the
        /// reason when it does not.
        /// </summary>
        public static bool TryConfirmFormat(RenderTexture target, out string reason)
        {
            if (target == null)
            {
                reason = "there is no render target to read a format from";
                return false;
            }

            GraphicsFormat format = target.depthStencilFormat;
            if (format == GraphicsFormat.None)
            {
                reason = "the target has no depth-stencil attachment at all";
                return false;
            }

            int bits = StencilBits(format);
            if (bits != 8)
            {
                reason = "the target's depth-stencil format " + format + " carries " + bits
                    + " stencil bits, and this path uses all eight of them";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// The depth-stencil format to ask a render target for when the stencil byte is needed. Named here so that a
        /// caller does not have to know which of the formats carries a stencil.
        /// </summary>
        public static GraphicsFormat EightBitStencilFormat => GraphicsFormat.D24_UNorm_S8_UInt;

        private static int StencilBits(GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.D16_UNorm_S8_UInt:
                case GraphicsFormat.D24_UNorm_S8_UInt:
                case GraphicsFormat.D32_SFloat_S8_UInt:
                case GraphicsFormat.S8_UInt:
                    return 8;
                default:
                    return 0;
            }
        }
    }
}
