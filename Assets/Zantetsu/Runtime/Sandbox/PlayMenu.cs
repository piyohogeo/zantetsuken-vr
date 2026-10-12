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

        public void Flush()
        {
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
        private PlaySettings _settings;
        private Camera _camera;
        private bool _gameInputHeldBack;

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
            return _settings.ApplySaved();
        }

        private void Update() => Tick();

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
            TakeGameInput();
            if (_view == null)
            {
                _view = new PlayMenuView();
                _settings.Bind();
                var bunny = FindFirstObjectByType<BunnyDemo>();
                _view.Build(Rows(), bunny != null ? "NONCOMMERCIAL Stanford Bunny demo" : Scenario == SandboxScenarioKind.Play
                    ? "play"
                    : SandboxScenario.Name(Scenario) + ": the script waits; speed and turn are the script's", bunny != null ? bunny.ResetScene : (System.Action)null);
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
            GiveKatanaBack();
        }

        private void Close()
        {
            _view?.Hide();
            _settings.Flush();
            GiveKatanaBack();
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
            return rows;
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
        }

        private void OnDestroy() => Release();

        /// <summary>What the menu's end does: no pause and no input switched off is left behind it.</summary>
        internal void Release()
        {
            if (_state.Open)
            {
                GiveKatanaBack();
                GamePause.SetManual(false);
            }

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
