using NUnit.Framework;
using Zantetsu.Core;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The Player's start keeps it running out of focus (TL 2026-10-02): a Player only, the Editor untouched, and the
    /// project setting left as it is (the start sets the running Player's value, not the project's).
    /// </summary>
    public class PlayerRunInBackgroundTests
    {
        [Test]
        public void APlayerStart_SetsIt_AndTheEditorIsLeftAlone()
        {
            Assert.That(PlayerRunInBackground.AppliesTo(isEditor: false), Is.True);
            Assert.That(PlayerRunInBackground.AppliesTo(isEditor: true), Is.False);
        }

        [Test]
        public void TheProjectSetting_IsNotChanged()
        {
            Assert.That(UnityEditor.PlayerSettings.runInBackground, Is.False, "the project's runInBackground stays off; the Player's start sets its own value");
        }
    }
}
