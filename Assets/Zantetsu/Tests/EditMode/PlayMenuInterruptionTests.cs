using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The menu under the system's interruptions (DESIGN 4.6, D-241): the XR focus lost holds the game's input and
    /// hides the katana, the menu and the dialog keep their state and take no pointer through it, a drag or a press
    /// held across it is dropped, the walking comes back only after a frame whose input is read and let go, and the
    /// settings are saved once at the interruption's start. A system end is taken as the next frame would take it
    /// (<see cref="GamePause.TakeSystemEndsForTest"/>): the engine's frame does not advance in a test.
    /// </summary>
    public sealed class PlayMenuInterruptionTests
    {
        private sealed class ScriptedInput : IPlayMenuInput
        {
            public PlayMenuInputFrame Frame;
            public PlayMenuInputFrame Read(Transform trackingSpace, Camera camera) => Frame;
        }

        private readonly List<Object> _made = new List<Object>();
        private float _timeScale, _volume;
        private ScriptedInput _input;
        private Transform _offset, _head;
        private SandboxRightHandKatana _katana;
        private SandboxLocomotionInput _plain;
        private MobPlanPlayerInput _city;

        // The saved settings this Editor holds: put back as they were, saved, whatever the test did (a slider drag writes them).
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
            Time.timeScale = 1f;
            _input = new ScriptedInput();
            var origin = new GameObject("XR Origin");
            _made.Add(origin);
            _offset = new GameObject("Camera Offset").transform;
            _offset.SetParent(origin.transform, false);
            var camera = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            _head = camera.transform;
            _head.SetParent(_offset, false);
            _head.localPosition = new Vector3(0f, 1.6f, 0f);
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
                if (made != null) Object.DestroyImmediate(made);
            }

            _made.Clear();
            GamePause.ResetForTest();
            Time.timeScale = _timeScale;
            AudioListener.volume = _volume;
            foreach (string key in SettingKeys)
            {
                PlayerPrefs.DeleteKey(key);
                if (_savedSettings.TryGetValue(key, out float value)) PlayerPrefs.SetFloat(key, value);
            }

            PlayerPrefs.Save();
        }

        private PlayMenu NewMenu(SandboxScenarioKind scenario = SandboxScenarioKind.Play)
        {
            var host = new GameObject("Play menu under test");
            _made.Add(host);
            PlayMenu menu = host.AddComponent<PlayMenu>();
            menu.Input = _input;
            menu.Camera = _head.GetComponent<Camera>();
            menu.Setup(scenario);
            return menu;
        }

        private PlayStatisticsService NewStatistics()
        {
            var host = new GameObject("statistics under test");
            _made.Add(host);
            PlayStatisticsService service = host.AddComponent<PlayStatisticsService>();
            service.Setup(SandboxScenarioKind.Play, "WalkCity", new Zantetsu.Core.Statistics.MemoryPlayStatisticsStore(), null);
            return service;
        }

        private void Tick(PlayMenu menu, bool menuDown = false, bool select = false, bool held = false, Vector3? pointAt = null, Vector2 leftStick = default, bool unavailable = false)
        {
            var frame = new PlayMenuInputFrame { menuDown = menuDown, selectDown = select, otherGameInputHeld = held || select, leftStick = leftStick, inputUnavailable = unavailable };
            if (pointAt.HasValue)
            {
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

        private void ShowKatanaInHand()
        {
            if (_katana.Katana == null)
            {
                var visual = new GameObject("Katana visual").transform;   // the visual root the grip pose drives
                visual.SetParent(_katana.transform, false);
                _katana.Katana = visual;
            }

            var sample = new Zantetsu.Core.Input.BladePoseSample(1, 0.5, new Vector3(0.3f, 1.2f, 0.4f), Quaternion.identity, Zantetsu.Core.Input.BladeTrackingState.Position | Zantetsu.Core.Input.BladeTrackingState.Rotation);
            Assert.That(_katana.TryApplySample(sample), Is.True, "a tracked grip pose shows the katana");
            Assert.That(_katana.IsShown, Is.True);
        }

        private static Vector3 Along(RectTransform rect, float t)
        {
            Rect r = rect.rect;
            return rect.TransformPoint(new Vector3(Mathf.Lerp(r.xMin, r.xMax, t), r.center.y, 0f));
        }

        private static PlayMenuView.Row Speed(PlayMenu menu)
        {
            foreach (PlayMenuView.Row row in menu.View.Rows) if (row.label == "Speed") return row;
            Assert.Fail("no Speed row");
            return null;
        }

        [Test]
        public void TheFocusLost_InPlay_HoldsTheGamesInput_HidesTheKatana_AndGivesItBack_TheWalkingAfterAReadRelease()
        {
            PlayMenu menu = NewMenu();
            Tick(menu);
            Assert.That(_katana.LiveInputEnabled && _plain.LiveInputEnabled && _city.liveInput, Is.True);

            GamePause.SetFocusLost(true);
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.System));
            Assert.That(new[] { _katana.LiveInputEnabled, _plain.LiveInputEnabled, _city.liveInput }, Is.EqualTo(new[] { false, false, false }), "held by the system");
            Assert.That(_katana.ShowPoseWhileInputOff, Is.False, "the hand's picture goes with the focus");
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.False, "the menu's button is not read while the system has the input");
            Assert.That(GamePause.Manual, Is.False);

            // The focus back: the end is taken at the next frame; the katana comes back at once, the walking waits for a release.
            GamePause.SetFocusLost(false);
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.System), "until the frame after: still held");
            GamePause.TakeSystemEndsForTest();
            Assert.That(GamePause.System, Is.False);
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.None));
            Assert.That(_katana.LiveInputEnabled, Is.True, "the katana at once (its switch starts the slash state over)");
            Assert.That(_plain.LiveInputEnabled || _city.liveInput, Is.False, "the walking waits");
            Tick(menu, menuDown: true, held: true);
            Assert.That(menu.IsOpen, Is.False, "the button held through the interruption is no press");
            Assert.That(_city.liveInput, Is.False, "the trigger still held: not let go");
            Tick(menu, held: true);
            Assert.That(_city.liveInput, Is.False, "the trigger still held: not let go");
            Tick(menu, unavailable: true);
            Assert.That(_city.liveInput, Is.False, "an input not read is not let go");
            Tick(menu, leftStick: new Vector2(0.5f, 0f));
            Assert.That(_city.liveInput, Is.False, "a stick off its centre: not let go");
            Tick(menu);
            Assert.That(_plain.LiveInputEnabled && _city.liveInput, Is.True, "read and let go: the walking is the game's again");
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.True, "a new press after the release opens the menu as ever");
        }

        [Test]
        public void TheFocusLost_WithTheMenuOpen_KeepsTheMenuAndTheManualPause_DropsTheDrag_IgnoresThePointer_AndRestoresThePreview()
        {
            PlayMenu menu = NewMenu();
            Tick(menu);
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen && GamePause.Manual, Is.True);
            Assert.That(_katana.ShowPoseWhileInputOff, Is.True, "in play the katana is previewed while the menu is open");
            PlayMenuView.Row speed = Speed(menu);
            Tick(menu, pointAt: Along(speed.background.rectTransform, 0.3f));            // the select that opened nothing: let go first
            Tick(menu, select: true, pointAt: Along(speed.background.rectTransform, 0.3f));
            Assert.That(menu.View.HeldRow, Is.SameAs(speed), "a drag of the speed slider under way");
            float dragged = speed.read();

            ShowKatanaInHand();   // the preview has shown the katana in the hand
            GamePause.SetFocusLost(true);
            Assert.That(menu.IsOpen && GamePause.Manual && GamePause.System, Is.True, "the menu and the manual pause stay");
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.Menu | PlayMenu.Holder.System));
            Assert.That(menu.View.HeldRow, Is.Null, "the drag is dropped at the interruption");
            Assert.That(_katana.ShowPoseWhileInputOff, Is.False, "the preview is off while the focus is lost");
            Assert.That(_katana.IsShown, Is.False, "and the katana shown so far is hidden, not merely no longer updated");
            Assert.That(_katana.LiveInputEnabled, Is.False);
            // The select held, the pointer at the slider's other end, through the interruption: nothing moves.
            Tick(menu, select: true, pointAt: Along(speed.background.rectTransform, 0.9f));
            Tick(menu, select: true, pointAt: Along(speed.background.rectTransform, 0.9f));
            Assert.That(speed.read(), Is.EqualTo(dragged), "no input reaches the menu");
            Assert.That(menu.View.HeldRow, Is.Null);
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.True, "nor its button");

            GamePause.SetFocusLost(false);
            GamePause.TakeSystemEndsForTest();
            Assert.That(GamePause.System, Is.False);
            Assert.That(menu.IsOpen && GamePause.Manual && GamePause.IsPaused, Is.True, "the menu and its pause stay; nothing resumed by itself");
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.Menu));
            Assert.That(_katana.ShowPoseWhileInputOff, Is.True, "the preview is back");
            Assert.That(_katana.LiveInputEnabled, Is.False, "the menu still holds the katana");
            // The select held since before the interruption, over the slider's far end and then the resume button: no drag, no press.
            Tick(menu, select: true, pointAt: Along(speed.background.rectTransform, 0.9f));
            Assert.That(speed.read(), Is.EqualTo(dragged));
            Assert.That(menu.View.HeldRow, Is.Null);
            Vector3 resume = Centre(menu.View.ResumeRect);
            Tick(menu, select: true, pointAt: resume);
            Assert.That(menu.IsOpen, Is.True, "a select held across the interruption is no press");
            // Let go and pressed anew: resume.
            Tick(menu, pointAt: resume);
            Tick(menu, select: true, pointAt: resume);
            Assert.That(menu.IsOpen, Is.False, "a new press resumes");
            Assert.That(GamePause.IsPaused, Is.False);
            Assert.That(_katana.LiveInputEnabled, Is.True);
        }

        [Test]
        public void TheConsentDialog_KeepsItsStateThroughAnInterruption_AndASelectHeldAcrossIt_AnswersNothing()
        {
            PlayMenu menu = NewMenu();
            PlayStatisticsService statistics = NewStatistics();
            Tick(menu);
            Assert.That(menu.AskConsent(statistics), Is.True);
            Assert.That(menu.IsDialogOpen && GamePause.Manual, Is.True);
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.Dialog));
            Vector3 agree = Centre(menu.Dialog.FirstRect);
            Tick(menu, pointAt: agree);   // the select that was down when it opened: let go once

            GamePause.SetFocusLost(true);
            Assert.That(menu.IsDialogOpen && GamePause.Manual && GamePause.System, Is.True, "the dialog and its pause stay");
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.Dialog | PlayMenu.Holder.System));
            Tick(menu, select: true, pointAt: agree);
            Tick(menu, select: true, pointAt: agree);
            Assert.That(menu.IsDialogOpen, Is.True, "no answer while the system has the input");
            GamePause.SetFocusLost(false);
            GamePause.TakeSystemEndsForTest();
            Assert.That(menu.Holders, Is.EqualTo(PlayMenu.Holder.Dialog));
            Tick(menu, select: true, pointAt: agree);
            Assert.That(menu.IsDialogOpen, Is.True, "the select held across the interruption answers nothing");
            Tick(menu, pointAt: agree);
            Tick(menu, select: true, pointAt: agree);
            Assert.That(menu.IsDialogOpen, Is.False, "a new press answers");
            Assert.That(statistics.Consent.Agreed, Is.True);
            Assert.That(GamePause.IsPaused, Is.False);
        }

        [Test]
        public void TheConsentDialog_StandsBeforeTheHeadAgain_WhenASystemPauseEnds_AndNotAtItsBeginning()
        {
            PlayMenu menu = NewMenu();
            PlayStatisticsService statistics = NewStatistics();
            Tick(menu);
            Assert.That(menu.AskConsent(statistics), Is.True);
            Tick(menu);
            int placed = menu.DialogPlacements;

            GamePause.SetFocusLost(true);
            Tick(menu);
            Assert.That(menu.DialogPlacements, Is.EqualTo(placed), "nothing is moved while the system has the view");
            GamePause.SetFocusLost(false);
            GamePause.TakeSystemEndsForTest();
            Assert.That(menu.DialogPlacements, Is.EqualTo(placed + 1), "stood before the head once at the end (a restarted session remakes the space about the head)");
            Assert.That(menu.IsDialogOpen, Is.True, "open and unanswered still");
            Tick(menu);
            Assert.That(menu.DialogPlacements, Is.EqualTo(placed + 1), "and not again at the next frame");
        }

        [Test]
        public void TheSettings_AreSavedOnce_AtTheInterruptionsStart_AndTheMenuWorksWithoutAnyConsent()
        {
            PlayMenu menu = NewMenu();
            Tick(menu);
            Tick(menu, menuDown: true);
            int flushesBefore = PlaySettings.Flushes;
            GamePause.SetFocusLost(true);
            Assert.That(PlaySettings.Flushes, Is.EqualTo(flushesBefore + 1), "flushed once at the start");
            GamePause.SetSystem(true);
            GamePause.SetFocusLost(false);
            GamePause.SetFocusLost(true);
            Assert.That(PlaySettings.Flushes, Is.EqualTo(flushesBefore + 1), "another reason, a repeated loss: no further flush");
            Assert.That(GamePause.Reasons, Is.EqualTo(GamePauseReason.Manual | GamePauseReason.Application | GamePauseReason.Focus));
            GamePause.SetSystem(false);
            GamePause.SetFocusLost(false);
            GamePause.TakeSystemEndsForTest();
            Assert.That(GamePause.Reasons, Is.EqualTo(GamePauseReason.Manual), "the menu's pause outlives the system's");
            Assert.That(menu.IsOpen, Is.True);
            // (No statistics service was installed here at all: the pause is nothing of the consent's.)
        }

        [Test]
        public void TheTrackingLost_AfterTheFocusCameBack_AndTheTriggerHeldWhenItIsBack_PressesNothing_OnTheDialogNorTheMenu()
        {
            // The consent dialog, the focus lost and back, then the right controller's tracking lost with the trigger held.
            PlayMenu menu = NewMenu();
            PlayStatisticsService statistics = NewStatistics();
            Tick(menu);
            Assert.That(menu.AskConsent(statistics), Is.True);
            Vector3 agree = Centre(menu.Dialog.FirstRect);
            Tick(menu, pointAt: agree);
            GamePause.SetFocusLost(true);
            GamePause.SetFocusLost(false);
            GamePause.TakeSystemEndsForTest();
            // Not tracked: the frame reads no select (false) -- no evidence of a release.
            Tick(menu, unavailable: true, select: false);
            Tick(menu, unavailable: true, select: false);
            // Tracked again with the trigger held: no press.
            Tick(menu, select: true, pointAt: agree);
            Tick(menu, select: true, pointAt: agree);
            Assert.That(menu.IsDialogOpen, Is.True, "a trigger held when the tracking comes back is no click");
            Tick(menu, pointAt: agree);
            Tick(menu, select: true, pointAt: agree);
            Assert.That(menu.IsDialogOpen, Is.False, "let go in a read frame and pressed anew: a click");
            Assert.That(statistics.Consent.Agreed, Is.True);

            // The same on the menu's resume button, with a drag under way when the tracking is lost.
            Tick(menu);
            Tick(menu, menuDown: true);
            Tick(menu);
            PlayMenuView.Row speed = Speed(menu);
            Tick(menu, select: true, pointAt: Along(speed.background.rectTransform, 0.3f));
            Assert.That(menu.View.HeldRow, Is.SameAs(speed));
            float dragged = speed.read();
            Tick(menu, unavailable: true, select: false);
            Assert.That(menu.View.HeldRow, Is.Null, "the drag is dropped when the tracking is lost");
            Vector3 resume = Centre(menu.View.ResumeRect);
            Tick(menu, select: true, pointAt: resume);
            Tick(menu, select: true, pointAt: Along(speed.background.rectTransform, 0.9f));
            Assert.That(menu.IsOpen, Is.True, "the trigger held when the tracking comes back resumes nothing");
            Assert.That(speed.read(), Is.EqualTo(dragged), "and drags nothing");
            Tick(menu, pointAt: resume);
            Tick(menu, select: true, pointAt: resume);
            Assert.That(menu.IsOpen, Is.False, "let go and pressed anew: resumed");
        }

        [Test]
        public void TheMenuButton_HeldOrLetGoThroughAnInterruption_PressesOnlyAfterAReadFrameShowedItLetGo()
        {
            PlayMenu menu = NewMenu();
            Tick(menu);
            // Held through the interruption.
            GamePause.SetFocusLost(true);
            Tick(menu, menuDown: true);
            Tick(menu, menuDown: true);
            GamePause.SetFocusLost(false);
            GamePause.TakeSystemEndsForTest();
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.False, "held through: no press after");
            Tick(menu);
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.True, "let go in a read frame and pressed anew: a press");
            Tick(menu);
            Tick(menu, menuDown: true);
            Tick(menu);
            Assert.That(menu.IsOpen, Is.False);
            // Let go during the interruption (the controllers read false meanwhile): that is no evidence either.
            GamePause.SetFocusLost(true);
            Tick(menu, menuDown: false);
            Tick(menu, menuDown: false);
            GamePause.SetFocusLost(false);
            GamePause.TakeSystemEndsForTest();
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.False, "what the button read while the system had the input is no release");
            Tick(menu);
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.True);
            // Not read (tracking lost) with the button down, then read again with it still down: no press.
            Tick(menu);
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.False, "closed by the press");
            Tick(menu, unavailable: true, menuDown: false);
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.False, "a frame not read is no release");
            Tick(menu);
            Tick(menu, menuDown: true);
            Assert.That(menu.IsOpen, Is.True);
        }

        [Test]
        public void AValueOrThePoseNotRead_ThoughTheDeviceIsValidAndTracked_IsAnInputNotRead_WithItsCause()
        {
            Assert.That(PlayMenuDeviceInput.Unavailable(true, true, true, true, true, true, true, true), Is.EqualTo(PlayMenuInputUnavailable.None), "everything read");
            Assert.That(PlayMenuDeviceInput.Unavailable(false, true, true, true, true, true, true, true), Is.EqualTo(PlayMenuInputUnavailable.LeftNotValid));
            Assert.That(PlayMenuDeviceInput.Unavailable(true, false, false, true, true, true, true, true), Is.EqualTo(PlayMenuInputUnavailable.RightNotValid));
            Assert.That(PlayMenuDeviceInput.Unavailable(true, true, false, true, true, true, true, true), Is.EqualTo(PlayMenuInputUnavailable.RightNotTracked), "the tracking lost: told apart from the rest");
            Assert.That(PlayMenuDeviceInput.Unavailable(true, true, true, false, true, true, true, true), Is.EqualTo(PlayMenuInputUnavailable.ValueNotRead), "the menu button not read");
            Assert.That(PlayMenuDeviceInput.Unavailable(true, true, true, true, false, true, true, true), Is.EqualTo(PlayMenuInputUnavailable.ValueNotRead), "the left stick not read");
            Assert.That(PlayMenuDeviceInput.Unavailable(true, true, true, true, true, false, true, true), Is.EqualTo(PlayMenuInputUnavailable.ValueNotRead), "the right stick not read");
            Assert.That(PlayMenuDeviceInput.Unavailable(true, true, true, true, true, true, false, true), Is.EqualTo(PlayMenuInputUnavailable.ValueNotRead), "the trigger not read");
            Assert.That(PlayMenuDeviceInput.Unavailable(true, true, true, true, true, true, true, false), Is.EqualTo(PlayMenuInputUnavailable.PoseNotRead), "tracked and the buttons read, the pose alone not read");
            Assert.That(PlayMenuDeviceInput.Unavailable(true, true, false, true, true, true, true, false), Is.EqualTo(PlayMenuInputUnavailable.RightNotTracked), "not tracked: the pose's absence is that");
        }

        [Test]
        public void ThePoseAloneNotRead_ThenBack_WithTheTriggerHeld_PressesNothing()
        {
            // The tracking flag and the buttons read, the position and rotation not (the pointer gone, the select reads
            // false), then the pose back with the trigger still held: no click on the dialog or the menu.
            PlayMenu menu = NewMenu();
            PlayStatisticsService statistics = NewStatistics();
            Tick(menu);
            Assert.That(menu.AskConsent(statistics), Is.True);
            Vector3 agree = Centre(menu.Dialog.FirstRect);
            Tick(menu, pointAt: agree);
            var poseLost = new PlayMenuInputFrame { inputUnavailable = true, unavailableCause = PlayMenuInputUnavailable.PoseNotRead, selectDown = false, hasPointer = false };
            _input.Frame = poseLost; menu.Tick();
            _input.Frame = poseLost; menu.Tick();
            Tick(menu, select: true, pointAt: agree);
            Tick(menu, select: true, pointAt: agree);
            Assert.That(menu.IsDialogOpen, Is.True, "the trigger held when the pose is back is no click");
            Tick(menu, pointAt: agree);
            Tick(menu, select: true, pointAt: agree);
            Assert.That(menu.IsDialogOpen, Is.False, "let go in a read frame and pressed anew: a click");
            Tick(menu);
            Tick(menu, menuDown: true);
            Tick(menu);
            Vector3 resume = Centre(menu.View.ResumeRect);
            _input.Frame = poseLost; menu.Tick();
            Tick(menu, select: true, pointAt: resume);
            Tick(menu, select: true, pointAt: resume);
            Assert.That(menu.IsOpen, Is.True, "nor a resume");
            Tick(menu, pointAt: resume);
            Tick(menu, select: true, pointAt: resume);
            Assert.That(menu.IsOpen, Is.False);
        }

        [Test]
        public void TheMenusEnd_UnderASystemPause_LeavesTheInputToTheGame_AndTheSystemPauseToTheSystem()
        {
            PlayMenu menu = NewMenu();
            Tick(menu);
            GamePause.SetFocusLost(true);
            Assert.That(_katana.LiveInputEnabled, Is.False);
            menu.Release();
            Assert.That(_katana.LiveInputEnabled && _plain.LiveInputEnabled && _city.liveInput, Is.True, "nothing switched off is left behind the menu");
            Assert.That(GamePause.System && GamePause.IsPaused, Is.True, "the system pause is not the menu's to end");
        }
    }
}
