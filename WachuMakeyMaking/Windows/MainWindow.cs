using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Services;

namespace WachuMakeyMaking.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly RecipeCacheService recipeCacheService;
    private readonly SolverService solverService;

    private static readonly bool CheckedDefault = false;

    // Track which recipes are selected (checked)
    private readonly Dictionary<string, bool> recipeSelections = [];

    // Track currency values (keyed by currency RowId)
    private readonly Dictionary<uint, float> currencyValues = [];

    // Track manual recipe value overrides (keyed by recipe key)
    // null means use calculated value, non-null means use this override
    private readonly Dictionary<string, int> recipeValueOverrides = [];

    private readonly Dictionary<uint, int> resourceQuantityOverrides = [];

    private Dictionary<uint, bool> resourceSelections = [];

    private readonly HashSet<ModItem> allIngredients = [];

    private ModItemStack[] allDisplayResources = null!;

    private Dictionary<ModItem, ModItemStack> inventoryDict = null!;

    // Solver state tracking
    private SolverService.State solverState = SolverService.State.Idle;
    private string solverProgressMessage = string.Empty;
    private Solution? currentSolution = null;
    private List<ModRecipeWithValue> currentRecipes = [];
    private List<ModRecipeWithValue> solverRecipes = [];
    private bool shouldSwitchToResultsTab = false;
    private bool shouldSwitchToRecipesTab = false;

    // Filter text the user can type to narrow candidates
    private string resourceAddFilter = string.Empty;

    public MainWindow(RecipeCacheService recipeCacheService, SolverService solverService)
        : base($"{Plugin.Name}?##{Plugin.Name}ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.recipeCacheService = recipeCacheService;
        this.solverService = solverService;

        // Register as a progress listener
        this.solverService.RegisterProgressListener(OnSolverProgressUpdate);

        // Subscribe to inventory changes
        Plugin.GameInventory.InventoryChanged += OnInventoryChanged;

        this.allIngredients = [.. recipeCacheService.FindRecipes().Values.SelectMany(x => x.Ingredients.Keys)];
        CraftimizerAvailable = Plugin.PluginInterface.GetIpcSubscriber<bool>("Craftimizer.IsAvailable");
        OpenMacroEditor = Plugin.PluginInterface.GetIpcSubscriber<ushort, bool>("Craftimizer.OpenMacroEditor");
    }

    public void Dispose()
    {
        // Unsubscribe from inventory changes
        Plugin.GameInventory.InventoryChanged -= OnInventoryChanged;
    }

    private static readonly GameInventoryType[] InventoriesToCheck = [
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
        GameInventoryType.Crystals,
    ];

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (events.Any(x => InventoriesToCheck.Contains(x.Item.ContainerType)) || events.Count == 0)
        {
            ResetResourceOverrides();
        }
    }

    private void ResetSolver()
    {
        this.solverService.Reset();
        this.solverState = SolverService.State.Idle;
        this.solverProgressMessage = string.Empty;
        this.currentSolution = null;
        this.currentRecipes.Clear();
    }

    private void ResetRecipeOverrides()
    {
        this.recipeValueOverrides.Clear();
        this.recipeSelections.Clear();
        ResetSolver();
    }

    private void ResetResourceOverrides()
    {
        var actualCrystals = RecipeCacheService.GetCrystals();
        var actualItems = RecipeCacheService.GetConsolidatedItems();
        this.allDisplayResources =
        [
            .. actualItems.Concat(actualCrystals).Where(x => this.allIngredients.Contains(x.Item)),
        ];
        this.inventoryDict = this.allDisplayResources.ToDictionary(x => x.Item, x => x);
        this.resourceQuantityOverrides.Clear();
        this.resourceSelections = this.allDisplayResources.ToDictionary(x => x.Id, x => true);
        ResetSolver();
        this.recipeCacheService.ForceRefresh(ApplyOverrides(this.allDisplayResources));
        ResetRecipeOverrides();
    }

    public override void Draw()
    {
        if (this.allDisplayResources == null || this.inventoryDict == null)
        {
            this.allDisplayResources = [];
            this.inventoryDict = [];
            OnInventoryChanged([]);
        }

        // Initialize cache if needed
        _ = this.recipeCacheService.EnsureCacheInitializedAsync();

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
                var recipesTabFlags = this.shouldSwitchToRecipesTab
                    ? ImGuiTabItemFlags.SetSelected
                    : ImGuiTabItemFlags.None;
                if (this.shouldSwitchToRecipesTab)
                {
                    this.shouldSwitchToRecipesTab = false;
                }
                using (var tab = ImRaii.TabItem("Recipes", recipesTabFlags))
                {
                    if (tab.Success)
                    {
                        DrawRecipesTab();
                    }
                }

                // Tab 3: Results
                var resultsTabFlags = this.shouldSwitchToResultsTab
                    ? ImGuiTabItemFlags.SetSelected
                    : ImGuiTabItemFlags.None;
                if (this.shouldSwitchToResultsTab)
                {
                    this.shouldSwitchToResultsTab = false;
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
        if (allDisplayResources == null)
            return;

        if (ImGui.Button("Reset"))
        {
            ResetResourceOverrides();
        }

        ImGui.SameLine();

        if (ImGui.Button("Submit"))
        {
            this.recipeCacheService.ForceRefresh(ApplyOverrides(this.allDisplayResources));
            ResetRecipeOverrides();
            this.shouldSwitchToRecipesTab = true;
        }

        ImGui.SameLine();

        var selectedItems = this.allDisplayResources.Count(r => this.resourceSelections.GetValueOrDefault(r.Id, false));
        ImGui.Text($"{this.allDisplayResources.Length} resources found with recipes ({selectedItems} selected)");

        ImGuiHelpers.ScaledDummy(10.0f);

        var presentItems = new HashSet<uint>(this.allDisplayResources?.Select(x => x.Id) ?? []);
        var candidates = this.allIngredients.Where(x => !presentItems.Contains(x.RowId)).OrderBy(x => x.Name).ToList();

        // Filter textbox for candidate list
        ImGui.Text("Add resource:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(250.0f);
        if (ImGui.InputText("##resource_filter", ref this.resourceAddFilter, 256)) { }

        // Apply the filter (case-insensitive) to the candidate list.
        var filteredCandidates = string.IsNullOrWhiteSpace(this.resourceAddFilter)
            ? candidates
            :
            [
                .. candidates.Where(x =>
                    x.Name.ToString().Contains(this.resourceAddFilter, StringComparison.OrdinalIgnoreCase)
                ),
            ];

        // Current display name for combo (from filtered list)
        var currentName = filteredCandidates.Count > 0 ? filteredCandidates[0].Name : "Select...";

        ImGui.SameLine();
        ImGui.SetNextItemWidth(250.0f);

        // Constrain the combo popup to max height 100px and a reasonable width.
        // Call before BeginCombo so it applies to the combo popup window.
        ImGui.SetNextWindowSizeConstraints(new Vector2(0, 0), new Vector2(250.0f, 300.0f));
        if (ImGui.BeginCombo("##add_resource_combo", currentName, ImGuiComboFlags.None))
        {
            for (var i = 0; i < filteredCandidates.Count; i++)
            {
                var name = filteredCandidates[i].Name;
                if (ImGui.Selectable(name, i == 0))
                {
                    // Immediately add the clicked item
                    var chosen = filteredCandidates[Math.Max(0, Math.Min(i, filteredCandidates.Count - 1))];

                    // Default to quantity 0 (user can edit after adding)
                    var list = new List<ModItemStack>(this.allDisplayResources ?? []) { new(chosen, chosen.RowId, 0) };
                    this.allDisplayResources = [.. list];

                    // Ensure selection and quantity state exists
                    this.resourceSelections[chosen.RowId] = true;
                    this.resourceQuantityOverrides[chosen.RowId] = 0;

                    // Update inventory lookup and refresh cache using existing override logic
                    this.inventoryDict = this.allDisplayResources.ToDictionary(x => x.Item, x => x);
                    this.recipeCacheService.ForceRefresh(ApplyOverrides(this.allDisplayResources));

                    // Reset filter and selected index so the combo shows the full list next time
                    this.resourceAddFilter = string.Empty;

                    // Close the combo popup after selection
                    ImGui.CloseCurrentPopup();
                }
                if (i == 0)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGuiHelpers.ScaledDummy(10.0f);

        // Prepare toggle state / counts used by header checkbox
        var totalResources = this.allDisplayResources!.Length;
        var selectedCount = this.allDisplayResources.Count(r => this.resourceSelections.GetValueOrDefault(r.Id, false));
        var allSelected = selectedCount == totalResources && totalResources > 0;
        var someSelected = selectedCount > 0 && selectedCount < totalResources;
        var noneSelected = selectedCount == 0;

        // Reserve the remaining content height for the child so the table can scroll independently.
        var avail = ImGui.GetContentRegionAvail();
        using (var child = ImRaii.Child("ResourcesTableChild", new Vector2(-1.0f, avail.Y), true))
        {
            if (!child.Success)
                return;

            var tableFlags =
                ImGuiTableFlags.RowBg
                | ImGuiTableFlags.BordersInnerV
                | ImGuiTableFlags.SizingFixedFit
                | ImGuiTableFlags.ScrollY;
            if (ImGui.BeginTable("ResourcesTable", 3, tableFlags))
            {
                // Column widths: fixed for quantity and checkbox, stretch for resource name
                ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 80.0f);
                ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, 22.0f);
                ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.WidthStretch);

                // Freeze the header row so it doesn't scroll with the body
                ImGui.TableSetupScrollFreeze(0, 1);

                // Header row (frozen)
                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                ImGui.TableSetColumnIndex(0);
                ImGui.Text("Quantity");
                ImGui.TableSetColumnIndex(1);
                var headerToggle = allSelected;
                if (ImGui.Checkbox("##toggleAllResources", ref headerToggle))
                {
                    // Preserve previous semantics: clicking when intermediate or empty selects all.
                    if (headerToggle)
                    {
                        foreach (var resource in allDisplayResources)
                            this.resourceSelections[resource.Id] = true;
                    }
                    else
                    {
                        foreach (var resource in allDisplayResources)
                            this.resourceSelections[resource.Id] = false;
                    }
                }

                // Draw intermediate indicator if needed (horizontal line inside the checkbox cell)
                if (someSelected)
                {
                    var checkboxPos = ImGui.GetItemRectMin();
                    var checkboxSize = ImGui.GetItemRectSize();
                    var drawList = ImGui.GetWindowDrawList();
                    var center = new Vector2(
                        checkboxPos.X + (checkboxSize.X * 0.5f),
                        checkboxPos.Y + (checkboxSize.Y * 0.5f)
                    );
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

                // Rows (table body will scroll; header row is frozen)
                foreach (var resourceItem in this.allDisplayResources.OrderBy(r => r.Item.Name.ToString()))
                {
                    var resourceId = resourceItem.Id;
                    var isSelected = this.resourceSelections.GetValueOrDefault(resourceId, true);
                    var quantity = GetResourceQuantity(resourceItem);

                    ImGui.TableNextRow();

                    // Quantity column
                    ImGui.TableSetColumnIndex(0);
                    ImGui.SetNextItemWidth(80.0f);
                    if (ImGui.InputInt($"##quantity_{resourceId}", ref quantity))
                    {
                        this.resourceQuantityOverrides[resourceId] = Math.Min(Math.Max(quantity, 0), 999999);
                    }

                    // Checkbox column
                    ImGui.TableSetColumnIndex(1);
                    if (ImGui.Checkbox($"##sel_{resourceId}", ref isSelected))
                    {
                        this.resourceSelections[resourceId] = isSelected;
                    }

                    // Resource column (icon + name)
                    ImGui.TableSetColumnIndex(2);
                    DrawIcon(resourceItem.Id);
                    var displayName = resourceItem.Item.Name;
                    if (this.inventoryDict.TryGetValue(resourceItem.Item, out var originalItemStack))
                    {
                        displayName += $" ({originalItemStack.Quantity} available)";
                    }
                    ImGui.Text(displayName);
                }

                ImGui.EndTable();
            }
        }
    }

    private static void DrawIcon(uint itemId, double value = -1)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var iconLookup = new GameIconLookup
        {
            IconId = itemSheet.GetRow(itemId).Icon,
            ItemHq = false,
            HiRes = false,
        };
        var iconTexture = Plugin.TextureProvider.GetFromGameIcon(iconLookup);
        var iconWrap = iconTexture.GetWrapOrEmpty();
        if (iconWrap != null)
        {
            var iconSize = new Vector2(20.0f * ImGui.GetIO().FontGlobalScale, 20.0f * ImGui.GetIO().FontGlobalScale);
            ImGui.Image(iconWrap.Handle, iconSize);

            if (value >= 0)
            {
                // Show value tooltip when the icon is hovered
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text($"Value: {Math.Floor(value)} gil");
                    ImGui.EndTooltip();
                }
            }

            ImGui.SameLine();
        }
    }
    private ICallGateSubscriber<ushort, bool> OpenMacroEditor { get; }
    public ICallGateSubscriber<bool> CraftimizerAvailable { get; }

    // Attempt to open the crafting log on the recipe for `itemId`.
    // Implementation details differ between client versions — this helper:
    // 1) finds the Recipe row for the given result item (if any)
    // 2) calls into the game's UI/agent to open the recipe UI (placeholder)
    // You must hook the exact agent/function from your FFXIVClientStructs version.
    // If you don't have client structs available, you can leave this as a no-op or log.
    private void OpenRecipeInCraftingLog(uint recipeId)
    {
        var canUseCraftimizer = CraftimizerAvailable.InvokeFunc();
        if (canUseCraftimizer)
        {
            var success = OpenMacroEditor.InvokeFunc((ushort)recipeId);
        }
        else
        {
            // Find a recipe whose result item matches this itemId
            var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();
            var recipe = recipeSheet.GetRow(recipeId);
            var matchingGearSets = EnumerateGearSets().Where(x => x.JobId == recipe.CraftType.RowId);
            if (!matchingGearSets.Any()) throw new Exception($"No gearset found for job {recipe.CraftType.RowId}");
            unsafe
            {
                RaptureGearsetModule.Instance()->EquipGearset(matchingGearSets.First().GearSetId);
                AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipe.RowId);
            }
        }

    }

    private static List<(int GearSetId, int JobId)> EnumerateGearSets()
    {
        var gearSetJobs = new List<(int GearSetId, int JobId)>();
        unsafe
        {
            var gearsetModule = RaptureGearsetModule.Instance();
            var i = -1;
            foreach (ref var gearset in gearsetModule->Entries)
            {
                i++;
                if (!gearset.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists) || gearset.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.MainHandMissing))
                    continue;
                gearSetJobs.Add((i, gearset.ClassJob - 8));
            }
        }

        return gearSetJobs;
    }

    private ModItemStack[] ApplyOverrides(ModItemStack[] allDisplayResources)
    {
        var updated = new List<ModItemStack>();
        foreach (var resourceItem in allDisplayResources)
        {
            var quantity = GetResourceQuantity(resourceItem);
            if (!this.resourceSelections.TryGetValue(resourceItem.Id, out var selected) || selected)
            {
                updated.Add(new ModItemStack(resourceItem.Item, resourceItem.Id, quantity));
            }
        }

        return [.. updated];
    }

    private void DrawRecipesTab()
    {
        if (this.recipeCacheService.IsCacheInitializing)
        {
            ImGui.Text("Loading recipes...");

            if (!string.IsNullOrEmpty(this.recipeCacheService.CurrentProcessingStep))
            {
                ImGui.Text(this.recipeCacheService.CurrentProcessingStep);
            }

            if (this.recipeCacheService.TotalProgress > 0)
            {
                var progress = (float)this.recipeCacheService.CurrentProgress / this.recipeCacheService.TotalProgress;
                ImGui.ProgressBar(progress, new Vector2(-1, 20));
            }

            return;
        }

        // Create a local snapshot of the cache to avoid race conditions
        var cachedRecipes = this.recipeCacheService.CachedRecipes?.ToList() ?? [];

        // Update recipe selections for any new recipes
        foreach (var recipe in cachedRecipes)
        {
            var recipeKey = recipe.Item.Name.ToString();
            if (!this.recipeSelections.ContainsKey(recipeKey))
            {
                this.recipeSelections[recipeKey] = CheckedDefault;
            }
        }

        // Remove selections and overrides for recipes that are no longer in cache
        var currentRecipeKeys = new HashSet<string>(cachedRecipes.Select(r => r.Item.Name.ToString()));
        var keysToRemove = this.recipeSelections.Keys.Where(key => !currentRecipeKeys.Contains(key)).ToList();
        foreach (var key in keysToRemove)
        {
            this.recipeSelections.Remove(key);
            this.recipeValueOverrides.Remove(key);
        }

        if (ImGui.Button("Reset"))
        {
            ResetRecipeOverrides();
        }

        ImGui.SameLine();

        var selectedRecipes = cachedRecipes
            .Where(r => this.recipeSelections.GetValueOrDefault(r.Item.Name.ToString(), false))
            .ToList();

        if (selectedRecipes.Count == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Solve"))
        {
            // We slight wiggle the costs in order to prefer one over the other to avoid degeneracy
            var recipes = selectedRecipes
                .Select((x, index) => x with { Value = GetRecipeValue(x) * 1.001 * (index + 1) })
                .ToList();
            this.solverRecipes = [.. selectedRecipes.Select((x, index) => x with { Value = GetRecipeValue(x) })];
            ;
            this.currentRecipes = recipes;
            // Switch to Results tab
            this.shouldSwitchToResultsTab = true;
            // Call the solver service
            Task.Run(() => this.solverService.Solve(recipes, ApplyOverrides(this.allDisplayResources)));
        }

        if (selectedRecipes.Count == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();

        ImGui.Text($"{cachedRecipes.Count} craftable recipes found ({selectedRecipes.Count} selected)");

        if (!string.IsNullOrEmpty(this.recipeCacheService.UniversalisMessage))
        {
            ImGui.TextColored(KnownColor.OrangeRed.Vector(), this.recipeCacheService.UniversalisMessage);
        }

        ImGuiHelpers.ScaledDummy(10.0f);
        if (cachedRecipes.Count > 0)
        {
            var currencyGrouping = cachedRecipes.GroupBy(x => x.Currency.RowId).Where(x => x.Key != 1);

            // Clean up currency values for currencies that are no longer in cache
            var currentCurrencyIds = new HashSet<uint>(currencyGrouping.Select(g => g.Key));
            var currencyIdsToRemove = this.currencyValues.Keys.Where(id => !currentCurrencyIds.Contains(id)).ToList();
            foreach (var id in currencyIdsToRemove)
            {
                this.currencyValues.Remove(id);
            }

            // Editable scrip value controls
            foreach (var currencyGroup in currencyGrouping)
            {
                var currencyId = currencyGroup.Key;
                var currency = currencyGroup.First().Currency;

                // Initialize currency value if not present
                if (!this.currencyValues.TryGetValue(currencyId, out var currencyValue))
                {
                    currencyValue = 1.0f;
                    this.currencyValues[currencyId] = currencyValue;
                }

                if (ImGui.InputFloat($"{currency.Name} gil value", ref currencyValue, 0, 0, "%.2f"))
                {
                    // Cap at 1000 to avoid prices exceeding 999999
                    this.currencyValues[currencyId] = Math.Min(currencyValue, 1000.0f);
                }
            }

            // Prepare toggle state / counts used by header checkbox
            var totalRecipes = cachedRecipes.Count;
            var selectedCount = cachedRecipes.Count(r =>
                this.recipeSelections.GetValueOrDefault(r.Item.Name.ToString(), false)
            );
            var allSelected = selectedCount == totalRecipes && totalRecipes > 0;
            var someSelected = selectedCount > 0 && selectedCount < totalRecipes;
            var noneSelected = selectedCount == 0;

            // Reserve the remaining content height so the table can scroll independently and freeze the header
            var avail = ImGui.GetContentRegionAvail();
            using (var child = ImRaii.Child("RecipesTableChild", new Vector2(-1.0f, avail.Y), true))
            {
                if (!child.Success)
                    return;

                var tableFlags =
                    ImGuiTableFlags.RowBg
                    | ImGuiTableFlags.BordersInnerV
                    | ImGuiTableFlags.SizingFixedFit
                    | ImGuiTableFlags.ScrollY;
                if (ImGui.BeginTable("RecipesTable", 3, tableFlags))
                {
                    // Column widths: fixed for value and checkbox, stretch for recipe name
                    ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 80.0f);
                    ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, 22.0f);
                    ImGui.TableSetupColumn("Recipe", ImGuiTableColumnFlags.WidthStretch);

                    // Freeze header row (columns, rows)
                    ImGui.TableSetupScrollFreeze(0, 1);

                    // Header row (frozen)
                    ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Value");

                    ImGui.TableSetColumnIndex(1);
                    var headerToggle = allSelected;
                    if (ImGui.Checkbox("##toggleAllRecipes", ref headerToggle))
                    {
                        // If clicking when intermediate or empty, select all
                        if (someSelected || noneSelected)
                        {
                            foreach (var recipe in cachedRecipes)
                            {
                                this.recipeSelections[recipe.Item.Name.ToString()] = true;
                            }
                        }
                        // If clicking when all selected, deselect all
                        else
                        {
                            foreach (var recipe in cachedRecipes)
                            {
                                this.recipeSelections[recipe.Item.Name.ToString()] = false;
                            }
                        }
                    }

                    // Draw intermediate indicator if needed (overlay a line to indicate partial selection)
                    if (someSelected)
                    {
                        var checkboxPos = ImGui.GetItemRectMin();
                        var checkboxSize = ImGui.GetItemRectSize();
                        var drawList = ImGui.GetWindowDrawList();
                        var center = new Vector2(
                            checkboxPos.X + (checkboxSize.X * 0.5f),
                            checkboxPos.Y + (checkboxSize.Y * 0.5f)
                        );
                        var lineLength = checkboxSize.X * 0.3f;
                        drawList.AddLine(
                            new Vector2(center.X - lineLength, center.Y),
                            new Vector2(center.X + lineLength, center.Y),
                            ImGui.GetColorU32(ImGuiCol.Text),
                            checkboxSize.Y * 0.6f
                        );
                    }

                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text("Recipe");

                    // Rows (table body will scroll; header is frozen)
                    foreach (var recipe in cachedRecipes.OrderBy(r => r.Item.Name.ToString()))
                    {
                        var recipeKey = recipe.Item.Name.ToString();
                        var isSelected = this.recipeSelections.GetValueOrDefault(recipeKey, false);

                        // Get the displayed value (use override if exists, otherwise calculated)
                        var value = GetRecipeValue(recipe);

                        ImGui.TableNextRow();

                        // Value column
                        ImGui.TableSetColumnIndex(0);
                        ImGui.SetNextItemWidth(80.0f);
                        if (ImGui.InputInt($"##value_{recipeKey}", ref value))
                        {
                            // Clamp to valid range (0-999999)
                            this.recipeValueOverrides[recipeKey] = Math.Min(Math.Max(value, 0), 999999);
                        }

                        // Checkbox column
                        ImGui.TableSetColumnIndex(1);
                        if (ImGui.Checkbox($"##{recipeKey}", ref isSelected))
                        {
                            this.recipeSelections[recipeKey] = isSelected;
                        }

                        // Recipe column (icon + name) — single click handler for entire cell using an InvisibleButton
                        ImGui.TableSetColumnIndex(2);

                        // Reserve full available width for the column before drawing
                        var fullWidth = ImGui.GetContentRegionAvail().X;
                        var iconHeight = 20.0f * ImGui.GetIO().FontGlobalScale;
                        var rowHeight = Math.Max(ImGui.GetFrameHeightWithSpacing(), iconHeight);

                        // Create the invisible button that covers the whole cell
                        ImGui.InvisibleButton($"cell_btn_recipe_{recipe.RowId}", new Vector2(fullWidth, rowHeight));
                        if (ImGui.IsItemClicked())
                        {
                            if (recipe.RowId > 0)
                            {
                                try
                                {
                                    OpenRecipeInCraftingLog(recipe.RowId);
                                }
                                catch (Exception ex)
                                {
                                    Plugin.Log.Error(
                                        $"Failed to open crafting log for recipe {recipe.RowId}: {ex.Message}"
                                    );
                                }
                            }
                        }

                        var btnMin = ImGui.GetItemRectMin();
                        var padX = 4.0f;
                        var iconY = btnMin.Y + ((rowHeight - iconHeight) * 0.5f);

                        ImGui.SetCursorScreenPos(new Vector2(btnMin.X + padX, iconY));
                        DrawIcon(recipe.Item.RowId);
                        ImGui.Text($"{recipe.Item.Name}");

                        // Move cursor to the right edge of the invisible button so subsequent columns render correctly
                        ImGui.SetCursorScreenPos(new Vector2(btnMin.X + fullWidth, btnMin.Y));
                    }

                    ImGui.EndTable();
                }
            }
        }
    }

    private void DrawResultsTab()
    {
        if (this.solverState == SolverService.State.Idle)
        {
            ImGui.Text("No solution computed yet. Go to the Recipes tab and click 'Solve' to start.");
            return;
        }

        // Display current state
        ImGui.Text($"Status: {this.solverProgressMessage}");

        if (this.solverState == SolverService.State.FindingInitialSolution)
        {
            ImGui.Text("Finding initial solution...");
        }
        else if (this.solverState == SolverService.State.Optimising)
        {
            ImGui.Text("Optimising...");
            if (currentSolution != null)
            {
                ImGui.Text($"Current best value: {Math.Floor(this.currentSolution.OptimalValue)} gil");
            }
        }
        else if (this.solverState == SolverService.State.Finished && this.currentSolution != null)
        {
            ImGuiHelpers.ScaledDummy(10.0f);
            ImGui.Text("Finished");
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(5.0f);

            // Reserve the remaining content height so the table can scroll independently and freeze the header
            var avail = ImGui.GetContentRegionAvail();
            using (var innerChild = ImRaii.Child("ResultsSolutionChild", new Vector2(-1.0f, avail.Y), true))
            {
                if (!innerChild.Success)
                    return;
                var tableFlags =
                    ImGuiTableFlags.RowBg
                    | ImGuiTableFlags.BordersInnerV
                    | ImGuiTableFlags.SizingFixedFit
                    | ImGuiTableFlags.ScrollY;
                // Display solution as a table with a single click handler for the whole cell
                if (ImGui.BeginTable("SolutionTable", 4, tableFlags))
                {
                    ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 100.0f);
                    ImGui.TableSetupColumn("Per Unit", ImGuiTableColumnFlags.WidthFixed, 100.0f);
                    ImGui.TableSetupColumn("Contribution", ImGuiTableColumnFlags.WidthFixed, 100.0f);
                    ImGui.TableHeadersRow();

                    // Undo the wiggling applied before solving to get original values
                    for (var i = 0; i < this.solverRecipes.Count && i < this.currentSolution.Values.Count; i++)
                    {
                        var quantity = (int)Math.Round(this.currentSolution.Values[i]);
                        if (quantity > 0)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);

                            // Reserve full available width for the column before drawing
                            var fullWidth = ImGui.GetContentRegionAvail().X;
                            var iconHeight = 20.0f * ImGui.GetIO().FontGlobalScale;
                            var rowHeight = Math.Max(ImGui.GetFrameHeightWithSpacing(), iconHeight);

                            ImGui.InvisibleButton(
                                $"cell_btn_result_{this.solverRecipes[i].RowId}",
                                new Vector2(fullWidth, rowHeight)
                            );
                            if (ImGui.IsItemClicked())
                            {
                                try
                                {
                                    OpenRecipeInCraftingLog(this.solverRecipes[i].RowId);
                                }
                                catch (Exception ex)
                                {
                                    Plugin.Log.Error(
                                        $"Failed to open crafting log for recipe {this.solverRecipes[i].RowId}: {ex.Message}"
                                    );
                                }
                            }

                            var btnMin = ImGui.GetItemRectMin();
                            var padX = 4.0f;
                            var iconY = btnMin.Y + ((rowHeight - iconHeight) * 0.5f);

                            ImGui.SetCursorScreenPos(new Vector2(btnMin.X + padX, iconY));
                            DrawIcon(this.solverRecipes[i].Item.RowId, this.solverRecipes[i].Value);
                            ImGui.Text(this.solverRecipes[i].Item.Name);

                            ImGui.SetCursorScreenPos(new Vector2(btnMin.X + fullWidth, btnMin.Y));

                            ImGui.TableSetColumnIndex(1);
                            ImGui.Text((this.solverRecipes[i].Number * quantity).ToString());
                            ImGui.TableSetColumnIndex(2);
                            ImGui.Text($"{(int)this.solverRecipes[i].Value}");
                            ImGui.TableSetColumnIndex(3);
                            ImGui.Text($"{(int)this.solverRecipes[i].Value * this.solverRecipes[i].Number * quantity}");
                        }
                    }

                    ImGui.EndTable();
                }
            }
        }
        else if (this.solverState == SolverService.State.Error || this.solverState == SolverService.State.Unbounded)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), $"Error: {this.solverProgressMessage}");
        }
    }

    private int GetResourceQuantity(ModItemStack resourceItem)
    {
        if (this.resourceQuantityOverrides.TryGetValue(resourceItem.Id, out var quantity))
            return quantity;
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
        if (recipeValueOverrides.TryGetValue(recipeKey, out var overrideValue))
            return Math.Min(overrideValue, 999999);
        return calculatedValue;
    }

    private void OnSolverProgressUpdate(SolverService.State state, string message, Solution? solution)
    {
        this.solverState = state;
        this.solverProgressMessage = message;
        this.currentSolution = solution;
    }
}
