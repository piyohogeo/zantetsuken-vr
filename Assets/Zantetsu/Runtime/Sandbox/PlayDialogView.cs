using System;
using UnityEngine;
using UnityEngine.UI;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A dialog of the play menu's kind (DESIGN 4.6, 21.18): one world space canvas before the head with a title, a
    /// body of several lines, and two buttons; driven by the same pointer the menu is. Made from the menu view's own
    /// pieces. The first-run consent to the play statistics is shown by it; what the buttons mean is the caller's.
    /// </summary>
    public sealed class PlayDialogView
    {
        private const float Width = 980f;
        private const float UnitMetres = PlayMenuView.UnitMetres;

        private GameObject _root;
        private RectTransform _canvas;
        private Image _first, _second;
        private RectTransform _cursor;
        private bool _selectWasDown = true;
        private Material _material;

        public bool IsBuilt => _root != null;
        public bool Visible => _root != null && _root.activeSelf;
        public RectTransform FirstRect => _first != null ? _first.rectTransform : null;
        public RectTransform SecondRect => _second != null ? _second.rectTransform : null;

        /// <summary>Makes the canvas, hidden: a title, the body (lines by '\n'), and the two buttons' labels.</summary>
        public void Build(string title, string body, string firstLabel, string secondLabel)
        {
            if (_root != null)
            {
                return;
            }

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Shader shader = Resources.Load<Shader>(PlayMenuView.ShaderResource);
            if (shader != null && shader.isSupported)
            {
                _material = new Material(shader) { name = "Zantetsu play dialog", hideFlags = HideFlags.HideAndDontSave };
            }

            int lines = 1;
            foreach (char c in body) if (c == '\n') lines++;
            float bodyHeight = Mathf.Max(60f, lines * 30f + 20f);
            float height = 120f + bodyHeight + 140f;
            _root = new GameObject("Zantetsu play dialog canvas", typeof(RectTransform), typeof(Canvas));
            if (Application.isPlaying)
            {
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }

            _root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            _canvas = (RectTransform)_root.transform;
            _canvas.sizeDelta = new Vector2(Width, height);
            _canvas.localScale = Vector3.one * UnitMetres;
            PlayMenuView.Stretch(PlayMenuView.NewImage("Panel", _canvas, PlayMenuView.PanelColour).rectTransform);
            Text titleText = PlayMenuView.NewText("Title", _canvas, font, title, 40, PlayMenuView.TextColour, TextAnchor.MiddleCenter);
            PlayMenuView.Place(titleText.rectTransform, 0f, height * 0.5f - 56f, Width - 80f, 60f);
            Text bodyText = PlayMenuView.NewText("Body", _canvas, font, body, 24, PlayMenuView.TextColour, TextAnchor.UpperLeft);
            PlayMenuView.Place(bodyText.rectTransform, 0f, height * 0.5f - 110f - bodyHeight * 0.5f, Width - 100f, bodyHeight);
            _first = PlayMenuView.NewImage("First", _canvas, PlayMenuView.ControlColour);
            PlayMenuView.Place(_first.rectTransform, -Width * 0.25f, -height * 0.5f + 70f, 380f, 84f);
            PlayMenuView.Stretch(PlayMenuView.NewText("Label", _first.rectTransform, font, firstLabel, 34, PlayMenuView.TextColour, TextAnchor.MiddleCenter).rectTransform);
            _second = PlayMenuView.NewImage("Second", _canvas, PlayMenuView.ControlColour);
            PlayMenuView.Place(_second.rectTransform, Width * 0.25f, -height * 0.5f + 70f, 380f, 84f);
            PlayMenuView.Stretch(PlayMenuView.NewText("Label", _second.rectTransform, font, secondLabel, 34, PlayMenuView.TextColour, TextAnchor.MiddleCenter).rectTransform);
            Image cursor = PlayMenuView.NewImage("Pointer", _canvas, new Color(1f, 0.85f, 0.25f, 1f));
            _cursor = cursor.rectTransform;
            PlayMenuView.Place(_cursor, 0f, 0f, 14f, 14f);
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

        /// <summary>Shows the dialog before the head, as the menu is shown.</summary>
        public void Show(Vector3 headPosition, Quaternion headRotation)
        {
            if (_root == null) return;
            Vector3 facing = headRotation * Vector3.forward;
            facing.y = 0f;
            facing = facing.sqrMagnitude < 1e-6f ? Vector3.forward : facing.normalized;
            _canvas.SetPositionAndRotation(headPosition + (facing * PlayMenuView.Distance), Quaternion.LookRotation(facing, Vector3.up));
            _selectWasDown = true;   // a select already down when it opens presses nothing
            _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        /// <summary>One frame of the pointer: 1 where the first button was pressed, 2 the second, 0 neither.</summary>
        public int Drive(in PlayMenuInputFrame input)
        {
            if (_root == null || !_root.activeSelf) return 0;
            bool pressed = input.selectDown && !_selectWasDown;
            _selectWasDown = input.selectDown;
            if (!input.hasPointer || !TryMeet(input.pointerOrigin, input.pointerDirection, out Vector3 world))
            {
                _cursor.gameObject.SetActive(false);
                _first.color = PlayMenuView.ControlColour;
                _second.color = PlayMenuView.ControlColour;
                return 0;
            }

            _cursor.gameObject.SetActive(true);
            _cursor.position = world;
            bool overFirst = Contains(_first.rectTransform, world), overSecond = Contains(_second.rectTransform, world);
            _first.color = overFirst ? PlayMenuView.HoverColour : PlayMenuView.ControlColour;
            _second.color = overSecond ? PlayMenuView.HoverColour : PlayMenuView.ControlColour;
            if (!pressed) return 0;
            return overFirst ? 1 : overSecond ? 2 : 0;
        }

        public bool TryMeet(Vector3 origin, Vector3 direction, out Vector3 world)
        {
            world = default;
            var plane = new Plane(-_canvas.forward, _canvas.position);
            var ray = new Ray(origin, direction);
            if (!plane.Raycast(ray, out float distance) || distance <= 0f) return false;
            world = ray.GetPoint(distance);
            return true;
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
                if (Application.isPlaying) UnityEngine.Object.Destroy(_root); else UnityEngine.Object.DestroyImmediate(_root);
                _root = null;
            }

            if (_material != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_material); else UnityEngine.Object.DestroyImmediate(_material);
                _material = null;
            }
        }
    }
}
