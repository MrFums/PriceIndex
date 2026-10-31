using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Trading;
using EFT.UI.Ragfair;
using Newtonsoft.Json;
using SPT.Common.Http;
using SPT.Common.Utils;
using UnityEngine;

namespace PriceIndex
{
    internal struct TraderOffer
    {
        public Trader Trader;
        public int Amount;
        public MongoID Currency;
        public double Roubles;
    }

    internal enum FleaState
    {
        Loading,
        Ready,
        Refreshing,
        Unavailable
    }

    internal static class Prices
    {
        private const string FenceId = "579dc571d53a0658a154fbec";
        private const string FleaRoute = "/betterflea/item-price";
        private const int MostCached = 2000;
        private const int MostInFlight = 4;
        private const int MostPerSecond = 20;

        private enum BetterFlea
        {
            Unknown,
            Checking,
            Yes,
            No
        }

        private static BetterFlea _betterFlea = BetterFlea.Unknown;
        private static Action _checked;

        private class Entry
        {
            public double Price;
            public bool Known;
            public float FetchedAt = float.NegativeInfinity;
            public bool Pending;
            public bool Waiting;
            public Action Done;

            public bool Stale => !Pending && Time.realtimeSinceStartup - FetchedAt > Plugin.RefreshSeconds.Value;

            public FleaState Read(Action onReady, out double price)
            {
                if (Pending && onReady != null)
                {
                    Done -= onReady;
                    Done += onReady;
                }

                price = Known ? Price : 0;
                if (Known)
                    return Pending ? FleaState.Refreshing : FleaState.Ready;
                return Pending ? FleaState.Loading : FleaState.Unavailable;
            }

            public void Finish(bool failed = false)
            {
                Pending = false;
                FetchedAt = Time.realtimeSinceStartup;
                if (failed)
                {
                    FetchedAt -= Math.Max(0, Plugin.RefreshSeconds.Value - Redraw.RetrySeconds);
                    Redraw.SweepIn(Redraw.RetrySeconds);
                }
                else
                {
                    Redraw.SweepIn(Plugin.RefreshSeconds.Value);
                }

                var done = Done;
                Done = null;
                Invoke(done);
            }
        }

        private static readonly Dictionary<string, Entry> Flea = new Dictionary<string, Entry>();
        private static readonly Dictionary<string, Entry> Market = new Dictionary<string, Entry>();
        private static readonly HashSet<string> SupplyRequested = new HashSet<string>();
        private static readonly Queue<KeyValuePair<string, Entry>> Queued = new Queue<KeyValuePair<string, Entry>>();
        private static readonly Queue<KeyValuePair<string, Entry>> MarketQueued = new Queue<KeyValuePair<string, Entry>>();
        private static int _inFlight;
        private static float _secondStart = float.NegativeInfinity;
        private static int _sentThisSecond;

        private static void Invoke(Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }

        public static IEftSession Session
        {
            get
            {
                var app = Singleton<ClientApplication<IEftSession>>.Instance;
                return app?.GetClientBackEndSession();
            }
        }

        public static void Warm(Item item)
        {
            if (!FleaOff)
                TryGetFlea(item, out _, out _, null, false);

            var session = Session;
            if (session?.Traders == null)
                return;
            foreach (Trader trader in session.Traders)
            {
                if (trader?.Info != null && trader.Info.Available && trader._supplyData == null)
                    LoadSupplyData(session, trader, null);
            }
        }

        public static FleaState TryGetFlea(Item item, out double price, out bool exact, Action onReady, bool urgent)
        {
            price = 0;
            exact = false;
            if (FleaOff)
                return FleaState.Unavailable;

            switch (CheckBetterFlea(onReady))
            {
                case BetterFlea.Unknown:
                case BetterFlea.Checking:
                    return FleaState.Loading;
                case BetterFlea.Yes:
                    FleaState state = TryGetBetterFlea(item, out price, onReady, urgent);
                    exact = state != FleaState.Unavailable;
                    if (exact)
                        return state;
                    break;
            }

            return TryGetAverage(item, out price, onReady, urgent);
        }

        public static void Update()
        {
            if (_betterFlea == BetterFlea.Unknown)
                CheckBetterFlea(null);
            SendQueued();
        }

        private static bool FleaOff => RagFair.Settings != null && !RagFair.Settings.enabled;

        private static bool CanSend()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _secondStart >= 1f)
            {
                _secondStart = now;
                _sentThisSecond = 0;
            }

            return _inFlight < MostInFlight && _sentThisSecond < MostPerSecond;
        }

        private static void SendQueued()
        {
            while (Queued.Count > 0 && CanSend())
            {
                var next = Queued.Dequeue();
                if (next.Value.Waiting)
                    SendFlea(next.Key, next.Value);
            }

            while (MarketQueued.Count > 0 && CanSend())
            {
                var next = MarketQueued.Dequeue();
                if (next.Value.Waiting)
                    SendMarket(next.Key, next.Value);
            }
        }

        private static BetterFlea CheckBetterFlea(Action onDone)
        {
            if (_betterFlea == BetterFlea.Checking && onDone != null)
            {
                _checked -= onDone;
                _checked += onDone;
            }

            if (_betterFlea != BetterFlea.Unknown || Session == null)
                return _betterFlea;

            _betterFlea = BetterFlea.Checking;
            _checked += onDone;
            Check();
            return _betterFlea;

            async void Check()
            {
                try
                {
                    await Post("[]");
                    _betterFlea = BetterFlea.Yes;
                    Plugin.Log.LogInfo("Better Flea found on the server, flea prices are its own.");
                }
                catch (Exception e)
                {
                    _betterFlea = BetterFlea.No;
                    Plugin.Log.LogInfo($"No Better Flea prices on this server ({e.Message}). Using the flea's average prices.");
                }

                var done = _checked;
                _checked = null;
                Invoke(done);
            }
        }

        private static async Task<double> Post(string body)
        {
            Client client = RequestHandler.HttpClient;
            using (HttpRequestMessage request = client.CreateNewHttpRequest(HttpMethod.Post, FleaRoute))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await client.HttpClient.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("the server answered " + (int)response.StatusCode);

                    byte[] data = await response.Content.ReadAsByteArrayAsync();
                    if (Zlib.IsCompressed(data))
                        data = Zlib.Decompress(data);

                    var answer = JsonConvert.DeserializeObject<FleaAnswer>(Encoding.UTF8.GetString(data))
                                 ?? throw new Exception("the server sent nothing back");
                    return answer.Price;
                }
            }
        }

        private static FleaState TryGetBetterFlea(Item item, out double price, Action onReady, bool urgent)
        {
            string body = Serialize(item);
            if (!Flea.TryGetValue(body, out var entry))
            {
                if (Flea.Count >= MostCached)
                    Flea.Clear();
                Flea[body] = entry = new Entry();
            }

            if (entry.Stale)
                RequestFlea(body, entry, urgent);
            else if (urgent && entry.Waiting)
                SendFlea(body, entry);
            return entry.Read(onReady, out price);
        }

        private static void RequestFlea(string body, Entry entry, bool urgent)
        {
            entry.Pending = true;
            if (!urgent && !CanSend())
            {
                entry.Waiting = true;
                Queued.Enqueue(new KeyValuePair<string, Entry>(body, entry));
                return;
            }

            SendFlea(body, entry);
        }

        private static async void SendFlea(string body, Entry entry)
        {
            entry.Waiting = false;
            _inFlight++;
            _sentThisSecond++;
            bool failed = false;
            try
            {
                double price = await Post(body);
                bool known = price > 0;
                if (known != entry.Known || known && price != entry.Price)
                    Redraw.Changed();
                entry.Price = price;
                entry.Known = known;
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning($"Could not get a Better Flea price: {e.Message}");
            }
            finally
            {
                _inFlight--;
                entry.Finish(failed);
                SendQueued();
            }
        }

        private static FleaState TryGetAverage(Item item, out double price, Action onReady, bool urgent)
        {
            price = 0;
            List<Item> parts = item is Weapon
                ? item.GetAllItems().Where(part => part == item || Attached(part, item)).ToList()
                : new List<Item> { item };

            bool loading = false;
            bool refreshing = false;
            foreach (Item part in parts)
            {
                FleaState state = TryGetMarket(part.TemplateId.ToString(), out double average, onReady, urgent);
                if (state == FleaState.Loading)
                    loading = true;
                else if (state == FleaState.Refreshing)
                    refreshing = true;
                else if (state == FleaState.Unavailable && part == item)
                    return FleaState.Unavailable;

                int rounds = part != item && part.CurrentAddress is StackSlotItemAddress ? Math.Max(1, part.StackObjectsCount) : 1;
                price += average * rounds;
            }

            if (loading)
                return FleaState.Loading;

            price *= Quality(item);
            if (price <= 0)
                return FleaState.Unavailable;
            return refreshing ? FleaState.Refreshing : FleaState.Ready;
        }

        private static FleaState TryGetMarket(string tpl, out double average, Action onReady, bool urgent)
        {
            if (!Market.TryGetValue(tpl, out var entry))
                Market[tpl] = entry = new Entry();

            if (entry.Stale && Session != null)
                RequestMarket(tpl, entry, urgent);
            else if (urgent && entry.Waiting)
                SendMarket(tpl, entry);

            return entry.Read(onReady, out average);
        }

        private static void RequestMarket(string tpl, Entry entry, bool urgent)
        {
            entry.Pending = true;
            if (!urgent && !CanSend())
            {
                entry.Waiting = true;
                MarketQueued.Enqueue(new KeyValuePair<string, Entry>(tpl, entry));
                return;
            }

            SendMarket(tpl, entry);
        }

        private static void SendMarket(string tpl, Entry entry)
        {
            entry.Waiting = false;
            var session = Session;
            if (session == null)
            {
                entry.Finish(true);
                return;
            }

            _inFlight++;
            _sentThisSecond++;
            session.GetMarketPrices(tpl, result =>
            {
                _inFlight--;
                bool failed = !result.Succeed;
                if (result.Succeed && result.Value != null)
                {
                    if (!entry.Known || result.Value.avg != entry.Price)
                        Redraw.Changed();
                    entry.Price = result.Value.avg;
                    entry.Known = true;
                }

                entry.Finish(failed);
                SendQueued();
            });
        }

        private static double Quality(Item item)
        {
            if (item is Weapon)
                return Math.Round(PartQuality(item), 5);

            double sum = 0;
            int count = 0;
            foreach (Item part in item.GetAllItems())
            {
                if (part != item && !Attached(part, item))
                    continue;
                if (part.TryGetItemComponent(out ArmorComponent armor) && armor.Repairable.TemplateDurability <= 0)
                    continue;

                sum += PartQuality(part);
                count++;
            }

            return count == 0 ? 1 : Math.Min(Math.Round(sum / count, 5), 1);
        }

        private static double PartQuality(Item item)
        {
            double quality = 1;
            if (item.TryGetItemComponent(out MedKitComponent medKit) && medKit.MaxHpResource > 0)
                quality = medKit.HpResource / medKit.MaxHpResource;
            else if (item.TryGetItemComponent(out RepairableComponent repairable))
                quality = repairable.TemplateDurability > 0 ? Math.Sqrt(repairable.Durability / repairable.TemplateDurability) : 1;
            else if (item.TryGetItemComponent(out FoodDrinkComponent food) && food.MaxResource > 0)
                quality = food.HpPercent / food.MaxResource;
            else if (item.TryGetItemComponent(out KeyComponent key) && key.NumberOfUsages > 0 && key.Template.MaximumNumberOfUsage > 0)
                quality = (key.Template.MaximumNumberOfUsage - key.NumberOfUsages) / (double)key.Template.MaximumNumberOfUsage;
            else if (item.TryGetItemComponent(out ResourceComponent resource) && resource.MaxResource > 0 && resource.Value < resource.MaxResource)
                quality = resource.Value / resource.MaxResource;
            else if (item is RepairKit repairKit && repairKit.MaxRepairResource > 0)
                quality = repairKit.Resource / repairKit.MaxRepairResource;

            return quality <= 0 ? 0.01 : quality;
        }

        private class FleaAnswer
        {
            public double Price { get; set; }
        }

        private static string Serialize(Item root)
        {
            var ids = new Dictionary<Item, string>();
            foreach (Item item in root.GetAllItems())
            {
                if (item == root || Attached(item, root))
                    ids[item] = (ids.Count + 1).ToString("x24");
            }

            var parts = new List<object>();
            foreach (Item item in ids.Keys)
            {
                ItemAddress address = item.CurrentAddress;
                var upd = Upd(item, item != root);
                parts.Add(new Dictionary<string, object>
                {
                    ["_id"] = ids[item],
                    ["_tpl"] = item.TemplateId.ToString(),
                    ["parentId"] = item == root ? null : ids[address.Container.ParentItem],
                    ["slotId"] = item == root ? null : address.Container.ID,
                    ["upd"] = upd.Count > 0 ? upd : null
                });
            }

            return JsonConvert.SerializeObject(parts, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }

        private static bool Attached(Item item, Item root)
        {
            while (item != root)
            {
                ItemAddress address = item.CurrentAddress;
                if (address == null || address is GridItemAddress)
                    return false;
                item = address.Container.ParentItem;
                if (item == null)
                    return false;
            }

            return true;
        }

        private static Dictionary<string, object> Upd(Item item, bool withStack)
        {
            var upd = new Dictionary<string, object>();
            if (withStack && item.StackObjectsCount > 1)
                upd["StackObjectsCount"] = item.StackObjectsCount;

            if (item.TryGetItemComponent(out RepairableComponent repairable))
                upd["Repairable"] = new { Durability = Round(repairable.Durability), MaxDurability = Round(repairable.MaxDurability) };
            if (item.TryGetItemComponent(out MedKitComponent medKit))
                upd["MedKit"] = new { HpResource = Round(medKit.HpResource) };
            if (item.TryGetItemComponent(out FoodDrinkComponent food))
                upd["FoodDrink"] = new { HpPercent = Round(food.HpPercent) };
            if (item.TryGetItemComponent(out KeyComponent key) && key.NumberOfUsages > 0)
                upd["Key"] = new { key.NumberOfUsages };
            if (item.TryGetItemComponent(out ResourceComponent resource) && resource.Value < resource.MaxResource)
                upd["Resource"] = new { Value = Round(resource.Value), UnitsConsumed = Round(resource.MaxResource - resource.Value) };
            if (item.TryGetItemComponent(out RepairKitComponent repairKit))
                upd["RepairKit"] = new { Resource = Round(repairKit.Resource) };
            return upd;
        }

        private static double Round(float value)
        {
            return Math.Round(value, 2);
        }

        public static string FleaBlock(Item item)
        {
            RagfairSettings settings = RagFair.Settings;
            ProfileInfo player = Session?.Profile?.Info;
            if (settings != null && !settings.enabled)
                return "The Flea Market is turned off.";
            if (settings != null && player != null && player.Level < settings.minUserLevel)
                return $"The Flea Market opens at level {settings.minUserLevel}.";
            if (player?.GetBan(EBanType.RagFair) != null)
                return "You're banned from the Flea Market for now.";

            if (!item.CanSellOnRagfair)
            {
                string reason = null;
                try
                {
                    if (item.Owner is ItemController controller)
                        RagFair.CanBeSelectedAtRagfair(item, controller, out reason);
                }
                catch (Exception)
                {
                    reason = null;
                }

                return string.IsNullOrEmpty(reason) || reason.IndexOf("non-empty", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "This item can't be listed on the Flea Market."
                    : reason + ".";
            }

            if (settings != null && settings.isOnlyFoundInRaidAllowed && !item.CanSellOnRagfairRaidRelated)
                return "Only found in raid items can be listed on the Flea Market.";
            return null;
        }

        public static double? FleaFee(Item item, double pricePerItem)
        {
            try
            {
                int count = Math.Max(1, item.StackObjectsCount);
                return Math.Ceiling(PriceCalculator.CalculateTaxPrice(item, count, pricePerItem, false));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool TryGetBestTrader(Item item, out TraderOffer best, out bool loading, Action onLoaded)
        {
            best = default;
            loading = false;

            var session = Session;
            if (session?.Traders == null)
                return false;

            foreach (Trader trader in session.Traders)
            {
                if (trader?.Info == null || !trader.Info.Available)
                    continue;
                if (trader.Id == FenceId && !Plugin.IncludeFence.Value)
                    continue;

                if (trader._supplyData == null)
                {
                    loading = true;
                    LoadSupplyData(session, trader, onLoaded);
                    continue;
                }

                Trader.ItemPrice? price;
                try
                {
                    price = trader.GetUserItemPrice(item);
                }
                catch (OverflowException)
                {
                    continue;
                }

                if (price == null || price.Value.CurrencyId == null)
                    continue;

                MongoID currency = price.Value.CurrencyId.Value;
                double roubles = price.Value.Amount * Rate(trader, currency);
                if (roubles > best.Roubles)
                {
                    best = new TraderOffer
                    {
                        Trader = trader,
                        Amount = price.Value.Amount,
                        Currency = currency,
                        Roubles = roubles
                    };
                }
            }

            return best.Trader != null;
        }

        public static double? MoneyRoubles(Item item)
        {
            double count = Math.Max(1, item.StackObjectsCount);
            MongoID currency = item.TemplateId;
            if (currency == CurrencyUtil.ROUBLE_ID)
                return count;

            var traders = Session?.Traders;
            if (traders == null)
                return null;
            foreach (Trader trader in traders)
            {
                if (trader?.CurrencyCourses != null && trader.CurrencyCourses.TryGetValue(currency.ToString(), out double rate) && rate > 0)
                    return count * rate;
            }

            return null;
        }

        private static double Rate(Trader trader, MongoID currency)
        {
            return trader.CurrencyCourses.TryGetValue(currency.ToString(), out double rate) ? rate : 1d;
        }

        private static async void LoadSupplyData(IEftSession session, Trader trader, Action onLoaded)
        {
            if (!SupplyRequested.Add(trader.Id))
                return;

            try
            {
                var result = await session.GetSupplyData(trader.Id);
                if (result.Succeed && trader._supplyData == null)
                    trader._supplyData = result.Value;
                onLoaded?.Invoke();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not load prices for trader {trader.Id}: {e.Message}");
            }
            finally
            {
                if (trader._supplyData == null)
                    SupplyRequested.Remove(trader.Id);
            }
        }

        public static string Money(double amount)
        {
            return Math.Round(amount).FormatSeparate();
        }

        public static string Money(double amount, MongoID currency)
        {
            string number = Money(amount);
            if (currency == CurrencyUtil.DOLLAR_ID)
                return "$" + number;
            if (currency == CurrencyUtil.EURO_ID)
                return "€" + number;
            return number + " ₽";
        }
    }

    internal static class Redraw
    {
        public const float RetrySeconds = 10f;
        private const float SweepGap = 0.25f;

        public static readonly Action OnPrice = () => _due = true;
        private static bool _due;
        private static bool _changed;
        private static float _sweepAt = float.PositiveInfinity;
        private static float _lastSweep = float.NegativeInfinity;

        public static void Changed()
        {
            _changed = true;
        }

        public static void SweepIn(float seconds)
        {
            _sweepAt = Math.Min(_sweepAt, Time.realtimeSinceStartup + seconds);
        }

        public static bool SweepDue()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastSweep < SweepGap || !_changed && now < _sweepAt)
                return false;

            _lastSweep = now;
            _changed = false;
            _sweepAt = float.PositiveInfinity;
            return true;
        }

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
        public bool FleaLoading;
        public bool TraderLoading;
        public bool Refreshing;
        public bool FleaLocked;
        public bool FleaBanned;
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

        public static bool Shows(Item item, ShowIn where)
        {
            if (item == null || CurrencyUtil.TryGetCurrencyType(item.TemplateId, out _))
                return false;
            return ShowsHere(item, where);
        }

        public static bool ShowsMoney(Item item, ShowIn where)
        {
            return item != null && CurrencyUtil.TryGetCurrencyType(item.TemplateId, out _) && ShowsHere(item, where);
        }

        private static bool ShowsHere(Item item, ShowIn where)
        {
            if (where == ShowIn.None || item.QuestItem)
                return false;

            bool inRaid = Singleton<GameWorld>.Instantiated && !(Singleton<GameWorld>.Instance is HideoutGameWorld);
            return (where & (inRaid ? ShowIn.Raid : ShowIn.Stash)) != 0;
        }

        public static PriceInfo For(Item item, Action onReady, bool urgent, bool flea = true)
        {
            int stack = Math.Max(1, item.StackObjectsCount);
            int count = ShopStock(item) ? 1 : stack;
            var info = new PriceInfo { Count = count };
            bool hasContents = item.GetAllItems().Any(part => part != item && part.CurrentAddress is GridItemAddress);

            var fleaTip = new StringBuilder();
            string block = Prices.FleaBlock(item);
            double each = 0;
            bool exact = false;
            FleaState state = flea ? Prices.TryGetFlea(item, out each, out exact, onReady, urgent) : FleaState.Unavailable;
            info.Refreshing = state == FleaState.Refreshing;
            switch (state)
            {
                case FleaState.Ready:
                case FleaState.Refreshing:
                    double total = each * count;
                    info.FleaEach = each;
                    info.FleaTotal = total;
                    info.FleaLocked = block != null;
                    info.FleaBanned = block != null;
                    if (info.FleaLocked)
                        info.FleaName = "Flea market (locked)";
                    info.FleaValue = Amount(each, total, count, null);
                    info.FleaText = Amount(each, total, count, CurrencyUtil.ROUBLE_ID);
                    fleaTip.Append("Sells for about ").Append(Prices.Money(total, CurrencyUtil.ROUBLE_ID));
                    if (count > 1)
                        fleaTip.Append(" (").Append(Prices.Money(each, CurrencyUtil.ROUBLE_ID)).Append(" each)");
                    fleaTip.Append(exact ? ".\nPrice from Better Flea." : ".\nPrice from the base game.");

                    if (info.FleaLocked)
                    {
                        fleaTip.Append('\n').Append(block).Append("\nUntil you can list it, only traders will buy it.");
                    }
                    else
                    {
                        double? fee = Prices.FleaFee(item, each);
                        info.FleaKeep = total - (fee ?? 0) * count / stack;
                        if (hasContents)
                            fleaTip.Append("\nEmpty it before listing it.");
                    }
                    break;
                case FleaState.Loading:
                    info.FleaValue = info.FleaText = "...";
                    info.Loading = info.FleaLoading = true;
                    break;
                default:
                    if (block != null)
                    {
                        info.FleaBanned = true;
                        info.FleaValue = info.FleaText = "Can't be listed";
                        fleaTip.Append(block);
                    }
                    else
                    {
                        info.FleaValue = info.FleaText = "N/A";
                        fleaTip.Append("The flea has no price for this item right now.");
                    }
                    break;
            }

            info.FleaTip = fleaTip.ToString();

            var traderTip = new StringBuilder();
            if (Prices.TryGetBestTrader(item, out TraderOffer best, out bool loading, onReady))
            {
                double traderEach = (double)best.Amount / stack;
                double traderTotal = traderEach * count;
                double roubles = best.Roubles / stack * count;
                info.TraderName = best.Trader.LocalizedName;
                info.TraderCurrency = best.Currency;
                info.TraderRoubles = roubles;
                info.TraderValue = Amount(traderEach, traderTotal, count, null);
                info.TraderText = Amount(traderEach, traderTotal, count, best.Currency);
                traderTip.Append(best.Trader.LocalizedName).Append(" pays the most: ").Append(Prices.Money(traderTotal, best.Currency));
                if (best.Currency != CurrencyUtil.ROUBLE_ID)
                    traderTip.Append(" (").Append(Prices.Money(roubles, CurrencyUtil.ROUBLE_ID)).Append(')');
                if (count > 1)
                    traderTip.Append(", ").Append(Prices.Money(traderEach, best.Currency)).Append(" each");
                traderTip.Append('.');

                if (info.FleaKeep > 0)
                {
                    double gap = Math.Abs(info.FleaKeep - roubles);
                    traderTip.Append('\n').Append(info.FleaKeep > roubles
                        ? "The Flea Market gets you " + Prices.Money(gap, CurrencyUtil.ROUBLE_ID) + " more after its fee."
                        : best.Trader.LocalizedName + " gets you " + Prices.Money(gap, CurrencyUtil.ROUBLE_ID) + " more than the Flea Market after its fee.");
                }
            }
            else if (loading)
            {
                info.TraderValue = info.TraderText = "...";
                info.Loading = info.TraderLoading = true;
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

        private static bool ShopStock(Item item)
        {
            return !(item.Owner is InventoryController) || item.StackObjectsCount > item.StackMaxSize;
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
