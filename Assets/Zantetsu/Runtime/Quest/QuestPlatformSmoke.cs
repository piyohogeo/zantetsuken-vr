#if UNITY_ANDROID && !UNITY_EDITOR && DEVELOPMENT_BUILD
using System;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.Quest
{
    internal static class QuestPlatformSmoke
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static unsafe void Verify()
        {
            if (Marshal.SizeOf<VpInstanceClip>() != VpIndexedIndirectDrawBatch.InstanceClipStride
                || Marshal.SizeOf<VpCullCommand>() != VpCullCommand.Stride)
                throw new InvalidOperationException("CPU/GPU layout sizes differ");
            var backing = VpPlatformPageBacking.Instance;
            int page = checked((int)backing.Granularity);
            if (!VpVirtualArray<byte>.TryCreate(backing, page * 3, page, out var array, out string failure))
                throw new InvalidOperationException(failure);
            using (array)
            {
                void* before = NativeArrayUnsafeUtility.GetUnsafePtr(array.View);
                var view = array.View;
                view[0] = 0x5a;
                if (!array.TryCommitTo(page * 2, out failure)) throw new InvalidOperationException(failure);
                view[page] = 0xa5;
                if (before != NativeArrayUnsafeUtility.GetUnsafePtr(array.View) || array.View[0] != 0x5a || array.View[page] != 0xa5)
                    throw new InvalidOperationException("Page growth changed the address or existing data");
            }
            Debug.Log("QUEST PLATFORM: PASS reserve/grow/read/release pageSize=" + page
                + " clipStride=" + Marshal.SizeOf<VpInstanceClip>() + " cullStride=" + Marshal.SizeOf<VpCullCommand>());
        }
    }
}
#endif
