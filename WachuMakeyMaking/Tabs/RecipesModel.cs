using FFXIVClientStructs.FFXIV.Common.Lua;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Services;

namespace WachuMakeyMaking.Tabs
{
    public class RecipesModel : UpdatingModel
    {
        private readonly RecipeService recipeService;

        public List<ModItemWithValue> Recipes { get; private set; } = [];
        public Dictionary<ModItem, bool> RecipeSelections { get; private set; } = [];
        private Dictionary<ModItem, double> recipeValueOverrides = [];
        public Dictionary<ModItem, ModItemWithValue> RecipesWithValues { get; private set; } = [];
        public Dictionary<ModItem, ModItemWithValue> WiggledRecipesWithValues { get; private set; } = [];

        // Track currency values (keyed by currency RowId)
        public Dictionary<uint, float> CurrencyValues { get; private set; } = [];

        // Flags
        public bool UseProfitOverride { get; private set; } = false;

        public RecipesModel(RecipeService recipeService)
        {
            this.recipeService = recipeService;
            this.ScheduleUpdate();
        }

        protected override async Task UpdateAsync()
        {
            // From the recipe service get all recipes that use the selected ingredients (pushed to it by the ingredients tab)
            // as well as the prices from universalis that should be cached in memory
            var pricesByItemId = this.recipeService.PricesByItemId;
            var craftCostByItemId = this.recipeService.CraftCostByItemId;

            // Add all the recipes to the selection dictionary if they aren't already there, defaulting to false (not selected)
            foreach (var item in pricesByItemId.Values)
            {
                this.RecipeSelections.TryAdd(item, false);
            }

            // Apply the manual price overrides
            var baseRecipes = pricesByItemId.Values
                .Select(x => x with { Value = recipeValueOverrides.GetValueOrDefault(x.Item, x.Value) }).ToList();

            this.Recipes = baseRecipes;

            // Create a list of selected recipes to push to the solver
            var selectedRecipes = baseRecipes
                .Where(x => this.RecipeSelections.GetValueOrDefault(x.Item, false));

            selectedRecipes = !this.UseProfitOverride ? selectedRecipes : selectedRecipes.Select(
                x => new ModItemWithValue(
                    x.Item,
                    x.Value - (craftCostByItemId.TryGetValue(x.Item.RowId, out var craftCost) ? craftCost.Value : 0),
                    this.recipeService.PricesByItemId.GetValueOrDefault(x.Item.RowId, new ModItemWithValue(x.Item, 0, x.Item)).Item));

            // Create an index-matched list with wiggled values to actually pass, and keep the original for the lookup after
            var wiggledRecipes = selectedRecipes
                .Select((x, index) => x with { Value = x.Value + (index + 1) * 1e-6 })
                .ToList();
            
            // Write the selected and wriggled to a publicly visible getter to pass to the solver
            this.RecipesWithValues = selectedRecipes.ToDictionary(
                x => x.Item,
                x => new ModItemWithValue(
                    x.Item,
                    x.Value,
                    this.recipeService.PricesByItemId.GetValueOrDefault(x.Item.RowId, new ModItemWithValue(x.Item, 0, x.Item)).Item));

            this.WiggledRecipesWithValues = wiggledRecipes.ToDictionary(
                x => x.Item,
                x => new ModItemWithValue(
                    x.Item,
                    x.Value,
                    this.recipeService.PricesByItemId.GetValueOrDefault(x.Item.RowId, new ModItemWithValue(x.Item, 0, x.Item)).Item));
        }

        public void SetSelected(ModItem item, bool selected)
        {
            this.RecipeSelections[item] = selected;
            this.ScheduleUpdate();
        }

        public void RemoveCurrencyValue(uint id)
        {
            this.CurrencyValues.Remove(id);
            this.ScheduleUpdate();
        }

        public void SetCurrencyValue(uint currencyId, float value)
        {
            this.CurrencyValues[currencyId] = value;
            this.ScheduleUpdate();
        }

        public void SetRecipeValueOverride(ModItem item, int value)
        {
            this.recipeValueOverrides[item] = value;
            this.ScheduleUpdate();
        }

        public void ResetOverrides()
        {
            this.RecipeSelections = [];
            this.recipeValueOverrides = [];
            this.ScheduleUpdate();
        }
    }
}
