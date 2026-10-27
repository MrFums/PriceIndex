using System;
using System.Linq;
using System.Text;
using EFT;
using EFT.InventoryLogic;

namespace PriceIndex
{
    internal static class Redraw
    {
        public static readonly Action OnPrice = () => _due = true;
        private static bool _due;

        public static bool Due()
        {
            if (!_due)
                return false;
            _due = false;
            return true;
        }
    }

    internal sealed class PriceInfo
    {
        public bool Loading;
        public bool Refreshing;
        public int Count = 1;

        public string FleaName = "Flea market";
        public string FleaValue;
        public string FleaText;
        public string FleaTip;
        public double? FleaEach;
        public double? FleaTotal;
        public double FleaKeep;

        public string TraderName = "Traders";
        public string TraderValue;
        public string TraderText;
        public string TraderTip;
        public MongoID TraderCurrency = CurrencyUtil.ROUBLE_ID;
        public double? TraderRoubles;

        public static bool Shows(Item item)
        {
            return item != null && !CurrencyUtil.TryGetCurrencyType(item.TemplateId, out _) && !item.QuestItem;
        }

        public static PriceInfo For(Item item, Action onReady)
        {
            var info = new PriceInfo { Count = Math.Max(1, item.StackObjectsCount) };
            int count = info.Count;
            bool hasContents = item.GetAllItems().Any(part => part != item && part.CurrentAddress is GridItemAddress);

            var fleaTip = new StringBuilder();
            string block = Prices.FleaBlock(item);
            if (block != null)
            {
                info.FleaValue = info.FleaText = "Can't be listed";
                fleaTip.Append(block);
            }
            else
            {
                FleaState state = Prices.TryGetFlea(item, out double each, out bool exact, onReady);
                info.Refreshing = state == FleaState.Refreshing;
                switch (state)
                {
                    case FleaState.Ready:
                    case FleaState.Refreshing:
                        double total = each * count;
                        info.FleaEach = each;
                        info.FleaTotal = total;
                        info.FleaValue = Amount(each, total, count, null);
                        info.FleaText = Amount(each, total, count, CurrencyUtil.ROUBLE_ID);
                        fleaTip.Append("Sells for about ").Append(Prices.Money(total, CurrencyUtil.ROUBLE_ID));
                        if (count > 1)
                            fleaTip.Append(" (").Append(Prices.Money(each, CurrencyUtil.ROUBLE_ID)).Append(" each)");
                        fleaTip.Append(exact ? ".\nPrice from Better Flea." : ".\nPrice from the base game.");

                        double? fee = Prices.FleaFee(item, each);
                        info.FleaKeep = total - (fee ?? 0);

                        if (hasContents)
                            fleaTip.Append("\nEmpty it before listing it.");
                        break;
                    case FleaState.Loading:
                        info.FleaValue = info.FleaText = "...";
                        info.Loading = true;
                        break;
                    default:
                        info.FleaValue = info.FleaText = "N/A";
                        fleaTip.Append("The flea has no price for this item right now.");
                        break;
                }
            }

            info.FleaTip = fleaTip.ToString();

            var traderTip = new StringBuilder();
            if (Prices.TryGetBestTrader(item, out TraderOffer best, out bool loading, onReady))
            {
                double each = (double)best.Amount / count;
                info.TraderName = best.Trader.LocalizedName;
                info.TraderCurrency = best.Currency;
                info.TraderRoubles = best.Roubles;
                info.TraderValue = Amount(each, best.Amount, count, null);
                info.TraderText = Amount(each, best.Amount, count, best.Currency);
                traderTip.Append(best.Trader.LocalizedName).Append(" pays the most: ").Append(Prices.Money(best.Amount, best.Currency));
                if (best.Currency != CurrencyUtil.ROUBLE_ID)
                    traderTip.Append(" (").Append(Prices.Money(best.Roubles, CurrencyUtil.ROUBLE_ID)).Append(')');
                if (count > 1)
                    traderTip.Append(", ").Append(Prices.Money(each, best.Currency)).Append(" each");
                traderTip.Append('.');

                if (info.FleaKeep > 0)
                {
                    double gap = Math.Abs(info.FleaKeep - best.Roubles);
                    traderTip.Append('\n').Append(info.FleaKeep > best.Roubles
                        ? "The Flea Market gets you " + Prices.Money(gap, CurrencyUtil.ROUBLE_ID) + " more after its fee."
                        : best.Trader.LocalizedName + " gets you " + Prices.Money(gap, CurrencyUtil.ROUBLE_ID) + " more than the Flea Market after its fee.");
                }
            }
            else if (loading)
            {
                info.TraderValue = info.TraderText = "...";
                info.Loading = true;
            }
            else if (hasContents)
            {
                info.TraderValue = info.TraderText = "Empty it first";
                traderTip.Append("Traders only buy containers that are empty.");
            }
            else
            {
                info.TraderValue = info.TraderText = "Won't buy";
                traderTip.Append("No trader you can trade with buys this item.");
            }

            info.TraderTip = traderTip.ToString();
            return info;
        }

        private static string Amount(double each, double total, int count, MongoID? currency)
        {
            string Money(double amount) => currency == null ? Prices.Money(amount) : Prices.Money(amount, currency.Value);

            if (count <= 1)
                return Money(total);

            switch (Plugin.Stacks.Value)
            {
                case StackPrice.Single:
                    return Money(each);
                case StackPrice.Stack:
                    return Money(total);
                default:
                    return Money(each) + " (" + Money(total) + ")";
            }
        }
    }
}
