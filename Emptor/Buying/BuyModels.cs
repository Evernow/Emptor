using System;
using System.Collections.Generic;

namespace Emptor.Buying;

/// <summary>Quality filter for a shopping-list entry.</summary>
public enum QualityFilter
{
    /// <summary>Buy the cheapest acceptable listing regardless of quality.</summary>
    Either = 0,

    /// <summary>Only buy normal-quality listings.</summary>
    NqOnly = 1,

    /// <summary>Only buy high-quality listings.</summary>
    HqOnly = 2,
}

/// <summary>
/// What to do when the cheapest acceptable listing is a stack larger than the
/// quantity still needed.
/// </summary>
public enum OvershootPolicy
{
    /// <summary>Always take the listing, even if it overshoots the target.</summary>
    Allow = 0,

    /// <summary>Only take listings whose quantity fits the remaining need.</summary>
    Skip = 1,

    /// <summary>
    /// Take an overshooting listing only if it exceeds the remaining need by no
    /// more than <see cref="BuyRequestItem.OvershootLimitPercent"/>.
    /// </summary>
    Limit = 2,
}

/// <summary>Terminal reason a single shopping-list item stopped buying.</summary>
public enum StopReason
{
    None = 0,
    QuantityMet,
    PriceExceeded,
    NoListings,
    BudgetExceeded,
    Overshoot,
    PromptMismatch,
    Blocked,
    Cancelled,
    Indeterminate,
    ItemUnresolved,
    OpenFailed,
    SearchFailed,

    /// <summary>The current zone has no Market Board and none could be reached.</summary>
    NoBoardInZone,

    /// <summary>Lifestream travel to a Market Board was attempted but did not arrive.</summary>
    TravelFailed,
}

/// <summary>One line of a buy request (from the config window or the IPC API).</summary>
public sealed class BuyRequestItem
{
    public uint ItemId { get; set; }

    /// <summary>Optional; resolved to <see cref="ItemId"/> when the id is 0.</summary>
    public string? ItemName { get; set; }

    public long MaxUnitPrice { get; set; }

    public int Quantity { get; set; }

    public QualityFilter Quality { get; set; } = QualityFilter.Either;

    public OvershootPolicy Overshoot { get; set; } = OvershootPolicy.Allow;

    public int OvershootLimitPercent { get; set; } = 25;
}

/// <summary>A whole buy order: several items plus optional overall budget.</summary>
public sealed class BuyRequest
{
    public string? ClientRequestId { get; set; }

    /// <summary>Optional cap on total gil spent across the whole order. 0 = no cap.</summary>
    public long TotalGilBudget { get; set; }

    /// <summary>
    /// The caller has already positioned the character at a Market Board (e.g. via
    /// Lifestream). Emptor won't pathfind — it just interacts with a board that is
    /// already in range (or an already-open board) and fails fast otherwise.
    /// </summary>
    public bool SkipTravel { get; set; }

    /// <summary>
    /// Restrict travel to this city's Market Board — a key or name from
    /// <see cref="Emptor.GameData.MarketCities"/> (e.g. "kugane", "gridania").
    /// Null / empty = Emptor's default ("/li mb", i.e. Ul'dah). Listings are
    /// world-wide identical, so this only chooses where Emptor travels; a board
    /// already in reach is used as-is. Ignored when <see cref="SkipTravel"/>.
    /// </summary>
    public string? City { get; set; }

    public List<BuyRequestItem> Items { get; set; } = new();
}

/// <summary>One completed marketboard transaction.</summary>
public sealed class PurchaseRecord
{
    public long UnitPrice { get; set; }
    public int Quantity { get; set; }
    public bool Hq { get; set; }
    public long TotalGil { get; set; }
    public string RetainerId { get; set; } = string.Empty;
}

/// <summary>Per-item outcome.</summary>
public sealed class BuyItemResult
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int RequestedQuantity { get; set; }
    public int PurchasedQuantity { get; set; }
    public long TotalGilSpent { get; set; }
    public List<PurchaseRecord> Purchases { get; set; } = new();

    /// <summary>Cheapest listing that was NOT bought (the "if you wanted more" hint).</summary>
    public long? NextLowestUnitPrice { get; set; }
    public int? NextLowestQuantity { get; set; }
    public bool? NextLowestHq { get; set; }

    public bool ListingsExhausted { get; set; }
    public StopReason StoppedReason { get; set; } = StopReason.None;

    /// <summary>
    /// Every listing seen for this item at read time, cheapest first (after the
    /// quality filter). Populated for discovery requests (quantity 0) and left
    /// null otherwise to keep buy results small.
    /// </summary>
    public List<PurchaseRecord>? AvailableListings { get; set; }
}

public enum OrderState
{
    Queued = 0,
    Running,
    Completed,
    Cancelled,
    Rejected,
    Failed,
}

/// <summary>Live state of a submitted order. The runner mutates this in place.</summary>
public sealed class BuyOrder
{
    public string OrderId { get; } = Guid.NewGuid().ToString("N");
    public string? ClientRequestId { get; set; }
    public OrderState State { get; set; } = OrderState.Queued;
    public string Message { get; set; } = string.Empty;
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public long TotalGilSpent { get; set; }
    public long TotalGilBudget { get; set; }

    public BuyRequest Request { get; init; } = new();
    public List<BuyItemResult> Items { get; } = new();

    /// <summary>True while this order came from the config window (vs the IPC API).</summary>
    public bool FromUi { get; init; }

    public bool CancelRequested { get; set; }

    public bool IsTerminal => State is OrderState.Completed or OrderState.Cancelled
        or OrderState.Rejected or OrderState.Failed;
}
