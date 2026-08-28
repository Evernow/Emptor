using System;

namespace Emptor.Buying;

/// <summary>
/// Per-keystroke inter-key-interval model, calibrated to Dhakal et al. 2018,
/// "Observations on Typing from 136 Million Keystrokes" (CHI): average inter-key
/// interval ~238 ms (SD ~112, right-skewed, ~60 ms floor), average ~52 WPM, and
/// WPM ≈ 12000 / IKI(ms). Key points modelled:
///  - IKIs are log-normal, not uniform noise.
///  - A per-session typing skill (WPM) sets the base interval.
///  - Bigram effects: hand-alternating pairs are faster; same-key repeats and
///    reaching to/from Space are slower.
///  - The first keystroke after focusing carries an initiation latency.
///  - A few keystrokes get an extra hesitation pause.
/// One instance per search (a fresh "sitting down to type").
/// </summary>
public sealed class TypingModel
{
    // QWERTY hand assignment (lowercase + digits); Space handled separately.
    private const string LeftHand = "12345qwertasdfgzxcvb";
    private const string RightHand = "67890yuiophjklnm";

    private readonly Random rng = new();
    private readonly double baseMedianIki; // ms
    private readonly double sigma;

    public TypingModel()
    {
        // session skill: WPM ~ N(52, 14), clamped to a plausible range
        var wpm = Math.Clamp(52 + (14 * Gauss()), 26, 95);
        baseMedianIki = 12000.0 / wpm;      // ~231 ms at 52 WPM
        sigma = 0.40 + (0.08 * rng.NextDouble()); // 0.40..0.48
    }

    /// <summary>Delay before the first character (reaction + motor planning).</summary>
    public TimeSpan Initiation()
        => Scaled(LogNormal(520, 0.5, 200, 2400));

    /// <summary>Interval before typing <paramref name="cur"/> given the previous char.</summary>
    public TimeSpan NextInterval(char prev, char cur)
    {
        var iki = baseMedianIki * BigramFactor(prev, cur) * Math.Exp(sigma * Gauss());
        if (rng.NextDouble() < 0.06) // occasional hesitation
            iki += LogNormal(380, 0.5, 140, 1600);
        return Scaled(Math.Clamp(iki, 55, 3000));
    }

    private static double BigramFactor(char a, char b)
    {
        a = char.ToLowerInvariant(a);
        b = char.ToLowerInvariant(b);
        if (a == b && a != ' ') return 1.35;             // same-key repeat
        if (a == ' ' || b == ' ') return 1.15;           // to / from Space
        var aL = LeftHand.IndexOf(a) >= 0;
        var aR = RightHand.IndexOf(a) >= 0;
        var bL = LeftHand.IndexOf(b) >= 0;
        var bR = RightHand.IndexOf(b) >= 0;
        if ((aL && bR) || (aR && bL)) return 0.82;        // hand alternation
        if ((aL && bL) || (aR && bR)) return 1.08;        // same hand
        return 1.0;
    }

    // ---- helpers --------------------------------------------------

    private static double Speed => Math.Clamp(Plugin.Instance.Configuration.EmulationSpeed, 0.2f, 4f);
    private static bool On => Plugin.Instance.Configuration.HumanEmulation;

    private static TimeSpan Scaled(double ms)
        => TimeSpan.FromMilliseconds(On ? ms * Speed : Math.Clamp(ms * 0.1, 5, 40));

    private double LogNormal(double median, double s, double min, double max)
        => Math.Clamp(median * Math.Exp(s * Gauss()), min, max);

    private double Gauss()
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
