using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed class ExchangeApi(HttpClient http)
{
    private const string Binance = "https://api.binance.com/api/v3/";
    private const string Bybit = "https://api.bybit.com/v5/market/";
    private const string Okx = "https://www.okx.com/api/v5/";
    private const string Bitget = "https://api.bitget.com/api/v2/spot/";
    private const string Mexc = "https://api.mexc.com/api/v3/";
    private const string KuCoin = "https://api.kucoin.com/api/";
    private const string Gate = "https://api.gateio.ws/api/v4/spot/";
    private const string Alpha = "https://www.binance.com/bapi/defi/v1/public/";

    public async Task<Catalog> LoadCatalog(Venue venue, CancellationToken ct)
    {
        try
        {
            return new(await GetMarkets(venue, ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException
                                   or FormatException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            return new([], ex is OperationCanceledException ? "тайм-аут" : ex.Message);
        }
    }

    private async Task<Market[]> GetMarkets(Venue venue, CancellationToken ct)
    {
        if (venue == Venue.Alpha)
        {
            var tokenTask = Get(Alpha + "wallet-direct/buw/wallet/cex/alpha/all/token/list", ct);
            var pairTask = Get(Alpha + "alpha-trade/get-exchange-info", ct);
            await Task.WhenAll(tokenTask, pairTask);
            var tokens = Envelope(await tokenTask, "000000").EnumerateArray()
                .Where(x => !string.IsNullOrWhiteSpace(x.Str("alphaId")))
                .GroupBy(x => x.Str("alphaId"))
                .ToDictionary(g => g.Key, g => g.Select(x => x.Str("symbol").Trim().ToUpperInvariant()).Distinct().ToArray());
            var active = ReadPairs(venue, Envelope(await pairTask, "000000").GetProperty("symbols"), ct);
            if (active.Any(x => !tokens.TryGetValue(x.Base, out var names) || names.Length != 1 || names[0].Length == 0))
                throw new JsonException("Не удалось сопоставить все активные Alpha ID с тикерами");
            return Validate(active.Select(x => x with { Base = tokens[x.Base][0] }));
        }

        string url = venue switch
        {
            Venue.Binance => Binance + "exchangeInfo?permissions=SPOT&showPermissionSets=false",
            Venue.Bybit => Bybit + "instruments-info?category=spot",
            Venue.Okx => Okx + "public/instruments?instType=SPOT",
            Venue.Bitget => Bitget + "public/symbols",
            Venue.Mexc => Mexc + "exchangeInfo",
            Venue.KuCoin => KuCoin + "v2/symbols",
            Venue.Gate => Gate + "currency_pairs",
            _ => throw new ArgumentOutOfRangeException(nameof(venue))
        };
        var root = await Get(url, ct);
        if (venue == Venue.Bybit)
        {
            var pairs = new List<Market>();
            var seen = new HashSet<string>();
            while (true)
            {
                var page = BybitResult(root);
                pairs.AddRange(ReadPairs(venue, page.GetProperty("list"), ct));
                string cursor = page.Optional("nextPageCursor");
                if (string.IsNullOrEmpty(cursor)) return Validate(pairs);
                if (!seen.Add(cursor) || seen.Count > 30) throw new JsonException("Неполный каталог Bybit");
                root = await Get(url + "&cursor=" + Uri.EscapeDataString(cursor), ct);
            }
        }

        var rows = venue switch
        {
            Venue.Binance or Venue.Mexc => root.GetProperty("symbols"),
            Venue.Okx => Envelope(root, "0"),
            Venue.Bitget => Envelope(root, "00000"),
            Venue.KuCoin => Envelope(root, "200000"),
            _ => root
        };
        return Validate(ReadPairs(venue, rows, ct));
    }

    private static List<Market> ReadPairs(Venue venue, JsonElement rows, CancellationToken ct)
    {
        var pairs = new List<Market>();
        foreach (var x in rows.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            Market Make(string symbol, string baseName, string quote) => new(venue,
                x.Str(symbol), x.Str(baseName).Trim().ToUpperInvariant(), x.Str(quote).Trim().ToUpperInvariant());

            Market? pair = venue switch
            {
                Venue.Binance when x.Str("status") == "TRADING" && x.GetProperty("isSpotTradingAllowed").GetBoolean() =>
                    Make("symbol", "baseAsset", "quoteAsset"),
                Venue.Mexc when x.Str("status") == "1" && x.GetProperty("isSpotTradingAllowed").GetBoolean() =>
                    Make("symbol", "baseAsset", "quoteAsset"),
                Venue.Alpha when x.Str("status") == "TRADING" => Make("symbol", "baseAsset", "quoteAsset"),
                Venue.Bybit when x.Str("status") == "Trading" => Make("symbol", "baseCoin", "quoteCoin"),
                Venue.Okx when x.Str("state") == "live" => Make("instId", "baseCcy", "quoteCcy"),
                Venue.Bitget when x.Str("status") == "online" => Make("symbol", "baseCoin", "quoteCoin"),
                Venue.KuCoin when x.GetProperty("enableTrading").GetBoolean() => Make("symbol", "baseCurrency", "quoteCurrency"),
                Venue.Gate when x.Str("trade_status") is "tradable" or "buyable" or "sellable" => Make("id", "base", "quote"),
                _ => null
            };
            if (pair != null) pairs.Add(pair);
        }
        return pairs;
    }

    private static Market[] Validate(IEnumerable<Market> pairs)
    {
        var result = pairs.DistinctBy(x => x.Key).ToArray();
        if (result.Length == 0 || result.Any(x => x.Symbol.Length == 0 || x.Base.Length == 0 || x.Quote.Length == 0))
            throw new JsonException("Пустой или некорректный каталог");
        return result;
    }

    public async Task<Candle[]> Candles(Market market, bool recent, CancellationToken ct)
    {
        string symbol = Uri.EscapeDataString(market.Symbol);
        int count = recent ? 5 : 240;
        long start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - count * 60L;
        string url = market.Venue switch
        {
            Venue.Binance => Binance + $"klines?symbol={symbol}&interval=1m&limit={count}",
            Venue.Bybit => Bybit + $"kline?category=spot&symbol={symbol}&interval=1&limit={count}",
            Venue.Okx => Okx + $"market/candles?instId={symbol}&bar=1m&limit={count}",
            Venue.Bitget => Bitget + $"market/candles?symbol={symbol}&granularity=1min&limit={count}",
            Venue.Mexc => Mexc + $"klines?symbol={symbol}&interval=1m&limit={count}",
            Venue.KuCoin => KuCoin + $"v1/market/candles?symbol={symbol}&type=1min&startAt={start}",
            Venue.Gate => Gate + $"candlesticks?currency_pair={symbol}&interval=1m&limit={count}",
            Venue.Alpha => Alpha + $"alpha-trade/klines?symbol={symbol}&interval=1m&limit={count}",
            _ => throw new ArgumentOutOfRangeException(nameof(market))
        };
        var root = await Get(url, ct);
        var rows = market.Venue switch
        {
            Venue.Bybit => BybitResult(root).GetProperty("list"),
            Venue.Okx => Envelope(root, "0"),
            Venue.Bitget => Envelope(root, "00000"),
            Venue.KuCoin => Envelope(root, "200000"),
            Venue.Alpha => Envelope(root, "000000"),
            _ => root
        };
        return rows.EnumerateArray().Select(row => market.Venue switch
            {
                Venue.KuCoin => new Candle(row[0].Int() * 1000, row[1].Num(), row[3].Num(), row[4].Num(), row[2].Num()),
                Venue.Gate => new Candle(row[0].Int() * 1000, row[5].Num(), row[3].Num(), row[4].Num(), row[2].Num()),
                _ => new Candle(row[0].Int(), row[1].Num(), row[2].Num(), row[3].Num(), row[4].Num())
            })
            .Where(c => double.IsFinite(c.High) && double.IsFinite(c.Low) && double.IsFinite(c.Open) &&
                double.IsFinite(c.Close) && c.Low > 0 && c.Low <= Math.Min(c.Open, c.Close) && c.High >= Math.Max(c.Open, c.Close))
            .DistinctBy(c => c.Time).OrderBy(c => c.Time).ToArray();
    }

    public async Task<(Uri Address, int PingMilliseconds)> KuCoinSocket(CancellationToken ct)
    {
        using var response = await http.PostAsync(KuCoin + "v1/bullet-public", null, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var data = Envelope(json.RootElement, "200000");
        var server = data.GetProperty("instanceServers")[0];
        var endpoint = new Uri(server.Str("endpoint"));
        if (endpoint.Scheme != "wss" || !endpoint.Host.EndsWith(".kucoin.com", StringComparison.OrdinalIgnoreCase))
            throw new JsonException("Некорректный адрес потока KuCoin");
        var builder = new UriBuilder(endpoint)
        {
            Query = $"token={Uri.EscapeDataString(data.Str("token"))}&connectId={Guid.NewGuid():N}"
        };
        return (builder.Uri, (int)Math.Clamp(server.GetProperty("pingInterval").Int() * 0.75, 4000, 25000));
    }

    private async Task<JsonElement> Get(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.Clone();
    }

    private static JsonElement Envelope(JsonElement root, string successCode)
    {
        if (root.Str("code") != successCode) throw new JsonException($"API вернул код {root.Str("code")}");
        return root.GetProperty("data");
    }

    private static JsonElement BybitResult(JsonElement root)
    {
        if (root.Str("retCode") != "0") throw new JsonException($"Bybit вернул код {root.Str("retCode")}");
        return root.GetProperty("result");
    }
}

internal static class JsonFields
{
    public static string Str(this JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? "" : value.ToString();
    public static string Str(this JsonElement value, string key) => value.GetProperty(key).Str();
    public static string Optional(this JsonElement value, string key) => value.TryGetProperty(key, out var x) ? x.Str() : "";
    public static long Int(this JsonElement value) => long.Parse(value.Str(), CultureInfo.InvariantCulture);
    public static double Num(this JsonElement value) => double.Parse(value.Str(), CultureInfo.InvariantCulture);
}
