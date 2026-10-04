using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using WachuMakeyMaking.Services;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Tabs
{
    public class IngredientsTab
    {
        private readonly IngredientsModel model;
        private readonly RecipeService recipeService;
        private readonly InventoryService inventoryService;
        private const int TAG_COLS = 4;
        private const float TAG_COL_WIDTH = 200f;

        public IngredientsTab(RecipeService recipeService, InventoryService inventoryService)
        {
            this.recipeService = recipeService;
            this.inventoryService = inventoryService;
            var recipes = this.recipeService.GetRecipes();
            this.model = new IngredientsModel(inventoryService, recipeService, recipes);
            this.inventoryService.InitCompleteEvent.Subscribe("IngredientsTab", () => this.model.ScheduleUpdate());
        }

        public void Draw()
        {
            var displayItems = this.model.DisplayItems;
            if (displayItems == null)
                return;

            if (ImGui.Button("Reset"))
            {
                this.model.ResetOverrides();
            }

            ImGui.SameLine();

            var itemSelections = model.IngredientSelections;

            var selectedItemsCount = itemSelections.Count(x => x.Value);
            ImGui.Text($"{displayItems.Length} resources found with recipes ({selectedItemsCount} selected)");

            ImGuiHelpers.ScaledDummy(5.0f);
            if (ImGui.CollapsingHeader("Item sources (inclusive)"))
            {
                var itemSources = this.inventoryService.ItemSources;

                var baseX = 0f;
                foreach (var (itemSource, sourceEnabled, i) in itemSources.Select((kvp, i) => (kvp.Key, kvp.Value, i)))
                {
                    if (i == 0)
                    {
                        baseX = ImGui.GetCursorPosX() + 25f;
                    }

                    ImGui.SetCursorPosX(baseX + (i % TAG_COLS) * TAG_COL_WIDTH);
                    var isChecked = sourceEnabled;
                    if (ImGui.Checkbox($"##_RUF_{itemSource}", ref isChecked))
                    {
                        this.model.SetItemSource(itemSource, isChecked);
                    }

                    ImGui.SameLine();
                    ImGui.Text(itemSource);
                    if (i < itemSources.Count - 1 && (i + 1) % TAG_COLS != 0)
                    {
                        ImGui.SameLine();
                    }
                }
            }

            ImGuiHelpers.ScaledDummy(5.0f);

            if (ImGui.CollapsingHeader("Resource addition categories (inclusive)"))
            {
                foreach (var divisionCategory in this.model.DivisionTags)
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
                        this.model.SetDivisionCategory(categoryName, headerChecked);
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
                        var barLeft = new Vector2(
                            itemMin.X + style.FramePadding.X,
                            (itemMin.Y + itemMax.Y) / 2f - 1.0f
                        );
                        var barRight = new Vector2(
                            itemMin.X + style.FramePadding.X + 15.0f,
                            (itemMin.Y + itemMax.Y) / 2f + 1.0f
                        );
                        var col = ImGui.GetColorU32(ImGuiCol.Text);
                        drawList.AddRectFilled(barLeft, barRight, col, 1.0f);
                    }

                    ImGui.SameLine();
                    if (ImGui.CollapsingHeader(categoryName))
                    {
                        var baseX = 0f;
                        for (var i = 0; i < divisions.Count; i++)
                        {
                            if (i == 0)
                            {
                                baseX = ImGui.GetCursorPosX() + 25f;
                            }

                            ImGui.SetCursorPosX(baseX + (i % TAG_COLS) * TAG_COL_WIDTH);
                            var division = divisions.ElementAt(i);
                            var divisionName = division.Key.Name.ToString();
                            var isChecked = division.Value;
                            if (ImGui.Checkbox($"##_RUF_{categoryName}_{divisionName}", ref isChecked))
                            {
                                this.model.SetDivision(categoryName, division.Key, headerChecked);
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

            if (ImGui.CollapsingHeader("Resource addition filters (exclusive)"))
            {
                var baseX = 0f;
                var manualInsertions = 0;
                baseX = ImGui.GetCursorPosX() + 25f;

                // Add the two manual checkboxes for "Only Equippable" and "Only Unequippable"
                manualInsertions = UiUtils.DefineManualCheckbox(
                    "filters",
                    baseX,
                    manualInsertions,
                    "Only Equippable",
                    ref this.model.onlyEquippable,
                    ref this.model.onlyUnequippable,
                    this.model.ScheduleUpdate
                );
                manualInsertions = UiUtils.DefineManualCheckbox(
                    "filters",
                    baseX,
                    manualInsertions,
                    "Only Unequippable",
                    ref this.model.onlyUnequippable,
                    ref this.model.onlyEquippable,
                    this.model.ScheduleUpdate
                );

                // Add the two manual checkboxes for "Only Crafted" and "Only Raw"
                manualInsertions = UiUtils.DefineManualCheckbox(
                    "filters",
                    baseX,
                    manualInsertions,
                    "Only Crafted",
                    ref this.model.onlyCrafted,
                    ref this.model.onlyRaw,
                    this.model.ScheduleUpdate
                );
                manualInsertions = UiUtils.DefineManualCheckbox(
                    "filters",
                    baseX,
                    manualInsertions,
                    "Only Raw",
                    ref this.model.onlyRaw,
                    ref this.model.onlyCrafted,
                    this.model.ScheduleUpdate
                );
            }

            ImGuiHelpers.ScaledDummy(5.0f);

            var filterOffsetX = 175f;

            // Filter textbox for candidate list
            ImGui.Text("Add resource:");
            ImGui.SameLine();
            ImGui.SetCursorPosX(filterOffsetX);
            ImGui.SetNextItemWidth(250.0f);
            var ingredientAddFilter = this.model.IngredientAddFilter;
            if (ImGui.InputText("##resource_filter", ref ingredientAddFilter, 256))
            {
                this.model.SetIngredientAddFilter(ingredientAddFilter);
            }

            // Current display name for combo (from filtered list)
            var currentName =
                this.model.FilteredCandidates.Count > 0 ? this.model.FilteredCandidates[0].Name : "Select...";

            ImGui.SameLine();
            ImGui.SetNextItemWidth(250.0f);

            // Constrain the combo popup to max height 100px and a reasonable width.
            // Call before BeginCombo so it applies to the combo popup window.
            ImGui.SetNextWindowSizeConstraints(new Vector2(0, 0), new Vector2(250.0f, 300.0f));
            if (ImGui.BeginCombo("##add_resource_combo", currentName, ImGuiComboFlags.None))
            {
                for (var i = 0; i < this.model.FilteredCandidates.Count; i++)
                {
                    var name = this.model.FilteredCandidates[i].Name;
                    if (ImGui.Selectable(name, i == 0))
                    {
                        // Immediately add the clicked item
                        var chosenIndex = Math.Max(0, Math.Min(i, this.model.FilteredCandidates.Count - 1));
                        var chosenItem = this.model.FilteredCandidates[chosenIndex];

                        this.model.AddManualIngredients([chosenItem]);

                        // Reset filter and selected index so the combo shows the full list next time
                        this.model.SetIngredientAddFilter(string.Empty);

                        // Close the combo popup after selection
                        ImGui.CloseCurrentPopup();
                    }
                    if (i == 0)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            // Filter textbox for candidate list
            ImGui.Text("Add resources for recipe:");
            ImGui.SameLine();
            ImGui.SetCursorPosX(filterOffsetX);
            ImGui.SetNextItemWidth(250.0f);
            var recipeIngredientAddFilter = this.model.RecipeIngredientAddFilter;
            if (ImGui.InputText("##recipe_resource_filter", ref recipeIngredientAddFilter, 256))
            {
                this.model.SetRecipeIngredientAddFilter(recipeIngredientAddFilter);
            }

            // Current display name for combo (from filtered list)
            currentName =
                this.model.FilteredCraftableCandidates.Count > 0
                    ? this.model.FilteredCraftableCandidates[0].Name
                    : "Select...";

            ImGui.SameLine();
            ImGui.SetNextItemWidth(250.0f);

            // Constrain the combo popup to max height 100px and a reasonable width.
            // Call before BeginCombo so it applies to the combo popup window.
            ImGui.SetNextWindowSizeConstraints(new Vector2(0, 0), new Vector2(250.0f, 300.0f));
            if (ImGui.BeginCombo("##add_recipe_resource_combo", currentName, ImGuiComboFlags.None))
            {
                for (var i = 0; i < this.model.FilteredCraftableCandidates.Count; i++)
                {
                    var name = this.model.FilteredCraftableCandidates[i].Name;
                    if (ImGui.Selectable(name, i == 0))
                    {
                        var chosen = this.model.FilteredCraftableCandidates[
                            Math.Max(0, Math.Min(i, this.model.FilteredCraftableCandidates.Count - 1))
                        ];
                        var ingredients = this
                            .recipeService.GetRecipesByOutput(chosen)
                            .FirstOrDefault()
                            ?.Ingredients.Keys.Where(x => !this.model.DisplayItems.Any(y => y.Item.RowId == x.RowId))
                            .ToList();

                        if (ingredients == null || ingredients.Count == 0)
                        {
                            ImGui.CloseCurrentPopup();
                            continue;
                        }

                        // Default to quantity 0 (user can edit after adding)
                        this.model.AddManualIngredients(ingredients);

                        // Reset filter and selected index so the combo shows the full list next time
                        this.model.SetRecipeIngredientAddFilter(string.Empty);

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
            var totalResources = this.model.DisplayItems!.Length;
            var selectedCount = this.model.DisplayItems.Count(r =>
                this.model.IngredientSelections.GetValueOrDefault(r.Id, false)
            );
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
                        this.model.SetIngredientSelections(this.model.DisplayItems, headerToggle);
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
                    foreach (var resourceItem in this.model.DisplayItems.OrderByDescending(r => r.Id))
                    {
                        var isSelected = this.model.IngredientIsSelected(resourceItem.Item);
                        var quantity = resourceItem.Quantity;

                        ImGui.TableNextRow();

                        // Quantity column
                        ImGui.TableSetColumnIndex(0);
                        ImGui.SetNextItemWidth(80.0f);
                        if (ImGui.InputInt($"##quantity_{resourceItem.Id}", ref quantity))
                        {
                            this.model.SetItemQuantity(resourceItem.Id, quantity);
                        }

                        // Checkbox column
                        ImGui.TableSetColumnIndex(1);
                        if (ImGui.Checkbox($"##sel_{resourceItem.Id}", ref isSelected))
                        {
                            this.model.SetIngredientSelections([resourceItem], isSelected);
                        }

                        // Resource column (icon + name)
                        ImGui.TableSetColumnIndex(2);
                        UiUtils.DrawIcon(resourceItem.Id);
                        ImGui.SameLine();

                        var displayName = resourceItem.Item.Name;
                        if (this.model.Ingredients.TryGetValue(resourceItem.Item, out var originalItemStack))
                        {
                            displayName += $" ({originalItemStack.Quantity} available)";
                        }
                        ImGui.Text(displayName);
                    }

                    ImGui.EndTable();
                }
            }
        }
    }
}
