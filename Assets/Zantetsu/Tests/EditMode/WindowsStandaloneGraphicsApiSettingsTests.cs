using NUnit.Framework;
using UnityEditor;
using UnityEngine.Rendering;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Pins the project setting the Phase 0.11 NVENC bring-up profile depends
    /// on: the Windows x64 Player runs on Direct3D11 and on nothing else.
    /// </summary>
    /// <remarks>
    /// The bring-up admission boundary refuses any configuration whose current
    /// graphics API is not D3D11, so leaving the Windows Player on automatic
    /// graphics APIs would let a build select another one and make capture
    /// unsupported at start-up for a reason the project never chose. This is a
    /// read-only assertion through the public Editor API; the serialized
    /// settings themselves, and every other build target, are not this
    /// fixture's business.
    /// </remarks>
    public class WindowsStandaloneGraphicsApiSettingsTests
    {
        [Test]
        public void WindowsStandalone64_UsesDirect3D11Only()
        {
            Assert.That(
                PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64),
                Is.False,
                "the Windows x64 Player must not choose its graphics API automatically.");

            GraphicsDeviceType[] apis =
                PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);

            Assert.That(apis, Is.Not.Null);
            Assert.That(apis.Length, Is.EqualTo(1));
            Assert.That(apis[0], Is.EqualTo(GraphicsDeviceType.Direct3D11));
        }
    }
}
