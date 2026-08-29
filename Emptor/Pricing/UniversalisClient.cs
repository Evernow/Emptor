using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Networking.Http;

namespace Emptor.Pricing;

/// <summary>Which geographic level a Universalis aggregate is for.</summary>
public enum PriceLevel { World, Datacenter, Region }

/// <summary>One aggregate figure (min listing / recent purchase) with its source world.</summary>
public sealed record AggPoint(long Price, uint? WorldId, DateTimeOffset? Time);

/// <summary>Per-item aggregated market data from Universalis, for one queried location.</summary>
public sealed class ItemAggregate
{
    public required Dictionary<PriceLevel, AggPoint?> MinNq { get; init; }
    public required Dictionary<PriceLevel, AggPoint?> MinHq { get; init; }
    public required Dictionary<PriceLevel, AggPoint?> RecentNq { get; init; }
    public required Dictionary<PriceLevel, AggPoint?> RecentHq { get; init; }
    public required Dictionary<PriceLevel, double?> AvgNq { get; init; }
    public required Dictionary<PriceLevel, double?> AvgHq { get; init; }
    public required Dictionary<PriceLevel, double?> VelNq { get; init; }
    public required Dictionary<PriceLevel, double?> VelHq { get; init; }
}

/// <summary>
/// Thin Universalis client — the v2 "aggregated" endpoint, which returns the
/// world / DC / region min-listing, recent purchase, average sale price and
/// daily sale velocity for up to 100 items in one call. Adapted from
/// ffxiv-priceinsight's UniversalisClientV2.
/// </summary>
public sealed class UniversalisClient : IDisposable
{
    private readonly HappyEyeballsCallback happyEyeballs = new();
    private readonly HttpClient http;

    public UniversalisClient()
    {
        http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = happyEyeballs.ConnectCallback,
        });
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Emptor/{v} ({Environment.OSVersion})");
        http.Timeout = TimeSpan.FromSeconds(20);
    }

    /// <param name="location">A world name, data-centre name, or region name (e.g. "Gilgamesh", "Aether", "North-America").</param>
    public async Task<Dictionary<uint, ItemAggregate>?> FetchAggregated(
        string location, IReadOnlyCollection<uint> itemIds, CancellationToken ct)
    {
        if (itemIds.Count == 0)
            return new();

        var url = $"https://universalis.app/api/v2/aggregated/{Uri.EscapeDataString(location)}/"
                  + string.Join(',', itemIds);

        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                Plugin.Log.Warning($"[Emptor] Universalis {location}: HTTP {(int)resp.StatusCode}");
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<AggregatedDto>(stream, JsonOpts, ct).ConfigureAwait(false);
            if (dto?.Results is null)
                return null;

            var map = new Dictionary<uint, ItemAggregate>();
            foreach (var r in dto.Results)
                map[r.ItemId] = r.ToAggregate();
            return map;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[Emptor] Universalis fetch failed ({location}).");
            return null;
        }
    }

    public void Dispose()
    {
        http.Dispose();
        happyEyeballs.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // ---- wire format (universalis v2 aggregated) --------------------------

    private sealed class AggregatedDto
    {
        [JsonPropertyName("results")] public List<ResultDto>? Results { get; set; }
    }

    private sealed class ResultDto
    {
        [JsonPropertyName("itemId")] public uint ItemId { get; set; }
        [JsonPropertyName("nq")] public AggDto? Nq { get; set; }
        [JsonPropertyName("hq")] public AggDto? Hq { get; set; }

        public ItemAggregate ToAggregate() => new()
        {
            MinNq = Levels(Nq?.MinListing, e => new AggPoint((long)(e.Price ?? 0), e.WorldId, e.When)),
            MinHq = Levels(Hq?.MinListing, e => new AggPoint((long)(e.Price ?? 0), e.WorldId, e.When)),
            RecentNq = Levels(Nq?.RecentPurchase, e => new AggPoint((long)(e.Price ?? 0), e.WorldId, e.When)),
            RecentHq = Levels(Hq?.RecentPurchase, e => new AggPoint((long)(e.Price ?? 0), e.WorldId, e.When)),
            AvgNq = LevelsD(Nq?.AverageSalePrice),
            AvgHq = LevelsD(Hq?.AverageSalePrice),
            VelNq = LevelsD(Nq?.DailySaleVelocity),
            VelHq = LevelsD(Hq?.DailySaleVelocity),
        };

        private static Dictionary<PriceLevel, AggPoint?> Levels(ValueDto? v, Func<EntryDto, AggPoint> f)
        {
            AggPoint? One(EntryDto? e) => e?.Price is > 0 ? f(e) : null;
            return new()
            {
                [PriceLevel.World] = One(v?.World),
                [PriceLevel.Datacenter] = One(v?.Dc),
                [PriceLevel.Region] = One(v?.Region),
            };
        }

        private static Dictionary<PriceLevel, double?> LevelsD(ValueDto? v)
        {
            double? One(EntryDto? e) => e?.Price is > 0 ? e.Price : (e?.Quantity is > 0 ? e.Quantity : null);
            return new()
            {
                [PriceLevel.World] = One(v?.World),
                [PriceLevel.Datacenter] = One(v?.Dc),
                [PriceLevel.Region] = One(v?.Region),
            };
        }
    }

    private sealed class AggDto
    {
        [JsonPropertyName("minListing")] public ValueDto? MinListing { get; set; }
        [JsonPropertyName("recentPurchase")] public ValueDto? RecentPurchase { get; set; }
        [JsonPropertyName("averageSalePrice")] public ValueDto? AverageSalePrice { get; set; }
        [JsonPropertyName("dailySaleVelocity")] public ValueDto? DailySaleVelocity { get; set; }
    }

    private sealed class ValueDto
    {
        [JsonPropertyName("world")] public EntryDto? World { get; set; }
        [JsonPropertyName("dc")] public EntryDto? Dc { get; set; }
        [JsonPropertyName("region")] public EntryDto? Region { get; set; }
    }

    private sealed class EntryDto
    {
        [JsonPropertyName("price")] public double? Price { get; set; }
        [JsonPropertyName("quantity")] public double? Quantity { get; set; }
        [JsonPropertyName("worldId")] public uint? WorldId { get; set; }
        [JsonPropertyName("timestamp")] public long? Timestamp { get; set; }

        public DateTimeOffset? When => Timestamp is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(Timestamp.Value)
            : null;
    }
}
