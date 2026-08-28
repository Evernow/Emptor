using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace Emptor.GameData;

/// <summary>Resolves item names to ids and back, restricted to marketable items.</summary>
public static class ItemResolver
{
    /// <summary>An item can be sold on the marketboard iff it has a search category.</summary>
    public static bool IsMarketable(uint itemId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        return sheet.TryGetRow(itemId, out var row) && row.ItemSearchCategory.RowId != 0;
    }

    public static string GetName(uint itemId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        return sheet.TryGetRow(itemId, out var row) ? row.Name.ExtractText() : $"Item {itemId}";
    }

    /// <summary>
    /// Exact (case-insensitive) name match against a marketable item.
    /// Returns 0 when there is no unambiguous match.
    /// </summary>
    public static uint ResolveExact(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0;

        var trimmed = name.Trim();
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        uint match = 0;
        foreach (var row in sheet)
        {
            if (row.ItemSearchCategory.RowId == 0)
                continue;
            if (!string.Equals(row.Name.ExtractText(), trimmed, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match != 0)
                return 0; // ambiguous
            match = row.RowId;
        }

        return match;
    }

    /// <summary>Up to <paramref name="limit"/> marketable items whose name contains <paramref name="fragment"/>.</summary>
    public static List<(uint ItemId, string Name)> Search(string fragment, int limit = 20)
    {
        var results = new List<(uint, string)>();
        if (string.IsNullOrWhiteSpace(fragment))
            return results;

        var needle = fragment.Trim();
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var row in sheet)
        {
            if (row.ItemSearchCategory.RowId == 0)
                continue;
            var itemName = row.Name.ExtractText();
            if (itemName.Length == 0)
                continue;
            if (itemName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                results.Add((row.RowId, itemName));
                if (results.Count >= limit)
                    break;
            }
        }

        return results
            .OrderBy(r => r.Item2.Length)
            .ThenBy(r => r.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
