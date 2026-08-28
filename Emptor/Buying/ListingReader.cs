using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace Emptor.Buying;

/// <summary>A single marketboard listing, copied out of the game's InfoProxy.</summary>
public readonly record struct CandidateListing(
    ulong ListingId,
    ulong RetainerId,
    long UnitPrice,
    long TotalTax,
    int Quantity,
    bool Hq,
    uint ItemId)
{
    public long StackCost => (UnitPrice * Quantity) + TotalTax;
}

public static class ListingReader
{
    /// <summary>
    /// Reads the live listing cache for the currently-open browse. Caller must
    /// have already confirmed <c>InfoProxyItemSearch.SearchItemId == expectedItemId</c>
    /// and <c>WaitingForListings == false</c>.
    /// </summary>
    public static unsafe List<CandidateListing> ReadRaw(uint expectedItemId)
    {
        var result = new List<CandidateListing>();
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || proxy->SearchItemId != expectedItemId)
            return result;

        var count = Math.Min((int)proxy->ListingCount, proxy->Listings.Length);
        for (var i = 0; i < count; i++)
        {
            ref var listing = ref proxy->Listings[i];
            if (listing.ListingId == 0 || listing.RetainerId == 0 ||
                listing.UnitPrice == 0 || listing.Quantity == 0)
            {
                continue;
            }

            result.Add(new CandidateListing(
                listing.ListingId,
                listing.RetainerId,
                listing.UnitPrice,
                listing.TotalTax,
                (int)listing.Quantity,
                listing.IsHqItem,
                listing.ItemId));
        }

        return result;
    }

    public static bool MatchesQuality(bool listingIsHq, QualityFilter filter) => filter switch
    {
        QualityFilter.Either => true,
        QualityFilter.NqOnly => !listingIsHq,
        QualityFilter.HqOnly => listingIsHq,
        _ => true,
    };

    /// <summary>Cheapest-first, applying the quality filter. Does not apply the price ceiling.</summary>
    public static List<CandidateListing> RankByPrice(IEnumerable<CandidateListing> listings, QualityFilter quality)
        => listings
            .Where(l => MatchesQuality(l.Hq, quality))
            .OrderBy(l => l.UnitPrice)
            .ThenBy(l => l.Quantity)
            .ToList();
}
