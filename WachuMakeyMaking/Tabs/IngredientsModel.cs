using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Services;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Tabs
{
    public partial class IngredientsModel : UpdatingModel
    {
        private readonly InventoryService inventoryService;
        private readonly RecipeService recipeService;
        private const int MAX_LEVEL = 100;

        // Extended item information
        private readonly Dictionary<ModItem, HashSet<uint>> ingredientDivisions;
        private readonly HashSet<ModItem> ingredientsUsable;
        private readonly HashSet<ModItem> ingredientsEquippable;

        // UI Selections
        public Dictionary<string, Dictionary<ModNotebookDivision, bool>> DivisionTags { get; private set; } = [];
        public string[] DivisionCategories => [.. this.DivisionTags.Keys];
        public bool onlyEquippable;
        public bool onlyUnequippable;
        public bool onlyCrafted;
        public bool onlyRaw;

        // Search params
        public string IngredientAddFilter { get; private set; } = string.Empty;
        public string RecipeIngredientAddFilter { get; private set; } = string.Empty;

        // Data
        public Dictionary<ModItem, ModItemStack> Ingredients { get; private set; } = [];
        public ModItemStack[] DisplayItems { get; private set; } = null!;
        public Dictionary<uint, bool> IngredientSelections { get; private set; } = [];
        public List<ModItem> FilteredCandidates { get; private set; } = [];
        public List<ModItem> FilteredCraftableCandidates { get; private set; } = [];

        public IngredientsModel(InventoryService inventoryService, RecipeService recipeService, List<ModRecipe> recipes)
        {
            this.inventoryService = inventoryService;
            this.recipeService = recipeService;

            this.inventoryService.Reset();

            this.ingredientDivisions = SetupIngredientDivisions(recipes, this.recipeService.GetRecipesByOutput);
            this.ingredientsUsable = SetupUsability(recipes, this.recipeService.GetRecipesByOutput);
            this.ingredientsEquippable = SetupEquippability(recipes, this.recipeService.GetRecipesByOutput);

            this.DivisionTags = SetupDivisions(recipes);
            this.onlyEquippable = false;
            this.onlyUnequippable = false;
            this.onlyCrafted = false;
            this.onlyRaw = false;
            this.IngredientSelections = [];

            this.ScheduleUpdate();
        }

        protected override async Task UpdateAsync()
        {
            var allCraftableRecipes = this.recipeService.GetRecipes();

            HashSet<ModItem> allIngredients = [.. allCraftableRecipes.SelectMany(x => x.Ingredients.Keys)];
            // Get all the items actually in our bags
            var items = this.inventoryService.GetOverriddenItems();
            var ingredients = items.Where(x => allIngredients.Contains(x.Item));

            this.Ingredients = ingredients.ToDictionary(x => x.Item, x => x);

            // Add the manually inserted items and the overridden quantities
            var displayIngredients = ingredients.OrderBy(x => x.Item.Name);

            // Push to visible with a reverse lookup
            this.DisplayItems = [.. displayIngredients];

            // Merge all the newly added items to the selections defaulted as selected
            foreach (var item in this.DisplayItems)
            {
                this.IngredientSelections.TryAdd(item.Id, true);
            }

            this.FilteredCandidates = FilterResourceCandidates(allIngredients);
            this.FilteredCraftableCandidates = FilterCraftableCandidates(allIngredients);

            var selectedDisplayItems = this
                .DisplayItems.Where(x => this.IngredientSelections.GetValueOrDefault(x.Id, false))
                .ToList();

            this.recipeService.UpdateAsync(selectedDisplayItems);
        }

        public void ResetOverrides()
        {
            this.IngredientSelections = this.DisplayItems.ToDictionary(x => x.Id, x => true);
            this.inventoryService.ResetManualOverrides();
            this.ScheduleUpdate();
        }

        //////////////////////////////////////////////////////////////////////
        //// Public fetchers that allow finding information about an item ////
        //////////////////////////////////////////////////////////////////////
        public bool IngredientIsUsable(ModItem item)
        {
            return this.ingredientsUsable.Contains(item);
        }

        public bool IngredientIsEquippable(ModItem item)
        {
            return this.ingredientsEquippable.Contains(item);
        }

        public bool IngredientIsSelected(ModItem item)
        {
            return this.IngredientSelections[item.RowId];
        }

        public HashSet<uint> GetDivisionsForItem(ModItem item)
        {
            return this.ingredientDivisions.GetValueOrDefault(item, []);
        }

        ////////////////////////////////////////////////////////////////////////
        //// Mutation must go via these methods to trigger updates reliably ////
        ////////////////////////////////////////////////////////////////////////

        public void SetItemSource(string itemSource, bool isChecked)
        {
            this.inventoryService.ItemSources[itemSource] = isChecked;
            this.ScheduleUpdate();
        }

        public void SetItemQuantity(uint itemId, int quantity)
        {
            quantity = Math.Min(Math.Max(quantity, 0), 999999);
            this.inventoryService.SetItemQuantity(itemId, quantity);
            this.ScheduleUpdate();
        }

        public void SetDivisionCategory(string categoryName, bool isChecked)
        {
            foreach (var division in this.DivisionTags[categoryName].Keys)
            {
                this.DivisionTags[categoryName][division] = isChecked;
            }

            this.ScheduleUpdate();
        }

        public void SetDivision(string categoryName, ModNotebookDivision division, bool isChecked)
        {
            this.DivisionTags[categoryName][division] = isChecked;
            this.ScheduleUpdate();
        }

        public void AddManualIngredients(IEnumerable<ModItem> chosenItems)
        {
            foreach (var item in chosenItems)
            {
                this.inventoryService.AddManualIngredient(new ModItemStack(item, item.RowId, 0));
                this.IngredientSelections[item.RowId] = true;
            }

            this.ScheduleUpdate();
        }

        public void SetIngredientSelections(IEnumerable<ModItemStack> ingredients, bool value)
        {
            foreach (var ingredient in ingredients)
            {
                this.IngredientSelections[ingredient.Id] = value;
            }

            this.ScheduleUpdate();
        }

        public void SetIngredientAddFilter(string ingredientAddFilter)
        {
            this.IngredientAddFilter = ingredientAddFilter;
            this.ScheduleUpdate();
        }

        public void SetRecipeIngredientAddFilter(string recipeIngredientAddFilter)
        {
            this.RecipeIngredientAddFilter = recipeIngredientAddFilter;
            this.ScheduleUpdate();
        }

        ///////////////////////
        //// Private utils ////
        ///////////////////////
        private static HashSet<ModItem> SetupEquippability(
            IEnumerable<ModRecipe> recipes,
            Func<ModItem, List<ModRecipe>> getRecipeForItem
        )
        {
            var ingredientsEquippable = new HashSet<ModItem>();
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();

            // Add ingredients from recipes whose result is gear
            var gearRecipes = recipes
                .Where(r =>
                {
                    var row = itemSheet.GetRow(r.Item.RowId);
                    return row.FilterGroup <= 4; // treat <=4 as gear
                })
                .ToList();

            foreach (var recipe in gearRecipes)
            {
                foreach (var ingredient in recipe.Ingredients.Keys)
                {
                    ingredientsEquippable.Add(ingredient);
                }
            }

            // Recursively include ingredients of those ingredients regardless of their FilterGroup
            var nestedIngredientsToCheck = ingredientsEquippable
                .SelectMany(getRecipeForItem)
                .OfType<ModRecipe>()
                .ToList();

            if (nestedIngredientsToCheck.Count > 0)
            {
                ingredientsEquippable.UnionWith(
                    SetupEquippabilityRecursive(nestedIngredientsToCheck, getRecipeForItem)
                );
            }

            return ingredientsEquippable;
        }

        private static HashSet<ModItem> SetupEquippabilityRecursive(
            IEnumerable<ModRecipe> recipes,
            Func<ModItem, List<ModRecipe>> getRecipeForItem
        )
        {
            var ingredients = new HashSet<ModItem>();
            foreach (var recipe in recipes)
            {
                foreach (var ingredient in recipe.Ingredients.Keys)
                {
                    ingredients.Add(ingredient);
                }
            }

            var nested = ingredients.SelectMany(getRecipeForItem).OfType<ModRecipe>().ToList();
            if (nested.Count > 0)
            {
                ingredients.UnionWith(SetupEquippabilityRecursive(nested, getRecipeForItem));
            }

            return ingredients;
        }

        private static HashSet<ModItem> SetupUsability(
            IEnumerable<ModRecipe> recipes,
            Func<ModItem, List<ModRecipe>> getRecipeForItem
        )
        {
            var ingredientsUsable = new HashSet<ModItem>();
            foreach (var recipe in recipes.Where(RecipeService.HasRequirementsForRecipe))
            {
                foreach (var ingredient in recipe.Ingredients.Keys)
                {
                    ingredientsUsable.Add(ingredient);
                }
            }

            var nestedIngredientsToCheck = ingredientsUsable.SelectMany(getRecipeForItem).OfType<ModRecipe>().ToList();

            if (nestedIngredientsToCheck.Count > 0)
            {
                ingredientsUsable.UnionWith(SetupUsability(nestedIngredientsToCheck, getRecipeForItem));
            }

            return ingredientsUsable;
        }

        private static Dictionary<ModItem, HashSet<uint>> SetupIngredientDivisions(
            IEnumerable<ModRecipe> recipes,
            Func<ModItem, List<ModRecipe>> getRecipeForItem
        )
        {
            var ingredientDivisions = new Dictionary<ModItem, HashSet<uint>>();
            foreach (var recipe in recipes)
            {
                foreach (var ingredient in recipe.Ingredients.Keys)
                {
                    if (!ingredientDivisions.TryGetValue(ingredient, out var divisions))
                    {
                        divisions = [];
                        ingredientDivisions[ingredient] = divisions;
                    }

                    divisions.Add(recipe.noteBookDivisionId);
                }
            }

            var nestedIngredientsToCheck = ingredientDivisions
                .Keys.SelectMany(getRecipeForItem)
                .OfType<ModRecipe>()
                .ToList();

            // Add the root items so that we can track end product divisions as well.
            // We'll be reinserting the ingredients from the nested recipes, but as we're using a HashSet, duplicates will be ignored.
            foreach (var recipe in recipes)
            {
                if (!ingredientDivisions.TryGetValue(recipe.Item, out var recipeDivisions))
                {
                    recipeDivisions = [];
                    ingredientDivisions[recipe.Item] = recipeDivisions;
                }

                recipeDivisions.Add(recipe.noteBookDivisionId);
            }

            if (nestedIngredientsToCheck.Count > 0)
            {
                ingredientDivisions.MergeUnion(SetupIngredientDivisions(nestedIngredientsToCheck, getRecipeForItem));
            }

            return ingredientDivisions;
        }

        private static Dictionary<string, Dictionary<ModNotebookDivision, bool>> SetupDivisions(
            IEnumerable<ModRecipe> recipes
        )
        {
            var divisionCategorySheet = Plugin.DataManager.GetExcelSheet<NotebookDivisionCategory>();
            var divisionSheet = Plugin.DataManager.GetExcelSheet<NotebookDivision>();
            var levellingDivisions = divisionSheet
                .Where(x =>
                    x.NotebookDivisionCategory.RowId == 0
                    && x.Name.ToString().Length > 0
                    && char.IsDigit(x.Name.ToString()[0])
                    && TryParseInt(x.Name.ToString(), MAX_LEVEL) < MAX_LEVEL
                )
                .Select(x => new ModNotebookDivision(x));
            var masterworkDivisions = divisionSheet
                .Where(x => x.NotebookDivisionCategory.RowId == 1)
                .Select(x => new ModNotebookDivision(x))
                .OrderBy(
                    x =>
                        IsInteger()
                            .Split(x.Name.Replace("(", "").Replace(")", ""))
                            .Select(chunk => new ChunkWrapper(chunk)),
                    new ChunkComparer()
                );
            var housingDivisions = divisionSheet
                .Where(x => x.NotebookDivisionCategory.RowId == 2)
                .Select(x => new ModNotebookDivision(x));

            return new Dictionary<string, Dictionary<ModNotebookDivision, bool>>()
            {
                ["Standard"] = levellingDivisions.ToDictionary(x => x, x => true),
                [divisionCategorySheet.GetRow(1).Name.ToString()] = masterworkDivisions.ToDictionary(x => x, x => true),
                [divisionCategorySheet.GetRow(2).Name.ToString()] = housingDivisions.ToDictionary(x => x, x => true),
                ["Other"] = new() { [new ModNotebookDivision(null, "Other")] = true },
            };
        }

        private static int TryParseInt(string input, int defaultValue)
        {
            var match = IntPrefixMatch().Match(input);

            if (match.Success)
            {
                return int.Parse(match.Value);
            }

            return defaultValue;
        }

        [GeneratedRegex(@"\d{1,3}")]
        private static partial Regex IntPrefixMatch();

        [GeneratedRegex("([0-9]+)")]
        private static partial Regex IsInteger();

        // Wrapper to determine if a chunk is text or a number
        private class ChunkWrapper(string value)
        {
            public string Value { get; } = value;
            public bool IsNumber { get; } = int.TryParse(value, out _);
        }

        // Custom comparer to look at chunks sequentially
        private class ChunkComparer : IComparer<IEnumerable<ChunkWrapper>>
        {
            public int Compare(IEnumerable<ChunkWrapper>? x, IEnumerable<ChunkWrapper>? y)
            {
                if (x == null && y == null)
                    return 0;
                if (x == null)
                    return -1;
                if (y == null)
                    return 1;

                var enumX = x.GetEnumerator();
                var enumY = y.GetEnumerator();

                while (enumX.MoveNext() && enumY.MoveNext())
                {
                    var chunkX = enumX.Current;
                    var chunkY = enumY.Current;

                    if (chunkX.IsNumber && chunkY.IsNumber)
                    {
                        var numX = int.Parse(chunkX.Value);
                        var numY = int.Parse(chunkY.Value);
                        var cmp = numX.CompareTo(numY);
                        if (cmp != 0)
                            return cmp;
                    }
                    else
                    {
                        var cmp = string.Compare(chunkX.Value, chunkY.Value, StringComparison.OrdinalIgnoreCase);
                        if (cmp != 0)
                            return cmp;
                    }
                }
                return 0;
            }
        }

        private List<ModItem> FilterResourceCandidates(HashSet<ModItem> allIngredients)
        {
            var presentItems = new HashSet<uint>(this.DisplayItems.Select(x => x.Item.RowId) ?? []);
            var candidates = allIngredients.Where(x => !presentItems.Contains(x.RowId)).OrderBy(x => x.Name).ToList();

            var otherDivisionSelected = this.DivisionTags["Other"].First(x => x.Key.Name == "Other").Value;
            // Otherwise, filter by selected divisions
            var selectedDivisions = this
                .DivisionTags.SelectMany(category => category.Value.Where(tag => tag.Value).Select(tag => tag.Key))
                .Where(division => division.Division != null)
                .ToHashSet();
            candidates =
            [
                .. candidates.Where(item =>
                {
                    // Manual exclusionary filters
                    if (this.onlyEquippable && !this.ingredientsEquippable.Contains(item))
                    {
                        return false;
                    }

                    if (this.onlyUnequippable && this.ingredientsEquippable.Contains(item))
                    {
                        return false;
                    }

                    if (this.onlyCrafted && this.recipeService.GetRecipesByOutput(item).Count == 0)
                    {
                        return false;
                    }

                    if (this.onlyRaw && this.recipeService.GetRecipesByOutput(item).Count > 0)
                    {
                        return false;
                    }

                    // Inclusive OR filters

                    // If the item has no divisions, it doesn't match any selected division.
                    if (!this.ingredientDivisions.TryGetValue(item, out var divisions) || divisions.Count == 0)
                    {
                        // If the user has selected the "Other" division, include items with no divisions.
                        return otherDivisionSelected;
                    }

                    // If it has divisions, check if any of them match the selected divisions.
                    var matches = selectedDivisions.Select(x => x.RowId).Intersect(divisions).Any();

                    if (matches)
                    {
                        return true;
                    }

                    var allDivisionIds = this
                        .DivisionTags.SelectMany(category => category.Value.Select(tag => tag.Key.RowId))
                        .ToHashSet();
                    // If the user has selected the "Other" division, include items only when ALL of their divisions are not in the
                    // known division ids. Previously we included items if any division was unknown which made "Other" overly inclusive
                    // (e.g. items with one known division and one unknown division would be included). Require all divisions to be
                    // unknown so that items that have at least one known division are still filtered by the selected categories.
                    return otherDivisionSelected && divisions.All(x => !allDivisionIds.Contains(x));
                }),
            ];

            // Only include items that are usable based on the recipe requirements. e.g. it's used in at least 1 recipe we know how to craft.
            candidates = [.. candidates.Where(this.ingredientsUsable.Contains)];

            // Apply the filter (case-insensitive) to the candidate list.
            return string.IsNullOrWhiteSpace(this.IngredientAddFilter)
                ? candidates
                :
                [
                    .. candidates.Where(x =>
                        x.Name.ToString().Contains(this.IngredientAddFilter, StringComparison.OrdinalIgnoreCase)
                    ),
                ];
        }

        private List<ModItem> FilterCraftableCandidates(HashSet<ModItem> allIngredients)
        {
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            var candidates = allIngredients.ToList();

            var otherDivisionSelected = this.DivisionTags["Other"].First(x => x.Key.Name == "Other").Value;
            // Otherwise, filter by selected divisions
            var selectedDivisions = this
                .DivisionTags.SelectMany(category => category.Value.Where(tag => tag.Value).Select(tag => tag.Key))
                .Where(division => division.Division != null)
                .ToHashSet();

            candidates =
            [
                .. candidates.Where(item =>
                {
                    // Manual exclusionary filters
                    var recipes = this.recipeService.GetRecipesByOutput(item);

                    // Only including things with recipes, so if there's no recipe, we can skip the rest of the checks.
                    if (recipes.Count == 0)
                    {
                        return false;
                    }

                    if (!recipes.Any(recipe => RecipeService.HasRequirementsForRecipe(recipe)))
                    {
                        return false;
                    }

                    var row = itemSheet.GetRow(recipes.First().Item.RowId);
                    if (this.onlyEquippable && row.FilterGroup > 4)
                    {
                        return false;
                    }

                    if (this.onlyUnequippable && row.FilterGroup <= 4)
                    {
                        return false;
                    }

                    if (this.onlyRaw)
                    {
                        return false;
                    }

                    // Inclusive OR filters

                    // If the item has no divisions, it doesn't match any selected division.
                    if (!this.ingredientDivisions.TryGetValue(item, out var divisions) || divisions.Count == 0)
                    {
                        // If the user has selected the "Other" division, include items with no divisions.
                        return otherDivisionSelected;
                    }

                    // If it has divisions, check if any of them match the selected divisions.
                    var matches = selectedDivisions.Select(x => x.RowId).Intersect(divisions).Any();

                    if (matches)
                    {
                        return true;
                    }

                    var allDivisionIds = this
                        .DivisionTags.SelectMany(category => category.Value.Select(tag => tag.Key.RowId))
                        .ToHashSet();
                    // If the user has selected the "Other" division, include items with divisions not in the possible division ids
                    return otherDivisionSelected && !divisions.All(x => allDivisionIds.Contains(x));
                }),
            ];

            // Only include items that are usable based on the recipe requirements. e.g. it's used in at least 1 recipe we know how to craft.
            candidates = candidates
                .Where(x =>
                {
                    var recipes = this.recipeService.GetRecipesByOutput(x);
                    if (recipes.Count == 0)
                        return false;
                    // Filter out any candidates that have their entire ingredient list already added to the resource list
                    return !recipes.All(recipe =>
                        recipe.Ingredients.Keys.All(ingredient =>
                            this.DisplayItems.Any(displayItem => displayItem.Item.RowId == ingredient.RowId)
                        )
                    );
                })
                .OrderBy(x => x.Name)
                .ToList();

            // Apply the filter (case-insensitive) to the candidate list.
            return string.IsNullOrWhiteSpace(this.RecipeIngredientAddFilter)
                ? candidates
                :
                [
                    .. candidates.Where(x =>
                        x.Name.ToString().Contains(this.RecipeIngredientAddFilter, StringComparison.OrdinalIgnoreCase)
                    ),
                ];
        }
    }
}
