namespace ThinMarketMonitor;

internal sealed class ColibriBridge : IDisposable
{
    private readonly ColibriClient client;
    private readonly Settings settings;
    private readonly CancellationTokenSource stop = new();
    private CancellationTokenSource? resolving;
    private (Market Market, long Generation)? pending;
    private (bool Enabled, bool Sync, string Slot) configuration;
    private bool running;
    private long generation;
    public string Status { get; private set; } = "";
    public bool HasError { get; private set; }
    public event Action? StatusChanged;

    public ColibriBridge(Settings settings, ColibriClient? client = null)
    {
        this.settings = settings;
        this.client = client ?? new();
        configuration = Configuration();
    }

    private (bool, bool, string) Configuration() => (settings.ColibriEnabled, settings.ColibriOverviewSync, settings.ColibriSlot);

    public void Configure()
    {
        if (configuration == Configuration()) return;
        configuration = Configuration();
        CancelPending();
        Report("", false);
    }

    public void CancelPending()
    {
        generation++;
        pending = null;
        resolving?.Cancel();
    }

    public void Open(Market market, bool automatic = false)
    {
        if (stop.IsCancellationRequested || !settings.ColibriEnabled || automatic && !settings.ColibriOverviewSync) return;
        CancelPending();
        pending = (market, generation);
        Report("Ожидание переключения…", false);
        if (!running) _ = Pump();
    }

    private async Task Pump()
    {
        running = true;
        try
        {
            while (pending is { } request && !stop.IsCancellationRequested)
            {
                pending = null;
                using var resolution = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                resolving = resolution;
                string slot = settings.ColibriSlot;
                try
                {
                    await Task.Delay(180, resolution.Token);
                    int port = await client.Check(slot, resolution.Token);
                    var instrument = await client.Resolve(port, request.Market, resolution.Token);
                    resolution.Token.ThrowIfCancellationRequested();
                    if (request.Generation != generation || !settings.ColibriEnabled) continue;
                    Report($"Открытие {request.Market.Ticker}…", false);
                    // Serialize issued writes. A later selection is sent only after this write has completed.
                    await client.Set(port, slot, instrument, stop.Token);
                    if (request.Generation == generation) Report($"{request.Market.Label} · стакан переключён", false);
                }
                catch (Exception) when (resolution.IsCancellationRequested || stop.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (request.Generation == generation) Report(ColibriClient.ErrorMessage(ex), true);
                }
                finally { if (ReferenceEquals(resolving, resolution)) resolving = null; }
            }
        }
        finally { running = false; }
    }

    public async Task Check(CancellationToken ct)
    {
        long current = generation;
        try
        {
            if (!settings.ColibriEnabled) { Report("Включите интеграцию Colibri.", false); return; }
            await client.Check(settings.ColibriSlot, ct);
            if (current == generation) Report("Local API доступен · стакан найден", false);
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (current == generation) Report(ColibriClient.ErrorMessage(ex), true); }
    }

    private void Report(string message, bool error)
    {
        Status = message;
        HasError = error;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        CancelPending();
        stop.Cancel();
        client.Dispose();
    }
}
