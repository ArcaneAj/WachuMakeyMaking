using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SamplePlugin.Models;
using SamplePlugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SamplePlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly RecipeCacheService recipeCacheService;

    // Track which recipes are selected (checked)
    private Dictionary<string, bool> recipeSelections = new();

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

                // Remove selections for recipes that are no longer in cache
                var currentRecipeKeys = new HashSet<string>(cachedRecipes.Select(r => r.Item.Name.ToString()));
                var keysToRemove = recipeSelections.Keys.Where(key => !currentRecipeKeys.Contains(key)).ToList();
                foreach (var key in keysToRemove)
                {
                    recipeSelections.Remove(key);
                }

                var selectedRecipes = cachedRecipes.Where(r => recipeSelections.GetValueOrDefault(r.Item.Name.ToString(), false)).ToList();

                ImGui.Text($"{cachedRecipes.Count} craftable recipes found ({selectedRecipes.Count} selected)");

                if (cachedRecipes.Count > 0)
                {
                    ImGuiHelpers.ScaledDummy(10.0f);
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

                        // Editable value textbox (fixed width)
                        var value = (float)recipe.Value; // Convert double to float for ImGui
                        ImGui.SetNextItemWidth(80.0f); // Fixed width for the textbox
                        ImGui.InputFloat($"##value_{recipeKey}", ref value, 0, 0, "%.0f");

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
