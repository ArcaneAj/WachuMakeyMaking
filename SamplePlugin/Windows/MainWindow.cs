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
                    return;
                }

                // Create a local snapshot of the cache to avoid race conditions
                var cachedRecipes = recipeCacheService.CachedRecipes?.ToList() ?? new List<SamplePlugin.Models.ModRecipe>();

                ImGui.Text($"{cachedRecipes.Count} craftable recipes found");

                if (cachedRecipes.Count > 0)
                {
                    ImGuiHelpers.ScaledDummy(10.0f);
                    ImGui.Text("Craftable Recipes:");

                    using (ImRaii.PushIndent(20f))
                    {
                        foreach (var recipe in cachedRecipes.OrderBy(r => r.Item.Name.ToString()))
                        {
                            ImGui.Text($"{recipe.Item.Name}");
                        }
                    }
                }
            }
        }
    }

}
