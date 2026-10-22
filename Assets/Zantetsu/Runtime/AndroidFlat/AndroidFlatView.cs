using UnityEngine;
using UnityEngine.UI;

namespace Zantetsu.AndroidFlat
{
    internal enum FlatCommand
    {
        None, Menu, Resume, MoveDown, MoveUp, TurnDown, TurnUp, Fsr, Scale, Sharpness, Reset,
        StickPosition, Back, StickLeft, StickRight, StickDown, StickUp, StickReset,
        MultiDrawIndirect, DrawIndirectCount, ApplyRendering
    }
    internal sealed class AndroidFlatView
    {
        private sealed class Control
        {
            public FlatCommand command;
            public Image image;
            public Text label;
            public Rect rect;
            public bool interactable = true;
            public Image checkbox;
            public GameObject checkmark;
            public Image checkShort, checkLong;
        }
        private readonly Control[] controls = new Control[20];
        private GameObject root, panel;
        private Image stick, thumb;
        private Text status, title, stickOrientation, stickHorizontal, stickVertical;
        private Sprite circle;
        private Texture2D circleTexture;
        private Font font;
        private Rect panelRect;
        private bool menu, layoutPage;

        public void Build(Transform persistentParent)
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            root = new GameObject("Android touch controls", typeof(RectTransform), typeof(Canvas));
            root.transform.SetParent(persistentParent, false);
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            root.GetComponent<Canvas>().sortingOrder = 1000;
            circleTexture = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            var pixels = new Color32[128 * 128];
            for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
            {
                float d = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(64, 64));
                pixels[y * 128 + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(64f - d) * 255f));
            }
            circleTexture.SetPixels32(pixels); circleTexture.Apply();
            circle = Sprite.Create(circleTexture, new Rect(0, 0, 128, 128), new Vector2(.5f, .5f));
            stick = NewImage("Move and turn", root.transform, new Color(.85f, .94f, 1, .19f));
            stick.sprite = circle;
            thumb = NewImage("Stick thumb", root.transform, new Color(.85f, .94f, 1, .48f));
            thumb.sprite = circle;
            status = NewText("Status", root.transform, 22, TextAnchor.MiddleLeft);
            controls[0] = NewControl(FlatCommand.Menu, root.transform, "Menu");
            panel = NewImage("Pause panel", root.transform, new Color(.035f, .05f, .08f, .95f)).gameObject;
            title = NewText("Title", panel.transform, 28, TextAnchor.MiddleCenter);
            title.text = "Paused";
            var commands = new[] { FlatCommand.Resume, FlatCommand.MoveDown, FlatCommand.MoveUp, FlatCommand.TurnDown, FlatCommand.TurnUp,
                FlatCommand.Fsr, FlatCommand.Scale, FlatCommand.Sharpness, FlatCommand.Reset,
                FlatCommand.StickPosition, FlatCommand.Back, FlatCommand.StickLeft, FlatCommand.StickRight,
                FlatCommand.StickDown, FlatCommand.StickUp, FlatCommand.StickReset,
                FlatCommand.MultiDrawIndirect, FlatCommand.DrawIndirectCount, FlatCommand.ApplyRendering };
            for (int i = 0; i < commands.Length; i++)
                controls[i + 1] = commands[i] == FlatCommand.MultiDrawIndirect || commands[i] == FlatCommand.DrawIndirectCount
                    ? NewCheckbox(commands[i], panel.transform) : NewControl(commands[i], panel.transform, "");
            stickOrientation = NewText("Joystick orientation", panel.transform, 24, TextAnchor.MiddleCenter);
            stickHorizontal = NewText("Joystick horizontal position", panel.transform, 24, TextAnchor.MiddleCenter);
            stickVertical = NewText("Joystick vertical position", panel.transform, 24, TextAnchor.MiddleCenter);
            panel.SetActive(false);
        }

        private Control NewControl(FlatCommand command, Transform parent, string label)
        {
            Image image = NewImage(command.ToString(), parent, new Color(.13f, .19f, .25f, .96f));
            Text text = NewText("Label", image.transform, 24, TextAnchor.MiddleCenter);
            text.text = label;
            text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero; text.rectTransform.offsetMax = Vector2.zero;
            return new Control { command = command, image = image, label = text };
        }
        private Control NewCheckbox(FlatCommand command, Transform parent)
        {
            Control control = NewControl(command, parent, "");
            control.label.alignment = TextAnchor.MiddleLeft;
            control.checkbox = NewImage("Checkbox", control.image.transform, new Color(.4f, .5f, .6f));
            Image inset = NewImage("Inset", control.checkbox.transform, new Color(.06f, .09f, .12f));
            inset.rectTransform.anchorMin = Vector2.one * .1f;
            inset.rectTransform.anchorMax = Vector2.one * .9f;
            inset.rectTransform.offsetMin = inset.rectTransform.offsetMax = Vector2.zero;
            control.checkmark = new GameObject("Checkmark", typeof(RectTransform));
            var mark = (RectTransform)control.checkmark.transform;
            mark.SetParent(control.checkbox.transform, false);
            mark.anchorMin = Vector2.zero; mark.anchorMax = Vector2.one;
            mark.offsetMin = mark.offsetMax = Vector2.zero;
            control.checkShort = NewImage("Short stroke", mark, Color.white);
            control.checkLong = NewImage("Long stroke", mark, Color.white);
            return control;
        }

        private static void SetEnabled(Control control, bool enabled)
        {
            control.interactable = enabled;
            control.image.color = enabled ? new Color(.13f, .19f, .25f, .96f) : new Color(.075f, .09f, .105f, .96f);
            control.label.color = enabled ? new Color(.93f, .97f, 1f) : new Color(.43f, .46f, .49f);
            if (control.checkbox != null)
                control.checkbox.color = enabled ? new Color(.4f, .5f, .6f) : new Color(.23f, .26f, .29f);
        }

        private static void SetCheckbox(Control control, bool enabled, bool selected, string label)
        {
            SetEnabled(control, enabled);
            control.checkmark.SetActive(selected);
            control.label.text = label;
        }

        private static Image NewImage(string name, Transform parent, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>(); image.color = colour; image.raycastTarget = false;
            return image;
        }
        private Text NewText(string name, Transform parent, int size, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>(); text.font = font; text.fontSize = size;
            text.color = new Color(.93f, .97f, 1); text.alignment = alignment;
            text.raycastTarget = false; text.resizeTextForBestFit = true; text.resizeTextMinSize = 12; text.resizeTextMaxSize = size;
            return text;
        }
        private static void Place(RectTransform transform, Rect rect)
        {
            transform.anchorMin = transform.anchorMax = Vector2.zero; transform.pivot = new Vector2(.5f, .5f);
            transform.anchoredPosition = rect.center; transform.sizeDelta = rect.size;
        }
        private void Put(Control control, Rect screenRect)
        {
            control.rect = screenRect;
            Rect rect = screenRect;
            if (control.image.transform.parent == panel.transform) rect.position -= panelRect.position;
            Place(control.image.rectTransform, rect);
            if (control.checkbox != null)
            {
                float side = screenRect.height * .54f;
                Place(control.checkbox.rectTransform, new Rect(screenRect.height * .25f,
                    (screenRect.height - side) * .5f, side, side));
                control.label.rectTransform.offsetMin = new Vector2(screenRect.height * 1.08f, 0);
                Place(control.checkShort.rectTransform, new Rect(side * .125f, side * .37f, side * .35f, side * .12f));
                control.checkShort.rectTransform.localRotation = Quaternion.Euler(0, 0, -45);
                Place(control.checkLong.rectTransform, new Rect(side * .255f, side * .46f, side * .65f, side * .12f));
                control.checkLong.rectTransform.localRotation = Quaternion.Euler(0, 0, 45);
            }
        }

        public void ShowStickPosition(bool open) { layoutPage = open; }

        public void Layout(Rect safe, Vector2 centre, float radius, bool open, AndroidFlatSettings settings, bool portrait,
            AndroidFlatDrawCapabilities capabilities, bool renderingChangesPending)
        {
            menu = open;
            if (!open) layoutPage = false;
            float unit = Mathf.Min(safe.width, safe.height) / 900f;
            float pad = 24 * unit, row = 64 * unit;
            status.fontSize = status.resizeTextMaxSize = Mathf.RoundToInt(24 * unit);
            title.fontSize = title.resizeTextMaxSize = Mathf.RoundToInt(32 * unit);
            foreach (var control in controls)
                if (control != null) control.label.fontSize = control.label.resizeTextMaxSize = Mathf.RoundToInt(28 * unit);
            foreach (var text in new[] { stickOrientation, stickHorizontal, stickVertical })
            {
                text.fontSize = text.resizeTextMaxSize = Mathf.RoundToInt(28 * unit);
                text.gameObject.SetActive(layoutPage);
            }
            Place(stick.rectTransform, new Rect(centre - Vector2.one * radius, Vector2.one * radius * 2));
            Put(controls[0], new Rect(safe.xMax - pad - 150 * unit, safe.yMax - pad - row, 150 * unit, row));
            Place(status.rectTransform, new Rect(safe.x + pad, safe.yMax - pad - row, safe.width - 200 * unit - pad * 2, row));
            float panelHeight = row * (layoutPage ? 9f : 12.9f);
            panelRect = new Rect(safe.center.x - Mathf.Min(safe.width * .46f, 420 * unit), safe.center.y - panelHeight * .5f,
                Mathf.Min(safe.width * .92f, 840 * unit), panelHeight);
            Place((RectTransform)panel.transform, panelRect);
            Place(title.rectTransform, new Rect(0, panelRect.height - row, panelRect.width, row));
            title.text = layoutPage ? "Joystick position" : "Paused";
            for (int i = 1; i < controls.Length; i++)
            {
                bool visible = i == 1 || (layoutPage ? i >= 11 && i <= 16 : i <= 10 || i >= 17);
                controls[i].image.gameObject.SetActive(visible);
                if (!visible) controls[i].rect = default;
            }
            float left = panelRect.x + pad, width = panelRect.width - pad * 2, top = panelRect.yMax - row * 2;
            if (layoutPage)
            {
                Vector2 position = settings.GetStickPosition(portrait);
                stickOrientation.text = portrait ? "Portrait" : "Landscape";
                stickHorizontal.text = "From left: " + Mathf.RoundToInt(position.x * 100f) + "%";
                stickVertical.text = "From bottom: " + Mathf.RoundToInt(position.y * 100f) + "%";
                Place(stickOrientation.rectTransform, new Rect(pad, top - panelRect.y, width, row * .85f));
                top -= row;
                Place(stickHorizontal.rectTransform, new Rect(pad, top - panelRect.y, width, row * .85f));
                top -= row;
                Put(controls[12], new Rect(left, top, width * .48f, row * .85f)); controls[12].label.text = "Left -1%";
                Put(controls[13], new Rect(left + width * .52f, top, width * .48f, row * .85f)); controls[13].label.text = "Right +1%";
                top -= row;
                Place(stickVertical.rectTransform, new Rect(pad, top - panelRect.y, width, row * .85f));
                top -= row;
                Put(controls[14], new Rect(left, top, width * .48f, row * .85f)); controls[14].label.text = "Down -1%";
                Put(controls[15], new Rect(left + width * .52f, top, width * .48f, row * .85f)); controls[15].label.text = "Up +1%";
                top -= row;
                Put(controls[16], new Rect(left, top, width, row * .85f)); controls[16].label.text = "Reset " + (portrait ? "portrait" : "landscape") + " position";
                top -= row;
                Put(controls[11], new Rect(left, top, width * .48f, row * .85f)); controls[11].label.text = "Back";
                Put(controls[1], new Rect(left + width * .52f, top, width * .48f, row * .85f)); controls[1].label.text = "Resume";
            }
            else
            {
                Put(controls[1], new Rect(left, top, width, row * .85f)); controls[1].label.text = "Resume";
                top -= row;
                Put(controls[2], new Rect(left, top, width * .48f, row * .85f)); controls[2].label.text = "Move -   " + settings.MoveSpeed.ToString("F2");
                Put(controls[3], new Rect(left + width * .52f, top, width * .48f, row * .85f)); controls[3].label.text = "Move +";
                top -= row;
                Put(controls[4], new Rect(left, top, width * .48f, row * .85f)); controls[4].label.text = "Turn -   " + settings.TurnSpeed.ToString("F0");
                Put(controls[5], new Rect(left + width * .52f, top, width * .48f, row * .85f)); controls[5].label.text = "Turn +";
                top -= row;
                Put(controls[6], new Rect(left, top, width, row * .85f)); controls[6].label.text = "FSR 1: " + (settings.Fsr ? "On" : "Off");
                top -= row;
                Put(controls[7], new Rect(left, top, width, row * .85f)); controls[7].label.text = "3D resolution: " + (settings.Fsr ? settings.RenderScale : 1f).ToString("P0");
                top -= row;
                Put(controls[8], new Rect(left, top, width, row * .85f)); controls[8].label.text = "Sharpness: " + settings.Sharpness.ToString("F2");
                top -= row;
                bool multi = capabilities.MultiDrawIndirect && settings.MultiDrawIndirect;
                Put(controls[17], new Rect(left, top, width, row * .85f));
                SetCheckbox(controls[17], capabilities.MultiDrawIndirect, multi,
                    capabilities.MultiDrawIndirect ? "MultiDrawIndirect" : "MultiDrawIndirect (unavailable)");
                top -= row;
                Put(controls[18], new Rect(left, top, width, row * .85f));
                SetCheckbox(controls[18], capabilities.DrawIndirectCount && multi,
                    capabilities.DrawIndirectCount && multi && settings.DrawIndirectCount,
                    !capabilities.DrawIndirectCount ? "DrawIndirectCount (unavailable)"
                    : !multi ? "DrawIndirectCount (MDI off)" : "DrawIndirectCount");
                top -= row;
                Put(controls[19], new Rect(left, top, width, row * .85f));
                controls[19].label.text = "Apply rendering (reset scene)";
                SetEnabled(controls[19], renderingChangesPending);
                top -= row;
                Put(controls[10], new Rect(left, top, width, row * .85f)); controls[10].label.text = "Joystick position";
                top -= row;
                Put(controls[9], new Rect(left, top, width, row * .85f)); controls[9].label.text = "Reset scene";
            }
            panel.SetActive(open);
            stick.enabled = thumb.enabled = !open;
        }
        public void SetStick(Vector2 centre, Vector2 value, float radius)
        {
            float knob = radius * .42f;
            Place(thumb.rectTransform, new Rect(centre + value * radius * .62f - Vector2.one * knob, Vector2.one * knob * 2));
        }
        public void SetStatus(string value) { status.text = value; }
        public bool IsUi(Vector2 point) => (menu && panelRect.Contains(point)) || controls[0].rect.Contains(point);
        public FlatCommand CommandAt(Vector2 point)
        {
            if (controls[0].rect.Contains(point)) return FlatCommand.Menu;
            if (!menu) return FlatCommand.None;
            for (int i = 1; i < controls.Length; i++)
                if (controls[i] != null && controls[i].interactable && controls[i].image.gameObject.activeInHierarchy
                    && controls[i].rect.Contains(point)) return controls[i].command;
            return FlatCommand.None;
        }
        public void Destroy()
        {
            if (root != null) Object.Destroy(root);
            if (circle != null) Object.Destroy(circle);
            if (circleTexture != null) Object.Destroy(circleTexture);
        }
    }
}
