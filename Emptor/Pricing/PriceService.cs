using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emptor.GameData;

namespace Emptor.Pricing;

public sealed class PricePoint
{
    public long Price { get; set; }
    public string? World { get; set; }
    public long? UnixMs { get; set; }
    public string? Age { get; set; }
}

public sealed class QualityPrices
{
    public PricePoint? MinListing { get; set; }
    public PricePoint? RecentPurchase { get; set; }
    public double? AverageSalePrice { get; set; }
    public double? DailySaleVelocity { get; set; }
}

public sealed class LevelPrices
{
    public string Level { get; set; } = "";
    public string Location { get; set; } = "";
    public QualityPrices Nq { get; set; } = new();
    public QualityPrices Hq { get; set; } = new();
}

public sealed class ItemPrices
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public long FetchedUnixMs { get; set; }
    public List<LevelPrices> Levels { get; set; } = new();
    public string? Error { get; set; }
}

public sealed record PriceRequest(IReadOnlyList<uint> ItemIds, PriceScope Scope, string? Target);

public sealed record PriceLookup(Dictionary<uint, ItemPrices> Ready, List<uint> Pending, string? Error);

/// <summary>
/// Universalis price lookups with a small in-memory cache and background fetch.
/// Never touches the game — usable anywhere, market board or not.
/// </summary>
public sealed class PriceService : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(45);

    private readonly UniversalisClient client = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expiry, ItemPrices Data)> cache = new();
    private readonly ConcurrentDictionary<string, Task> inflight = new();
    private readonly CancellationTokenSource cts = new();

    public void Dispose()
    {
        cts.Cancel();
        client.Dispose();
        cts.Dispose();
    }

    /// <summary>
    /// Resolve where a scope actually points for the current player. Returns the
    /// display target, the cache-key discriminator, and the Universalis location
    /// string(s) to query. Null <paramref name="error"/> on success.
    /// </summary>
    public bool ResolveScope(PriceScope scope, string? target, out string display, out string key,
        out IReadOnlyList<string> locations, out string? error)
    {
        display = string.Empty;
        key = string.Empty;
        locations = Array.Empty<string>();
        error = null;

        var current = Worlds.CurrentWorld();
        var home = Worlds.HomeWorld() ?? current;

        switch (scope)
        {
            case PriceScope.World:
            {
                var w = string.IsNullOrWhiteSpace(target) ? current : Worlds.ByName(target) ?? Worlds.Resolve(target);
                if (w is null) { error = $"Unknown world '{target}'."; return false; }
                display = w.Name; key = $"w:{w.Id}"; locations = new[] { w.Name };
                return true;
            }
            case PriceScope.Datacenter:
            {
                var dc = string.IsNullOrWhiteSpace(target) ? current?.DcName : target.Trim();
                if (string.IsNullOrWhiteSpace(dc) || !Worlds.IsDatacenter(dc))
                { error = $"Unknown data centre '{target}'."; return false; }
                display = dc; key = $"d:{dc.ToLowerInvariant()}"; locations = new[] { dc };
                return true;
            }
            case PriceScope.Region:
            {
                var region = string.IsNullOrWhiteSpace(target) ? current?.RegionName : NormalizeRegion(target);
                if (string.IsNullOrWhiteSpace(region) || region == "unknown")
                { error = $"Unknown region '{target}'."; return false; }
                display = region; key = $"r:{region}"; locations = new[] { region };
                return true;
            }
            case PriceScope.Reachable:
            {
                if (home is null) { error = "Not logged in."; return false; }
                var regions = Worlds.ReachableRegions(home);
                display = string.Join(" + ", regions);
                key = $"x:{home.RegionId}";
                locations = regions;
                return true;
            }
            default:
                error = "Unknown scope.";
                return false;
        }
    }

    private static string NormalizeRegion(string s) => s.Trim().ToLowerInvariant() switch
    {
        "jp" or "japan" => "Japan",
        "na" or "north-america" or "north america" or "namerica" => "North-America",
        "eu" or "europe" => "Europe",
        "oce" or "oceania" or "materia" => "Oceania",
        _ => "unknown",
    };

    /// <summary>The geographic level a scope's headline figure comes from.</summary>
    private static PriceLevel LevelFor(PriceScope s) => s switch
    {
        PriceScope.World => PriceLevel.World,
        PriceScope.Datacenter => PriceLevel.Datacenter,
        _ => PriceLevel.Region,
    };

    public PriceLookup Lookup(PriceRequest req, bool refresh = false)
    {
        if (!ResolveScope(req.Scope, req.Target, out var display, out var keyDisc, out var locations, out var error))
            return new PriceLookup(new(), new(), error);

        var ready = new Dictionary<uint, ItemPrices>();
        var pending = new List<uint>();
        var toFetch = new List<uint>();

        foreach (var id in req.ItemIds.Distinct())
        {
            var ck = $"{id}|{req.Scope}|{keyDisc}";
            if (!refresh && cache.TryGetValue(ck, out var hit) && hit.Expiry > DateTimeOffset.UtcNow)
                ready[id] = hit.Data;
            else
            {
                pending.Add(id);
                toFetch.Add(id);
            }
        }

        if (toFetch.Count > 0)
        {
            var batchKey = $"{req.Scope}|{keyDisc}|{string.Join(',', toFetch.OrderBy(x => x))}";
            inflight.GetOrAdd(batchKey, _ => RunFetch(batchKey, req.Scope, display, keyDisc, locations, toFetch));
        }

        return new PriceLookup(ready, pending, null);
    }

    private Task RunFetch(string batchKey, PriceScope scope, string display, string keyDisc,
        IReadOnlyList<string> locations, IReadOnlyList<uint> itemIds)
    {
        return Task.Run(async () =>
        {
            try
            {
                var level = LevelFor(scope);
                // location -> per-item aggregate
                var byLocation = new List<(string Location, Dictionary<uint, ItemAggregate> Data)>();
                foreach (var loc in locations)
                {
                    var d = await client.FetchAggregated(loc, itemIds, cts.Token).ConfigureAwait(false);
                    if (d is not null)
                        byLocation.Add((loc, d));
                }

                var now = DateTimeOffset.UtcNow;
                foreach (var id in itemIds)
                {
                    var ip = new ItemPrices
                    {
                        ItemId = id,
                        ItemName = ItemResolver.GetName(id),
                        Scope = scope.ToString().ToLowerInvariant(),
                        FetchedUnixMs = now.ToUnixTimeMilliseconds(),
                    };

                    if (byLocation.Count == 0)
                    {
                        ip.Error = "Universalis unavailable.";
                    }
                    else if (scope == PriceScope.Reachable)
                    {
                        // Merge the region-level figures from each queried region,
                        // keeping the cheapest.
                        var merged = new LevelPrices { Level = "reachable", Location = display };
                        foreach (var (_, data) in byLocation)
                            if (data.TryGetValue(id, out var agg))
                                MergeCheapest(merged, agg, PriceLevel.Region);
                        ip.Levels.Add(merged);
                    }
                    else
                    {
                        var (loc, data) = byLocation[0];
                        if (data.TryGetValue(id, out var agg))
                        {
                            // Emit every level the query returned (a world query
                            // gives world+dc+region), headline first.
                            foreach (var lvl in LevelsToEmit(level))
                            {
                                var lp = BuildLevel(lvl, agg);
                                if (lp is null) continue;
                                lp.Location = lvl == level ? display : LocationLabel(lvl, agg, loc);
                                ip.Levels.Add(lp);
                            }
                        }
                        if (ip.Levels.Count == 0)
                            ip.Error = "No market data for this item at that scope.";
                    }

                    cache[$"{id}|{scope}|{keyDisc}"] = (now.Add(CacheTtl), ip);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "[Emptor] Price fetch task failed.");
            }
            finally
            {
                inflight.TryRemove(batchKey, out _);
            }
        }, cts.Token);
    }

    private static IEnumerable<PriceLevel> LevelsToEmit(PriceLevel headline) => headline switch
    {
        PriceLevel.World => new[] { PriceLevel.World, PriceLevel.Datacenter, PriceLevel.Region },
        PriceLevel.Datacenter => new[] { PriceLevel.Datacenter, PriceLevel.Region },
        _ => new[] { PriceLevel.Region },
    };

    private static string LocationLabel(PriceLevel lvl, ItemAggregate agg, string queried)
    {
        var wid = agg.MinNq[lvl]?.WorldId ?? agg.MinHq[lvl]?.WorldId;
        var w = wid is { } id ? Worlds.ById(id) : null;
        return lvl switch
        {
            PriceLevel.World => w?.Name ?? queried,
            PriceLevel.Datacenter => w?.DcName ?? queried,
            _ => w?.RegionName ?? queried,
        };
    }

    private static LevelPrices? BuildLevel(PriceLevel lvl, ItemAggregate agg)
    {
        var nq = BuildQuality(lvl, agg.MinNq, agg.RecentNq, agg.AvgNq, agg.VelNq);
        var hq = BuildQuality(lvl, agg.MinHq, agg.RecentHq, agg.AvgHq, agg.VelHq);
        if (IsEmpty(nq) && IsEmpty(hq))
            return null;
        return new LevelPrices { Level = LevelName(lvl), Nq = nq, Hq = hq };
    }

    private static QualityPrices BuildQuality(PriceLevel lvl,
        Dictionary<PriceLevel, AggPoint?> min, Dictionary<PriceLevel, AggPoint?> recent,
        Dictionary<PriceLevel, double?> avg, Dictionary<PriceLevel, double?> vel)
        => new()
        {
            MinListing = ToPoint(min[lvl]),
            RecentPurchase = ToPoint(recent[lvl]),
            AverageSalePrice = avg[lvl],
            DailySaleVelocity = vel[lvl],
        };

    private static void MergeCheapest(LevelPrices into, ItemAggregate agg, PriceLevel lvl)
    {
        Take(into.Nq, agg.MinNq[lvl], agg.RecentNq[lvl], agg.AvgNq[lvl], agg.VelNq[lvl]);
        Take(into.Hq, agg.MinHq[lvl], agg.RecentHq[lvl], agg.AvgHq[lvl], agg.VelHq[lvl]);
    }

    private static void Take(QualityPrices q, AggPoint? min, AggPoint? recent, double? avg, double? vel)
    {
        if (min is not null && (q.MinListing is null || min.Price < q.MinListing.Price))
            q.MinListing = ToPoint(min);

        var recentTime = recent?.Time ?? DateTimeOffset.MinValue;
        var haveTime = q.RecentPurchase?.UnixMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.MinValue;
        if (recent is not null && (q.RecentPurchase is null || recentTime > haveTime))
            q.RecentPurchase = ToPoint(recent);

        q.AverageSalePrice ??= avg;
        q.DailySaleVelocity ??= vel;
    }

    private static PricePoint? ToPoint(AggPoint? p)
    {
        if (p is null || p.Price <= 0)
            return null;
        var w = p.WorldId is { } id ? Worlds.ById(id) : null;
        return new PricePoint
        {
            Price = p.Price,
            World = w?.Name,
            UnixMs = p.Time?.ToUnixTimeMilliseconds(),
            Age = p.Time is { } t ? Humanize(DateTimeOffset.UtcNow - t) : null,
        };
    }

    private static bool IsEmpty(QualityPrices q)
        => q.MinListing is null && q.RecentPurchase is null && q.AverageSalePrice is null && q.DailySaleVelocity is null;

    private static string LevelName(PriceLevel l) => l switch
    {
        PriceLevel.World => "world",
        PriceLevel.Datacenter => "datacenter",
        _ => "region",
    };

    private static string Humanize(TimeSpan ago)
    {
        if (ago < TimeSpan.Zero) ago = TimeSpan.Zero;
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24) return $"{(int)ago.TotalHours}h ago";
        return $"{(int)ago.TotalDays}d ago";
    }
}
