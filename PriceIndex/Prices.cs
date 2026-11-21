using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
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
        private const string BatchRoute = "/betterflea/item-prices";
        private const string ClientDataRoute = "/betterflea/client-data";
        private const string MarketRoute = "/client/ragfair/itemMarketPrice";
        private const string MarketKey = "market:";
        private const float MarketKeepSeconds = 1800f;
        private const int MarketTimeout = 30000;
        private const float SaveEvery = 10f;
        private const string CacheName = "PriceIndex-flea.json";
        private const int MostCached = 4000;
        private const int MostPerBatch = 250;
        private const double PreloadBudget = 2d;
        private const int MostInFlight = 4;
        private const int MostPerSecond = 20;
        private const int WarmInFlight = 1;
        private const int WarmPerSecond = 3;
        private const int FailuresBeforePause = 3;
        private const float FirstPause = 10f;
        private const float LongestPause = 60f;

        private enum Priority
        {
            Urgent,
            Normal,
            Warm
        }

        private struct Job
        {
            public string Key;
            public Entry Entry;
            public Priority Tier;
        }

        private enum BetterFlea
        {
            Unknown,
            Checking,
            Yes,
            No
        }

        private static BetterFlea _betterFlea = BetterFlea.Unknown;
        private static bool _batch;
        private static Action _checked;

        private class Entry
        {
            public double Price;
            public bool Known;
            public float FetchedAt = float.NegativeInfinity;
            public bool Pending;
            public bool Waiting;
            public Action Done;
            public LinkedListNode<string> Used;
            public Priority Tier;
            public bool Market;

            private float Keep => Market ? Math.Max(MarketKeepSeconds, Plugin.RefreshSeconds.Value) : Plugin.RefreshSeconds.Value;

            public bool Stale => !Pending && Time.realtimeSinceStartup - FetchedAt > Keep;

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
                    FetchedAt -= Math.Max(0, Keep - Redraw.RetrySeconds);
                    Redraw.SweepIn(Redraw.RetrySeconds);
                }
                else
                {
                    Redraw.SweepIn(Keep);
                }

                var done = Done;
                Done = null;
                Invoke(done);
            }
        }

        private static readonly Dictionary<string, Entry> Flea = new Dictionary<string, Entry>();
        private static readonly LinkedList<string> FleaOrder = new LinkedList<string>();
        private static readonly HashSet<string> SupplyRequested = new HashSet<string>();
        private static readonly Dictionary<string, float> SupplyRetryAt = new Dictionary<string, float>();
        private static readonly Queue<Job>[] Jobs = { new Queue<Job>(), new Queue<Job>(), new Queue<Job>() };
        private static readonly Queue<float> Sent = new Queue<float>();
        private static readonly Queue<float> WarmSent = new Queue<float>();
        private static int _inFlight;
        private static int _warmInFlight;
        private static int _failures;
        private static float _pausedUntil = float.NegativeInfinity;
        private static float _nextPause = FirstPause;
        private static HashSet<string> _bannedList;
        private static bool _bannedFetching;
        private static readonly List<Item> PreloadItems = new List<Item>();
        private static int _preloadNext;
        private static bool _preloaded;
        private static bool _wasInRaid;
        private static bool _cacheLoaded;
        private static bool _cacheDirty;
        private static float _saveAt;

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
                TryGetFlea(item, out _, out _, null, false, true);
            LoadTraders(Redraw.OnPrice);
        }

        public static void LoadTraders(Action onLoaded = null)
        {
            var session = Session;
            if (session?.Traders == null)
                return;
            foreach (Trader trader in session.Traders)
            {
                if (trader?.Info != null && trader.Info.Available && trader._supplyData == null)
                    LoadSupplyData(session, trader, onLoaded ?? Redraw.OnPrice);
            }
        }

        public static FleaState TryGetFlea(Item item, out double price, out bool exact, Action onReady, bool urgent, bool warm = false)
        {
            Priority tier = urgent ? Priority.Urgent : warm ? Priority.Warm : Priority.Normal;
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
                    FleaState state = TryGetBetterFlea(item, out price, onReady, tier);
                    exact = state != FleaState.Unavailable;
                    if (exact)
                        return state;
                    break;
            }

            return TryGetAverage(item, out price, onReady, tier);
        }

        public static void Update()
        {
            if (_betterFlea == BetterFlea.Unknown)
                CheckBetterFlea(null);
            LoadCache();
            Preload();
            SendQueued();
            SaveCache();
        }

        public static bool InRaid => Singleton<GameWorld>.Instantiated && !(Singleton<GameWorld>.Instance is HideoutGameWorld);

        private static void Preload()
        {
            LoadTraders(Redraw.OnPrice);
            bool inRaid = InRaid;
            if (inRaid != _wasInRaid)
            {
                _wasInRaid = inRaid;
                _preloaded = false;
                PreloadItems.Clear();
            }

            if (inRaid || FleaOff || _betterFlea == BetterFlea.Unknown || _betterFlea == BetterFlea.Checking)
                return;

            if (!_preloaded)
            {
                if (_betterFlea == BetterFlea.Yes)
                    FetchBans();
                var inventory = Session?.Profile?.Inventory;
                if (inventory == null)
                    return;

                _preloaded = true;
                _preloadNext = 0;
                PreloadItems.Clear();
                Item equipment = inventory.Equipment;
                foreach (Item root in new Item[] { inventory.Stash, equipment })
                {
                    if (root == null)
                        continue;
                    foreach (Item item in root.GetAllItems())
                    {
                        if (item.CurrentAddress is GridItemAddress || item.CurrentAddress?.Container?.ParentItem == equipment)
                            PreloadItems.Add(item);
                    }
                }
            }

            bool warm = _betterFlea == BetterFlea.Yes;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (_preloadNext < PreloadItems.Count && watch.Elapsed.TotalMilliseconds < PreloadBudget)
            {
                Item item = PreloadItems[_preloadNext++];
                if (item is Money || item.QuestItem)
                    continue;
                TryGetFlea(item, out _, out _, null, false, warm);
                if (item is Magazine mag && mag.FirstRealAmmo() is Item round)
                    TryGetFlea(round, out _, out _, null, false, warm);
            }
        }

        private static bool FleaOff => !Plugin.ShowFlea.Value || RagFair.Settings != null && !RagFair.Settings.enabled;

        public static bool FleaShown => !FleaOff;

        private static bool CanSend(bool warm)
        {
            float now = Time.realtimeSinceStartup;
            if (now < _pausedUntil)
                return false;

            while (Sent.Count > 0 && now - Sent.Peek() >= 1f)
                Sent.Dequeue();
            while (WarmSent.Count > 0 && now - WarmSent.Peek() >= 1f)
                WarmSent.Dequeue();

            if (_inFlight >= MostInFlight || Sent.Count >= MostPerSecond)
                return false;
            return !warm || _warmInFlight < WarmInFlight && WarmSent.Count < WarmPerSecond;
        }

        private static void SendQueued()
        {
            while (true)
            {
                Job? first = NextJob();
                if (first == null || !CanSend(first.Value.Tier == Priority.Warm))
                    return;

                if (!_batch)
                {
                    Jobs[(int)first.Value.Tier].Dequeue();
                    Send(first.Value);
                    continue;
                }

                var batch = new List<Job>();
                bool warm = true;
                foreach (Queue<Job> jobs in Jobs)
                {
                    while (jobs.Count > 0 && batch.Count < MostPerBatch)
                    {
                        Job job = jobs.Dequeue();
                        if (!Valid(job))
                            continue;
                        job.Entry.Waiting = false;
                        batch.Add(job);
                        if (job.Tier != Priority.Warm)
                            warm = false;
                    }
                }

                SendBatch(batch, warm);
            }
        }

        private static bool Valid(Job job)
        {
            return job.Entry.Waiting && job.Entry.Tier == job.Tier;
        }

        private static Job? NextJob()
        {
            foreach (Queue<Job> jobs in Jobs)
            {
                while (jobs.Count > 0)
                {
                    if (Valid(jobs.Peek()))
                        return jobs.Peek();
                    jobs.Dequeue();
                }
            }

            return null;
        }

        private static void Ask(string key, Entry entry, Priority tier)
        {
            if (!entry.Pending)
            {
                entry.Pending = true;
                entry.Waiting = true;
                entry.Tier = tier;
            }
            else if (entry.Waiting && tier < entry.Tier)
            {
                entry.Tier = tier;
            }
            else
            {
                return;
            }

            Jobs[(int)tier].Enqueue(new Job { Key = key, Entry = entry, Tier = tier });
            if (!_batch)
                SendQueued();
        }

        private static void Started(bool warm)
        {
            float now = Time.realtimeSinceStartup;
            _inFlight++;
            Sent.Enqueue(now);
            if (!warm)
                return;
            _warmInFlight++;
            WarmSent.Enqueue(now);
        }

        private static void Ended(bool warm, bool failed)
        {
            _inFlight--;
            if (warm)
                _warmInFlight--;
            if (failed)
                Failed();
            else
                Succeeded();
        }

        private static async void Send(Job job)
        {
            job.Entry.Waiting = false;
            bool warm = job.Tier == Priority.Warm;
            Started(warm);
            bool failed = false;
            try
            {
                Store(job.Entry, job.Entry.Market ? await MarketAverage(job.Key.Substring(MarketKey.Length)) : await Post(job.Key));
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning(job.Entry.Market ? $"Could not get a flea price: {e.Message}" : $"Could not get a Better Flea price: {e.Message}");
            }

            Ended(warm, failed);
            job.Entry.Finish(failed);
            SendQueued();
        }

        private static async void SendBatch(List<Job> batch, bool warm)
        {
            Started(warm);
            bool failed = false;
            List<double> prices = null;
            try
            {
                prices = await PostBatch(batch.Select(job => job.Key));
                if (prices.Count != batch.Count)
                    throw new Exception($"the server sent {prices.Count} prices for {batch.Count} items");
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning($"Could not get Better Flea prices: {e.Message}");
            }

            Ended(warm, failed);
            for (int i = 0; i < batch.Count; i++)
            {
                if (!failed)
                    Store(batch[i].Entry, prices[i]);
                batch[i].Entry.Finish(failed);
            }

            SendQueued();
        }

        private static void Store(Entry entry, double price)
        {
            bool known = price > 0;
            if (entry.Known && (!known || price != entry.Price))
                Redraw.Changed();
            entry.Price = price;
            entry.Known = known;
            if (entry.Market)
                _cacheDirty = true;
        }

        private static string CachePath => Path.Combine(BepInEx.Paths.CachePath, CacheName);

        private static void LoadCache()
        {
            if (_cacheLoaded)
                return;
            _cacheLoaded = true;
            try
            {
                if (!File.Exists(CachePath))
                    return;
                var saved = JsonConvert.DeserializeObject<Dictionary<string, double>>(File.ReadAllText(CachePath));
                if (saved == null)
                    return;
                foreach (var pair in saved)
                {
                    if (pair.Value <= 0)
                        continue;
                    Entry entry = Cached(MarketKey + pair.Key, true);
                    entry.Price = pair.Value;
                    entry.Known = true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not read the saved flea prices: {e.Message}");
            }
        }

        private static void SaveCache()
        {
            float now = Time.realtimeSinceStartup;
            if (!_cacheDirty || now < _saveAt)
                return;
            _cacheDirty = false;
            _saveAt = now + SaveEvery;
            try
            {
                var saved = new Dictionary<string, double>();
                foreach (var pair in Flea)
                {
                    if (pair.Value.Market && pair.Value.Known)
                        saved[pair.Key.Substring(MarketKey.Length)] = pair.Value.Price;
                }
                Directory.CreateDirectory(BepInEx.Paths.CachePath);
                File.WriteAllText(CachePath, JsonConvert.SerializeObject(saved));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not save the flea prices: {e.Message}");
            }
        }

        private static void Failed()
        {
            if (++_failures < FailuresBeforePause)
                return;

            _failures = 0;
            _pausedUntil = Time.realtimeSinceStartup + _nextPause;
            Plugin.Log.LogWarning($"{FailuresBeforePause} price requests failed in a row. Pausing price requests for {_nextPause:0} seconds.");
            Redraw.SweepIn(_nextPause);
            _nextPause = Math.Min(LongestPause, _nextPause * 2f);
        }

        private static void Succeeded()
        {
            _failures = 0;
            _nextPause = FirstPause;
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
                    try
                    {
                        await PostBatch(new string[0]);
                        _batch = true;
                    }
                    catch (Exception)
                    {
                        _batch = false;
                    }

                    _betterFlea = BetterFlea.Yes;
                    Plugin.Log.LogInfo(_batch
                        ? "Better Flea found on the server, flea prices are its own and are asked for many at a time."
                        : "Better Flea found on the server, flea prices are its own. Update Better Flea to ask for many at a time.");
                }
                catch (Exception e)
                {
                    _betterFlea = BetterFlea.No;
                    Plugin.Log.LogInfo($"No Better Flea prices on this server ({e.Message}). Using the flea prices the game already has.");
                }

                var done = _checked;
                _checked = null;
                Invoke(done);
            }
        }

        private static async Task<double> Post(string body)
        {
            var answer = JsonConvert.DeserializeObject<FleaAnswer>(await PostText(FleaRoute, body))
                         ?? throw new Exception("the server sent nothing back");
            return answer.Price;
        }

        private static async Task<double> MarketAverage(string tpl)
        {
            Client client = RequestHandler.HttpClient;
            byte[] body = Zlib.Compress(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { templateId = tpl })), ZlibCompression.Maximum);
            using (var timeout = new CancellationTokenSource(MarketTimeout))
            using (HttpRequestMessage request = client.CreateNewHttpRequest(HttpMethod.Post, MarketRoute))
            {
                request.Content = new ByteArrayContent(body);
                using (HttpResponseMessage response = await client.HttpClient.SendAsync(request, timeout.Token))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("the server answered " + (int)response.StatusCode);

                    byte[] data = await response.Content.ReadAsByteArrayAsync();
                    if (Zlib.IsCompressed(data))
                        data = Zlib.Decompress(data);
                    var answer = JsonConvert.DeserializeObject<MarketAnswer>(Encoding.UTF8.GetString(data));
                    if (answer == null || answer.Err != 0 || answer.Data == null)
                        throw new Exception("the server sent no price back");
                    return answer.Data.Avg ?? 0;
                }
            }
        }

        private static async Task<List<double>> PostBatch(IEnumerable<string> bodies)
        {
            var answer = JsonConvert.DeserializeObject<BatchAnswer>(await PostText(BatchRoute, "[" + string.Join(",", bodies) + "]"));
            return answer?.Prices ?? throw new Exception("the server sent no prices back");
        }

        public static bool IsFleaBanned(Item item)
        {
            if (item?.Template == null)
                return false;
            if (_bannedList != null)
                return _bannedList.Contains(item.TemplateId.ToString());
            return !item.Template.CanSellOnRagfair;
        }

        private static async void FetchBans()
        {
            if (_bannedFetching)
                return;
            _bannedFetching = true;
            try
            {
                Client client = RequestHandler.HttpClient;
                using (HttpRequestMessage request = client.CreateNewHttpRequest(HttpMethod.Get, ClientDataRoute))
                using (HttpResponseMessage response = await client.HttpClient.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("the server answered " + (int)response.StatusCode);
                    byte[] data = await response.Content.ReadAsByteArrayAsync();
                    if (Zlib.IsCompressed(data))
                        data = Zlib.Decompress(data);
                    var answer = JsonConvert.DeserializeObject<BanAnswer>(Encoding.UTF8.GetString(data));
                    if (answer?.FleaBanned != null)
                    {
                        var list = new HashSet<string>(answer.FleaBanned);
                        if (_bannedList == null || !_bannedList.SetEquals(list))
                            Redraw.Changed();
                        _bannedList = list;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not get Better Flea's banned items: {e.Message}");
            }
            finally
            {
                _bannedFetching = false;
            }
        }

        private static async Task<string> PostText(string route, string body)
        {
            Client client = RequestHandler.HttpClient;
            using (HttpRequestMessage request = client.CreateNewHttpRequest(HttpMethod.Post, route))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await client.HttpClient.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("the server answered " + (int)response.StatusCode);

                    byte[] data = await response.Content.ReadAsByteArrayAsync();
                    if (Zlib.IsCompressed(data))
                        data = Zlib.Decompress(data);
                    return Encoding.UTF8.GetString(data);
                }
            }
        }

        private static FleaState TryGetBetterFlea(Item item, out double price, Action onReady, Priority tier)
        {
            string body = Serialize(item);
            Entry entry = Cached(body, false);
            if (entry.Stale || entry.Waiting)
                Ask(body, entry, tier);
            return entry.Read(onReady, out price);
        }

        private static Entry Cached(string key, bool market)
        {
            if (!Flea.TryGetValue(key, out var entry))
            {
                while (Flea.Count >= MostCached && FleaOrder.First != null)
                {
                    Flea.Remove(FleaOrder.First.Value);
                    FleaOrder.RemoveFirst();
                }
                Flea[key] = entry = new Entry { Market = market };
                entry.Used = FleaOrder.AddLast(key);
            }
            else
            {
                FleaOrder.Remove(entry.Used);
                FleaOrder.AddLast(entry.Used);
            }

            return entry;
        }

        private static FleaState TryGetMarket(string tpl, out double price, Action onReady, Priority tier)
        {
            string key = MarketKey + tpl;
            Entry entry = Cached(key, true);
            if (entry.Stale || entry.Waiting)
                Ask(key, entry, tier);
            return entry.Read(onReady, out price);
        }

        private static FleaState TryGetAverage(Item item, out double price, Action onReady, Priority tier)
        {
            FleaState state = TryGetMarket(item.TemplateId.ToString(), out double average, onReady, tier);
            price = 0;
            if (state == FleaState.Loading)
                return state;
            if (average <= 0)
                return FleaState.Unavailable;

            price = average * Quality(item);
            return price > 0 ? state : FleaState.Unavailable;
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

        private class BatchAnswer
        {
            public List<double> Prices { get; set; }
        }

        private class BanAnswer
        {
            public List<string> FleaBanned { get; set; }
        }

        private class MarketAnswer
        {
            [JsonProperty("err")]
            public int Err { get; set; }

            [JsonProperty("data")]
            public MarketPrices Data { get; set; }
        }

        private class MarketPrices
        {
            [JsonProperty("avg")]
            public double? Avg { get; set; }
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

        public static bool Attached(Item item, Item root)
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

            if (!item.Template.CanSellOnRagfair)
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

                // Fill missing USD/EUR/GP courses before the game looks them up.
                // Anarchy Flea can set payouts to GP/USD while CurrencyCourses lacks that key;
                // GetUserItemPrice then throws and that trader would be ignored.
                EnsureCurrencyCourses(trader);

                Trader.ItemPrice? price;
                try
                {
                    price = trader.GetUserItemPrice(item);
                }
                catch (Exception)
                {
                    // Still skip if the game can't price this item for this trader.
                    continue;
                }

                if (price == null || price.Value.CurrencyId == null)
                    continue;

                MongoID currency = price.Value.CurrencyId.Value;
                double rate = Rate(trader, currency);
                if (rate <= 0)
                {
                    loading = true;
                    LoadTraders(onLoaded);
                    continue;
                }

                double roubles = price.Value.Amount * rate;
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

        private static void EnsureCurrencyCourses(Trader trader)
        {
            EnsureCourseEntry(trader, CurrencyUtil.ROUBLE_ID);
            EnsureCourseEntry(trader, CurrencyUtil.DOLLAR_ID);
            EnsureCourseEntry(trader, CurrencyUtil.EURO_ID);
            EnsureCourseEntry(trader, CurrencyUtil.GP_ID);
        }

        private static void EnsureCourseEntry(Trader trader, MongoID currency)
        {
            double? rate = Course(currency);
            if (rate == null || rate <= 0)
                return;

            WriteCourse(trader._supplyData?.CurrencyCourses, currency, rate.Value);
            WriteCourse(trader.CurrencyCourses, currency, rate.Value);
        }

        private static void WriteCourse(Dictionary<string, double> courses, MongoID currency, double rate)
        {
            if (courses == null)
                return;

            string key = currency.ToString();
            // Replace missing or placeholder (1) rates so GetUserItemPrice does not throw / mis-convert.
            if (courses.TryGetValue(key, out double existing) && PlausibleCourse(currency, existing))
                return;
            courses[key] = rate;
        }

        public static MongoID PrimaryId
        {
            get
            {
                switch (Plugin.ValueCurrency.Value)
                {
                    case PrimaryCurrency.USD:
                        return CurrencyUtil.DOLLAR_ID;
                    case PrimaryCurrency.EUR:
                        return CurrencyUtil.EURO_ID;
                    case PrimaryCurrency.GP:
                        return CurrencyUtil.GP_ID;
                    default:
                        return CurrencyUtil.ROUBLE_ID;
                }
            }
        }

        public static bool PrimaryIsRoubles => Plugin.ValueCurrency.Value == PrimaryCurrency.RUB;

        private struct CourseEntry
        {
            public double Rate;
            public string Source;
        }

        // Per-currency cache. Single-slot cache used to clobber USD when GP was resolved for traders.
        private static readonly Dictionary<string, CourseEntry> Courses = new Dictionary<string, CourseEntry>();

        // Used when traders/handbook have nothing usable (common once RUB offers are disabled).
        private const double FallbackUsd = 140d;
        private const double FallbackEur = 160d;
        private const double FallbackGp = 38000d;

        public static void ResetCourse()
        {
            Courses.Clear();
        }

        /// <summary>Rouble exchange rate for a currency (roubles per 1 unit), or null if not loaded yet.</summary>
        public static double? Course(MongoID currency)
        {
            if (currency == CurrencyUtil.ROUBLE_ID)
                return 1d;

            string key = currency.ToString();
            double rate = ResolveCourse(currency, out string source);
            if (rate <= 1d)
            {
                LoadTraders(Redraw.OnPrice);
                return null;
            }

            bool changed = !Courses.TryGetValue(key, out CourseEntry previous)
                           || previous.Rate != rate
                           || previous.Source != source;
            if (changed)
            {
                Courses[key] = new CourseEntry { Rate = rate, Source = source };
                if (currency == PrimaryId)
                {
                    Plugin.Log.LogInfo(
                        $"Primary currency course ready ({source}): {Plugin.ValueCurrency.Value} = {rate:0.##} roubles each. "
                        + "Flea amounts stay in roubles from Better Flea; tags convert with this rate.");
                    Redraw.Changed();
                }
            }

            return rate;
        }

        private static double ResolveCourse(MongoID currency, out string source)
        {
            source = null;
            int overrideRate = Plugin.RublesPerPrimary != null ? Plugin.RublesPerPrimary.Value : 0;
            if (overrideRate > 0 && currency == PrimaryId && PlausibleCourse(currency, overrideRate))
            {
                source = "config";
                return overrideRate;
            }

            double trader = TraderCourse(currency);
            if (PlausibleCourse(currency, trader))
            {
                source = "trader";
                return trader;
            }

            double handbook = HandbookCourse(currency);
            if (PlausibleCourse(currency, handbook))
            {
                source = "handbook";
                return handbook;
            }

            double fallback = FallbackCourse(currency);
            if (fallback > 1d)
            {
                source = "fallback";
                return fallback;
            }

            source = null;
            return 0;
        }

        private static double TraderCourse(MongoID currency)
        {
            var traders = Session?.Traders;
            if (traders == null)
                return 0;

            string key = currency.ToString();
            double best = 0;
            foreach (Trader trader in traders)
            {
                // Only supply data — trader.CurrencyCourses alone can be a placeholder of 1.
                if (trader?._supplyData?.CurrencyCourses == null)
                    continue;
                if (!trader._supplyData.CurrencyCourses.TryGetValue(key, out double rate))
                    continue;
                if (!PlausibleCourse(currency, rate))
                    continue;
                if (rate > best)
                    best = rate;
            }

            return best;
        }

        private static double HandbookCourse(MongoID currency)
        {
            try
            {
                // CreditsPrice is the handbook rouble value of one unit of that currency.
                var templates = Singleton<ItemFactory>.Instance?.ItemTemplates;
                if (templates != null && templates.TryGetValue(currency, out ItemTemplate template)
                    && PlausibleCourse(currency, template.CreditsPrice))
                    return template.CreditsPrice;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not read handbook currency course: {e.Message}");
            }

            return 0;
        }

        private static double FallbackCourse(MongoID currency)
        {
            if (currency == CurrencyUtil.DOLLAR_ID)
                return FallbackUsd;
            if (currency == CurrencyUtil.EURO_ID)
                return FallbackEur;
            if (currency == CurrencyUtil.GP_ID)
                return FallbackGp;
            return 0;
        }

        private static bool PlausibleCourse(MongoID currency, double rate)
        {
            if (rate <= 1d)
                return false;
            // GP coins are thousands of roubles; USD/EUR are typically ~100–200.
            if (currency == CurrencyUtil.GP_ID)
                return rate >= 1000d;
            return rate >= 10d;
        }

        public static double? ToPrimary(double roubles)
        {
            if (PrimaryIsRoubles)
                return roubles;

            double? rate = Course(PrimaryId);
            if (rate == null)
                return null;
            return roubles / rate.Value;
        }

        public static double? ToRoubles(double amount, MongoID currency)
        {
            if (currency == CurrencyUtil.ROUBLE_ID)
                return amount;

            double? rate = Course(currency);
            if (rate == null)
                return null;
            return amount * rate.Value;
        }

        public static double? MoneyColourValue(Item item)
        {
            double? value = MoneyValue(item);
            if (value == null || !Plugin.ColourFullMoney.Value || item.StackMaxSize <= 1)
                return value;
            return value / Math.Max(1, item.StackObjectsCount) * item.StackMaxSize;
        }

        public static double? MoneyValue(Item item)
        {
            double? roubles = MoneyRoubles(item);
            return roubles == null ? null : ToPrimary(roubles.Value);
        }

        public static double? MoneyRoubles(Item item)
        {
            double count = Math.Max(1, item.StackObjectsCount);
            MongoID currency = item.TemplateId;
            if (currency == CurrencyUtil.ROUBLE_ID)
                return count;

            double? rate = Course(currency);
            return rate == null ? null : count * rate.Value;
        }

        private static double Rate(Trader trader, MongoID currency)
        {
            if (currency == CurrencyUtil.ROUBLE_ID)
                return 1d;

            // Prefer the shared course (handbook / config / fallback). Trader tables may
            // still hold placeholders even after EnsureCurrencyCourses on other traders.
            double? global = Course(currency);
            if (global != null)
                return global.Value;

            string key = currency.ToString();
            if (trader._supplyData?.CurrencyCourses != null
                && trader._supplyData.CurrencyCourses.TryGetValue(key, out double rate)
                && PlausibleCourse(currency, rate))
                return rate;

            if (trader.CurrencyCourses != null
                && trader.CurrencyCourses.TryGetValue(key, out rate)
                && PlausibleCourse(currency, rate))
                return rate;

            return 0d;
        }

        private static async void LoadSupplyData(IEftSession session, Trader trader, Action onLoaded)
        {
            if (SupplyRetryAt.TryGetValue(trader.Id, out float retryAt) && Time.realtimeSinceStartup < retryAt)
                return;
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
                SupplyRequested.Remove(trader.Id);
                if (trader._supplyData == null)
                    SupplyRetryAt[trader.Id] = Time.realtimeSinceStartup + Redraw.RetrySeconds;
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
            if (currency == CurrencyUtil.GP_ID)
                return number + " GP";
            return number + " ₽";
        }

        public static string PrimaryMoney(double roubles)
        {
            double? primary = ToPrimary(roubles);
            return primary == null ? "..." : Money(primary.Value, PrimaryId);
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
        public double? ColourFlea;
        public bool IsAmmo;
        public double? ColourTrader;
        public int Slots = 1;
        public ColourCategory Kind = ColourCategory.Other;

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

            return (where & (Prices.InRaid ? ShowIn.Raid : ShowIn.Stash)) != 0;
        }

        public static PriceInfo For(Item item, Action onReady, bool urgent, bool flea = true)
        {
            int stack = Math.Max(1, item.StackObjectsCount);
            int count = ShopStock(item) ? 1 : stack;
            IntVec2 size = item.CalculateCellSize();
            var info = new PriceInfo { Count = count, Slots = Math.Max(1, size.X * size.Y) };
            bool hasContents = item.GetAllItems().Any(part => part != item && part.CurrentAddress is GridItemAddress);

            MongoID primary = Prices.PrimaryId;
            if (!Prices.PrimaryIsRoubles && Prices.Course(primary) == null)
            {
                Prices.LoadTraders(onReady);
                info.Loading = true;
            }

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

                    double? showEach = Prices.ToPrimary(each);
                    double? showTotal = Prices.ToPrimary(total);
                    if (showEach == null || showTotal == null)
                    {
                        info.FleaValue = info.FleaText = "...";
                        info.Loading = info.FleaLoading = true;
                    }
                    else
                    {
                        info.FleaValue = Amount(showEach.Value, showTotal.Value, count, null) + info.PerSlot(showTotal.Value, null);
                        info.FleaText = Amount(showEach.Value, showTotal.Value, count, primary) + info.PerSlot(showTotal.Value, primary);
                        fleaTip.Append("Sells for about ").Append(Prices.Money(showTotal.Value, primary));
                        if (count > 1)
                            fleaTip.Append(" (").Append(Prices.Money(showEach.Value, primary)).Append(" each)");
                        fleaTip.Append(exact ? ".\nPrice from Better Flea." : ".\nPrice from the base game.");
                    }

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
                        else if (!item.CanSellOnRagfair)
                            fleaTip.Append("\nTake the banned parts off before listing it.");
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

                double? primaryEach = Prices.ToPrimary(roubles / Math.Max(1, count));
                double? primaryTotal = Prices.ToPrimary(roubles);
                if (primaryEach == null || primaryTotal == null)
                {
                    info.TraderValue = info.TraderText = "...";
                    info.Loading = info.TraderLoading = true;
                }
                else if (best.Currency == primary)
                {
                    info.TraderValue = Amount(traderEach, traderTotal, count, null) + info.PerSlot(traderTotal, null);
                    info.TraderText = Amount(traderEach, traderTotal, count, best.Currency) + info.PerSlot(traderTotal, best.Currency);
                }
                else
                {
                    info.TraderValue = Amount(primaryEach.Value, primaryTotal.Value, count, null) + info.PerSlot(primaryTotal.Value, null);
                    info.TraderText = Amount(primaryEach.Value, primaryTotal.Value, count, primary) + info.PerSlot(primaryTotal.Value, primary);
                }

                traderTip.Append(best.Trader.LocalizedName).Append(" pays the most: ").Append(Prices.Money(traderTotal, best.Currency));
                if (best.Currency != primary && primaryTotal != null)
                    traderTip.Append(" (").Append(Prices.Money(primaryTotal.Value, primary)).Append(')');
                if (count > 1)
                    traderTip.Append(", ").Append(Prices.Money(traderEach, best.Currency)).Append(" each");
                traderTip.Append('.');

                if (info.FleaKeep > 0)
                {
                    double gap = Math.Abs(info.FleaKeep - roubles);
                    string gapText = Prices.PrimaryMoney(gap);
                    traderTip.Append('\n').Append(info.FleaKeep > roubles
                        ? "The Flea Market gets you " + gapText + " more after its fee."
                        : best.Trader.LocalizedName + " gets you " + gapText + " more than the Flea Market after its fee.");
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
            ColourValues(info, item, count, flea, onReady);
            return info;
        }

        private const int AmmoRounds = 100;

        private static void ColourValues(PriceInfo info, Item item, int count, bool flea, Action onReady)
        {
            info.ColourFlea = info.FleaTotal;
            info.ColourTrader = info.TraderRoubles;
            info.IsAmmo = item is Ammo;
            info.Kind = Tiers.KindOf(item);
            if (Plugin.ColourFullStacks.Value && item.StackMaxSize > 1)
            {
                double scale = (item is Ammo ? AmmoRounds : item.StackMaxSize) / (double)count;
                info.ColourFlea *= scale;
                info.ColourTrader *= scale;
            }

            if (!Plugin.ColourFullMags.Value)
                return;

            foreach (Magazine mag in item.GetAllItems().OfType<Magazine>())
            {
                if (mag != item && !Prices.Attached(mag, item))
                    continue;
                int missing = mag.MaxCount - mag.Count;
                Item round = mag.FirstRealAmmo();
                if (missing <= 0 || round == null)
                    continue;

                if (flea && info.ColourFlea != null)
                {
                    FleaState state = Prices.TryGetFlea(round, out double each, out _, onReady, false);
                    if (state == FleaState.Loading)
                        info.Loading = info.FleaLoading = true;
                    else if (state != FleaState.Unavailable)
                        info.ColourFlea += each * missing;
                }

                if (info.ColourTrader != null)
                {
                    if (Prices.TryGetBestTrader(round, out TraderOffer offer, out bool loading, onReady))
                        info.ColourTrader += offer.Roubles / Math.Max(1, round.StackObjectsCount) * missing;
                    else if (loading)
                        info.Loading = info.TraderLoading = true;
                }
            }
        }

        private static bool ShopStock(Item item)
        {
            EOwnerType? owner = item.Owner?.OwnerType;
            return owner == EOwnerType.Trader || owner == EOwnerType.RagFair || item.StackObjectsCount > item.StackMaxSize;
        }

        private string PerSlot(double total, MongoID? currency)
        {
            if (!Plugin.PerSlot.Value || Slots <= 1)
                return string.Empty;
            double each = total / Slots;
            return "  ·  " + (currency == null ? Prices.Money(each) : Prices.Money(each, currency.Value)) + "/slot";
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
