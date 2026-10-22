using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.AndroidFlat.Tests
{
    public sealed class AndroidFlatDrawSettingsTests
    {
        private const string Prefix = "zantetsu.androidflat.";
        private static readonly string[] FloatKeys =
        {
            "move", "turn", "scale", "sharpness",
            "stick.portrait.x", "stick.portrait.y", "stick.landscape.x", "stick.landscape.y"
        };
        private static readonly string[] IntKeys = { "fsr", "multiDrawIndirect", "drawIndirectCount" };
        private readonly List<Preference> saved = new List<Preference>();
        private readonly List<Object> made = new List<Object>();
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private struct Preference
        {
            public string key;
            public bool existed, integer;
            public int intValue;
            public float floatValue;
        }

        [SetUp]
        public void SetUp()
        {
            saved.Clear();
            foreach (string key in FloatKeys) Remember(key, false);
            foreach (string key in IntKeys) Remember(key, true);
            foreach (Preference value in saved) PlayerPrefs.DeleteKey(value.key);
        }

        private void Remember(string key, bool integer)
        {
            key = Prefix + key;
            saved.Add(new Preference
            {
                key = key, integer = integer, existed = PlayerPrefs.HasKey(key),
                intValue = integer ? PlayerPrefs.GetInt(key) : 0,
                floatValue = integer ? 0f : PlayerPrefs.GetFloat(key)
            });
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object value in made) if (value != null) Object.DestroyImmediate(value);
            made.Clear();
            foreach (Preference value in saved)
            {
                if (!value.existed) PlayerPrefs.DeleteKey(value.key);
                else if (value.integer) PlayerPrefs.SetInt(value.key, value.intValue);
                else PlayerPrefs.SetFloat(value.key, value.floatValue);
            }
            PlayerPrefs.Save();
            saved.Clear();
        }

        [Test]
        public void NewAndUnconfiguredSettings_RequestBothIndirectFeatures()
        {
            var settings = new AndroidFlatSettings();
            Assert.That(settings.MultiDrawIndirect, Is.True);
            Assert.That(settings.DrawIndirectCount, Is.True);
            settings.Load();
            Assert.That(settings.MultiDrawIndirect, Is.True);
            Assert.That(settings.DrawIndirectCount, Is.True);
        }

        [Test]
        public void DisabledFeatures_AreRestoredFromSavedSettings()
        {
            var settings = new AndroidFlatSettings { MultiDrawIndirect = false, DrawIndirectCount = false };
            settings.Save();
            var restored = new AndroidFlatSettings();
            restored.Load();
            Assert.That(restored.MultiDrawIndirect, Is.False);
            Assert.That(restored.DrawIndirectCount, Is.False);
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, true)]
        public void CountCapability_RequiresMultiDraw(bool multi, bool count, bool expectedCount)
        {
            var capabilities = new AndroidFlatDrawCapabilities(multi, count);
            Assert.That(capabilities.MultiDrawIndirect, Is.EqualTo(multi));
            Assert.That(capabilities.DrawIndirectCount, Is.EqualTo(expectedCount));
        }

        [Test]
        public void UnsupportedFeatures_CannotBeSelected_WithoutErasingTheUsersRequests()
        {
            var settings = new AndroidFlatSettings();
            object view = NewView();
            Layout(view, settings, new AndroidFlatDrawCapabilities(true, true));
            Color enabledMultiColour = ((Text)Field(Control(view, "MultiDrawIndirect"), "label")).color;
            Color enabledCountColour = ((Text)Field(Control(view, "DrawIndirectCount"), "label")).color;
            Layout(view, settings, new AndroidFlatDrawCapabilities(false, false));
            Checkbox(view, "MultiDrawIndirect", false, false);
            Checkbox(view, "DrawIndirectCount", false, false);
            Assert.That(((Text)Field(Control(view, "MultiDrawIndirect"), "label")).color, Is.Not.EqualTo(enabledMultiColour), "disabled option is visually distinguished");
            Assert.That(((Text)Field(Control(view, "DrawIndirectCount"), "label")).color, Is.Not.EqualTo(enabledCountColour));
            Assert.That(CommandAt(view, Centre(view, "MultiDrawIndirect")), Is.EqualTo("None"));
            Assert.That(CommandAt(view, Centre(view, "DrawIndirectCount")), Is.EqualTo("None"));
            Assert.That(settings.MultiDrawIndirect, Is.True, "availability must not overwrite the saved preference");
            Assert.That(settings.DrawIndirectCount, Is.True);
        }

        [Test]
        public void ADeviceWithoutCountDraws_StillOffersTheMultiDrawCheckbox()
        {
            object view = NewView();
            Layout(view, new AndroidFlatSettings(), new AndroidFlatDrawCapabilities(true, false));
            Checkbox(view, "MultiDrawIndirect", true, true);
            Checkbox(view, "DrawIndirectCount", false, false);
            Assert.That(CommandAt(view, Centre(view, "MultiDrawIndirect")), Is.EqualTo("MultiDrawIndirect"));
            Assert.That(CommandAt(view, Centre(view, "DrawIndirectCount")), Is.EqualTo("None"));
        }

        [Test]
        public void DisablingMultiDraw_DisablesCountInput_ButPreservesItsSavedRequestForReenabling()
        {
            var settings = new AndroidFlatSettings { MultiDrawIndirect = false, DrawIndirectCount = true };
            settings.Save();
            var restored = new AndroidFlatSettings();
            restored.Load();
            var capabilities = new AndroidFlatDrawCapabilities(true, true);
            object view = NewView();
            Layout(view, restored, capabilities);
            Checkbox(view, "MultiDrawIndirect", false, true);
            Checkbox(view, "DrawIndirectCount", false, false);
            Assert.That(CommandAt(view, Centre(view, "MultiDrawIndirect")), Is.EqualTo("MultiDrawIndirect"));
            Assert.That(CommandAt(view, Centre(view, "DrawIndirectCount")), Is.EqualTo("None"));
            Assert.That(restored.DrawIndirectCount, Is.True, "the requested count option survives a disabled parent feature");
            restored.MultiDrawIndirect = true;
            Layout(view, restored, capabilities);
            Checkbox(view, "MultiDrawIndirect", true, true);
            Checkbox(view, "DrawIndirectCount", true, true);
            Assert.That(CommandAt(view, Centre(view, "DrawIndirectCount")), Is.EqualTo("DrawIndirectCount"));
            Assert.That(restored.DrawIndirectCount, Is.True);
        }

        [Test]
        public void ADisabledCountRequest_OnASupportedDevice_IsUncheckedAndCanBeSelected()
        {
            object view = NewView();
            Layout(view, new AndroidFlatSettings { DrawIndirectCount = false }, new AndroidFlatDrawCapabilities(true, true));
            Checkbox(view, "DrawIndirectCount", false, true);
            Assert.That(CommandAt(view, Centre(view, "DrawIndirectCount")), Is.EqualTo("DrawIndirectCount"));
        }

        [Test]
        public void RenderingCommands_KeepTheCurrentRouteUntilApplied_AndRestoreCountAfterMultiDrawReturns()
        {
            VpBodyRouteRequest previousRoute = VpNativeDrawSetup.Request;
            bool previousMulti = VpNativeDrawSetup.UnityMultiDrawIndirect;
            bool previousCount = VpNativeDrawPlugin.VulkanIndirectCount;
            try
            {
                var host = new GameObject("Android rendering settings application under test");
                made.Add(host);
                AndroidFlatPlayer player = host.AddComponent<AndroidFlatPlayer>();
                typeof(AndroidFlatPlayer).GetField("drawCapabilities", Fields).SetValue(player, new AndroidFlatDrawCapabilities(true, true));
                typeof(AndroidFlatPlayer).GetField("launchBodyRoute", Fields).SetValue(player, VpBodyRouteRequest.NativePreferred);
                var settings = (AndroidFlatSettings)Field(player, "settings");

                Apply();
                Applied(VpBodyRouteRequest.NativePreferred, true, true);

                Command("MultiDrawIndirect");
                Assert.That(settings.MultiDrawIndirect, Is.False);
                Assert.That(PlayerPrefs.GetInt(Prefix + "multiDrawIndirect"), Is.Zero, "the command persists the requested setting");
                Assert.That(PlayerPrefs.GetInt(Prefix + "drawIndirectCount"), Is.EqualTo(1));
                Assert.That(Pending(), Is.True);
                Assert.That(VpNativeDrawSetup.Request, Is.EqualTo(VpBodyRouteRequest.NativePreferred), "the current world's native layout is retained until apply");
                Assert.That(VpNativeDrawSetup.UnityMultiDrawIndirect, Is.True);
                Assert.That(VpNativeDrawPlugin.VulkanIndirectCount, Is.True);

                Apply();
                Applied(VpBodyRouteRequest.UnityApi, false, false);
                Assert.That(settings.DrawIndirectCount, Is.True, "turning off MDI preserves the count preference");

                Command("MultiDrawIndirect");
                Assert.That(Pending(), Is.True);
                Assert.That(VpNativeDrawSetup.Request, Is.EqualTo(VpBodyRouteRequest.UnityApi));
                Assert.That(VpNativeDrawPlugin.VulkanIndirectCount, Is.False);
                Apply();
                Applied(VpBodyRouteRequest.NativePreferred, true, true);

                Command("DrawIndirectCount");
                Assert.That(settings.DrawIndirectCount, Is.False);
                Assert.That(PlayerPrefs.GetInt(Prefix + "drawIndirectCount"), Is.Zero);
                Assert.That(Pending(), Is.True);
                Assert.That(VpNativeDrawPlugin.VulkanIndirectCount, Is.True, "the requested count change also waits for apply");
                Apply();
                Applied(VpBodyRouteRequest.NativePreferred, true, false);

                void Apply() => typeof(AndroidFlatPlayer).GetMethod("ApplyRenderingSettings", Fields).Invoke(player, null);
                bool Pending() => (bool)typeof(AndroidFlatPlayer).GetProperty("RenderingChangesPending", Fields).GetValue(player);
                void Command(string name)
                {
                    MethodInfo command = typeof(AndroidFlatPlayer).GetMethod("Command", Fields);
                    Type commandType = command.GetParameters()[0].ParameterType;
                    command.Invoke(player, new[] { Enum.Parse(commandType, name) });
                }
                void Applied(VpBodyRouteRequest route, bool multi, bool count)
                {
                    Assert.That(VpNativeDrawSetup.Request, Is.EqualTo(route));
                    Assert.That(VpNativeDrawSetup.UnityMultiDrawIndirect, Is.EqualTo(multi));
                    Assert.That(VpNativeDrawPlugin.VulkanIndirectCount, Is.EqualTo(count));
                    Assert.That(Pending(), Is.False, "the applied configuration matches the effective requests");
                }
            }
            finally
            {
                VpNativeDrawSetup.Request = previousRoute;
                VpNativeDrawSetup.UnityMultiDrawIndirect = previousMulti;
                VpNativeDrawPlugin.VulkanIndirectCount = previousCount;
            }
        }

        [TestCase(900f, 1600f)]
        [TestCase(1600f, 900f)]
        public void RenderingCheckboxesAndApply_FitTheSafeAreaWithoutOverlappingOtherControls(float width, float height)
        {
            object view = NewView();
            var safe = new Rect(27f, 19f, width - 54f, height - 38f);
            Layout(view, new AndroidFlatSettings(), new AndroidFlatDrawCapabilities(true, true), safe, true);
            Rect panel = (Rect)Field(view, "panelRect");
            Inside(panel, safe, "settings panel in the safe area");
            var rectangles = new List<(string command, Rect rect)>();
            foreach (object control in (Array)Field(view, "controls"))
            {
                if (control == null) continue;
                Image image = (Image)Field(control, "image");
                string command = Field(control, "command").ToString();
                if (!image.gameObject.activeInHierarchy || command == "Menu") continue;
                Rect rect = (Rect)Field(control, "rect");
                Inside(rect, panel, command + " in the panel");
                Assert.That(rect.width, Is.GreaterThan(0f), command);
                Assert.That(rect.height, Is.GreaterThan(0f), command);
                foreach (var other in rectangles)
                {
                    bool overlap = Mathf.Min(rect.xMax, other.rect.xMax) - Mathf.Max(rect.xMin, other.rect.xMin) > .01f
                        && Mathf.Min(rect.yMax, other.rect.yMax) - Mathf.Max(rect.yMin, other.rect.yMin) > .01f;
                    Assert.That(overlap, Is.False, command + " overlaps " + other.command);
                }
                rectangles.Add((command, rect));
            }
            Assert.That(CommandAt(view, Centre(view, "MultiDrawIndirect")), Is.EqualTo("MultiDrawIndirect"));
            Assert.That(CommandAt(view, Centre(view, "DrawIndirectCount")), Is.EqualTo("DrawIndirectCount"));
            Assert.That(CommandAt(view, Centre(view, "ApplyRendering")), Is.EqualTo("ApplyRendering"));
        }

        private object NewView()
        {
            Type type = typeof(AndroidFlatSettings).Assembly.GetType("Zantetsu.AndroidFlat.AndroidFlatView", true);
            object view = Activator.CreateInstance(type, true);
            var parent = new GameObject("Android flat rendering settings under test");
            made.Add(parent);
            type.GetMethod("Build", Fields).Invoke(view, new object[] { parent.transform });
            // The view's runtime Destroy uses deferred destruction. EditMode owns these generated assets directly.
            made.Add((Object)Field(view, "circle"));
            made.Add((Object)Field(view, "circleTexture"));
            return view;
        }

        private static void Layout(object view, AndroidFlatSettings settings, AndroidFlatDrawCapabilities capabilities,
            Rect? safe = null, bool pending = false)
        {
            Rect area = safe ?? new Rect(0f, 0f, 900f, 1600f);
            view.GetType().GetMethod("Layout", Fields).Invoke(view,
                new object[] { area, new Vector2(100f, 200f), 60f, true, settings, area.height > area.width, capabilities, pending });
        }

        private static object Field(object owner, string name)
        {
            FieldInfo field = owner.GetType().GetField(name, Fields);
            Assert.That(field, Is.Not.Null, owner.GetType().Name + "." + name);
            return field.GetValue(owner);
        }

        private static object Control(object view, string command)
        {
            foreach (object control in (Array)Field(view, "controls"))
                if (control != null && Field(control, "command").ToString() == command) return control;
            Assert.Fail("no control for " + command);
            return null;
        }

        private static Vector2 Centre(object view, string command)
        {
            object control = Control(view, command);
            Assert.That(((Image)Field(control, "image")).gameObject.activeInHierarchy, Is.True, command + " is visible");
            return ((Rect)Field(control, "rect")).center;
        }

        private static void Checkbox(object view, string command, bool isChecked, bool interactable)
        {
            object control = Control(view, command);
            Assert.That(Field(control, "checkbox"), Is.Not.Null, command + " has a checkbox");
            Assert.That(((GameObject)Field(control, "checkmark")).activeSelf, Is.EqualTo(isChecked), command + " checkmark");
            Assert.That(Field(control, "interactable"), Is.EqualTo(interactable), command + " interactable");
        }

        private static string CommandAt(object view, Vector2 point)
            => view.GetType().GetMethod("CommandAt", Fields).Invoke(view, new object[] { point }).ToString();

        private static void Inside(Rect inner, Rect outer, string message)
        {
            Assert.That(inner.xMin, Is.GreaterThanOrEqualTo(outer.xMin - .1f), message);
            Assert.That(inner.xMax, Is.LessThanOrEqualTo(outer.xMax + .1f), message);
            Assert.That(inner.yMin, Is.GreaterThanOrEqualTo(outer.yMin - .1f), message);
            Assert.That(inner.yMax, Is.LessThanOrEqualTo(outer.yMax + .1f), message);
        }
    }
}
