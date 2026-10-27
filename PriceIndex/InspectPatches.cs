using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PriceIndex
{
    internal static class InspectPatches
    {
        internal enum PriceRow
        {
            Flea,
            Trader
        }

        private const string LineName = "PriceIndexLine";

        private static readonly AccessTools.FieldRef<ItemSpecificationPanel, Item> PanelItem =
            AccessTools.FieldRefAccess<ItemSpecificationPanel, Item>("_item");

        private static readonly AccessTools.FieldRef<ItemSpecificationPanel, SimpleTooltip> PanelTooltip =
            AccessTools.FieldRefAccess<ItemSpecificationPanel, SimpleTooltip>("_secondaryTooltip");

        private static readonly AccessTools.FieldRef<ItemSpecificationPanel, ViewList<ItemAttribute, CompactCharacteristicPanel>> CompactRows =
            AccessTools.FieldRefAccess<ItemSpecificationPanel, ViewList<ItemAttribute, CompactCharacteristicPanel>>("_compactBarAttributeViewers");

        private static readonly HashSet<ItemSpecificationPanel> Waiting = new HashSet<ItemSpecificationPanel>();

        private static readonly List<Line> Lines = new List<Line>();

        private static bool _loggedLayout;

        public static void Apply(Harmony harmony)
        {
            var recreate = AccessTools.DeclaredMethod(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.RecreateAttributeBars))
                           ?? throw new MissingMethodException(nameof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.RecreateAttributeBars));
            var icon = AccessTools.DeclaredMethod(typeof(StaticIcons), nameof(StaticIcons.GetAttributeIcon))
                       ?? throw new MissingMethodException(nameof(StaticIcons), nameof(StaticIcons.GetAttributeIcon));

            harmony.Patch(recreate,
                prefix: new HarmonyMethod(typeof(InspectPatches), nameof(RecreatePrefix)),
                postfix: new HarmonyMethod(typeof(InspectPatches), nameof(RecreatePostfix)));
            harmony.Patch(icon, prefix: new HarmonyMethod(typeof(InspectPatches), nameof(IconPrefix)));
        }

        private static void RecreatePrefix(ItemSpecificationPanel __instance, ref ItemAttribute[] __state)
        {
            __state = null;
            try
            {
                Item item = PanelItem(__instance);
                if (Plugin.Inspect.Value != InspectStyle.StatsGrid || !Shows(__instance, item))
                    return;

                __state = Rows(__instance, item);
                item.Attributes.AddRange(__state);
            }
            catch (Exception e)
            {
                __state = null;
                Plugin.Log.LogError($"Could not add prices to the inspect window: {e}");
            }
        }

        private static void RecreatePostfix(ItemSpecificationPanel __instance, ItemAttribute[] __state)
        {
            try
            {
                Item item = PanelItem(__instance);
                if (__state != null)
                {
                    foreach (ItemAttribute row in __state)
                        item?.Attributes.Remove(row);
                    SetIcons(CompactRows(__instance));
                }

                InspectStyle style = Plugin.Inspect.Value;
                bool line = (style == InspectStyle.LineUnderStats || style == InspectStyle.LineAboveStats) && Shows(__instance, item);
                ShowLine(__instance, line ? Rows(__instance, item) : null, style);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not finish the prices in the inspect window: {e}");
            }
        }

        private static bool IconPrefix(Enum id, ref Sprite __result)
        {
            if (!(id is PriceRow))
                return true;

            __result = null;
            return false;
        }

        private static bool Shows(ItemSpecificationPanel panel, Item item)
        {
            if (!PriceInfo.Shows(item) || !panel.Examined)
                return false;

            bool inRaid = Singleton<GameWorld>.Instantiated && !(Singleton<GameWorld>.Instance is HideoutGameWorld);
            return Plugin.InRaid.Value || !inRaid;
        }

        private static ItemAttribute[] Rows(ItemSpecificationPanel panel, Item item)
        {
            PriceInfo info = PriceInfo.For(item, Redraw.OnPrice);
            if (info.Loading || info.Refreshing)
                Waiting.Add(panel);

            return new[]
            {
                Row(PriceRow.Flea, info.FleaName, info.FleaValue, info.FleaTip, CurrencyUtil.ROUBLE_ID),
                Row(PriceRow.Trader, info.TraderName, info.TraderValue, info.TraderTip, info.TraderCurrency)
            };
        }

        private static ItemAttribute Row(PriceRow id, string name, string value, string tooltip, MongoID currency)
        {
            return new PriceAttribute(id)
            {
                Name = name,
                DisplayNameFunc = () => name,
                StringValue = () => value,
                FullStringValue = () => tooltip,
                DisplayType = () => EItemAttributeDisplayType.Compact,
                Currency = currency
            };
        }

        private sealed class PriceAttribute : ItemAttribute
        {
            public MongoID Currency;

            public PriceAttribute(Enum id) : base(id)
            {
            }
        }

        private static void SetIcons(IEnumerable<KeyValuePair<ItemAttribute, CompactCharacteristicPanel>> rows)
        {
            if (rows == null)
                return;

            foreach (var pair in rows)
                SetIcon(pair.Key, pair.Value);
        }

        private static void SetIcon(ItemAttribute attribute, CompactCharacteristicPanel row)
        {
            if (!(attribute is PriceAttribute price) || row == null || row.Icon == null)
                return;

            Sprite sign = EFTHardSettings.Instance.StaticIcons.GetSmallCurrencySign(price.Currency, nullable: true);
            row.Icon.sprite = sign;
            row.Icon.gameObject.SetActive(sign != null);
            if (sign != null)
                row.Icon.SetNativeSize();
        }

        private sealed class Line
        {
            public ItemSpecificationPanel Panel;
            public GridLayoutGroup Grid;
            public GridLayoutGroup Copy;
            public CompactCharacteristicPanel[] Rows;
        }

        private static void ShowLine(ItemSpecificationPanel panel, ItemAttribute[] rows, InspectStyle style)
        {
            Line line = Lines.FirstOrDefault(l => l.Panel == panel);
            if (rows == null)
            {
                if (line != null && line.Copy != null)
                    line.Copy.gameObject.SetActive(false);
                return;
            }

            if (line == null || line.Copy == null)
            {
                line = CreateLine(panel);
                if (line == null)
                    return;
            }

            Transform grid = line.Grid.transform;
            Transform copy = line.Copy.transform;
            if (style == InspectStyle.LineAboveStats)
                PlaceBefore(copy, TopOfStats(panel, grid));
            else
                PlaceAfter(copy, grid);

            line.Copy.gameObject.SetActive(true);
            line.Copy.constraintCount = line.Grid.constraintCount;

            SimpleTooltip tooltip = PanelTooltip(panel);
            for (int i = 0; i < line.Rows.Length; i++)
            {
                CompactCharacteristicPanel row = line.Rows[i];
                row.Show(rows[i], tooltip, true);
                row.CompareWith(null);
                SetIcon(rows[i], row);
            }
        }

        private static Line CreateLine(ItemSpecificationPanel panel)
        {
            Transform gridTransform = panel._compactPanel;
            GridLayoutGroup grid = gridTransform != null ? gridTransform.GetComponent<GridLayoutGroup>() : null;
            if (grid == null || gridTransform.parent == null || panel._compactCharTemplate == null)
                return null;

            if (!_loggedLayout)
            {
                _loggedLayout = true;
                Transform parent = gridTransform.parent;
                Plugin.Log.LogInfo($"Inspect window stats sit in '{parent.name}' ("
                                   + string.Join(", ", parent.GetComponents<Component>().Select(c => c.GetType().Name)) + "), "
                                   + $"children: {string.Join(", ", Enumerable.Range(0, parent.childCount).Select(i => parent.GetChild(i).name))}");
            }

            GameObject copy = UnityEngine.Object.Instantiate(gridTransform.gameObject, gridTransform.parent, false);
            copy.name = LineName;
            for (int i = copy.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(copy.transform.GetChild(i).gameObject);

            var layout = copy.GetComponent<GridLayoutGroup>();
            layout.startAxis = GridLayoutGroup.Axis.Horizontal;

            var rows = new CompactCharacteristicPanel[2];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = UnityEngine.Object.Instantiate(panel._compactCharTemplate, copy.transform, false);
                rows[i].name = LineName + "Row" + i;
            }

            Lines.RemoveAll(l => l.Panel == null || l.Copy == null);
            var line = new Line { Panel = panel, Grid = grid, Copy = layout, Rows = rows };
            Lines.Add(line);
            return line;
        }

        private static Transform TopOfStats(ItemSpecificationPanel panel, Transform grid)
        {
            Transform top = grid;
            foreach (Component part in new Component[] { panel._charPanelContainer, panel._charDurability, panel._charMastering })
            {
                if (part != null && part.transform.parent == grid.parent && part.gameObject.activeSelf
                    && part.transform.GetSiblingIndex() < top.GetSiblingIndex())
                    top = part.transform;
            }

            return top;
        }

        private static void PlaceBefore(Transform moving, Transform target)
        {
            int index = target.GetSiblingIndex();
            moving.SetSiblingIndex(moving.GetSiblingIndex() < index ? index - 1 : index);
        }

        private static void PlaceAfter(Transform moving, Transform target)
        {
            int index = target.GetSiblingIndex();
            moving.SetSiblingIndex(moving.GetSiblingIndex() < index ? index : index + 1);
        }

        public static void Update()
        {
            foreach (Line line in Lines)
            {
                if (line.Copy != null && line.Grid != null && line.Copy.constraintCount != line.Grid.constraintCount)
                    line.Copy.constraintCount = line.Grid.constraintCount;
            }
        }

        public static void RedrawWaiting()
        {
            var panels = Waiting.ToList();
            Waiting.Clear();
            foreach (ItemSpecificationPanel panel in panels)
            {
                if (panel == null || !panel.isActiveAndEnabled || PanelItem(panel) == null)
                    continue;

                try
                {
                    panel.RecreateAttributeBars();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Could not update the prices in the inspect window: {e}");
                }
            }
        }
    }
}
