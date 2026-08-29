using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Ipc;
using Emptor.Buying;
using Emptor.GameData;
using Emptor.Pricing;

namespace Emptor.Ipc;

/// <summary>
/// Dalamud IPC surface. All payloads are JSON strings so callers never need a
/// shared contract assembly. Prefix: <c>Emptor.</c>
/// </summary>
public sealed class EmptorIpc : IDisposable
{
    // v2: request accepts "skipTravel".
    // v3: with skipTravel omitted/false Emptor now travels to a board itself
    //     (vnavmesh walk, or Lifestream "/li mb" when no board is in the zone).
    // v4: request accepts "city" (pin travel to one city's board); Emptor.GetCities.
    // v5: request accepts "world" + "returnToHomeWorld"; Emptor.GetReachableWorlds
    //     and Emptor.LookupPrices (Universalis, no board needed).
    public const int Version = 5;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly OrderQueue queue;
    private readonly PriceService prices;

    private readonly ICallGateProvider<int> apiVersion;
    private readonly ICallGateProvider<bool> isBusy;
    private readonly ICallGateProvider<string> getCities;
    private readonly ICallGateProvider<string> getReachableWorlds;
    private readonly ICallGateProvider<string, string> lookupPrices;
    private readonly ICallGateProvider<string, string> submitOrder;
    private readonly ICallGateProvider<string, string> getOrder;
    private readonly ICallGateProvider<string, bool> cancelOrder;
    private readonly ICallGateProvider<string, object?> orderCompleted;

    public EmptorIpc(OrderQueue queue, MarketBuyRunner runner, PriceService prices)
    {
        this.queue = queue;
        this.prices = prices;

        var pi = Plugin.PluginInterface;
        apiVersion = pi.GetIpcProvider<int>("Emptor.ApiVersion");
        isBusy = pi.GetIpcProvider<bool>("Emptor.IsBusy");
        getCities = pi.GetIpcProvider<string>("Emptor.GetCities");
        getReachableWorlds = pi.GetIpcProvider<string>("Emptor.GetReachableWorlds");
        lookupPrices = pi.GetIpcProvider<string, string>("Emptor.LookupPrices");
        submitOrder = pi.GetIpcProvider<string, string>("Emptor.SubmitOrder");
        getOrder = pi.GetIpcProvider<string, string>("Emptor.GetOrder");
        cancelOrder = pi.GetIpcProvider<string, bool>("Emptor.CancelOrder");
        orderCompleted = pi.GetIpcProvider<string, object?>("Emptor.OrderCompleted");

        apiVersion.RegisterFunc(() => Version);
        isBusy.RegisterFunc(() => queue.IsBusy);
        getCities.RegisterFunc(GetCities);
        getReachableWorlds.RegisterFunc(GetReachableWorlds);
        lookupPrices.RegisterFunc(LookupPrices);
        submitOrder.RegisterFunc(SubmitOrder);
        getOrder.RegisterFunc(GetOrder);
        cancelOrder.RegisterFunc(queue.Cancel);

        runner.OrderFinished += OnOrderFinished;
    }

    public void Dispose()
    {
        apiVersion.UnregisterFunc();
        isBusy.UnregisterFunc();
        getCities.UnregisterFunc();
        getReachableWorlds.UnregisterFunc();
        lookupPrices.UnregisterFunc();
        submitOrder.UnregisterFunc();
        getOrder.UnregisterFunc();
        cancelOrder.UnregisterFunc();
    }

    private void OnOrderFinished(BuyOrder order)
    {
        try
        {
            orderCompleted.SendMessage(order.OrderId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Emptor] OrderCompleted IPC notify failed.");
        }
    }

    // ---- handlers -----------------------------------------------------

    private string SubmitOrder(string requestJson)
    {
        RequestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RequestDto>(requestJson, JsonOpts);
        }
        catch (Exception ex)
        {
            return Rejected($"Invalid request JSON: {ex.Message}");
        }

        if (dto?.Items is not { Count: > 0 })
            return Rejected("Request had no items.");

        var city = GameData.MarketCities.Resolve(dto.City);
        if (!string.IsNullOrWhiteSpace(dto.City) && city is null)
            return Rejected($"Unknown city '{dto.City}'. Known: {GameData.MarketCities.KnownKeys()}.");

        string? worldName = null;
        if (!string.IsNullOrWhiteSpace(dto.World))
        {
            var w = Worlds.Resolve(dto.World);
            if (w is null)
                return Rejected($"Unknown world '{dto.World}'.");
            if (!Worlds.IsReachable(w))
                return Rejected(
                    $"World '{w.Name}' ({w.DcName}) is not reachable — data-centre travel only reaches your "
                    + "home region's data centres and Materia. See Emptor.GetReachableWorlds.");
            worldName = w.Name;
        }

        var request = new BuyRequest
        {
            ClientRequestId = dto.ClientRequestId,
            TotalGilBudget = dto.TotalGilBudget ?? 0,
            SkipTravel = dto.SkipTravel ?? false,
            City = city?.Key,
            World = worldName,
            ReturnToHomeWorld = dto.ReturnToHomeWorld ?? false,
        };

        foreach (var i in dto.Items)
        {
            var resolvedId = i.ItemId ?? 0u;
            if (resolvedId == 0 && !string.IsNullOrWhiteSpace(i.ItemName))
                resolvedId = ItemResolver.ResolveExact(i.ItemName!);
            if (resolvedId == 0)
                return Rejected($"Could not resolve item '{i.ItemName ?? i.ItemId?.ToString(CultureInfo.InvariantCulture) ?? "?"}'.");
            if (!ItemResolver.IsMarketable(resolvedId))
                return Rejected($"Item {resolvedId} is not marketable.");
            if (i.MaxUnitPrice < 0 || i.Quantity < 0)
                return Rejected("maxUnitPrice and quantity must be >= 0.");

            request.Items.Add(new BuyRequestItem
            {
                ItemId = resolvedId,
                ItemName = i.ItemName,
                MaxUnitPrice = i.MaxUnitPrice,
                Quantity = i.Quantity,
                Quality = ParseQuality(i.Quality),
                Overshoot = ParseOvershoot(i.Overshoot),
                OvershootLimitPercent = i.OvershootLimitPercent ?? 25,
            });
        }

        var order = queue.Submit(request, fromUi: false);
        return JsonSerializer.Serialize(ToDto(order), JsonOpts);
    }

    private static string GetCities()
        => JsonSerializer.Serialize(
            GameData.MarketCities.All.Select(c => new CityDto
            {
                Key = c.Key,
                Display = c.Display,
                Route = c.RouteKind.ToString(),
            }).ToList(),
            JsonOpts);

    private static string GetReachableWorlds()
    {
        var home = Worlds.HomeWorld();
        var current = Worlds.CurrentWorld();
        if (home is null)
            return JsonSerializer.Serialize(new ReachableWorldsDto { Error = "Not logged in." }, JsonOpts);

        var reachableDcs = Worlds.ReachableDatacenters(home)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dto = new ReachableWorldsDto
        {
            HomeWorld = home.Name,
            CurrentWorld = current?.Name ?? home.Name,
            Region = home.RegionName,
            Datacenters = Worlds.AllPublic()
                .Where(w => reachableDcs.Contains(w.DcName))
                .GroupBy(w => (w.RegionName, w.DcName))
                .Select(g => new DcDto
                {
                    Datacenter = g.Key.DcName,
                    Region = g.Key.RegionName,
                    Worlds = g.Select(w => w.Name).OrderBy(n => n).ToList(),
                })
                .OrderBy(d => d.Region).ThenBy(d => d.Datacenter)
                .ToList(),
        };
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    private string LookupPrices(string requestJson)
    {
        PriceRequestDto? dto;
        try { dto = JsonSerializer.Deserialize<PriceRequestDto>(requestJson, JsonOpts); }
        catch (Exception ex) { return JsonSerializer.Serialize(new PriceReplyDto { Error = $"Invalid JSON: {ex.Message}" }, JsonOpts); }

        if (dto?.Items is not { Count: > 0 })
            return JsonSerializer.Serialize(new PriceReplyDto { Error = "No items." }, JsonOpts);

        var ids = new List<uint>();
        foreach (var it in dto.Items)
        {
            var id = it.ItemId ?? 0u;
            if (id == 0 && !string.IsNullOrWhiteSpace(it.ItemName))
                id = ItemResolver.ResolveExact(it.ItemName!);
            if (id == 0 || !ItemResolver.IsMarketable(id))
                return JsonSerializer.Serialize(
                    new PriceReplyDto { Error = $"Item '{it.ItemName ?? it.ItemId?.ToString(CultureInfo.InvariantCulture) ?? "?"}' is not a marketable item." },
                    JsonOpts);
            ids.Add(id);
        }

        var scope = ParseScope(dto.Scope);
        var result = prices.Lookup(new PriceRequest(ids, scope, dto.Target), dto.Refresh ?? false);

        return JsonSerializer.Serialize(new PriceReplyDto
        {
            Scope = scope.ToString().ToLowerInvariant(),
            Error = result.Error,
            Pending = result.Pending.Count == 0 ? null : result.Pending,
            Items = result.Ready.Count == 0 ? null : result.Ready.Values.ToList(),
        }, JsonOpts);
    }

    private PriceScope ParseScope(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "world" => PriceScope.World,
        "datacenter" or "dc" => PriceScope.Datacenter,
        "region" => PriceScope.Region,
        "reachable" or "all" => PriceScope.Reachable,
        _ => Plugin.Instance.Configuration.DefaultPriceScope,
    };

    private string GetOrder(string orderId)
    {
        var order = queue.Get(orderId);
        return order is null
            ? Rejected($"No order with id '{orderId}'.")
            : JsonSerializer.Serialize(ToDto(order), JsonOpts);
    }

    private static string Rejected(string message)
        => JsonSerializer.Serialize(new OrderDto { State = "rejected", Message = message }, JsonOpts);

    private static QualityFilter ParseQuality(string? s) => s?.ToLowerInvariant() switch
    {
        "nq" => QualityFilter.NqOnly,
        "hq" => QualityFilter.HqOnly,
        _ => QualityFilter.Either,
    };

    private static OvershootPolicy ParseOvershoot(string? s) => s?.ToLowerInvariant() switch
    {
        "skip" => OvershootPolicy.Skip,
        "limit" => OvershootPolicy.Limit,
        _ => OvershootPolicy.Allow,
    };

    // ---- mapping -----------------------------------------------------

    private static OrderDto ToDto(BuyOrder o) => new()
    {
        OrderId = o.OrderId,
        ClientRequestId = o.ClientRequestId,
        State = o.State.ToString().ToLowerInvariant(),
        Message = o.Message,
        City = o.Request.City,
        World = o.Request.World,
        StartedUtc = o.StartedUtc,
        FinishedUtc = o.FinishedUtc,
        TotalGilSpent = o.TotalGilSpent,
        TotalGilBudget = o.TotalGilBudget == 0 ? null : o.TotalGilBudget,
        Items = o.Items.Select(r => new ItemResultDto
        {
            ItemId = r.ItemId,
            ItemName = r.ItemName,
            RequestedQuantity = r.RequestedQuantity,
            PurchasedQuantity = r.PurchasedQuantity,
            TotalGilSpent = r.TotalGilSpent,
            Purchases = r.Purchases.Select(p => new PurchaseDto
            {
                UnitPrice = p.UnitPrice,
                Quantity = p.Quantity,
                Hq = p.Hq,
                TotalGil = p.TotalGil,
                RetainerId = p.RetainerId,
            }).ToList(),
            NextLowestUnitPrice = r.NextLowestUnitPrice,
            NextLowestQuantity = r.NextLowestQuantity,
            NextLowestHq = r.NextLowestHq,
            ListingsExhausted = r.ListingsExhausted,
            StoppedReason = r.StoppedReason.ToString(),
            AvailableListings = r.AvailableListings?.Select(p => new PurchaseDto
            {
                UnitPrice = p.UnitPrice,
                Quantity = p.Quantity,
                Hq = p.Hq,
                TotalGil = p.TotalGil,
            }).ToList(),
        }).ToList(),
    };

    // ---- DTOs -------------------------------------------------------

    private sealed class RequestDto
    {
        public string? ClientRequestId { get; set; }
        public long? TotalGilBudget { get; set; }
        public bool? SkipTravel { get; set; }
        public string? City { get; set; }
        public string? World { get; set; }
        public bool? ReturnToHomeWorld { get; set; }
        public List<RequestItemDto>? Items { get; set; }
    }

    private sealed class CityDto
    {
        public string Key { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
        public string Route { get; set; } = string.Empty;
    }

    private sealed class ReachableWorldsDto
    {
        public string? Error { get; set; }
        public string? HomeWorld { get; set; }
        public string? CurrentWorld { get; set; }
        public string? Region { get; set; }
        public List<DcDto>? Datacenters { get; set; }
    }

    private sealed class DcDto
    {
        public string Datacenter { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public List<string> Worlds { get; set; } = new();
    }

    private sealed class PriceRequestDto
    {
        public string? Scope { get; set; }
        public string? Target { get; set; }
        public bool? Refresh { get; set; }
        public List<RequestItemDto>? Items { get; set; }
    }

    private sealed class PriceReplyDto
    {
        public string? Scope { get; set; }
        public string? Error { get; set; }
        public List<uint>? Pending { get; set; }
        public List<Pricing.ItemPrices>? Items { get; set; }
    }

    private sealed class RequestItemDto
    {
        public uint? ItemId { get; set; }
        public string? ItemName { get; set; }
        public long MaxUnitPrice { get; set; }
        public int Quantity { get; set; }
        public string? Quality { get; set; }
        public string? Overshoot { get; set; }
        public int? OvershootLimitPercent { get; set; }
    }

    private sealed class OrderDto
    {
        public string? OrderId { get; set; }
        public string? ClientRequestId { get; set; }
        public string State { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? City { get; set; }
        public string? World { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public long TotalGilSpent { get; set; }
        public long? TotalGilBudget { get; set; }
        public List<ItemResultDto>? Items { get; set; }
    }

    private sealed class ItemResultDto
    {
        public uint ItemId { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public int RequestedQuantity { get; set; }
        public int PurchasedQuantity { get; set; }
        public long TotalGilSpent { get; set; }
        public List<PurchaseDto> Purchases { get; set; } = new();
        public long? NextLowestUnitPrice { get; set; }
        public int? NextLowestQuantity { get; set; }
        public bool? NextLowestHq { get; set; }
        public bool ListingsExhausted { get; set; }
        public string StoppedReason { get; set; } = string.Empty;
        public List<PurchaseDto>? AvailableListings { get; set; }
    }

    private sealed class PurchaseDto
    {
        public long UnitPrice { get; set; }
        public int Quantity { get; set; }
        public bool Hq { get; set; }
        public long TotalGil { get; set; }
        public string? RetainerId { get; set; }
    }
}
