using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using Zantetsu.Core;
using Zantetsu.Core.Statistics;
using Zantetsu.Sandbox;
using Object = UnityEngine.Object;

namespace Zantetsu.AndroidFlat.Tests
{
    public sealed class AndroidFlatStatisticsTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<GameObject> hosts = new List<GameObject>();
        private readonly List<object> views = new List<object>();
        private readonly List<Object> assets = new List<Object>();
        private readonly Dictionary<string, object> pauseState = new Dictionary<string, object>();
        private readonly List<(string key, bool existed, bool integer, int intValue, float floatValue)> preferences
            = new List<(string, bool, bool, int, float)>();
        private float timeScale;

        [SetUp]
        public void SetUp()
        {
            timeScale = Time.timeScale;
            foreach (string name in new[] { "s_state", "s_timeScaleHeld", "s_timeScaleBefore" })
                pauseState[name] = typeof(GamePause).GetField(name, Statics).GetValue(null);
            typeof(GamePause).GetMethod("ResetForTest", Statics).Invoke(null, null);
            foreach (string key in new[] { "move", "turn", "scale", "sharpness", "stick.portrait.x", "stick.portrait.y", "stick.landscape.x", "stick.landscape.y" }) Remember(key, false);
            foreach (string key in new[] { "fsr", "multiDrawIndirect", "drawIndirectCount" }) Remember(key, true);
        }

        private void Remember(string key, bool integer)
        {
            key = "zantetsu.androidflat." + key;
            preferences.Add((key, PlayerPrefs.HasKey(key), integer, integer ? PlayerPrefs.GetInt(key) : 0, integer ? 0f : PlayerPrefs.GetFloat(key)));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (object view in views)
            {
                ((PlayLoadingView)Field(view, "loadingView")).Destroy();
                foreach (string name in new[] { "root", "circle", "circleTexture" })
                {
                    var value = (Object)Field(view, name);
                    if (value != null) Object.DestroyImmediate(value);
                    Set(view, name, null);
                }
            }
            foreach (GameObject host in hosts)
            {
                if (host == null) continue;
                AndroidFlatPlayer player = host.GetComponent<AndroidFlatPlayer>();
                if (player != null) Set(player, "pipeline", null);
                Object.DestroyImmediate(host);
            }
            foreach (Object asset in assets) if (asset != null) Object.DestroyImmediate(asset);
            views.Clear(); hosts.Clear(); assets.Clear();
            typeof(GamePause).GetMethod("ResetForTest", Statics).Invoke(null, null);
            foreach (var saved in pauseState) typeof(GamePause).GetField(saved.Key, Statics).SetValue(null, saved.Value);
            pauseState.Clear();
            Time.timeScale = timeScale;
            foreach (var value in preferences)
            {
                if (!value.existed) PlayerPrefs.DeleteKey(value.key);
                else if (value.integer) PlayerPrefs.SetInt(value.key, value.intValue);
                else PlayerPrefs.SetFloat(value.key, value.floatValue);
            }
            PlayerPrefs.Save(); preferences.Clear();
        }

        [Test]
        public void AnUndecidedConsentDialog_BlocksGameplayAndOtherMenuCommandsUntilAnAnswer()
        {
            AndroidFlatPlayer player = NewPlayer(out PlayStatisticsService service, out _, out object view);
            Invoke(player, "OpenStatisticsConsent", false);
            Layout(view, new Rect(0, 0, 900, 1600));
            Assert.That(service.Consent.Decision, Is.EqualTo(PlayStatisticsDecision.Undecided));
            Assert.That(service.Collector.Agreed, Is.False);
            Assert.That(service.Statistics.Platform, Is.Empty, "the machine is not read before consent");
            Assert.That(GamePause.Manual, Is.True);
            Assert.That(Field(player, "consentOpen"), Is.True);
            foreach (Vector2 point in new[] { new Vector2(-100, -100), new Vector2(9999, 9999), new Vector2(450, 800) })
                Assert.That((bool)Invoke(view, "IsUi", point), Is.True, "the dialog consumes the whole touch screen");
            Assert.That(((Image)Field(Control(view, "Menu"), "image")).gameObject.activeInHierarchy, Is.False);
            foreach (object control in (Array)Field(view, "controls"))
            {
                if (control == null || !((Image)Field(control, "image")).gameObject.activeInHierarchy) continue;
                string command = Field(control, "command").ToString();
                Assert.That(command, Is.EqualTo("StatisticsAgree").Or.EqualTo("StatisticsDecline"), "only explicit answers are offered");
            }
            Command(player, "Menu");
            Command(player, "Resume");
            Assert.That(Field(player, "consentOpen"), Is.True, "menu/back cannot bypass the first answer");
            Assert.That(service.Consent.Decision, Is.EqualTo(PlayStatisticsDecision.Undecided));
            Assert.That(CommandAt(view, Centre(view, "StatisticsAgree")), Is.EqualTo("StatisticsAgree"));
            Assert.That(CommandAt(view, Centre(view, "StatisticsDecline")), Is.EqualTo("StatisticsDecline"));
        }

        [TestCase(900f, 1600f)]
        [TestCase(1600f, 900f)]
        public void ConsentAndStatisticsSettings_FitTheSafeAreaWithTheFullPolicyReadable(float width, float height)
        {
            AndroidFlatPlayer player = NewPlayer(out _, out _, out object view);
            var safe = new Rect(23f, 31f, width - 52f, height - 69f);
            Invoke(player, "OpenStatisticsConsent", false);
            Layout(view, safe);
            AssertVisibleControlsFit(view, safe);
            Text body = (Text)Field(view, "statisticsBody");
            Assert.That(body.gameObject.activeInHierarchy, Is.True);
            Assert.That(body.text, Is.EqualTo(PlayStatisticsService.ConsentBodyForAndroid));
            Assert.That(body.horizontalOverflow, Is.EqualTo(HorizontalWrapMode.Wrap));
            Assert.That(body.preferredHeight, Is.LessThanOrEqualTo(body.rectTransform.rect.height + .5f), "the consent's full text fits without clipping");
            Invoke(player, "CloseStatisticsConsent", false);
            Invoke(player, "ShowStatisticsSettings");
            Layout(view, safe);
            AssertVisibleControlsFit(view, safe);
            Assert.That(CommandAt(view, Centre(view, "StatisticsToggle")), Is.EqualTo("StatisticsToggle"));
            Assert.That(CommandAt(view, Centre(view, "Back")), Is.EqualTo("Back"));
        }

        [TestCase(900f, 1600f)]
        [TestCase(1600f, 900f)]
        public void BundledPrivacyPages_AreReadableOfflineAndDoNotChangeConsent(float width, float height)
        {
            AndroidFlatPlayer player = NewPlayer(out PlayStatisticsService service, out _, out object view);
            var safe = new Rect(23, 31, width - 52, height - 69);
            Set(player, "menu", true);
            Invoke(player, "ShowStatisticsSettings");
            Layout(view, safe);
            Assert.That(CommandAt(view, Centre(view, "Privacy")), Is.EqualTo("Privacy"));
            Command(player, "Privacy");
            for (int i = 0; i < AndroidFlatPrivacy.Pages.Length; i++)
            {
                Layout(view, safe);
                AssertVisibleControlsFit(view, safe);
                Text body = (Text)Field(view, "statisticsBody");
                Assert.That(body.text, Is.EqualTo(AndroidFlatPrivacy.Pages[i]));
                Assert.That(body.preferredHeight, Is.LessThanOrEqualTo(body.rectTransform.rect.height + .5f));
                Assert.That(service.Consent.Decision, Is.EqualTo(PlayStatisticsDecision.Undecided));
                Command(player, "PrivacyNext");
            }
            Layout(view, safe);
            Assert.That(CommandAt(view, Centre(view, "PrivacyNext")), Is.EqualTo("None"));
            Command(player, "Statistics");
            Layout(view, safe);
            Assert.That(CommandAt(view, Centre(view, "Privacy")), Is.EqualTo("Privacy"));
            Assert.That(service.Consent.Decision, Is.EqualTo(PlayStatisticsDecision.Undecided));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void AnsweringConsent_UpdatesTheServiceAndReturnsToTheOriginatingPlayOrMenu(bool fromMenu, bool agree)
        {
            AndroidFlatPlayer player = NewPlayer(out PlayStatisticsService service, out MemoryPlayStatisticsStore store, out object view);
            Set(player, "menu", fromMenu);
            GamePause.SetManual(fromMenu);
            Invoke(player, "OpenStatisticsConsent", fromMenu);
            Command(player, agree ? "StatisticsAgree" : "StatisticsDecline");
            Assert.That(Field(player, "consentOpen"), Is.False);
            Assert.That(Field(player, "menu"), Is.EqualTo(fromMenu));
            Assert.That(GamePause.Manual, Is.EqualTo(fromMenu), "initial consent resumes play; menu consent stays paused");
            Assert.That(service.Consent.Agreed, Is.EqualTo(agree));
            Assert.That(service.Collector.Agreed, Is.EqualTo(agree));
            Assert.That(store.TryGet(PlayStatisticsConsent.DecisionKey, out string decision), Is.True);
            Assert.That(decision, Is.EqualTo(agree ? "agreed" : "declined"));
            Assert.That(store.TryGet(PlayStatisticsConsent.InstallIdKey, out _), Is.EqualTo(agree));
            Layout(view, new Rect(0f, 0f, 900f, 1600f), fromMenu);
            Assert.That(CommandAt(view, Centre(view, fromMenu ? "StatisticsToggle" : "Menu")),
                Is.EqualTo(fromMenu ? "StatisticsToggle" : "Menu"));
        }

        [Test]
        public void WithdrawingFromStatisticsSettings_ClearsCollectedPlayAndStopsCollectionUntilAgreedAgain()
        {
            AndroidFlatPlayer player = NewPlayer(out PlayStatisticsService service, out MemoryPlayStatisticsStore store, out object view);
            Invoke(player, "OpenStatisticsConsent", false);
            Command(player, "StatisticsAgree");
            CountFrames(service);
            Assert.That(service.Statistics.HasPlay, Is.True, "real collector frames were accumulated after agreement");
            Assert.That(service.Statistics.Platform, Is.Not.Empty);
            Set(player, "menu", true);
            GamePause.SetManual(true);
            Invoke(player, "ShowStatisticsSettings");
            Layout(view, new Rect(0, 0, 900, 1600));
            Command(player, "StatisticsToggle");
            Assert.That(service.Consent.Decision, Is.EqualTo(PlayStatisticsDecision.Declined));
            Assert.That(service.Collector.Agreed, Is.False);
            Assert.That(service.Statistics.HasPlay, Is.False);
            Assert.That(service.Statistics.Platform, Is.Empty);
            Assert.That(store.TryGet(PlayStatisticsConsent.InstallIdKey, out _), Is.False);
            Assert.That(service.Sender.Offer().Kind, Is.EqualTo(PlayStatisticsOfferKind.NotAgreed));
            CountFrames(service);
            Assert.That(service.Statistics.HasPlay, Is.False, "frames after withdrawal are discarded");
            Command(player, "StatisticsToggle");
            if ((bool)Field(player, "consentOpen")) Command(player, "StatisticsAgree");
            Assert.That(service.Consent.Agreed && service.Collector.Agreed, Is.True);
            CountFrames(service);
            Assert.That(service.Statistics.HasPlay, Is.True, "agreeing again starts a fresh collection");
        }

        [TestCase(UpscalingFilterSelection.FSR, .67f, true)]
        [TestCase(UpscalingFilterSelection.FSR, 1f, false)]
        [TestCase(UpscalingFilterSelection.Linear, .67f, false)]
        public void TheProvider_ReportsTheRuntimePipelineAndAppliedDrawFlags_InsteadOfPendingPreferences(
            UpscalingFilterSelection filter, float scale, bool fsrActive)
        {
            AndroidFlatPlayer player = NewPlayer(out _, out _, out _);
            var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            assets.Add(pipeline);
            pipeline.renderScale = scale; pipeline.upscalingFilter = filter; pipeline.msaaSampleCount = 4;
            Set(player, "pipeline", pipeline);
            var settings = (AndroidFlatSettings)Field(player, "settings");
            settings.Fsr = !fsrActive; settings.RenderScale = .9f;
            settings.MultiDrawIndirect = false; settings.DrawIndirectCount = true;
            Set(player, "appliedMultiDrawIndirect", true);
            Set(player, "appliedDrawIndirectCount", true);
            var provider = (IPlayStatisticsFlatSource)player;
            var conditions = new PlayConditions();
            provider.ReadStatisticsConditions(ref conditions);
            Assert.That(conditions.ScreenWidth, Is.EqualTo(Screen.width));
            Assert.That(conditions.ScreenHeight, Is.EqualTo(Screen.height));
            Assert.That(conditions.RenderScale, Is.EqualTo(scale).Within(.001f));
            Assert.That(conditions.MsaaSamples, Is.EqualTo(4));
            Assert.That(conditions.FsrEnabled, Is.EqualTo(fsrActive));
            Assert.That(conditions.MultiDrawIndirect, Is.True, "the pending preference is off but this world still has MDI applied");
            Assert.That(conditions.DrawIndirectCount, Is.False, "a requested/applied count option alone is not an active native compaction route");
        }

        [Test]
        public void TheProvider_ExcludesPreparationAndSceneResetFromReadyPlay()
        {
            AndroidFlatPlayer player = NewPlayer(out _, out _, out _);
            var provider = (IPlayStatisticsFlatSource)player;
            Assert.That(provider.StatisticsReady, Is.False);
            Set(player, "ready", true);
            Assert.That(provider.StatisticsReady, Is.True);
            Set(player, "sceneChanging", true);
            Assert.That(provider.StatisticsReady, Is.False);
            Set(player, "sceneChanging", false);
            Set(player, "ready", false);
            Assert.That(provider.StatisticsReady, Is.False);
        }

        private AndroidFlatPlayer NewPlayer(out PlayStatisticsService service, out MemoryPlayStatisticsStore store, out object view)
        {
            var host = new GameObject("Android statistics settings under test");
            hosts.Add(host);
            AndroidFlatPlayer player = host.AddComponent<AndroidFlatPlayer>();
            view = Field(player, "view"); views.Add(view);
            Invoke(view, "Build", host.transform);
            service = host.AddComponent<PlayStatisticsService>();
            store = new MemoryPlayStatisticsStore();
            Invoke(service, "Setup", SandboxScenarioKind.Play, "AndroidStatisticsTest", store, null);
            service.FlatSource = player;
            Set(player, "statistics", service);
            return player;
        }

        private static void CountFrames(PlayStatisticsService service)
        {
            var conditions = new PlayConditions { Scene = "AndroidStatisticsTest", Mode = "play", DrawRoute = "VP3C", XrRuntime = PlayEnvironment.NoXr };
            for (int i = 0; i < 7; i++) service.Collector.Frame(true, false, true, in conditions, 16.67f,
                new FrameTimeSample { HasMain = true, MainMs = 8f }, 1);
        }

        private static object Field(object owner, string name)
        {
            FieldInfo field = owner.GetType().GetField(name, Members);
            Assert.That(field, Is.Not.Null, owner.GetType().Name + "." + name);
            return field.GetValue(owner);
        }
        private static void Set(object owner, string name, object value)
            => owner.GetType().GetField(name, Members).SetValue(owner, value);
        private static object Invoke(object owner, string name, params object[] arguments)
            => owner.GetType().GetMethod(name, Members).Invoke(owner, arguments);
        private static object Control(object view, string command)
        {
            foreach (object control in (Array)Field(view, "controls"))
                if (control != null && Field(control, "command").ToString() == command) return control;
            Assert.Fail("no control for " + command); return null;
        }
        private static Vector2 Centre(object view, string command) => ((Rect)Field(Control(view, command), "rect")).center;
        private static string CommandAt(object view, Vector2 point) => Invoke(view, "CommandAt", point).ToString();
        private static void Command(AndroidFlatPlayer player, string command)
        {
            MethodInfo method = player.GetType().GetMethod("Command", Members);
            method.Invoke(player, new[] { Enum.Parse(method.GetParameters()[0].ParameterType, command) });
        }
        private static void Layout(object view, Rect safe, bool open = true)
            => Invoke(view, "Layout", safe, new Vector2(100, 200), 60f, open, new AndroidFlatSettings(),
                safe.height > safe.width, new AndroidFlatDrawCapabilities(true, true), false);
        private static void AssertVisibleControlsFit(object view, Rect safe)
        {
            Rect panel = (Rect)Field(view, "panelRect");
            Inside(panel, safe, "panel in safe area");
            var controls = new List<(string name, Rect rect)>();
            foreach (object control in (Array)Field(view, "controls"))
            {
                if (control == null || !((Image)Field(control, "image")).gameObject.activeInHierarchy) continue;
                string command = Field(control, "command").ToString();
                if (command == "Menu") continue;
                Rect rect = (Rect)Field(control, "rect");
                Inside(rect, panel, command);
                foreach (var other in controls) Assert.That(rect.Overlaps(other.rect), Is.False, command + " overlaps " + other.name);
                controls.Add((command, rect));
            }
        }
        private static void Inside(Rect inner, Rect outer, string message)
        {
            Assert.That(inner.xMin, Is.GreaterThanOrEqualTo(outer.xMin - .1f), message);
            Assert.That(inner.xMax, Is.LessThanOrEqualTo(outer.xMax + .1f), message);
            Assert.That(inner.yMin, Is.GreaterThanOrEqualTo(outer.yMin - .1f), message);
            Assert.That(inner.yMax, Is.LessThanOrEqualTo(outer.yMax + .1f), message);
        }
    }
}
