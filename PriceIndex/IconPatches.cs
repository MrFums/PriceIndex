using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace PriceIndex
{
    internal static class IconPatches
    {
        private const string LabelName = "PriceIndexPriceTag";
        private const float Padding = 2f;

        private static readonly HashSet<ItemView> Waiting = new HashSet<ItemView>();

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

            Item item = cell.Item;
            string text = Plugin.IconPrice.Value != IconPrice.Off && Shows(cell, item) ? Text(cell, item) : null;

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
            tag.gameObject.SetActive(true);
            tag.transform.SetAsLastSibling();
            tag.Follow();
        }

        private static bool Shows(ItemView cell, Item item)
        {
            if (!(cell is GridItemView) || !PriceInfo.Shows(item) || !cell.IsSearched || !cell.Examined)
                return false;
            if (cell.ItemContext == null || !cell.ItemContext.HasViewComponent(EItemViewComponent.QuestItem))
                return false;

            bool inRaid = Singleton<GameWorld>.Instantiated && !(Singleton<GameWorld>.Instance is HideoutGameWorld);
            return Plugin.InRaid.Value || !inRaid;
        }

        private static string Text(ItemView cell, Item item)
        {
            PriceInfo info = PriceInfo.For(item, Redraw.OnPrice);
            if (info.Loading || info.Refreshing)
                Waiting.Add(cell);

            double? roubles;
            switch (Plugin.IconPrice.Value)
            {
                case IconPrice.Flea:
                    roubles = info.FleaTotal;
                    break;
                case IconPrice.Trader:
                    roubles = info.TraderRoubles;
                    break;
                default:
                    roubles = info.FleaTotal != null || info.TraderRoubles != null
                        ? Math.Max(info.FleaTotal ?? 0, info.TraderRoubles ?? 0)
                        : (double?)null;
                    break;
            }

            bool traders = !(item.Owner is InventoryController);
            if (roubles != null && (Plugin.Stacks.Value == StackPrice.Single || traders))
                roubles /= info.Count;

            return roubles > 0 ? Short(roubles.Value) : null;
        }

        private static string Short(double roubles)
        {
            if (roubles < 1000)
                return "<1k";
            if (roubles < 10000)
                return (roubles / 1000).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k";
            if (roubles < 1000000)
                return Math.Round(roubles / 1000).ToString("0") + "k";
            return (roubles / 1000000).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
        }

        private static Placement Create(ItemView cell)
        {
            TextMeshProUGUI count = cell.BottomPanel.ItemValue;
            if (count == null)
                return null;

            var tag = new GameObject(LabelName, typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
            var rect = (RectTransform)tag.transform;
            rect.SetParent(cell.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(Padding, Padding);

            var image = tag.GetComponent<UnityEngine.UI.Image>();
            image.sprite = TagSprite();
            image.type = UnityEngine.UI.Image.Type.Sliced;
            image.color = new Color(0f, 0f, 0f, 0.6f);
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
            label.color = new Color32(0xc5, 0xc3, 0xb2, 0xff);
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
            return placement;
        }

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
            private static readonly List<UnityEngine.UI.Graphic> Graphics = new List<UnityEngine.UI.Graphic>();

            public UnityEngine.UI.Image Tag;
            public TextMeshProUGUI Label;
            public ItemView Cell;

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

                float height = Label.fontSize * TagToText;
                float scale = TagHeight / height;
                float left = (TagLeft + HoleGap) / scale;
                Tag.pixelsPerUnitMultiplier = scale;
                Label.rectTransform.offsetMin = new Vector2(left, 0f);
                Label.rectTransform.offsetMax = new Vector2(-RightPad, 0f);
                Vector2 size = new Vector2(left + Label.preferredWidth + RightPad, height);
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
                QuestItemViewPanelIcon(obstacles, cellRect);
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
                foreach (UnityEngine.UI.Graphic graphic in Graphics)
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

            private void QuestItemViewPanelIcon(List<Rect> obstacles, RectTransform cellRect)
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
            var cells = Waiting.ToList();
            Waiting.Clear();
            foreach (ItemView cell in cells)
            {
                if (cell != null && cell.Item != null)
                    CellPostfix(cell);
            }
        }

        public static void RedrawAll()
        {
            foreach (ItemView cell in UnityEngine.Object.FindObjectsOfType<GridItemView>())
                CellPostfix(cell);
        }
    }
}
