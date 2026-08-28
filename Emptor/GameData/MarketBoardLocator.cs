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
    public const float InteractDistance = 4.2f;
    public const float SearchRadius = 90f;

    public static IGameObject? FindNearest()
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position;
        var boards = Plugin.ObjectTable.Where(IsMarketBoard);
        if (me is null)
            return boards.FirstOrDefault();
        return boards
            .Where(b => Horizontal(me.Value, b.Position) <= SearchRadius)
            .OrderBy(b => Horizontal(me.Value, b.Position))
            .FirstOrDefault();
    }

    public static float? DistanceTo(IGameObject board)
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position;
        return me is null ? null : Horizontal(me.Value, board.Position);
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
        => o.IsTargetable
           && o.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject or ObjectKind.ReactionEventObject
           && string.Equals(o.Name.TextValue, "Market Board", StringComparison.OrdinalIgnoreCase);

    private static float Horizontal(Vector3 a, Vector3 b)
        => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));
}
