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

    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.fums.priceindex";
        public const string Name = "Fums-PriceIndex";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<InspectStyle> Inspect;
        internal static ConfigEntry<bool> HoverTooltip;
        internal static ConfigEntry<IconPrice> IconPrice;
        internal static ConfigEntry<StackPrice> Stacks;
        internal static ConfigEntry<IconPosition> IconPosition;
        internal static ConfigEntry<bool> InRaid;
        internal static ConfigEntry<bool> IncludeFence;
        internal static ConfigEntry<int> RefreshSeconds;

        private bool _patched;

        private void Awake()
        {
            Log = Logger;

            Inspect = Config.Bind("1. Display", "Inspect window", InspectStyle.LineUnderStats,
                "Where the flea and trader prices go in the item's inspect window. "
                + "StatsGrid: in with the item's stats (where they land depends on how many stats it has). "
                + "LineUnderStats / LineAboveStats: a line of their own, always in the same place. "
                + "Takes effect the next time a window opens.");
            HoverTooltip = Config.Bind("1. Display", "Hover tooltip", true,
                "Also show the prices under the item's name when hovering it.");
            IconPrice = Config.Bind("1. Display", "Price on items", PriceIndex.IconPrice.Most,
                "A short price in the corner of your own items in the stash and inventory. "
                + "Flea: the flea price. Trader: the best trader's. "
                + "Most: where you can sell it for the most right now, the flea or the best trader. "
                + "The flea only counts when you can list it there (flea level, found in raid only, and with "
                + "Better Flea its bans, item unlock levels and trader-bought items).");
            IconPosition = Config.Bind("1. Display", "Price position on items", PriceIndex.IconPosition.BottomLeft,
                "Where the price on items goes. MiddleLeft: halfway up the left edge, the same place on every item. "
                + "BottomLeft: the bottom left corner, with the game's own icons there moved up above it.");
            Stacks = Config.Bind("1. Display", "Stacks", StackPrice.SingleAndStack,
                "How a stack is priced. SingleAndStack: one item, then the whole stack in brackets. "
                + "Single: one item. Stack: the whole stack. Prices on items show one of the two "
                + "(the whole stack unless set to Single).");
            InRaid = Config.Bind("2. General", "Show in raid", true,
                "Also show prices in a raid.");
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
