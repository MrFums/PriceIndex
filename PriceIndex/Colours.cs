using System;
using System.Collections.Generic;
using UnityEngine;

namespace PriceIndex
{
    internal static class Tiers
    {
        private struct Tier
        {
            public double Min;
            public Color32 Colour;
        }

        public const int Count = 5;

        private static readonly List<Tier> Built = new List<Tier>();
        private static bool _dirty = true;

        public static Color32 Blocked
        {
            get
            {
                Color picked = Plugin.ColourBlocked.Value;
                return new Color32((byte)Mathf.RoundToInt(picked.r * 255f), (byte)Mathf.RoundToInt(picked.g * 255f), (byte)Mathf.RoundToInt(picked.b * 255f), 0xff);
            }
        }

        public static void Invalidate()
        {
            _dirty = true;
        }

        public static bool NamesOn
        {
            get { return Plugin.ColourEnabled.Value && Plugin.ColourNames.Value != NameColour.Off; }
        }

        public static bool NamesOnItems
        {
            get
            {
                NameColour mode = Plugin.ColourNames.Value;
                return Plugin.ColourEnabled.Value && (mode == NameColour.OnItems || mode == NameColour.Both);
            }
        }

        public static bool NamesOnHover
        {
            get
            {
                NameColour mode = Plugin.ColourNames.Value;
                return Plugin.ColourEnabled.Value && (mode == NameColour.OnHover || mode == NameColour.Both);
            }
        }

        public static bool TextOn
        {
            get
            {
                PriceColour mode = Plugin.ColourPrices.Value;
                return Plugin.ColourEnabled.Value && (mode == PriceColour.Text || mode == PriceColour.TagBackgroundAndText);
            }
        }

        public static bool PricesOn
        {
            get { return Plugin.ColourEnabled.Value && Plugin.ColourPrices.Value != PriceColour.Off; }
        }

        public static bool TagBackgroundOn
        {
            get
            {
                PriceColour mode = Plugin.ColourPrices.Value;
                return Plugin.ColourEnabled.Value && (mode == PriceColour.TagBackground || mode == PriceColour.TagBackgroundAndText);
            }
        }

        public static bool ItemBackgroundOn
        {
            get { return Plugin.ColourEnabled.Value && Plugin.ColourItems.Value; }
        }

        public static string Hexcode(Color32 colour)
        {
            return "#" + ColorUtility.ToHtmlStringRGB(colour);
        }

        public static string Wrap(string text, Color32? colour)
        {
            return colour == null || string.IsNullOrEmpty(text) ? text : "<color=" + Hexcode(colour.Value) + ">" + text + "</color>";
        }

        public static Color32? ForText(PriceInfo info)
        {
            return TextOn ? For(info) : null;
        }

        public static Color32? ForName(PriceInfo info)
        {
            return NamesOn ? For(info) : null;
        }

        public static Color32? ForTag(PriceInfo info)
        {
            return PricesOn ? For(info) : null;
        }

        public static Color32? ForItem(PriceInfo info)
        {
            return ItemBackgroundOn ? For(info) : null;
        }

        public static Color32 Panel(Color32 colour)
        {
            float strength = Plugin.ColourStrength.Value / 100f;
            return new Color32((byte)(colour.r * strength), (byte)(colour.g * strength), (byte)(colour.b * strength), 0xff);
        }

        public static Color32 Backing(Color32 colour)
        {
            Color32 result = colour;
            for (int i = 0; i < 12 && Luminance(result) > 0.18f; i++)
                result = new Color32((byte)(result.r * 0.88f), (byte)(result.g * 0.88f), (byte)(result.b * 0.88f), 0xff);
            return result;
        }

        private static float Luminance(Color32 colour)
        {
            return 0.2126f * Linear(colour.r) + 0.7152f * Linear(colour.g) + 0.0722f * Linear(colour.b);
        }

        private static float Linear(byte channel)
        {
            float value = channel / 255f;
            return value <= 0.04045f ? value / 12.92f : Mathf.Pow((value + 0.055f) / 1.055f, 2.4f);
        }

        private static Color32? For(PriceInfo info)
        {
            if (!NamesOn && !PricesOn && !ItemBackgroundOn)
                return null;

            double? flea = info.FleaBanned ? null : info.FleaTotal;
            double? roubles;
            switch (Plugin.ColourSource.Value)
            {
                case ColourBy.Flea:
                    if (info.FleaLoading)
                        return null;
                    if (info.FleaBanned)
                        return Blocked;
                    roubles = flea;
                    break;
                case ColourBy.Trader:
                    if (info.TraderLoading)
                        return null;
                    roubles = info.TraderRoubles;
                    if (roubles == null)
                        return Blocked;
                    break;
                default:
                    if (info.Loading)
                        return null;
                    roubles = flea != null || info.TraderRoubles != null
                        ? Math.Max(flea ?? 0, info.TraderRoubles ?? 0)
                        : (double?)null;
                    if (roubles == null && (info.FleaBanned || info.FleaTotal == null))
                        return Blocked;
                    break;
            }

            return roubles == null ? null : Lookup(roubles.Value);
        }

        public static Color32? ForMoney(double? roubles)
        {
            return roubles == null || !Plugin.ColourEnabled.Value ? null : Lookup(roubles.Value);
        }

        private static Color32? Lookup(double roubles)
        {
            if (_dirty)
                Rebuild();

            Color32? found = null;
            foreach (Tier tier in Built)
            {
                if (roubles < tier.Min)
                    break;
                found = tier.Colour;
            }

            return found;
        }

        private static void Rebuild()
        {
            _dirty = false;
            Built.Clear();
            for (int i = 0; i < Count; i++)
            {
                Color picked = Plugin.TierColour[i].Value;
                Built.Add(new Tier { Min = Math.Max(0, Plugin.TierMin[i].Value), Colour = new Color32((byte)Mathf.RoundToInt(picked.r * 255f), (byte)Mathf.RoundToInt(picked.g * 255f), (byte)Mathf.RoundToInt(picked.b * 255f), 0xff) });
            }

            Built.Sort((a, b) => a.Min.CompareTo(b.Min));
        }
    }
}
