using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Services;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Windows;

public sealed partial class MainWindow : Window, IDisposable
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
    private Dictionary<string, Dictionary<ModNotebookDivision, bool>> divisionTags;
    private Dictionary<ModItem, HashSet<uint>> ingredientDivisions;
    private HashSet<ModItem> ingredientsUsable;
    private HashSet<ModItem> ingredientsEquippable;
    private const int MAX_LEVEL = 100;
    private const int TAG_COLS = 4;
    private const float TAG_COL_WIDTH = 200f;
    private bool onlyEquippable = false;
    private bool onlyUnequippable = false;
    private bool onlyCrafted = false;
    private bool onlyRaw = false;

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

        var recipes = this.recipeCacheService.FindRecipes().Values;

        this.divisionTags = SetupDivisions(recipes);
        this.ingredientDivisions = SetupIngredientDivisions(recipes);
        this.ingredientsUsable = SetupUsability(recipes);
        this.ingredientsEquippable = SetupEquippability(recipes);

        this.allIngredients = [.. recipes.SelectMany(x => x.Ingredients.Keys)];
    }

    private HashSet<ModItem> SetupEquippability(IEnumerable<ModRecipe> recipes)
    {
        var ingredientsEquippable = new HashSet<ModItem>();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();

        // Add ingredients from recipes whose result is gear
        var gearRecipes = recipes.Where(r =>
        {
            var row = itemSheet.GetRow(r.Item.RowId);
            return row.FilterGroup <= 4; // treat <=4 as gear
        }).ToList();

        foreach (var recipe in gearRecipes)
        {
            foreach (var ingredient in recipe.Ingredients.Keys)
            {
                ingredientsEquippable.Add(ingredient);
            }
        }

        // Recursively include ingredients of those ingredients regardless of their FilterGroup
        var nestedIngredientsToCheck = ingredientsEquippable
            .Select(this.recipeCacheService.FindRecipeByResultItem)
            .OfType<ModRecipe>()
            .ToList();

        if (nestedIngredientsToCheck.Count > 0)
        {
            ingredientsEquippable.UnionWith(SetupEquippabilityRecursive(nestedIngredientsToCheck));
        }

        return ingredientsEquippable;
    }

    private HashSet<ModItem> SetupEquippabilityRecursive(IEnumerable<ModRecipe> recipes)
    {
        var ingredients = new HashSet<ModItem>();
        foreach (var recipe in recipes)
        {
            foreach (var ingredient in recipe.Ingredients.Keys)
            {
                ingredients.Add(ingredient);
            }
        }

        var nested = ingredients.Select(this.recipeCacheService.FindRecipeByResultItem).OfType<ModRecipe>().ToList();
        if (nested.Count > 0)
        {
            ingredients.UnionWith(SetupEquippabilityRecursive(nested));
        }

        return ingredients;
    }

    private HashSet<ModItem> SetupUsability(IEnumerable<ModRecipe> recipes)
    {
        var ingredientsUsable = new HashSet<ModItem>();
        foreach (var recipe in recipes.Where(RecipeCacheService.HasRequirementsForRecipe))
        {
            foreach (var ingredient in recipe.Ingredients.Keys)
            {
                ingredientsUsable.Add(ingredient);
            }
        }

        var nestedIngredientsToCheck = ingredientsUsable.Select(this.recipeCacheService.FindRecipeByResultItem).OfType<ModRecipe>().ToList();

        if (nestedIngredientsToCheck.Count > 0)
        {
            ingredientsUsable.UnionWith(SetupUsability(nestedIngredientsToCheck));
        }

        return ingredientsUsable;
    }

    private Dictionary<ModItem, HashSet<uint>> SetupIngredientDivisions(IEnumerable<ModRecipe> recipes)
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

        var nestedIngredientsToCheck = ingredientDivisions.Keys.Select(this.recipeCacheService.FindRecipeByResultItem).OfType<ModRecipe>().ToList();


        if (nestedIngredientsToCheck.Count > 0)
        {
            ingredientDivisions.MergeUnion(SetupIngredientDivisions(nestedIngredientsToCheck));
        }

        return ingredientDivisions;
    }

    private Dictionary<string, Dictionary<ModNotebookDivision, bool>> SetupDivisions(IEnumerable<ModRecipe> recipes)
    {
        var divisionCategorySheet = Plugin.DataManager.GetExcelSheet<NotebookDivisionCategory>();
        var divisionSheet = Plugin.DataManager.GetExcelSheet<NotebookDivision>();
        var levellingDivisions = divisionSheet.Where(
            x => x.NotebookDivisionCategory.RowId == 0 &&
            x.Name.ToString().Length > 0 &&
            char.IsDigit(x.Name.ToString()[0]) &&
            TryParseInt(x.Name.ToString(), MAX_LEVEL) < MAX_LEVEL)
            .Select(x => new ModNotebookDivision(x));
        var masterworkDivisions = divisionSheet.Where(x => x.NotebookDivisionCategory.RowId == 1)
            .Select(x => new ModNotebookDivision(x))
            .OrderBy(x => IsInteger().Split(x.Name.Replace("(", "").Replace(")", "")).Select(chunk => new ChunkWrapper(chunk)), new ChunkComparer());
        var housingDivisions = divisionSheet.Where(x => x.NotebookDivisionCategory.RowId == 2)
            .Select(x => new ModNotebookDivision(x));

        return new Dictionary<string, Dictionary<ModNotebookDivision, bool>>()
        {
            ["Standard"] = levellingDivisions.ToDictionary(x => x, x => true),
            [divisionCategorySheet.GetRow(1).Name.ToString()] = masterworkDivisions.ToDictionary(x => x, x => true),
            [divisionCategorySheet.GetRow(2).Name.ToString()] = housingDivisions.ToDictionary(x => x, x => true),
            ["Other"] = new()
            {
                [new ModNotebookDivision(null, "Other")] = true,
            },
        };
    }

    public void Dispose()
    {
        // Unsubscribe from inventory changes
        Plugin.GameInventory.InventoryChanged -= OnInventoryChanged;
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (events.Any(e => e.Type == GameInventoryEvent.Added || e.Type == GameInventoryEvent.Removed || e.Type == GameInventoryEvent.Changed))
        {
            MergeInventoryChanges();
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
        Plugin.Log.Info("Inventory changed, resetting resource overrides");
        var actualItems = this.recipeCacheService.GetConsolidatedItems();
        this.allDisplayResources =
        [
            .. actualItems.Where(x => this.allIngredients.Contains(x.Item)),
        ];
        this.inventoryDict = this.allDisplayResources.ToDictionary(x => x.Item, x => x);
        this.resourceQuantityOverrides.Clear();
        this.resourceSelections = this.allDisplayResources.ToDictionary(x => x.Id, x => true);
        ResetSolver();
        this.recipeCacheService.ForceRefresh(ApplyOverrides(this.allDisplayResources));
        ResetRecipeOverrides();
    }

    private void MergeInventoryChanges()
    {
        if (this.allDisplayResources == null || this.inventoryDict == null)
        {
            ResetResourceOverrides();
            return;
        }

        var actualItems = this.recipeCacheService.GetConsolidatedItems()
            .Where(x => this.allIngredients.Contains(x.Item))
            .ToDictionary(x => x.Id, x => x);

        var merged = new List<ModItemStack>();
        foreach (var existing in this.allDisplayResources)
        {
            if (actualItems.TryGetValue(existing.Id, out var liveItem))
            {
                merged.Add(new ModItemStack(existing.Item, existing.Id, liveItem.Quantity));
                actualItems.Remove(existing.Id);
            }
            else if (this.resourceQuantityOverrides.ContainsKey(existing.Id) || !this.resourceSelections.GetValueOrDefault(existing.Id, true))
            {
                // User has customized this row (manual quantity/added it, or explicitly unchecked it) - keep it
                // even though it's no longer present in live inventory.
                merged.Add(existing);
            }
            else
            {
                // Untouched row that genuinely left inventory - drop it, same as a full reset would.
                this.resourceSelections.Remove(existing.Id);
            }
        }

        // Anything left in actualItems is a newly-discovered ingredient that wasn't displayed before.
        foreach (var newItem in actualItems.Values)
        {
            merged.Add(newItem);
            this.resourceSelections[newItem.Id] = true;
        }

        var mergedArray = merged.ToArray();

        // Refresh the "(N available)" annotation regardless of whether anything else changed.
        this.inventoryDict = mergedArray.ToDictionary(x => x.Item, x => x);

        if (AppliedResourcesEqual(ApplyOverrides(this.allDisplayResources), ApplyOverrides(mergedArray)))
        {
            return;
        }

        this.allDisplayResources = mergedArray;
        this.recipeCacheService.ForceRefresh(ApplyOverrides(this.allDisplayResources));
    }

    private static bool AppliedResourcesEqual(ModItemStack[] before, ModItemStack[] after)
    {
        if (before.Length != after.Length)
            return false;

        var beforeById = before.ToDictionary(x => x.Id, x => x.Quantity);
        foreach (var item in after)
        {
            if (!beforeById.TryGetValue(item.Id, out var quantity) || quantity != item.Quantity)
                return false;
        }

        return true;
    }

    public override void Draw()
    {
        if (this.allDisplayResources == null || this.inventoryDict == null)
        {
            this.allDisplayResources = [];
            this.inventoryDict = [];
            ResetResourceOverrides();
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
        List<ModItem> filteredCandidates;
        ImGuiHelpers.ScaledDummy(5.0f);

        if (ImGui.CollapsingHeader("Resource usage filters"))
        {
            foreach (var divisionCategory in this.divisionTags)
            {
                var categoryName = divisionCategory.Key;
                var divisions = divisionCategory.Value;

                // Determine tri-state: all checked, none checked, or indeterminate (some checked)
                var allChecked = divisions.All(x => x.Value);
                var anyChecked = divisions.Any(x => x.Value);

                // Use the "allChecked" value for the visible checkbox state. If the category is
                // indeterminate (some but not all children selected) we'll draw a small "-" overlay
                // to indicate the mixed state.
                var headerChecked = allChecked;
                if (ImGui.Checkbox($"##_RUF_{categoryName}", ref headerChecked))
                {
                    // Toggle all children to the new header state
                    foreach (var tag in this.divisionTags[categoryName])
                    {
                        this.divisionTags[categoryName][tag.Key] = headerChecked;
                    }
                }

                // If some children are selected but not all, render an indeterminate mark over the checkbox
                if (anyChecked && !allChecked)
                {
                    var drawList = ImGui.GetWindowDrawList();
                    var itemMin = ImGui.GetItemRectMin();
                    var itemMax = ImGui.GetItemRectMax();
                    var style = ImGui.GetStyle();

                    // Position the small horizontal bar inside the checkbox square. FramePadding.X is
                    // used to approximate the left edge of the checkbox box inside the item rectangle.
                    var barLeft = new Vector2(itemMin.X + style.FramePadding.X, (itemMin.Y + itemMax.Y) / 2f - 1.0f);
                    var barRight = new Vector2(itemMin.X + style.FramePadding.X + 15.0f, (itemMin.Y + itemMax.Y) / 2f + 1.0f);
                    var col = ImGui.GetColorU32(ImGuiCol.Text);
                    drawList.AddRectFilled(barLeft, barRight, col, 1.0f);
                }

                ImGui.SameLine();
                if (ImGui.CollapsingHeader(categoryName))
                {
                    var baseX = 0f;
                    var manualInsertions = 0;
                    if (categoryName == "Other")
                    {
                        baseX = ImGui.GetCursorPosX() + 25f;

                        // Add the two manual checkboxes for "Only Equippable" and "Only Unequippable"
                        manualInsertions = DefineManualCheckbox(categoryName, baseX, manualInsertions, "Only Equippable", ref this.onlyEquippable, ref this.onlyUnequippable);

                        manualInsertions = DefineManualCheckbox(categoryName, baseX, manualInsertions, "Only Unequippable", ref this.onlyUnequippable, ref this.onlyEquippable);

                        // Add the two manual checkboxes for "Only Crafted" and "Only Raw"
                        manualInsertions = DefineManualCheckbox(categoryName, baseX, manualInsertions, "Only Crafted", ref this.onlyCrafted, ref this.onlyRaw);

                        manualInsertions = DefineManualCheckbox(categoryName, baseX, manualInsertions, "Only Raw", ref this.onlyRaw, ref this.onlyCrafted);
                    }

                    for (var i = manualInsertions; i < divisions.Count + manualInsertions; i++)
                    {
                        if (i == 0)
                        {
                            baseX = ImGui.GetCursorPosX() + 25f;
                        }

                        ImGui.SetCursorPosX(baseX + (i % TAG_COLS) * TAG_COL_WIDTH);
                        var division = divisions.ElementAt(i - manualInsertions);
                        var divisionName = division.Key.Name.ToString();
                        var isChecked = division.Value;
                        if (ImGui.Checkbox($"##_RUF_{categoryName}_{divisionName}", ref isChecked))
                        {
                            this.divisionTags[categoryName][division.Key] = isChecked;
                        }
                        ImGui.SameLine();
                        ImGui.Text(divisionName);
                        if (i < divisions.Count - 1 && (i + 1) % TAG_COLS != 0)
                        {
                            ImGui.SameLine();
                        }
                    }
                }
            }
        }

        ImGuiHelpers.ScaledDummy(5.0f);

        filteredCandidates = FilterResourcesCandidates();

        // Filter textbox for candidate list
        ImGui.Text("Add resource:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(250.0f);
        if (ImGui.InputText("##resource_filter", ref this.resourceAddFilter, 256)) { }

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

        //if (ImGui.Button("Mock"))
        //{
        //    filteredCandidates.Where(x => !x.Name.Contains("Cosmotized", StringComparison.OrdinalIgnoreCase)).Take(Math.Min(1000, filteredCandidates.Count)).ToList().ForEach(chosen =>
        //    {
        //        var list = new List<ModItemStack>(this.allDisplayResources ?? []) { new(chosen, chosen.RowId, 99) };
        //        this.allDisplayResources = [.. list];
        //        this.resourceSelections[chosen.RowId] = true;
        //        this.resourceQuantityOverrides[chosen.RowId] = 99;
        //    });
        //}

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

    private static int DefineManualCheckbox(string categoryName, float baseX, int manualInsertions, string divisionName, ref bool primaryFlag, ref bool secondaryFlag)
    {
        ImGui.SetCursorPosX(baseX + manualInsertions % TAG_COLS * TAG_COL_WIDTH);
        var isChecked = primaryFlag;
        if (ImGui.Checkbox($"##_RUF_{categoryName}_{divisionName}", ref isChecked))
        {
            primaryFlag = isChecked;
            // If this flag was set true, ensure the opposite flag is cleared. If set false, leave the other flag as-is.
            secondaryFlag = secondaryFlag && !isChecked;
        }
        ImGui.SameLine();
        ImGui.Text(divisionName);
        if ((manualInsertions + 1) % TAG_COLS != 0)
        {
            ImGui.SameLine();
        }
        return manualInsertions + 1;
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

    // Attempt to open the crafting log on the recipe for `itemId`.
    // Implementation details differ between client versions — this helper:
    // 1) finds the Recipe row for the given result item (if any)
    // 2) calls into the game's UI/agent to open the recipe UI (placeholder)
    // You must hook the exact agent/function from your FFXIVClientStructs version.
    // If you don't have client structs available, you can leave this as a no-op or log.
    private void OpenRecipeInCraftingLog(uint recipeId)
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

    private List<ModItem> FilterResourcesCandidates()
    {
        var presentItems = new HashSet<uint>(this.allDisplayResources?.Select(x => x.Id) ?? []);
        var candidates = this.allIngredients.Where(x => !presentItems.Contains(x.RowId)).OrderBy(x => x.Name).ToList();

        var otherDivisionSelected = this.divisionTags["Other"].First(x => x.Key.Name == "Other").Value;
        // Otherwise, filter by selected divisions
        var selectedDivisions = this.divisionTags
            .SelectMany(category => category.Value.Where(tag => tag.Value).Select(tag => tag.Key))
            .Where(division => division.Division != null)
            .ToHashSet();
        candidates = [.. candidates.Where(item =>
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

            if (this.onlyCrafted && this.recipeCacheService.FindRecipeByResultItem(item) == null)
            {
                return false;
            }

            if (this.onlyRaw && this.recipeCacheService.FindRecipeByResultItem(item) != null)
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

            var allDivisionIds = this.divisionTags.SelectMany(category => category.Value.Select(tag => tag.Key.RowId)).ToHashSet();
            // If the user has selected the "Other" division, include items with divisions not in the possible division ids
            return otherDivisionSelected && !divisions.All(x => allDivisionIds.Contains(x));
        })];

        // Only include items that are usable based on the recipe requirements. e.g. it's used in at least 1 recipe we know how to craft.
        candidates = candidates
            .Where(x => this.ingredientsUsable.Contains(x)).ToList();

        // Apply the filter (case-insensitive) to the candidate list.
        return string.IsNullOrWhiteSpace(this.resourceAddFilter)
            ? candidates
            :
            [
                .. candidates.Where(x =>
                    x.Name.ToString().Contains(this.resourceAddFilter, StringComparison.OrdinalIgnoreCase)
                ),
            ];
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
    public class ChunkWrapper(string value)
    {
        public string Value { get; } = value;
        public bool IsNumber { get; } = int.TryParse(value, out _);
    }

    // Custom comparer to look at chunks sequentially 
    public class ChunkComparer : IComparer<IEnumerable<ChunkWrapper>>
    {
        public int Compare(IEnumerable<ChunkWrapper>? x, IEnumerable<ChunkWrapper>? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

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
                    if (cmp != 0) return cmp;
                }
                else
                {
                    var cmp = string.Compare(chunkX.Value, chunkY.Value, StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0) return cmp;
                }
            }
            return 0;
        }
    }
}
