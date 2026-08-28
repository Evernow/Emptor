using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Emptor.GameData;

/// <summary>Small character-state actions a person would do before using a board.</summary>
public static unsafe class PlayerActions
{
    private static Vector3 lastPos;
    private static DateTime lastPosStampUtc;

    public static bool IsMounted => Plugin.Condition[ConditionFlag.Mounted]
        || Plugin.Condition[ConditionFlag.RidingPillion];

    /// <summary>General action 23 = Dismount. Safe to call repeatedly until <see cref="IsMounted"/> is false.</summary>
    public static void Dismount()
    {
        var am = ActionManager.Instance();
        if (am != null)
            am->UseAction(ActionType.GeneralAction, 23);
    }

    /// <summary>True while the character position is still changing (sampled between calls).</summary>
    public static bool IsMoving()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
            return false;

        var now = DateTime.UtcNow;
        var pos = player.Position;
        var moved = Vector3.Distance(pos, lastPos);
        var dt = now - lastPosStampUtc;
        lastPos = pos;
        lastPosStampUtc = now;

        // first sample, or a long gap → assume settled
        if (dt > TimeSpan.FromSeconds(1))
            return false;
        return moved > 0.05f;
    }
}
