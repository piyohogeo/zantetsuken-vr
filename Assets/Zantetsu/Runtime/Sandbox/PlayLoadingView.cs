using UnityEngine;
using UnityEngine.UI;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The start-up's picture (2026-10-18, TL): "Now loading..." with a ring of dots whose bright one goes round, so that
    /// the player sees the application alive while the scene prepares (the crowd's pool and plan, the placed
    /// cuttables, the Quest's drawing warm-up) and the katana does not cut yet. One small world space canvas of the
    /// menu's kind (<see cref="PlayMenuView"/>: its pieces, its front-drawn material), kept before the head and a little
    /// below the eyes by its owner, who also decides when it is shown. It presses nothing and takes no pointer.
    /// </summary>
    public sealed class PlayLoadingView
    {
        public const float Width = 520f;
        public const float Height = 120f;
        public const int Dots = 8;

        /// <summary>Revolutions of the bright dot a second.</summary>
        public const float RevolutionsPerSecond = 1f;

        private const float DotRadius = 34f;
        private const float DotSize = 14f;
        private static readonly Color DotColour = new Color(0.36f, 0.62f, 0.90f, 1f);

        private GameObject _root;
        private RectTransform _canvas, _window;
        private bool _overlay;
        private readonly Image[] _dots = new Image[Dots];
        private Material _material;

        public bool IsBuilt => _root != null;
        public bool Visible => _root != null && _root.activeSelf;
        public Transform Root => _root != null ? _root.transform : null;
        public RectTransform Window => _window;

        /// <summary>Whether it is drawn in front of everything, by the overlay canvas or its own world-space material.</summary>
        public bool DrawnInFront => _overlay || _material != null;

        /// <summary>The dots' alphas now (tests: the bright one goes round).</summary>
        public float DotAlpha(int index) => _dots[index] != null ? _dots[index].color.a : 0f;

        /// <summary>Makes the world-space canvas, hidden.</summary>
        public void Build() => Build(false, null, 0);

        /// <summary>Makes the screen-space canvas, hidden, with its lifetime owned by the given parent.</summary>
        public void BuildOverlay(Transform parent, int sortingOrder = 2000) => Build(true, parent, sortingOrder);

        private void Build(bool overlay, Transform parent, int sortingOrder)
        {
            if (_root != null) return;
            _overlay = overlay;
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (!overlay)
            {
                Shader shader = Resources.Load<Shader>(PlayMenuView.ShaderResource);
                if (shader != null && shader.isSupported)
                    _material = new Material(shader) { name = "Zantetsu play loading", hideFlags = HideFlags.HideAndDontSave };
            }

            _root = new GameObject("Zantetsu play loading canvas", typeof(RectTransform), typeof(Canvas));
            Canvas canvas = _root.GetComponent<Canvas>();
            _canvas = (RectTransform)_root.transform;
            if (overlay)
            {
                _canvas.SetParent(parent, false);
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = sortingOrder;
                _window = (RectTransform)new GameObject("Window", typeof(RectTransform)).transform;
                _window.SetParent(_canvas, false);
                _window.anchorMin = _window.anchorMax = Vector2.zero;
                _window.pivot = Vector2.one * .5f;
            }
            else
            {
                if (Application.isPlaying) Object.DontDestroyOnLoad(_root);
                canvas.renderMode = RenderMode.WorldSpace;
                _window = _canvas;
                _canvas.localScale = Vector3.one * PlayMenuView.UnitMetres;
            }
            _window.sizeDelta = new Vector2(Width, Height);
            PlayMenuView.Stretch(PlayMenuView.NewImage("Panel", _window, PlayMenuView.PanelColour).rectTransform);
            var ring = new GameObject("Ring", typeof(RectTransform));
            var ringRect = (RectTransform)ring.transform;
            ringRect.SetParent(_window, false);
            PlayMenuView.Place(ringRect, -Width * 0.5f + 80f, 0f, DotRadius * 2f + DotSize, DotRadius * 2f + DotSize);
            for (int i = 0; i < Dots; i++)
            {
                float angle = (Mathf.PI * 2f * i) / Dots;
                Image dot = PlayMenuView.NewImage("Dot " + i, ringRect, DotColour);
                PlayMenuView.Place(dot.rectTransform, Mathf.Sin(angle) * DotRadius, Mathf.Cos(angle) * DotRadius, DotSize, DotSize);
                _dots[i] = dot;
            }

            Text text = PlayMenuView.NewText("Text", _window, font, "Now loading...", 34, PlayMenuView.TextColour, TextAnchor.MiddleLeft);
            PlayMenuView.Place(text.rectTransform, 50f, 0f, Width - 180f, 60f);
            if (_material != null)
            {
                foreach (Graphic graphic in _root.GetComponentsInChildren<Graphic>(true)) graphic.material = _material;
            }

            Animate(0f);
            _root.SetActive(false);
        }

        /// <summary>The ring at <paramref name="seconds"/>: the bright dot goes round clockwise, the ones behind it fade.</summary>
        public void Animate(float seconds)
        {
            if (_root == null) return;
            float phase = seconds * RevolutionsPerSecond * Dots;
            for (int i = 0; i < Dots; i++)
            {
                // How far behind the bright dot this one is, in dots (0: the bright one), going round.
                float behind = Mathf.Repeat(phase - i, Dots);
                Color colour = DotColour;
                colour.a = Mathf.Lerp(1f, 0.15f, behind / Dots);
                _dots[i].color = colour;
            }
        }

        /// <summary>Puts the canvas where its owner wants it (world position and facing), shown.</summary>
        public void Place(Vector3 position, Quaternion rotation)
        {
            if (_root == null) return;
            _canvas.SetPositionAndRotation(position, rotation);
            if (!_root.activeSelf) _root.SetActive(true);
        }

        /// <summary>Centres the window in the screen's safe area, in pixels, and shows it.</summary>
        public void PlaceOverlay(Rect safeArea)
        {
            if (_root == null || !_overlay) return;
            _window.anchoredPosition = safeArea.center;
            _window.localScale = Vector3.one * (Mathf.Min(safeArea.width, safeArea.height) / 900f);
            if (!_root.activeSelf) _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        public void Destroy()
        {
            if (_root != null)
            {
                if (Application.isPlaying) Object.Destroy(_root);
                else Object.DestroyImmediate(_root);
                _root = null;
            }

            if (_material != null)
            {
                if (Application.isPlaying) Object.Destroy(_material);
                else Object.DestroyImmediate(_material);
                _material = null;
            }
            _canvas = _window = null;
            _overlay = false;
            for (int i = 0; i < _dots.Length; i++) _dots[i] = null;
        }
    }
}
