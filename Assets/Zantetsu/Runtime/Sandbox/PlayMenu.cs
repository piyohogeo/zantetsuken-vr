using System.Collections.Generic;
using UnityEngine;
using Zantetsu.Core;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Whether the menu is open (DESIGN 4.6), from the menu button and the menu's own resume. Pure: one step a frame.
    /// <para>
    /// **A press, not a hold.** The menu opens or closes when the button goes down, once: held down it does nothing
    /// more, and a button already down when this starts is no press until it has been let go.
    /// </para>
    /// <para>
    /// **Nothing of the menu reaches the game.** After a close the game's own input stays off until everything the
    /// game would act on -- the sticks, the trigger -- has been let go once: the trigger that pressed "resume" and a
    /// stick held through the pause do not move or turn the player in the frames after it. What "let go" is, is not
    /// this state's to say: its caller gives it (the walking inputs' own neutral, <see cref="PlayMenu"/>).
    /// </para>
    /// </summary>
    public sealed class PlayMenuState
    {
        private bool _menuWasDown = true;

        public bool Open { get; private set; }

        /// <summary>The menu was closed and something the game would act on has been held ever since.</summary>
        public bool AwaitingRelease { get; private set; }

        /// <summary>Whether the game's own input is read: not while the menu is open, not until its input is let go after it.</summary>
        public bool GameInputAllowed => !Open && !AwaitingRelease;

        public int Opens { get; private set; }

        /// <summary>One frame. True when the menu opened or closed in it.</summary>
        public bool Step(bool menuDown, bool resumeAsked, bool gameInputHeld)
        {
            bool pressed = menuDown && !_menuWasDown;
            _menuWasDown = menuDown;
            bool changed = pressed || (Open && resumeAsked);
            if (changed)
            {
                Open = !Open;
                AwaitingRelease = !Open;
                if (Open)
                {
                    Opens++;
                }
            }

            if (AwaitingRelease && !gameInputHeld)
            {
                AwaitingRelease = false;
            }

            return changed;
        }

        /// <summary>Closes the menu without a press (the menu itself going away): the game's input comes back at once.</summary>
        internal void RequireRelease() => AwaitingRelease = true;

        /// <summary>
        /// After an interruption, or a frame whose input was not read (D-241): a menu button down now is no press
        /// until a read frame showed it let go and it is pressed anew. What the button read while the system had the
        /// input, or while it was not read, is no evidence of a release.
        /// </summary>
        internal void ForgetPress() => _menuWasDown = true;

        public void ForceClosed()
        {
            Open = false;
            AwaitingRelease = false;
        }

        /// <summary>
        /// Opens the menu without a press (a check's own opening, at a point of its choosing): as a press opens it,
        /// once. False, and nothing changed, when it is open already. The button closes it afterwards as ever.
        /// </summary>
        public bool OpenWithoutPress()
        {
            if (Open)
            {
                return false;
            }

            Open = true;
            AwaitingRelease = false;
            Opens++;
            return true;
        }
    }

    /// <summary>
    /// The player's own settings the menu changes (DESIGN 4.6): the volume, the move speed and the turn of the scene's
    /// one live locomotion input, whichever kind it is -- the crowd's (a turn speed) or the plain one (a snap angle).
    /// No way of moving is added or changed here.
    /// <para>
    /// **Kept for play only.** In <c>play</c> the values are saved as they are changed and put back at the next start;
    /// in a scripted scenario nothing saved is applied and the move speed and the turn cannot be changed, because the
    /// scripts move the player with the same components.
    /// </para>
    /// </summary>
    public sealed class PlaySettings
    {
        public const string VolumeKey = "zantetsu.play.volume";
        public const string MoveSpeedKey = "zantetsu.play.moveSpeed";
        public const string TurnSpeedKey = "zantetsu.play.turnDegreesPerSecond";
        public const string SnapTurnKey = "zantetsu.play.snapTurnDegrees";
        public const string BladeAngleKey = "zantetsu.play.bladeAngleDegrees";

        public const float MoveSpeedMinimum = 0.5f, MoveSpeedMaximum = 3.0f, MoveSpeedStep = 0.1f;
        public const float TurnSpeedMinimum = 30f, TurnSpeedMaximum = 240f, TurnSpeedStep = 10f;
        public const float SnapTurnMinimum = 15f, SnapTurnMaximum = 90f, SnapTurnStep = 15f;

        /// <summary>The katana's turn toward its edge from the scene's grip offset, in degrees (negative: toward the spine).</summary>
        public const float BladeAngleMinimum = -30f, BladeAngleMaximum = 60f, BladeAngleStep = 2.5f;

        /// <summary>
        /// Play's blade angle where the player has saved none (DESIGN 19.1.11, D-222): 30 degrees toward the edge of
        /// the scenes' provisional grip offset, the angle at which the sword was seen to follow the controller.
        /// </summary>
        public const float BladeAngleDefault = 30f;

        private readonly bool _persist;
        private MobPlanPlayerInput _city;
        private SandboxLocomotionInput _plain;
        private readonly List<SandboxRightHandKatana> _katanas = new List<SandboxRightHandKatana>();

        public PlaySettings(bool persist)
        {
            _persist = persist;
        }

        /// <summary>Whether the move speed and the turn are the player's to change: in play, with a locomotion input found.</summary>
        public bool MovementChangeable => _persist && (_city != null || _plain != null);

        /// <summary>The turn is a speed (the crowd's input) rather than a snap angle (the plain one).</summary>
        public bool TurnIsContinuous => _city != null;

        public bool HasLocomotion => _city != null || _plain != null;

        /// <summary>
        /// Whether the blade's angle is the player's to change: in play, with a katana in the scene. A scripted
        /// scenario replays recorded hands with the offset they were recorded for.
        /// </summary>
        public bool BladeAngleChangeable => _persist && _katanas.Count > 0;

        /// <summary>Finds the scene's live locomotion input: the crowd's where there is one, the plain one otherwise.</summary>
        public void Bind()
        {
            _katanas.Clear();
            foreach (SandboxRightHandKatana katana in Object.FindObjectsByType<SandboxRightHandKatana>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                _katanas.Add(katana);
            }

            _city = null;
            _plain = null;
            foreach (MobPlanPlayerInput input in Object.FindObjectsByType<MobPlanPlayerInput>(FindObjectsSortMode.None))
            {
                if (input.enabled && input.gameObject.activeInHierarchy)
                {
                    _city = input;
                    return;
                }
            }

            foreach (SandboxLocomotionInput input in Object.FindObjectsByType<SandboxLocomotionInput>(FindObjectsSortMode.None))
            {
                if (input.enabled && input.gameObject.activeInHierarchy)
                {
                    _plain = input;
                    return;
                }
            }
        }

        /// <summary>Puts the saved values on, where this is play and something was saved. Returns what it did, for the log.</summary>
        public string ApplySaved()
        {
            if (!_persist)
            {
                return "saved settings not applied (not play)";
            }

            int applied = 0;
            if (PlayerPrefs.HasKey(VolumeKey))
            {
                AudioListener.volume = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumeKey));
                applied++;
            }

            if (HasLocomotion && PlayerPrefs.HasKey(MoveSpeedKey))
            {
                SetMoveSpeed(PlayerPrefs.GetFloat(MoveSpeedKey), false);
                applied++;
            }

            string turnKey = TurnIsContinuous ? TurnSpeedKey : SnapTurnKey;
            if (HasLocomotion && PlayerPrefs.HasKey(turnKey))
            {
                SetTurn(PlayerPrefs.GetFloat(turnKey), false);
                applied++;
            }

            // The blade's angle is play's own: the saved one, or play's default where none was saved.
            string blade = "";
            if (_katanas.Count > 0)
            {
                bool saved = PlayerPrefs.HasKey(BladeAngleKey);
                SetBladeAngle(saved ? PlayerPrefs.GetFloat(BladeAngleKey) : BladeAngleDefault, false);
                if (saved) applied++;
                blade = "; blade angle " + BladeAngle.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + (saved ? " (saved)" : " (play's default)");
            }

            return "saved settings applied: " + applied + blade;
        }

        public float BladeAngle => _katanas.Count > 0 && _katanas[0] != null ? _katanas[0].EdgewardTiltDegrees : 0f;

        public void SetBladeAngle(float value) => SetBladeAngle(value, true);

        private void SetBladeAngle(float value, bool save)
        {
            if (!_persist)
            {
                return;
            }

            value = Mathf.Clamp(value, BladeAngleMinimum, BladeAngleMaximum);
            bool any = false;
            foreach (SandboxRightHandKatana katana in _katanas)
            {
                if (katana != null)
                {
                    katana.EdgewardTiltDegrees = value;
                    any = true;
                }
            }

            if (any && save)
            {
                Save(BladeAngleKey, value);
            }
        }

        public float Volume => AudioListener.volume;

        public void SetVolume(float value)
        {
            AudioListener.volume = Mathf.Clamp01(value);
            Save(VolumeKey, AudioListener.volume);
        }

        public float MoveSpeed => _city != null ? _city.speed : _plain != null ? _plain.MoveSpeed : 0f;

        public void SetMoveSpeed(float value) => SetMoveSpeed(value, true);

        private void SetMoveSpeed(float value, bool save)
        {
            value = Mathf.Clamp(value, MoveSpeedMinimum, MoveSpeedMaximum);
            if (_city != null)
            {
                _city.speed = value;
            }
            else if (_plain != null)
            {
                _plain.MoveSpeed = value;
            }
            else
            {
                return;
            }

            if (save)
            {
                Save(MoveSpeedKey, value);
            }
        }

        public float Turn => _city != null ? _city.turnDegreesPerSecond : _plain != null ? _plain.SnapTurnDegrees : 0f;

        public void SetTurn(float value) => SetTurn(value, true);

        private void SetTurn(float value, bool save)
        {
            if (_city != null)
            {
                value = Mathf.Clamp(value, TurnSpeedMinimum, TurnSpeedMaximum);
                _city.turnDegreesPerSecond = value;
                if (save) Save(TurnSpeedKey, value);
            }
            else if (_plain != null)
            {
                value = Mathf.Clamp(value, SnapTurnMinimum, SnapTurnMaximum);
                _plain.SnapTurnDegrees = value;
                if (save) Save(SnapTurnKey, value);
            }
        }

        private void Save(string key, float value)
        {
            if (_persist)
            {
                PlayerPrefs.SetFloat(key, value);
            }
        }

        /// <summary>How often the settings were written through (tests): once a close, once an interruption's start.</summary>
        public static int Flushes { get; private set; }

        public void Flush()
        {
            Flushes++;
            if (_persist)
            {
                PlayerPrefs.Save();
            }
        }
    }

    /// <summary>
    /// The pause menu's small entrance (DESIGN 4.6): each frame, before any game input, it reads the menu button,
    /// opens or closes the menu, and sets the game's manual pause to match. Opened, it shows the settings before the
    /// player and gives the right controller to the menu; closed, it hands the controllers back to the game.
    /// <para>
    /// **What it stops is the game's progress, by <see cref="GamePause"/>**, which every entrance of progress reads
    /// (and which pauses the physics step). It disables no world, driver or drawing. Of the game's input it switches
    /// off only what reads the controllers by itself: the katana's live input -- whose switch starts the slash state
    /// over, so no movement of the hand during the pause is part of a stroke after it -- and the locomotion inputs'.
    /// Head and controller tracking, the cameras and the display go on.
    /// </para>
    /// <para>
    /// **The headset coming off is not this.** The application's own pause is <see cref="GamePause.System"/>; it
    /// neither opens nor closes the menu, and its end leaves a menu the player opened open and the game paused.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(-30000)]
    [DisallowMultipleComponent]
    public sealed class PlayMenu : MonoBehaviour
    {
        public const string Prefix = "ZANTETSU MENU: ";

        private readonly PlayMenuState _state = new PlayMenuState();
        private readonly List<SandboxRightHandKatana> _katanas = new List<SandboxRightHandKatana>();
        private readonly List<SandboxLocomotionInput> _plainInputs = new List<SandboxLocomotionInput>();
        private readonly List<MobPlanPlayerInput> _cityInputs = new List<MobPlanPlayerInput>();
        private PlayMenuView _view;
        private PlayDialogView _dialog;
        private PlayStatisticsService _statistics;
        private bool _dialogOpen;
        private PlaySettings _settings;
        private Camera _camera;
        private bool _gameInputHeldBack;

        /// <summary>
        /// Who holds the game's input away from the game (D-241): the menu, the consent dialog, the system pause.
        /// The input is taken when the first holder comes and given back when the last one goes; no holder's coming
        /// takes it twice, and no holder's going gives back what another still holds.
        /// </summary>
        [System.Flags]
        internal enum Holder { None = 0, Menu = 1, Dialog = 2, System = 4 }

        private Holder _holders;
        private bool _inputUnavailable;
        private PlayMenuInputUnavailable _unavailableCause;

        internal Holder Holders => _holders;

        public static PlayMenu Instance { get; private set; }

        /// <summary>The source of input: the devices unless a test gives another.</summary>
        public IPlayMenuInput Input { get; set; } = new PlayMenuDeviceInput();

        /// <summary>The player's camera: the main camera unless one is given. Its parent is the rig's tracking space.</summary>
        public Camera Camera
        {
            get => _camera;
            set => _camera = value;
        }

        public SandboxScenarioKind Scenario { get; private set; }
        public PlayMenuState State => _state;
        public PlayMenuView View => _view;
        public PlaySettings Settings => _settings;
        public bool IsOpen => _state.Open;

        /// <summary>The first-run consent dialog is shown (the game paused, the menu's button does nothing meanwhile).</summary>
        public bool IsDialogOpen => _dialogOpen;

        public PlayDialogView Dialog => _dialog;

        /// <summary>Makes the menu for this session's scenario, once. It lives across scene loads.</summary>
        public static PlayMenu Install(SandboxScenarioKind scenario)
        {
            if (Instance != null)
            {
                return Instance;
            }

            var host = new GameObject("Zantetsu play menu");
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(host);
            }

            PlayMenu menu = host.AddComponent<PlayMenu>();
            Instance = menu;
            Debug.Log(Prefix + "installed for " + SandboxScenario.Name(scenario) + "; " + menu.Setup(scenario)
                + "; opened by the left controller's menu button (Escape on a keyboard)");
            return menu;
        }

        /// <summary>The scenario this menu serves: its settings are kept and changeable in play only. Returns what was applied, for the log.</summary>
        internal string Setup(SandboxScenarioKind scenario)
        {
            Scenario = scenario;
            _settings = new PlaySettings(scenario == SandboxScenarioKind.Play);
            _settings.Bind();
            FollowSystemPause();
            return _settings.ApplySaved();
        }

        private void Update() => Tick();

        private bool _followingSystemPause;

        /// <summary>
        /// Follows the system pause (D-241): its beginning holds the game's input, drops the menu's and the dialog's
        /// pending pointer state and saves the settings once; its end gives the input back as a close does -- the
        /// katana at once unless a menu or a dialog still holds it, the walking once the input is read and let go.
        /// A system pause already holding when this is called is taken as beginning now.
        /// </summary>
        internal void FollowSystemPause()
        {
            if (_followingSystemPause) return;
            _followingSystemPause = true;
            GamePause.SystemChanged += OnSystemChanged;
            if (GamePause.System) OnSystemChanged(true);
        }

        private void OnSystemChanged(bool began)
        {
            if (began)
            {
                _settings?.Flush();   // the settings changed so far are kept, once, whatever the pause does next
                HoldGameInput(Holder.System);
                _view?.CancelInput();
                _dialog?.CancelInput();
                Debug.Log(Prefix + "system pause began (frame " + Time.frameCount + "): input held by " + _holders + "; menu " + (_state.Open ? "open" : "closed")
                    + ", dialog " + (_dialogOpen ? "open" : "closed") + "; settings flushed; " + GamePause.Describe());
                return;
            }

            // The pointer's select and the menu's button as they are now are no press: what a read frame shows let go
            // and then pressed anew counts. The walking waits for a frame whose input is read and let go.
            _view?.CancelInput();
            _dialog?.CancelInput();
            _state.ForgetPress();
            _dialogMenuWasDown = true;
            _state.RequireRelease();
            ReleaseGameInput(Holder.System);
            // The consent dialog stands before the head again (unit 7-B, B4): a system pause that restarted the XR
            // session (a sleep, the headset taken off) remakes the tracking space about the head's new place and
            // facing, and a dialog left where it was in the world is then beside or behind the player, who cannot
            // answer what they cannot find. The head's position alone did not tell it (within 0.5 m).
            if (_dialogOpen && _dialog != null) PlaceDialog("the system pause ended");
            Debug.Log(Prefix + "system pause ended (frame " + Time.frameCount + "): input held by " + _holders + "; menu " + (_state.Open ? "open" : "closed")
                + ", dialog " + (_dialogOpen ? "open" : "closed") + "; the walking waits for the input to be read and let go; " + GamePause.Describe());
        }

        /// <summary>One frame of the menu: what <c>Update</c> does, before any game input of the frame.</summary>
        internal void Tick()
        {
            _settings ??= new PlaySettings(false);
            if (_camera == null)
            {
                _camera = Camera.main;
            }

            Transform trackingSpace = _camera != null ? _camera.transform.parent : null;
            PlayMenuInputFrame input = Input != null ? Input.Read(trackingSpace, _camera) : default;
            if (GamePause.System)
            {
                // The system has the input (D-241): nothing of the menu, the dialog or the game acts on it, and what
                // the controllers read meanwhile is no evidence of a release (the end forgets the press); the
                // pictures stay as they are.
                return;
            }

            if (input.inputUnavailable != _inputUnavailable || (input.inputUnavailable && input.unavailableCause != _unavailableCause))
            {
                // One line a transition -- read / not read, and a change of cause while not read -- never a frame.
                _inputUnavailable = input.inputUnavailable;
                _unavailableCause = input.unavailableCause;
                Debug.Log(Prefix + (input.inputUnavailable ? "the input is not read: cause=" + input.unavailableCause + "; nothing is let go, nothing is pressed"
                    : "the input is read again") + " (frame " + Time.frameCount + ")");
            }

            if (input.inputUnavailable)
            {
                // Not read (D-241): no pointer reaches the menu or the dialog, a drag is dropped, a select or the menu
                // button held across this is no press afterwards, and the walking is not let go (GameInputHeld).
                _view?.CancelInput();
                _dialog?.CancelInput();
                _state.ForgetPress();
                _dialogMenuWasDown = true;
                return;
            }

            if (_dialogOpen)
            {
                // The consent dialog (DESIGN 21.18) has the pointer to itself; the menu's button waits. The dialog is
                // asked for when the scenario's scene has just loaded -- before the scene's own start and before the
                // headset's pose reaches the camera -- so where it first stands is not where the player's head is.
                // It is stood before the head again once the head is found away from where it was placed for (the
                // rig placed, the tracking begun, the player walked off), and whenever the menu's button is pressed:
                // a dialog the player cannot find cannot be answered, and the game is paused until it is.
                if (_camera != null)
                {
                    bool away = (_camera.transform.position - _dialogPlacedFor).sqrMagnitude > DialogReplaceMetres * DialogReplaceMetres;
                    bool asked = input.menuDown && !_dialogMenuWasDown;
                    if (away || asked)
                    {
                        PlaceDialog(away ? "the head is away from where it was placed for" : "the menu button");
                    }
                }

                _dialogMenuWasDown = input.menuDown;
                int pressed = _dialog.Drive(in input);
                if (pressed != 0)
                {
                    CloseDialog(pressed == 1);
                }

                return;
            }

            if (_state.Open && _view != null)
            {
                _view.Drive(in input);
            }

            bool resume = _view != null && _view.ConsumeResume();
            if (_state.Step(input.menuDown, resume, GameInputHeld(in input)))
            {
                if (_state.Open)
                {
                    Open();
                }
                else
                {
                    Close();
                }
            }

            if (_gameInputHeldBack && _state.GameInputAllowed)
            {
                GiveLocomotionBack();
            }
        }

        /// <summary>
        /// Whether anything the game would act on is held: the trigger or a movement key, or a stick that is not
        /// neutral for a walking input the menu took -- by that input's own dead zone and turn threshold, so that the
        /// input given back reads an empty command in its first frame. A stick only just off its centre, inside a
        /// threshold of the menu's own, would move the player the moment the input came back.
        /// </summary>
        private bool GameInputHeld(in PlayMenuInputFrame input)
        {
            if (input.inputUnavailable)
            {
                return true;   // not read is not let go (D-241): the release waits for a frame whose input is read
            }

            if (input.otherGameInputHeld || input.selectDown)
            {
                return true;
            }

            foreach (SandboxLocomotionInput plain in _plainInputs)
            {
                if (plain != null && !plain.IsNeutral(input.leftStick, input.rightStick.x))
                {
                    return true;
                }
            }

            foreach (MobPlanPlayerInput city in _cityInputs)
            {
                if (city != null && !MobPlanPlayerInput.IsNeutral(input.leftStick))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Opens the menu as its button does, without a press: the game is paused and the menu shown where the caller
        /// stands. For a check that needs the pause at a point of its own (the Prop Slash run's ending,
        /// <see cref="SandboxPropSlashPlayerCheck.PauseAtEndingArgument"/>); nothing in a run that does not ask.
        /// False when the menu is open already.
        /// </summary>
        /// <summary>
        /// Asks the first-run consent to the play statistics (DESIGN 21.18) where it was never answered: the game pauses
        /// and the controllers are taken as for the menu, the dialog stands before the head, and the answer goes to the
        /// service. Answered before: nothing is asked. Returns whether the dialog was opened.
        /// </summary>
        public bool AskConsent(PlayStatisticsService statistics)
        {
            _statistics = statistics;
            if (statistics == null || statistics.Consent.Decision != Core.Statistics.PlayStatisticsDecision.Undecided || _dialogOpen || _state.Open)
            {
                return false;
            }

            if (_camera == null)
            {
                _camera = Camera.main;
            }

            GamePause.SetManual(true);
            HoldGameInput(Holder.Dialog);
            if (_dialog == null)
            {
                _dialog = new PlayDialogView();
                _dialog.Build(PlayStatisticsService.ConsentTitle, PlayStatisticsService.ConsentBody, PlayStatisticsService.ConsentAgree, PlayStatisticsService.ConsentDecline);
            }

            _dialogOpen = true;
            _dialogMenuWasDown = true;   // a button down when it opens asks for nothing
            _dialogPlacements = 0;
            Debug.Log(Prefix + "consent dialog opened (frame " + Time.frameCount + "); the game is paused");
            PlaceDialog("opened");
            return true;
        }

        /// <summary>How far the head may be from where the dialog was placed for before the dialog is stood before it again, metres.</summary>
        public const float DialogReplaceMetres = 0.5f;

        private Vector3 _dialogPlacedFor;
        private bool _dialogMenuWasDown;
        private int _dialogPlacements;

        /// <summary>How often the open dialog was stood before the head (1 at its opening).</summary>
        public int DialogPlacements => _dialogPlacements;

        // The dialog before the head as it is now; the head's place is kept, to tell when the head has gone from it.
        private void PlaceDialog(string why)
        {
            Vector3 position = _camera != null ? _camera.transform.position : Vector3.zero;
            Quaternion rotation = _camera != null ? _camera.transform.rotation : Quaternion.identity;
            _dialog.Show(position, rotation);
            _dialogPlacedFor = position;
            _dialogPlacements++;
            // Few: the opening, the rig's placement and the tracking's beginning, then only what the player asks for.
            if (_dialogPlacements <= 8)
            {
                Debug.Log(Prefix + "consent dialog placed (" + why + ", frame " + Time.frameCount + "): head at " + position.ToString("F2") + ", facing yaw " + rotation.eulerAngles.y.ToString("F0"));
            }
        }

        private void CloseDialog(bool agreed)
        {
            _dialogOpen = false;
            _dialog.Hide();
            if (agreed) _statistics.Agree(); else _statistics.Withdraw();
            // The controllers come back as after the menu: the katana at once (unless the system still holds it), the
            // walking once the sticks were let go (the inputs taken at the opening are still the ones held back).
            ReleaseGameInput(Holder.Dialog);
            _state.RequireRelease();
            GamePause.SetManual(false);
            Debug.Log(Prefix + "consent dialog closed (frame " + Time.frameCount + "): " + (agreed ? "agreed" : "declined") + "; " + GamePause.Describe());
        }

        internal bool OpenWithoutPress()
        {
            if (!_state.OpenWithoutPress())
            {
                return false;
            }

            _settings ??= new PlaySettings(false);
            if (_camera == null)
            {
                _camera = Camera.main;
            }

            Open();
            return true;
        }

        private void Open()
        {
            // Before anything else of this frame reads it: the game's progress stops here.
            GamePause.SetManual(true);
            HoldGameInput(Holder.Menu);
            if (_view == null)
            {
                _view = new PlayMenuView();
                _settings.Bind();
                var bunny = FindFirstObjectByType<BunnyDemo>();
                _view.Build(Rows(), bunny != null ? "NONCOMMERCIAL Stanford Bunny demo" : Scenario == SandboxScenarioKind.Play
                    ? "play"
                    : SandboxScenario.Name(Scenario) + ": the script waits; speed and turn are the script's", bunny != null ? bunny.ResetScene : (System.Action)null,
                    Debug.isDebugBuild ? SwingsText : (System.Func<string>)null);
            }

            Vector3 position = _camera != null ? _camera.transform.position : Vector3.zero;
            Quaternion rotation = _camera != null ? _camera.transform.rotation : Quaternion.identity;
            _view.Show(position, rotation);
            Debug.Log(Prefix + "opened (frame " + Time.frameCount + "); the game is paused; the menu is drawn "
                + (_view.DrawnInFront ? "in front of everything" : "with uGUI's default material, depth tested (its own shader is not in this build)"));
        }

        internal void HoldLocomotionUntilRelease()
        {
            _state.RequireRelease();
            TakeGameInput();
            if (_holders == Holder.None)
            {
                GiveKatanaBack();   // the walking alone is held; the katana is nobody's to keep
            }
        }

        private void Close()
        {
            _view?.Hide();
            _settings.Flush();
            ReleaseGameInput(Holder.Menu);
            GamePause.SetManual(false);
            Debug.Log(Prefix + "closed (frame " + Time.frameCount + "); " + GamePause.Describe());
        }

        private IReadOnlyList<PlayMenuView.Row> Rows()
        {
            bool movement = _settings.MovementChangeable;
            var rows = new List<PlayMenuView.Row>
            {
                new PlayMenuView.Row
                {
                    label = "Volume", minimum = 0f, maximum = 1f, step = 0.05f, format = "P0",
                    read = () => _settings.Volume, write = _settings.SetVolume,
                },
                new PlayMenuView.Row
                {
                    label = "Speed", minimum = PlaySettings.MoveSpeedMinimum, maximum = PlaySettings.MoveSpeedMaximum,
                    step = PlaySettings.MoveSpeedStep, format = "F1", unit = " m/s", interactable = movement,
                    read = () => _settings.MoveSpeed, write = _settings.SetMoveSpeed,
                },
                _settings.TurnIsContinuous || !_settings.HasLocomotion
                    ? new PlayMenuView.Row
                    {
                        label = "Turn", minimum = PlaySettings.TurnSpeedMinimum, maximum = PlaySettings.TurnSpeedMaximum,
                        step = PlaySettings.TurnSpeedStep, format = "F0", unit = " °/s", interactable = movement,
                        read = () => _settings.Turn, write = _settings.SetTurn,
                    }
                    : new PlayMenuView.Row
                    {
                        label = "Snap turn", minimum = PlaySettings.SnapTurnMinimum, maximum = PlaySettings.SnapTurnMaximum,
                        step = PlaySettings.SnapTurnStep, format = "F0", unit = " °", interactable = movement,
                        read = () => _settings.Turn, write = _settings.SetTurn,
                    },
            };
            var bunny = FindFirstObjectByType<BunnyDemo>();
            if (bunny != null) rows.Add(new PlayMenuView.Row
            {
                label = "Bunny count", minimum = BunnyDemo.MinimumCount, maximum = BunnyDemo.MaximumCount, step = 1f, format = "F0",
                read = () => bunny.RequestedCount, write = value => bunny.RequestedCount = Mathf.RoundToInt(value),
            });
            rows.Add(new PlayMenuView.Row
            {
                label = "Cut surface debug", isToggle = true,
                read = () => VpCutSurfaceColour.DebugEnabled ? 1f : 0f,
                write = value => VpCutSurfaceColour.SetDebugEnabled(value >= 0.5f),
            });

            // The katana's angle in the hand, last: play's own (a scripted run keeps the offset its hands were recorded for).
            rows.Add(new PlayMenuView.Row
            {
                label = "Blade angle", minimum = PlaySettings.BladeAngleMinimum, maximum = PlaySettings.BladeAngleMaximum,
                step = PlaySettings.BladeAngleStep, format = "F1", unit = " °", interactable = _settings.BladeAngleChangeable,
                read = () => _settings.BladeAngle, write = _settings.SetBladeAngle,
            });
            // The play statistics' consent (DESIGN 21.18), changeable here: ON agrees (a new install id), OFF withdraws.
            PlayStatisticsService statistics = _statistics != null ? _statistics : PlayStatisticsService.Instance;
            if (statistics != null)
            {
                rows.Add(new PlayMenuView.Row
                {
                    label = "Send play statistics", isToggle = true,
                    read = () => statistics.Consent.Agreed ? 1f : 0f,
                    write = value => { if (value >= 0.5f) { if (!statistics.Consent.Agreed) statistics.Agree(); } else if (statistics.Consent.Agreed) statistics.Withdraw(); },
                });
            }

            return rows;
        }

        // The development readout under the settings (a Development build and the Editor only): the katana's last
        // swings and what the slash made of each (SlashSwingDiagnosis), read when the menu is shown.
        internal string SwingsText()
        {
            SandboxRightHandKatana katana = _katanas.Count > 0 ? _katanas[0] : FindFirstObjectByType<SandboxRightHandKatana>();
            return "Last swings (development readout; newest first)\n" + (katana != null ? katana.Swings.Describe(4) : "no katana");
        }

        // A holder comes: the input is taken for the first, and the katana's preview in the hand follows who holds.
        private void HoldGameInput(Holder holder)
        {
            bool first = _holders == Holder.None;
            _holders |= holder;
            if (first)
            {
                TakeGameInput();
            }

            UpdateKatanaPreview();
        }

        // A holder goes: the katana comes back with the last one; otherwise its preview follows who still holds.
        private void ReleaseGameInput(Holder holder)
        {
            _holders &= ~holder;
            if (_holders == Holder.None)
            {
                GiveKatanaBack();
            }
            else
            {
                UpdateKatanaPreview();
            }
        }

        // In play the katana is shown in the hand while the menu sets its angle -- but not while the system has the
        // input: the hand's picture goes with the pointer then, and comes back with the focus.
        private void UpdateKatanaPreview()
        {
            bool show = Scenario == SandboxScenarioKind.Play && (_holders & Holder.System) == 0;
            foreach (SandboxRightHandKatana katana in _katanas)
            {
                if (katana != null)
                {
                    katana.ShowPoseWhileInputOff = show;
                }
            }
        }

        // What reads the controllers by itself is switched off for the menu's time, and only what was on comes back.
        private void TakeGameInput()
        {
            _katanas.Clear();
            foreach (SandboxRightHandKatana katana in FindObjectsByType<SandboxRightHandKatana>(FindObjectsSortMode.None))
            {
                if (katana.LiveInputEnabled)
                {
                    _katanas.Add(katana);
                    katana.LiveInputEnabled = false;

                    // In play the katana goes on being shown in the hand, so that its angle can be seen while it is set.
                    katana.ShowPoseWhileInputOff = Scenario == SandboxScenarioKind.Play;
                }
            }

            if (_gameInputHeldBack)
            {
                // Opened again before the sticks were let go: the inputs taken at the first opening are still the ones to give back.
                return;
            }

            _plainInputs.Clear();
            foreach (SandboxLocomotionInput input in FindObjectsByType<SandboxLocomotionInput>(FindObjectsSortMode.None))
            {
                if (input.LiveInputEnabled)
                {
                    _plainInputs.Add(input);
                    input.LiveInputEnabled = false;
                }
            }

            _cityInputs.Clear();
            foreach (MobPlanPlayerInput input in FindObjectsByType<MobPlanPlayerInput>(FindObjectsSortMode.None))
            {
                if (input.liveInput)
                {
                    _cityInputs.Add(input);
                    input.liveInput = false;
                }
            }

            _gameInputHeldBack = true;
        }

        // The katana's live input comes back with the close: its switch starts the slash state over, so the hand's
        // movement during the pause is in no stroke.
        private void GiveKatanaBack()
        {
            foreach (SandboxRightHandKatana katana in _katanas)
            {
                if (katana != null)
                {
                    katana.ShowPoseWhileInputOff = false;
                    katana.LiveInputEnabled = true;
                }
            }

            _katanas.Clear();
        }

        // The locomotion inputs come back once everything they read has been let go.
        private void GiveLocomotionBack()
        {
            foreach (SandboxLocomotionInput input in _plainInputs)
            {
                if (input != null)
                {
                    input.LiveInputEnabled = true;
                }
            }

            foreach (MobPlanPlayerInput input in _cityInputs)
            {
                if (input != null)
                {
                    input.liveInput = true;
                }
            }

            _plainInputs.Clear();
            _cityInputs.Clear();
            _gameInputHeldBack = false;
            Debug.Log(Prefix + "the walking input is the game's again (frame " + Time.frameCount + "): the input was read and let go");
        }

        private void OnDestroy() => Release();

        /// <summary>What the menu's end does: no pause and no input switched off is left behind it.</summary>
        internal void Release()
        {
            if (_followingSystemPause)
            {
                GamePause.SystemChanged -= OnSystemChanged;
                _followingSystemPause = false;
            }

            if (_dialogOpen)
            {
                GamePause.SetManual(false);
            }

            _dialog?.Destroy();
            _dialog = null;
            _dialogOpen = false;
            if (_state.Open)
            {
                GamePause.SetManual(false);
            }

            _holders = Holder.None;
            GiveKatanaBack();
            _state.ForceClosed();
            if (_gameInputHeldBack)
            {
                GiveLocomotionBack();
            }

            _view?.Destroy();
            _view = null;
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
