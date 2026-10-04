namespace WachuMakeyMaking.Utils
{
    public static class FeatureFlags
    {
        public const bool AllowSellingIngredients = false; // If true, the solver can choose to "sell" selected ingredients instead of crafting them into outputs

        public static bool VerifyBranchRows = false; // If true, the solver will verify that all branch rows are valid and log an error if any are invalid
    }
}
