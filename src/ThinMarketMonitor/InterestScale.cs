using System.Globalization;

namespace ThinMarketMonitor;

internal readonly record struct InterestScale(double Low, double High, double Step)
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    internal static InterestScale? Create(ReadOnlySpan<InterestPoint> points, int intervals)
    {
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        foreach (var point in points)
        {
            if (!double.IsFinite(point.Value) || point.Value < 0) continue;
            low = Math.Min(low, point.Value);
            high = Math.Max(high, point.Value);
        }
        if (!double.IsFinite(low)) return null;
        if (high == low)
        {
            double margin = high > 0 ? high * .02 : 1;
            low = Math.Max(0, low - margin);
            high += margin;
        }
        double raw = (high - low) / intervals;
        double order = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        if (!(order > 0) || !double.IsFinite(high)) return null;
        double step = order * (raw <= order ? 1 : raw <= 2 * order ? 2 : raw <= 5 * order ? 5 : 10);
        double first = Math.Floor(low / step) * step;
        double last = Math.Ceiling(high / step) * step;
        return double.IsFinite(last) && last > first ? new(first, last, step) : null;
    }

    internal IEnumerable<double> Ticks()
    {
        int count = (int)Math.Round((High - Low) / Step);
        for (int i = 0; i <= count; i++) yield return Low + Step * i;
    }

    internal string Label(double value) => Format(value, High, Step);

    internal string Delta(double value)
    {
        // A drag can measure between ticks, so keep more precision than the grid.
        string sign = value > 0 ? "+" : "";
        return sign + Format(value, Math.Abs(value), Math.Min(Step / 100, Math.Abs(value) / 10));
    }

    private static string Format(double value, double magnitude, double step)
    {
        if (value == 0) return "0";
        var (divisor, suffix) = magnitude switch
        {
            >= 1e12 => (1e12, "T"), >= 1e9 => (1e9, "B"), >= 1e6 => (1e6, "M"),
            >= 1e3 => (1e3, "K"), _ => (1d, "")
        };
        double scaledStep = step / divisor;
        double decimals = Math.Max(0, Math.Ceiling(-Math.Log10(scaledStep) - 1e-9));
        if (decimals > 8 || (suffix == "T" && magnitude >= 1e15)) return value.ToString("G14", Russian);
        return (value / divisor).ToString("0" + (decimals > 0 ? "." + new string('#', (int)decimals) : ""), Russian) + suffix;
    }
}
