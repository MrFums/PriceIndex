using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PriceIndex
{
    public enum InspectStyle
    {
        Off,
        StatsGrid,
        LineUnderStats,
        LineAboveStats
    }

    public enum IconPrice
    {
        Off,
        Flea,
        Trader,
        Most
    }

    public enum IconPosition
    {
        MiddleLeft,
        BottomLeft
    }

    public enum StackPrice
    {
        SingleAndStack,
        Single,
        Stack
    }

    [Flags]
    public enum ShowIn
    {
        None = 0,
        Stash = 1,
        Raid = 2
    }

    public enum ColourBy
    {
        Most,
        Flea,
        Trader
    }

    public enum PriceColour
    {
        Off,
        Text,
        TagBackground
    }

    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.fums.priceindex";
        public const string Name = "Fums-PriceIndex";
        public const string Version = "1.2.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<InspectStyle> Inspect;
        internal static ConfigEntry<ShowIn> InspectIn;
        internal static ConfigEntry<ShowIn> HoverIn;
        internal static ConfigEntry<IconPrice> IconPrice;
        internal static ConfigEntry<ShowIn> IconIn;
        internal static ConfigEntry<StackPrice> Stacks;
        internal static ConfigEntry<IconPosition> IconPosition;
        internal static ConfigEntry<bool> IncludeFence;
        internal static ConfigEntry<int> RefreshSeconds;
        internal static ConfigEntry<ColourBy> ColourSource;
        internal static ConfigEntry<Color> ColourBlocked;
        internal static ConfigEntry<bool> ColourEnabled;
        internal static ConfigEntry<bool> ColourNames;
        internal static ConfigEntry<PriceColour> ColourPrices;
        internal static ConfigEntry<bool> ColourItems;
        internal static ConfigEntry<int> ColourStrength;
        internal static readonly ConfigEntry<int>[] TierMin = new ConfigEntry<int>[Tiers.Count];
        internal static readonly ConfigEntry<Color>[] TierColour = new ConfigEntry<Color>[Tiers.Count];

        private static readonly int[] TierMinDefaults = { 0, 15000, 50000, 150000, 400000 };
        private static readonly Color[] TierColourDefaults =
        {
            new Color32(0xb4, 0xba, 0xbd, 0xff),
            new Color32(0x6f, 0xbf, 0x73, 0xff),
            new Color32(0x4d, 0xa3, 0xff, 0xff),
            new Color32(0xb0, 0x7c, 0xff, 0xff),
            new Color32(0xff, 0xc2, 0x33, 0xff)
        };

        private const string WhereText = "Stash: out of raid (stash, traders, hideout). Raid: in a raid. "
                                         + "Tick both for everywhere, neither to turn it off.";

        private bool _patched;

        private void Awake()
        {
            Log = Logger;

            Inspect = Config.Bind("1. Display", "Inspect window", InspectStyle.LineUnderStats,
                "Where the flea and trader prices go in the item's inspect window. "
                + "StatsGrid: in with the item's stats (where they land depends on how many stats it has). "
                + "LineUnderStats / LineAboveStats: a line of their own, always in the same place. "
                + "Takes effect the next time a window opens.");
            InspectIn = Config.Bind("1. Display", "Inspect window shows in", ShowIn.Stash | ShowIn.Raid,
                "Where the prices show in the inspect window. " + WhereText);
            HoverIn = Config.Bind("1. Display", "Hover tooltip shows in", ShowIn.Stash | ShowIn.Raid,
                "Where the prices show under the item's name when hovering it. "
                + "The tooltip then opens straight away instead of after the game's short delay. " + WhereText);
            IconPrice = Config.Bind("1. Display", "Price on items", PriceIndex.IconPrice.Most,
                "A short price in the corner of your own items in the stash and inventory. "
                + "Flea: the flea price. Trader: the best trader's. "
                + "Most: whichever pays more, the flea or the best trader. "
                + "A flea price you can't get yet shows in orange: your flea level is too low, it isn't found in raid, "
                + "or with Better Flea it's banned, above your unlock level or bought from a trader. "
                + "Until then, only traders will buy it.");
            IconIn = Config.Bind("1. Display", "Price on items shows in", ShowIn.Stash | ShowIn.Raid,
                "Where the price on items shows. " + WhereText);
            IconPosition = Config.Bind("1. Display", "Price position on items", PriceIndex.IconPosition.BottomLeft,
                "Where the price on items goes. MiddleLeft: halfway up the left edge, the same place on every item. "
                + "BottomLeft: the bottom left corner, with the game's own icons there moved up above it.");
            Stacks = Config.Bind("1. Display", "Stacks", StackPrice.SingleAndStack,
                "How a stack is priced. SingleAndStack: one item, then the whole stack in brackets. "
                + "Single: one item. Stack: the whole stack. Prices on items show one of the two "
                + "(the whole stack unless set to Single).");
            IncludeFence = Config.Bind("2. General", "Include Fence", true,
                "Count Fence when looking for the trader who pays the most.");
            RefreshSeconds = Config.Bind("3. Advanced", "Flea price refresh (seconds)", 120,
                new ConfigDescription("How long a flea price is kept before it is asked for again. "
                                      + "Better Flea's prices drift over time, so they're asked for again now and then.",
                    new AcceptableValueRange<int>(10, 1800)));

            ColourEnabled = Config.Bind("4. Colours", "Colours", true,
                "Colour things by how much the item is worth, using the tiers below. Untick to turn all colours off.");
            ColourNames = Config.Bind("4. Colours", "Colour item names", true,
                "Colour the item's name on the item in the stash and inventory, and in the hover tooltip.");
            ColourPrices = Config.Bind("4. Colours", "Colour prices", PriceColour.Text,
                "How the price is coloured. Off: not at all. Text: the price text in the hover tooltip, the inspect window and on items. "
                + "TagBackground: the price on items gets a coloured background (its text turns light to stay readable), "
                + "while the tooltip and inspect window keep their normal colours.");
            ColourItems = Config.Bind("4. Colours", "Colour item backgrounds", false,
                "Colour the background of the item itself, in the stash and inventory. It works alongside the other colour settings.");
            ColourStrength = Config.Bind("4. Colours", "Item background strength", 35,
                new ConfigDescription("How bright an item's background colour is, as a percentage of the tier colour. Lower is darker and softer.",
                    new AcceptableValueRange<int>(10, 100)));
            ColourSource = Config.Bind("4. Colours", "Colours based on", ColourBy.Most,
                "Which price picks the tier. Most: whichever pays more, the flea or the best trader. "
                + "Flea: the flea price. Trader: the best trader's.");
            ColourBlocked = Config.Bind("4. Colours", "Unsellable colour", (Color)new Color32(0xff, 0x52, 0x52, 0xff),
                "The colour for an item that can't be sold. With Flea: the flea has banned it. With Trader: no trader buys it. "
                + "With Most: the flea has banned it and no trader buys it either. Click the colour to pick one.");
            ColourBlocked.SettingChanged += (_, __) => ColoursChanged();
            for (int i = 0; i < Tiers.Count; i++)
            {
                TierMin[i] = Config.Bind("4. Colours", $"Tier {i + 1} min price", TierMinDefaults[i],
                    new ConfigDescription(i == 0
                            ? "Roubles an item must be worth to get this tier's colour. Items worth less than tier 1 are left uncoloured, so keep it at 0 to colour everything. "
                              + "A stack counts as a whole."
                            : "Roubles an item must be worth to get this tier's colour instead of the one below.",
                        new AcceptableValueRange<int>(0, 100000000)));
                TierColour[i] = Config.Bind("4. Colours", $"Tier {i + 1} colour", TierColourDefaults[i],
                    "The colour for this tier. Click the colour to pick one. Only the colour is used, not its opacity.");
                TierMin[i].SettingChanged += (_, __) => ColoursChanged();
                TierColour[i].SettingChanged += (_, __) => ColoursChanged();
            }

            ColourEnabled.SettingChanged += (_, __) => ColoursChanged();
            ColourNames.SettingChanged += (_, __) => ColoursChanged();
            ColourPrices.SettingChanged += (_, __) => ColoursChanged();
            ColourItems.SettingChanged += (_, __) => ColoursChanged();
            ColourStrength.SettingChanged += (_, __) => ColoursChanged();
            ColourSource.SettingChanged += (_, __) => ColoursChanged();

            var harmony = new Harmony(Guid);
            try
            {
                InspectPatches.Apply(harmony);
                TooltipPatches.Apply(harmony);
                IconPatches.Apply(harmony);
                _patched = true;
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();
                Logger.LogError($"{Name} {Version} could not patch the game, nothing will be shown: {e}");
                return;
            }

            IconPrice.SettingChanged += (_, __) => IconPatches.RedrawAll();
            Stacks.SettingChanged += (_, __) => IconPatches.RedrawAll();
            IconPosition.SettingChanged += (_, __) => IconPatches.RedrawAll();
            IconIn.SettingChanged += (_, __) => IconPatches.RedrawAll();
            Logger.LogInfo($"{Name} {Version} loaded.");
        }

        private void ColoursChanged()
        {
            Tiers.Invalidate();
            if (_patched)
                IconPatches.RedrawAll();
        }

        private void Update()
        {
            if (!_patched)
                return;

            Prices.Update();
            InspectPatches.Update();
            if (Redraw.Due())
            {
                InspectPatches.RedrawWaiting();
                TooltipPatches.RedrawWaiting();
                IconPatches.RedrawWaiting();
            }

            if (Redraw.SweepDue())
            {
                IconPatches.RedrawAll();
                TooltipPatches.RedrawWaiting();
            }
        }
    }
}
