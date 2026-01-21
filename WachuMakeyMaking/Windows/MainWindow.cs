using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Services;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly RecipeCacheService recipeCacheService;
    private readonly SolverService solverService;

    private static readonly bool CheckedDefault = false;

    // Track which recipes are selected (checked)
    private Dictionary<string, bool> recipeSelections = new();

    // Track currency values (keyed by currency RowId)
    private Dictionary<uint, float> currencyValues = new();

    // Track manual recipe value overrides (keyed by recipe key)
    // null means use calculated value, non-null means use this override
    private Dictionary<string, int> recipeValueOverrides = new();

    private Dictionary<uint, int> resourceQuantityOverrides = new();

    private Dictionary<uint, bool> resourceSelections = new();

    private readonly HashSet<ModItem> allIngredients = new();


    // We give this window a hidden ID using ##.
    // The user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    public MainWindow(Plugin plugin, RecipeCacheService recipeCacheService, SolverService solverService)
        : base("My Amazing Window##With a hidden ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        this.recipeCacheService = recipeCacheService;
        this.solverService = solverService;

        allIngredients = [.. recipeCacheService.FindRecipes().SelectMany(x => x.Ingredients.Keys)];

        if (false)
        {
            // One-time CSV dump of all items, streamed directly to file to avoid large in-memory buffers
            try
            {
                var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
                if (itemSheet != null)
                {
                    var path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "G:\\Code\\WachuMakeyMaking\\ItemDump.csv");

                    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));

                    // Optional header
                    writer.WriteLine("Name,RowId,Description");

                    foreach (var item in itemSheet)
                    {
                        var name = item.Name.ToString().Replace("\"", "\"\"");
                        var description = item.Description.ToString().Replace("\"", "\"\"");

                        writer.Write('"');
                        writer.Write(name);
                        writer.Write('"');
                        writer.Write(',');
                        writer.Write(item.RowId);
                        writer.Write(',');
                        writer.Write('"');
                        writer.Write(description);
                        writer.Write('"');
                        writer.WriteLine();
                    }

                    writer.Flush();
                    Plugin.Log.Information($"Item dump written to {path}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to dump item sheet: {ex}");
            }
        }
    }

    public void Dispose()
    {
        // Service disposal is handled by the plugin
    }

    private void ResetRecipeSelections()
    {
        recipeSelections.Clear();
    }

    public override void Draw()
    {
        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUi();
        }

        // Initialize cache if needed
        _ = recipeCacheService.EnsureCacheInitializedAsync();

        // Create tabs
        using (var tabBar = ImRaii.TabBar("MainTabs"))
        {
            if (tabBar.Success)
            {
                // Tab 1: Resources/Inventory
                using (var tab = ImRaii.TabItem("Resources"))
                {
                    if (tab.Success)
                    {
                        DrawResourcesTab();
                    }
                }

                // Tab 2: Recipes (current content)
                using (var tab = ImRaii.TabItem("Recipes"))
                {
                    if (tab.Success)
                    {
                        DrawRecipesTab();
                    }
                }

                // Tab 3: Results
                using (var tab = ImRaii.TabItem("Results"))
                {
                    if (tab.Success)
                    {
                        ImGui.Text("Results tab - coming soon!");
                    }
                }
            }
        }
    }

    private void DrawResourcesTab()
    {
        // Get actual inventory quantities before overrides
        var actualCrystals = recipeCacheService.GetCrystals();
        var actualItems = recipeCacheService.GetConsolidatedItems();
        // Combine cached and manual resources for display
        var allDisplayResources = actualItems.Concat(actualCrystals).Where(x => allIngredients.Contains(x.Item)).ToArray();
        var inventoryDict = allDisplayResources.ToDictionary(x => x.Item, x => x);

        ImGui.Text($"Available Resources: {allDisplayResources.Length}");

        ImGui.SameLine();
        if (ImGui.Button("Submit"))
        {
            recipeCacheService.ForceRefresh();
            ResetRecipeSelections();
        }

        // Column headers
        ImGui.Text("Quantity");
        ImGui.SameLine(110.0f); // Position after the fixed-width textbox
        ImGui.Text("Resource");
        ImGui.Separator();

        // Normally a BeginChild() would have to be followed by an unconditional EndChild(),
        // ImRaii takes care of this after the scope ends.
        // This works for all ImGui functions that require specific handling, examples are BeginTable() or Indent().
        using (var child = ImRaii.Child("ResourcesChildWithAScrollbar", Vector2.Zero, true))
        {
            // Check if this child is drawing
            if (child.Success)
            {

                foreach (var resourceItem in allDisplayResources.OrderBy(r => r.Item.Name.ToString()))
                {
                    var resourceId = resourceItem.Id;
                    var isSelected = resourceSelections.GetValueOrDefault(resourceId, true);

                    var quantity = GetResourceQuantity(resourceItem);

                    // Editable quantity textbox (fixed width)
                    ImGui.SetNextItemWidth(55.0f); // Fixed width for the textbox
                    if (ImGui.InputInt($"##quantity_{resourceId}", ref quantity))
                    {
                        // Clamp to valid range (0 to 999999)
                        resourceQuantityOverrides[resourceId] = Math.Min(Math.Max(quantity, 0), 999999);
                    }

                    ImGui.SameLine();

                    // Checkbox
                    if (ImGui.Checkbox($"##{resourceId}", ref isSelected))
                    {
                        resourceSelections[resourceId] = isSelected;
                    }

                    ImGui.SameLine();
                    var displayName = resourceItem.Item.Name;
                    if (inventoryDict.TryGetValue(resourceItem.Item, out var originalItemStack))
                    {
                        displayName += $" ({originalItemStack.Quantity} available)";
                    }
                    ImGui.Text(displayName);
                }
            }
        }
    }

    private void DrawRecipesTab()
    {
        // Normally a BeginChild() would have to be followed by an unconditional EndChild(),
        // ImRaii takes care of this after the scope ends.
        // This works for all ImGui functions that require specific handling, examples are BeginTable() or Indent().
        using (var child = ImRaii.Child("SomeChildWithAScrollbar", Vector2.Zero, true))
        {
            // Check if this child is drawing
            if (child.Success)
            {
                if (recipeCacheService.IsCacheInitializing)
                {
                    ImGui.Text("Loading recipes...");

                    if (!string.IsNullOrEmpty(recipeCacheService.CurrentProcessingStep))
                    {
                        ImGui.Text(recipeCacheService.CurrentProcessingStep);
                    }

                    if (recipeCacheService.TotalProgress > 0)
                    {
                        var progress = (float)recipeCacheService.CurrentProgress / recipeCacheService.TotalProgress;
                        ImGui.ProgressBar(progress, new Vector2(-1, 20));
                    }

                    return;
                }

                // Create a local snapshot of the cache to avoid race conditions
                var cachedRecipes = recipeCacheService.CachedRecipes?.ToList() ?? [];

                // Update recipe selections for any new recipes
                foreach (var recipe in cachedRecipes)
                {
                    var recipeKey = recipe.Item.Name.ToString();
                    if (!recipeSelections.ContainsKey(recipeKey))
                    {
                        recipeSelections[recipeKey] = CheckedDefault;
                    }

                    if (recipe.Item.Name.ToString().Contains("Grade 4 Artisanal")) recipeSelections[recipeKey] = true;
                }

                // Remove selections and overrides for recipes that are no longer in cache
                var currentRecipeKeys = new HashSet<string>(cachedRecipes.Select(r => r.Item.Name.ToString()));
                var keysToRemove = recipeSelections.Keys.Where(key => !currentRecipeKeys.Contains(key)).ToList();
                foreach (var key in keysToRemove)
                {
                    recipeSelections.Remove(key);
                    recipeValueOverrides.Remove(key);
                }

                var selectedRecipes = cachedRecipes.Where(r => recipeSelections.GetValueOrDefault(r.Item.Name.ToString(), false)).ToList();

                ImGui.Text($"{cachedRecipes.Count} craftable recipes found ({selectedRecipes.Count} selected)");

                ImGui.SameLine();
                if (ImGui.Button("Solve"))
                {
                    // We slight wiggle the costs in order to prefer one over the other to avoid degeneracy
                    var recipes = selectedRecipes.Select((ModRecipeWithValue x, int index) => x with { Value = GetRecipeValue(x) + 0.001 * index });
                    // Call the solver service
                    Task.Run(() => solverService.Solve(
                        recipes.ToList(),
                        [.. recipeCacheService.GetCrystals(), .. recipeCacheService.GetConsolidatedItems()]
                        ));
                }

                if (cachedRecipes.Count > 0)
                {
                    ImGuiHelpers.ScaledDummy(10.0f);

                    var currencyGrouping = cachedRecipes.GroupBy(x => x.Currency.RowId).Where(x => x.Key != 1);

                    // Clean up currency values for currencies that are no longer present
                    var currentCurrencyIds = new HashSet<uint>(currencyGrouping.Select(g => g.Key));
                    var currencyIdsToRemove = currencyValues.Keys.Where(id => !currentCurrencyIds.Contains(id)).ToList();
                    foreach (var id in currencyIdsToRemove)
                    {
                        currencyValues.Remove(id);
                    }

                    // Editable scrip value controls
                    foreach (var currencyGroup in currencyGrouping)
                    {
                        var currencyId = currencyGroup.Key;
                        var currency = currencyGroup.First().Currency;

                        // Initialize currency value if not present
                        if (!currencyValues.ContainsKey(currencyId))
                        {
                            currencyValues[currencyId] = 1.0f;
                        }

                        var currencyValue = currencyValues[currencyId];
                        if (ImGui.InputFloat($"{currency.Name} gil value", ref currencyValue, 0, 0, "%.2f"))
                        {
                            currencyValues[currencyId] = currencyValue;
                        }
                    }

                    ImGuiHelpers.ScaledDummy(5.0f);
                    ImGui.Text("Craftable Recipes:");

                    // Column headers
                    ImGui.Text("Value");
                    ImGui.SameLine(85.0f); // Position after the fixed-width textbox
                    ImGui.Text("Recipe");
                    ImGui.Separator();

                    foreach (var recipe in cachedRecipes.OrderBy(r => r.Item.Name.ToString()))
                    {
                        var recipeKey = recipe.Item.Name.ToString();
                        var isSelected = recipeSelections.GetValueOrDefault(recipeKey, false);

                        // Get the displayed value (use override if exists, otherwise calculated)
                        var value = GetRecipeValue(recipe);

                        // Editable value textbox (fixed width)
                        ImGui.SetNextItemWidth(80.0f); // Fixed width for the textbox
                        if (ImGui.InputInt($"##value_{recipeKey}", ref value))
                        {
                            // Clamp to valid range (0-999999)
                            recipeValueOverrides[recipeKey] = Math.Min(Math.Max(value, 0), 999999);
                        }

                        ImGui.SameLine();

                        // Checkbox
                        if (ImGui.Checkbox($"##{recipeKey}", ref isSelected))
                        {
                            recipeSelections[recipeKey] = isSelected;
                        }

                        ImGui.SameLine();
                        ImGui.Text($"{recipe.Item.Name}");
                    }
                }
            }
        }
    }

    private int GetResourceQuantity(ModItemStack resourceItem)
    {
        if (resourceQuantityOverrides.TryGetValue(resourceItem.Id, out var quantity)) return quantity;
        return resourceItem.Quantity;
    }

    private int GetRecipeValue(ModRecipeWithValue recipe)
    {
        var recipeKey = recipe.Item.Name.ToString();
        var currencyId = recipe.Currency.RowId;
        var currencyMultiplier = currencyValues.GetValueOrDefault(currencyId, 1.0f);

        // Calculate base value with currency multiplier
        var calculatedValue = Math.Min((int)Math.Floor(recipe.Value * currencyMultiplier), 999999);

        // Return manual override if exists, otherwise calculated value
        if(recipeValueOverrides.TryGetValue(recipeKey, out var overrideValue)) return Math.Min(overrideValue, 999999);
        return calculatedValue;
    }

}
