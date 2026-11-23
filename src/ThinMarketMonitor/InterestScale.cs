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
        double step = NiceStep(high - low, intervals);
        if (!(step > 0) || !double.IsFinite(high)) return null;
        double first = Math.Floor(low / step) * step;
        double last = Math.Ceiling(high / step) * step;
        return double.IsFinite(last) && last > first ? new(first, last, step) : null;
    }

    internal IEnumerable<double> Ticks()
    {
        double first = Math.Ceiling(Low / Step - 1e-9) * Step;
        int count = (int)Math.Floor((High - first) / Step + 1e-9);
        for (int i = 0; i <= count; i++) yield return first + Step * i;
    }

    internal static InterestScale? Fixed(double low, double high, int intervals)
    {
        double step = NiceStep(high - low, Math.Max(3, intervals));
        return double.IsFinite(low) && double.IsFinite(high) && step > 0 ? new(low, high, step) : null;
    }

    private static double NiceStep(double span, int intervals)
    {
        double raw = span / intervals;
        double order = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        return order * (raw <= order ? 1 : raw <= 2 * order ? 2 : raw <= 5 * order ? 5 : 10);
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
