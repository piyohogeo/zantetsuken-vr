using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The light a drawing test stands in. Every VP surface is lit by URP's lighting from the scene it is drawn in
    /// (DESIGN 5.3) -- its main light, its ambient probe, its default reflection -- so a test that reads colours says
    /// what that scene's light is, and puts back what the scene had: the active scene's ambient and reflection
    /// settings, and which of its lights were on.
    /// <para>
    /// Only the ambient modes that need no bake are used (three colours, or one), so the ambient probe is the one
    /// asked for as soon as it is set.
    /// </para>
    /// </summary>
    internal sealed class VpTestLighting : IDisposable
    {
        private readonly AmbientMode _mode;
        private readonly Color _sky, _equator, _ground, _flat;
        private readonly float _ambientIntensity, _reflectionIntensity;
        private readonly DefaultReflectionMode _reflectionMode;
        private readonly Texture _customReflection;
        private readonly List<Light> _switchedOff = new List<Light>();
        private bool _disposed;

        private VpTestLighting()
        {
            _mode = RenderSettings.ambientMode;
            _sky = RenderSettings.ambientSkyColor;
            _equator = RenderSettings.ambientEquatorColor;
            _ground = RenderSettings.ambientGroundColor;
            _flat = RenderSettings.ambientLight;
            _ambientIntensity = RenderSettings.ambientIntensity;
            _reflectionIntensity = RenderSettings.reflectionIntensity;
            _reflectionMode = RenderSettings.defaultReflectionMode;
            _customReflection = RenderSettings.customReflectionTexture;
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.enabled)
                {
                    light.enabled = false;
                    _switchedOff.Add(light);
                }
            }
        }

        /// <summary>
        /// Ambient light alone, bright from every side and brighter from above than from below, with no reflection: a
        /// surface of a saturated colour is drawn in that colour whichever way it faces -- between about 0.55 and 0.85
        /// of it, linear, after the common colour multiplier -- and faces of different heights take different shades.
        /// What a test of where something is drawn, not of how it is lit, reads its colours in.
        /// </summary>
        internal static VpTestLighting BrightFromEverySide()
        {
            var lighting = new VpTestLighting();
            lighting.Ambient(Grey(1.5f), Grey(1.15f), Grey(0.9f));
            RenderSettings.reflectionIntensity = 0f;
            DynamicGI.UpdateEnvironment();
            Color up = lighting.AmbientTowards(Vector3.up);
            Color down = lighting.AmbientTowards(Vector3.down);
            Assert.That(down.r, Is.InRange(0.85f, 1.2f), "the test light from below (linear): " + down);
            Assert.That(up.r, Is.InRange(down.r + 0.15f, 1.55f), "and from above: " + up);
            return lighting;
        }

        /// <summary>
        /// The scene's lights off and its ambient light and default reflection as given: three ambient colours (as a
        /// material holds a colour) and a cubemap, or none for no reflection. A test adds the light it wants.
        /// </summary>
        internal static VpTestLighting Of(Color sky, Color equator, Color ground, Cubemap reflection)
        {
            var lighting = new VpTestLighting();
            lighting.Ambient(sky, equator, ground);
            if (reflection != null)
            {
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = reflection;
                RenderSettings.reflectionIntensity = 1f;
            }
            else
            {
                RenderSettings.reflectionIntensity = 0f;
            }

            DynamicGI.UpdateEnvironment();
            return lighting;
        }

        /// <summary>The ambient probe of the scene towards a direction, linear: what a face with that normal is given.</summary>
        internal Color AmbientTowards(Vector3 direction)
        {
            var results = new Color[1];
            RenderSettings.ambientProbe.Evaluate(new[] { direction.normalized }, results);
            return results[0];
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            RenderSettings.ambientMode = _mode;
            RenderSettings.ambientSkyColor = _sky;
            RenderSettings.ambientEquatorColor = _equator;
            RenderSettings.ambientGroundColor = _ground;
            RenderSettings.ambientLight = _flat;
            RenderSettings.ambientIntensity = _ambientIntensity;
            RenderSettings.reflectionIntensity = _reflectionIntensity;
            RenderSettings.defaultReflectionMode = _reflectionMode;
            RenderSettings.customReflectionTexture = _customReflection;
            DynamicGI.UpdateEnvironment();
            foreach (Light light in _switchedOff)
            {
                if (light != null)
                {
                    light.enabled = true;
                }
            }

            _switchedOff.Clear();
        }

        private void Ambient(Color sky, Color equator, Color ground)
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = sky;
            RenderSettings.ambientEquatorColor = equator;
            RenderSettings.ambientGroundColor = ground;
            RenderSettings.ambientIntensity = 1f;
        }

        // A grey of the given linear value, as the colour the ambient setting takes.
        private static Color Grey(float linear)
        {
            float value = Mathf.LinearToGammaSpace(linear);
            return new Color(value, value, value, 1f);
        }
    }
}
