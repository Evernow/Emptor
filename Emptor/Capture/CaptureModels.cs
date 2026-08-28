using System;
using System.Collections.Generic;

namespace Emptor.Capture;

/// <summary>One thing that happened during a capture session.</summary>
public sealed class CaptureEvent
{
    /// <summary>Milliseconds since the capture started.</summary>
    public long TMs { get; set; }

    /// <summary>Milliseconds since the previous event.</summary>
    public long DtMs { get; set; }

    /// <summary>"addon" | "market" | "sample" | "runner" | "note".</summary>
    public string Category { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Dictionary<string, object?> Payload { get; set; } = new();
}

/// <summary>A single listing as it appeared to the player / recorder.</summary>
public sealed class CaptureListing
{
    public ulong ListingId { get; set; }
    public ulong RetainerId { get; set; }
    public string? RetainerName { get; set; }
    public long UnitPrice { get; set; }
    public long TotalTax { get; set; }
    public int Quantity { get; set; }
    public bool Hq { get; set; }
    public uint ItemId { get; set; }
}

public sealed class CaptureSession
{
    public string Mode { get; set; } = "manual"; // "manual" | "automated"
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public long DurationMs { get; set; }
    public uint Territory { get; set; }
    public string? CharacterName { get; set; }

    public List<CaptureEvent> Events { get; set; } = new();

    public CaptureSummary Summary { get; set; } = new();
}

/// <summary>Human-readable digest computed when the capture stops.</summary>
public sealed class CaptureSummary
{
    public uint? ItemId { get; set; }
    public string? ItemName { get; set; }

    public bool PurchaseObserved { get; set; }
    public long? PaidUnitPrice { get; set; }
    public int? PaidQuantity { get; set; }
    public bool? PaidHq { get; set; }
    public long? PaidTotalGil { get; set; }
    public ulong? PaidListingId { get; set; }
    public string? PaidRetainer { get; set; }

    /// <summary>All listings visible at purchase time (or last seen), cheapest first.</summary>
    public List<CaptureListing> OptionsAtPurchase { get; set; } = new();

    /// <summary>Where the bought listing ranked among the visible options (1 = cheapest).</summary>
    public int? ChosenRankByPrice { get; set; }

    // key intervals (ms), null if that transition was not observed
    public long? TargetBoardToSearchOpenMs { get; set; }
    public long? SearchOpenToFirstSearchMs { get; set; }
    public long? SearchToOfferingsMs { get; set; }
    public long? OfferingsToRowClickMs { get; set; }
    public long? RowClickToConfirmMs { get; set; }
    public long? ConfirmToYesMs { get; set; }
    public long? YesToServerConfirmMs { get; set; }

    /// <summary>Every inter-event gap on the marketboard addons, in order (ms).</summary>
    public List<long> AddonClickGapsMs { get; set; } = new();

    public bool Scrolled { get; set; }
    public bool HoveredRows { get; set; }
    public bool ChangedSearchModeOrFilter { get; set; }
    public bool OpenedHistory { get; set; }
    public int SearchCount { get; set; }
    public int OfferingsPagesReceived { get; set; }

    public List<string> Notes { get; set; } = new();
}
