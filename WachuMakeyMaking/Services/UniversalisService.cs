using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Services;

public sealed class UniversalisService : IDisposable
{
    private readonly CollectableService collectableService;
    private readonly HttpClient httpClient;
    private readonly BatchProcessor<ModItem, ModItemWithValue> itemDataProcessor;
    private const int MaxAttempts = 6;
    public string ErrorMessage => this.errorMessage;
    private string errorMessage = string.Empty;
    private static readonly ExcelSheet<Item> ItemSheet = Plugin.DataManager.GetExcelSheet<Item>();
    private readonly ModItem gil;

    public UniversalisService(CollectableService collectableService)
    {
        this.collectableService = collectableService;
        this.gil = ItemSheet.GetRow(1).ToMod();
        this.httpClient = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
        var version =
            Assembly
                .GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        this.httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Plugin.Name, version));
        this.itemDataProcessor = new BatchProcessor<ModItem, ModItemWithValue>(
            batchFetcher: GetItemPricesAsync,
            batchSize: 100,
            batchTimeout: TimeSpan.FromMilliseconds(100),
            cacheTtl: TimeSpan.FromHours(1)
        );
    }

    public void Dispose()
    {
        this.httpClient?.Dispose();
        this.itemDataProcessor.Dispose();
    }

    public async Task<IReadOnlyDictionary<ModItem, ModItemWithValue>> GetOrFetchAsync(IEnumerable<ModItem> itemsToPrice)
    {
        return await itemDataProcessor.GetOrFetchAsync(itemsToPrice);
    }

    private async Task<Dictionary<ModItem, ModItemWithValue>> GetItemPricesAsync(
        List<ModItem> items,
        CancellationToken cancellationToken
    )
    {
        // Get player's home world ID
        var homeWorldId = Plugin.PlayerState.HomeWorld.RowId;
        var itemsById = items.ToDictionary(x => x.RowId, x => x);

        var marketBoardResults = new List<MarketBoardResult>();
        var toFetch = itemsById.Keys.ToList();

        var collectablesWithValues = new List<ModItemWithValue>();
        foreach (var itemId in toFetch)
        {
            if (itemsById.TryGetValue(itemId, out var item))
            {
                // Check if this item is collectable
                var (isCollectable, scripType, scripValue) = this.collectableService.GetCollectableInfo(item);
                if (isCollectable)
                {
                    collectablesWithValues.Add(new ModItemWithValue(item, scripValue, scripType));
                }
            }
        }

        // Take out the ones we found now that we're outside the loop
        foreach (var collectableItem in collectablesWithValues)
        {
            toFetch.Remove(collectableItem.Item.RowId);
        }

        for (var i = 0; i < MaxAttempts; i++)
        {
            if (toFetch.Count == 0)
                break;
            try
            {
                var json = await FetchChunk(toFetch, homeWorldId, cancellationToken);

                if (json?.results != null)
                {
                    marketBoardResults = json.results;
                }

                if (json?.failedItems != null)
                {
                    toFetch = json.failedItems;
                }
            }
            catch (Exception e)
            {
                var errorReason = ErrorReason.Other;
                if (e.Message.Contains("TooManyRequests"))
                {
                    errorReason = e.Message.Contains("max connections reached")
                        ? ErrorReason.MaxConnections
                        : ErrorReason.RateLimit;
                }

                this.errorMessage =
                    $"Error fetching market data from Universalis ({errorReason}), falling back to store prices for missing items.";
                // Exponential backoff to give universalis more time to breathe
                // https://docs.universalis.app/
                // "There is a rate limit of 25 req/s (50 req/s burst) on the API, and 15 req/s (30 req/s burst) on the website itself, if you're scraping instead."
                // "The number of simultaneous connections per IP is capped to 8."
                // More likely to be simultaneous connections via other plugins, as we should at worst have 2 or 3 concurrent calls
                if (i < MaxAttempts - 1) // No point waiting the last one if we're not going to try again
                    await Task.Delay(1000 * (int)Math.Pow(2, i), cancellationToken);
            }
        }

        var universalisResults = marketBoardResults.Select(x => new ModItemWithValue(
            itemsById[x.itemId],
            GetMarketValue(x),
            this.gil
        ));

        var storeItemsWithValues = toFetch
            .Where(itemId => itemsById.TryGetValue(itemId, out var _))
            .Select(itemId =>
            {
                var item = itemsById[itemId];
                // Get the item's store price as a fallback, assuming we make it HQ for a 10% bonus
                var storePrice = ItemSheet.GetRow(itemId).PriceLow * 1.1;
                var modItem = new ModItemWithValue(item, storePrice, this.gil);
                return modItem;
            })
            .ToList();

        return collectablesWithValues
            .Concat(universalisResults)
            .Concat(storeItemsWithValues)
            .ToDictionary(x => x.Item, x => x);
    }

    private async Task<AggregatedMarketBoardResult?> FetchChunk(
        List<uint> chunk,
        uint homeWorldId,
        CancellationToken cancellationToken = default
    )
    {
        var ids = string.Join(',', chunk);

        await Task.Delay(50, cancellationToken); // brief pause to avoid rate limiting
        using var response = await this.httpClient.GetAsync(
            $"https://universalis.app/api/v2/aggregated/{homeWorldId}/{ids}",
            cancellationToken
        );

        if (response.StatusCode != HttpStatusCode.OK)
        {
            Plugin.Log.Warning(
                $"Universalis returned status {homeWorldId} {response.StatusCode} {response.ReasonPhrase} {await response.Content.ReadAsStringAsync(cancellationToken)} for ids: {ids}"
            );
            return new AggregatedMarketBoardResult { results = [], failedItems = [.. chunk] };
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<AggregatedMarketBoardResult>(
            responseStream,
            cancellationToken: cancellationToken
        );
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static double GetMarketValue(MarketBoardResult marketItem)
    {
        var marketValue =
            marketItem.hq?.minListing?.dc?.price
            ?? marketItem.nq?.minListing?.dc?.price
            ?? marketItem.hq?.recentPurchase?.dc?.price
            ?? marketItem.nq?.recentPurchase?.dc?.price
            ?? -1;

        if (marketValue == -1)
        {
            var marketItemJson = JsonSerializer.Serialize(marketItem, JsonOptions);
            Plugin.Log.Info($"Market item with no value found: {marketItemJson}");
        }

        return marketValue;
    }

    private enum ErrorReason
    {
        Other,
        MaxConnections,
        RateLimit,
    }
}
