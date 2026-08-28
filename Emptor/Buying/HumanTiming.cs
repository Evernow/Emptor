using System;

namespace Emptor.Buying;

/// <summary>
/// Human-like delays for every step. Human reaction / decision / motor times are
/// right-skewed (log-normal-ish), not symmetric — a firm floor near the median
/// with an occasional long tail. Each value is drawn from a log-normal and
/// scaled by <see cref="Configuration.EmulationSpeed"/>. Typing has its own
/// per-keystroke model — see <see cref="TypingModel"/>.
/// </summary>
public static class HumanTiming
{
    private static bool On => Plugin.Instance.Configuration.HumanEmulation;
    private static double Speed => Math.Clamp(Plugin.Instance.Configuration.EmulationSpeed, 0.2f, 4f);

    /// <summary>Tiny stall before a discrete UI action even when "thinking" is done.</summary>
    public static TimeSpan Stall() => Draw(140, 0.45, 45, 500);

    /// <summary>Look at the just-opened board and orient.</summary>
    public static TimeSpan OrientAfterOpen() => Draw(760, 0.5, 320, 4200);

    /// <summary>Click into the search field.</summary>
    public static TimeSpan ClickIntoField() => Draw(320, 0.45, 130, 1400);

    /// <summary>Read the search result list and decide which row is the item.</summary>
    public static TimeSpan ReadResultsAndPickItem() => Draw(1500, 0.55, 550, 6500);

    /// <summary>Move the pointer to a specific list row before clicking it.</summary>
    public static TimeSpan ReachForRow() => Draw(460, 0.45, 170, 1700);

    /// <summary>Move the pointer to a button (Yes / Search) before clicking it.</summary>
    public static TimeSpan ReachForButton() => Draw(380, 0.42, 150, 1300);

    /// <summary>Scan the listing table — longer with more rows on screen.</summary>
    public static TimeSpan ScanListings(int visible) =>
        Draw(850 + (55 * Math.Min(visible, 20)), 0.55, 500, 7500);

    /// <summary>Read the "Purchase X for Y gil?" prompt.</summary>
    public static TimeSpan ReadConfirmPrompt() => Draw(1150, 0.5, 480, 4800);

    /// <summary>Settle after a confirmed purchase.</summary>
    public static TimeSpan AfterPurchaseSettle() => Draw(720, 0.5, 260, 3200);

    /// <summary>Pause before buying the next listing of the same item.</summary>
    public static TimeSpan BetweenPurchases()
    {
        if (!On)
            return TimeSpan.FromMilliseconds(200);
        if (Rng.NextDouble() < 0.07) // glanced away / read chat
            return Draw(8500, 0.5, 4500, 22000);
        return Draw(2900, 0.5, 1300, 8500);
    }

    /// <summary>Pause before moving to the next item on the list.</summary>
    public static TimeSpan BetweenItems() => Draw(3600, 0.5, 1500, 15000);

    /// <summary>Walk up to / turn toward the board before interacting.</summary>
    public static TimeSpan BeforeInteractBoard() => Draw(560, 0.45, 220, 2300);

    // ---- internals -------------------------------------------------

    private static readonly Random Rng = new();

    private static TimeSpan Draw(double medianMs, double sigma, double minMs, double maxMs)
    {
        if (!On)
            return TimeSpan.FromMilliseconds(Math.Clamp(medianMs * 0.12, 40, 250));
        var v = medianMs * Math.Exp(sigma * Gauss()) * Speed;
        return TimeSpan.FromMilliseconds(Math.Clamp(v, minMs, maxMs));
    }

    internal static double Gauss()
    {
        // Box-Muller
        double u1, u2;
        lock (Rng)
        {
            u1 = 1.0 - Rng.NextDouble();
            u2 = 1.0 - Rng.NextDouble();
        }
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
