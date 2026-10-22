using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Zantetsu.Sandbox;
using Object = UnityEngine.Object;

namespace Zantetsu.AndroidFlat.Tests
{
    public sealed class AndroidFlatLoadingTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<object> views = new List<object>();
        private readonly List<Object> made = new List<Object>();
        private readonly List<PlayLoadingView> loadingViews = new List<PlayLoadingView>();
        private static readonly Rect Portrait = new Rect(24f, 48f, 852f, 1480f);

        [TearDown]
        public void TearDown()
        {
            foreach (object view in views) ((PlayLoadingView)Field(view, "loadingView")).Destroy();
            foreach (PlayLoadingView view in loadingViews) view.Destroy();
            foreach (Object value in made) if (value != null) Object.DestroyImmediate(value);
            views.Clear(); loadingViews.Clear(); made.Clear();
        }

        [Test]
        public void LoadingBeforeTheFirstLayout_ShowsTheSharedQuestWindowAndHidesPlayControls()
        {
            object view = NewView();
            PlayLoadingView loading = Loading(view);
            Assert.That(loading.Visible, Is.False, "Build alone does not display loading");
            SetLoading(view, true, Portrait, 0f);
            Assert.That(loading.Visible, Is.True);
            Canvas canvas = loading.Root.GetComponent<Canvas>();
            Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            Assert.That(canvas.sortingOrder, Is.GreaterThan(((GameObject)Field(view, "root")).GetComponent<Canvas>().sortingOrder));
            Assert.That(loading.DrawnInFront, Is.True);
            Assert.That(loading.Window.rect.width, Is.EqualTo(PlayLoadingView.Width).Within(.01f));
            Assert.That(loading.Window.rect.height, Is.EqualTo(PlayLoadingView.Height).Within(.01f));
            AssertCentredAndInside(loading.Window, Portrait);

            var quest = new PlayLoadingView();
            loadingViews.Add(quest);
            quest.Build();
            Assert.That(loading.Window.Find("Panel").GetComponent<Image>().color,
                Is.EqualTo(quest.Window.Find("Panel").GetComponent<Image>().color), "the shared dark window");
            Assert.That(loading.Window.Find("Text").GetComponent<Text>().text,
                Is.EqualTo(quest.Window.Find("Text").GetComponent<Text>().text));
            int dots = 0;
            foreach (Image image in loading.Root.GetComponentsInChildren<Image>(true))
            {
                if (!image.name.StartsWith("Dot ", StringComparison.Ordinal)) continue;
                dots++;
                Assert.That(image.color.b, Is.GreaterThan(image.color.r), "the shared blue ring");
            }
            Assert.That(dots, Is.EqualTo(PlayLoadingView.Dots));
            AssertPlayControlsHidden(view);
        }

        [Test]
        public void LoadingRing_UsesTheSuppliedUnscaledClockWhileGameplayIsPaused()
        {
            float previousScale = Time.timeScale;
            try
            {
                Time.timeScale = 0f;
                object view = NewView();
                SetLoading(view, true, Portrait, 0f);
                Assert.That(Loading(view).DotAlpha(0), Is.EqualTo(1f).Within(.0001f));
                SetLoading(view, true, Portrait, 1f / (PlayLoadingView.Dots * PlayLoadingView.RevolutionsPerSecond));
                Assert.That(Loading(view).DotAlpha(1), Is.EqualTo(1f).Within(.0001f), "the bright dot advances despite timeScale zero");
                Assert.That(Loading(view).DotAlpha(0), Is.LessThan(1f));
            }
            finally { Time.timeScale = previousScale; }
        }

        [TestCase(900f, 1600f)]
        [TestCase(1600f, 900f)]
        public void SafeAreaChanges_KeepTheScaledWindowCentredAndFullyVisible(float width, float height)
        {
            object view = NewView();
            var first = new Rect(31f, 47f, width - 77f, height - 91f);
            var rotated = new Rect(53f, 29f, height - 101f, width - 83f);
            SetLoading(view, true, first, 0f);
            Rect firstWindow = Pixels(Loading(view).Window);
            AssertCentredAndInside(Loading(view).Window, first);
            SetLoading(view, true, rotated, .25f);
            AssertCentredAndInside(Loading(view).Window, rotated);
            Assert.That(Vector2.Distance(Pixels(Loading(view).Window).center, firstWindow.center), Is.GreaterThan(1f),
                "rotation updates the position rather than keeping the old safe-area centre");
        }

        [Test]
        public void LoadingConsumesEveryTouch_AndALayoutCannotExposePauseOrPlayControls()
        {
            object view = NewView();
            Layout(view, Portrait, true);
            Vector2 resume = ControlCentre(view, "Resume");
            Vector2 menu = ControlCentre(view, "Menu");
            Assert.That(CommandAt(view, resume), Is.EqualTo("Resume"));
            SetLoading(view, true, Portrait, 0f);
            Layout(view, Portrait, true);
            AssertPlayControlsHidden(view);
            foreach (Vector2 point in new[] { resume, menu, Portrait.center, new Vector2(-100f, -100f), new Vector2(9999f, 9999f) })
            {
                Assert.That((bool)Invoke(view, "IsUi", point), Is.True, "loading consumes every touch");
                Assert.That(CommandAt(view, point), Is.EqualTo("None"));
            }
        }

        [Test]
        public void HidingLoading_RestoresMenuAndPlayUi_AndTheSameOverlayCanShowAgain()
        {
            object view = NewView();
            SetLoading(view, true, Portrait, 0f);
            Transform overlay = Loading(view).Root;
            SetLoading(view, false, Portrait, .2f);
            Assert.That(Loading(view).Visible, Is.False);
            Layout(view, Portrait, true);
            Assert.That(((GameObject)Field(view, "panel")).activeInHierarchy, Is.True);
            Assert.That(CommandAt(view, ControlCentre(view, "Resume")), Is.EqualTo("Resume"));
            Layout(view, Portrait, false);
            Assert.That(CommandAt(view, ControlCentre(view, "Menu")), Is.EqualTo("Menu"));
            Assert.That(((Image)Field(view, "stick")).isActiveAndEnabled, Is.True);
            Assert.That(((Image)Field(view, "thumb")).isActiveAndEnabled, Is.True);
            Assert.That(((Text)Field(view, "status")).isActiveAndEnabled, Is.True);
            Assert.That((bool)Invoke(view, "IsUi", Portrait.center), Is.False, "play area is available again");

            SetLoading(view, true, Portrait, .5f);
            Assert.That(Loading(view).Visible, Is.True);
            Assert.That(Loading(view).Root, Is.SameAs(overlay), "a later scene preparation reuses the shared overlay");
            AssertPlayControlsHidden(view);
            Assert.That(CommandAt(view, ControlCentre(view, "Menu")), Is.EqualTo("None"));
        }

        [Test]
        public void PlayerWithoutACamera_StillUpdatesLoading_AndStopsItForFailureOrReadiness()
        {
            var host = new GameObject("Android player loading boundary under test");
            made.Add(host);
            AndroidFlatPlayer player = host.AddComponent<AndroidFlatPlayer>();
            object view = Field(player, "view");
            try
            {
                Invoke(view, "Build", host.transform);
                Assert.That(Field(player, "cameraView"), Is.Null, "no scene or camera is installed for this test");
                Invoke(player, "Update");
                Assert.That(Loading(view).Visible, Is.True, "loading must update before Update returns for a missing camera");

                typeof(AndroidFlatPlayer).GetField("failure", Members).SetValue(player, "preparation failed under test");
                Invoke(player, "Update");
                Assert.That(Loading(view).Visible, Is.False, "a failure must not leave a loading window spinning indefinitely");

                typeof(AndroidFlatPlayer).GetField("failure", Members).SetValue(player, null);
                typeof(AndroidFlatPlayer).GetField("ready", Members).SetValue(player, true);
                Invoke(player, "Update");
                Assert.That(Loading(view).Visible, Is.False, "a ready player has no loading window");

                typeof(AndroidFlatPlayer).GetField("ready", Members).SetValue(player, false);
                Invoke(player, "Update");
                Assert.That(Loading(view).Visible, Is.True, "a later preparation can display loading without a camera");
            }
            finally
            {
                Loading(view).Destroy();
                // The player owns the view at runtime. Release its generated objects immediately in EditMode,
                // then clear those references so the player's OnDestroy cannot defer their destruction.
                foreach (string name in new[] { "root", "circle", "circleTexture" })
                {
                    var value = (Object)Field(view, name);
                    if (value != null) Object.DestroyImmediate(value);
                    view.GetType().GetField(name, Members).SetValue(view, null);
                }
                Object.DestroyImmediate(host);
            }
        }

        private object NewView()
        {
            Type type = typeof(AndroidFlatSettings).Assembly.GetType("Zantetsu.AndroidFlat.AndroidFlatView", true);
            object view = Activator.CreateInstance(type, true);
            var parent = new GameObject("Android loading view under test");
            made.Add(parent); views.Add(view);
            Invoke(view, "Build", parent.transform);
            made.Add((Object)Field(view, "circle"));
            made.Add((Object)Field(view, "circleTexture"));
            return view;
        }

        private static void SetLoading(object view, bool loading, Rect safe, float seconds)
            => Invoke(view, "SetLoading", loading, safe, seconds);

        private static void Layout(object view, Rect safe, bool menu)
            => Invoke(view, "Layout", safe, new Vector2(130f, 250f), 70f, menu,
                new AndroidFlatSettings(), safe.height > safe.width, new AndroidFlatDrawCapabilities(true, true), false);

        private static PlayLoadingView Loading(object view) => (PlayLoadingView)Field(view, "loadingView");

        private static object Field(object owner, string name)
        {
            FieldInfo field = owner.GetType().GetField(name, Members);
            Assert.That(field, Is.Not.Null, owner.GetType().Name + "." + name);
            return field.GetValue(owner);
        }

        private static object Invoke(object owner, string name, params object[] arguments)
        {
            MethodInfo method = owner.GetType().GetMethod(name, Members);
            Assert.That(method, Is.Not.Null, owner.GetType().Name + "." + name);
            return method.Invoke(owner, arguments);
        }

        private static object Control(object view, string command)
        {
            foreach (object control in (Array)Field(view, "controls"))
                if (control != null && Field(control, "command").ToString() == command) return control;
            Assert.Fail("no control for " + command);
            return null;
        }

        private static Vector2 ControlCentre(object view, string command) => ((Rect)Field(Control(view, command), "rect")).center;
        private static string CommandAt(object view, Vector2 point) => Invoke(view, "CommandAt", point).ToString();

        private static void AssertPlayControlsHidden(object view)
        {
            Assert.That(((GameObject)Field(view, "panel")).activeInHierarchy, Is.False, "the pause panel is hidden");
            Assert.That(((Image)Field(view, "stick")).isActiveAndEnabled, Is.False);
            Assert.That(((Image)Field(view, "thumb")).isActiveAndEnabled, Is.False);
            Assert.That(((Text)Field(view, "status")).isActiveAndEnabled, Is.False);
            Assert.That(((Image)Field(Control(view, "Menu"), "image")).isActiveAndEnabled, Is.False);
        }

        private static Rect Pixels(RectTransform window)
        {
            Vector2 dimensions = Vector2.Scale(window.rect.size, new Vector2(Mathf.Abs(window.localScale.x), Mathf.Abs(window.localScale.y)));
            return new Rect(window.anchoredPosition - dimensions * .5f, dimensions);
        }

        private static void AssertCentredAndInside(RectTransform window, Rect safe)
        {
            Rect pixels = Pixels(window);
            Assert.That(Vector2.Distance(pixels.center, safe.center), Is.LessThan(.01f));
            Assert.That(pixels.width, Is.GreaterThan(0f));
            Assert.That(pixels.height, Is.GreaterThan(0f));
            Assert.That(pixels.xMin, Is.GreaterThanOrEqualTo(safe.xMin - .01f));
            Assert.That(pixels.xMax, Is.LessThanOrEqualTo(safe.xMax + .01f));
            Assert.That(pixels.yMin, Is.GreaterThanOrEqualTo(safe.yMin - .01f));
            Assert.That(pixels.yMax, Is.LessThanOrEqualTo(safe.yMax + .01f));
            Assert.That(pixels.width / pixels.height, Is.EqualTo(PlayLoadingView.Width / PlayLoadingView.Height).Within(.001f));
        }
    }
}
