using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Core;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Windows play's NEXT-launch preference. The current device and the launcher's explanation are read once;
    /// saving a choice changes neither. Scripted runs, the Editor and other platforms never read this preference.
    /// </summary>
    internal sealed class PlayMenuGraphicsSettings
    {
        internal const string NextLaunchNote = "Changes take effect on the next launch.";
        private const string ReadError = "Could not read the graphics preference. Select a graphics API to save it.";
        private const string SaveError = "Could not save the graphics preference. Select it again to retry.";
        private static readonly string[] Modes =
        {
            WindowsGraphicsPreference.Auto, WindowsGraphicsPreference.Direct3D12, WindowsGraphicsPreference.Direct3D11,
        };
        private readonly Action<string> _save;
        private readonly Action<string> _report;
        private readonly string _launchNote;
        private string _error;

        internal int SelectedIndex { get; private set; } = -1;
        internal string CurrentDescription { get; }
        private string BaseDescription => CurrentDescription + "\n" + NextLaunchNote + _launchNote;
        internal string Description => BaseDescription
            + (string.IsNullOrEmpty(_error) ? string.Empty : "\n" + _error);

        internal static PlayMenuGraphicsSettings ForCurrentPlayer(SandboxScenarioKind scenario)
        {
            // Gate before reading either the environment or the file: a recorded scenario never takes saved choices.
            if (!Available(scenario, Application.platform, Application.isEditor)) return null;
            return Create(scenario, Application.platform, Application.isEditor, SystemInfo.graphicsDeviceType,
                Environment.GetEnvironmentVariable(WindowsGraphicsPreference.SourceEnvironment),
                Environment.GetEnvironmentVariable(WindowsGraphicsPreference.ReasonEnvironment),
                WindowsGraphicsPreference.Load, WindowsGraphicsPreference.Save,
                message => Debug.LogWarning(PlayMenu.Prefix + message));
        }

        internal static bool Available(SandboxScenarioKind scenario, RuntimePlatform platform, bool editor)
            => !editor && platform == RuntimePlatform.WindowsPlayer && scenario == SandboxScenarioKind.Play;

        internal static PlayMenuGraphicsSettings Create(SandboxScenarioKind scenario, RuntimePlatform platform, bool editor,
            GraphicsDeviceType actual, string source, string reason, Func<string> load, Action<string> save, Action<string> report)
        {
            if (!Available(scenario, platform, editor)) return null;
            return new PlayMenuGraphicsSettings(actual, source, reason, load, save, report);
        }

        private PlayMenuGraphicsSettings(GraphicsDeviceType actual, string source, string reason,
            Func<string> load, Action<string> save, Action<string> report)
        {
            _save = save;
            _report = report;
            _launchNote = string.IsNullOrEmpty(source)
                ? "\nNext time, launch with CutWorldSandbox.exe or the launch settings app."
                : source == "command-line" ? "\nLaunch arguments take precedence over saved preferences." : string.Empty;
            CurrentDescription = "Current: " + actual + " / " + SourceLabel(source);
            if (!string.IsNullOrEmpty(source) && !string.IsNullOrWhiteSpace(reason))
                CurrentDescription += "\nReason: " + OneLine(reason);
            try
            {
                SelectedIndex = Array.IndexOf(Modes, load());
                if (SelectedIndex < 0) throw new InvalidDataException("Unknown graphics preference.");
            }
            catch (Exception exception) when (IsPreferenceError(exception))
            {
                _error = ReadError;
                _report?.Invoke("graphics preference could not be read: " + exception.GetType().Name + ": " + OneLine(exception.Message));
            }
        }

        internal void Select(int index)
        {
            if (index < 0 || index >= Modes.Length) throw new ArgumentOutOfRangeException(nameof(index));
            try
            {
                _save(Modes[index]);
                SelectedIndex = index;
                _error = null;
            }
            catch (Exception exception) when (IsPreferenceError(exception))
            {
                _error = SaveError;
                _report?.Invoke("graphics preference could not be saved: " + exception.GetType().Name + ": " + OneLine(exception.Message));
            }
        }

        internal void AppendRows(List<PlayMenuView.Row> rows)
        {
            rows.Add(new PlayMenuView.Row
            {
                label = "Graphics API",
                choices = new[] { "Auto (recommended)", "Standard D3D12", "Compatibility D3D11" },
                read = () => SelectedIndex,
                write = value => Select(Mathf.RoundToInt(value)),
            });
            rows.Add(new PlayMenuView.Row
            {
                // Measure enough space for a later error before the canvas is sized. Show only the current state.
                label = BaseDescription + "\n" + SaveError,
                isText = true, interactable = false, readText = () => Description,
            });
        }

        internal static string SourceLabel(string source)
        {
            switch (source)
            {
                case "auto": return "Automatic";
                case "saved": return "Saved preference";
                case "command-line": return "Launch argument";
                case null:
                case "": return "Direct launch";
                default: return "Unknown source (" + OneLine(source) + ")";
            }
        }

        internal static string OneLine(string value) => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        private static bool IsPreferenceError(Exception exception)
            => exception is IOException || exception is InvalidDataException || exception is UnauthorizedAccessException || exception is System.Security.SecurityException;
    }
}
