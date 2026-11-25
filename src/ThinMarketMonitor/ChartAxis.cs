using System.Globalization;

namespace ThinMarketMonitor;

internal readonly record struct ChartTimeTick(long Time, string Label);

internal static class ChartAxis
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private const long Day = 86_400_000;
    private static readonly int[] ClockSteps =
        [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10_800, 14_400, 21_600, 43_200];
    private static readonly int[] DaySteps = [1, 2, 3, 7, 14];
    private static readonly int[] MonthSteps = [1, 2, 3, 6, 12, 24, 60, 120, 240];

    internal static double NiceStep(double span, int intervals)
    {
        double raw = span / Math.Max(1, intervals);
        if (raw <= 0 || !double.IsFinite(raw)) return 0;
        double order = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = order * (raw <= order ? 1 : raw <= 2 * order ? 2 : raw <= 5 * order ? 5 : 10);
        return double.IsFinite(step) ? step : 0;
    }

    internal static IEnumerable<double> NumericTicks(double low, double high, double step)
    {
        if (!(step > 0) || !double.IsFinite(step)) yield break;
        double first = Math.Ceiling(low / step - 1e-9);
        double last = Math.Floor(high / step + 1e-9);
        if (!double.IsFinite(first) || !double.IsFinite(last) || last < first) yield break;
        int count = (int)Math.Min(512, last - first + 1);
        for (int i = 0; i < count; i++) yield return (first + i) * step;
    }

    internal static string PriceLabel(double value, double resolution)
    {
        if (value == 0) return "0";
        if (!(resolution > 0) || !double.IsFinite(resolution)) return value.ToString("G8", Russian);
        double decimals = Math.Max(0, Math.Ceiling(-Math.Log10(resolution) - 1e-9));
        if (decimals > 12 || Math.Abs(value) >= 1e10)
        {
            int significant = (int)Math.Clamp(Math.Floor(Math.Log10(Math.Abs(value))) - Math.Log10(resolution) + 2, 4, 16);
            return value.ToString($"G{significant}", Russian);
        }
        return value.ToString($"F{(int)decimals}", Russian);
    }

    internal static string DateLabel(DateTimeOffset time, bool seconds = false) =>
        time.ToString(seconds ? "ddd dd MMM\\'yy HH:mm:ss" : "ddd dd MMM\\'yy HH:mm", English);

    internal static ChartTimeTick[] TimeTicks(double from, double to, long barTime, int intervals, TimeZoneInfo zone)
    {
        if (!(to > from) || from < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || to > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            return [];
        double wanted = Math.Max(barTime, (to - from) / Math.Max(1, intervals));
        var start = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(from)), zone);
        var end = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Ceiling(to)), zone);
        if (wanted <= ClockSteps[^1] * 1000L)
        {
            long step = ClockSteps.First(value => value * 1000L >= wanted) * 1000L;
            var ticks = new List<ChartTimeTick>();
            AddClockTicks(start.Offset);
            if (start.Offset != end.Offset) AddClockTicks(end.Offset);
            return ticks.OrderBy(tick => tick.Time).ToArray();

            void AddClockTicks(TimeSpan offset)
            {
                long shift = (long)offset.TotalMilliseconds;
                long first = (long)Math.Ceiling((from + shift) / step) * step - shift;
                for (long stamp = first; stamp <= to; stamp += step)
                {
                    var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(stamp), zone);
                    if (local.Offset != offset) continue;
                    string format = step < 60_000 ? "HH:mm:ss" :
                        local.TimeOfDay == TimeSpan.Zero && start.Date != end.Date ? "dd MMM" : "HH:mm";
                    ticks.Add(new(stamp, local.ToString(format, English)));
                }
            }
        }

        var calendar = new List<ChartTimeTick>();
        if (wanted <= DaySteps[^1] * Day)
        {
            int step = DaySteps.First(value => value * Day >= wanted);
            long firstDay = start.Ticks / TimeSpan.TicksPerDay;
            long lastDay = end.Ticks / TimeSpan.TicksPerDay;
            string format = to - from >= 365 * Day ? "dd MMM\\'yy" : "dd MMM";
            for (long day = firstDay - firstDay % step; day <= lastDay; day += step)
                AddCalendarTick(new DateTime(day * TimeSpan.TicksPerDay), format);
        }
        else
        {
            int step = MonthSteps.FirstOrDefault(value => value * 30 * Day >= wanted, MonthSteps[^1]);
            int firstMonth = start.Year * 12 + start.Month - 1;
            int lastMonth = end.Year * 12 + end.Month - 1;
            string format = step >= 12 ? "yyyy" : "MMM\\'yy";
            for (int month = Math.Max(12, firstMonth - firstMonth % step); month <= lastMonth; month += step)
                AddCalendarTick(new DateTime(month / 12, month % 12 + 1, 1), format);
        }
        return calendar.ToArray();

        void AddCalendarTick(DateTime wallTime, string format)
        {
            if (zone.IsInvalidTime(wallTime)) return;
            long stamp = new DateTimeOffset(wallTime, zone.GetUtcOffset(wallTime)).ToUnixTimeMilliseconds();
            if (stamp >= from && stamp <= to) calendar.Add(new(stamp, wallTime.ToString(format, English)));
        }
    }
}
