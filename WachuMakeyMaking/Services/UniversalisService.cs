using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WachuMakeyMaking.Models;

namespace WachuMakeyMaking.Services;

public sealed class UniversalisService : IDisposable
{
    private readonly HttpClient httpClient;
    private const int MaxRetries = 3;
    public string ErrorMessage => this.errorMessage;
    private string errorMessage = string.Empty;

    public UniversalisService()
    {
        this.httpClient = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
    }

    public void Dispose()
    {
        this.httpClient?.Dispose();
    }

    public async Task<AggregatedMarketBoardResult> GetMarketDataAsync(
        List<uint> itemIds,
        Action<string> onIterationUpdate,
        CancellationToken cancellationToken = default
    )
    {
        this.errorMessage = string.Empty;
        // Get player's home world ID
        var homeWorldId = Plugin.PlayerState.HomeWorld.RowId;

        // Normalize and deduplicate list
        var idsArray = itemIds.Where(id => id != 0).Distinct().ToList();
        if (idsArray == null || idsArray.Count == 0)
            return new AggregatedMarketBoardResult { results = [], failedItems = [] };

        var results = new List<MarketBoardResult>();
        var newResults = new List<MarketBoardResult>();
        var failed = new List<uint>();

        for (var i = 0; i < MaxRetries; i++)
        {
            try
            {   
                (newResults, idsArray) = await GetDataForWorldAsync(homeWorldId, idsArray, onIterationUpdate, cancellationToken);
                results.AddRange(newResults);
                break;
            }
            catch (OperationCanceledException)
            {
                Plugin.Log.Info("Universalis API request cancelled");
                throw; // Should bypass retries
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error calling Universalis API for ids [{idsArray}]: {ex.Message}");
                // Wait a second and try again
                await Task.Delay(i * 1000, cancellationToken);
            }
        }

        if (idsArray.Count != 0)
        {
            this.errorMessage =
                "Error fetching market data from Universalis, falling back to store prices for missing items.";
            Plugin.ChatGui.PrintError(this.errorMessage);
        }

        return new AggregatedMarketBoardResult { results = results, failedItems = idsArray };
    }

    private async Task<(List<MarketBoardResult> results, List<uint> failed)> GetDataForWorldAsync(
        uint homeWorldId,
        List<uint> idsArray,
        Action<string> onIterationUpdate,
        CancellationToken cancellationToken
    )
    {
        if (idsArray.Count == 0)
            return ([], []);
        var aggregatedResults = new List<MarketBoardResult>();
        var failed = new List<uint>();

        // Universalis aggregated endpoint accepts up to ~100 ids per request; split into chunks of 100
        foreach (var chunk in idsArray.Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var json = await FetchChunk(chunk, homeWorldId, cancellationToken);

            if (json?.results != null)
            {
                aggregatedResults.AddRange(json.results);
            }

            if (json?.failedItems != null)
            {
                failed.AddRange(json.failedItems);
            }

            onIterationUpdate($"Fetching market prices... {aggregatedResults.Count} complete, {failed.Count} failed, {idsArray.Count - aggregatedResults.Count - failed.Count} remaining");
        }

        return (aggregatedResults, failed);
    }

    private async Task<AggregatedMarketBoardResult?> FetchChunk(uint[] chunk, uint homeWorldId, CancellationToken cancellationToken = default)
    {
        var ids = string.Join(',', chunk);

        await Task.Delay(50, cancellationToken); // brief pause to avoid rate limiting
        using var response = await this.httpClient.GetAsync(
            $"https://universalis.app/api/v2/aggregated/{homeWorldId}/{ids}",
            cancellationToken
        );

        if (response.StatusCode != HttpStatusCode.OK)
        {
            Plugin.Log.Warning($"Universalis returned status {homeWorldId} {response.StatusCode} {response.ReasonPhrase} {await response.Content.ReadAsStringAsync(cancellationToken)} for ids: {ids}");
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
}
