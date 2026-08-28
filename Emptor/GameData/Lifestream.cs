using System;
using System.Linq;

namespace Emptor.GameData;

/// <summary>
/// Thin Lifestream bridge. Emptor needs exactly one thing from Lifestream:
/// "take me to a Market Board in this city" — the <c>/li mb</c> command, which
/// aethernets to the nearest board shard and walks the rest. Choosing a world /
/// data centre is the caller's job, not Emptor's.
/// </summary>
public static class Lifestream
{
    private const string Internal = "Lifestream";

    /// <summary>Lifestream is installed and loaded.</summary>
    public static bool Available => Plugin.PluginInterface.InstalledPlugins.Any(p =>
        p.IsLoaded && string.Equals(p.InternalName, Internal, StringComparison.OrdinalIgnoreCase));

    /// <summary>Lifestream is mid-travel (teleporting / walking / zoning).</summary>
    public static bool IsBusy()
    {
        try { return Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// Run <c>/li &lt;args&gt;</c> (e.g. <c>"mb"</c>, <c>"Kogane Dori"</c>,
    /// <c>"tp Old Sharlayan"</c>). Returns false only if the command could not be
    /// dispatched at all (Lifestream not registered). A person parking at a board
    /// does exactly this.
    /// </summary>
    public static bool RunLiCommand(string args)
    {
        try { return Plugin.CommandManager.ProcessCommand("/li " + args); }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[Emptor] \"/li {args}\" dispatch failed.");
            return false;
        }
    }

    /// <summary>Fire <c>/li mb</c> — Lifestream's built-in "go to a Market Board" (Ul'dah).</summary>
    public static bool GoToMarketBoard() => RunLiCommand("mb");

    /// <summary>Stop whatever Lifestream is doing (only used to undo a travel Emptor started).</summary>
    public static void Abort()
    {
        try { Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
        catch { /* ignore */ }
    }
}
