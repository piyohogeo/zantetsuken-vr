using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The settings menu's picture and its pointer (DESIGN 4.6): one World Space Canvas of uGUI made from code -- a
    /// panel, a resume button, setting sliders and an ON/OFF button -- about 1.3 m in front of where the head looked when it was opened,
    /// standing in the world from then on so that the head moves freely before it.
    /// <para>
    /// **It is drawn by Unity, in the camera's own render.** Nothing of the VP display or of the native draw route is
    /// asked for it: a world space canvas is drawn by the camera, for each eye the camera has. Its own material
    /// (<see cref="ShaderResource"/>) draws it after everything else with no depth test, so a wall nearer than the
    /// menu does not hide what the pointer can still press. Where a build lacks that shader the canvas falls back to
    /// uGUI's default material, which is depth tested; the menu says which it is drawing with.
    /// </para>
    /// <para>
    /// **The pointer is a ray, not an event system.** The ray the input gives is met with the canvas's plane and the
    /// point is looked up in the controls' own rectangles: a press on the button asks to resume, a press on a slider
    /// takes it until the select is let go. No EventSystem and no input module is needed or made.
    /// </para>
    /// </summary>
    public sealed class PlayMenuView
    {
        /// <summary>How far in front of the head the menu is put when it opens, metres (provisional).</summary>
        public const float Distance = 1.3f;

        /// <summary>World metres a canvas unit: an 800 x 620 canvas is 0.8 m wide.</summary>
        public const float UnitMetres = 0.001f;

        /// <summary>The menu's shader, in a Resources folder so that every build has it: drawn in front of everything.</summary>
        public const string ShaderResource = "ZantetsuPlayMenuUi";

        private Material _material;

        /// <summary>Whether the menu is drawn in front of everything (its own material) rather than depth tested (uGUI's default).</summary>
        public bool DrawnInFront => _material != null;

        private const float Width = 800f;
        private const float Height = 620f;

        /// <summary>The height of a setting's row slot.</summary>
        private const float RowStep = 96f;

        /// <summary>An explanation row's font size: the consent dialog's body size (<see cref="PlayDialogView.BodyFontSize"/>).</summary>
        internal const int ExplanationFontSize = PlayDialogView.BodyFontSize;

        internal static readonly Color PanelColour = new Color(0.06f, 0.07f, 0.09f, 0.92f);
        internal static readonly Color ControlColour = new Color(0.20f, 0.23f, 0.28f, 1f);
        internal static readonly Color HoverColour = new Color(0.32f, 0.40f, 0.52f, 1f);
        private static readonly Color FillColour = new Color(0.36f, 0.62f, 0.90f, 1f);
        private static readonly Color DisabledColour = new Color(0.14f, 0.15f, 0.17f, 1f);
        internal static readonly Color TextColour = new Color(0.94f, 0.95f, 0.96f, 1f);
        internal static readonly Color DimTextColour = new Color(0.55f, 0.57f, 0.60f, 1f);

        /// <summary>One slider or ON/OFF button of the menu: what it shows, its range and step, and who takes its value.</summary>
        public sealed class Row
        {
            public string label;
            public float minimum, maximum, step;
            public string format = "F1";
            public string unit = "";
            public bool interactable = true;
            public bool isToggle;
            /// <summary>A line of text alone (an explanation): no slider, no toggle, no value; read/write unused.</summary>
            public bool isText;
            public Func<float> read;
            public Action<float> write;

            internal Slider slider;
            internal Image background;
            internal Text valueText;
            internal Text labelText;
        }

        private readonly List<Row> _rows = new List<Row>();
        private GameObject _root;
        private RectTransform _canvas;
        private Image _resume;
        private Image _reset;
        private Action _resetAction;
        private RectTransform _cursor;
        private Row _held;
        private bool _selectWasDown = true;
        private bool _resumeAsked;
        private Text _detail;
        private Func<string> _detailRead;

        /// <summary>The room under the settings for the development readout, when the menu is built with one.</summary>
        private const float DetailHeight = 160f;

        public bool IsBuilt => _root != null;
        public bool Visible => _root != null && _root.activeSelf;
        public Transform Root => _root != null ? _root.transform : null;
        public IReadOnlyList<Row> Rows => _rows;

        /// <summary>The resume button's rectangle, for a test's pointer.</summary>
        public RectTransform ResumeRect => _resume != null ? _resume.rectTransform : null;
        public RectTransform ResetRect => _reset != null ? _reset.rectTransform : null;

        /// <summary>The development readout's text under the settings, or null where the menu was built without one.</summary>
        public Text DetailText => _detail;

        /// <summary>
        /// Makes the canvas, hidden. <paramref name="note"/> is a line under the title (which scenario, what is fixed in it).
        /// </summary>
        public void Build(IReadOnlyList<Row> rows, string note, Action resetAction = null, Func<string> detail = null)
        {
            if (_root != null)
            {
                return;
            }

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Shader shader = Resources.Load<Shader>(ShaderResource);
            if (shader != null && shader.isSupported)
            {
                _material = new Material(shader) { name = "Zantetsu play menu", hideFlags = HideFlags.HideAndDontSave };
            }
            _root = new GameObject("Zantetsu play menu canvas", typeof(RectTransform), typeof(Canvas));
            if (Application.isPlaying)
            {
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }

            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            _canvas = (RectTransform)_root.transform;
            // An explanation row takes the room its wrapped text needs (TL review 2026-10-18 [P2]: a fixed 88 cut it),
            // measured here before the canvas is sized; every other row keeps its 96. It is drawn in the ordinary text
            // colour at the consent dialog's body size (TL 2026-10-18: dim 18 pt was unreadable on the Quest).
            float extra = 0f;
            foreach (Row row in rows)
            {
                if (!row.isText) continue;
                row.labelText = NewText("Text " + row.label, _canvas, font, row.label, ExplanationFontSize, TextColour, TextAnchor.UpperLeft);
                float textHeight = FitWrapped(row.labelText, Width - 100f, 40f);
                extra += Mathf.Max(RowStep, textHeight + 16f) - RowStep;
            }

            float height = Height + Mathf.Max(0, rows.Count - 3) * RowStep + extra + (resetAction != null ? 96f : 0f) + (detail != null ? DetailHeight : 0f);
            _canvas.sizeDelta = new Vector2(Width, height);
            _canvas.localScale = Vector3.one * UnitMetres;

            Image panel = NewImage("Panel", _canvas, PanelColour);
            Stretch(panel.rectTransform);

            Text title = NewText("Title", _canvas, font, "Paused", 44, TextColour, TextAnchor.MiddleCenter);
            Place(title.rectTransform, 0f, height * 0.5f - 60f, Width - 80f, 64f);
            if (!string.IsNullOrEmpty(note))
            {
                Text noteText = NewText("Note", _canvas, font, note, 22, DimTextColour, TextAnchor.MiddleCenter);
                Place(noteText.rectTransform, 0f, height * 0.5f - 112f, Width - 80f, 32f);
            }

            _resume = NewImage("Resume", _canvas, ControlColour);
            Place(_resume.rectTransform, 0f, height * 0.5f - 190f, 420f, 84f);
            Text resumeText = NewText("Label", _resume.rectTransform, font, "Resume", 36, TextColour, TextAnchor.MiddleCenter);
            Stretch(resumeText.rectTransform);

            _resetAction = resetAction;
            if (resetAction != null)
            {
                _reset = NewImage("Reset scene", _canvas, ControlColour);
                Place(_reset.rectTransform, 0f, -height * 0.5f + 56f, 420f, 72f);
                Text resetText = NewText("Label", _reset.rectTransform, font, "Reset scene", 32, TextColour, TextAnchor.MiddleCenter);
                Stretch(resetText.rectTransform);
            }
            // Each row in its slot from the top down: a setting's slot is RowStep (its centre where it always was), an
            // explanation's is its text's height with a margin.
            float top = height * 0.5f - 300f + (RowStep * 0.5f);
            foreach (Row row in rows)
            {
                float slot = row.isText ? Mathf.Max(RowStep, row.labelText.rectTransform.sizeDelta.y + 16f) : RowStep;
                BuildRow(row, font, top - (slot * 0.5f));
                _rows.Add(row);
                top -= slot;
            }

            if (detail != null)
            {
                // A development readout under the settings (the last swings and what the slash made of each), read
                // again each time the menu is shown: text alone, pressed by nothing.
                _detailRead = detail;
                _detail = NewText("Detail", _canvas, font, string.Empty, 20, DimTextColour, TextAnchor.UpperLeft);
                Place(_detail.rectTransform, 0f, -height * 0.5f + (resetAction != null ? 100f : 20f) + DetailHeight * 0.5f, Width - 80f, DetailHeight - 16f);
            }

            Image cursor = NewImage("Pointer", _canvas, new Color(1f, 0.85f, 0.25f, 1f));
            _cursor = cursor.rectTransform;
            Place(_cursor, 0f, 0f, 14f, 14f);
            cursor.gameObject.SetActive(false);
            if (_material != null)
            {
                foreach (Graphic graphic in _root.GetComponentsInChildren<Graphic>(true))
                {
                    graphic.material = _material;
                }
            }

            _root.SetActive(false);
        }

        private void BuildRow(Row row, Font font, float y)
        {
            Color text = row.interactable ? TextColour : DimTextColour;
            if (row.isText)
            {
                // The explanation of a setting (unit 9-B: the play statistics): text across the row, wrapping, made and
                // measured in Build (its height is its text's) -- before the panel was, so it is moved after it here: a
                // canvas draws its children in order, and the 92 % panel drawn over it made it dark (Quest, 2026-10-18).
                Place(row.labelText.rectTransform, 0f, y, Width - 100f, row.labelText.rectTransform.sizeDelta.y);
                row.labelText.transform.SetAsLastSibling();
                return;
            }

            row.labelText = NewText(row.label, _canvas, font, row.label, 26, text, TextAnchor.MiddleLeft);
            Place(row.labelText.rectTransform, -200f, y, 280f, 40f);

            if (row.isToggle)
            {
                row.background = NewImage("Toggle " + row.label, _canvas, row.interactable ? ControlColour : DisabledColour);
                Place(row.background.rectTransform, 90f, y, 280f, 44f);
                row.valueText = NewText("Value", row.background.rectTransform, font, "OFF", 26, text, TextAnchor.MiddleCenter);
                Stretch(row.valueText.rectTransform);
                return;
            }

            var sliderObject = new GameObject("Slider " + row.label, typeof(RectTransform), typeof(Slider));
            var sliderRect = (RectTransform)sliderObject.transform;
            sliderRect.SetParent(_canvas, false);
            Place(sliderRect, 90f, y, 280f, 44f);

            row.background = NewImage("Background", sliderRect, row.interactable ? ControlColour : DisabledColour);
            Stretch(row.background.rectTransform);

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            var fillAreaRect = (RectTransform)fillArea.transform;
            fillAreaRect.SetParent(sliderRect, false);
            Stretch(fillAreaRect);
            Image fill = NewImage("Fill", fillAreaRect, row.interactable ? FillColour : DimTextColour);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = Vector2.one;
            fill.rectTransform.sizeDelta = Vector2.zero;

            row.slider = sliderObject.GetComponent<Slider>();
            row.slider.direction = Slider.Direction.LeftToRight;
            row.slider.minValue = row.minimum;
            row.slider.maxValue = row.maximum;
            row.slider.wholeNumbers = false;
            row.slider.fillRect = fill.rectTransform;
            row.slider.targetGraphic = row.background;
            row.slider.transition = Selectable.Transition.None;
            row.slider.interactable = row.interactable;

            row.valueText = NewText("Value", _canvas, font, "", 26, text, TextAnchor.MiddleRight);
            Place(row.valueText.rectTransform, 290f, y, 100f, 40f);
        }

        /// <summary>
        /// Shows the menu before the head: at <see cref="Distance"/> along the head's horizontal facing, upright and
        /// turned to the head. It stands there until it is shown again.
        /// </summary>
        public void Show(Vector3 headPosition, Quaternion headRotation)
        {
            if (_root == null)
            {
                return;
            }

            Vector3 facing = headRotation * Vector3.forward;
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-6f)
            {
                facing = headRotation * Vector3.up;
                facing.y = 0f;
            }

            facing = facing.sqrMagnitude < 1e-6f ? Vector3.forward : facing.normalized;
            _canvas.SetPositionAndRotation(headPosition + (facing * Distance), Quaternion.LookRotation(facing, Vector3.up));
            Refresh();
            _held = null;
            _selectWasDown = true;   // a select already down when the menu opens presses nothing
            _resumeAsked = false;
            _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null)
            {
                _root.SetActive(false);
            }

            _held = null;
        }

        /// <summary>
        /// An interruption's boundary (D-241): a drag in progress is dropped, a select held is no press until it was
        /// let go and pressed anew, a resume asked but not taken is forgotten, and the pointer is hidden. The picture
        /// stays as it is.
        /// </summary>
        public void CancelInput()
        {
            _held = null;
            _selectWasDown = true;
            _resumeAsked = false;
            if (_cursor != null) _cursor.gameObject.SetActive(false);
        }

        /// <summary>The row being dragged now, or null (tests).</summary>
        internal Row HeldRow => _held;

        /// <summary>Whether the resume button was pressed since this was last asked.</summary>
        public bool ConsumeResume()
        {
            bool asked = _resumeAsked;
            _resumeAsked = false;
            return asked;
        }

        /// <summary>Brings every slider and number to the value its setting has now.</summary>
        public void Refresh()
        {
            foreach (Row row in _rows)
            {
                if (row.isText) continue;
                float value = row.read != null ? row.read() : row.minimum;
                if (row.isToggle)
                {
                    row.valueText.text = value >= 0.5f ? "ON" : "OFF";
                }
                else
                {
                    row.slider.SetValueWithoutNotify(Mathf.Clamp(value, row.minimum, row.maximum));
                    row.valueText.text = value.ToString(row.format, CultureInfo.InvariantCulture) + row.unit;
                }
            }

            if (_detail != null && _detailRead != null)
            {
                _detail.text = _detailRead() ?? string.Empty;
            }
        }

        /// <summary>
        /// One frame of the pointer: where the ray meets the canvas, what it is over, and what the select does there.
        /// </summary>
        public void Drive(in PlayMenuInputFrame input)
        {
            if (!Visible)
            {
                return;
            }

            bool pressed = input.selectDown && !_selectWasDown;
            _selectWasDown = input.selectDown;
            Vector3 world = default;
            bool hit = input.hasPointer && TryMeet(input.pointerOrigin, input.pointerDirection, out world);
            _cursor.gameObject.SetActive(hit);
            if (hit)
            {
                Vector3 local = _canvas.InverseTransformPoint(world);
                _cursor.anchoredPosition = new Vector2(local.x, local.y);
            }

            if (_reset != null)
            {
                bool overReset = hit && Contains(_reset.rectTransform, world);
                _reset.color = overReset ? HoverColour : ControlColour;
                if (overReset && pressed) { _resetAction?.Invoke(); return; }
            }
            bool overResume = hit && Contains(_resume.rectTransform, world);
            _resume.color = overResume ? HoverColour : ControlColour;
            if (overResume && pressed)
            {
                _resumeAsked = true;
            }

            if (!input.selectDown)
            {
                _held = null;
            }

            foreach (Row row in _rows)
            {
                if (!row.interactable)
                {
                    continue;
                }

                bool over = hit && Contains(row.background.rectTransform, world);
                row.background.color = over || _held == row ? HoverColour : ControlColour;
                if (over && pressed)
                {
                    if (row.isToggle)
                    {
                        _held = null;
                        row.write?.Invoke(row.read != null && row.read() >= 0.5f ? 0f : 1f);
                        Refresh();
                    }
                    else
                    {
                        _held = row;
                    }
                }
            }

            if (_held != null && hit)
            {
                Take(_held, world);
            }
        }

        private void Take(Row row, Vector3 world)
        {
            RectTransform rect = row.background.rectTransform;
            Vector3 local = rect.InverseTransformPoint(world);
            float t = Mathf.InverseLerp(rect.rect.xMin, rect.rect.xMax, local.x);
            float value = Mathf.Lerp(row.minimum, row.maximum, t);
            if (row.step > 0f)
            {
                value = row.minimum + (Mathf.Round((value - row.minimum) / row.step) * row.step);
            }

            value = Mathf.Clamp(value, row.minimum, row.maximum);
            if (!Mathf.Approximately(value, row.slider.value))
            {
                row.write?.Invoke(value);
                Refresh();
            }
        }

        /// <summary>Where a ray meets the canvas's plane, in the world; false behind the ray or beside the canvas.</summary>
        public bool TryMeet(Vector3 origin, Vector3 direction, out Vector3 world)
        {
            world = default;
            if (_canvas == null)
            {
                return false;
            }

            var plane = new Plane(_canvas.forward, _canvas.position);
            if (!plane.Raycast(new Ray(origin, direction), out float distance) || distance <= 0f)
            {
                return false;
            }

            world = origin + (direction.normalized * distance);
            return Contains(_canvas, world);
        }

        private static bool Contains(RectTransform rect, Vector3 world)
        {
            Vector3 local = rect.InverseTransformPoint(world);
            return rect.rect.Contains(new Vector2(local.x, local.y));
        }

        public void Destroy()
        {
            if (_root != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(_root);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(_root);
                }

                _root = null;
            }

            if (_material != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(_material);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(_material);
                }

                _material = null;
            }
        }

        internal static Image NewImage(string name, Transform parent, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        internal static Text NewText(string name, Transform parent, Font font, string value, int size, Color colour, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = font;
            text.text = value;
            text.fontSize = size;
            text.color = colour;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        internal static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Makes <paramref name="text"/> wrap at <paramref name="width"/> and gives it the height its wrapped lines take
        /// (at least <paramref name="minimum"/>), so that none of it is cut; the height is returned for the layout. The
        /// vertical overflow is Truncate, so that a text too long for the room it was given would show as cut in a
        /// check rather than run over what is below it.
        /// </summary>
        internal static float FitWrapped(Text text, float width, float minimum)
        {
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            Place(text.rectTransform, 0f, 0f, width, minimum);
            float height = Mathf.Max(minimum, Mathf.Ceil(text.preferredHeight) + 8f);
            text.rectTransform.sizeDelta = new Vector2(width, height);
            return height;
        }

        /// <summary>The height <paramref name="text"/>'s wrapped lines need in the rectangle it has now (tests: nothing cut).</summary>
        internal static float NeededHeight(Text text) => text.preferredHeight;

        internal static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
