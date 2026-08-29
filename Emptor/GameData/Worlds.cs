using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Lumina.Excel.Sheets;

namespace Emptor.GameData;

/// <summary>How wide a Universalis price lookup should reach.</summary>
public enum PriceScope
{
    /// <summary>A single world.</summary>
    World,

    /// <summary>Every world on one data centre.</summary>
    Datacenter,

    /// <summary>Every data centre in one physical region.</summary>
    Region,

    /// <summary>
    /// Everywhere the player could actually travel to buy: their region's data
    /// centres plus Materia (Oceania). Materia residents get Materia only.
    /// </summary>
    Reachable,
}

public sealed record WorldInfo(
    uint Id, string Name, string DcName, uint RegionId, string RegionName, bool IsPublic);

/// <summary>
/// World / data centre / region lookup, plus the data-centre-travel reachability
/// rule (own region + Materia). Built once from the <see cref="World"/> sheet.
/// </summary>
public static class Worlds
{
    // Universalis region location strings, keyed by WorldDCGroupType.Region.
    private static readonly Dictionary<uint, string> RegionNames = new()
    {
        { 1, "Japan" }, { 2, "North-America" }, { 3, "Europe" }, { 4, "Oceania" },
    };

    public const uint OceaniaRegionId = 4;

    private static readonly object gate = new();
    private static Dictionary<uint, WorldInfo>? byId;
    private static Dictionary<string, WorldInfo>? byName;

    private static void EnsureLoaded()
    {
        lock (gate)
        {
            if (byId is not null)
                return;

            var id = new Dictionary<uint, WorldInfo>();
            var name = new Dictionary<string, WorldInfo>(StringComparer.OrdinalIgnoreCase);
            Load(id, name);
            byName = name;
            byId = id;
        }
    }

    private static void Load(Dictionary<uint, WorldInfo> byId, Dictionary<string, WorldInfo> byName)
    {
        foreach (var w in Plugin.DataManager.GetExcelSheet<World>())
        {
            var name = w.Name.ExtractText();
            if (string.IsNullOrEmpty(name))
                continue;

            var dc = w.DataCenter.ValueNullable;
            if (dc is null || dc.Value.Region.RowId == 0)
                continue; // internal / test worlds

            var regionId = dc.Value.Region.RowId;
            var info = new WorldInfo(
                w.RowId, name, dc.Value.Name.ExtractText(),
                regionId, RegionNames.GetValueOrDefault(regionId, "unknown"), w.IsPublic);

            byId[w.RowId] = info;
            if (w.IsPublic && !byName.ContainsKey(name))
                byName[name] = info;
        }
    }

    public static WorldInfo? ById(uint id)
    {
        EnsureLoaded();
        return byId!.GetValueOrDefault(id);
    }

    public static WorldInfo? ByName(string? name)
    {
        EnsureLoaded();
        return string.IsNullOrWhiteSpace(name) ? null : byName!.GetValueOrDefault(name.Trim());
    }

    /// <summary>Match a world by id (numeric string) or public name.</summary>
    public static WorldInfo? Resolve(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var t = s.Trim();
        return uint.TryParse(t, out var id) ? ById(id) : ByName(t);
    }

    private static IPlayerCharacter? Me => Plugin.ObjectTable.LocalPlayer as IPlayerCharacter;

    public static WorldInfo? CurrentWorld()
    {
        var id = Me?.CurrentWorld.RowId ?? 0;
        return id == 0 ? null : ById(id);
    }

    public static WorldInfo? HomeWorld()
    {
        var id = Me?.HomeWorld.RowId ?? 0;
        return id == 0 ? null : ById(id);
    }

    public static IReadOnlyList<WorldInfo> AllPublic()
    {
        EnsureLoaded();
        return byName!.Values
            .OrderBy(w => w.RegionName).ThenBy(w => w.DcName).ThenBy(w => w.Name)
            .ToList();
    }

    public static bool IsDatacenter(string name)
    {
        EnsureLoaded();
        return byName!.Values.Any(w => string.Equals(w.DcName, name, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> DatacentersInRegion(uint regionId)
        => AllPublic().Where(w => w.RegionId == regionId).Select(w => w.DcName)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();

    /// <summary>
    /// Data-centre-travel rule: the player's own region's DCs, plus Materia
    /// (Oceania). A Materia home world can only reach Materia.
    /// </summary>
    public static IReadOnlyList<string> ReachableDatacenters(WorldInfo home)
    {
        if (home.RegionId == OceaniaRegionId)
            return DatacentersInRegion(OceaniaRegionId);
        return DatacentersInRegion(home.RegionId)
            .Concat(DatacentersInRegion(OceaniaRegionId))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Universalis region location strings covering the Reachable scope.</summary>
    public static IReadOnlyList<string> ReachableRegions(WorldInfo home)
        => home.RegionId == OceaniaRegionId
            ? new[] { "Oceania" }
            : new[] { home.RegionName, "Oceania" };
}
