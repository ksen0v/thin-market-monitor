using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Xml.Linq;
using System.Windows.Threading;
using ThinMarketMonitor;

internal static class ColibriChecks
{
    private const string Slot = "91f1c08302a14ced8e452631843f3075";
    private static readonly Market Btc = new(Venue.Binance, "BTCUSDT", "BTC", "USDT");
    private static readonly Market Eth = new(Venue.Binance, "ETHUSDT", "ETH", "USDT");

    public static async Task Run()
    {
        using var server = new LocalApi();
        using var client = new ColibriClient(_ => Task.FromResult(server.Port));
        int port = await client.Check(Slot, CancellationToken.None);
        var resolved = await client.Resolve(port, Btc, CancellationToken.None);
        await client.Set(port, Slot, resolved, CancellationToken.None);
        var write = server.Requests.Last(r => r.Method == "PUT");
        Ensure(write.Path == "/app/slots/" + Slot, "the copied slot ID is the only write target");
        using (var json = JsonDocument.Parse(write.Body))
        {
            var content = json.RootElement.GetProperty("content");
            Ensure(content.EnumerateObject().First().Name == "kind" &&
                content.GetProperty("kind").GetString() == "orderbook" &&
                content.GetProperty("exchange").GetString() == "BinanceSpot" &&
                content.GetProperty("symbol").GetString() == "BTCUSDT" &&
                content.EnumerateObject().Count() == 3, "orderbook payload and first discriminator");
        }
        var futures = await client.Resolve(port, Btc with { Contract = new("usdm") }, CancellationToken.None);
        Ensure(futures.Exchange == "BinanceLinearFutures", "futures stays on the futures venue");
        var denomination = new Market(Venue.Binance, "1000SHIBUSDT", "1000SHIB", "USDT");
        Ensure((await client.Resolve(port, denomination, CancellationToken.None)).Symbol == denomination.Symbol &&
            (await client.Resolve(port, denomination with { Base = "SHIB", Contract = new("usdm") }, CancellationToken.None))
            .Exchange == "BinanceLinearFutures", "spot denominations and futures aliases retain the exact wire symbol");
        server.WriteError = true;
        await Fails(() => client.Set(port, Slot, resolved, CancellationToken.None), "unknown symbol write is reported");
        server.WriteError = false;
        await Fails(() => client.Resolve(port, Btc with { Venue = Venue.KuCoin, Symbol = "BTC-USDT" }, CancellationToken.None),
            "unsupported venue has no fallback");
        await Fails(() => client.Resolve(port, Btc with { Symbol = "XBTUSDT" }, CancellationToken.None),
            "a similar symbol has no fallback");
        await Fails(() => client.Check("../wrong-slot", CancellationToken.None), "slot IDs cannot become paths");

        server.PingVersion = 99;
        await Fails(() => client.Check(Slot, CancellationToken.None), "unsupported protocol");
        server.PingVersion = 1;
        server.SlotKind = "chart";
        await Fails(() => client.Check(Slot, CancellationToken.None), "existing charts cannot be overwritten");
        server.SlotKind = "orderbook";
        server.SlotMissing = true;
        await Fails(() => client.Check(Slot, CancellationToken.None), "unknown slot");
        server.SlotMissing = false;
        server.Redirect = true;
        await Fails(() => client.Check(Slot, CancellationToken.None), "HTTP redirects stay unfollowed");
        server.Redirect = false;
        Ensure(!server.Requests.Any(r => r.Path == "/redirect-target"), "redirect target was never contacted");
        var closedListener = new TcpListener(IPAddress.Loopback, 0);
        closedListener.Start();
        int closedPort = ((IPEndPoint)closedListener.LocalEndpoint).Port;
        closedListener.Stop();
        using (var disconnected = new ColibriClient(_ => Task.FromResult(closedPort)))
        {
            bool refused = false;
            try { await disconnected.Check(Slot, CancellationToken.None); }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException)
            { refused = ColibriClient.ErrorMessage(ex).Contains("Colibri"); }
            Ensure(refused, "connection refusal has an actionable local API message");
        }
        Ensure(server.Requests.All(r => !r.Headers.Contains("Authorization:", StringComparison.OrdinalIgnoreCase) &&
            !r.Headers.Contains("Origin:", StringComparison.OrdinalIgnoreCase) &&
            (r.Path is "/ping" or "/exchanges" || r.Path.StartsWith("/exchanges/", StringComparison.Ordinal) ||
             r.Path == "/app/slots/" + Slot)), "only the approved local endpoints and headers were used");

        await OnDispatcher(async () =>
        {
            var settings = new Settings { ColibriEnabled = true, ColibriSlot = Slot, ColibriOverviewSync = true };
            using var bridge = new ColibriBridge(settings, new ColibriClient(_ => Task.FromResult(server.Port)));
            int initial = server.Writes.Count;
            bridge.Open(Btc, automatic: true);
            await Task.Delay(30);
            bridge.Open(Eth, automatic: true);
            await Until(() => server.Writes.Count == initial + 1 && bridge.Status.Contains("переключён"));
            Ensure(server.Writes.Last() == "ETHUSDT", "rapid selection coalesces to the last market");

            server.DelayWrite = true;
            initial = server.Writes.Count;
            bridge.Open(Btc);
            await Until(() => server.Writes.Count == initial + 1);
            bridge.Open(Eth);
            await Until(() => server.Writes.Count == initial + 2 && bridge.Status.Contains("ETH/USDT") &&
                bridge.Status.Contains("переключён"));
            Ensure(server.MaxConcurrentWrites == 1 && server.CompletedWrites.Last() == "ETHUSDT",
                "an in-flight write completes before the last selection is sent");
            server.DelayWrite = false;
            initial = server.Writes.Count;
            bridge.Open(Btc, automatic: true);
            settings.ColibriOverviewSync = false;
            bridge.Configure();
            await Task.Delay(260);
            Ensure(server.Writes.Count == initial, "disabling overview sync cancels queued selections");
            bridge.Open(Btc);
            settings.ColibriEnabled = false;
            bridge.Configure();
            await Task.Delay(260);
            Ensure(server.Writes.Count == initial, "disabling integration cancels queued selections");
        });

        await OnDispatcher(async () =>
        {
            var app = new TestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            var settings = new Settings { ColibriEnabled = true, ColibriSlot = Slot, ColibriOverviewSync = true };
            using var window = new MainWindow(false, settings, new ColibriClient(_ => Task.FromResult(server.Port)));
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var cells = (ChartCell[])typeof(MainWindow).GetField("cells", flags)!.GetValue(window)!;
            var cell = cells[0];
            cell.Activate(false);
            cell.Assign(Btc, pinned: true);
            var open = (Button)typeof(ChartCell).GetField("colibri", flags)!.GetValue(cell)!;
            Ensure(open.Content is Image { Source: System.Windows.Media.Imaging.BitmapImage logo } &&
                logo.UriSource.AbsoluteUri.EndsWith("/Assets/colibri.png", StringComparison.Ordinal),
                "the selected Colibri terminal uses its official logo");
            int initial = server.Writes.Count;
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => server.CompletedWrites.Count == initial + 1);
            Ensure(server.Writes.Last() == "BTCUSDT", "signal cell button reaches the actual market");
            var panel = (OverviewPanel)typeof(MainWindow).GetField("overviewPanel", flags)!.GetValue(window)!;
            var grid = (DataGrid)typeof(OverviewPanel).GetField("table", flags)!.GetValue(panel)!;
            var sync = (CheckBox)typeof(OverviewPanel).GetField("colibriSync", flags)!.GetValue(panel)!;
            int events = 0;
            panel.UserSelected += _ => events++;
            panel.SetSource([Btc, Eth], visible: false);
            grid.SelectedIndex = 0;
            await Task.Delay(250);
            Ensure(events == 0 && server.Writes.Count == initial + 1, "programmatic selection sends no request");
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            grid.SelectedIndex = 1;
            await Until(() => server.CompletedWrites.Count == initial + 2);
            Ensure(events == 1 && server.Writes.Last() == "ETHUSDT", "user navigation triggers overview synchronization");
            var before = Keyboard.FocusedElement;
            await Task.Delay(40);
            Ensure(Keyboard.FocusedElement == before, "API confirmation does not move keyboard focus");
            panel.SetSource([Eth, Btc], visible: false);
            typeof(OverviewPanel).GetMethod("RefreshSort", flags)!.Invoke(panel, null);
            await Task.Delay(250);
            Ensure(events == 1 && server.Writes.Count == initial + 2, "catalog refresh and numeric sorting send nothing");
            sync.IsChecked = false;
            sync.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Ensure(!settings.ColibriOverviewSync, "overview toggle persists its setting");
            ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var options = (SettingsPanel)typeof(MainWindow).GetField("settingsPanel", flags)!.GetValue(window)!;
            typeof(SettingsPanel).GetMethod("SelectPage", flags)!.Invoke(options, [6]);
            window.UpdateLayout();
            Ensure(options.ActualHeight > 0, "integration settings render");
            settings.ColibriEnabled = false;
            typeof(MainWindow).GetMethod("UpdateIntegration", flags)!.Invoke(window, null);
            Ensure(open.Visibility == Visibility.Collapsed && sync.Visibility == Visibility.Collapsed,
                "disabling integration updates signal and overview controls");
            window.Close();
            app.Shutdown();
        });

        var persisted = Settings.Deserialize(JsonSerializer.Serialize(new Settings
            { ColibriEnabled = true, ColibriOverviewSync = true, ColibriSlot = Slot }));
        Ensure(persisted.ColibriEnabled && persisted.ColibriOverviewSync && persisted.ColibriSlot == Slot &&
            !Settings.Deserialize("{}").ColibriEnabled, "settings persist and migration starts disabled");
        Console.WriteLine("Colibri local API payload, matching, error handling, serialization and cancellation passed.");
    }

    private static async Task Fails(Func<Task> action, string check)
    {
        try { await action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException(check);
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!predicate()) await Task.Delay(20, stop.Token);
    }

    private static void Ensure(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException(check);
    }

    private static Task OnDispatcher(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private sealed record Request(string Method, string Path, string Body, string Headers);

    private sealed class TestApp : Application
    {
        public void InitializeComponent()
        {
            var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppStyles.xaml"));
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var dictionary = new XElement(wpf + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                source.Root!.Element(wpf + "Application.Resources")!.Elements());
            Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        }
    }

    private sealed class LocalApi : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private int activeWrites;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public ConcurrentQueue<Request> Requests { get; } = new();
        public ConcurrentQueue<string> Writes { get; } = new();
        public ConcurrentQueue<string> CompletedWrites { get; } = new();
        public int PingVersion { get; set; } = 1;
        public string SlotKind { get; set; } = "orderbook";
        public bool SlotMissing { get; set; }
        public bool Redirect { get; set; }
        public bool DelayWrite { get; set; }
        public bool WriteError { get; set; }
        public int MaxConcurrentWrites { get; private set; }

        public LocalApi()
        {
            listener.Start();
            _ = Accept();
        }

        private async Task Accept()
        {
            try
            {
                while (!stop.IsCancellationRequested) _ = Serve(await listener.AcceptTcpClientAsync(stop.Token));
            }
            catch (OperationCanceledException) { }
        }

        private async Task Serve(TcpClient socket)
        {
            using (socket)
            {
                try
                {
                    using var reader = new StreamReader(socket.GetStream(), Encoding.UTF8, leaveOpen: true);
                    string[] line = (await reader.ReadLineAsync(stop.Token))!.Split(' ');
                    var headers = new List<string>();
                    int length = 0;
                    string? header;
                    while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(stop.Token)))
                    {
                        headers.Add(header);
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = int.Parse(header.Split(':')[1]);
                    }
                    var body = new char[length];
                    int received = 0;
                    while (received < length)
                    {
                        int count = await reader.ReadAsync(body.AsMemory(received), stop.Token);
                        if (count == 0) throw new IOException("Incomplete test request.");
                        received += count;
                    }
                    var request = new Request(line[0], line[1], new string(body), string.Join("\n", headers));
                    Requests.Enqueue(request);
                    int code = 200;
                    string payload;
                    if (Redirect) { code = 302; payload = "{}"; }
                    else if (request.Path == "/ping") payload = JsonSerializer.Serialize(new
                        { name = "Colibri", version = "1.3.0", apiVersion = PingVersion, port = Port });
                    else if (request.Path == "/exchanges") payload = """
                        {"exchanges":[{"id":"BinanceSpot","name":"Binance","marketType":"Spot","trading":true},
                        {"id":"BinanceLinearFutures","name":"Binance","marketType":"LinearFutures","trading":true},
                        {"id":"BybitSpot","name":"Bybit","marketType":"Spot","trading":true}]}
                        """;
                    else if (request.Path.EndsWith("/symbols", StringComparison.Ordinal))
                    {
                        string exchange = request.Path.Split('/')[2];
                        payload = JsonSerializer.Serialize(new { exchange, symbols = new[]
                        {
                            new { symbol = "BTCUSDT", baseAsset = "BTC", quoteAsset = "USDT" },
                            new { symbol = "ETHUSDT", baseAsset = "ETH", quoteAsset = "USDT" },
                            new { symbol = "1000SHIBUSDT", baseAsset = "1000SHIB", quoteAsset = "USDT" }
                        } });
                    }
                    else if (request.Path == "/app/slots/" + Slot && SlotMissing)
                    { code = 404; payload = """{"code":"unknown_slot","message":"missing"}"""; }
                    else if (request.Method == "GET" && request.Path == "/app/slots/" + Slot)
                        payload = JsonSerializer.Serialize(new { slot = new { id = Slot, content = new { kind = SlotKind } } });
                    else if (request.Method == "PUT" && WriteError)
                    { code = 404; payload = """{"code":"unknown_symbol"}"""; }
                    else if (request.Method == "PUT" && request.Path == "/app/slots/" + Slot)
                    {
                        using var json = JsonDocument.Parse(request.Body);
                        var content = json.RootElement.GetProperty("content").Clone();
                        string symbol = content.GetProperty("symbol").GetString()!;
                        Writes.Enqueue(symbol);
                        MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, Interlocked.Increment(ref activeWrites));
                        if (DelayWrite) await Task.Delay(300, stop.Token);
                        CompletedWrites.Enqueue(symbol);
                        Interlocked.Decrement(ref activeWrites);
                        payload = JsonSerializer.Serialize(new { status = "changed", slot = new { id = Slot, content } });
                    }
                    else { code = 404; payload = """{"code":"not_found"}"""; }
                    byte[] bytes = Encoding.UTF8.GetBytes(payload);
                    string response = $"HTTP/1.1 {code} OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n" +
                        (code == 302 ? $"Location: http://127.0.0.1:{Port}/redirect-target\r\n" : "") + "\r\n";
                    await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
                    await socket.GetStream().WriteAsync(bytes, stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            }
        }

        public void Dispose() { stop.Cancel(); listener.Stop(); }
    }
}
