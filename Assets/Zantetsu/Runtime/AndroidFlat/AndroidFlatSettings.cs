using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Zantetsu.AndroidFlat
{
    public sealed class AndroidFlatSettings
    {
        private const string Prefix = "zantetsu.androidflat.";
        public float MoveSpeed = 1.49f;
        public float TurnSpeed = 120f;
        public bool Fsr = true;
        public float RenderScale = .75f;
        public float Sharpness = .5f;
        private static readonly Vector2 DefaultStickPosition = new Vector2(.15f, .30f);
        public Vector2 PortraitStickPosition = DefaultStickPosition;
        public Vector2 LandscapeStickPosition = DefaultStickPosition;

        public void Load()
        {
            MoveSpeed = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "move", MoveSpeed), .5f, 5f);
            TurnSpeed = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "turn", TurnSpeed), 30f, 240f);
            Fsr = PlayerPrefs.GetInt(Prefix + "fsr", 1) != 0;
            RenderScale = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "scale", RenderScale), .5f, 1f);
            Sharpness = Mathf.Clamp01(PlayerPrefs.GetFloat(Prefix + "sharpness", Sharpness));
            SetStickPosition(true, new Vector2(PlayerPrefs.GetFloat(Prefix + "stick.portrait.x", .15f),
                PlayerPrefs.GetFloat(Prefix + "stick.portrait.y", .30f)));
            SetStickPosition(false, new Vector2(PlayerPrefs.GetFloat(Prefix + "stick.landscape.x", .15f),
                PlayerPrefs.GetFloat(Prefix + "stick.landscape.y", .30f)));
        }
        public void Save()
        {
            PlayerPrefs.SetFloat(Prefix + "move", MoveSpeed);
            PlayerPrefs.SetFloat(Prefix + "turn", TurnSpeed);
            PlayerPrefs.SetInt(Prefix + "fsr", Fsr ? 1 : 0);
            PlayerPrefs.SetFloat(Prefix + "scale", RenderScale);
            PlayerPrefs.SetFloat(Prefix + "sharpness", Sharpness);
            PlayerPrefs.SetFloat(Prefix + "stick.portrait.x", PortraitStickPosition.x);
            PlayerPrefs.SetFloat(Prefix + "stick.portrait.y", PortraitStickPosition.y);
            PlayerPrefs.SetFloat(Prefix + "stick.landscape.x", LandscapeStickPosition.x);
            PlayerPrefs.SetFloat(Prefix + "stick.landscape.y", LandscapeStickPosition.y);
            PlayerPrefs.Save();
        }
        public Vector2 GetStickPosition(bool portrait) => portrait ? PortraitStickPosition : LandscapeStickPosition;
        public void SetStickPosition(bool portrait, Vector2 value)
        {
            value.x = float.IsFinite(value.x) ? Mathf.Clamp01(value.x) : DefaultStickPosition.x;
            value.y = float.IsFinite(value.y) ? Mathf.Clamp01(value.y) : DefaultStickPosition.y;
            if (portrait) PortraitStickPosition = value;
            else LandscapeStickPosition = value;
        }
        public void ResetStickPosition(bool portrait) => SetStickPosition(portrait, DefaultStickPosition);

        public void Apply(UniversalRenderPipelineAsset pipeline)
        {
            if (pipeline == null) return;
            pipeline.renderScale = Fsr ? RenderScale : 1f;
            pipeline.upscalingFilter = Fsr ? UpscalingFilterSelection.FSR : UpscalingFilterSelection.Auto;
            pipeline.fsrOverrideSharpness = true;
            pipeline.fsrSharpness = Sharpness;
        }
        public void NextScale()
        {
            RenderScale = RenderScale < .70f ? .75f : RenderScale < .80f ? .85f : RenderScale < .95f ? 1f : .67f;
        }
    }
}
