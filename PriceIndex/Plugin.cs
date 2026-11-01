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
        TagBackground,
        TagBackgroundAndText
    }

    public enum NameColour
    {
        Off,
        OnItems,
        OnHover,
        Both
    }

    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.fums.priceindex";
        public const string Name = "Fums-PriceIndex";
        public const string Version = "1.2.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<InspectStyle> Inspect;
        internal static ConfigEntry<ShowIn> InspectIn;
        internal static ConfigEntry<ShowIn> HoverIn;
        internal static ConfigEntry<IconPrice> IconPrice;
        internal static ConfigEntry<ShowIn> IconIn;
        internal static ConfigEntry<StackPrice> Stacks;
        internal static ConfigEntry<int> TagSize;
        internal static ConfigEntry<IconPosition> IconPosition;
        internal static ConfigEntry<bool> IncludeFence;
        internal static ConfigEntry<int> RefreshSeconds;
        internal static ConfigEntry<ColourBy> ColourSource;
        internal static ConfigEntry<Color> ColourBlocked;
        internal static ConfigEntry<bool> ColourEnabled;
        internal static ConfigEntry<NameColour> ColourNames;
        internal static ConfigEntry<PriceColour> ColourPrices;
        internal static ConfigEntry<bool> ColourItems;
        internal static ConfigEntry<int> ColourStrength;
        internal static ConfigEntry<bool> ColourFullStacks;
        internal static ConfigEntry<bool> ColourFullMags;
        internal static ConfigEntry<bool> ColourFullMoney;
        internal static ConfigEntry<bool> ColourBannedAmmo;
        internal static ConfigEntry<bool> HighlightBanned;
        internal static ConfigEntry<Color> BannedColour;
        internal static readonly ConfigEntry<int>[] TierMin = new ConfigEntry<int>[Tiers.Count];
        internal static readonly ConfigEntry<Color>[] TierColour = new ConfigEntry<Color>[Tiers.Count];

        private static readonly int[] TierMinDefaults = { 0, 20000, 50000, 100000, 250000 };
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
                + "Most: whichever pays more of the ones you can sell to, the flea or the best trader. "
                + "A flea price you can't get yet shows in orange: your flea level is too low, it isn't found in raid, "
                + "or with Better Flea it's banned, above your unlock level or bought from a trader. "
                + "Most skips it and shows the trader's price, unless no trader buys it.");
            IconIn = Config.Bind("1. Display", "Price on items shows in", ShowIn.Stash | ShowIn.Raid,
                "Where the price on items shows. " + WhereText);
            IconPosition = Config.Bind("1. Display", "Price position on items", PriceIndex.IconPosition.BottomLeft,
                "Where the price on items goes. MiddleLeft: halfway up the left edge, the same place on every item. "
                + "BottomLeft: the bottom left corner, with the game's own icons there moved up above it.");
            Stacks = Config.Bind("1. Display", "Stacks", StackPrice.SingleAndStack,
                "How a stack is priced. SingleAndStack: one item, then the whole stack in brackets. "
                + "Single: one item. Stack: the whole stack. Prices on items show one of the two "
                + "(the whole stack unless set to Single).");
            TagSize = Config.Bind("1. Display", "Price tag size on items", 100,
                new ConfigDescription("How big the price tag on items is, text and all, as a percentage of the normal size.",
                    new AcceptableValueRange<int>(80, 150)));
            TagSize.SettingChanged += (_, __) => IconPatches.RedrawAll();
            IncludeFence = Config.Bind("2. General", "Include Fence", true,
                "Count Fence when looking for the trader who pays the most.");
            RefreshSeconds = Config.Bind("3. Advanced", "Flea price refresh (seconds)", 120,
                new ConfigDescription("How long a flea price is kept before it is asked for again. "
                                      + "Better Flea's prices drift over time, so they're asked for again now and then.",
                    new AcceptableValueRange<int>(10, 1800)));

            const string colours = "4. Colours";
            ColourEnabled = Config.Bind(colours, "Colours", true,
                Describe("Colour things by how much the item is worth, using the tiers below. Untick to turn all colours off.", 100));
            ColourSource = Config.Bind(colours, "Colours based on", ColourBy.Most,
                Describe("Which price picks the tier. Most: whichever pays more, the flea or the best trader. "
                         + "Flea: the flea price. Trader: the best trader's.", 99));
            ColourNames = Config.Bind(colours, "Colour item names", NameColour.Both,
                Describe("Colour the item's name. OnItems: on the item in the stash and inventory. OnHover: in the hover tooltip. "
                         + "Both: in both places. Money is coloured by how much the stack is worth.", 98));
            ColourPrices = Config.Bind(colours, "Colour prices", PriceColour.Off,
                Describe("How the price is coloured. Off: not at all. Text: the price text in the hover tooltip, the inspect window and on items. "
                         + "TagBackground: the price on items gets a coloured background (its text turns light to stay readable), "
                         + "while the tooltip and inspect window keep their normal colours. "
                         + "TagBackgroundAndText: the coloured background on items, and coloured price text in the tooltip and inspect window.", 97));
            ColourItems = Config.Bind(colours, "Colour item backgrounds", true,
                Describe("Colour the background of the item itself, in the stash and inventory. It works alongside the other colour settings.", 96));
            ColourStrength = Config.Bind(colours, "Item background strength", 25,
                Describe("How bright an item's background colour is, as a percentage of the tier colour. Lower is darker and softer.", 95,
                    new AcceptableValueRange<int>(10, 100)));
            ColourFullStacks = Config.Bind(colours, "Colour stacks by type", true,
                Describe("Colour ammo by what 100 rounds are worth, and other stacks by what a full stack is worth, "
                         + "so the colour stays the same as the stack gets used up and ammo spreads across the tiers. "
                         + "Untick to colour by the stack you have.", 94));
            ColourFullMags = Config.Bind(colours, "Colour mags as fully loaded", true,
                Describe("Colour mags, and guns with mags in them, as if the mags were full of the ammo already in them, "
                         + "so firing doesn't change the colour. An empty mag is coloured as an empty mag. Untick to colour by the rounds in it.", 93));
            ColourFullMoney = Config.Bind(colours, "Colour money as full stack", true,
                Describe("Colour roubles, dollars and euros by what a full stack is worth, so each currency always has the same colour. "
                         + "Untick to colour by how much the stack holds.", 92));
            ColourBannedAmmo = Config.Bind(colours, "Colour banned ammo by flea price", true,
                Describe("Colour ammo by its flea price even when the flea has banned or locked it, so the colour shows how good the ammo is. "
                         + "The price on the item still shows what you can sell it for. Untick to colour banned ammo like other banned items.", 91));
            for (int i = 0; i < Tiers.Count; i++)
            {
                TierColour[i] = Config.Bind(colours, $"Tier {i + 1} colour", TierColourDefaults[i],
                    Describe("The colour for this tier. Click the colour to open its sliders, or type a hex code such as #4da3ff.",
                        90 - i * 2, drawer: ColourField.Draw));
                TierMin[i] = Config.Bind(colours, $"Tier {i + 1} from (roubles)", TierMinDefaults[i],
                    Describe(i == 0
                            ? "Items worth at least this many roubles get tier 1's colour. Keep it at 0 so every item gets a colour. A stack counts as a whole."
                            : $"Items worth at least this many roubles get tier {i + 1}'s colour instead of tier {i}'s. A stack counts as a whole.",
                        89 - i * 2));
                TierMin[i].SettingChanged += (_, __) => ColoursChanged();
                TierColour[i].SettingChanged += (_, __) => ColoursChanged();
            }

            ColourBlocked = Config.Bind(colours, "Unsellable colour", (Color)new Color32(0xb4, 0x87, 0x87, 0xff),
                Describe("The colour for an item that can't be sold. With Flea: the flea has banned it. With Trader: no trader buys it. "
                         + "With Most: the flea has banned it and no trader buys it either.", 70, drawer: ColourField.Draw));
            ColourBlocked.SettingChanged += (_, __) => ColoursChanged();

            HighlightBanned = Config.Bind(colours, "Highlight flea-banned items", true,
                Describe("Give items the flea has banned a background of their own colour, instead of a colour from the trader's price. "
                         + "Works even with Colours unticked. Only real bans count, not items locked by flea level or found in raid. "
                         + "Banned ammo keeps its tier colour while Colour banned ammo by flea price is on.", 69));
            BannedColour = Config.Bind(colours, "Flea-banned colour", (Color)new Color32(0xff, 0x00, 0x00, 0xff),
                Describe("The background colour for items the flea has banned.", 68, drawer: ColourField.Draw));
            HighlightBanned.SettingChanged += (_, __) => ColoursChanged();
            BannedColour.SettingChanged += (_, __) => ColoursChanged();

            ColourEnabled.SettingChanged += (_, __) => ColoursChanged();
            ColourNames.SettingChanged += (_, __) => ColoursChanged();
            ColourPrices.SettingChanged += (_, __) => ColoursChanged();
            ColourItems.SettingChanged += (_, __) => ColoursChanged();
            ColourStrength.SettingChanged += (_, __) => ColoursChanged();
            ColourFullStacks.SettingChanged += (_, __) => ColoursChanged();
            ColourFullMags.SettingChanged += (_, __) => ColoursChanged();
            ColourFullMoney.SettingChanged += (_, __) => ColoursChanged();
            ColourBannedAmmo.SettingChanged += (_, __) => ColoursChanged();
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

        private static ConfigDescription Describe(string text, int order, AcceptableValueBase range = null, Action<ConfigEntryBase> drawer = null)
        {
            return new ConfigDescription(text, range, new ConfigurationManagerAttributes { Order = order, CustomDrawer = drawer });
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

            IconPatches.Update();
        }
    }
}
