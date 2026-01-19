using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Lumina.Excel.Sheets;
using SamplePlugin.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SamplePlugin.Services;

public class RecipeCacheService : IDisposable
{
    private readonly Plugin plugin;

    // Cache for recipes to avoid recalculating every frame
    private List<ModRecipe> cachedRecipes = new();
    private bool isCacheInitializing = false;

    public RecipeCacheService(Plugin plugin)
    {
        this.plugin = plugin;

        // Subscribe to inventory changes
        Plugin.GameInventory.InventoryChanged += OnInventoryChanged;
    }

    public void Dispose()
    {
        // Unsubscribe from inventory changes
        Plugin.GameInventory.InventoryChanged -= OnInventoryChanged;
    }

    public bool IsCacheInitializing => isCacheInitializing;

    public string CurrentProcessingStep { get; private set; } = string.Empty;

    public int CurrentProgress { get; private set; }

    public int TotalProgress { get; private set; }

    public List<ModRecipe> CachedRecipes => cachedRecipes;

    public void ForceRefresh()
    {
        cachedRecipes.Clear();
        isCacheInitializing = false;
        CurrentProcessingStep = string.Empty;
        CurrentProgress = 0;
        TotalProgress = 0;
    }

    public async Task EnsureCacheInitializedAsync()
    {
        if (cachedRecipes.Count == 0 && !isCacheInitializing)
        {
            isCacheInitializing = true;
            await Task.Run(InitializeRecipeCacheAsync);
        }
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        // Clear cache when inventory changes - it will be rebuilt on next access
        cachedRecipes.Clear();
        isCacheInitializing = false; // Reset flag so new cache can be built
        CurrentProcessingStep = string.Empty;
        CurrentProgress = 0;
        TotalProgress = 0;
    }

    private async Task InitializeRecipeCacheAsync()
    {
        try
        {
            CurrentProcessingStep = "Getting inventory items...";
            CurrentProgress = 0;

            // Get consolidated item stacks and crystals
            var consolidatedItems = await Task.Run(GetConsolidatedItems);
            var crystals = await Task.Run(GetCrystals);

            // Create inventory count lookup (items + crystals)
            var inventoryCounts = consolidatedItems.ToDictionary(
                stack => stack.Id,
                stack => stack.Quantity
            );

            foreach (var crystal in crystals)
            {
                inventoryCounts[crystal.Id] = crystal.Quantity;
            }

            // Set total progress to the number of consolidated items to process
            TotalProgress = consolidatedItems.Count;
            CurrentProcessingStep = $"Finding recipes... (0/{consolidatedItems.Count} items)";

            // Find all recipes that use at least one ingredient from inventory
            var allRecipesWithInventoryIngredients = new List<ModRecipe>();

            for (int i = 0; i < consolidatedItems.Count; i++)
            {
                var item = consolidatedItems[i];
                var recipes = await Task.Run(() => FindRecipesWithIngredient(item.Item));
                allRecipesWithInventoryIngredients.AddRange(recipes);

                CurrentProgress = i + 1;
                CurrentProcessingStep = $"Finding recipes... ({CurrentProgress}/{consolidatedItems.Count} items)";
            }

            // Filter to only recipes that are entirely satisfiable, and remove duplicates by result item name
            CurrentProcessingStep = "Filtering craftable recipes...";
            var craftableRecipes = allRecipesWithInventoryIngredients
                .Where(recipe => CanCraftRecipe(recipe, inventoryCounts))
                .DistinctBy(r => r.Item.Name.ToString()) // Remove duplicates by result item name
                .ToList();

            // Atomically update the cache
            cachedRecipes = craftableRecipes;
        }
        finally
        {
            isCacheInitializing = false;
        }
    }


    private List<ModItemStack> GetCrystals()
    {
        return GetItemsFromInventory(GameInventoryType.Crystals).ToList();
    }

    private List<ModItemStack> GetConsolidatedItems()
    {
        var allItems = new List<ModItemStack>();

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();

        // Collect items from all inventory bags (excluding crystals)
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory1));
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory2));
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory3));
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory4));

        // Consolidate items with the same ID
        var consolidatedItems = allItems
            .GroupBy(stack => stack.Id)
            .Select(group =>
            {
                var firstStack = group.First();
                var totalQuantity = group.Sum(stack => stack.Quantity);
                return new ModItemStack(firstStack.Item, firstStack.Id, totalQuantity);
            })
            .ToList();

        return consolidatedItems;
    }

    private IEnumerable<ModItemStack> GetItemsFromInventory(GameInventoryType inventory)
    {
        var items = new List<ModItemStack>();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var gameInventoryItems = Plugin.GameInventory.GetInventoryItems(inventory).ToArray().Where(x => x.ItemId != 0);
        foreach (var item in gameInventoryItems) {
            if (!itemSheet.TryGetRow(item.BaseItemId, out var itemRow))
            {
                continue;
            }

            items.Add(new ModItemStack(itemRow, item.BaseItemId, item.Quantity));
        }

        return items;
    }

    private ModRecipe GetRecipeIngredients(Recipe recipe)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var ingredientsDict = new Dictionary<Item, byte>();

        try
        {
            // Iterate through both collections simultaneously
            var ingredients = recipe.Ingredient;
            var amounts = recipe.AmountIngredient;

            for (int i = 0; i < Math.Min(ingredients.Count, amounts.Count); i++)
            {
                var ingredientRef = ingredients[i];
                var amount = amounts[i];

                // Check if this ingredient exists and has a positive amount
                if (ingredientRef.RowId != 0 && amount > 0)
                {
                    // Get the actual Item object from the Excel sheet
                    if (itemSheet.TryGetRow(ingredientRef.RowId, out var item))
                    {
                        ingredientsDict[item] = amount;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log error but don't throw - return what we have
            Plugin.Log.Error($"Error getting recipe ingredients: {ex.Message}");
        }

        return new ModRecipe(recipe.ItemResult.Value, ingredientsDict);
    }

    private bool CanCraftRecipe(ModRecipe recipe, Dictionary<uint, int> inventoryCounts)
    {
        // Check if we have enough of each ingredient
        foreach (var ingredient in recipe.Ingredients)
        {
            var ingredientId = ingredient.Key.RowId;

            // Check if we have this item in inventory
            if (!inventoryCounts.TryGetValue(ingredientId, out var availableQuantity))
            {
                return false; // Don't have this ingredient at all
            }

            // Check if we have enough quantity
            if (availableQuantity < ingredient.Value)
            {
                return false; // Don't have enough of this ingredient
            }
        }

        return true; // Have enough of all ingredients
    }

    private List<ModRecipe> FindRecipesWithIngredient(Item item)
    {
        var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();

        return recipeSheet.Select(GetRecipeIngredients).Where(x => x.Ingredients.ContainsKey(item)).ToList();
    }
}
