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
    private List<ModItemStack>? _cachedItemStacks = null!;
    private Dictionary<uint, List<ModRecipe>> _cachedRecipes = new();
    private bool _isCacheInitializing = false;

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

    public bool IsCacheInitializing => _isCacheInitializing;

    public List<ModItemStack> CachedItemStacks => _cachedItemStacks;

    public Dictionary<uint, List<ModRecipe>> CachedRecipes => _cachedRecipes;

    public void ForceRefresh()
    {
        _cachedItemStacks = null;
        _cachedRecipes.Clear();
        _isCacheInitializing = false;
    }

    public async Task EnsureCacheInitializedAsync()
    {
        if (_cachedItemStacks == null && !_isCacheInitializing)
        {
            _isCacheInitializing = true;
            await Task.Run(InitializeRecipeCacheAsync);
        }
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        // Clear cache when inventory changes - it will be rebuilt on next access
        _cachedItemStacks = null;
        _isCacheInitializing = false; // Reset flag so new cache can be built
    }

    private async Task InitializeRecipeCacheAsync()
    {
        try
        {
            // Get a local copy of item stacks to avoid race conditions
            var itemStacks = await Task.Run(() => GetBagItemStacks());

            // Create a local recipes dictionary
            var recipes = new Dictionary<uint, List<ModRecipe>>();

            // Calculate recipes for all items in inventory
            foreach (var itemStack in itemStacks)
            {
                recipes[itemStack.Id] = await Task.Run(() => FindRecipesWithIngredient(itemStack.Item));
            }

            // Atomically update the cache
            _cachedItemStacks = itemStacks;
            _cachedRecipes = recipes;
        }
        finally
        {
            _isCacheInitializing = false;
        }
    }

    private List<ModItemStack> GetBagItemStacks()
    {
        var allItems = new List<ModItemStack>();

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();

        // Collect items from all inventory bags
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory1));
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory2));
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory3));
        allItems.AddRange(GetItemsFromInventory(GameInventoryType.Inventory4));

        return allItems;
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

    private List<ModRecipe> FindRecipesWithIngredient(uint ingredientId)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (!itemSheet.TryGetRow(ingredientId, out var item))
        {
            return [];
        }

        return FindRecipesWithIngredient(item);
    }

    private List<ModRecipe> FindRecipesWithIngredient(Item item)
    {
        var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();

        return recipeSheet.Select(GetRecipeIngredients).Where(x => x.Ingredients.ContainsKey(item)).ToList();
    }
}
