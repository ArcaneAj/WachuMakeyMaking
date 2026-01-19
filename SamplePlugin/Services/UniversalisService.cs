using Dalamud.Plugin.Services;
using SamplePlugin.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SamplePlugin.Services;

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

        var ids = string.Join(',', itemIds);

        try
        {
            using var result = await httpClient.GetAsync($"https://universalis.app/api/v2/aggregated/{homeWorldId}/{ids}", cancellationToken);

            if (result.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException("Invalid status code " + result.StatusCode, null, result.StatusCode);
            }

            await using var responseStream = await result.Content.ReadAsStreamAsync(cancellationToken);
            var json = await JsonSerializer.DeserializeAsync<AggregatedMarketBoardResult>(responseStream, cancellationToken: cancellationToken);
            if (json == null || json.results == null)
            {
                throw new HttpRequestException("Universalis returned null response");
            }

            return json;
        }
        catch (OperationCanceledException)
        {
            Plugin.Log.Warning("Universalis API request timed out after 10 seconds");
            throw;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error calling Universalis API: {ex.Message}");
            throw;
        }
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
