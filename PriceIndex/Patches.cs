using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using TMPro;
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
            return PriceInfo.Shows(item, Plugin.InspectIn.Value) && panel.Examined;
        }

        private static ItemAttribute[] Rows(ItemSpecificationPanel panel, Item item)
        {
            PriceInfo info = PriceInfo.For(item, Redraw.OnPrice, true);
            if (info.Loading || info.Refreshing)
                Waiting.Add(panel);

            Color32? tint = Tiers.ForText(info);
            return new[]
            {
                Row(PriceRow.Flea, info.FleaName, Tiers.Wrap(info.FleaValue, tint), info.FleaTip, CurrencyUtil.ROUBLE_ID),
                Row(PriceRow.Trader, info.TraderName, Tiers.Wrap(info.TraderValue, tint), info.TraderTip, info.TraderCurrency)
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
            CloseGap(line, style == InspectStyle.LineAboveStats);

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

        private static void CloseGap(Line line, bool above)
        {
            var window = line.Grid.transform.parent.GetComponent<VerticalLayoutGroup>();
            RectOffset grid = line.Grid.padding;
            int pull = Mathf.RoundToInt(line.Grid.spacing.y - (window != null ? window.spacing : 0f));
            line.Copy.padding = above
                ? new RectOffset(grid.left, grid.right, grid.top, pull - grid.top)
                : new RectOffset(grid.left, grid.right, pull - grid.bottom, grid.bottom);
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

    internal static class TooltipPatches
    {
        private const string Label = "#9a9a8e";
        private const string Value = "#e6e4d8";

        private static GridItemView _showing;

        private static Item _item;
        private static bool _money;
        private static string _name;
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

        private static void TooltipShowPrefix(ref string text, ref float delay)
        {
            GridItemView view = _showing;
            _showing = null;
            if (view == null || text == null)
                return;

            try
            {
                bool money = PriceInfo.ShowsMoney(view.Item, Plugin.HoverIn.Value);
                if (!money && !PriceInfo.Shows(view.Item, Plugin.HoverIn.Value) || !view.Examined
                    || !text.StartsWith(view.GetItemName(), StringComparison.Ordinal))
                    return;

                _item = view.Item;
                _money = money;
                _name = view.GetItemName();
                _baseText = text;
                text = _shownText = Compose();
                delay = 0f;
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

            _shownText = Compose();
            tooltip.SetText(_shownText);
        }

        private static string Compose()
        {
            if (_money)
                return ComposeMoney();

            PriceInfo info = PriceInfo.For(_item, Redraw.OnPrice, true);
            Color32? nameTint = !Tiers.NamesOnHover ? null : Tiers.ShowsBanned(_item) ? Tiers.Banned : Tiers.ForName(info);
            string head = Head(nameTint);

            Color32? tint = Tiers.ForText(info);
            string value = tint != null ? Tiers.Hexcode(tint.Value) : Value;
            return head + new StringBuilder("\n")
                .Append(Colour(Label, info.FleaName + "  ")).Append(Colour(value, info.FleaText)).Append('\n')
                .Append(Colour(Label, info.TraderName + "  ")).Append(Colour(value, info.TraderText))
                .ToString();
        }

        private static string ComposeMoney()
        {
            double? worth = Prices.MoneyRoubles(_item);
            Color32? tint = Tiers.ForMoney(Prices.MoneyColourRoubles(_item));
            string head = Head(Tiers.NamesOnHover ? tint : null);
            if (worth == null)
                return head;

            string value = Tiers.TextOn && tint != null ? Tiers.Hexcode(tint.Value) : Value;
            return head + "\n" + Colour(Label, "Worth  ") + Colour(value, Prices.Money(worth.Value, CurrencyUtil.ROUBLE_ID));
        }

        private static string Head(Color32? tint)
        {
            return tint != null && _baseText.StartsWith(_name, StringComparison.Ordinal)
                ? Tiers.Wrap(_name, tint) + _baseText.Substring(_name.Length)
                : _baseText;
        }

        private static string Colour(string colour, string text)
        {
            return "<color=" + colour + ">" + text + "</color>";
        }
    }

    internal static class IconPatches
    {
        private const string LabelName = "PriceIndexPriceTag";
        private const float Padding = 2f;

        private const double FrameBudget = 2d;

        private static readonly HashSet<ItemView> Waiting = new HashSet<ItemView>();
        private static readonly HashSet<ItemView> Drawn = new HashSet<ItemView>();
        private static readonly Queue<ItemView> Due = new Queue<ItemView>();
        private static readonly HashSet<ItemView> InLine = new HashSet<ItemView>();

        public static void Apply(Harmony harmony)
        {
            var setPanel = AccessTools.DeclaredMethod(typeof(ItemView), nameof(ItemView.SetQuestItemViewPanel))
                           ?? throw new MissingMethodException(nameof(ItemView), nameof(ItemView.SetQuestItemViewPanel));
            harmony.Patch(setPanel, postfix: new HarmonyMethod(typeof(IconPatches), nameof(CellPostfix)));

            var setSearched = AccessTools.DeclaredPropertySetter(typeof(ItemView), nameof(ItemView.IsSearched))
                              ?? throw new MissingMethodException(nameof(ItemView), "set_" + nameof(ItemView.IsSearched));
            harmony.Patch(setSearched, postfix: new HarmonyMethod(typeof(IconPatches), nameof(CellPostfix)));
        }

        private static void CellPostfix(ItemView __instance)
        {
            try
            {
                UpdateCell(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not put a price on an item: {e}");
            }
        }

        private static void UpdateCell(ItemView cell)
        {
            if (cell == null || cell.BottomPanel == null)
                return;

            if (cell is GridItemView)
                Drawn.Add(cell);

            Item item = cell.Item;
            if (ShowsMoney(cell, item))
            {
                double? roubles = Prices.MoneyColourRoubles(item);
                if (roubles == null)
                    Waiting.Add(cell);
                Color32? worth = Tiers.ForMoney(roubles);
                TintCell(cell, Tiers.NamesOnItems ? worth : null, Tiers.ItemBackgroundOn ? worth : null);
                Transform moneyTag = cell.transform.Find(LabelName);
                if (moneyTag != null)
                    moneyTag.gameObject.SetActive(false);
                return;
            }

            bool locked = false;
            bool shows = Shows(cell, item);
            bool tagged = Plugin.IconPrice.Value != IconPrice.Off && shows;
            bool named = shows && Tiers.NamesOnItems;
            bool backed = shows && Tiers.ItemBackgroundOn;
            PriceInfo info = tagged || named || backed ? PriceInfo.For(item, Redraw.OnPrice, false, NeedsFlea(tagged, named, backed)) : null;
            if (info != null && (info.Loading || info.Refreshing))
                Waiting.Add(cell);

            Color32? back = backed ? Tiers.ForItem(info) : null;
            Color32? name = named ? Tiers.ForName(info) : null;
            if (shows && Tiers.ShowsBanned(item))
            {
                back = Tiers.Banned;
                if (named)
                    name = Tiers.Banned;
            }
            TintCell(cell, name, back);

            Color32? tier = null;
            string text = tagged ? Text(info, out locked, out tier) : null;
            if (locked && Tiers.PricesOn)
                tier = Tiers.Blocked;
            if (!tagged && Warms(cell, item))
                Prices.Warm(item);

            Transform existing = cell.transform.Find(LabelName);
            if (string.IsNullOrEmpty(text))
            {
                if (existing != null)
                    existing.gameObject.SetActive(false);
                return;
            }

            Placement tag = existing != null ? existing.GetComponent<Placement>() : Create(cell);
            if (tag == null)
                return;

            tag.Label.text = text;
            Color32 textColour = locked ? LockedColour : TextColour;
            Color background = DefaultBackground;
            bool bold = false;
            if (tier != null)
            {
                bold = true;
                if (Tiers.TagBackgroundOn)
                {
                    background = Tiers.Backing(tier.Value);
                    textColour = LightText;
                }
                else
                {
                    background = TintedTextBackground;
                    textColour = tier.Value;
                }
            }

            tag.Label.fontSize = tag.BaseSize * TextScale * Plugin.TagSize.Value / 100f;
            tag.Label.fontStyle = bold ? tag.BaseStyle | FontStyles.Bold : tag.BaseStyle;
            tag.Label.color = textColour;
            tag.Tag.color = background;
            tag.gameObject.SetActive(true);
            tag.transform.SetAsLastSibling();
            tag.Follow();
        }

        private static bool NeedsFlea(bool tagged, bool named, bool backed)
        {
            bool tag = tagged && Plugin.IconPrice.Value != IconPrice.Trader;
            bool colour = (named || backed || tagged && Tiers.PricesOn) && Plugin.ColourSource.Value != ColourBy.Trader;
            return tag || colour;
        }

        private static bool Shows(ItemView cell, Item item)
        {
            return cell is GridItemView && cell.IsSearched && cell.Examined && PriceInfo.Shows(item, Plugin.IconIn.Value)
                   && cell.ItemContext != null && cell.ItemContext.HasViewComponent(EItemViewComponent.QuestItem);
        }

        private static bool ShowsMoney(ItemView cell, Item item)
        {
            return cell is GridItemView && cell.IsSearched && cell.Examined && PriceInfo.ShowsMoney(item, Plugin.IconIn.Value);
        }

        private static bool Warms(ItemView cell, Item item)
        {
            return cell is GridItemView && cell.IsSearched && cell.Examined
                   && (PriceInfo.Shows(item, Plugin.HoverIn.Value)
                       || Plugin.Inspect.Value != InspectStyle.Off && PriceInfo.Shows(item, Plugin.InspectIn.Value));
        }

        private static void TintCell(ItemView cell, Color32? name, Color32? back)
        {
            CellTint holder = cell.GetComponent<CellTint>();
            if (holder == null)
            {
                if (name == null && back == null)
                    return;
                holder = cell.gameObject.AddComponent<CellTint>();
                holder.View = cell;
                holder.Caption = (cell as GridItemView)?.Caption;
                holder.Panel = cell.ColorPanel;
            }

            holder.Name = name;
            holder.Back = back;
            holder.Apply();
        }

        private class CellTint : MonoBehaviour
        {
            private static readonly AccessTools.FieldRef<ItemView, Color> BackgroundColour =
                AccessTools.FieldRefAccess<ItemView, Color>("BackgroundColor");

            private static readonly AccessTools.FieldRef<ItemView, Color> OriginalBackgroundColour =
                AccessTools.FieldRefAccess<ItemView, Color>("OriginalBackgroundColor");

            private static readonly AccessTools.FieldRef<ItemView, bool> Highlighted =
                AccessTools.FieldRefAccess<ItemView, bool>("Highlighted");

            private static readonly AccessTools.FieldRef<ItemView, bool> HighlightedGlobally =
                AccessTools.FieldRefAccess<ItemView, bool>("HighlightedGlobally");

            public ItemView View;
            public TextMeshProUGUI Caption;
            public Image Panel;
            public Color32? Name;
            public Color32? Back;

            private string shownName;
            private Color32? shownTint;
            private bool backApplied;
            private Color backSet;
            private Color backOriginal;

            private void LateUpdate()
            {
                Apply();
            }

            private static string Bare(string text)
            {
                const string open = "<color=#";
                const string close = "</color>";
                const int openLength = 15;
                if (text.Length >= openLength + close.Length && text.StartsWith(open, StringComparison.Ordinal)
                    && text[openLength - 1] == '>' && text.EndsWith(close, StringComparison.Ordinal))
                    return text.Substring(openLength, text.Length - openLength - close.Length);
                return text;
            }

            public void Apply()
            {
                if (Caption != null)
                {
                    string current = Caption.text ?? string.Empty;
                    if (current != shownName || !Equals(Name, shownTint))
                    {
                        string bare = Bare(current);
                        string wanted = Name != null && bare.Length > 0
                            ? "<color=" + Tiers.Hexcode(Name.Value) + ">" + bare + "</color>"
                            : bare;
                        if (current != wanted)
                            Caption.text = wanted;
                        shownName = wanted;
                        shownTint = Name;
                    }
                }

                if (View == null)
                    View = GetComponent<ItemView>();
                if (View == null)
                    return;

                // Drive ItemView.BackgroundColor and let UpdateColor() pick SelectedColor when the
                // game is hovering or showing accept/merge highlights. Writing ColorPanel directly
                // every frame was wiping those highlights.
                if (Back != null)
                {
                    if (!backApplied)
                        backOriginal = OriginalBackgroundColour(View);

                    Color desired = Tiers.Panel(Back.Value);
                    desired.a = Mathf.Max(backOriginal.a, 0.85f);
                    backApplied = true;
                    backSet = desired;

                    if (BackgroundColour(View) != desired)
                    {
                        BackgroundColour(View) = desired;
                        View.UpdateColor();
                    }
                    else if (!Highlighted(View) && !HighlightedGlobally(View)
                             && Panel != null && Panel.color != desired)
                    {
                        View.UpdateColor();
                    }
                }
                else if (backApplied)
                {
                    backApplied = false;
                    BackgroundColour(View) = backOriginal;
                    View.UpdateColor();
                }
            }
        }

        private static string Text(PriceInfo info, out bool locked, out Color32? tier)
        {
            locked = false;
            tier = Tiers.ForTag(info);

            double? roubles;
            switch (Plugin.IconPrice.Value)
            {
                case IconPrice.Flea:
                    roubles = info.FleaTotal;
                    locked = info.FleaLocked;
                    break;
                case IconPrice.Trader:
                    roubles = info.TraderRoubles;
                    break;
                default:
                    if (info.Loading)
                        return null;
                    double? flea = info.FleaLocked ? null : info.FleaTotal;
                    if (flea != null || info.TraderRoubles != null)
                    {
                        roubles = Math.Max(flea ?? 0, info.TraderRoubles ?? 0);
                    }
                    else
                    {
                        roubles = info.FleaTotal;
                        locked = info.FleaLocked && info.FleaTotal != null;
                    }
                    break;
            }

            if (roubles != null && Plugin.Stacks.Value == StackPrice.Single)
                roubles /= info.Count;

            return roubles > 0 ? Short(roubles.Value) : null;
        }

        private static string Short(double roubles)
        {
            if (roubles < 1000)
                return "<1k";
            if (roubles < 10000)
                return (roubles / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            if (roubles < 1000000)
                return Math.Round(roubles / 1000).ToString("0") + "k";
            return (roubles / 1000000).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        private static Placement Create(ItemView cell)
        {
            TextMeshProUGUI count = cell.BottomPanel.ItemValue;
            if (count == null)
                return null;

            var tag = new GameObject(LabelName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)tag.transform;
            rect.SetParent(cell.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(Padding, Padding);

            var image = tag.GetComponent<Image>();
            image.sprite = TagSprite();
            image.type = Image.Type.Sliced;
            image.color = DefaultBackground;
            image.raycastTarget = false;

            TextMeshProUGUI label = UnityEngine.Object.Instantiate(count, rect, false);
            label.name = "Price";
            label.gameObject.SetActive(true);
            label.enabled = true;
            label.alpha = 1f;
            label.maxVisibleCharacters = 99999;
            label.canvasRenderer.SetAlpha(1f);
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            label.enableAutoSizing = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.fontSize = count.fontSize * TextScale;
            label.margin = Vector4.zero;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = TextColour;
            foreach (Component extra in label.GetComponents<Component>()
                         .Where(c => !(c is RectTransform) && !(c is TextMeshProUGUI) && !(c is CanvasRenderer)))
                UnityEngine.Object.Destroy(extra);

            var text = label.rectTransform;
            text.anchorMin = Vector2.zero;
            text.anchorMax = Vector2.one;
            text.pivot = new Vector2(0f, 0.5f);

            var placement = tag.AddComponent<Placement>();
            placement.Tag = image;
            placement.Label = label;
            placement.Cell = cell;
            placement.BaseStyle = label.fontStyle;
            placement.BaseSize = count.fontSize;
            return placement;
        }

        private static readonly Color32 TextColour = new Color32(0xc5, 0xc3, 0xb2, 0xff);
        private static readonly Color DefaultBackground = new Color(0f, 0f, 0f, 0.6f);
        private static readonly Color TintedTextBackground = new Color(0f, 0f, 0f, 0.82f);
        private static readonly Color32 LightText = new Color32(0xf2, 0xf0, 0xe4, 0xff);
        private static readonly Color32 LockedColour =new Color32(0xe0, 0x8a, 0x4a, 0xff);

        private const int TagHeight = 32;
        private const int TagPoint = 12;
        private const int TagWidth = TagPoint + 24;
        private const int TagLeft = TagPoint + 5;
        private const float TextScale = 0.7f;
        private const float TagToText = 1.45f;
        private const int HoleGap = 5;
        private static Sprite tagSprite;

        private static Sprite TagSprite()
        {
            if (tagSprite != null)
                return tagSprite;

            const int samples = 4;
            var pixels = new Color32[TagWidth * TagHeight];
            for (int y = 0; y < TagHeight; y++)
            {
                for (int x = 0; x < TagWidth; x++)
                {
                    int inside = 0;
                    for (int sy = 0; sy < samples; sy++)
                    for (int sx = 0; sx < samples; sx++)
                    {
                        if (InTag(x + (sx + 0.5f) / samples, y + (sy + 0.5f) / samples))
                            inside++;
                    }
                    pixels[y * TagWidth + x] = new Color32(255, 255, 255, (byte)(255 * inside / (samples * samples)));
                }
            }

            var texture = new Texture2D(TagWidth, TagHeight, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            tagSprite = Sprite.Create(texture, new Rect(0, 0, TagWidth, TagHeight), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(TagLeft, 8, 8, 8));
            return tagSprite;
        }

        private static bool InTag(float x, float y)
        {
            const float corner = 6f;
            const float hole = 3f;
            float middle = TagHeight / 2f;

            if (x < TagPoint && Mathf.Abs(y - middle) > middle * x / TagPoint)
                return false;

            float cornerX = TagWidth - corner;
            if (x > cornerX && (y < corner || y > TagHeight - corner))
            {
                float cornerY = y < corner ? corner : TagHeight - corner;
                if ((x - cornerX) * (x - cornerX) + (y - cornerY) * (y - cornerY) > corner * corner)
                    return false;
            }

            return (x - TagPoint) * (x - TagPoint) + (y - middle) * (y - middle) > hole * hole;
        }

        private class Placement : MonoBehaviour
        {
            private const float Gap = 2f;
            private const float RightPad = 3f;
            private const int SettleFrames = 5;
            private static readonly Vector3[] Corners = new Vector3[4];
            private static readonly List<Rect> Obstacles = new List<Rect>();
            private static readonly List<Graphic> Graphics = new List<Graphic>();

            public Image Tag;
            public TextMeshProUGUI Label;
            public ItemView Cell;
            public FontStyles BaseStyle;
            public float BaseSize;

            private int framesLeft;

            private void LateUpdate()
            {
                Layout();
                if (--framesLeft <= 0)
                    enabled = false;
            }

            public void Follow()
            {
                framesLeft = SettleFrames;
                enabled = true;
                Layout();
            }

            private void Layout()
            {
                if (Tag == null || Label == null || Cell == null)
                    return;

                float zoom = Plugin.TagSize.Value / 100f;
                float height = BaseSize * TextScale * TagToText * zoom;
                float scale = TagHeight / height;
                float left = (TagLeft + HoleGap) / scale;
                float right = RightPad * zoom;
                Tag.pixelsPerUnitMultiplier = scale;
                Label.rectTransform.offsetMin = new Vector2(left, 0f);
                Label.rectTransform.offsetMax = new Vector2(-right, 0f);
                Vector2 size = new Vector2(left + Label.preferredWidth + right, height);
                var rect = (RectTransform)transform;
                rect.sizeDelta = size;

                var cellRect = (RectTransform)Cell.transform;

                TextMeshProUGUI mastery = Cell is SlotItemView slot ? slot._masteringLabel : null;
                if (Showing(mastery) && TextBox(mastery, cellRect, out Rect masteryBox))
                {
                    Place(masteryBox.xMax + Gap * 3, masteryBox.yMin);
                    return;
                }

                if (Plugin.IconPosition.Value == IconPosition.MiddleLeft)
                {
                    Place(Padding, (cellRect.rect.height - size.y) / 2f);
                    return;
                }

                var obstacles = Obstacles;
                obstacles.Clear();
                AddText(obstacles, Cell.BottomPanel.ItemValue, cellRect);
                AddText(obstacles, Cell.BottomPanel.ItemInscription, cellRect);
                AddBox(obstacles, Cell.BottomPanel.InsuredIcon != null ? Cell.BottomPanel.InsuredIcon.transform as RectTransform : null, cellRect);
                AddBox(obstacles, Cell.BottomPanel.RepairBuffIcon != null ? Cell.BottomPanel.RepairBuffIcon.rectTransform : null, cellRect);
                AddQuestIcon(obstacles, cellRect);
                AddBox(obstacles, Cell is SlotItemView gun && gun._durabilitySlider != null ? gun._durabilitySlider.transform as RectTransform : null, cellRect);
                AddBox(obstacles, Cell.transform.Find("BetterFleaMarker") as RectTransform, cellRect);
                AddGraphics(obstacles, Cell.BottomPanel.ItemViewStats, cellRect);

                float y = Padding;
                for (bool moved = true; moved;)
                {
                    moved = false;
                    foreach (Rect box in obstacles)
                    {
                        if (box.xMin < Padding + size.x && box.xMax > Padding && box.yMin < y + size.y && box.yMax > y)
                        {
                            y = box.yMax + Gap;
                            moved = true;
                        }
                    }
                }

                Place(Padding, y);
            }

            private void Place(float x, float y)
            {
                ((RectTransform)transform).anchoredPosition = new Vector2(x, y);
            }

            private static void AddGraphics(List<Rect> obstacles, Component owner, RectTransform cellRect)
            {
                if (owner == null || !owner.gameObject.activeInHierarchy)
                    return;

                Rect cell = cellRect.rect;
                owner.GetComponentsInChildren(Graphics);
                foreach (Graphic graphic in Graphics)
                {
                    if (!graphic.enabled || graphic.color.a <= 0.01f)
                        continue;

                    int before = obstacles.Count;
                    if (graphic is TextMeshProUGUI label)
                        AddText(obstacles, label, cellRect);
                    else
                        AddBox(obstacles, graphic.rectTransform, cellRect);
                    if (obstacles.Count > before)
                    {
                        Rect box = obstacles[before];
                        if (box.width >= cell.width / 2 && box.height >= cell.height / 2)
                            obstacles.RemoveAt(before);
                    }
                }
                Graphics.Clear();
            }

            private void AddQuestIcon(List<Rect> obstacles, RectTransform cellRect)
            {
                var panel = Cell.BottomPanel.QuestsItemViewPanel;
                if (panel != null && panel.gameObject.activeInHierarchy && panel._questIconImage != null)
                    AddBox(obstacles, panel._questIconImage.rectTransform, cellRect);
            }

            private static void AddText(List<Rect> obstacles, TextMeshProUGUI text, RectTransform cellRect)
            {
                if (Showing(text) && TextBox(text, cellRect, out Rect box))
                    obstacles.Add(box);
            }

            private static void AddBox(List<Rect> obstacles, RectTransform target, RectTransform cellRect)
            {
                if (target == null || !target.gameObject.activeInHierarchy)
                    return;

                target.GetWorldCorners(Corners);
                obstacles.Add(Between(cellRect, Corners[0], Corners[2]));
            }

            private static bool Showing(TextMeshProUGUI text)
            {
                return text != null && text.gameObject.activeInHierarchy && !string.IsNullOrEmpty(text.text);
            }

            private static bool TextBox(TextMeshProUGUI text, RectTransform cellRect, out Rect box)
            {
                Bounds bounds = text.textBounds;
                box = Between(cellRect,
                    text.rectTransform.TransformPoint(bounds.min),
                    text.rectTransform.TransformPoint(bounds.max));
                return box.width > 0 && box.height > 0;
            }

            private static Rect Between(RectTransform cellRect, Vector3 worldLow, Vector3 worldHigh)
            {
                Vector2 origin = cellRect.rect.min;
                Vector2 low = (Vector2)cellRect.InverseTransformPoint(worldLow) - origin;
                Vector2 high = (Vector2)cellRect.InverseTransformPoint(worldHigh) - origin;
                return Rect.MinMaxRect(Mathf.Min(low.x, high.x), Mathf.Min(low.y, high.y), Mathf.Max(low.x, high.x), Mathf.Max(low.y, high.y));
            }
        }

        public static void RedrawWaiting()
        {
            foreach (ItemView cell in Waiting)
                Line(cell);
            Waiting.Clear();
        }

        public static void RedrawAll()
        {
            Drawn.RemoveWhere(cell => cell == null);
            foreach (ItemView cell in Drawn)
            {
                if (cell.isActiveAndEnabled)
                    Line(cell);
            }
        }

        public static void Update()
        {
            if (Due.Count == 0)
                return;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (Due.Count > 0 && watch.Elapsed.TotalMilliseconds < FrameBudget)
            {
                ItemView cell = Due.Dequeue();
                InLine.Remove(cell);
                if (cell != null && cell.Item != null)
                    CellPostfix(cell);
            }
        }

        private static void Line(ItemView cell)
        {
            if (cell != null && InLine.Add(cell))
                Due.Enqueue(cell);
        }
    }
}
