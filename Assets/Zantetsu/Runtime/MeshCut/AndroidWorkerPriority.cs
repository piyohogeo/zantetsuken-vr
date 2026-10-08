#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    internal static class AndroidWorkerPriority
    {
        private static int _mainNice;
        private static bool _captured;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void CaptureMain()
        {
            int tid = gettid();
            _mainNice = getpriority(0, tid);
            _captured = true;
            string affinity = "unknown";
            foreach (string line in File.ReadAllLines("/proc/self/task/" + tid + "/status"))
                if (line.StartsWith("Cpus_allowed_list:")) affinity = line.Trim();
            Debug.Log("QUEST THREAD: Main tid=" + tid + " nice=" + _mainNice + " " + affinity);
        }

        public static void Apply(WorkDestination destination)
        {
            if (!_captured) throw new InvalidOperationException("Main nice must be captured before workers start");
            int difference = destination == WorkDestination.GeometryPool ? 3 : destination == WorkDestination.BackgroundPool ? 6 : 0;
            int desired = _mainNice + difference;
            if (desired > 19) throw new InvalidOperationException("Requested nice difference exceeds Android nice range: Main=" + _mainNice + " difference=" + difference);
            int tid = gettid();
            if (setpriority(0, tid, desired) != 0)
                throw new InvalidOperationException("setpriority tid=" + tid + " nice=" + desired + " errno=" + Marshal.GetLastWin32Error());
            int observed = getpriority(0, tid);
            if (observed != desired) throw new InvalidOperationException("nice readback " + observed + " differs from requested " + desired);
            Debug.Log("QUEST THREAD: " + destination + " tid=" + tid + " mainNice=" + _mainNice + " requestedNice=" + desired + " observedNice=" + observed);
        }

        [DllImport("libc")] private static extern int gettid();
        [DllImport("libc", SetLastError = true)] private static extern int getpriority(int which, int who);
        [DllImport("libc", SetLastError = true)] private static extern int setpriority(int which, int who, int priority);
    }
}
#endif
