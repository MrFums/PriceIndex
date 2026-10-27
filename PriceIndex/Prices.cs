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

        private enum BetterFlea
        {
            Unknown,
            Checking,
            Yes,
            No
        }

        private static BetterFlea _betterFlea = BetterFlea.Unknown;
        private static Action _checked;

        private class FleaEntry
        {
            public double Price;
            public bool Known;
            public float FetchedAt = float.NegativeInfinity;
            public bool Pending;
            public Action Done;
        }

        private static readonly Dictionary<string, FleaEntry> Flea = new Dictionary<string, FleaEntry>();
        private static readonly HashSet<string> SupplyRequested = new HashSet<string>();

        private class MarketEntry
        {
            public double Average;
            public bool Known;
            public float FetchedAt = float.NegativeInfinity;
            public bool Pending;
            public Action Done;
        }

        private static readonly Dictionary<string, MarketEntry> Market = new Dictionary<string, MarketEntry>();

        public static IEftSession Session
        {
            get
            {
                var app = Singleton<ClientApplication<IEftSession>>.Instance;
                return app?.GetClientBackEndSession();
            }
        }

        public static FleaState TryGetFlea(Item item, out double price, out bool exact, Action onReady)
        {
            price = 0;
            exact = false;
            switch (CheckBetterFlea(onReady))
            {
                case BetterFlea.Unknown:
                case BetterFlea.Checking:
                    return FleaState.Loading;
                case BetterFlea.Yes:
                    FleaState state = TryGetBetterFlea(item, out price, onReady);
                    exact = state != FleaState.Unavailable;
                    if (exact)
                        return state;
                    break;
            }

            return TryGetAverage(item, out price, onReady);
        }

        public static void Update()
        {
            if (_betterFlea == BetterFlea.Unknown)
                CheckBetterFlea(null);
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
                try
                {
                    done?.Invoke();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError(e);
                }
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

        private static FleaState TryGetBetterFlea(Item item, out double price, Action onReady)
        {
            price = 0;
            string body = Serialize(item);
            if (!Flea.TryGetValue(body, out var entry))
            {
                if (Flea.Count >= MostCached)
                    Flea.Clear();
                Flea[body] = entry = new FleaEntry();
            }

            bool stale = Time.realtimeSinceStartup - entry.FetchedAt > Plugin.RefreshSeconds.Value;
            if (stale && !entry.Pending)
                RequestFlea(body, entry);

            if (entry.Pending && onReady != null)
            {
                entry.Done -= onReady;
                entry.Done += onReady;
            }

            if (entry.Known)
            {
                price = entry.Price;
                return entry.Pending ? FleaState.Refreshing : FleaState.Ready;
            }

            return entry.Pending ? FleaState.Loading : FleaState.Unavailable;
        }

        private const int MostInFlight = 4;
        private static int _inFlight;
        private static readonly Queue<KeyValuePair<string, FleaEntry>> Queued = new Queue<KeyValuePair<string, FleaEntry>>();

        private static void RequestFlea(string body, FleaEntry entry)
        {
            entry.Pending = true;
            if (_inFlight >= MostInFlight)
            {
                Queued.Enqueue(new KeyValuePair<string, FleaEntry>(body, entry));
                return;
            }

            SendFlea(body, entry);
        }

        private static async void SendFlea(string body, FleaEntry entry)
        {
            _inFlight++;
            try
            {
                double price = await Post(body);
                entry.Price = price;
                entry.Known = price > 0;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not get a Better Flea price: {e.Message}");
            }
            finally
            {
                _inFlight--;
                entry.Pending = false;
                entry.FetchedAt = Time.realtimeSinceStartup;
                var done = entry.Done;
                entry.Done = null;
                try
                {
                    done?.Invoke();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError(e);
                }

                if (Queued.Count > 0)
                {
                    var next = Queued.Dequeue();
                    SendFlea(next.Key, next.Value);
                }
            }
        }

        private static FleaState TryGetAverage(Item item, out double price, Action onReady)
        {
            price = 0;
            List<Item> parts = item is Weapon
                ? item.GetAllItems().Where(part => part == item || Attached(part, item)).ToList()
                : new List<Item> { item };

            bool loading = false;
            bool refreshing = false;
            foreach (Item part in parts)
            {
                FleaState state = TryGetMarket(part.TemplateId.ToString(), out double average, onReady);
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

        private static FleaState TryGetMarket(string tpl, out double average, Action onReady)
        {
            average = 0;
            if (!Market.TryGetValue(tpl, out var entry))
                Market[tpl] = entry = new MarketEntry();

            if (Time.realtimeSinceStartup - entry.FetchedAt > Plugin.RefreshSeconds.Value && !entry.Pending)
            {
                var session = Session;
                if (session != null)
                {
                    entry.Pending = true;
                    session.GetMarketPrices(tpl, result =>
                    {
                        entry.Pending = false;
                        entry.FetchedAt = Time.realtimeSinceStartup;
                        if (result.Succeed && result.Value != null)
                        {
                            entry.Average = result.Value.avg;
                            entry.Known = true;
                        }

                        var done = entry.Done;
                        entry.Done = null;
                        try
                        {
                            done?.Invoke();
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError(e);
                        }
                    });
                }
            }

            if (entry.Pending && onReady != null)
            {
                entry.Done -= onReady;
                entry.Done += onReady;
            }

            if (entry.Known)
            {
                average = entry.Average;
                return entry.Pending ? FleaState.Refreshing : FleaState.Ready;
            }

            return entry.Pending ? FleaState.Loading : FleaState.Unavailable;
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
}
