using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

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

    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.fums.priceindex";
        public const string Name = "Fums-PriceIndex";
        public const string Version = "1.1.0";

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
        }
    }
}
