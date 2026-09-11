using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;

namespace Zantetsu.Observability.StandaloneTests
{
    /// <summary>
    /// Windows Standalone sentinel for the native graphics binding: in a real
    /// Player, on the real graphics device, the plugin loaded and holds the
    /// D3D11 device Unity is using.
    /// </summary>
    /// <remarks>
    /// This assembly is built for Windows Standalone x64 only, so the test runs
    /// where it means something instead of being ignored in the Editor. What it
    /// proves stops at the binding: no encoder session is opened, no capability
    /// is queried, no completion event is registered, and nothing is claimed
    /// about the adapter's vendor.
    /// </remarks>
    public class NvencNativeGraphicsObservationSentinelTests
    {
        [Test]
        public void Player_LoadedThePluginAndHoldsTheCurrentD3D11Device()
        {
            // The Player really is running on D3D11, which is what the plugin
            // reports about.
            Assert.That(
                SystemInfo.graphicsDeviceType,
                Is.EqualTo(GraphicsDeviceType.Direct3D11));

            Assert.That(NvencNativeGraphicsObservationBridge.IsObservable, Is.True);
            Assert.That(
                NvencNativeGraphicsObservationBridge.TryObserve(
                    out NvencGraphicsObservationV1 observation),
                Is.True);

            Assert.That(observation.IsInitialized, Is.True);
            Assert.That(observation.IsUnityPluginLoaded, Is.True);
            Assert.That(observation.HasCurrentD3D11Device, Is.True);

            // The ABI this build speaks is V1; a mismatch would have thrown
            // above rather than reaching here.
            Assert.That(NvencNativeGraphicsObservationBridge.AbiVersion, Is.EqualTo(1u));
        }
    }
}
