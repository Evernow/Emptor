using System;
using System.Linq;
using System.Numerics;

namespace Emptor.GameData;

/// <summary>vnavmesh IPC — best-effort walk to a point. No-ops if vnavmesh is absent.</summary>
public static class Navigation
{
    private const string Internal = "vnavmesh";

    public static bool Available => Plugin.PluginInterface.InstalledPlugins.Any(p =>
        p.IsLoaded && string.Equals(p.InternalName, Internal, StringComparison.OrdinalIgnoreCase));

    public static bool IsReady()
    {
        try { return Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc(); }
        catch { return false; }
    }

    public static bool IsRunning()
    {
        try { return Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc(); }
        catch { return false; }
    }

    public static bool MoveCloseTo(Vector3 dest, float range)
    {
        try
        {
            return Plugin.PluginInterface
                .GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo")
                .InvokeFunc(dest, false, range);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Emptor] vnavmesh move IPC failed.");
            return false;
        }
    }

    public static void Stop()
    {
        try { Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction(); }
        catch { /* ignore */ }
    }
}
