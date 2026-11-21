using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed record ColibriInstrument(string Exchange, string Symbol);

internal sealed class ColibriClient : IDisposable
{
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2)
    }) { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 12 * 1024 * 1024 };
    private readonly Func<CancellationToken, Task<int>> discoverPort;

    public ColibriClient(Func<CancellationToken, Task<int>>? discoverPort = null) =>
        this.discoverPort = discoverPort ?? ReadPort;

    private static async Task<int> ReadPort(CancellationToken ct)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Colibri", "localapi.json");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = await JsonDocument.ParseAsync(file, cancellationToken: ct);
            return document.RootElement.GetProperty("port").GetInt32();
        }
        catch (FileNotFoundException) { throw new IOException("Включите Local API в Colibri → Настройки → Программа."); }
        catch (DirectoryNotFoundException) { throw new IOException("Запустите Colibri и включите Local API в настройках программы."); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new IOException("Colibri ещё не опубликовал корректный порт Local API. Повторите проверку."); }
    }

    public async Task<int> Check(string slot, CancellationToken ct)
    {
        ValidateSlot(slot);
        int port = await discoverPort(ct);
        if (port is < 1 or > 65535) throw new IOException("В файле Local API Colibri указан некорректный порт.");
        var ping = await Get(port, "/ping", ct);
        if (ping.GetProperty("name").GetString() != "Colibri" || ping.GetProperty("apiVersion").GetInt32() != 1)
            throw new IOException("Эта версия Local API Colibri пока не поддерживается.");
        var target = await Get(port, SlotPath(slot), ct);
        var current = target.GetProperty("slot");
        if (!Guid.TryParse(current.GetProperty("id").GetString(), out var actual) || actual != Guid.Parse(slot))
            throw new IOException("Colibri вернул другую панель. Скопируйте ID нужного стакана заново.");
        if (current.GetProperty("content").GetProperty("kind").GetString() != "orderbook")
            throw new IOException("Указанная панель должна содержать стакан. Откройте в ней стакан в Colibri.");
        return port;
    }

    public async Task<ColibriInstrument> Resolve(int port, Market market, CancellationToken ct)
    {
        string[] types = MarketTypes(market);
        var catalog = await Get(port, "/exchanges", ct);
        var candidates = catalog.GetProperty("exchanges").EnumerateArray().Where(row =>
            types.Contains(row.GetProperty("marketType").GetString(), StringComparer.OrdinalIgnoreCase) &&
            MatchesVenue(row.GetProperty("id").GetString()!, market.Venue,
                [row.GetProperty("marketType").GetString()!])).ToArray();
        var matches = new List<ColibriInstrument>();
        foreach (var venue in candidates)
        {
            string id = venue.GetProperty("id").GetString()!;
            var symbols = await Get(port, $"/exchanges/{Uri.EscapeDataString(id)}/symbols", ct);
            if (!string.Equals(symbols.GetProperty("exchange").GetString(), id, StringComparison.Ordinal))
                throw new IOException("Каталог Colibri содержит несогласованный идентификатор биржи.");
            foreach (var symbol in symbols.GetProperty("symbols").EnumerateArray())
            {
                string wire = symbol.GetProperty("symbol").GetString()!;
                string asset = symbol.GetProperty("baseAsset").GetString()!;
                if (market.Contract != null) asset = ExchangeApi.Underlying(market.Venue, asset);
                if (wire.Equals(market.Symbol, StringComparison.OrdinalIgnoreCase) &&
                    symbol.GetProperty("quoteAsset").GetString()!.Equals(market.Quote, StringComparison.OrdinalIgnoreCase) &&
                    asset.Equals(market.Base, StringComparison.OrdinalIgnoreCase))
                    matches.Add(new(id, wire));
            }
        }
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new IOException($"В Colibri отсутствует точное соответствие для {market.Label}."),
            _ => throw new IOException($"Colibri вернул несколько рынков для {market.Label}. Однозначное соответствие не найдено.")
        };
    }

    private static string[] MarketTypes(Market market)
    {
        var contract = market.Contract;
        if (contract == null) return ["Spot"];
        // Colibri publishes WEEX crypto and TradFi perpetuals together as WeexLinearFutures.
        if (market.Venue == Venue.Weex)
            return contract is { Product: "linear", Unit: QuantityUnit.Base } ? ["LinearFutures"] : [];
        bool inverse = contract.Unit == QuantityUnit.Quote || contract.Product is "inverse" or "coinm" or "COIN-FUTURES";
        if (market.Venue == Venue.Mexc && contract.Multiplier <= 0)
            throw new IOException("Тип этого контракта MEXC не подтверждён для Colibri.");
        bool delivery = contract.Product == "FUTURES" || contract.Product.StartsWith("delivery/", StringComparison.Ordinal);
        bool perpetual = contract.Product == "SWAP" || contract.Product.StartsWith("futures/", StringComparison.Ordinal);
        if (market.Venue.Extended())
        {
            delivery = market.Venue == Venue.Htx && contract.Product == "delivery" ||
                market.Venue == Venue.Kraken && (market.Symbol.StartsWith("FF_", StringComparison.Ordinal) ||
                                                market.Symbol.StartsWith("FI_", StringComparison.Ordinal));
            perpetual = !delivery;
        }
        string family = inverse ? "Inverse" : "Linear";
        if (delivery) return [family + "Futures"];
        if (perpetual) return [family + "Perpetual"];
        return [family + "Futures", family + "Perpetual"];
    }

    private static bool MatchesVenue(string id, Venue venue, string[] types)
    {
        string[] names = venue == Venue.Gate ? ["Gate", "GateIo"] : venue == Venue.Alpha ? ["BinanceAlpha"] : [venue.ToString()];
        return names.Any(name => types.Any(type => id.Equals(name + type, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task Set(int port, string slot, ColibriInstrument instrument, CancellationToken ct)
    {
        var body = new { content = new { kind = "orderbook", exchange = instrument.Exchange, symbol = instrument.Symbol } };
        using var request = new HttpRequestMessage(HttpMethod.Put, Address(port, SlotPath(slot)))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        var result = await Send(request, ct);
        var returned = result.GetProperty("slot");
        var content = returned.GetProperty("content");
        if (result.GetProperty("status").GetString() != "changed" ||
            !Guid.TryParse(returned.GetProperty("id").GetString(), out var id) || id != Guid.Parse(slot) ||
            content.GetProperty("kind").GetString() != "orderbook" ||
            content.GetProperty("exchange").GetString() != instrument.Exchange ||
            content.GetProperty("symbol").GetString() != instrument.Symbol)
            throw new IOException("Colibri не подтвердил смену инструмента в выбранном стакане.");
    }

    private static void ValidateSlot(string slot)
    {
        if (!Guid.TryParse(slot, out _)) throw new IOException("Вставьте постоянный ID стакана, скопированный кнопкой ⧉ в Colibri.");
    }

    private static string SlotPath(string slot)
    {
        ValidateSlot(slot);
        return "/app/slots/" + Guid.Parse(slot).ToString("N");
    }

    private static Uri Address(int port, string path)
    {
        if (port is < 1 or > 65535) throw new IOException("Некорректный порт Colibri.");
        return new Uri($"http://127.0.0.1:{port}{path}");
    }

    private async Task<JsonElement> Get(int port, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Address(port, path));
        return await Send(request, ct);
    }

    private async Task<JsonElement> Send(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            string code = "";
            try { using var error = JsonDocument.Parse(json); code = error.RootElement.GetProperty("code").GetString() ?? ""; }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            throw new IOException(code switch
            {
                "unknown_slot" or "unknown_panel" => "Стакан с таким ID не найден в Colibri. Скопируйте его ID заново.",
                "unknown_symbol" => "Этот инструмент не найден в каталоге Colibri.",
                "slot_occupied_by_widget" => "В выбранной панели Colibri открыт виджет. Укажите ID стакана.",
                "unsupported_api_version" => "Версия Local API Colibri несовместима с этой интеграцией.",
                "not_ready" or "unavailable" => "Colibri ещё загружает данные. Повторите попытку.",
                "forbidden_origin" or "forbidden_host" or "permission_denied" => "Colibri отклонил локальное подключение. Проверьте настройки Local API.",
                "rate_limited" => "Colibri ограничил частоту запросов. Повторите попытку позже.",
                "not_found" => "В этой версии Colibri отсутствует нужный маршрут Local API. Обновите Colibri.",
                _ when (int)response.StatusCode is >= 300 and < 400 => "Colibri вернул перенаправление, которое интеграция не поддерживает.",
                _ => $"Local API Colibri вернул HTTP {(int)response.StatusCode}."
            });
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static string ErrorMessage(Exception ex) => ex switch
    {
        HttpRequestException => "Не удалось подключиться к Colibri. Запустите терминал и включите Local API.",
        OperationCanceledException => "Colibri не ответил вовремя. Повторите попытку.",
        JsonException or KeyNotFoundException or InvalidOperationException or FormatException =>
            "Ответ Local API Colibri имеет неподдерживаемый формат. Проверьте версию терминала.",
        IOException or UnauthorizedAccessException => ex.Message,
        _ => "Не удалось переключить стакан Colibri. Повторите проверку подключения."
    };

    public void Dispose() => http.Dispose();
}
