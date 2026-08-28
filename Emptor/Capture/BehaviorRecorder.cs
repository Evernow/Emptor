using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Emptor.Capture;

/// <summary>
/// Records everything that happens around a marketboard purchase — manual or
/// automated — into one JSON session file plus a human-readable summary.
/// </summary>
public sealed class BehaviorRecorder : IDisposable
{
    private static readonly string[] WatchedAddons =
    {
        "ItemSearch", "ItemSearchResult", "SelectYesno", "SelectOk",
        "ContextMenu", "Talk", "RetainerSell", "Journal",
    };

    private static readonly AddonEvent[] WatchedAddonEvents =
    {
        AddonEvent.PostSetup, AddonEvent.PreFinalize, AddonEvent.PostReceiveEvent,
    };

    private readonly object gate = new();
    private readonly Stopwatch clock = new();
    private CaptureSession? session;
    private long lastEventMs;

    // change-detection state for the sampler
    private string? lastTargetName;
    private int lastListingCount = -1;
    private bool lastWaiting;
    private ulong lastFirstListingId;
    private Vector3 lastSampledPos;
    private string? lastSearchSnapshot;

    // timeline anchors (ms since start), -1 = not seen
    private long aBoardTargeted = -1, aSearchOpen = -1, aResultOpen = -1,
        aFirstOfferings = -1, aLastOfferings = -1, aRowClick = -1, aConfirmOpen = -1,
        aPurchaseRequested = -1, aItemPurchased = -1;

    private readonly Dictionary<int, List<CaptureListing>> offeringsByRequest = new();
    private int lastOfferingsRequestId;
    private IMarketBoardPurchaseHandler? purchaseHandler;

    public bool IsRecording { get; private set; }
    public int EventCount => session?.Events.Count ?? 0;
    public string? LastSavedPath { get; private set; }
    public CaptureSummary? LastSummary { get; private set; }

    public string Start(string mode)
    {
        lock (gate)
        {
            if (IsRecording)
                return "Already recording.";

            session = new CaptureSession
            {
                Mode = mode,
                StartedUtc = DateTime.UtcNow,
                Territory = Plugin.ClientState.TerritoryType,
                CharacterName = Plugin.ObjectTable.LocalPlayer?.Name.TextValue,
            };
            ResetAnchors();
            clock.Restart();
            lastEventMs = 0;
            IsRecording = true;
        }

        foreach (var ev in WatchedAddonEvents)
            Plugin.AddonLifecycle.RegisterListener(ev, WatchedAddons, OnAddon);

        Plugin.MarketBoard.OfferingsReceived += OnOfferings;
        Plugin.MarketBoard.HistoryReceived += OnHistory;
        Plugin.MarketBoard.PurchaseRequested += OnPurchaseRequested;
        Plugin.MarketBoard.ItemPurchased += OnItemPurchased;
        Plugin.MarketBoard.TaxRatesReceived += OnTaxRates;
        Plugin.Framework.Update += OnTick;

        Add("note", "capture-start", new() { ["mode"] = mode });
        return $"Recording ({mode}).";
    }

    public string Stop()
    {
        CaptureSession? finished;
        lock (gate)
        {
            if (!IsRecording || session is null)
                return "Not recording.";
            IsRecording = false;
            finished = session;
            session = null;
        }

        Plugin.Framework.Update -= OnTick;
        Plugin.MarketBoard.OfferingsReceived -= OnOfferings;
        Plugin.MarketBoard.HistoryReceived -= OnHistory;
        Plugin.MarketBoard.PurchaseRequested -= OnPurchaseRequested;
        Plugin.MarketBoard.ItemPurchased -= OnItemPurchased;
        Plugin.MarketBoard.TaxRatesReceived -= OnTaxRates;
        foreach (var ev in WatchedAddonEvents)
            Plugin.AddonLifecycle.UnregisterListener(ev, WatchedAddons, OnAddon);

        finished.FinishedUtc = DateTime.UtcNow;
        finished.DurationMs = clock.ElapsedMilliseconds;
        clock.Stop();

        BuildSummary(finished);
        LastSummary = finished.Summary;

        try
        {
            var dir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "captures");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"session-{finished.StartedUtc:yyyyMMdd-HHmmss}-{finished.Mode}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(finished, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            LastSavedPath = path;
            return $"Saved {finished.Events.Count} events to {path}";
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Emptor] Failed to save capture.");
            return $"Capture finished but could not be saved: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (IsRecording)
            Stop();
    }

    // ---- event sinks --------------------------------------------------

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        var payload = new Dictionary<string, object?>
        {
            ["addon"] = args.AddonName,
            ["lifecycle"] = type.ToString(),
        };

        if (args is AddonReceiveEventArgs re)
        {
            payload["atkEventType"] = re.AtkEventType.ToString();
            payload["eventParam"] = re.EventParam;

            var t = re.AtkEventType.ToString();
            if (t.Contains("Scroll", StringComparison.OrdinalIgnoreCase) || t.Contains("Wheel", StringComparison.OrdinalIgnoreCase))
                MarkFlag(s => s.Scrolled = true);
            if (t.Contains("MouseOver", StringComparison.OrdinalIgnoreCase) && args.AddonName == "ItemSearchResult")
                MarkFlag(s => s.HoveredRows = true);
            if (args.AddonName == "ItemSearchResult" &&
                (t.Contains("ListItemClick", StringComparison.OrdinalIgnoreCase) || t.Contains("ListItemToggle", StringComparison.OrdinalIgnoreCase)))
                SetAnchor(ref aRowClick);
            if (args.AddonName == "ItemSearch" && t.Contains("ButtonClick", StringComparison.OrdinalIgnoreCase))
                MarkFlag(s => s.ChangedSearchModeOrFilter = true);
        }

        if (args.AddonName == "ItemSearch" && type == AddonEvent.PostSetup)
            SetAnchor(ref aSearchOpen);
        if (args.AddonName == "ItemSearchResult" && type == AddonEvent.PostSetup)
            SetAnchor(ref aResultOpen);
        if (args.AddonName == "SelectYesno" && type == AddonEvent.PostSetup)
            SetAnchor(ref aConfirmOpen);

        Add("addon", $"{args.AddonName}.{type}", payload);
    }

    private void OnOfferings(IMarketBoardCurrentOfferings offerings)
    {
        SetAnchor(ref aFirstOfferings);
        aLastOfferings = clock.ElapsedMilliseconds;
        lastOfferingsRequestId = offerings.RequestId;
        MarkFlag(s => s.OfferingsPagesReceived++);

        lock (gate)
        {
            if (!offeringsByRequest.TryGetValue(offerings.RequestId, out var acc))
                offeringsByRequest[offerings.RequestId] = acc = new();
            foreach (var l in offerings.ItemListings)
            {
                var c = ToCapture(l);
                if (acc.All(x => x.ListingId != c.ListingId))
                    acc.Add(c);
            }
        }

        Add("market", "OfferingsReceived", new()
        {
            ["requestId"] = offerings.RequestId,
            ["count"] = offerings.ItemListings.Count,
            ["listings"] = offerings.ItemListings.Select(ToCapture).ToList(),
        });
    }

    private void OnHistory(IMarketBoardHistory history)
    {
        MarkFlag(s => s.OpenedHistory = true);
        Add("market", "HistoryReceived", new()
        {
            ["itemId"] = history.ItemId,
            ["count"] = history.HistoryListings.Count,
        });
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler h)
    {
        purchaseHandler = h;
        SetAnchor(ref aPurchaseRequested);
        Add("market", "PurchaseRequested", new()
        {
            ["listingId"] = h.ListingId,
            ["retainerId"] = h.RetainerId,
            ["itemId"] = h.CatalogId,
            ["quantity"] = h.ItemQuantity,
            ["pricePerUnit"] = h.PricePerUnit,
            ["totalTax"] = h.TotalTax,
            ["hq"] = h.IsHq,
        });
    }

    private void OnItemPurchased(IMarketBoardPurchase p)
    {
        SetAnchor(ref aItemPurchased);
        Add("market", "ItemPurchased", new()
        {
            ["itemId"] = p.CatalogId,
            ["quantity"] = p.ItemQuantity,
            ["gil"] = GameData.GameState.GetGil(),
        });
    }

    private void OnTaxRates(IMarketTaxRates t) =>
        Add("market", "TaxRatesReceived", new() { ["limsa"] = t.LimsaLominsaTax });

    // ---- per-tick sampler --------------------------------------------

    private unsafe void OnTick(IFramework framework)
    {
        if (!IsRecording)
            return;

        try
        {
            var target = Plugin.TargetManager.Target;
            var targetName = target?.Name.TextValue;
            if (targetName != lastTargetName)
            {
                lastTargetName = targetName;
                var isBoard = target is not null
                    && target.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject or ObjectKind.ReactionEventObject
                    && string.Equals(targetName, "Market Board", StringComparison.OrdinalIgnoreCase);
                if (isBoard)
                    SetAnchor(ref aBoardTargeted);
                Add("sample", "target", new()
                {
                    ["name"] = targetName,
                    ["kind"] = target?.ObjectKind.ToString(),
                    ["isMarketBoard"] = isBoard,
                    ["distanceY"] = target is null || Plugin.ObjectTable.LocalPlayer is null
                        ? (object?)null
                        : Vector3.Distance(target.Position, Plugin.ObjectTable.LocalPlayer.Position),
                });
            }

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player is not null)
            {
                var moved = Vector3.Distance(player.Position, lastSampledPos);
                if (moved > 0.5f)
                {
                    lastSampledPos = player.Position;
                    Add("sample", "player", new()
                    {
                        ["pos"] = new[] { player.Position.X, player.Position.Y, player.Position.Z },
                        ["rot"] = player.Rotation,
                        ["mounted"] = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted],
                    });
                }
            }

            var isAddon = Plugin.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch", 1);
            if (isAddon != null && isAddon->AtkUnitBase.IsVisible)
            {
                var ag = AgentItemSearch.Instance();
                var snap = string.Join("|",
                    $"text={isAddon->SearchText.ToString()}",
                    $"partial={isAddon->PartialMatch}",
                    $"mode={isAddon->Mode}",
                    $"btnEnabled={(isAddon->SearchButton != null && isAddon->SearchButton->IsEnabled)}",
                    $"resultRows={(isAddon->ResultsList != null ? isAddon->ResultsList->GetItemCount() : -1)}",
                    $"agentItems={(ag == null ? -1 : (int)ag->ItemCount)}",
                    $"partialSearching={(ag != null && ag->IsPartialSearching)}",
                    $"pushPending={(ag != null && ag->IsItemPushPending)}");
                if (snap != lastSearchSnapshot)
                {
                    lastSearchSnapshot = snap;
                    Add("sample", "itemSearch", new()
                    {
                        ["text"] = isAddon->SearchText.ToString(),
                        ["partialMatch"] = isAddon->PartialMatch,
                        ["mode"] = isAddon->Mode.ToString(),
                        ["searchButtonEnabled"] = isAddon->SearchButton != null && isAddon->SearchButton->IsEnabled,
                        ["resultRows"] = isAddon->ResultsList != null ? isAddon->ResultsList->GetItemCount() : -1,
                        ["agentItemCount"] = ag == null ? -1 : (int)ag->ItemCount,
                        ["agentPartialSearching"] = ag != null && ag->IsPartialSearching,
                        ["agentItemPushPending"] = ag != null && ag->IsItemPushPending,
                    });
                }
            }

            var proxy = InfoProxyItemSearch.Instance();
            if (proxy != null && proxy->SearchItemId != 0)
            {
                var count = (int)proxy->ListingCount;
                var first = count > 0 ? proxy->Listings[0].ListingId : 0;
                if (count != lastListingCount || proxy->WaitingForListings != lastWaiting || first != lastFirstListingId)
                {
                    lastListingCount = count;
                    lastWaiting = proxy->WaitingForListings;
                    lastFirstListingId = first;

                    var agent = AgentItemSearch.Instance();
                    Add("sample", "infoProxy", new()
                    {
                        ["searchItemId"] = proxy->SearchItemId,
                        ["listingCount"] = count,
                        ["waitingForListings"] = proxy->WaitingForListings,
                        ["agentItemCount"] = agent == null ? 0 : (int)agent->ItemCount,
                        ["agentPartialSearching"] = agent != null && agent->IsPartialSearching,
                        ["agentItemPushPending"] = agent != null && agent->IsItemPushPending,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Emptor] capture sampler tick failed.");
        }
    }

    // ---- helpers ----------------------------------------------------

    public void Note(string text) => Add("note", text, new());

    public void RunnerEvent(string name, Dictionary<string, object?> payload) => Add("runner", name, payload);

    private void Add(string category, string name, Dictionary<string, object?> payload)
    {
        lock (gate)
        {
            if (session is null)
                return;
            var now = clock.ElapsedMilliseconds;
            session.Events.Add(new CaptureEvent
            {
                TMs = now,
                DtMs = now - lastEventMs,
                Category = category,
                Name = name,
                Payload = payload,
            });
            lastEventMs = now;
        }
    }

    private void MarkFlag(Action<CaptureSummary> mut)
    {
        lock (gate)
        {
            if (session is not null)
                mut(session.Summary);
        }
    }

    private void SetAnchor(ref long anchor)
    {
        if (anchor < 0)
            anchor = clock.ElapsedMilliseconds;
    }

    private void ResetAnchors()
    {
        aBoardTargeted = aSearchOpen = aResultOpen = aFirstOfferings = aLastOfferings =
            aRowClick = aConfirmOpen = aPurchaseRequested = aItemPurchased = -1;
        lastTargetName = null;
        lastListingCount = -1;
        lastFirstListingId = 0;
        lastWaiting = false;
        lastSampledPos = default;
        lastSearchSnapshot = null;
        offeringsByRequest.Clear();
        lastOfferingsRequestId = 0;
        purchaseHandler = null;
    }

    private static CaptureListing ToCapture(IMarketBoardItemListing l) => new()
    {
        ListingId = l.ListingId,
        RetainerId = l.RetainerId,
        RetainerName = l.RetainerName,
        UnitPrice = l.PricePerUnit,
        TotalTax = l.TotalTax,
        Quantity = (int)l.ItemQuantity,
        Hq = l.IsHq,
        ItemId = l.ItemId,
    };

    private void BuildSummary(CaptureSession s)
    {
        var sum = s.Summary;

        static long? Delta(long a, long b) => a >= 0 && b >= 0 && b >= a ? b - a : null;
        sum.TargetBoardToSearchOpenMs = Delta(aBoardTargeted, aSearchOpen);
        sum.SearchOpenToFirstSearchMs = Delta(aSearchOpen, aResultOpen);
        sum.SearchToOfferingsMs = Delta(aResultOpen, aFirstOfferings);
        sum.OfferingsToRowClickMs = Delta(aLastOfferings, aRowClick);
        sum.RowClickToConfirmMs = Delta(aRowClick, aConfirmOpen);
        sum.ConfirmToYesMs = Delta(aConfirmOpen, aPurchaseRequested);
        sum.YesToServerConfirmMs = Delta(aPurchaseRequested, aItemPurchased);

        // inter-event gaps on the MB addons
        long prev = -1;
        foreach (var e in s.Events.Where(e => e.Category == "addon" &&
                     (e.Name.StartsWith("ItemSearch") || e.Name.StartsWith("SelectYesno"))))
        {
            if (prev >= 0)
                sum.AddonClickGapsMs.Add(e.TMs - prev);
            prev = e.TMs;
        }

        sum.SearchCount = s.Events.Count(e => e.Name == "ItemSearchResult.PostSetup");

        if (purchaseHandler is { } h)
        {
            sum.PurchaseObserved = true;
            sum.ItemId = h.CatalogId;
            sum.ItemName = GameData.ItemResolver.GetName(h.CatalogId);
            sum.PaidUnitPrice = h.PricePerUnit;
            sum.PaidQuantity = (int)h.ItemQuantity;
            sum.PaidHq = h.IsHq;
            sum.PaidTotalGil = (h.PricePerUnit * h.ItemQuantity) + h.TotalTax;
            sum.PaidListingId = h.ListingId;
        }

        // pick the request that gathered the most listings (the item the user actually browsed),
        // preferring the one that contains the bought listing
        List<CaptureListing>? chosen = null;
        if (sum.PaidListingId is { } lid0)
            chosen = offeringsByRequest.Values.FirstOrDefault(v => v.Any(x => x.ListingId == lid0));
        chosen ??= offeringsByRequest.TryGetValue(lastOfferingsRequestId, out var last) ? last : null;
        chosen ??= offeringsByRequest.Values.OrderByDescending(v => v.Count).FirstOrDefault();

        if (chosen is { Count: > 0 })
        {
            sum.OptionsAtPurchase = chosen.OrderBy(l => l.UnitPrice).ToList();
            if (sum.ItemId is null)
            {
                sum.ItemId = chosen[0].ItemId;
                sum.ItemName = GameData.ItemResolver.GetName(chosen[0].ItemId);
            }
            if (sum.PaidListingId is { } lid)
            {
                var idx = sum.OptionsAtPurchase.FindIndex(l => l.ListingId == lid);
                if (idx >= 0)
                {
                    sum.ChosenRankByPrice = idx + 1;
                    sum.PaidRetainer = sum.OptionsAtPurchase[idx].RetainerName;
                }
            }
        }

        if (!sum.PurchaseObserved)
            sum.Notes.Add("No purchase was observed during this capture.");
        if (sum.ChosenRankByPrice is > 1)
            sum.Notes.Add($"Bought the #{sum.ChosenRankByPrice} cheapest listing, not the cheapest.");
    }
}
