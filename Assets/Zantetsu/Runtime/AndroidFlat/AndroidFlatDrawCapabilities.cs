using Zantetsu.Rendering;

namespace Zantetsu.AndroidFlat
{
    /// <summary>Drawing features usable by this Player, independently of the saved preferences.</summary>
    public readonly struct AndroidFlatDrawCapabilities
    {
        public readonly bool MultiDrawIndirect;
        public readonly bool DrawIndirectCount;

        public AndroidFlatDrawCapabilities(bool multiDrawIndirect, bool drawIndirectCount)
        {
            MultiDrawIndirect = multiDrawIndirect;
            DrawIndirectCount = multiDrawIndirect && drawIndirectCount;
        }

        public static AndroidFlatDrawCapabilities Read()
        {
            if (!VpNativeDrawPlugin.Vulkan || !VpNativeDrawPlugin.TryLoad(out _)) return default;
            bool count = VpNativeDrawPlugin.IndirectCountReady && VpNativeDrawPlugin.ZeroEventId != 0
                && VpNativeDrawPlugin.BarrierEventId != 0 && VpCommandCompaction.TryLoad(out _);
            return new AndroidFlatDrawCapabilities(true, count);
        }
    }
}
