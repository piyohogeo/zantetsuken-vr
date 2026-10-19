using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The menu's "Depenetration" setting (TL 2026-10-19): the physics' maximum depenetration velocity, 1 to 10 m/s with
    /// 1 as the default, play's own -- applied to the physics' default and to every body that exists, saved and put
    /// back at the next start of play; a scripted scenario runs with the project's physics as it is.
    /// </summary>
    public sealed class PlayDepenetrationSettingTests
    {
        private readonly List<Object> _made = new List<Object>();
        private float _physicsBefore;
        private bool _had;
        private float _saved;

        [SetUp]
        public void SetUp()
        {
            _physicsBefore = Physics.defaultMaxDepenetrationVelocity;
            _had = PlayerPrefs.HasKey(PlaySettings.DepenetrationKey);
            _saved = _had ? PlayerPrefs.GetFloat(PlaySettings.DepenetrationKey) : 0f;
            PlayerPrefs.DeleteKey(PlaySettings.DepenetrationKey);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object made in _made) if (made != null) Object.DestroyImmediate(made);
            _made.Clear();
            Physics.defaultMaxDepenetrationVelocity = _physicsBefore;
            PlayerPrefs.DeleteKey(PlaySettings.DepenetrationKey);
            if (_had) PlayerPrefs.SetFloat(PlaySettings.DepenetrationKey, _saved);
            PlayerPrefs.Save();
        }

        // A body with its actor, its own value set; or one made inactive and never active (the crowd's pool), with no actor yet.
        private Rigidbody NewBody(string name, bool active)
        {
            var go = new GameObject(name);
            _made.Add(go);
            if (!active) go.SetActive(false);
            Rigidbody body = go.AddComponent<Rigidbody>();
            if (active) body.maxDepenetrationVelocity = 3f;
            return body;
        }

        [Test]
        public void TheRange_Is1To10_WithTheDefault1()
        {
            Assert.That(PlaySettings.DepenetrationMinimum, Is.EqualTo(1f));
            Assert.That(PlaySettings.DepenetrationMaximum, Is.EqualTo(10f));
            Assert.That(PlaySettings.DepenetrationDefault, Is.EqualTo(1f));
        }

        [Test]
        public void InPlay_TheDefaultIsAppliedWhereNothingIsSaved_AndASetValue_ReachesThePhysicsAndEveryBody_AndIsKept()
        {
            Physics.defaultMaxDepenetrationVelocity = 7f;   // whatever the project left
            Rigidbody standing = NewBody("standing", true);
            Rigidbody pooled = NewBody("pooled", false);
            var settings = new PlaySettings(true);
            settings.Bind();
            string applied = settings.ApplySaved();
            Assert.That(settings.DepenetrationChangeable, Is.True);
            Assert.That(Physics.defaultMaxDepenetrationVelocity, Is.EqualTo(1f), "nothing saved: the default");
            Assert.That(applied, Does.Contain("max depenetration velocity 1.0 m/s (default)"));
            Assert.That(PlayerPrefs.HasKey(PlaySettings.DepenetrationKey), Is.False, "the default is applied, not saved");
            Assert.That(standing.maxDepenetrationVelocity, Is.EqualTo(1f), "every body that has its actor");

            settings.SetDepenetration(4.5f);
            Assert.That(Physics.defaultMaxDepenetrationVelocity, Is.EqualTo(4.5f));
            Assert.That(settings.Depenetration, Is.EqualTo(4.5f));
            Assert.That(standing.maxDepenetrationVelocity, Is.EqualTo(4.5f));
            // A body never active before (the crowd's pool) has no actor yet: it takes the default when activated.
            pooled.gameObject.SetActive(true);
            Assert.That(pooled.maxDepenetrationVelocity, Is.EqualTo(4.5f), "activated after the change: the default it takes is the new one");
            // (What a body that was active once comes back with after inactivity is not asserted: in the Editor, with no
            // physics step between, it came back with the old value in one run and with the default in another. The
            // pieces cut after a change are new bodies and take the default; that is what the setting is for.)
            settings.SetDepenetration(5f);
            Assert.That(pooled.maxDepenetrationVelocity, Is.EqualTo(5f).And.EqualTo(standing.maxDepenetrationVelocity), "every active body is set");
            Assert.That(PlayerPrefs.GetFloat(PlaySettings.DepenetrationKey), Is.EqualTo(5f), "the last value set is saved");
            settings.SetDepenetration(25f);
            Assert.That(Physics.defaultMaxDepenetrationVelocity, Is.EqualTo(10f), "clamped to the maximum");
            settings.SetDepenetration(0f);
            Assert.That(Physics.defaultMaxDepenetrationVelocity, Is.EqualTo(1f), "clamped to the minimum");

            // The next start of play puts the saved value back.
            settings.SetDepenetration(6f);
            Physics.defaultMaxDepenetrationVelocity = 1f;
            var again = new PlaySettings(true);
            again.Bind();
            Assert.That(again.ApplySaved(), Does.Contain("max depenetration velocity 6.0 m/s (saved)"));
            Assert.That(Physics.defaultMaxDepenetrationVelocity, Is.EqualTo(6f));
        }

        [Test]
        public void InAScriptedScenario_NothingSavedIsApplied_AndNothingIsChanged()
        {
            PlayerPrefs.SetFloat(PlaySettings.DepenetrationKey, 8f);
            Physics.defaultMaxDepenetrationVelocity = 1f;
            var settings = new PlaySettings(false);
            settings.Bind();
            settings.ApplySaved();
            Assert.That(settings.DepenetrationChangeable, Is.False);
            Assert.That(Physics.defaultMaxDepenetrationVelocity, Is.EqualTo(1f), "the project's physics as it is");
            settings.SetDepenetration(9f);
            Assert.That(Physics.defaultMaxDepenetrationVelocity, Is.EqualTo(1f), "not the player's to change");
            Assert.That(PlayerPrefs.GetFloat(PlaySettings.DepenetrationKey), Is.EqualTo(8f), "and nothing is written");
        }

        [Test]
        public void TheMenu_HasTheRow_BeforeTheKatanasRows_WithTheRangeAndStep()
        {
            var camera = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            _made.Add(camera);
            var host = new GameObject("menu under test");
            _made.Add(host);
            PlayMenu menu = host.AddComponent<PlayMenu>();
            menu.Camera = camera.GetComponent<Camera>();
            menu.Setup(SandboxScenarioKind.Play);
            Assert.That(menu.OpenWithoutPress(), Is.True);
            try
            {
                IReadOnlyList<PlayMenuView.Row> rows = menu.View.Rows;
                int at = -1;
                for (int i = 0; i < rows.Count; i++) if (rows[i].label == "Depenetration") at = i;
                Assert.That(at, Is.GreaterThanOrEqualTo(0), "the row is there");
                Assert.That(rows[at + 1].label, Is.EqualTo("Grip offset"), "before the katana's own rows (grip offset, blade angle)");
                PlayMenuView.Row row = rows[at];
                Assert.That(row.minimum, Is.EqualTo(1f));
                Assert.That(row.maximum, Is.EqualTo(10f));
                Assert.That(row.step, Is.EqualTo(0.5f));
                Assert.That(row.unit, Is.EqualTo(" m/s"));
                Assert.That(row.interactable, Is.True);
                Assert.That(row.read(), Is.EqualTo(1f), "the default");
                row.write(2.5f);
                Assert.That(Physics.defaultMaxDepenetrationVelocity, Is.EqualTo(2.5f));
            }
            finally
            {
                menu.Release();
            }
        }
    }
}
