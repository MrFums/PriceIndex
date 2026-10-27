using System;
using System.Text;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using UnityEngine;

namespace PriceIndex
{
    internal static class TooltipPatches
    {
        private const string Label = "#9a9a8e";
        private const string Value = "#e6e4d8";

        private static GridItemView _showing;

        private static Item _item;
        private static string _baseText;
        private static string _shownText;

        public static void Apply(Harmony harmony)
        {
            var showTooltip = AccessTools.DeclaredMethod(typeof(GridItemView), nameof(GridItemView.ShowTooltip))
                              ?? throw new MissingMethodException(nameof(GridItemView), nameof(GridItemView.ShowTooltip));
            var tooltipShow = AccessTools.Method(typeof(SimpleTooltip), nameof(SimpleTooltip.Show),
                                  new[] { typeof(string), typeof(Vector2?), typeof(float), typeof(float?) })
                              ?? throw new MissingMethodException(nameof(SimpleTooltip), nameof(SimpleTooltip.Show));

            harmony.Patch(showTooltip,
                prefix: new HarmonyMethod(typeof(TooltipPatches), nameof(ShowTooltipPrefix)),
                postfix: new HarmonyMethod(typeof(TooltipPatches), nameof(ShowTooltipPostfix)));
            harmony.Patch(tooltipShow, prefix: new HarmonyMethod(typeof(TooltipPatches), nameof(TooltipShowPrefix)));
        }

        private static void ShowTooltipPrefix(GridItemView __instance)
        {
            _showing = __instance;
        }

        private static void ShowTooltipPostfix()
        {
            _showing = null;
        }

        private static void TooltipShowPrefix(ref string text)
        {
            GridItemView view = _showing;
            _showing = null;
            if (view == null || text == null || !Plugin.HoverTooltip.Value)
                return;

            try
            {
                if (!PriceInfo.Shows(view.Item) || !view.Examined)
                    return;
                bool inRaid = Singleton<GameWorld>.Instantiated && !(Singleton<GameWorld>.Instance is HideoutGameWorld);
                if (!Plugin.InRaid.Value && inRaid)
                    return;
                if (!text.StartsWith(view.GetItemName(), StringComparison.Ordinal))
                    return;

                _item = view.Item;
                _baseText = text;
                text = _shownText = text + Lines(_item);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Tooltip price failed: {e}");
            }
        }

        public static void RedrawWaiting()
        {
            SimpleTooltip tooltip = ItemUiContext.Instance?.Tooltip;
            if (tooltip == null || _item == null || !tooltip.Displayed || tooltip._label.text != _shownText)
                return;

            _shownText = _baseText + Lines(_item);
            tooltip.SetText(_shownText);
        }

        private static string Lines(Item item)
        {
            PriceInfo info = PriceInfo.For(item, Redraw.OnPrice);
            return new StringBuilder("\n")
                .Append(Colour(Label, info.FleaName + "  ")).Append(Colour(Value, info.FleaText)).Append('\n')
                .Append(Colour(Label, info.TraderName + "  ")).Append(Colour(Value, info.TraderText))
                .ToString();
        }

        private static string Colour(string colour, string text)
        {
            return "<color=" + colour + ">" + text + "</color>";
        }
    }
}
