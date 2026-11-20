using System.Globalization;

namespace ThinMarketMonitor;

internal sealed record SpreadRule(bool Enabled = false, double? Minimum = null, double? Maximum = null)
{
    public bool Valid => Bound(Minimum) && Bound(Maximum) &&
        (Minimum == null || Maximum == null || Minimum <= Maximum);

    private static bool Bound(double? value) => value == null || double.IsFinite(value.Value) && value >= 0;

    public bool Accepts(double value) => Valid && double.IsFinite(value) && value >= 0 &&
        (Minimum == null || value >= Minimum) && (Maximum == null || value <= Maximum);

    public static bool TryParse(string minimum, string maximum, bool enabled, out SpreadRule rule)
    {
        bool Parse(string text, out double? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double number) || !Bound(number)) return false;
            value = number;
            return true;
        }
        bool lower = Parse(minimum, out var min), upper = Parse(maximum, out var max);
        rule = new(enabled, min, max);
        return lower && upper && rule.Valid;
    }
}

internal readonly record struct SpreadQuote(double Bid, double Ask, long Time)
{
    public const int FreshMilliseconds = 15_000;

    public double? Percent(long now)
    {
        if (Time < now - FreshMilliseconds || Time > now + 3000 ||
            !double.IsFinite(Bid) || !double.IsFinite(Ask) || Bid <= 0 || Ask < Bid) return null;
        // Equivalent to (ask - bid) / ((ask + bid) / 2) * 100, without overflowing ask + bid.
        return ((Ask - Bid) / Ask) / (0.5 + 0.5 * (Bid / Ask)) * 100;
    }
}
