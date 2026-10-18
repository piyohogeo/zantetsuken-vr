using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Statistics;
using Zantetsu.Quest;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Play's start-up preparation (2026-10-18): the drawing warmed before the first cut, the katana's cut input held
    /// meanwhile beside the menu, the consent dialog and the system pause, and the statistics' play start waiting. The
    /// preparation holds the cut input alone (the walking, the head and the drawing go on, nothing is paused), is asked
    /// for before the menu exists, lets go of its own hold only, and is begun and ended once. The drawing preparation
    /// itself fails as a failure (never a completion) and is given up as an abandonment.
    /// </summary>
    public sealed class PlayPreparationHoldTests
    {
        private sealed class ScriptedInput : IPlayMenuInput
        {
            public PlayMenuInputFrame Frame;
            public PlayMenuInputFrame Read(Transform trackingSpace, Camera camera) => Frame;
        }

        private readonly List<Object> _made = new List<Object>();
        private float _timeScale, _volume;
        private ScriptedInput _input;
        private Transform _head;
        private SandboxRightHandKatana _katana;
        private SandboxLocomotionInput _plain;
        private MobPlanPlayerInput _city;
        private static readonly string[] SettingKeys = { PlaySettings.VolumeKey, PlaySettings.MoveSpeedKey, PlaySettings.TurnSpeedKey, PlaySettings.SnapTurnKey, PlaySettings.BladeAngleKey };
        private readonly Dictionary<string, float> _savedSettings = new Dictionary<string, float>();

        [SetUp]
        public void SetUp()
        {
            _savedSettings.Clear();
            foreach (string key in SettingKeys)
            {
                if (PlayerPrefs.HasKey(key)) _savedSettings[key] = PlayerPrefs.GetFloat(key);
                PlayerPrefs.DeleteKey(key);
            }

            _timeScale = Time.timeScale;
            _volume = AudioListener.volume;
            GamePause.ResetForTest();
            PlayPreparation.ResetForSession();
            Time.timeScale = 1f;
            _input = new ScriptedInput();
            var origin = new GameObject("XR Origin");
            _made.Add(origin);
            Transform offset = new GameObject("Camera Offset").transform;
            offset.SetParent(origin.transform, false);
            var camera = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            _head = camera.transform;
            _head.SetParent(offset, false);
            var katanaObject = new GameObject("Katana rig");
            _made.Add(katanaObject);
            _katana = katanaObject.AddComponent<SandboxRightHandKatana>();
            _plain = origin.AddComponent<SandboxLocomotionInput>();
            var cityObject = new GameObject("Crowd input");
            _made.Add(cityObject);
            _city = cityObject.AddComponent<MobPlanPlayerInput>();
        }

        [TearDown]
        public void TearDown()
        {
            QuestNpcSlashDemo.EndPlayPreparation("the test's end");
            PlayMenu.ReleasePreparation();
            foreach (PlayMenu menu in Object.FindObjectsByType<PlayMenu>(FindObjectsSortMode.None))
            {
                menu.Release();
                Object.DestroyImmediate(menu.gameObject);
            }

            foreach (Object made in _made)
            {
                if (made != null) Object.DestroyImmediate(made);
            }

            _made.Clear();
            GamePause.ResetForTest();
            PlayPreparation.ResetForSession();
            Time.timeScale = _timeScale;
            AudioListener.volume = _volume;
            foreach (string key in SettingKeys)
            {
                PlayerPrefs.DeleteKey(key);
                if (_savedSettings.TryGetValue(key, out float value)) PlayerPrefs.SetFloat(key, value);
            }

            PlayerPrefs.Save();
        }

        // Made as the product makes it (Install: the instance the preparation's hold and release reach, then its setup).
        private PlayMenu NewMenu()
        {
            PlayMenu menu = PlayMenu.Install(SandboxScenarioKind.Play);
            Assert.That(PlayMenu.Instance, Is.SameAs(menu));
            _made.Add(menu.gameObject);
            menu.Input = _input;
            menu.Camera = _head.GetComponent<Camera>();
            return menu;
        }

        private void Tick(PlayMenu menu, bool menuDown = false)
        {
            _input.Frame = new PlayMenuInputFrame { menuDown = menuDown };
            menu.Tick();
        }

        [Test]
        public void ThePreparation_AskedForBeforeTheMenu_HoldsTheKatanaAlone_PausesNothing_AndItsEndGivesTheKatanaBack()
        {
            PlayMenu.HoldForPreparation();
            PlayMenu menu = NewMenu();
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.Preparation), "applied when the menu is set up");
            Assert.That(_katana.LiveInputEnabled, Is.False, "the cut input is held");
            Assert.That(_plain.LiveInputEnabled && _city.liveInput, Is.True, "the walking is not");
            Assert.That(GamePause.IsPaused, Is.False, "nothing is paused: the head, the drawing and the preparation go on");
            Assert.That(_katana.ShowPoseWhileInputOff, Is.True, "in play the katana is still shown in the hand");
            Tick(menu);
            Assert.That(_katana.LiveInputEnabled, Is.False, "a frame of the menu does not give it back");

            PlayMenu.ReleasePreparation();
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.None));
            Assert.That(_katana.LiveInputEnabled, Is.True, "the katana is the game's again");
            Assert.That(_katana.ShowPoseWhileInputOff, Is.False);
            PlayMenu.ReleasePreparation();
            Assert.That(_katana.LiveInputEnabled, Is.True, "a second release changes nothing");
        }

        [Test]
        public void ThePreparation_WithTheMenuOpened_TheMenuTakesTheWalking_AndThePreparationsEnd_KeepsTheKatanaUntilTheMenuCloses()
        {
            PlayMenu.HoldForPreparation();
            PlayMenu menu = NewMenu();
            Tick(menu);   // a button down from the start is no press: one frame up first
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.True);
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.Preparation | PlayMenu.Holder.Menu));
            Assert.That(_plain.LiveInputEnabled || _city.liveInput, Is.False, "the menu takes the walking");

            PlayMenu.ReleasePreparation();
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.Menu), "the menu's hold stands");
            Assert.That(_katana.LiveInputEnabled, Is.False, "not given back while the menu holds it");

            Tick(menu);
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.False);
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.None));
            Assert.That(_katana.LiveInputEnabled, Is.True, "the katana with the last holder");
            Tick(menu);
            Assert.That(_plain.LiveInputEnabled && _city.liveInput, Is.True, "the walking after a read release, as ever");
        }

        [Test]
        public void ThePreparation_ThroughASystemPause_TheSystemsEndDoesNotGiveTheKatanaBack_AndTheMenuClosedBeforeItsEndNeither()
        {
            PlayMenu.HoldForPreparation();
            PlayMenu menu = NewMenu();
            GamePause.SetFocusLost(true);
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.Preparation | PlayMenu.Holder.System));
            GamePause.SetFocusLost(false);
            GamePause.TakeSystemEndsForTest();
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.Preparation), "the system's end lets go of the system's hold alone");
            Assert.That(_katana.LiveInputEnabled, Is.False, "the preparation still holds the cut input");

            Tick(menu);
            Tick(menu, menuDown: true);
            Tick(menu);
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.False, "opened and closed during the preparation");
            Assert.That(_katana.LiveInputEnabled, Is.False, "the menu's close gives back nothing the preparation holds");

            PlayMenu.ReleasePreparation();
            Assert.That(_katana.LiveInputEnabled, Is.True);
        }

        [Test]
        public void ThePreparation_EndedBeforeTheMenuIsMade_HoldsNothing()
        {
            PlayMenu.HoldForPreparation();
            PlayMenu.ReleasePreparation();
            PlayMenu menu = NewMenu();
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.None));
            Assert.That(_katana.LiveInputEnabled, Is.True);
        }

        [Test]
        public void PlaysPreparation_IsBegunOnce_AndEndedOnce_TheStatisticsCountAndTheHoldTogether()
        {
            QuestNpcSlashDemo.BeginPlayPreparation();
            QuestNpcSlashDemo.BeginPlayPreparation();
            Assert.That(PlayPreparation.Pending, Is.EqualTo(1), "begun once");
            Assert.That(PlayPreparation.Begun, Is.EqualTo(1));
            Assert.That(PlayMenu.PreparationHeld && QuestNpcSlashDemo.PlayPreparing, Is.True);

            QuestNpcSlashDemo.EndPlayPreparation("completed");
            QuestNpcSlashDemo.EndPlayPreparation("a destroy after it");
            Assert.That(PlayPreparation.Pending, Is.Zero);
            Assert.That(PlayPreparation.Ended, Is.EqualTo(1), "ended once, whichever way came first");
            Assert.That(PlayMenu.PreparationHeld || QuestNpcSlashDemo.PlayPreparing, Is.False);
        }

        [Test]
        public void PlaysPreparation_WaitsForAWorldFirst()
        {
            Assert.That(QuestNpcSlashDemo.PlayWaitingFor(null, null), Is.EqualTo("a cut world"));
        }

        [Test]
        public void TheDrawingPreparation_WithoutItsCamera_FailsAsAFailure_NeverAsACompletion()
        {
            var prewarm = new QuestDrawPrewarm(null, null, null, true, true, false);
            IEnumerator run = prewarm.Run();
            int steps = 0;
            while (run.MoveNext() && steps < 100) steps++;
            Assert.That(prewarm.Finished, Is.True);
            Assert.That(prewarm.Completed, Is.False);
            Assert.That(prewarm.Failure, Is.EqualTo("the real camera is required"));
            Assert.That(prewarm.Report, Has.Some.EqualTo("result=the real camera is required"));
            Assert.That(steps, Is.Zero, "no frame waited for what cannot come");
            Assert.That(() => prewarm.Run().MoveNext(), Throws.InvalidOperationException, "it runs once");

            prewarm.Abandon("after its end");
            Assert.That(prewarm.Failure, Is.EqualTo("the real camera is required"), "an abandonment after the end changes nothing");
        }

        /// <summary>
        /// The drawing preparation copies the katana's meshes (their vertex layout kept, every triangle made zero-area)
        /// and so needs them readable in a Player: the in-repo sword model came unreadable (2026-10-13, ca28016d) and the
        /// preparation failed on the Quest 3S on 2026-10-18 ("needs its readable, nonempty source mesh: Blade_75cm").
        /// </summary>
        [Test]
        public void TheKatanaModel_TheDrawingPreparationCopies_IsReadableInAPlayer()
        {
            GameObject model = Resources.Load<GameObject>(SandboxKatanaModel.ResourceName);
            Assert.That(model, Is.Not.Null, "the katana model the product loads");
            MeshFilter[] filters = model.GetComponentsInChildren<MeshFilter>(true);
            Assert.That(filters, Is.Not.Empty);
            foreach (MeshFilter filter in filters)
            {
                Assert.That(filter.sharedMesh, Is.Not.Null, filter.name);
                Assert.That(filter.sharedMesh.isReadable, Is.True, "readable in a Player: " + filter.sharedMesh.name);
                Assert.That(filter.sharedMesh.vertexCount, Is.GreaterThan(0), filter.sharedMesh.name);
            }
        }

        [Test]
        public void TheDrawingPreparation_GivenUp_IsAnAbandonment_NotACompletion()
        {
            var prewarm = new QuestDrawPrewarm(null, null, null, false, false, false);
            prewarm.Abandon("its owner was destroyed");
            Assert.That(prewarm.Finished, Is.True);
            Assert.That(prewarm.Completed, Is.False);
            Assert.That(prewarm.Failure, Is.EqualTo("abandoned: its owner was destroyed"));
        }
    }
}
