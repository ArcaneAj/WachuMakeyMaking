using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using SamplePlugin.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace SamplePlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly string goatImagePath;
    private readonly Plugin plugin;

    // We give this window a hidden ID using ##.
    // The user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    public MainWindow(Plugin plugin, string goatImagePath)
        : base("My Amazing Window##With a hidden ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.goatImagePath = goatImagePath;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUi();
        }

        // Normally a BeginChild() would have to be followed by an unconditional EndChild(),
        // ImRaii takes care of this after the scope ends.
        // This works for all ImGui functions that require specific handling, examples are BeginTable() or Indent().
        using (var child = ImRaii.Child("SomeChildWithAScrollbar", Vector2.Zero, true))
        {
            // Check if this child is drawing
            if (child.Success)
            {

                uint ingotId = 44001;

                var itemStacks = GetBagItemStacks();

                ImGuiHelpers.ScaledDummy(20.0f);
                ImGui.Text($"{itemStacks.Count} found");

                //ImGui.Text("Player Inventory:");

                //using (ImRaii.PushIndent(20f))
                //{
                //    foreach (var item in itemStacks)
                //    {
                //        ImGui.Text($"{item.Item.Name}:{item.Id} x{item.Quantity}");
                //    }
                //}


                foreach (var itemStack in itemStacks)
                {
                    // Find recipes that use the ingot as an ingredient
                    var recipes = FindRecipesWithIngredient(itemStack.Item);

                    if (recipes.Count == 0) continue;

                    ImGuiHelpers.ScaledDummy(20.0f);
                    ImGui.Text($"Recipes using {itemStack.Item.Name} ({itemStack.Id}):");

                    ImGui.Text($"Found {recipes.Count} recipes");

                    using (ImRaii.PushIndent(20f))
                    {
                        foreach (var recipe in recipes)
                        {
                            ImGui.Text($"{recipe.Item.Name}");

                            //using (ImRaii.PushIndent(20f))
                            //{
                            //    // Display ingredients for this recipe
                            //    foreach (var pair in recipe.Ingredients)
                            //    {
                            //        ImGui.Text($"{pair.Key.Name}: {pair.Value}");
                            //    }
                            //}
                        }
                    }
                }
            }
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

    private string GetItemName(uint itemId)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (itemSheet.TryGetRow(itemId, out var itemRow))
        {
            return itemRow.Name.ToString();
        }

        return $"Unknown Item ({itemId})";
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
