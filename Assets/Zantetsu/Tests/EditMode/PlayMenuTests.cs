using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Sandbox;
using Zantetsu.Rendering;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The pause menu (DESIGN 4.6), driven a frame at a time with given input: the button's press and hold, what the
    /// opening stops and the closing gives back, the pointer through a moved rig, the settings, and the system pause
    /// beside it.
    /// </summary>
    public sealed class PlayMenuTests
    {
        private sealed class ScriptedInput : IPlayMenuInput
        {
            public PlayMenuInputFrame Frame;
            public Transform LastTrackingSpace;

            public PlayMenuInputFrame Read(Transform trackingSpace, Camera camera)
            {
                LastTrackingSpace = trackingSpace;
                return Frame;
            }
        }

        private readonly List<Object> _made = new List<Object>();
        private float _timeScale, _volume;
        private ScriptedInput _input;
        private Transform _offset, _head;
        private SandboxRightHandKatana _katana;
        private SandboxLocomotionInput _plain;
        private MobPlanPlayerInput _city;

        [SetUp]
        public void SetUp()
        {
            _timeScale = Time.timeScale;
            _volume = AudioListener.volume;
            GamePause.ResetForTest();
            Time.timeScale = 1f;
            _input = new ScriptedInput();

            // A rig carried away from the origin and turned, as artificial movement leaves it.
            var origin = new GameObject("XR Origin");
            _made.Add(origin);
            origin.transform.SetPositionAndRotation(new Vector3(40f, 0f, -12f), Quaternion.Euler(0f, 110f, 0f));
            _offset = new GameObject("Camera Offset").transform;
            _offset.SetParent(origin.transform, false);
            var camera = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            _head = camera.transform;
            _head.SetParent(_offset, false);
            _head.localPosition = new Vector3(0.1f, 1.6f, 0.05f);
            _head.localRotation = Quaternion.Euler(12f, -25f, 4f);

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
            foreach (PlayMenu menu in Object.FindObjectsByType<PlayMenu>(FindObjectsSortMode.None))
            {
                menu.Release();
                Object.DestroyImmediate(menu.gameObject);
            }

            foreach (Object made in _made)
            {
                if (made != null)
                {
                    Object.DestroyImmediate(made);
                }
            }

            _made.Clear();
            GamePause.ResetForTest();
            Time.timeScale = _timeScale;
            AudioListener.volume = _volume;
        }

        private PlayMenu NewMenu(SandboxScenarioKind scenario)
        {
            var host = new GameObject("Play menu under test");
            _made.Add(host);
            PlayMenu menu = host.AddComponent<PlayMenu>();
            menu.Input = _input;
            menu.Camera = _head.GetComponent<Camera>();
            menu.Setup(scenario);
            return menu;
        }

        private void Tick(PlayMenu menu, bool menuDown = false, bool select = false, bool held = false, Vector3? pointAt = null, Vector2 leftStick = default, Vector2 rightStick = default)
        {
            var frame = new PlayMenuInputFrame { menuDown = menuDown, selectDown = select, otherGameInputHeld = held || select, leftStick = leftStick, rightStick = rightStick };
            if (pointAt.HasValue)
            {
                // A controller tracked half a metre before the rig's origin at chest height, aimed at the point: its pose
                // is in the tracking space and reaches the world through that space, as the device input gives it.
                Vector3 trackedPosition = new Vector3(0.2f, 1.2f, 0.4f);
                Vector3 worldPosition = _offset.TransformPoint(trackedPosition);
                Quaternion trackedRotation = Quaternion.Inverse(_offset.rotation) * Quaternion.LookRotation(pointAt.Value - worldPosition);
                PlayMenuDeviceInput.ToWorld(_offset, trackedPosition, trackedRotation, out frame.pointerOrigin, out frame.pointerDirection);
                frame.hasPointer = true;
            }

            _input.Frame = frame;
            menu.Tick();
        }

        private static Vector3 Centre(RectTransform rect) => rect.TransformPoint(rect.rect.center);

        private static Vector3 Along(RectTransform rect, float t)
        {
            Rect r = rect.rect;
            return rect.TransformPoint(new Vector3(Mathf.Lerp(r.xMin, r.xMax, t), r.center.y, 0f));
        }

        // ---- the state alone ----------------------------------------------------------------------------------------

        [Test]
        public void InADevelopmentBuild_TheMenuShowsTheLastSwings_UnderTheSettings()
        {
            Assert.That(Debug.isDebugBuild, Is.True, "the layout: the Editor counts as a development build");
            PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
            Tick(menu);   // a button down from the start is no press: one frame up first
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.True);
            Assert.That(menu.View.DetailText, Is.Not.Null, "the development readout under the settings");
            Assert.That(menu.View.DetailText.text, Does.StartWith("Last swings").And.Contain("no swing yet"));
            Assert.That(menu.View.Rows.Count, Is.EqualTo(5), "the rows are the settings alone");
        }

        [Test]
        public void TheConsentDialog_PausesAndTakesTheControllers_ItsAnswerGoesToTheService_AndItIsNotAskedAgain()
        {
            var host = new GameObject("statistics under test");
            _made.Add(host);
            PlayStatisticsService service = host.AddComponent<PlayStatisticsService>();
            var store = new Zantetsu.Core.Statistics.MemoryPlayStatisticsStore();
            service.Setup(SandboxScenarioKind.Play, "WalkCity", store, null);
            PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
            Tick(menu);
            Assert.That(menu.AskConsent(service), Is.True, "never answered: asked");
            Assert.That(menu.IsDialogOpen, Is.True);
            Assert.That(menu.IsOpen, Is.False, "the menu itself is not open");
            Assert.That(GamePause.Manual && GamePause.IsPaused, Is.True, "paused as for the menu");
            Assert.That(_katana.LiveInputEnabled, Is.False, "the katana is taken");
            Assert.That(menu.Dialog.Visible, Is.True);

            // The menu's button does nothing while the dialog is up.
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.False);
            Assert.That(menu.IsDialogOpen, Is.True);

            // "Don't send", pressed by the pointer: declined, the dialog gone, the game going on, the katana back.
            Vector3 decline = Centre(menu.Dialog.SecondRect);
            Tick(menu, pointAt: decline);                 // the select was down when it opened: no press
            Tick(menu, select: true, pointAt: decline);
            Assert.That(menu.IsDialogOpen, Is.False);
            Assert.That(service.Consent.Decision, Is.EqualTo(Zantetsu.Core.Statistics.PlayStatisticsDecision.Declined));
            Assert.That(GamePause.IsPaused, Is.False);
            Assert.That(_katana.LiveInputEnabled, Is.True);
            Assert.That(menu.AskConsent(service), Is.False, "answered: not asked again");

            // The setting in the menu: the statistics row, last, reading the consent and changing it.
            Tick(menu);
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.True);
            IReadOnlyList<PlayMenuView.Row> rows = menu.View.Rows;
            Assert.That(rows.Count, Is.EqualTo(6), "the settings and the statistics row");
            PlayMenuView.Row row = rows[5];
            Assert.That(row.label, Is.EqualTo("Send play statistics"));
            Assert.That(row.isToggle, Is.True);
            Assert.That(row.read(), Is.Zero, "declined: OFF");
            row.write(1f);
            Assert.That(service.Consent.Agreed, Is.True, "ON agrees");
            Assert.That(row.read(), Is.EqualTo(1f));
            row.write(0f);
            Assert.That(service.Consent.Agreed, Is.False, "OFF withdraws");
            Assert.That(store.TryGet(Zantetsu.Core.Statistics.PlayStatisticsConsent.InstallIdKey, out _), Is.False, "the install id is forgotten");
        }

        [Test]
        public void TheConsentDialog_AskedBeforeTheHeadIsWhereThePlayerIs_IsStoodBeforeTheHeadAgain_AndByTheMenuButton()
        {
            var host = new GameObject("statistics under test");
            _made.Add(host);
            PlayStatisticsService service = host.AddComponent<PlayStatisticsService>();
            service.Setup(SandboxScenarioKind.Play, "WalkCity", new Zantetsu.Core.Statistics.MemoryPlayStatisticsStore(), null);
            PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
            Tick(menu);

            // Asked as the scene loads: the camera is not yet where the player's head will be (the rig not placed, the
            // headset's pose not yet on the camera).
            Vector3 headAtLoad = _head.transform.position;
            Assert.That(menu.AskConsent(service), Is.True);
            Assert.That(menu.DialogPlacements, Is.EqualTo(1));
            Vector3 first = menu.Dialog.FirstRect.parent.position;

            // A small movement of the head leaves the dialog where it stands (it is fixed in the world).
            _head.transform.position = headAtLoad + new Vector3(0.1f, 0.05f, 0f);
            Tick(menu);
            Assert.That(menu.DialogPlacements, Is.EqualTo(1));
            Assert.That(menu.Dialog.FirstRect.parent.position, Is.EqualTo(first));

            // The head found well away (the rig placed, the tracking begun): the dialog stands before it again.
            Vector3 tracked = headAtLoad + new Vector3(3f, 1.6f, -2f);
            _head.transform.SetPositionAndRotation(tracked, Quaternion.Euler(0f, 90f, 0f));
            Tick(menu);
            Assert.That(menu.DialogPlacements, Is.EqualTo(2));
            Vector3 second = menu.Dialog.FirstRect.parent.position;
            Assert.That(Vector3.Distance(second, tracked + Vector3.right * PlayMenuView.Distance), Is.LessThan(1e-3f), "1.3 m along the head's horizontal facing, at the head's height");
            Tick(menu);
            Assert.That(menu.DialogPlacements, Is.EqualTo(2), "placed once for that head position");

            // The player turns away and cannot see it: the menu's button brings it before the head (and opens no menu).
            _head.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            Tick(menu);
            Assert.That(menu.DialogPlacements, Is.EqualTo(2), "turning alone does not move it");
            Tick(menu, menuDown: true);
            Assert.That(menu.DialogPlacements, Is.EqualTo(3));
            Assert.That(Vector3.Distance(menu.Dialog.FirstRect.parent.position, tracked + Vector3.back * PlayMenuView.Distance), Is.LessThan(1e-3f));
            Assert.That(menu.IsOpen, Is.False);
            Tick(menu, menuDown: true);
            Assert.That(menu.DialogPlacements, Is.EqualTo(3), "held down: once");

            // And it is answered where it stands now.
            Vector3 agree = Centre(menu.Dialog.FirstRect);
            Tick(menu, pointAt: agree);
            Tick(menu, select: true, pointAt: agree);
            Assert.That(menu.IsDialogOpen, Is.False);
            Assert.That(service.Consent.Agreed, Is.True);
        }

        [Test]
        public void TheConsentDialog_Agree_GivesTheAgreement_AndAnInstallId()
        {
            var host = new GameObject("statistics under test");
            _made.Add(host);
            PlayStatisticsService service = host.AddComponent<PlayStatisticsService>();
            service.Setup(SandboxScenarioKind.Play, "WalkCity", new Zantetsu.Core.Statistics.MemoryPlayStatisticsStore(), null);
            PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
            Tick(menu);
            Assert.That(menu.AskConsent(service), Is.True);
            Vector3 agree = Centre(menu.Dialog.FirstRect);
            Tick(menu, pointAt: agree);
            Tick(menu, select: true, pointAt: agree);
            Assert.That(menu.IsDialogOpen, Is.False);
            Assert.That(service.Consent.Agreed, Is.True);
            Assert.That(System.Guid.TryParse(service.Consent.InstallId, out _), Is.True, "a random id at the agreement");
            Assert.That(GamePause.IsPaused, Is.False);
        }

        [Test]
        public void TheButton_TogglesOnItsPressOnly_HeldDownItDoesNothingMore()
        {
            var state = new PlayMenuState();
            Assert.That(state.Step(true, false, false), Is.False, "a button down from the start is no press");
            Assert.That(state.Open, Is.False);
            Assert.That(state.Step(false, false, false), Is.False);
            Assert.That(state.Step(true, false, false), Is.True, "the press opens");
            Assert.That(state.Open, Is.True);
            for (int i = 0; i < 500; i++)
            {
                Assert.That(state.Step(true, false, false), Is.False, "held, frame " + i);
                Assert.That(state.Open, Is.True);
            }

            Assert.That(state.Step(false, false, false), Is.False, "letting go does nothing");
            Assert.That(state.Open, Is.True);
            Assert.That(state.Step(true, false, false), Is.True, "the next press closes");
            Assert.That(state.Open, Is.False);
            for (int i = 0; i < 500; i++)
            {
                Assert.That(state.Step(true, false, false), Is.False);
                Assert.That(state.Open, Is.False);
            }

            Assert.That(state.Opens, Is.EqualTo(1));
        }

        [Test]
        public void AfterAClose_TheGamesInputWaits_UntilWhatItReadsWasLetGoOnce()
        {
            var state = new PlayMenuState();
            state.Step(false, false, false);
            Assert.That(state.GameInputAllowed, Is.True);
            state.Step(true, false, true);
            Assert.That(state.GameInputAllowed, Is.False, "open");

            // Closed by the menu's own resume, pressed with the trigger: the trigger is still down, and a stick is held.
            Assert.That(state.Step(false, true, true), Is.True);
            Assert.That(state.Open, Is.False);
            Assert.That(state.AwaitingRelease && !state.GameInputAllowed, Is.True);
            for (int i = 0; i < 100; i++)
            {
                state.Step(false, false, true);
                Assert.That(state.GameInputAllowed, Is.False, "held, frame " + i);
            }

            state.Step(false, false, false);
            Assert.That(state.GameInputAllowed, Is.True, "let go");
            state.Step(false, false, true);
            Assert.That(state.GameInputAllowed, Is.True, "and what is pressed after that is the game's");

            // Closed with nothing held: allowed at once. A resume while closed opens nothing.
            state.Step(true, false, false);
            state.Step(false, false, false);
            Assert.That(state.Step(true, false, false), Is.True);
            Assert.That(state.GameInputAllowed, Is.True);
            Assert.That(state.Step(false, true, false), Is.False);
            Assert.That(state.Open, Is.False);
        }

        // ---- the menu -----------------------------------------------------------------------------------------------

        [Test]
        public void Opening_PausesTheGame_TakesTheControllersFromIt_AndShowsTheMenuBeforeTheHead()
        {
            PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
            Assert.That(_katana.LiveInputEnabled && _plain.LiveInputEnabled && _city.liveInput, Is.True, "live before");
            Tick(menu);
            Assert.That(menu.IsOpen || GamePause.IsPaused, Is.False);
            Assert.That(_input.LastTrackingSpace, Is.SameAs(_offset), "the pointer is read through the camera's parent");

            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.True);
            Assert.That(GamePause.Manual && GamePause.IsPaused && !GamePause.System, Is.True);
            Assert.That(Time.timeScale, Is.Zero);
            Assert.That(new[] { _katana.LiveInputEnabled, _plain.LiveInputEnabled, _city.liveInput }, Is.EqualTo(new[] { false, false, false }));
            Assert.That(menu.View.Visible, Is.True);

            // About 1.3 m along the head's horizontal facing, upright, turned to the head; it does not follow the head.
            Vector3 facing = _head.forward;
            facing.y = 0f;
            facing.Normalize();
            Vector3 expected = _head.position + (facing * PlayMenuView.Distance);
            Assert.That(Vector3.Distance(menu.View.Root.position, expected), Is.LessThan(1e-4f));
            Assert.That(Vector3.Angle(menu.View.Root.forward, facing), Is.LessThan(0.01f));
            Assert.That(Vector3.Angle(menu.View.Root.up, Vector3.up), Is.LessThan(0.01f));
            _head.localRotation = Quaternion.Euler(-30f, 80f, 0f);
            _head.localPosition += new Vector3(0.3f, -0.2f, 0.1f);
            for (int i = 0; i < 200; i++)
            {
                Tick(menu, menuDown: true);
            }

            Assert.That(menu.IsOpen, Is.True, "held for 200 frames: still open");
            Assert.That(menu.State.Opens, Is.EqualTo(1));
            Assert.That(Vector3.Distance(menu.View.Root.position, expected), Is.LessThan(1e-4f), "the head moved; the menu stands");
            Assert.That(GamePause.ManualPauses, Is.EqualTo(1));
        }

        [Test]
        public void Resume_ByTheButtonUnderTheControllersRay_GivesTheKatanaBackAtOnce_AndTheWalkingOnceLetGo()
        {
            PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
            Tick(menu);
            Tick(menu, menuDown: true);
            Tick(menu);
            Vector3 resume = Centre(menu.View.ResumeRect);

            // The ray meets the button; a select already down when it arrives is no press.
            Assert.That(menu.View.TryMeet(_offset.TransformPoint(new Vector3(0.2f, 1.2f, 0.4f)), resume - _offset.TransformPoint(new Vector3(0.2f, 1.2f, 0.4f)), out Vector3 met), Is.True);
            Assert.That(Vector3.Distance(met, resume), Is.LessThan(1e-3f), "the ray through the moved rig lands on the button");
            Tick(menu, pointAt: resume);
            Assert.That(menu.IsOpen, Is.True, "pointing alone does nothing");
            Tick(menu, select: true, pointAt: resume);
            Assert.That(menu.IsOpen, Is.False, "the press on resume closes");
            Assert.That(GamePause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
            Assert.That(menu.View.Visible, Is.False);
            Assert.That(_katana.LiveInputEnabled, Is.True, "the katana's live input is back (its switch started the slash state over)");
            Assert.That(_plain.LiveInputEnabled || _city.liveInput, Is.False, "the trigger that pressed resume is still down: no walking yet");
            for (int i = 0; i < 50; i++)
            {
                Tick(menu, held: true);
                Assert.That(_plain.LiveInputEnabled || _city.liveInput, Is.False, "held, frame " + i);
            }

            Tick(menu);
            Assert.That(_plain.LiveInputEnabled && _city.liveInput, Is.True, "let go once: the walking input is the game's again");
        }

        [Test]
        public void TheWalkingInputComesBack_OnlyWhenItsOwnStickIsNeutral_NotAtAThresholdOfTheMenus()
        {
            // The crowd's input moves from a deflection of 0.15 on. A stick held at 0.18 through the menu is below
            // any "about centred" guess and still a command: the input must not come back while it is there.
            Assert.That(MobPlanPlayerInput.IsNeutral(new Vector2(0f, 0.18f)), Is.False);
            Assert.That(MobPlanPlayerInput.IsNeutral(new Vector2(0f, 0.149f)), Is.True);
            PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
            Tick(menu);
            Tick(menu, menuDown: true, leftStick: new Vector2(0f, 0.18f));
            Assert.That(menu.IsOpen, Is.True);
            Tick(menu, leftStick: new Vector2(0f, 0.18f));
            Tick(menu, menuDown: true, leftStick: new Vector2(0f, 0.18f));
            Assert.That(menu.IsOpen, Is.False, "closed by the button, the stick still at 0.18");
            for (int i = 0; i < 60; i++)
            {
                Tick(menu, leftStick: new Vector2(0f, 0.18f));
                Assert.That(_city.liveInput || _plain.LiveInputEnabled, Is.False, "0.18 is a command of the crowd's input: not given back, frame " + i);
            }

            // Sideways the same, and just above the dead zone the same.
            Tick(menu, leftStick: new Vector2(-0.16f, 0f));
            Assert.That(_city.liveInput, Is.False);
            Tick(menu, leftStick: new Vector2(0.1f, 0.1f));
            Assert.That(_city.liveInput && _plain.LiveInputEnabled, Is.True, "(0.1, 0.1) is 0.141: neutral for both inputs, and both are back");
        }

        [Test]
        public void ThePlainInputsTurn_AndItsOwnDeadZone_DecideItsNeutral()
        {
            Object.DestroyImmediate(_city.gameObject);
            var serialized = new UnityEditor.SerializedObject(_plain);
            serialized.FindProperty("moveDeadZone").floatValue = 0.3f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(_plain.IsNeutral(new Vector2(0f, 0.29f), 0f), Is.True, "its own dead zone, as the scene sets it");
            Assert.That(_plain.IsNeutral(new Vector2(0f, 0.31f), 0f), Is.False);
            Assert.That(_plain.IsNeutral(Vector2.zero, 0.34f), Is.True, "a snap turn is armed again below half its threshold (0.35)");
            Assert.That(_plain.IsNeutral(Vector2.zero, -0.36f), Is.False);

            PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
            Tick(menu);
            Tick(menu, menuDown: true);
            Tick(menu);
            Tick(menu, menuDown: true, rightStick: new Vector2(0.9f, 0f));
            Assert.That(menu.IsOpen, Is.False);
            Tick(menu, rightStick: new Vector2(0.9f, 0f));
            Assert.That(_plain.LiveInputEnabled, Is.False, "the right stick held over: a snap turn would fire the moment the input came back");
            Tick(menu, rightStick: new Vector2(0.36f, 0f));
            Assert.That(_plain.LiveInputEnabled, Is.False, "not yet armed again");
            Tick(menu, leftStick: new Vector2(0f, 0.29f), rightStick: new Vector2(0.34f, 0f));
            Assert.That(_plain.LiveInputEnabled, Is.True, "inside its own dead zone and below the arming threshold: back");
        }

        [Test]
        public void TheDeviceInput_ReadsTheSticksAsTheyAre_WithNoThresholdOfItsOwn()
        {
            // No controller in an edit mode run: the frame has no stick and no pointer of a controller, and nothing held.
            PlayMenuInputFrame frame = new PlayMenuDeviceInput().Read(_offset, null);
            Assert.That(frame.leftStick, Is.EqualTo(Vector2.zero));
            Assert.That(frame.rightStick, Is.EqualTo(Vector2.zero));
            Assert.That(frame.menuDown, Is.False);
            Assert.That(typeof(PlayMenuDeviceInput).GetField("StickHeld"), Is.Null, "the menu keeps no stick threshold of its own");
        }

        [Test]
        public void TheButtonAgain_Closes_AndASelectBesideTheMenuDoesNothing()
        {
            PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
            Tick(menu);
            Tick(menu, menuDown: true);
            Tick(menu);
            Vector3 beside = menu.View.Root.position + (menu.View.Root.right * 2f);
            Tick(menu, pointAt: beside);
            Tick(menu, select: true, pointAt: beside);
            Assert.That(menu.IsOpen, Is.True);
            Tick(menu);
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.False);
            Assert.That(GamePause.IsPaused, Is.False);
            Assert.That(_katana.LiveInputEnabled && _plain.LiveInputEnabled && _city.liveInput, Is.True, "nothing was held: everything is back");
        }

        [Test]
        public void InPlay_TheSlidersChangeTheVolume_TheSpeed_AndTheTurnOfTheInputTheSceneMovesBy()
        {
            var keys = new[] { PlaySettings.VolumeKey, PlaySettings.MoveSpeedKey, PlaySettings.TurnSpeedKey, PlaySettings.SnapTurnKey };
            var saved = new Dictionary<string, float>();
            foreach (string key in keys)
            {
                if (PlayerPrefs.HasKey(key))
                {
                    saved[key] = PlayerPrefs.GetFloat(key);
                    PlayerPrefs.DeleteKey(key);
                }
            }

            try
            {
                _city.speed = 1.49f;
                _city.turnDegreesPerSecond = 120f;
                PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
                Assert.That(menu.Settings.TurnIsContinuous && menu.Settings.MovementChangeable, Is.True, "the crowd's input is the one the scene moves by");
                Tick(menu);
                Tick(menu, menuDown: true);
                Tick(menu);
                IReadOnlyList<PlayMenuView.Row> rows = menu.View.Rows;
                Assert.That(rows.Count, Is.EqualTo(5));
                Assert.That(new[] { rows[0].label, rows[1].label, rows[2].label }, Is.EqualTo(new[] { "Volume", "Speed", "Turn" }));

                // Each slider is taken by a press on it and follows the ray until the select is let go.
                RectTransform volume = (RectTransform)rows[0].slider.transform;
                Tick(menu, pointAt: Along(volume, 0.5f));
                Tick(menu, select: true, pointAt: Along(volume, 0.5f));
                Assert.That(AudioListener.volume, Is.EqualTo(0.5f).Within(1e-4f));
                Tick(menu, select: true, pointAt: Along(volume, 0.2f));
                Assert.That(AudioListener.volume, Is.EqualTo(0.2f).Within(1e-4f), "dragged");
                Tick(menu, pointAt: Along(volume, 0.9f));
                Assert.That(AudioListener.volume, Is.EqualTo(0.2f).Within(1e-4f), "let go: pointing alone changes nothing");

                RectTransform speed = (RectTransform)rows[1].slider.transform;
                Tick(menu, select: true, pointAt: Along(speed, 0.999f));
                Assert.That(_city.speed, Is.EqualTo(PlaySettings.MoveSpeedMaximum).Within(1e-4f));
                Tick(menu);
                Tick(menu, select: true, pointAt: Along(speed, 0.37f));
                Assert.That(_city.speed, Is.EqualTo(1.4f).Within(1e-4f), "stepped to a tenth: 0.5 + 0.37 * 2.5 = 1.425 -> 1.4");
                Tick(menu);

                RectTransform turn = (RectTransform)rows[2].slider.transform;
                Tick(menu, select: true, pointAt: Along(turn, 0.001f));
                Assert.That(_city.turnDegreesPerSecond, Is.EqualTo(PlaySettings.TurnSpeedMinimum).Within(1e-4f));
                Assert.That(rows[2].valueText.text, Is.EqualTo("30 °/s"));
                Assert.That(_plain.SnapTurnDegrees, Is.EqualTo(30f), "the other input's turn is not this scene's and is not touched");

                // Kept, for play: the next menu of play puts them back.
                Assert.That(PlayerPrefs.GetFloat(PlaySettings.MoveSpeedKey), Is.EqualTo(1.4f).Within(1e-4f));
                Assert.That(PlayerPrefs.GetFloat(PlaySettings.TurnSpeedKey), Is.EqualTo(30f).Within(1e-4f));
                _city.speed = 1.49f;
                _city.turnDegreesPerSecond = 120f;
                AudioListener.volume = 1f;
                PlayMenu again = NewMenu(SandboxScenarioKind.Play);
                Assert.That(new[] { _city.speed, _city.turnDegreesPerSecond, AudioListener.volume }, Is.EqualTo(new[] { 1.4f, 30f, 0.2f }).Within(1e-4f));
                Assert.That(again.Settings.MoveSpeed, Is.EqualTo(1.4f).Within(1e-4f));
            }
            finally
            {
                foreach (string key in keys)
                {
                    PlayerPrefs.DeleteKey(key);
                    if (saved.TryGetValue(key, out float value))
                    {
                        PlayerPrefs.SetFloat(key, value);
                    }
                }

                PlayerPrefs.Save();
            }
        }

        [TestCase(SandboxScenarioKind.Play)]
        [TestCase(SandboxScenarioKind.CityWalk)]
        public void CutSurfaceDebug_StartsOff_TogglesOncePerPress_AndSurvivesMenuReopening(SandboxScenarioKind scenario)
        {
            var colours = VpCutSurfaceColour.Capture();
            try
            {
                VpCutSurfaceColour.ApplyDefaults();
                PlayMenu menu = NewMenu(scenario);
                Tick(menu);
                Tick(menu, menuDown: true);
                Tick(menu);
                var row = menu.View.Rows[3];
                Assert.That(row.label, Is.EqualTo("Cut surface debug"));
                Assert.That(row.isToggle, Is.True);
                Assert.That(VpCutSurfaceColour.DebugEnabled, Is.False);
                Assert.That(row.valueText.text, Is.EqualTo("OFF"));
                Vector3 target = Centre(row.background.rectTransform);
                Tick(menu, select: true, pointAt: target);
                Assert.That(VpCutSurfaceColour.DebugEnabled, Is.True);
                Assert.That(row.valueText.text, Is.EqualTo("ON"));
                Assert.That(VpCutSurfaceColour.CurrentProvisional, Is.EqualTo(Color.red));
                Tick(menu, select: true, pointAt: target);
                Assert.That(VpCutSurfaceColour.DebugEnabled, Is.True, "holding the trigger does not toggle again");
                Tick(menu);
                Tick(menu, menuDown: true);
                Tick(menu);
                Tick(menu, menuDown: true);
                Tick(menu);
                Assert.That(row.valueText.text, Is.EqualTo("ON"), "reopening preserves the session selection");
                Tick(menu, select: true, pointAt: Centre(row.background.rectTransform));
                Assert.That(VpCutSurfaceColour.DebugEnabled, Is.False);
                Assert.That(row.valueText.text, Is.EqualTo("OFF"));
                Assert.That(VpCutSurfaceColour.CurrentProvisional, Is.EqualTo(VpCutSurfaceColour.DefaultColour));
            }
            finally { VpCutSurfaceColour.Restore(colours); }
        }

        [Test]
        public void InPlay_TheBladeAngle_TurnsTheKatanaTowardItsEdge_IsKept_AndTheKatanaIsShownWhileItIsSet()
        {
            bool had = PlayerPrefs.HasKey(PlaySettings.BladeAngleKey);
            float before = had ? PlayerPrefs.GetFloat(PlaySettings.BladeAngleKey) : 0f;
            PlayerPrefs.DeleteKey(PlaySettings.BladeAngleKey);
            try
            {
                Quaternion sceneOffset = _katana.GripToKatanaOffset.rotation;
                PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
                Assert.That(menu.Settings.BladeAngleChangeable, Is.True);
                Assert.That(_katana.EdgewardTiltDegrees, Is.EqualTo(PlaySettings.BladeAngleDefault), "nothing saved: play's default");
                Assert.That(PlaySettings.BladeAngleDefault, Is.EqualTo(30f), "30 degrees toward the edge (D-222)");
                Assert.That(Quaternion.Angle(_katana.GripToKatanaOffset.rotation, sceneOffset * Quaternion.Euler(30f, 0f, 0f)), Is.LessThan(1e-3f));
                Assert.That(PlayerPrefs.HasKey(PlaySettings.BladeAngleKey), Is.False, "the default is applied, not saved");
                Tick(menu);
                Tick(menu, menuDown: true);
                Tick(menu);
                Assert.That(_katana.LiveInputEnabled, Is.False, "the menu has the controllers");
                Assert.That(_katana.ShowPoseWhileInputOff, Is.True, "and in play the katana goes on being shown in the hand");

                IReadOnlyList<PlayMenuView.Row> rows = menu.View.Rows;
                PlayMenuView.Row row = rows[rows.Count - 1];
                Assert.That(row.label, Is.EqualTo("Blade angle"));
                Assert.That(row.interactable, Is.True);
                Assert.That(row.valueText.text, Is.EqualTo("30.0 °"));

                // The slider's far end is 60 degrees toward the edge; its near end 30 toward the spine. Then a value by the row.
                RectTransform slider = (RectTransform)row.slider.transform;
                Tick(menu, select: true, pointAt: Along(slider, 0.999f));
                Tick(menu);
                Assert.That(_katana.EdgewardTiltDegrees, Is.EqualTo(PlaySettings.BladeAngleMaximum).Within(1e-3f));
                Tick(menu, select: true, pointAt: Along(slider, 0.001f));
                Tick(menu);
                Assert.That(_katana.EdgewardTiltDegrees, Is.EqualTo(PlaySettings.BladeAngleMinimum).Within(1e-3f));
                row.write(37.5f);
                Assert.That(_katana.EdgewardTiltDegrees, Is.EqualTo(37.5f).Within(1e-3f));

                // Toward the edge is a turn about the side normal that takes the blade's axis toward -Y: the offset is the
                // scene's with that turn after it, so what is shown and what cuts turn together.
                Quaternion offset = _katana.GripToKatanaOffset.rotation;
                Assert.That(Quaternion.Angle(offset, sceneOffset * Quaternion.Euler(37.5f, 0f, 0f)), Is.LessThan(1e-3f));
                Vector3 axisBefore = sceneOffset * Vector3.forward, axisAfter = offset * Vector3.forward, edge = sceneOffset * Vector3.down;
                Assert.That(Vector3.Angle(axisBefore, axisAfter), Is.EqualTo(37.5f).Within(1e-2f));
                Assert.That(Vector3.Dot(axisAfter, edge), Is.GreaterThan(0.5f), "the blade's axis went toward the edge's side");
                Assert.That(Quaternion.Angle(_katana.CaptureConditions.GripOffsetRotation, offset), Is.LessThan(1e-3f), "a capture's conditions say the offset in use");

                // Closed: the katana is the game's again, and the value is kept for the next start of play.
                Tick(menu);
                Tick(menu, menuDown: true);
                Assert.That(_katana.LiveInputEnabled, Is.True);
                Assert.That(_katana.ShowPoseWhileInputOff, Is.False);
                Assert.That(PlayerPrefs.GetFloat(PlaySettings.BladeAngleKey), Is.EqualTo(37.5f).Within(1e-4f));
                _katana.EdgewardTiltDegrees = 0f;
                PlayMenu again = NewMenu(SandboxScenarioKind.Play);
                Assert.That(_katana.EdgewardTiltDegrees, Is.EqualTo(37.5f).Within(1e-3f), "put back at the next start of play");
                Assert.That(again.Settings.BladeAngle, Is.EqualTo(37.5f).Within(1e-3f));
            }
            finally
            {
                PlayerPrefs.DeleteKey(PlaySettings.BladeAngleKey);
                if (had)
                {
                    PlayerPrefs.SetFloat(PlaySettings.BladeAngleKey, before);
                }

                PlayerPrefs.Save();
            }
        }

        [Test]
        public void InAScriptedScenario_TheBladeAngleIsTheScenes_NothingSavedIsApplied_AndTheKatanaIsNotShownByTheMenu()
        {
            bool had = PlayerPrefs.HasKey(PlaySettings.BladeAngleKey);
            float before = had ? PlayerPrefs.GetFloat(PlaySettings.BladeAngleKey) : 0f;
            try
            {
                PlayerPrefs.SetFloat(PlaySettings.BladeAngleKey, 45f);
                Quaternion sceneOffset = _katana.GripToKatanaOffset.rotation;
                PlayMenu menu = NewMenu(SandboxScenarioKind.CityWalk);
                Assert.That(_katana.EdgewardTiltDegrees, Is.Zero, "a scripted scenario replays its hands with the offset they were recorded for");
                Assert.That(menu.Settings.BladeAngleChangeable, Is.False);
                Tick(menu);
                Tick(menu, menuDown: true);
                Tick(menu);
                IReadOnlyList<PlayMenuView.Row> rows = menu.View.Rows;
                PlayMenuView.Row row = rows[rows.Count - 1];
                Assert.That(row.label, Is.EqualTo("Blade angle"));
                Assert.That(row.interactable, Is.False);
                Tick(menu, select: true, pointAt: Along((RectTransform)row.slider.transform, 0.999f));
                Tick(menu);
                menu.Settings.SetBladeAngle(30f);
                Assert.That(_katana.EdgewardTiltDegrees, Is.Zero);
                Assert.That(Quaternion.Angle(_katana.GripToKatanaOffset.rotation, sceneOffset), Is.LessThan(1e-4f));
                Assert.That(_katana.ShowPoseWhileInputOff, Is.False, "and the menu does not show the katana of a script");
                Assert.That(PlayerPrefs.GetFloat(PlaySettings.BladeAngleKey), Is.EqualTo(45f), "nothing is saved from a scripted scenario");
            }
            finally
            {
                PlayerPrefs.DeleteKey(PlaySettings.BladeAngleKey);
                if (had)
                {
                    PlayerPrefs.SetFloat(PlaySettings.BladeAngleKey, before);
                }

                PlayerPrefs.Save();
            }
        }

        [Test]
        public void InAScriptedScenario_NothingSavedIsApplied_AndTheSpeedAndTheTurnAreNotTheMenus()
        {
            bool had = PlayerPrefs.HasKey(PlaySettings.MoveSpeedKey);
            float before = had ? PlayerPrefs.GetFloat(PlaySettings.MoveSpeedKey) : 0f;
            try
            {
                PlayerPrefs.SetFloat(PlaySettings.MoveSpeedKey, 2.7f);
                _city.speed = 1.49f;
                PlayMenu menu = NewMenu(SandboxScenarioKind.CityWalk);
                Assert.That(_city.speed, Is.EqualTo(1.49f), "a scripted scenario runs with the scene's own values");
                Assert.That(menu.Settings.MovementChangeable, Is.False);

                // A script moves the player with live input off already: the menu leaves it off, and gives back nothing.
                _city.liveInput = false;
                _katana.LiveInputEnabled = false;
                Tick(menu);
                Tick(menu, menuDown: true);
                Assert.That(GamePause.Manual, Is.True, "the script and the game stop all the same");
                Tick(menu);
                RectTransform speed = (RectTransform)menu.View.Rows[1].slider.transform;
                Assert.That(menu.View.Rows[1].interactable || menu.View.Rows[2].interactable, Is.False);
                Tick(menu, select: true, pointAt: Along(speed, 0.999f));
                Assert.That(_city.speed, Is.EqualTo(1.49f), "the speed is the script's");
                Tick(menu);
                Tick(menu, menuDown: true);
                Tick(menu);
                Assert.That(GamePause.IsPaused, Is.False);
                Assert.That(_city.liveInput || _katana.LiveInputEnabled, Is.False, "what the script had switched off stays off");
                Assert.That(PlayerPrefs.GetFloat(PlaySettings.MoveSpeedKey), Is.EqualTo(2.7f), "and nothing was saved over the player's own");
            }
            finally
            {
                PlayerPrefs.DeleteKey(PlaySettings.MoveSpeedKey);
                if (had)
                {
                    PlayerPrefs.SetFloat(PlaySettings.MoveSpeedKey, before);
                }

                PlayerPrefs.Save();
            }
        }

        [Test]
        public void WithoutTheCrowdsInput_TheTurnIsThePlainInputsSnapAngle()
        {
            Object.DestroyImmediate(_city.gameObject);
            bool had = PlayerPrefs.HasKey(PlaySettings.SnapTurnKey);
            float before = had ? PlayerPrefs.GetFloat(PlaySettings.SnapTurnKey) : 0f;
            PlayerPrefs.DeleteKey(PlaySettings.SnapTurnKey);
            PlayerPrefs.DeleteKey(PlaySettings.MoveSpeedKey);
            try
            {
                PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
                Assert.That(menu.Settings.TurnIsContinuous, Is.False);
                Tick(menu);
                Tick(menu, menuDown: true);
                Tick(menu);
                PlayMenuView.Row turn = menu.View.Rows[2];
                Assert.That(turn.label, Is.EqualTo("Snap turn"));
                Tick(menu, select: true, pointAt: Along((RectTransform)turn.slider.transform, 0.42f));
                Assert.That(_plain.SnapTurnDegrees, Is.EqualTo(45f), "15 + 0.42 * 75 = 46.5 -> the 15 degree step 45");
            }
            finally
            {
                PlayerPrefs.DeleteKey(PlaySettings.SnapTurnKey);
                PlayerPrefs.DeleteKey(PlaySettings.MoveSpeedKey);
                if (had)
                {
                    PlayerPrefs.SetFloat(PlaySettings.SnapTurnKey, before);
                }

                PlayerPrefs.Save();
            }
        }

        [Test]
        public void OpenedWithoutAPress_TheMenuIsOpenAsByItsButton_Once_AndTheButtonClosesItAfterwards()
        {
            PlayMenu menu = NewMenu(SandboxScenarioKind.CityWalk);
            Tick(menu);
            Assert.That(menu.OpenWithoutPress(), Is.True);
            Assert.That(menu.IsOpen && GamePause.Manual && GamePause.IsPaused, Is.True, "open, and the game paused");
            Assert.That(menu.View.Visible, Is.True, "the menu is shown");
            Assert.That(_katana.LiveInputEnabled, Is.False, "the controllers are the menu's");
            Assert.That(menu.OpenWithoutPress(), Is.False, "open already: nothing more");
            Assert.That(menu.State.Opens, Is.EqualTo(1));
            for (int i = 0; i < 5; i++)
            {
                Tick(menu);
            }

            Assert.That(menu.IsOpen && GamePause.Manual, Is.True, "it stays open with no press");
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen || GamePause.Manual, Is.False, "and the button closes it as ever");
        }

        [Test]
        public void TheEndingPauseCheck_NotAskedFor_DoesNothing()
        {
            PlayMenu menu = NewMenu(SandboxScenarioKind.CityWalk);
            Tick(menu);
            var check = new SandboxPropSlashPlayerCheck.EndingPauseCheck(false);
            check.Begin(menu);
            check.Reached(SandboxPropSlashPlayerCheck.EndingPauseCheck.Stage.SummaryAndWorld, "a stage");
            string folder = System.IO.Path.Combine(Application.temporaryCachePath, "ending-pause-check-" + System.Guid.NewGuid().ToString("N"));
            check.Write(folder);
            Assert.That(check.Begun, Is.False);
            Assert.That(menu.IsOpen || GamePause.Manual || GamePause.IsPaused, Is.False, "no pause, no menu");
            Assert.That(check.Stages, Is.Zero);
            Assert.That(check.Lines, Is.Empty);
            Assert.That(System.IO.Directory.Exists(folder), Is.False, "and nothing written");
        }

        [Test]
        public void TheEndingPauseCheck_AskedFor_PausesOnceByTheMenu_AndSaysOfEachStageWhetherTheGameWasStillPaused()
        {
            string folder = System.IO.Path.Combine(Application.temporaryCachePath, "ending-pause-check-" + System.Guid.NewGuid().ToString("N"));
            string file = System.IO.Path.Combine(folder, SandboxPropSlashPlayerCheck.EndingPauseCheck.FileName);
            try
            {
                PlayMenu menu = NewMenu(SandboxScenarioKind.CityWalk);
                Tick(menu);
                var check = new SandboxPropSlashPlayerCheck.EndingPauseCheck(true);
                check.Reached(SandboxPropSlashPlayerCheck.EndingPauseCheck.Stage.SummaryAndWorld, "before the ending");
                Assert.That(check.Stages, Is.Zero, "nothing is a stage before the ending begins");
                Assert.That(GamePause.IsPaused, Is.False);

                check.Begin(menu);
                Assert.That(check.Begun && menu.IsOpen && GamePause.Manual && GamePause.IsPaused, Is.True, "the menu opened, the game paused");
                Assert.That(menu.State.Opens, Is.EqualTo(1));
                check.Reached(SandboxPropSlashPlayerCheck.EndingPauseCheck.Stage.SummaryAndWorld, "the summary");
                check.Reached(SandboxPropSlashPlayerCheck.EndingPauseCheck.Stage.SummaryAndWorld, "the summary again");
                Assert.That(check.Stages, Is.EqualTo(1), "a stage is counted once");

                // The completion and the end without the final save between them -- what a platform whose save is not
                // looked at would leave: three stages, each paused, and the result is NOT held and names the one missing.
                check.Reached(SandboxPropSlashPlayerCheck.EndingPauseCheck.Stage.Completion, "the completion");
                check.Reached(SandboxPropSlashPlayerCheck.EndingPauseCheck.Stage.End, "the end");
                Assert.That(check.Stages, Is.EqualTo(3));
                Assert.That(check.StagesPaused, Is.EqualTo(3));
                Assert.That(check.Held, Is.False, "three of four stages is not the whole ending");
                check.Write(folder);
                string[] three = System.IO.File.ReadAllLines(file);
                Assert.That(three.Length, Is.EqualTo(5), "the pause, three stages, the result");
                StringAssert.Contains("the menu opened, without a press", three[0]);
                StringAssert.Contains("the summary: reached with the game still paused", three[1]);
                StringAssert.EndsWith("3 of 4 stages reached after the pause, 3 with the game still paused: NOT HELD; not reached: FinalSaveAndLog", three[4]);

                // With the final save reached as well, the ending is whole.
                check.Reached(SandboxPropSlashPlayerCheck.EndingPauseCheck.Stage.FinalSaveAndLog, "the final save");
                Assert.That(check.Held, Is.True);
                check.Write(folder);
                string[] whole = System.IO.File.ReadAllLines(file);
                Assert.That(whole.Length, Is.EqualTo(6));
                StringAssert.EndsWith("4 of 4 stages reached after the pause, 4 with the game still paused: held", whole[5]);

                // A pause that somebody ended is seen at the stages after it, and the result says which.
                Tick(menu);
                Tick(menu, menuDown: true);
                Assert.That(GamePause.Manual, Is.False);
                var ended = new SandboxPropSlashPlayerCheck.EndingPauseCheck(true);
                ended.Begin(menu);
                Assert.That(menu.IsOpen && GamePause.Manual, Is.True);
                ended.Reached(SandboxPropSlashPlayerCheck.EndingPauseCheck.Stage.SummaryAndWorld, "the summary");
                Tick(menu);
                Tick(menu, menuDown: true);
                Assert.That(menu.IsOpen || GamePause.Manual, Is.False, "somebody closed the menu");
                ended.Begin(menu);
                Assert.That(menu.IsOpen || GamePause.Manual, Is.False, "the check pauses once: it does not pause again");
                ended.Reached(SandboxPropSlashPlayerCheck.EndingPauseCheck.Stage.FinalSaveAndLog, "the final save");
                ended.Reached(SandboxPropSlashPlayerCheck.EndingPauseCheck.Stage.Completion, "the completion");
                ended.Reached(SandboxPropSlashPlayerCheck.EndingPauseCheck.Stage.End, "the end");
                Assert.That(ended.Held, Is.False);
                ended.Write(folder);
                string[] resumed = System.IO.File.ReadAllLines(file);
                StringAssert.Contains("the final save: reached with the game NOT PAUSED", resumed[2]);
                StringAssert.EndsWith("4 of 4 stages reached after the pause, 1 with the game still paused: NOT HELD; reached with the game not paused: FinalSaveAndLog, Completion, End", resumed[5]);

                // Without a menu the manual pause is set; a run that never came to its ending says that it did not.
                var bare = new SandboxPropSlashPlayerCheck.EndingPauseCheck(true);
                bare.Write(folder);
                StringAssert.Contains("NOT RUN", System.IO.File.ReadAllLines(System.IO.Path.Combine(folder, SandboxPropSlashPlayerCheck.EndingPauseCheck.FileName))[0]);
                bare.Begin(null);
                Assert.That(GamePause.Manual && GamePause.IsPaused && !menu.IsOpen, Is.True);
                StringAssert.Contains("no menu: the manual pause set", bare.Lines[0]);
            }
            finally
            {
                if (System.IO.Directory.Exists(folder))
                {
                    System.IO.Directory.Delete(folder, true);
                }
            }
        }

        [Test]
        public void TheSystemPause_NeitherOpensNorClosesTheMenu_AndTheMenusEndLeavesNoPauseBehind()
        {
            PlayMenu menu = NewMenu(SandboxScenarioKind.Play);
            Tick(menu);
            GamePause.SetSystem(true);
            Tick(menu);
            Assert.That(menu.IsOpen, Is.False, "the headset coming off opens no menu");
            Assert.That(GamePause.System && !GamePause.Manual, Is.True);
            GamePause.ResetForTest();

            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen && GamePause.Manual, Is.True);
            GamePause.SetSystem(true);
            Tick(menu);
            Tick(menu);
            Assert.That(menu.IsOpen && GamePause.Manual && menu.View.Visible, Is.True, "nor does it close one");

            // The menu's own end -- its object destroyed, as at the Player's end -- ends its pause and gives the input back.
            menu.Release();
            Assert.That(GamePause.Manual, Is.False);
            Assert.That(GamePause.System && GamePause.IsPaused, Is.True, "the system pause is not the menu's to end");
            Assert.That(_katana.LiveInputEnabled && _plain.LiveInputEnabled && _city.liveInput, Is.True);
        }

        [Test]
        public void KeepOneLocomotionInput_LeavesTheCrowdsLive_AndSwitchesTheOthersOff()
        {
            // Edit mode enables no component by itself; a scene's enabled components are what this is about.
            Assert.That(SandboxScenario.KeepOneLocomotionInput(), Does.StartWith("live locomotion inputs kept "));
            Assert.That(_city.liveInput, Is.True, "the crowd's input is never the one switched off");
        }
    }
}
