using System;

namespace Zantetsu.Core
{
    /// <summary>Build identity, independent of Android being used by both Quest and a touch-screen phone.</summary>
    public static class PlayerPlatform
    {
        public static bool IsAndroidFlat
        {
            get
            {
#if ZANTETSU_ANDROID_FLAT
                return true;
#elif UNITY_EDITOR
                return Array.IndexOf(Environment.GetCommandLineArgs(), "-zantetsuAndroidFlat") >= 0;
#else
                return false;
#endif
            }
        }
    }
}
