using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace PriceIndex
{
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
        public Action<ConfigEntryBase> CustomDrawer;
    }

    internal static class ColourField
    {
        private static readonly Dictionary<ConfigEntryBase, string> Typed = new Dictionary<ConfigEntryBase, string>();
        private static readonly HashSet<ConfigEntryBase> Open = new HashSet<ConfigEntryBase>();
        private static GUIStyle _swatch;

        private static GUIStyle Swatch
        {
            get
            {
                if (_swatch == null)
                {
                    _swatch = new GUIStyle();
                    _swatch.normal.background = Texture2D.whiteTexture;
                    _swatch.hover.background = Texture2D.whiteTexture;
                    _swatch.active.background = Texture2D.whiteTexture;
                    _swatch.margin = new RectOffset(4, 4, 3, 3);
                }

                return _swatch;
            }
        }

        public static void Draw(ConfigEntryBase entryBase)
        {
            var entry = (ConfigEntry<Color>)entryBase;
            Color colour = entry.Value;

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();

            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = new Color(colour.r, colour.g, colour.b, 1f);
            if (GUILayout.Button(GUIContent.none, Swatch, GUILayout.Width(70), GUILayout.Height(18)))
            {
                if (!Open.Remove(entry))
                    Open.Add(entry);
            }
            GUI.backgroundColor = previous;

            if (Typed.TryGetValue(entry, out string typing) && Parse(typing, out Color typed) && !Same(typed, colour))
            {
                Typed.Remove(entry);
                typing = null;
            }

            string shown = typing ?? "#" + ColorUtility.ToHtmlStringRGB(colour);
            string edited = GUILayout.TextField(shown, 9, GUILayout.Width(80));
            if (edited != shown)
            {
                Typed[entry] = edited;
                if (Parse(edited, out Color parsed))
                    entry.Value = parsed;
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (Open.Contains(entry))
            {
                float r = Slider("R", colour.r);
                float g = Slider("G", colour.g);
                float b = Slider("B", colour.b);
                if (r != colour.r || g != colour.g || b != colour.b)
                {
                    Typed.Remove(entry);
                    entry.Value = new Color(r, g, b, 1f);
                }
            }

            GUILayout.EndVertical();
        }

        private static float Slider(string name, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, GUILayout.Width(16));
            float result = GUILayout.HorizontalSlider(value, 0f, 1f, GUILayout.ExpandWidth(true));
            GUILayout.Label(Mathf.RoundToInt(result * 255f).ToString(), GUILayout.Width(30));
            GUILayout.EndHorizontal();
            return result;
        }

        private static bool Parse(string text, out Color colour)
        {
            colour = default;
            if (string.IsNullOrEmpty(text))
                return false;

            text = text.Trim();
            if (!text.StartsWith("#"))
                text = "#" + text;
            if (text.Length != 7 || !ColorUtility.TryParseHtmlString(text, out colour))
                return false;

            colour.a = 1f;
            return true;
        }

        private static bool Same(Color a, Color b)
        {
            return Mathf.RoundToInt(a.r * 255f) == Mathf.RoundToInt(b.r * 255f)
                   && Mathf.RoundToInt(a.g * 255f) == Mathf.RoundToInt(b.g * 255f)
                   && Mathf.RoundToInt(a.b * 255f) == Mathf.RoundToInt(b.b * 255f);
        }
    }
}
