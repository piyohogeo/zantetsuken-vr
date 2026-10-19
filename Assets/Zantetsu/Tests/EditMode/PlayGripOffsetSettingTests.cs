using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The menu's "Grip offset" (TL 2026-10-19: the hand seemed to hold the blade's root): the katana moved along its own
    /// blade axis, -10 to +30 cm, 5 cm by default (TL, the same day, after trying it), play's own -- what is shown and what cuts move together, the blade's
    /// angle keeps applying about the moved katana, saved and put back at the next start of play; a scripted scenario
    /// keeps the scenes' offset.
    /// </summary>
    public sealed class PlayGripOffsetSettingTests
    {
        private readonly List<Object> _made = new List<Object>();
        private SandboxRightHandKatana _katana;
        private bool _hadGrip, _hadBlade;
        private float _grip, _blade;

        [SetUp]
        public void SetUp()
        {
            _hadGrip = PlayerPrefs.HasKey(PlaySettings.GripOffsetKey);
            _grip = _hadGrip ? PlayerPrefs.GetFloat(PlaySettings.GripOffsetKey) : 0f;
            _hadBlade = PlayerPrefs.HasKey(PlaySettings.BladeAngleKey);
            _blade = _hadBlade ? PlayerPrefs.GetFloat(PlaySettings.BladeAngleKey) : 0f;
            PlayerPrefs.DeleteKey(PlaySettings.GripOffsetKey);
            PlayerPrefs.DeleteKey(PlaySettings.BladeAngleKey);
            var go = new GameObject("Katana rig");
            _made.Add(go);
            _katana = go.AddComponent<SandboxRightHandKatana>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object made in _made) if (made != null) Object.DestroyImmediate(made);
            _made.Clear();
            PlayerPrefs.DeleteKey(PlaySettings.GripOffsetKey);
            PlayerPrefs.DeleteKey(PlaySettings.BladeAngleKey);
            if (_hadGrip) PlayerPrefs.SetFloat(PlaySettings.GripOffsetKey, _grip);
            if (_hadBlade) PlayerPrefs.SetFloat(PlaySettings.BladeAngleKey, _blade);
            PlayerPrefs.Save();
        }

        [Test]
        public void TheRange_IsMinus10To30Centimetres_WithTheDefault5()
        {
            Assert.That(PlaySettings.GripOffsetMinimum, Is.EqualTo(-10f));
            Assert.That(PlaySettings.GripOffsetMaximum, Is.EqualTo(30f));
            Assert.That(PlaySettings.GripOffsetDefault, Is.EqualTo(5f));
            Assert.That(PlaySettings.GripOffsetStep, Is.EqualTo(1f));
        }

        [Test]
        public void TheKatana_MovesAlongItsOwnBladeAxis_ShownAndCuttingTogether_AndTheAngleStillTurnsIt()
        {
            Pose before = _katana.GripToKatanaOffset;
            var sample = new Zantetsu.Core.Input.BladePoseSample(1, 0.5, new Vector3(0.2f, 1.3f, -0.4f), Quaternion.Euler(10f, 90f, 0f),
                Zantetsu.Core.Input.BladeTrackingState.Position | Zantetsu.Core.Input.BladeTrackingState.Rotation);
            Assert.That(Zantetsu.Core.Input.BladePoseAdapter.TryEvaluate(sample, before, _katana.BladeFrame, out Zantetsu.Core.Input.EvaluatedBladePose poseBefore), Is.True);
            _katana.GripAxialOffsetMetres = 0.1f;
            Pose after = _katana.GripToKatanaOffset;
            Assert.That(Quaternion.Angle(after.rotation, before.rotation), Is.LessThan(1e-4f), "no turn");
            Vector3 axis = before.rotation * Vector3.forward;
            Assert.That(Vector3.Distance(after.position, before.position + (axis * 0.1f)), Is.LessThan(1e-5f), "10 cm toward the tip, along the blade");

            // What is shown (the katana's pose) and what cuts (the cut sample point) both move 10 cm along the blade in the world.
            Assert.That(Zantetsu.Core.Input.BladePoseAdapter.TryEvaluate(sample, after, _katana.BladeFrame, out Zantetsu.Core.Input.EvaluatedBladePose poseAfter), Is.True);
            Vector3 worldAxis = poseBefore.BladeAxis;
            Assert.That(Vector3.Distance(poseAfter.KatanaPose.position, poseBefore.KatanaPose.position + (worldAxis * 0.1f)), Is.LessThan(1e-5f), "the shown katana");
            Assert.That(Vector3.Distance(poseAfter.CutSamplePosition, poseBefore.CutSamplePosition + (worldAxis * 0.1f)), Is.LessThan(1e-5f), "the cutting point");

            // With the angle: the shift is along the turned blade.
            _katana.EdgewardTiltDegrees = 30f;
            Pose turned = _katana.GripToKatanaOffset;
            Vector3 turnedAxis = turned.rotation * Vector3.forward;
            _katana.GripAxialOffsetMetres = 0f;
            Pose turnedAtZero = _katana.GripToKatanaOffset;
            Assert.That(Vector3.Distance(turned.position, turnedAtZero.position + (turnedAxis * 0.1f)), Is.LessThan(1e-5f), "along the blade as it is turned");
            Assert.That(Vector3.Distance(turnedAtZero.position, before.position), Is.LessThan(1e-6f), "0 is the scene's own position");
        }

        [Test]
        public void InPlay_TheDefaultIsApplied_ASetValueReachesTheKatana_AndIsKept()
        {
            var settings = new PlaySettings(true);
            settings.Bind();
            string applied = settings.ApplySaved();
            Assert.That(applied, Does.Contain("grip offset 5 cm (default)"));
            Assert.That(_katana.GripAxialOffsetMetres, Is.EqualTo(0.05f).Within(1e-6f));
            Assert.That(PlayerPrefs.HasKey(PlaySettings.GripOffsetKey), Is.False, "the default is applied, not saved");

            settings.SetGripOffset(12f);
            Assert.That(_katana.GripAxialOffsetMetres, Is.EqualTo(0.12f).Within(1e-6f));
            Assert.That(settings.GripOffset, Is.EqualTo(12f).Within(1e-4f));
            Assert.That(PlayerPrefs.GetFloat(PlaySettings.GripOffsetKey), Is.EqualTo(12f));
            settings.SetGripOffset(99f);
            Assert.That(settings.GripOffset, Is.EqualTo(30f).Within(1e-4f), "clamped to the maximum");
            settings.SetGripOffset(-99f);
            Assert.That(settings.GripOffset, Is.EqualTo(-10f).Within(1e-4f), "clamped to the minimum");

            settings.SetGripOffset(7f);
            _katana.GripAxialOffsetMetres = 0f;
            var again = new PlaySettings(true);
            again.Bind();
            Assert.That(again.ApplySaved(), Does.Contain("grip offset 7 cm (saved)"));
            Assert.That(_katana.GripAxialOffsetMetres, Is.EqualTo(0.07f).Within(1e-6f), "put back at the next start of play");
        }

        [Test]
        public void InAScriptedScenario_TheScenesOffsetStands()
        {
            PlayerPrefs.SetFloat(PlaySettings.GripOffsetKey, 20f);
            var settings = new PlaySettings(false);
            settings.Bind();
            settings.ApplySaved();
            Assert.That(_katana.GripAxialOffsetMetres, Is.Zero, "a scripted scenario replays its hands with the offset they were recorded for");
            settings.SetGripOffset(15f);
            Assert.That(_katana.GripAxialOffsetMetres, Is.Zero);
            Assert.That(PlayerPrefs.GetFloat(PlaySettings.GripOffsetKey), Is.EqualTo(20f), "nothing written");
        }

        [Test]
        public void TheMenu_HasTheRow_BetweenTheDepenetrationAndTheBladeAngle()
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
                for (int i = 0; i < rows.Count; i++) if (rows[i].label == "Grip offset") at = i;
                Assert.That(at, Is.GreaterThan(0), "the row is there");
                Assert.That(rows[at - 1].label, Is.EqualTo("Depenetration"));
                Assert.That(rows[at + 1].label, Is.EqualTo("Blade angle"));
                PlayMenuView.Row row = rows[at];
                Assert.That(row.minimum, Is.EqualTo(-10f));
                Assert.That(row.maximum, Is.EqualTo(30f));
                Assert.That(row.step, Is.EqualTo(1f));
                Assert.That(row.unit, Is.EqualTo(" cm"));
                Assert.That(row.interactable, Is.True, "a katana in the scene, in play");
                Assert.That(row.read(), Is.EqualTo(5f).Within(1e-4f), "the default");
                row.write(8f);
                Assert.That(_katana.GripAxialOffsetMetres, Is.EqualTo(0.08f).Within(1e-6f));
            }
            finally
            {
                menu.Release();
            }
        }
    }
}
