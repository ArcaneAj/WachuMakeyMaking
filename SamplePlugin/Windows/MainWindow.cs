using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using SamplePlugin.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace SamplePlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly RecipeCacheService recipeCacheService;

    // Track which recipes are selected (checked)
    private Dictionary<string, bool> recipeSelections = new();

    // Track currency values (keyed by currency RowId)
    private Dictionary<uint, float> currencyValues = new();

    // Track manual recipe value overrides (keyed by recipe key)
    // null means use calculated value, non-null means use this override
    private Dictionary<string, float?> recipeValueOverrides = new();


    // We give this window a hidden ID using ##.
    // The user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    public MainWindow(Plugin plugin, RecipeCacheService recipeCacheService)
        : base("My Amazing Window##With a hidden ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        this.recipeCacheService = recipeCacheService;

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

        ImGui.SameLine();
        if (ImGui.Button("Force Refresh"))
        {
            recipeCacheService.ForceRefresh();
            ResetRecipeSelections();
        }

        // Initialize cache if needed
        _ = recipeCacheService.EnsureCacheInitializedAsync();

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

                // Update recipe selections for any new recipes (default to checked)
                foreach (var recipe in cachedRecipes)
                {
                    var recipeKey = recipe.Item.Name.ToString();
                    if (!recipeSelections.ContainsKey(recipeKey))
                    {
                        recipeSelections[recipeKey] = true; // Default to checked
                    }
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

                if (cachedRecipes.Count > 0)
                {
                    ImGuiHelpers.ScaledDummy(10.0f);

                    var currencyGrouping = selectedRecipes.GroupBy(x => x.Currency.RowId);
                    
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
                        if (ImGui.InputFloat($"{currency.Name} value", ref currencyValue, 0, 0, "%.2f"))
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
                        var currencyId = recipe.Currency.RowId;
                        var currencyMultiplier = currencyValues.GetValueOrDefault(currencyId, 1.0f);

                        // Calculate base value
                        var calculatedValue = Math.Floor(recipe.Value * currencyMultiplier);

                        // Get the displayed value (use override if exists, otherwise calculated)
                        var hasOverride = recipeValueOverrides.TryGetValue(recipeKey, out var overrideValue);
                        var value = hasOverride ? overrideValue.Value : (float)calculatedValue;

                        // Editable value textbox (fixed width)
                        ImGui.SetNextItemWidth(80.0f); // Fixed width for the textbox
                        if (ImGui.InputFloat($"##value_{recipeKey}", ref value, 0, 0, "%.0f"))
                        {
                            // User edited the value, store as override (preserves manual changes)
                            recipeValueOverrides[recipeKey] = value;
                        }
                        else if (!hasOverride)
                        {
                            // No override exists and no edit - value will show calculated
                            // No need to store anything, calculated value will be used next frame
                        }
                        // If override exists and no edit, keep the override (preserves manual changes)

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

}
