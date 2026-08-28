using System;
using Dalamud.Game.Network.Structures;

namespace Emptor.Buying;

public enum PurchaseOutcome
{
    Pending = 0,
    Verified,
    Rejected,
    Conflicted,
    Indeterminate,
}

/// <summary>
/// Correlates one in-flight marketboard purchase with the client's
/// <see cref="Dalamud.Plugin.Services.IMarketBoard"/> events plus the gil delta.
/// The owning runner subscribes the events once and forwards them here.
/// </summary>
public sealed class PurchaseConfirmationWatcher
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private uint expectedItemId;
    private ulong expectedListingId;
    private int expectedQuantity;
    private long unitPrice;
    private long gilBefore;
    private DateTime armedUtc;

    public PurchaseOutcome Outcome { get; private set; } = PurchaseOutcome.Pending;
    public string Message { get; private set; } = string.Empty;
    public bool RequestObserved { get; private set; }
    public long GilSpent { get; private set; }
    public int QuantityConfirmed { get; private set; }

    public bool IsArmed => Outcome == PurchaseOutcome.Pending && armedUtc != default;

    public void Arm(CandidateListing listing, long gilOnHand)
    {
        expectedItemId = listing.ItemId;
        expectedListingId = listing.ListingId;
        expectedQuantity = listing.Quantity;
        unitPrice = listing.UnitPrice;
        gilBefore = gilOnHand;
        armedUtc = DateTime.UtcNow;
        Outcome = PurchaseOutcome.Pending;
        Message = string.Empty;
        RequestObserved = false;
        GilSpent = 0;
        QuantityConfirmed = 0;
    }

    public void Disarm()
    {
        armedUtc = default;
        Outcome = PurchaseOutcome.Pending;
    }

    public void OnPurchaseRequested(IMarketBoardPurchaseHandler handler)
    {
        if (!IsArmed)
            return;

        RequestObserved = true;
        if (handler.ListingId != expectedListingId || handler.CatalogId != expectedItemId)
        {
            Outcome = PurchaseOutcome.Conflicted;
            Message = $"Client sent a purchase for listing {handler.ListingId}/item {handler.CatalogId}, expected {expectedListingId}/{expectedItemId}.";
        }
    }

    public void OnItemPurchased(IMarketBoardPurchase purchase, long gilNow)
    {
        if (!IsArmed || purchase.CatalogId != expectedItemId)
            return;

        QuantityConfirmed = (int)purchase.ItemQuantity;
        var delta = gilBefore - gilNow;
        GilSpent = delta > 0 ? delta : unitPrice * QuantityConfirmed;

        if ((int)purchase.ItemQuantity == expectedQuantity && delta > 0)
        {
            Outcome = PurchaseOutcome.Verified;
            Message = $"Bought {purchase.ItemQuantity} for {GilSpent:N0} gil.";
        }
        else if (delta <= 0)
        {
            Outcome = PurchaseOutcome.Rejected;
            Message = "Purchase response arrived but no gil moved.";
        }
        else
        {
            Outcome = PurchaseOutcome.Indeterminate;
            Message = $"Bought {purchase.ItemQuantity} (expected {expectedQuantity}); gil moved {delta:N0}.";
        }
    }

    /// <summary>Called each tick by the runner. Resolves the deadline.</summary>
    public void Tick()
    {
        if (IsArmed && DateTime.UtcNow - armedUtc > Deadline)
        {
            Outcome = PurchaseOutcome.Indeterminate;
            Message = "No purchase confirmation arrived before the deadline.";
        }
    }
}
