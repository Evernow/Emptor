using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Emptor.GameData;

/// <summary>How Lifestream should be told to reach a given city's Market Board.</summary>
public enum CityRouteKind
{
    /// <summary>Lifestream's built-in "/li mb" (goes to Ul'dah, Sapphire Avenue Exchange).</summary>
    LiMarketBoard,

    /// <summary>Teleport to the city aetheryte ("/li tp &lt;name&gt;"); Emptor walks the last bit.</summary>
    Teleport,

    /// <summary>
    /// Teleport to the city, then aethernet-hop to the market district ("/li &lt;shard&gt;",
    /// which Lifestream resolves to teleport-then-hop); Emptor walks the last bit.
    /// </summary>
    AethernetHop,
}

/// <summary>A city whose Market Board Emptor knows how to route to.</summary>
public sealed record MarketCity(
    string Key,
    string Display,
    CityRouteKind RouteKind,
    string LifestreamArg,
    string[] Aliases,
    uint AnchorTerritory = 0,
    float AnchorX = 0f,
    float AnchorY = 0f,
    float AnchorZ = 0f)
{
    /// <summary>
    /// A point ~next to the board, in the arrival territory. Used only as a
    /// fallback: after Lifestream travel, if no board object is loaded near the
    /// landing spot, Emptor walks here and the board streams in. Zero = none.
    /// </summary>
    public Vector3? Anchor =>
        AnchorTerritory == 0 || (AnchorX == 0f && AnchorY == 0f && AnchorZ == 0f)
            ? null
            : new Vector3(AnchorX, AnchorY, AnchorZ);
}

/// <summary>
/// The cities a caller may pin an order to ("only visit Kugane for this purchase").
/// Market Board listings are world-wide identical, so the city only decides where
/// Emptor travels — never which listings it sees.
/// </summary>
public static class MarketCities
{
    // Anchor points (territory + XYZ) are left at 0 until captured in-game with
    // "/emptor pos" while standing at each board. NavigateSearchRadius (200y)
    // already covers most landing spots; the anchor is the fallback for the few
    // large zones where the board object isn't streamed in at the landing spot.
    public static readonly IReadOnlyList<MarketCity> All = new[]
    {
        new MarketCity("uldah", "Ul'dah", CityRouteKind.LiMarketBoard, "mb",
            new[] { "ul'dah", "uldah", "ul dah", "sapphire avenue", "sapphire avenue exchange", "steps of nald", "steps of thal" }),

        new MarketCity("limsa", "Limsa Lominsa", CityRouteKind.Teleport, "tp Limsa Lominsa Lower Decks",
            new[] { "limsa", "limsa lominsa", "lominsa", "lower decks" }),

        new MarketCity("gridania", "Gridania", CityRouteKind.AethernetHop, "Shaded Bower",
            new[] { "gridania", "new gridania", "old gridania", "leatherworkers", "leatherworkers' guild", "shaded bower" }),

        new MarketCity("ishgard", "Foundation", CityRouteKind.AethernetHop, "Jeweled Crozier",
            new[] { "ishgard", "foundation", "jeweled crozier", "the jeweled crozier", "crozier" }),

        new MarketCity("kugane", "Kugane", CityRouteKind.AethernetHop, "Kogane Dori",
            new[] { "kugane", "kogane dori", "kogane dori markets", "kogane" }),

        new MarketCity("crystarium", "The Crystarium", CityRouteKind.AethernetHop, "Musica Universalis",
            new[] { "crystarium", "the crystarium", "musica universalis", "musica universalis markets" }),

        new MarketCity("sharlayan", "Old Sharlayan", CityRouteKind.Teleport, "tp Old Sharlayan",
            new[] { "sharlayan", "old sharlayan" }),

        new MarketCity("tuliyollal", "Tuliyollal", CityRouteKind.AethernetHop, "Bayside Bevy",
            new[] { "tuliyollal", "tulliyollal", "bayside bevy", "bayside bevy marketplace" }),
    };

    /// <summary>
    /// Match a caller-supplied string (key, display name, or a known alias /
    /// fragment) to a city. Null / whitespace returns null (= no preference).
    /// </summary>
    public static MarketCity? Resolve(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;

        var q = s.Trim().ToLowerInvariant();

        return All.FirstOrDefault(c => c.Key == q || c.Display.ToLowerInvariant() == q)
            ?? All.FirstOrDefault(c => c.Aliases.Any(a => a == q))
            ?? All.FirstOrDefault(c => c.Aliases.Any(a => q.Contains(a, StringComparison.Ordinal))
                                       || q.Contains(c.Key, StringComparison.Ordinal));
    }

    public static string KnownKeys() => string.Join(", ", All.Select(c => c.Key));
}
