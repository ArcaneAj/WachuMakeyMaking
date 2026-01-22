namespace WachuMakeyMaking.Models
{
    public class SaleHistory
    {
        public RegionDcWorld<Sale>? minListing { get; set; }
        public RegionDcWorld<Sale>? recentPurchase { get; set; }
        public RegionDc<Sale>? averageSalePrice { get; set; }
        public RegionDc<Velocity>? dailySaleVelocity { get; set; }
    }
}
