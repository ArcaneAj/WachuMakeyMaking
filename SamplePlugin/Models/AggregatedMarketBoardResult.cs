using System.Collections.Generic;

namespace SamplePlugin.Models
{
    public class AggregatedMarketBoardResult
    {
        public List<MarketBoardResult>? results { get; set; }
        public List<uint>? failedItems { get; set; }
    }
}
