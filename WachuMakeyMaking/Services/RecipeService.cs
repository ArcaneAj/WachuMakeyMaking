using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Caching.Memory;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Services
{
    public class RecipeService : BaseService, IRecipeService
    {
        private const int MAX_RECIPE_DEPTH = 10;
        private readonly TimeSpan cacheExpiration = TimeSpan.FromMinutes(30);
        private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
        private readonly UniversalisService universalisService;
        private static readonly ExcelSheet<Item> ItemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        private readonly ModItem gil;

        // Outputs
        public Dictionary<uint, ModItemWithValue> PricesByItemId { get; private set; } = [];
        public List<ModItemStack> SelectedIngredients { get; private set; } = [];

        // Progress tracking for the UI
        public uint InFlight { get; private set; } = 0;
        public string CurrentProcessingStep { get; private set; } = string.Empty;
        public string UniversalisMessage => this.universalisService.ErrorMessage;

        public RecipeService(UniversalisService universalisService)
        {
            this.universalisService = universalisService;

            this.gil = ItemSheet.GetRow(1).ToMod();
            GetRecipes();
        }

        // This class is responsible for getting the ingredients available passed into it
        // and spitting out what you can make with it, and what the values are.
        //
        // This should be recursive, so we can make tier 3 items if we have all the tier 1 ingredients
        // This should also return items themselves as "recipes" so we can list just selling the ingredient
        public async Task UpdateAsync(List<ModItemStack> selectedIngredients)
        {
            this.InFlight++;
            this.EmitUpdateStartEvent();
            try
            {
                // Filter out those that have the correct type but insufficient quantities - even if we craft the missing intermediaries
                var recipes = GetAllPossibleCrafts([.. selectedIngredients.Select(x => x.Item)])
                    .Where(recipe =>
                        recipe.CanMakeWith(selectedIngredients.ToDictionary(x => x.Item, x => x.Quantity))
                    );

                // Get all the prices for the recipes we can make, and the ingredients we have, and cache them in memory
                var itemsToPrice = recipes
                    .Select(x => x.Item)
                    .Concat(FeatureFlags.AllowSellingIngredients ? selectedIngredients.Select(x => x.Item) : [])
                    .ToHashSet();

                var prices = await universalisService.GetOrFetchAsync(itemsToPrice);

                var pricesByItemId = prices.ToDictionary(x => x.Key.RowId, x => x.Value);

                // Figure out the cost and therefore net profit for each recipe, and store it in the recipe object for later use.
                // We build up from layer 1 recipes and use them as the inputs for the next layer of recipes, so we can calculate the cost of making a recipe that uses other recipes as ingredients.
                // For example, if we want to make an iron dagger, we can either use the cost of the iron ingot, or we can use the cost of making the iron ingot from iron ore.
                // If the iron ingot is cheaper than the iron ore, we would sell the ore and use the ingot as our input cost. We therefore pick the cheaper of the two options for each ingredient, and use that as the cost of the recipe.
                // We need to therefore make a dictionary of the cheapest cost for each item as we go up the layers, and choose between that and the market price for each ingredient.
                var marketPrices = pricesByItemId.ToDictionary(x => x.Key, x => x.Value.Value);

                // These are the cheapest cost of making them from the ingredients we have.
                var craftedPrices = new Dictionary<uint, double>();

                var itemSet = new HashSet<ModItem>(selectedIngredients.Select(x => x.Item));

                double GetCheapestCost(uint itemId)
                {
                    var marketPrice = marketPrices.GetValueOrDefault(itemId, double.MaxValue);
                    var craftedPrice = craftedPrices.GetValueOrDefault(itemId, double.MaxValue);
                    return Math.Min(marketPrice, craftedPrice);
                }

                for (var i = 0; i < MAX_RECIPE_DEPTH; i++)
                {
                    // Get all the things we can make for out current ingredients set
                    var recipesMadeWithInputs = GetAllPossibleCrafts([.. itemSet]);

                    foreach (var recipe in recipesMadeWithInputs)
                    {
                        // Calculate the cost of making this recipe based on the cheapest costs of its ingredients
                        var cost = recipe.Ingredients.Sum(x => GetCheapestCost(x.Key.RowId) * x.Value);

                        // Store the crafted price, and overwrite it if it's cheaper than the existing price for this item (usually armorer vs blacksmith, etc)
                        if (craftedPrices.TryGetValue(recipe.Item.RowId, out var existingCost))
                        {
                            craftedPrices[recipe.Item.RowId] = Math.Min(existingCost, cost);
                        }
                        else
                        {
                            craftedPrices[recipe.Item.RowId] = cost;
                        }
                    }

                    // Get all the output items from those recipes
                    var recipeItems = recipesMadeWithInputs.Select(x => x.Item).ToHashSet();
                    // If there is at least 1 new item we've made, then we add it to the ingredient set and repeat
                    if (!recipeItems.Intersect(itemSet).Any())
                    {
                        break;
                    }

                    itemSet.UnionWith(recipeItems);
                }

                this.SelectedIngredients = selectedIngredients;
                this.PricesByItemId = pricesByItemId;

                // Make dummy entries for the ingredients themselves so we can compare them to the recipes we can make.
                // They are just recipes with themselves as the ingredient and no other ingredients, and the value is just the price of the item itself.
                // This way we can compare the profit of making a recipe vs just selling the ingredients themselves.
            }
            finally
            {
                this.EmitUpdateCompleteEvent();
                this.InFlight--;
            }
        }

        private List<ModRecipe> GetAllPossibleCrafts(List<ModItem> items)
        {
            CurrentProcessingStep = $"Finding recipes... (0 items)";
            // Determine what we can make with the items we have, then recursively ask the same question with the resulting items added until nothing new is found.
            var itemSet = new HashSet<ModItem>(items);
            var recipesSet = new HashSet<ModRecipe>();
            for (var i = 0; i < MAX_RECIPE_DEPTH; i++)
            {
                // Get all the things we can make for out current ingredients set
                var recipes = itemSet.SelectMany(FindRecipesWithIngredient).ToList();
                // Filter to recipes that have all the ingredients in our current set, not just one.
                recipes = recipes.Where(x => x.Ingredients.Keys.All(itemSet.Contains)).ToList();

                // Record the distinct recipes for return
                recipesSet.UnionWith(recipes);
                // Get all the output items from those recipes
                var recipeItems = recipes.Select(x => x.Item).ToHashSet();
                // If there is at least 1 new item we've made, then we add it to the ingredient set and repeat
                if (recipeItems.All(itemSet.Contains))
                {
                    break;
                }

                itemSet.UnionWith(recipeItems);
                CurrentProcessingStep = $"Finding recipes... ({itemSet.Count} items)";
            }

            return [.. recipesSet];
        }

        private static List<ModRecipe>? ModRecipes;

        public static List<ModRecipe> GetRecipes()
        {
            ModRecipes ??=
            [
                .. Plugin
                    .DataManager.GetExcelSheet<Recipe>()
                    .Where(x => x.ItemResult.Value.Name != string.Empty)
                    .Select(GetRecipeIngredients),
            ];

            return ModRecipes;
        }

        public List<ModRecipe> GetRecipesByOutputTestable(ModItem item)
        {
            return GetRecipesByOutput(item);
        }

        private static readonly Dictionary<ModItem, List<ModRecipe>> ModRecipesByOutputItem = [];

        public static List<ModRecipe> GetRecipesByOutput(ModItem item)
        {
            if (!ModRecipesByOutputItem.TryGetValue(item, out var cachedRecipes))
            {
                var recipes = GetRecipes().Where(x => x.Item == item).ToList();
                ModRecipesByOutputItem[item] = recipes;
                return recipes;
            }

            return cachedRecipes;
        }

        private static readonly Dictionary<ModItem, List<ModRecipe>> IngredientCache = [];

        public static List<ModRecipe> FindRecipesWithIngredient(ModItem item)
        {
            if (!IngredientCache.TryGetValue(item, out var cachedRecipes))
            {
                var recipes = GetRecipes().Where(x => x.Ingredients.ContainsKey(item)).ToList();
                IngredientCache[item] = recipes;
                return recipes;
            }

            return cachedRecipes;
        }

        //public Dictionary<uint, ModRecipe> FindRecipes()
        //{
        //    recipeCacheByRecipeId ??= Plugin
        //        .DataManager.GetExcelSheet<Recipe>()
        //        .Where(x => x.ItemResult.Value.Name != string.Empty)
        //        .Select(GetRecipeIngredients)
        //        .ToDictionary(x => x.RowId, x => x);

        //    recipeCacheByOutputItemId ??= recipeCacheByRecipeId.Values
        //        .GroupBy(x => x.Item.RowId)
        //        .ToDictionary(
        //            g => g.Key,
        //            g => g.ToArray()
        //        );

        //    return recipeCacheByRecipeId;
        //}

        //public ModRecipe? FindRecipeByResultItem(ModItem item)
        //{
        //    recipeCacheByOutputItemId ??= Plugin
        //        .DataManager.GetExcelSheet<Recipe>()
        //        .Where(x => x.ItemResult.Value.Name != string.Empty)
        //        .Select(GetRecipeIngredients)
        //        .GroupBy(x => x.Item.RowId)
        //        .ToDictionary(
        //            g => g.Key,
        //            g => g.ToArray()
        //        );

        //    recipeCacheByOutputItemId.TryGetValue(item.RowId, out var recipes);
        //    return recipes?.FirstOrDefault();
        //}

        private static ModRecipe GetRecipeIngredients(Recipe recipe)
        {
            var ingredientsDict = new Dictionary<ModItem, byte>();

            try
            {
                // Iterate through both collections simultaneously
                var ingredients = recipe.Ingredient;
                var amounts = recipe.AmountIngredient;

                for (var i = 0; i < Math.Min(ingredients.Count, amounts.Count); i++)
                {
                    var ingredientRef = ingredients[i];
                    var amount = amounts[i];

                    // Check if this ingredient exists and has a positive amount
                    if (ingredientRef.RowId != 0 && amount > 0)
                    {
                        // Get the actual Item object from the Excel sheet
                        if (ItemSheet.TryGetRow(ingredientRef.RowId, out var item))
                        {
                            ingredientsDict[item.ToMod()] = amount;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but don't throw - return what we have
                Plugin.Log.Error($"Error getting recipe ingredients: {ex.Message}");
            }

            // Get recipe level info
            var recipeLevelTable = Plugin.DataManager.GetExcelSheet<RecipeLevelTable>();
            var recipeLevel = recipeLevelTable.GetRow(recipe.RecipeLevelTable.RowId);

            var noteBookDivisionId =
                recipe.RecipeNotebookList.RowId != 0 && recipe.RecipeNotebookList.IsValid
                    ? (recipe.RecipeNotebookList.RowId - 1000) / 8 + 1000
                    : ((uint)recipe.RecipeLevelTable.Value.ClassJobLevel - 1) / 5;

            // offset of 8 is because 0-7 are the base combat classes in the ClassJob sheet we use later, but craft type starts at 0 since it only contains crafting classes
            return new ModRecipe(
                recipe.RowId,
                recipe.ItemResult.Value.ToMod(),
                recipe.AmountResult,
                ingredientsDict,
                recipeLevel.ClassJobLevel,
                recipe.CraftType.RowId + 8,
                recipe.SecretRecipeBook.RowId,
                noteBookDivisionId
            );
        }

        public static bool HasRequirementsForRecipe(ModRecipe recipe)
        {
            var classJobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
            var classJob = classJobSheet.GetRow(recipe.classJobId);

            var playerLevel = Plugin.PlayerState.GetClassJobLevel(classJob);
            if (playerLevel < recipe.classJobLevel)
            {
                return false; // Player level too low
            }

            if (recipe.book > 0)
            {
                unsafe
                {
                    if (!PlayerState.Instance()->IsSecretRecipeBookUnlocked(recipe.book))
                        return false;
                }
            }

            return true; // Have enough of all ingredients
        }
    }
}
