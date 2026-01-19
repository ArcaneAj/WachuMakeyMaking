// See https://aka.ms/new-console-template for more information
using SamplePlugin.Models;
using System.Net;
using System.Text.Json;

Console.WriteLine("Hello, World!");

var ids = "49220,49236,43341,43344,43345,43347,44202,44213,44219,44220,43263,43267,43271,43272,43274,43276,43279,43283,44189,44208,36081,31954,42458,43268,43278,43340,43342,43343,43346,44190,44195,44196,44201,44207,31957,49234,49235,49237,49238,44051,44224,43273,31953,31960,31958,31959,49222,49218,49219,49221";


var httpClient = new HttpClient(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    });

using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var cancellationToken = cancellationTokenSource.Token;
cancellationToken = CancellationToken.None;


using var result = await httpClient.GetAsync($"https://universalis.app/api/v2/aggregated/{21}/{ids}", cancellationToken);

// TODO: Process the result if needed
if (result.StatusCode != HttpStatusCode.OK)
{
    throw new HttpRequestException("Invalid status code " + result.StatusCode, null, result.StatusCode);
}

await using var responseStream = await result.Content.ReadAsStreamAsync(cancellationToken);

// Deserialize and re-serialize with indentation
//var jsonOptions = new JsonSerializerOptions
//{
//    WriteIndented = true
//};

//var jsonObject = await JsonSerializer.DeserializeAsync<object>(responseStream, cancellationToken: cancellationToken);
//var indentedJson = JsonSerializer.Serialize(jsonObject, jsonOptions);

//// Write the indented JSON to file
//await File.WriteAllTextAsync("G:\\Code\\WachuMakeyMaking\\Console\\response.json", indentedJson, cancellationToken);

var json = await JsonSerializer.DeserializeAsync<AggregatedMarketBoardResult>(responseStream, cancellationToken: cancellationToken);
if (json == null)
{
    throw new HttpRequestException("Universalis returned null response");
}


var items = new Dictionary<uint, MarketBoardResult>();
if (json.results != null)
{
    foreach (var item in json.results)
    {
        items.Add(item.itemId, item);
    }
}

Console.WriteLine($"{items.Count} items");
