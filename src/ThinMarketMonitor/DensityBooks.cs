using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed class DensityRateLimit(TimeSpan delay) : Exception("Биржа ограничила частоту запросов стакана")
{
    public TimeSpan Delay { get; } = delay;
}

internal sealed partial class ExchangeApi
{
    private readonly SemaphoreSlim densityRequests = new(8);

    internal static bool SupportsDensity(Market market) => SpreadSupported(market) &&
        (market.Venue == Venue.Lighter
            ? UsdQuotes.Supported(market.Quote) && market.Identifiers?.TrustedUsdQuote != false
            : UsdQuotes.Supported(market) && double.IsFinite(market.Contract?.Multiplier ?? 1));

    internal static int[] DensityDepths(Market market) => market.Venue switch
    {
        Venue.Weex => [15, 200],
        Venue.KuCoin => [20, 100],
        Venue.Htx when market.Contract?.Product == "linear" => [30, 150],
        Venue.Htx => [20, 150],
        Venue.Hyperliquid => [20],
        Venue.Kraken when market.Contract != null => [int.MaxValue],
        Venue.Bybit or Venue.Okx or Venue.Bitget => [20, 50, 100, 200],
        _ => [20, 50, 100]
    };

    internal static string DensityUrl(Market market, int depth)
    {
        string symbol = Uri.EscapeDataString(market.RestSymbol);
        string? product = market.Contract?.Product;
        return market.Venue switch
        {
            Venue.Binance => (product == "usdm" ? Usdm : product == "coinm" ? Coinm : Binance) + $"depth?symbol={symbol}&limit={depth}",
            Venue.Bybit => Bybit + $"orderbook?category={product ?? "spot"}&symbol={symbol}&limit={depth}",
            Venue.Okx => Okx + $"market/books?instId={symbol}&sz={depth}",
            Venue.Bitget => $"https://api.bitget.com/api/v3/market/orderbook?category={product ?? "SPOT"}&symbol={symbol}&limit={depth}",
            Venue.Mexc => product == null ? Mexc + $"depth?symbol={symbol}&limit={depth}" :
                $"https://api.mexc.com/api/v1/contract/depth/{symbol}?limit={depth}",
            Venue.KuCoin => product == null ? KuCoin + $"v1/market/orderbook/level2_{depth}?symbol={symbol}" :
                KuContract + $"level2/depth{depth}?symbol={symbol}",
            Venue.Gate => product == null ? Gate + $"order_book?currency_pair={symbol}&limit={depth}" :
                GateContract + $"{product}/order_book?contract={symbol}&limit={depth}",
            Venue.Alpha => Alpha + $"alpha-trade/fullDepth?symbol={symbol}&limit={depth}",
            Venue.Htx => HtxMarket(market) + $"depth?{HtxParameter(market)}={symbol}&" + (product == null
                ? depth == 20 ? "type=step0&depth=20" : "type=step0"
                : depth < 150 ? "type=step6" : "type=step0"),
            Venue.Kraken => product == null ? KrakenSpot + $"Depth?pair={symbol}&count={depth}" :
                KrakenFutures + $"orderbook?symbol={symbol}",
            Venue.Weex => WeexMarket(market) + $"depth?symbol={symbol}&limit={depth}",
            Venue.Lighter => Lighter + $"orderBookOrders?market_id={symbol}&limit={depth}",
            Venue.Hyperliquid => Hyperliquid,
            _ => throw new ArgumentOutOfRangeException(nameof(market))
        };
    }

    public async Task<DensityBook> ReadDensityBook(Market market, int depth, double distance, CancellationToken ct)
    {
        await densityRequests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            return await RequestDensityBook(market, depth, distance, timeout.Token).ConfigureAwait(false);
        }
        finally { densityRequests.Release(); }
    }

    private async Task<DensityBook> RequestDensityBook(Market market, int depth, double distance, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(market.Venue == Venue.Hyperliquid ? HttpMethod.Post : HttpMethod.Get,
            DensityUrl(market, depth));
        if (market.Venue == Venue.Hyperliquid)
            request.Content = JsonContent.Create(new { type = "l2Book", coin = market.Symbol });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or (HttpStatusCode)418)
        {
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow :
                TimeSpan.FromSeconds((int)response.StatusCode == 418 ? 120 : 30));
            throw new DensityRateLimit(delay);
        }
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);
        var root = json.RootElement;
        if (market.Venue == Venue.Bybit && root.Optional("retCode") == "10006" ||
            market.Venue == Venue.Okx && root.Optional("code") == "50011" ||
            market.Venue == Venue.KuCoin && root.Optional("code") == "429000" ||
            market.Venue == Venue.Binance && root.Optional("code") == "-1003")
            throw new DensityRateLimit(TimeSpan.FromSeconds(30));
        return DecodeDensityBook(market, root, depth, distance);
    }

    internal static DensityBook DecodeDensityBook(Market market, JsonElement root, int depth, double maxDistance)
    {
        if (market.Venue == Venue.Hyperliquid)
        {
            if (root.Str("coin") != market.Symbol) throw new JsonException("Hyperliquid вернул другой инструмент");
            var levels = root.GetProperty("levels");
            return ReadSides(levels[0], levels[1], depth, maxDistance, "px", "sz");
        }
        var book = market.Venue switch
        {
            Venue.Bybit => BybitResult(root),
            Venue.Okx => Envelope(root, "0")[0],
            Venue.Bitget => Envelope(root, "00000"),
            Venue.Alpha => Envelope(root, "000000"),
            Venue.Mexc when market.Contract != null => Envelope(root, "0"),
            Venue.KuCoin => Envelope(root, "200000"),
            Venue.Htx => HtxField(root, "tick"),
            Venue.Kraken when market.Contract == null => KrakenResult(root).EnumerateObject().Single().Value,
            Venue.Kraken => KrakenFuturesField(root, "orderBook"),
            Venue.Lighter when root.Optional("code") != "200" => throw new JsonException("Lighter не вернул стакан"),
            _ => root
        };
        bool shortNames = market.Venue is Venue.Bybit or Venue.Bitget;
        var (price, amount) = market.Venue switch
        {
            Venue.Gate when market.Contract != null => ("p", "s"),
            Venue.Lighter => ("price", "remaining_base_amount"),
            _ => (null, null)
        };
        string symbolField = market.Venue == Venue.Bybit ? "s" : market.Venue == Venue.Alpha ? "symbol" : "";
        if (symbolField.Length > 0 && book.Optional(symbolField) is { Length: > 0 } symbol && symbol != market.RestSymbol)
            throw new JsonException("Биржа вернула стакан другого инструмента");
        bool krakenFutures = market.Venue == Venue.Kraken && market.Contract != null;
        return ReadSides(book.GetProperty(shortNames ? "b" : "bids"), book.GetProperty(shortNames ? "a" : "asks"),
            depth, maxDistance, price, amount, krakenFutures);
    }

    private static DensityBook ReadSides(JsonElement bids, JsonElement asks, int depth, double maxDistance,
        string? priceField, string? amountField, bool reverseBids = false)
    {
        // Kraken Futures sends bids in ascending order.
        IEnumerable<JsonElement> bidRows = reverseBids ? bids.EnumerateArray().Reverse().ToArray() : bids.EnumerateArray();
        var askRows = asks.EnumerateArray();
        double bid = Best(bidRows), ask = Best(askRows);
        if (bid <= 0 || ask <= bid) return new([], []);
        double mid = bid / 2 + ask / 2;
        var buy = ReadSide(bidRows);
        var sell = ReadSide(askRows);
        return new(buy.Levels, sell.Levels, Math.Max(buy.Read, sell.Read), buy.Limited, sell.Limited);

        double Best(IEnumerable<JsonElement> rows)
        {
            foreach (var row in rows)
            {
                var entry = Level(row);
                if (entry.Quantity > 0) return entry.Price;
            }
            return 0;
        }

        (BookLevel[] Levels, int Read, bool Limited) ReadSide(IEnumerable<JsonElement> rows)
        {
            var levels = new List<BookLevel>();
            int read = 0;
            foreach (var row in rows)
            {
                var entry = Level(row);
                read++;
                if (entry.Quantity == 0) continue;
                bool outside = Math.Abs((entry.Price - mid) / mid) * 100 > maxDistance;
                if (outside && levels.Count != 0) return (levels.ToArray(), read, false);
                if (levels.Count > 0 && levels[^1].Price == entry.Price)
                    levels[^1] = new(entry.Price, levels[^1].Quantity + entry.Quantity);
                else levels.Add(entry);
                if (outside) return (levels.ToArray(), read, false);
            }
            return (levels.ToArray(), read, read >= depth);
        }

        BookLevel Level(JsonElement row)
        {
            double price = priceField == null ? row[0].Num() : row.GetProperty(priceField).Num();
            double size = amountField == null ? row[1].Num() : row.GetProperty(amountField).Num();
            if (!double.IsFinite(price) || price <= 0 || !double.IsFinite(size) || size < 0)
                throw new JsonException("Некорректный уровень стакана");
            return new(price, size);
        }
    }
}
