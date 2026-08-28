using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Emptor.GameData;

/// <summary>Finds and interacts with a real Market Board object.</summary>
public static class MarketBoardLocator
{
    /// <summary>How close counts as "at the board" (real 3D distance).</summary>
    public const float InteractDistance = 4.2f;

    /// <summary>How far Emptor will walk to a board when travel is allowed.</summary>
    public const float NavigateSearchRadius = 60f;

    /// <summary>
    /// Nearest targetable "Market Board" object within <paramref name="maxRange"/>
    /// yalms (real 3D distance). Returns null if none — the caller must NOT
    /// fall back to opening the board UI cold.
    /// </summary>
    public static IGameObject? FindNearest(float maxRange)
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position;
        if (me is null)
            return null;

        return Plugin.ObjectTable
            .Where(IsMarketBoard)
            .Where(b => Vector3.Distance(me.Value, b.Position) <= maxRange)
            .OrderBy(b => Vector3.Distance(me.Value, b.Position))
            .FirstOrDefault();
    }

    public static float? DistanceTo(IGameObject board)
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position;
        return me is null ? null : Vector3.Distance(me.Value, board.Position);
    }

    /// <summary>Target the board and send the real interact packet.</summary>
    public static unsafe bool Interact(IGameObject board)
    {
        var ts = TargetSystem.Instance();
        if (ts == null)
            return false;
        Plugin.TargetManager.Target = board;
        // LOS check off — market boards are large objects whose collision can
        // falsely reject an in-range interaction (per MarketMafioso).
        ts->InteractWithObject((ClientGameObject*)board.Address, false);
        return true;
    }

    private static bool IsMarketBoard(IGameObject o)
    {
        if (!o.IsTargetable)
            return false;
        if (o.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject or ObjectKind.ReactionEventObject))
            return false;
        var name = o.Name.TextValue;
        return name.Contains("Market Board", StringComparison.OrdinalIgnoreCase);
    }
}
