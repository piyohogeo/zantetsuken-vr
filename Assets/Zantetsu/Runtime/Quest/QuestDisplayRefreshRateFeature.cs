using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace Zantetsu.Quest
{
    /// <summary>
    /// Requests the display refresh rate through <c>XR_FB_display_refresh_rate</c> (plan 4: "実際に90 Hzへ設定できたことを確認").
    /// The OpenXR plugin 1.18.0 exposes no C# API for it, so the extension functions are resolved with xrGetInstanceProcAddr.
    /// The rates the runtime offers, the request result and the value read back are logged (<c>device.config</c>).
    /// </summary>
#if UNITY_EDITOR
    [OpenXRFeature(
        UiName = "Zantetsu: Display Refresh Rate (XR_FB_display_refresh_rate)",
        Desc = "Requests the display refresh rate through XR_FB_display_refresh_rate and logs what the runtime offers and reports.",
        Company = "probe",
        OpenxrExtensionStrings = ExtensionName,
        Version = "0.1.0",
        BuildTargetGroups = new[] { BuildTargetGroup.Android },
        FeatureId = featureId)]
#endif
    public sealed class QuestDisplayRefreshRateFeature : OpenXRFeature
    {
        public const string featureId = "com.zantetsuken.quest.displayrefreshrate";
        public const string ExtensionName = "XR_FB_display_refresh_rate";

        [Tooltip("Display refresh rate requested at session begin. 0 leaves the runtime default.")]
        public float requestedRate = 90f;

        public static float[] AvailableRates { get; private set; }
        public static float ReportedRate { get; private set; }
        public static int LastRequestResult { get; private set; } = int.MinValue;
        public static bool ExtensionEnabled { get; private set; }
        public static ulong Session { get; private set; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetInstanceProcAddrDelegate(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RequestDisplayRefreshRateDelegate(ulong session, float rate);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetDisplayRefreshRateDelegate(ulong session, out float rate);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private unsafe delegate int EnumerateDisplayRefreshRatesDelegate(ulong session, uint capacity, out uint count, float* rates);

        private RequestDisplayRefreshRateDelegate _request;
        private GetDisplayRefreshRateDelegate _get;
        private EnumerateDisplayRefreshRatesDelegate _enumerate;

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            ExtensionEnabled = OpenXRRuntime.IsExtensionEnabled(ExtensionName);
            if (!ExtensionEnabled)
            {
                Debug.LogWarning("QuestDisplayRefreshRateFeature: " + ExtensionName + " is not enabled by the runtime; refresh rate is left as is.");
                return true;
            }

            try
            {
                var gipa = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrDelegate>(xrGetInstanceProcAddr);
                _request = Resolve<RequestDisplayRefreshRateDelegate>(gipa, xrInstance, "xrRequestDisplayRefreshRateFB");
                _get = Resolve<GetDisplayRefreshRateDelegate>(gipa, xrInstance, "xrGetDisplayRefreshRateFB");
                _enumerate = Resolve<EnumerateDisplayRefreshRatesDelegate>(gipa, xrInstance, "xrEnumerateDisplayRefreshRatesFB");
            }
            catch (Exception e)
            {
                Debug.LogError("QuestDisplayRefreshRateFeature: resolving extension functions failed: " + e);
                _request = null;
                _get = null;
                _enumerate = null;
            }

            return true;
        }

        private static T Resolve<T>(GetInstanceProcAddrDelegate gipa, ulong instance, string name) where T : Delegate
        {
            int result = gipa(instance, name, out IntPtr fn);
            if (result != 0 || fn == IntPtr.Zero)
            {
                throw new InvalidOperationException(name + " not found (XrResult " + result + ")");
            }

            return Marshal.GetDelegateForFunctionPointer<T>(fn);
        }

        /// <summary>
        /// The session's state as the runtime tells it, kept with its frame for the frame records (DESIGN 4.6, the
        /// view-height diagnosis): the package logs the same change with a clock time and no frame.
        /// </summary>
        protected override void OnSessionStateChange(int oldState, int newState)
        {
            Zantetsu.Core.XrSessionStateReport.Set(oldState, newState, Time.frameCount);
            Debug.Log("XR SESSION: " + Zantetsu.Core.XrSessionStateReport.NameOf(oldState) + "->" + Zantetsu.Core.XrSessionStateReport.NameOf(newState) + " (frame " + Time.frameCount + ")");
        }

        protected override unsafe void OnSessionBegin(ulong xrSession)
        {
            Session = xrSession;
            var record = new Dictionary<string, object> { ["extensionEnabled"] = ExtensionEnabled, ["requested"] = requestedRate };
            if (_enumerate != null)
            {
                uint count;
                int r = _enumerate(xrSession, 0, out count, null);
                var rates = new float[count];
                if (r == 0 && count > 0)
                {
                    fixed (float* p = rates)
                    {
                        r = _enumerate(xrSession, count, out count, p);
                    }
                }

                AvailableRates = rates;
                record["enumerateResult"] = r;
                record["available"] = rates;
            }

            if (_request != null && requestedRate > 0f)
            {
                LastRequestResult = _request(xrSession, requestedRate);
                record["requestResult"] = LastRequestResult;
            }

            if (_get != null)
            {
                int r = _get(xrSession, out float rate);
                ReportedRate = rate;
                record["getResult"] = r;
                record["reported"] = rate;
            }
            Debug.Log("QuestDisplayRefreshRateFeature: requested " + requestedRate + " Hz, result " + LastRequestResult + ", runtime reports " + ReportedRate + " Hz");
        }

        /// <summary>Reads the runtime's current value again (for periodic logging). Returns false when unavailable.</summary>
        public static bool TryReadReported(out float rate)
        {
            rate = ReportedRate;
            var feature = OpenXRSettings.Instance != null ? OpenXRSettings.Instance.GetFeature<QuestDisplayRefreshRateFeature>() : null;
            if (feature == null || feature._get == null || Session == 0)
            {
                return false;
            }

            int r = feature._get(Session, out rate);
            if (r == 0)
            {
                ReportedRate = rate;
                return true;
            }

            return false;
        }
    }
}
