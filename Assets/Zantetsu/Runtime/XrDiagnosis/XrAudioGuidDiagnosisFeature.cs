using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using Zantetsu.Sandbox;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace Zantetsu.XrDiagnosis
{
    /// <summary>
    /// A diagnosis, not a product behaviour (TL, 2026-10-04). Off in the settings and inert unless the Player is started
    /// with <see cref="XrAudioGuidDiagnosis.ObserveArgument"/> or <see cref="XrAudioGuidDiagnosis.ReuseArgument"/>: then it puts itself between the application
    /// and the runtime's <c>xrGetAudioOutputDeviceGuidOculus</c> / <c>xrGetAudioInputDeviceGuidOculus</c> (through the
    /// package's hook on xrGetInstanceProcAddr) and feeds <see cref="XrAudioGuidDiagnosis"/>: how often each is called,
    /// how long each call takes, what it returns. With the reuse argument, and only when the runtime names itself a
    /// Simulator, a successful answer is given again to later calls of the same instance and direction.
    /// <para>
    /// Whether the plugin's own calls of the two functions go through the hooked table at all is what the first run
    /// with the observe argument shows: address lookups and calls are counted apart, and no call counted means the
    /// path is not this one.
    /// </para>
    /// Nothing here is the application's audio behaviour: on a device the audio route can change while it runs.
    /// The reuse argument is the adopted configuration of performance measurements in the Simulator (TL, 2026-10-04):
    /// off by default, Simulator-only, given explicitly at each measurement.
    /// </summary>
#if UNITY_EDITOR
    [OpenXRFeature(
        UiName = "Zantetsu: audio device id diagnosis (off unless asked by argument)",
        Desc = "Counts and times xrGetAudioOutputDeviceGuidOculus / xrGetAudioInputDeviceGuidOculus. Diagnosis only.",
        Company = "Zantetsu",
        Version = "0.0.1",
        BuildTargetGroups = new[] { BuildTargetGroup.Standalone },
        FeatureId = FeatureId)]
#endif
    public unsafe class XrAudioGuidDiagnosisFeature : OpenXRFeature
    {
        public const string FeatureId = "com.zantetsu.xr.audioguid.diagnosis";
        private const string OutputName = "xrGetAudioOutputDeviceGuidOculus";
        private const string InputName = "xrGetAudioInputDeviceGuidOculus";

        // For the Profiler: where in the frame, and on which thread, the two functions are called.
        private static readonly Unity.Profiling.ProfilerMarker s_outputMarker = new Unity.Profiling.ProfilerMarker("Zantetsu.XrAudioGuid.Output");
        private static readonly Unity.Profiling.ProfilerMarker s_inputMarker = new Unity.Profiling.ProfilerMarker("Zantetsu.XrAudioGuid.Input");

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetInstanceProcAddrDelegate(ulong instance, IntPtr name, IntPtr* function);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetAudioGuidDelegate(ulong instance, char* buffer);

        // Kept alive for as long as the process: the runtime holds their addresses.
        private static GetInstanceProcAddrDelegate s_originalGetProcAddr;
        private static readonly GetInstanceProcAddrDelegate s_hookGetProcAddr = HookedGetInstanceProcAddr;
        private static readonly GetAudioGuidDelegate s_hookOutput = HookedOutput;
        private static readonly GetAudioGuidDelegate s_hookInput = HookedInput;
        private static GetAudioGuidDelegate s_originalOutput, s_originalInput;
        private static IntPtr s_hookOutputPointer, s_hookInputPointer;

        // The feature is off in the settings. With one of the arguments it is switched on for this process, before the
        // OpenXR loader is initialised; without them nothing is touched.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void EnableFromArguments()
        {
            XrAudioGuidDiagnosis.DiagnosisMode mode = XrAudioGuidDiagnosis.ModeFromArguments(Environment.GetCommandLineArgs());
            if (mode == XrAudioGuidDiagnosis.DiagnosisMode.Off) return;
            OpenXRSettings settings = OpenXRSettings.Instance;
            XrAudioGuidDiagnosisFeature feature = settings != null ? settings.GetFeature<XrAudioGuidDiagnosisFeature>() : null;
            if (feature == null)
            {
                UnityEngine.Debug.Log("XR AUDIO GUID DIAGNOSIS: asked for (" + mode + ") but the feature is not in this Player's OpenXR settings: nothing is intercepted");
                return;
            }

            feature.enabled = true;
            UnityEngine.Debug.Log("XR AUDIO GUID DIAGNOSIS: ENABLED for this process by argument, mode " + mode + " (a diagnosis; not the application's audio behaviour)");
        }

        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            XrAudioGuidDiagnosis.DiagnosisMode mode = XrAudioGuidDiagnosis.ModeFromArguments(Environment.GetCommandLineArgs());
            if (mode == XrAudioGuidDiagnosis.DiagnosisMode.Off || func == IntPtr.Zero) return func;
            s_originalGetProcAddr = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrDelegate>(func);
            s_hookOutputPointer = Marshal.GetFunctionPointerForDelegate(s_hookOutput);
            s_hookInputPointer = Marshal.GetFunctionPointerForDelegate(s_hookInput);
            XrAudioGuidDiagnosis.Current.Begin(mode);
            UnityEngine.Debug.Log("XR AUDIO GUID DIAGNOSIS: xrGetInstanceProcAddr is hooked, mode " + mode);
            return Marshal.GetFunctionPointerForDelegate(s_hookGetProcAddr);
        }

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            string name = OpenXRRuntime.name;
            bool simulator = XrAudioGuidDiagnosis.IsSimulatorRuntime(name);
            XrAudioGuidDiagnosis.Current.OnInstanceCreate(xrInstance, name, simulator);
            if (XrAudioGuidDiagnosis.Current.Mode != XrAudioGuidDiagnosis.DiagnosisMode.Off)
            {
                UnityEngine.Debug.Log("XR AUDIO GUID DIAGNOSIS: instance created; runtime '" + name + "'; answers may be given again: "
                                      + (XrAudioGuidDiagnosis.Current.Mode == XrAudioGuidDiagnosis.DiagnosisMode.Reuse && simulator));
            }

            return true;
        }

        protected override void OnInstanceDestroy(ulong xrInstance)
        {
            XrAudioGuidDiagnosis.Current.OnInstanceDestroy(xrInstance);
            if (XrAudioGuidDiagnosis.Current.Mode == XrAudioGuidDiagnosis.DiagnosisMode.Reuse)
            {
                // The reuse mode says one thing at its end: how many calls were real and how many were answered from the kept value.
                UnityEngine.Debug.Log("XR AUDIO GUID DIAGNOSIS at the instance's end: " + XrAudioGuidDiagnosis.Current.DescribeReuseCounts());
            }
            else if (XrAudioGuidDiagnosis.Current.Mode != XrAudioGuidDiagnosis.DiagnosisMode.Off)
            {
                UnityEngine.Debug.Log("XR AUDIO GUID DIAGNOSIS at the instance's end: " + XrAudioGuidDiagnosis.Current.Describe());
            }
        }

        [AOT.MonoPInvokeCallback(typeof(GetInstanceProcAddrDelegate))]
        private static int HookedGetInstanceProcAddr(ulong instance, IntPtr name, IntPtr* function)
        {
            int result = s_originalGetProcAddr(instance, name, function);
            if (result != XrAudioGuidDiagnosis.Success || function == null || *function == IntPtr.Zero || name == IntPtr.Zero) return result;
            string asked = Marshal.PtrToStringAnsi(name);
            if (asked == OutputName)
            {
                s_originalOutput = Marshal.GetDelegateForFunctionPointer<GetAudioGuidDelegate>(*function);
                *function = s_hookOutputPointer;
                XrAudioGuidDiagnosis.Current.NoteLookup(XrAudioGuidDiagnosis.Output);
            }
            else if (asked == InputName)
            {
                s_originalInput = Marshal.GetDelegateForFunctionPointer<GetAudioGuidDelegate>(*function);
                *function = s_hookInputPointer;
                XrAudioGuidDiagnosis.Current.NoteLookup(XrAudioGuidDiagnosis.Input);
            }

            return result;
        }

        [AOT.MonoPInvokeCallback(typeof(GetAudioGuidDelegate))]
        private static int HookedOutput(ulong instance, char* buffer) => Call(XrAudioGuidDiagnosis.Output, s_originalOutput, instance, buffer);

        [AOT.MonoPInvokeCallback(typeof(GetAudioGuidDelegate))]
        private static int HookedInput(ulong instance, char* buffer) => Call(XrAudioGuidDiagnosis.Input, s_originalInput, instance, buffer);

        private static int Call(int side, GetAudioGuidDelegate original, ulong instance, char* buffer)
        {
            XrAudioGuidDiagnosis diagnosis = XrAudioGuidDiagnosis.Current;
            if (buffer == null || original == null) return original != null ? original(instance, buffer) : -1;
            var span = new Span<char>(buffer, XrAudioGuidDiagnosis.GuidLength);
            if (diagnosis.TryReuse(side, instance, span)) return XrAudioGuidDiagnosis.Success;
            Unity.Profiling.ProfilerMarker marker = side == XrAudioGuidDiagnosis.Output ? s_outputMarker : s_inputMarker;
            marker.Begin();
            long start = Stopwatch.GetTimestamp();
            int result = original(instance, buffer);
            long ticks = Stopwatch.GetTimestamp() - start;
            marker.End();
            diagnosis.Record(side, instance, result, ticks, span);
            return result;
        }
    }
}
