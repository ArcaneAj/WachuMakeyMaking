using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Interface.Textures;
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
namespace WachuMakeyMaking.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly RecipeCacheService recipeCacheService;
    private readonly SolverService solverService;

    private static readonly bool CheckedDefault = false;

    // Track which recipes are selected (checked)
    private Dictionary<string, bool> recipeSelections = [];

    // Track currency values (keyed by currency RowId)
    private Dictionary<uint, float> currencyValues = [];

    // Track manual recipe value overrides (keyed by recipe key)
    // null means use calculated value, non-null means use this override
    private Dictionary<string, int> recipeValueOverrides = [];

    private Dictionary<uint, int> resourceQuantityOverrides = [];

    private Dictionary<uint, bool> resourceSelections = [];

    private readonly HashSet<ModItem> allIngredients = [];

    private ModItemStack[] allDisplayResources = null!;

    private Dictionary<ModItem, ModItemStack> inventoryDict = null!;

    // Solver state tracking
    private SolverService.State solverState = SolverService.State.Idle;
    private string solverProgressMessage = string.Empty;
    private Solution? currentSolution = null;
    private List<ModRecipeWithValue> currentRecipes = [];
    private bool shouldSwitchToResultsTab = false;

    public MainWindow(Plugin plugin, RecipeCacheService recipeCacheService, SolverService solverService)
        : base($"{Plugin.Name}##{Plugin.Name}ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        this.recipeCacheService = recipeCacheService;
        this.solverService = solverService;

        // Register as a progress listener
        this.solverService.RegisterProgressListener(OnSolverProgressUpdate);

        // Subscribe to inventory changes
        Plugin.GameInventory.InventoryChanged += OnInventoryChanged;

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
        // Unsubscribe from inventory changes
        Plugin.GameInventory.InventoryChanged -= OnInventoryChanged;
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        // Get actual inventory quantities before overrides
        var actualCrystals = recipeCacheService.GetCrystals();
        var actualItems = recipeCacheService.GetConsolidatedItems();
        // Combine cached and manual resources for display
        allDisplayResources = [.. actualItems.Concat(actualCrystals).Where(x => allIngredients.Contains(x.Item))];
        inventoryDict = allDisplayResources.ToDictionary(x => x.Item, x => x);
        recipeCacheService.ForceRefresh(ApplyOverrides(allDisplayResources));
    }

    private void ResetSolver()
    {
        solverService.Reset();
        solverState = SolverService.State.Idle;
        solverProgressMessage = string.Empty;
        currentSolution = null;
        currentRecipes.Clear();
    }

    private void ResetRecipeOverrides()
    {
        recipeValueOverrides.Clear();
        recipeSelections.Clear();
        ResetSolver();
    }

    private void ResetResourceOverrides()
    {
        resourceQuantityOverrides.Clear();
        resourceSelections.Clear();
        ResetSolver();
        recipeCacheService.ForceRefresh(ApplyOverrides(allDisplayResources));
        ResetRecipeOverrides();
    }

    public override void Draw()
    {
        if (allDisplayResources == null || inventoryDict == null) {
            allDisplayResources = [];
            inventoryDict = [];
            OnInventoryChanged([]);
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
                var resultsTabFlags = shouldSwitchToResultsTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                if (shouldSwitchToResultsTab)
                {
                    shouldSwitchToResultsTab = false;
                }
                using (var tab = ImRaii.TabItem("Results", resultsTabFlags))
                {
                    if (tab.Success)
                    {
                        DrawResultsTab();
                    }
                }
            }
        }
    }

    private void DrawResourcesTab()
    {
        if (ImGui.Button("Reset"))
        {
            ResetResourceOverrides();
        }

        ImGui.SameLine();

        if (ImGui.Button("Submit"))
        {
            recipeCacheService.ForceRefresh(ApplyOverrides(allDisplayResources));
            ResetRecipeOverrides();
        }

        ImGui.SameLine();

        var selectedItems = allDisplayResources.Where(r => resourceSelections.GetValueOrDefault(r.Id, false)).ToList();
        ImGui.Text($"{allDisplayResources.Length} resources found with recipes ({selectedItems.Count} selected)");

        ImGuiHelpers.ScaledDummy(10.0f);

        // Prepare toggle state / counts used by header checkbox
        var totalResources = allDisplayResources.Length;
        var selectedCount = allDisplayResources.Count(r => resourceSelections.GetValueOrDefault(r.Id, false));
        bool allSelected = selectedCount == totalResources && totalResources > 0;
        bool someSelected = selectedCount > 0 && selectedCount < totalResources;
        bool noneSelected = selectedCount == 0;

        // Wrap table in a child to keep scrollbars (preserves original scrolling behaviour)
        using (var child = ImRaii.Child("ResourcesTableChild", Vector2.Zero, true))
        {
            if (!child.Success) return;

            var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY;
            if (ImGui.BeginTable("ResourcesTable", 3, tableFlags))
            {
                // Column widths: fixed for quantity and checkbox, stretch for resource name
                ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 80.0f);
                ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, 36.0f);
                ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.WidthStretch);

                // Custom header row so we can put the toggle-all checkbox into the middle column
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text("Quantity");

                ImGui.TableSetColumnIndex(1);
                var headerToggle = allSelected;
                if (ImGui.Checkbox("##toggleAllResources", ref headerToggle))
                {
                    // Clicking when intermediate or empty selects all; clicking when all deselects all
                    if (someSelected || noneSelected)
                    {
                        foreach (var resource in allDisplayResources)
                            resourceSelections[resource.Id] = true;
                    }
                    else
                    {
                        foreach (var resource in allDisplayResources)
                            resourceSelections[resource.Id] = false;
                    }
                }

                // Draw intermediate indicator if needed (horizontal line inside the checkbox cell)
                if (someSelected)
                {
                    var checkboxPos = ImGui.GetItemRectMin();
                    var checkboxSize = ImGui.GetItemRectSize();
                    var drawList = ImGui.GetWindowDrawList();
                    var center = new Vector2(checkboxPos.X + checkboxSize.X * 0.5f, checkboxPos.Y + checkboxSize.Y * 0.5f);
                    var lineLength = checkboxSize.X * 0.3f;
                    drawList.AddLine(
                        new Vector2(center.X - lineLength, center.Y),
                        new Vector2(center.X + lineLength, center.Y),
                        ImGui.GetColorU32(ImGuiCol.Text),
                        checkboxSize.Y * 0.6f
                    );
                }

                ImGui.TableSetColumnIndex(2);
                ImGui.Text("Resource");

                // Rows
                foreach (var resourceItem in allDisplayResources.OrderBy(r => r.Item.Name.ToString()))
                {
                    var resourceId = resourceItem.Id;
                    var isSelected = resourceSelections.GetValueOrDefault(resourceId, true);
                    var quantity = GetResourceQuantity(resourceItem);

                    ImGui.TableNextRow();

                    // Quantity column
                    ImGui.TableSetColumnIndex(0);
                    ImGui.SetNextItemWidth(55.0f);
                    if (ImGui.InputInt($"##quantity_{resourceId}", ref quantity))
                    {
                        resourceQuantityOverrides[resourceId] = Math.Min(Math.Max(quantity, 0), 999999);
                    }

                    // Checkbox column
                    ImGui.TableSetColumnIndex(1);
                    if (ImGui.Checkbox($"##sel_{resourceId}", ref isSelected))
                    {
                        resourceSelections[resourceId] = isSelected;
                    }

                    // Resource column (icon + name)
                    ImGui.TableSetColumnIndex(2);
                    DrawnIcon(resourceItem.Id);
                    var displayName = resourceItem.Item.Name;
                    if (inventoryDict.TryGetValue(resourceItem.Item, out var originalItemStack))
                    {
                        displayName += $" ({originalItemStack.Quantity} available)";
                    }
                    ImGui.Text(displayName);
                }

                ImGui.EndTable();
            }
        }
    }

    private static void DrawnIcon(uint itemId)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var iconLookup = new GameIconLookup
        {
            IconId = itemSheet.GetRow(itemId).Icon,
            ItemHq = false,
            HiRes = false
        };
        var iconTexture = Plugin.TextureProvider.GetFromGameIcon(iconLookup);
        var iconWrap = iconTexture.GetWrapOrEmpty();
        if (iconWrap != null)
        {
            var iconSize = new Vector2(20.0f * ImGui.GetIO().FontGlobalScale, 20.0f * ImGui.GetIO().FontGlobalScale);
            ImGui.Image(iconWrap.Handle, iconSize);
            ImGui.SameLine();
        }
    }

    private ModItemStack[] ApplyOverrides(ModItemStack[] allDisplayResources)
    {
        var updated = new List<ModItemStack>();
        foreach (var resourceItem in allDisplayResources)
        {
            var quantity = GetResourceQuantity(resourceItem);
            if (!resourceSelections.TryGetValue(resourceItem.Id, out var selected) || selected)
            {
                updated.Add(new ModItemStack(resourceItem.Item, resourceItem.Id, quantity));
            }
        }

        return [.. updated];
    }

    private void DrawRecipesTab()
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
        }

        // Remove selections and overrides for recipes that are no longer in cache
        var currentRecipeKeys = new HashSet<string>(cachedRecipes.Select(r => r.Item.Name.ToString()));
        var keysToRemove = recipeSelections.Keys.Where(key => !currentRecipeKeys.Contains(key)).ToList();
        foreach (var key in keysToRemove)
        {
            recipeSelections.Remove(key);
            recipeValueOverrides.Remove(key);
        }

        if (ImGui.Button("Reset"))
        {
            ResetRecipeOverrides();
        }

        ImGui.SameLine();

        var selectedRecipes = cachedRecipes.Where(r => recipeSelections.GetValueOrDefault(r.Item.Name.ToString(), false)).ToList();
        
        if (selectedRecipes.Count == 0)
        {
            ImGui.BeginDisabled();
        }
        
        if (ImGui.Button("Solve"))
        {
            // We slight wiggle the costs in order to prefer one over the other to avoid degeneracy
            var recipes = selectedRecipes.Select((ModRecipeWithValue x, int index) => x with { Value = GetRecipeValue(x) + 0.001 * index }).ToList();
            currentRecipes = recipes;
            // Switch to Results tab
            shouldSwitchToResultsTab = true;
            // Call the solver service
            Task.Run(() => solverService.Solve(
                recipes,
                ApplyOverrides(allDisplayResources)
                ));
        }
        
        if (selectedRecipes.Count == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();

        ImGui.Text($"{cachedRecipes.Count} craftable recipes found ({selectedRecipes.Count} selected)");

        ImGuiHelpers.ScaledDummy(10.0f);
        if (cachedRecipes.Count > 0)
        {
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

            // Column headers
            ImGuiHelpers.ScaledDummy(10.0f);
            ImGui.Text("Value");
            ImGui.SameLine(85.0f); // Position after the fixed-width textbox
            
            // Toggle all checkbox
            var totalRecipes = cachedRecipes.Count;
            var selectedCount = cachedRecipes.Count(r => recipeSelections.GetValueOrDefault(r.Item.Name.ToString(), false));
            bool allSelected = selectedCount == totalRecipes && totalRecipes > 0;
            bool someSelected = selectedCount > 0 && selectedCount < totalRecipes;
            bool noneSelected = selectedCount == 0;
            
            // For intermediate state, show as empty (unchecked) with a line overlay
            // For empty state, show as unchecked
            // For full state, show as checked
            bool toggleState = allSelected; // Only true when all are selected, false otherwise (including intermediate)
            
            // Store the state before the checkbox potentially changes it
            bool wasAllSelected = allSelected;
            bool wasSomeSelected = someSelected;
            
            if (ImGui.Checkbox("##toggleAll", ref toggleState))
            {
                // If clicking when intermediate or empty, select all
                if (wasSomeSelected || noneSelected)
                {
                    foreach (var recipe in cachedRecipes)
                    {
                        recipeSelections[recipe.Item.Name.ToString()] = true;
                    }
                }
                // If clicking when all selected, deselect all
                else if (wasAllSelected)
                {
                    foreach (var recipe in cachedRecipes)
                    {
                        recipeSelections[recipe.Item.Name.ToString()] = false;
                    }
                }
            }
            
            // Draw intermediate indicator if needed (overlay a line to indicate partial selection)
            if (wasSomeSelected)
            {
                var checkboxPos = ImGui.GetItemRectMin();
                var checkboxSize = ImGui.GetItemRectSize();
                var drawList = ImGui.GetWindowDrawList();
                // Draw a horizontal line through the checkbox to indicate intermediate state
                var center = new Vector2(checkboxPos.X + checkboxSize.X * 0.5f, checkboxPos.Y + checkboxSize.Y * 0.5f);
                var lineLength = checkboxSize.X * 0.3f;
                drawList.AddLine(
                    new Vector2(center.X - lineLength, center.Y),
                    new Vector2(center.X + lineLength, center.Y),
                    ImGui.GetColorU32(ImGuiCol.Text),
                    checkboxSize.Y * 0.6f
                );
            }
            
            ImGui.SameLine(110.0f); // Position after the checkbox
            ImGui.Text("Recipe");
        }

        // Normally a BeginChild() would have to be followed by an unconditional EndChild(),
        // ImRaii takes care of this after the scope ends.
        // This works for all ImGui functions that require specific handling, examples are BeginTable() or Indent().
        using (var child = ImRaii.Child("SomeChildWithAScrollbar", Vector2.Zero, true))
        {
            // Check if this child is drawing
            if (child.Success)
            {
                if (cachedRecipes.Count > 0)
                {
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

                        // Item icon
                        DrawnIcon(recipe.Item.RowId);

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

    private void OnSolverProgressUpdate(SolverService.State state, string message, Solution? solution)
    {
        this.solverState = state;
        this.solverProgressMessage = message;
        this.currentSolution = solution;
    }

    private void DrawResultsTab()
    {
        using (var child = ImRaii.Child("ResultsChildWithAScrollbar", Vector2.Zero, true))
        {
            if (child.Success)
            {
                if (solverState == SolverService.State.Idle)
                {
                    ImGui.Text("No solution computed yet. Go to the Recipes tab and click 'Solve' to start.");
                    return;
                }

                // Display current state
                ImGui.Text($"Status: {solverProgressMessage}");

                if (solverState == SolverService.State.FindingInitialSolution)
                {
                    ImGui.Text("Finding initial solution...");
                }
                else if (solverState == SolverService.State.Optimising)
                {
                    ImGui.Text("Optimising...");
                    if (currentSolution != null)
                    {
                        ImGui.Text($"Current best value: {Math.Floor(currentSolution.OptimalValue)} gil");
                    }
                }
                else if (solverState == SolverService.State.Finished && currentSolution != null)
                {
                    ImGuiHelpers.ScaledDummy(10.0f);
                    ImGui.Text("Finished");
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(5.0f);

                    // Display solution as a table
                    if (ImGui.BeginTable("SolutionTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                    {
                        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 100.0f);
                        ImGui.TableHeadersRow();

                        for (int i = 0; i < currentRecipes.Count && i < currentSolution.Values.Count; i++)
                        {
                            var quantity = (int)Math.Round(currentSolution.Values[i]);
                            if (quantity > 0)
                            {
                                ImGui.TableNextRow();
                                ImGui.TableSetColumnIndex(0);
                                DrawnIcon(currentRecipes[i].Item.RowId);
                                ImGui.Text(currentRecipes[i].Item.Name);
                                ImGui.TableSetColumnIndex(1);
                                ImGui.Text(quantity.ToString());
                            }
                        }

                        ImGui.EndTable();
                    }

                    ImGuiHelpers.ScaledDummy(10.0f);
                    ImGui.Separator();
                    ImGui.Text($"Total value: {Math.Floor(currentSolution.OptimalValue)} gil");
                }
                else if (solverState == SolverService.State.Error || solverState == SolverService.State.Unbounded)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), $"Error: {solverProgressMessage}");
                }
            }
        }
    }

}
