namespace ThinMarketMonitor;

internal readonly record struct OverviewSpreadValue(double Percent, long QuoteTime);

internal sealed class OverviewSpreadSampler : IDisposable
{
    private readonly ExchangeApi api;
    private readonly Market[][] batches;
    private readonly Market[] streamed;
    private readonly SpreadFilterMonitor? stream;

    public OverviewSpreadSampler(ExchangeApi api, Market[] markets)
    {
        this.api = api;
        var supported = markets.Where(ExchangeApi.SpreadSupported).ToArray();
        batches = supported.Where(m => !ExchangeApi.StreamedBook(m.Venue))
            .GroupBy(SpreadFilterMonitor.BatchKey).Select(g => g.ToArray()).ToArray();
        streamed = supported.Where(m => ExchangeApi.StreamedBook(m.Venue)).ToArray();
        if (streamed.Length != 0) stream = new(api, streamed);
    }

    public async Task<Dictionary<string, OverviewSpreadValue>> Capture(bool first, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var requests = batches.Select(group => ReadBatch(group, deadline.Token)).ToArray();
        var warming = first && stream != null ? WaitForBooks(stream, deadline.Token) : Task.CompletedTask;
        try
        {
            await Task.WhenAll(requests.Cast<Task>().Append(warming)).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = new Dictionary<string, OverviewSpreadValue>(StringComparer.Ordinal);
        foreach (var request in requests)
            if (request.IsCompletedSuccessfully)
                foreach (var (key, quote) in request.Result)
                    Add(key, quote);
        if (stream != null)
            foreach (var market in streamed)
                if (stream.Quote(market) is { } quote)
                    Add(market.Key, quote);
        return snapshot;

        void Add(string key, SpreadQuote quote)
        {
            if (quote.Percent(now) is { } percent) snapshot[key] = new(percent, quote.Time);
        }
    }

    private async Task<Dictionary<string, SpreadQuote>> ReadBatch(Market[] group, CancellationToken ct)
    {
        try { return await api.BestQuotes(group, ct).ConfigureAwait(false); }
        catch (Exception) { return new(); }
    }

    private async Task WaitForBooks(SpreadFilterMonitor monitor, CancellationToken ct)
    {
        while (true)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (streamed.All(m => monitor.Quote(m)?.Percent(now) != null)) return;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => stream?.Dispose();
}
