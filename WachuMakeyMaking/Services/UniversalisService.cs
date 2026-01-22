using Dalamud.Plugin.Services;
using WachuMakeyMaking.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WachuMakeyMaking.Services;

public class UniversalisService : IDisposable
{
    private readonly Plugin plugin;
    private readonly HttpClient httpClient;

    public UniversalisService(Plugin plugin)
    {
        this.plugin = plugin;
        httpClient = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        });
    }

    public void Dispose()
    {
        httpClient?.Dispose();
    }

    public async Task<AggregatedMarketBoardResult> GetMarketDataAsync(IEnumerable<uint> itemIds, CancellationToken cancellationToken = default)
    {
        // Get player's home world ID
        var homeWorldId = Plugin.PlayerState.HomeWorld.RowId;

        if (itemIds == null) return new AggregatedMarketBoardResult { results = new List<MarketBoardResult>(), failedItems = new List<uint>() };

        // Normalize and deduplicate list
        var idsArray = itemIds.Where(id => id != 0).Distinct().ToArray();
        if (idsArray.Length == 0) return new AggregatedMarketBoardResult { results = new List<MarketBoardResult>(), failedItems = new List<uint>() };

        var aggregatedResults = new List<MarketBoardResult>();
        var failed = new HashSet<uint>();

        // Universalis aggregated endpoint accepts up to ~100 ids per request; split into chunks of 100
        foreach (var chunk in idsArray.Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ids = string.Join(',', chunk);
            try
            {
                using var response = await httpClient.GetAsync($"https://universalis.app/api/v2/aggregated/{homeWorldId}/{ids}", cancellationToken);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Plugin.Log.Warning($"Universalis returned status {response.StatusCode} for ids: {ids}");
                    foreach (var id in chunk) failed.Add(id);
                    continue;
                }

                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var json = await JsonSerializer.DeserializeAsync<AggregatedMarketBoardResult>(responseStream, cancellationToken: cancellationToken);

                if (json?.results != null)
                {
                    aggregatedResults.AddRange(json.results);
                }

                if (json?.failedItems != null)
                {
                    foreach (var id in json.failedItems) failed.Add(id);
                }
            }
            catch (OperationCanceledException)
            {
                Plugin.Log.Warning("Universalis API request cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error calling Universalis API for ids [{ids}]: {ex.Message}");
                // mark all ids in this chunk as failed so the caller knows which items didn't return data
                foreach (var id in chunk) failed.Add(id);
            }

            await Task.Delay(50, cancellationToken); // brief pause to avoid rate limiting
        }

        return new AggregatedMarketBoardResult
        {
            results = aggregatedResults,
            failedItems = failed.ToList()
        };
    }

    public static double GetMarketValue(MarketBoardResult marketItem)
    {
        var marketValue = marketItem.hq?.minListing?.dc?.price ??
                         marketItem.nq?.minListing?.dc?.price ??
                         marketItem.hq?.recentPurchase?.dc?.price ??
                         marketItem.nq?.recentPurchase?.dc?.price ?? -1;

        if (marketValue == -1)
        {
            var marketItemJson = JsonSerializer.Serialize(marketItem, new JsonSerializerOptions { WriteIndented = true });
            Plugin.Log.Info($"Market item with no value found: {marketItemJson}");
        }

        return marketValue;
    }
}
