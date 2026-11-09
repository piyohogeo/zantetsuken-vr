using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    public sealed class PlayMenuGraphicsSettingsTests
    {
        private PlayMenuView _view;

        [TearDown]
        public void TearDown() { _view?.Destroy(); _view = null; }

        private static PlayMenuGraphicsSettings Settings(Func<string> load, Action<string> save,
            Action<string> report = null, string source = "auto", string reason = "AMD Radeon RX 570 compatibility selection")
            => PlayMenuGraphicsSettings.Create(SandboxScenarioKind.Play, RuntimePlatform.WindowsPlayer, false,
                GraphicsDeviceType.Direct3D11, source, reason, load, save, report);

        [TestCase(SandboxScenarioKind.CityWalk, RuntimePlatform.WindowsPlayer, false)]
        [TestCase(SandboxScenarioKind.StaticWait, RuntimePlatform.WindowsPlayer, false)]
        [TestCase(SandboxScenarioKind.NpcSlash, RuntimePlatform.WindowsPlayer, false)]
        [TestCase(SandboxScenarioKind.EmptyXr, RuntimePlatform.WindowsPlayer, false)]
        [TestCase(SandboxScenarioKind.Play, RuntimePlatform.Android, false)]
        [TestCase(SandboxScenarioKind.Play, RuntimePlatform.LinuxPlayer, false)]
        [TestCase(SandboxScenarioKind.Play, RuntimePlatform.OSXPlayer, false)]
        [TestCase(SandboxScenarioKind.Play, RuntimePlatform.WindowsEditor, true)]
        [TestCase(SandboxScenarioKind.Play, RuntimePlatform.WindowsPlayer, true)]
        public void OnlyWindowsPlayerPlay_HasTheSetting_OtherRunsNeverReadOrWriteIt(SandboxScenarioKind scenario, RuntimePlatform platform, bool editor)
        {
            int reads = 0, writes = 0;
            var settings = PlayMenuGraphicsSettings.Create(scenario, platform, editor, GraphicsDeviceType.Direct3D11,
                "auto", "reason", () => { reads++; return WindowsGraphicsPreference.Auto; }, _ => writes++, null);
            Assert.That(settings, Is.Null);
            Assert.That(reads, Is.Zero);
            Assert.That(writes, Is.Zero);
        }

        [Test]
        public void SelectionSavesForTheNextLaunch_TheActualDeviceAndLaunchReasonStayTheSame()
        {
            var saved = new List<string>();
            var settings = Settings(() => WindowsGraphicsPreference.Direct3D12, saved.Add);
            Assert.That(settings.SelectedIndex, Is.EqualTo(1));
            Assert.That(saved, Is.Empty, "opening never rewrites a preference");
            string current = settings.CurrentDescription;
            Assert.That(current, Does.Contain("Current: Direct3D11 / Automatic").And.Contain("RX 570"));
            settings.Select(2);
            settings.Select(0);
            Assert.That(saved, Is.EqualTo(new[] { WindowsGraphicsPreference.Direct3D11, WindowsGraphicsPreference.Auto }));
            Assert.That(settings.CurrentDescription, Is.EqualTo(current), "saving a preference cannot claim the running API changed");
            Assert.That(settings.Description, Does.Contain(PlayMenuGraphicsSettings.NextLaunchNote));
            Assert.That(PlayMenuGraphicsSettings.NextLaunchNote, Is.EqualTo("Changes take effect on the next launch."));
        }

        [TestCase("auto", "Automatic")]
        [TestCase("saved", "Saved preference")]
        [TestCase("command-line", "Launch argument")]
        [TestCase(null, "Direct launch")]
        [TestCase("", "Direct launch")]
        [TestCase("other", "Unknown source (other)")]
        public void StatusUsesTheActualDeviceAndLauncherSource(string source, string expected)
        {
            var settings = Settings(() => WindowsGraphicsPreference.Auto, _ => { }, source: source, reason: "the reason");
            Assert.That(settings.CurrentDescription, Does.Contain("Direct3D11 / " + expected));
            Assert.That(settings.CurrentDescription.Contains("the reason"), Is.EqualTo(!string.IsNullOrEmpty(source)));
            if (string.IsNullOrEmpty(source)) Assert.That(settings.Description, Does.Contain("CutWorldSandbox.exe or the launch settings app"));
            else Assert.That(settings.CurrentDescription, Does.Contain("Reason: the reason"));
            if (source == "command-line") Assert.That(settings.Description, Does.Contain("Launch arguments take precedence over saved preferences."));
            string line = SandboxLaunchRecord.DescribeGraphicsSelection(GraphicsDeviceType.Direct3D11, source, "the reason");
            Assert.That(line, Does.StartWith("actual=Direct3D11, source="));
            Assert.That(line, Does.Contain(string.IsNullOrEmpty(source) ? "direct (直接起動)" : source));
        }

        [Test]
        public void InvalidPreferenceIsVisible_AnExplicitChoiceRepairsIt()
        {
            var reports = new List<string>();
            string saved = null;
            var settings = Settings(() => throw new InvalidDataException("bad file"), mode => saved = mode, reports.Add);
            Assert.That(settings.SelectedIndex, Is.EqualTo(-1), "an invalid file is not presented as a saved auto choice");
            Assert.That(settings.Description, Does.Contain("Could not read the graphics preference."));
            Assert.That(reports.Count, Is.EqualTo(1));
            settings.Select(2);
            Assert.That(saved, Is.EqualTo(WindowsGraphicsPreference.Direct3D11));
            Assert.That(settings.SelectedIndex, Is.EqualTo(2));
            Assert.That(settings.Description, Does.Not.Contain("Could not read the graphics preference."));
        }

        [Test]
        public void SaveFailureKeepsTheLastSavedChoice_AndTheErrorFitsInTheMenu()
        {
            var reports = new List<string>();
            bool fail = true;
            var settings = Settings(() => WindowsGraphicsPreference.Auto, _ => { if (fail) throw new IOException("disk failure"); }, reports.Add);
            var rows = MakeView(settings);
            settings.Select(1);
            _view.Refresh();
            Assert.That(settings.SelectedIndex, Is.Zero);
            Assert.That(rows[0].valueText.text, Is.EqualTo("Auto (recommended)"));
            Assert.That(rows[1].labelText.text, Does.Contain("Could not save the graphics preference."));
            Assert.That(PlayMenuView.NeededHeight(rows[1].labelText), Is.LessThanOrEqualTo(rows[1].labelText.rectTransform.rect.height));
            Assert.That(reports.Count, Is.EqualTo(1));
            fail = false;
            settings.Select(2);
            _view.Refresh();
            Assert.That(rows[0].valueText.text, Is.EqualTo("Compatibility D3D11"));
            Assert.That(rows[1].labelText.text, Does.Not.Contain("Could not save the graphics preference."));
        }

        [Test]
        public void PointerCyclesTheThreeNamedChoices_OnlyOnANewPress()
        {
            var saved = new List<string>();
            var settings = Settings(() => WindowsGraphicsPreference.Auto, saved.Add);
            var rows = MakeView(settings);
            Assert.That(rows[0].label, Is.EqualTo("Graphics API"));
            Assert.That(rows[0].choices, Is.EqualTo(new[] { "Auto (recommended)", "Standard D3D12", "Compatibility D3D11" }));
            Vector3 target = rows[0].background.rectTransform.TransformPoint(rows[0].background.rectTransform.rect.center);
            var input = new PlayMenuInputFrame { hasPointer = true, pointerOrigin = Vector3.zero, pointerDirection = target.normalized };
            _view.Drive(in input);
            input.selectDown = true;
            for (int i = 0; i < 100; i++) _view.Drive(in input);
            Assert.That(saved, Is.EqualTo(new[] { WindowsGraphicsPreference.Direct3D12 }), "holding does not cycle or rewrite");
            Assert.That(rows[0].valueText.text, Is.EqualTo("Standard D3D12"));
            for (int i = 0; i < 2; i++)
            {
                input.selectDown = false; _view.Drive(in input);
                input.selectDown = true; _view.Drive(in input);
            }
            Assert.That(saved, Is.EqualTo(new[] { WindowsGraphicsPreference.Direct3D12, WindowsGraphicsPreference.Direct3D11, WindowsGraphicsPreference.Auto }));
            Assert.That(rows[1].labelText.text, Does.Contain("Current: Direct3D11 / Automatic"));
        }

        [Test]
        public void LongLauncherReasonWraps_InsideThePanelAndBelowTheChoice()
        {
            string reason = "The active adapter is AMD Radeon RX 570. Direct3D 11 was selected automatically for compatibility with this adapter. "
                + "A saved or explicit launch choice overrides automatic selection; the current device remains unchanged until the next launch.";
            var rows = MakeView(Settings(() => WindowsGraphicsPreference.Auto, _ => { }, reason: reason));
            var status = rows[1].labelText.rectTransform;
            var choice = rows[0].background.rectTransform;
            var panel = (RectTransform)_view.Root;
            Assert.That(rows[1].labelText.text, Does.Contain(reason).And.Contain(PlayMenuGraphicsSettings.NextLaunchNote));
            Assert.That(PlayMenuView.NeededHeight(rows[1].labelText), Is.LessThanOrEqualTo(status.rect.height));
            Assert.That(status.anchoredPosition.y + status.rect.height * 0.5f,
                Is.LessThanOrEqualTo(choice.anchoredPosition.y - choice.rect.height * 0.5f));
            Assert.That(status.anchoredPosition.y - status.rect.height * 0.5f, Is.GreaterThanOrEqualTo(-panel.rect.height * 0.5f));
            foreach (string name in rows[0].choices)
            {
                rows[0].valueText.text = name;
                Assert.That(rows[0].valueText.preferredWidth, Is.LessThanOrEqualTo(choice.rect.width), name + " fits the choice button");
            }
        }

        [Test]
        public void LaunchRecordKeepsItsActualApiAndReasonOnOneLine()
        {
            string line = SandboxLaunchRecord.DescribeGraphicsSelection(GraphicsDeviceType.Direct3D12, "command-line", "explicit\r\nselection");
            Assert.That(line, Is.EqualTo("actual=Direct3D12, source=command-line, reason=explicit  selection"));
        }

        private List<PlayMenuView.Row> MakeView(PlayMenuGraphicsSettings settings)
        {
            var rows = new List<PlayMenuView.Row>();
            settings.AppendRows(rows);
            _view = new PlayMenuView();
            _view.Build(rows, "play");
            _view.Show(Vector3.zero, Quaternion.identity);
            return rows;
        }
    }
}
