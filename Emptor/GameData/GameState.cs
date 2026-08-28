using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Emptor.GameData;

/// <summary>Small read-only helpers over game state that the buy runner needs.</summary>
public static class GameState
{
    /// <summary>
    /// Conditions under which the game refuses (or it is unsafe) to drive the
    /// marketboard. Mirrors MarketMafioso's PurchaseBlockingConditions.
    /// </summary>
    private static readonly ConditionFlag[] BlockingConditions =
    {
        ConditionFlag.Emoting,
        ConditionFlag.Mounted,
        ConditionFlag.Crafting,
        ConditionFlag.Gathering,
        ConditionFlag.PlayingMiniGame,
        ConditionFlag.Occupied,
        ConditionFlag.InCombat,
        ConditionFlag.Occupied30,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
    };

    /// <summary>Null when it is fine to act, otherwise a human-readable reason.</summary>
    public static string? GetBlockReason()
    {
        if (!Plugin.ClientState.IsLoggedIn)
            return "Not logged in.";

        foreach (var flag in BlockingConditions)
        {
            if (Plugin.Condition[flag])
                return $"Cannot use the marketboard while {flag}.";
        }

        return null;
    }

    public static unsafe long GetGil()
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : (long)manager->GetGil();
    }

    public static unsafe bool IsAddonReady(string name)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(name, 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }
}
