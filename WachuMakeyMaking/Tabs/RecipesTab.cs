using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Tabs
{
    public class RecipesTab
    {
        private readonly RecipesModel model;
        private readonly TabService tabService;
        private readonly RecipeService recipeService;
        private readonly SolverService solverService;

        public RecipesTab(TabService tabService, RecipeService recipeService, SolverService solverService)
        {
            this.model = new RecipesModel(recipeService);
            this.tabService = tabService;
            this.recipeService = recipeService;
            this.solverService = solverService;
            this.recipeService.UpdateCompleteEvent.Subscribe("RecipesTab", () => this.model.ScheduleUpdate());
        }

        public void Draw()
        {
            if (this.recipeService.InFlight > 0)
            {
                ImGui.Text("Loading recipes...");

                if (!string.IsNullOrEmpty(this.recipeService.CurrentProcessingStep))
                {
                    ImGui.Text(this.recipeService.CurrentProcessingStep);
                }

                return;
            }

            if (ImGui.Button("Reset"))
            {
                this.model.ResetOverrides();
            }

            ImGui.SameLine();

            var selectedCount = this.model.RecipeSelections.Count(x => x.Value);

            if (selectedCount == 0)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("Solve"))
            {
                // Call the solver service
                Task.Run(() => this.solverService.Solve(this.model.RecipesWithValues, this.model.WiggledRecipesWithValues));
                this.tabService.SetActiveTab(Tab.Results);
            }

            if (selectedCount == 0)
            {
                ImGui.EndDisabled();
            }

            ImGui.SameLine();

            ImGui.Text($"{this.model.RecipeSelections.Count} craftable recipes found ({selectedCount} selected)");

            if (!string.IsNullOrEmpty(this.recipeService.UniversalisMessage))
            {
                ImGui.TextColored(KnownColor.OrangeRed.Vector(), this.recipeService.UniversalisMessage);
            }

            ImGuiHelpers.ScaledDummy(10.0f);
            if (this.model.RecipeSelections.Count > 0)
            {
                var outputs = this.model.Recipes;
                var currencyGrouping = outputs.GroupBy(x => x.Currency.RowId).Where(x => x.Key != 1);

                // Clean up currency values for currencies that are no longer in cache
                var currentCurrencyIds = new HashSet<uint>(currencyGrouping.Select(g => g.Key));
                var currencyIdsToRemove = this.model.CurrencyValues.Keys.Where(id => !currentCurrencyIds.Contains(id)).ToList();
                foreach (var id in currencyIdsToRemove)
                {
                    this.model.RemoveCurrencyValue(id);
                }

                // Editable scrip value controls
                foreach (var currencyGroup in currencyGrouping)
                {
                    var currencyId = currencyGroup.Key;
                    var currency = currencyGroup.First().Currency;

                    // Initialize currency value if not present
                    if (!this.model.CurrencyValues.TryGetValue(currencyId, out var currencyValue))
                    {
                        currencyValue = 1.0f;
                        this.model.SetCurrencyValue(currencyId, currencyValue);
                    }

                    if (ImGui.InputFloat($"{currency.Name} gil value", ref currencyValue, 0, 0, "%.2f"))
                    {
                        // Cap at 1000 to avoid prices exceeding 999999
                        this.model.SetCurrencyValue(currencyId, Math.Min(currencyValue, 1000.0f));
                    }
                }

                // Prepare toggle state / counts used by header checkbox
                var totalOutputs = outputs.Count;
                var allSelected = selectedCount == totalOutputs && totalOutputs > 0;
                var someSelected = selectedCount > 0 && selectedCount < totalOutputs;
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
                                foreach (var output in outputs)
                                {
                                    this.model.SetSelected(output.Item, true);
                                }
                            }
                            // If clicking when all selected, deselect all
                            else
                            {
                                foreach (var output in outputs)
                                {
                                    this.model.SetSelected(output.Item, false);
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
                        foreach (var output in outputs.OrderByDescending(r => r.RowId))
                        {
                            var outputName = output.Item.Name.ToString();
                            var isSelected = this.model.RecipeSelections.GetValueOrDefault(output.Item, false);

                            // Get the displayed value (use override if exists, otherwise calculated)
                            var value = (int)Math.Floor(output.Value);

                            ImGui.TableNextRow();

                            // Value column
                            ImGui.TableSetColumnIndex(0);
                            ImGui.SetNextItemWidth(80.0f);
                            if (ImGui.InputInt($"##value_{outputName}", ref value))
                            {
                                // Clamp to valid range (0-999999)
                                this.model.SetRecipeValueOverride(output.Item, Math.Min(Math.Max(value, 0), 999999));
                            }

                            // Checkbox column
                            ImGui.TableSetColumnIndex(1);
                            if (ImGui.Checkbox($"##{outputName}", ref isSelected))
                            {
                                this.model.SetSelected(output.Item, isSelected);
                            }

                            // Recipe column (icon + name) — single click handler for entire cell using an InvisibleButton
                            ImGui.TableSetColumnIndex(2);

                            // Reserve full available width for the column before drawing
                            var fullWidth = ImGui.GetContentRegionAvail().X;
                            var iconHeight = 20.0f * ImGui.GetIO().FontGlobalScale;
                            var rowHeight = Math.Max(ImGui.GetFrameHeightWithSpacing(), iconHeight);

                            // Create the invisible button that covers the whole cell
                            ImGui.InvisibleButton($"cell_btn_recipe_{output.RowId}", new Vector2(fullWidth, rowHeight));
                            if (ImGui.IsItemClicked())
                            {
                                var recipe = this.recipeService.GetRecipesByOutput(output.Item).FirstOrDefault(r => r.RowId == output.RowId);
                                if (recipe != null)
                                {
                                    try
                                    {
                                        UiUtils.OpenRecipeInCraftingLog(recipe.RowId);
                                    }
                                    catch (Exception ex)
                                    {
                                        Plugin.Log.Error(
                                            $"Failed to open crafting log for recipe {output.RowId}: {ex.Message}"
                                        );
                                    }
                                }
                            }

                            var btnMin = ImGui.GetItemRectMin();
                            var padX = 4.0f;
                            var iconY = btnMin.Y + ((rowHeight - iconHeight) * 0.5f);

                            ImGui.SetCursorScreenPos(new Vector2(btnMin.X + padX, iconY));
                            UiUtils.DrawIcon(output.Item.RowId);
                            ImGui.SameLine();
                            ImGui.Text($"{output.Item.Name}");

                            // Move cursor to the right edge of the invisible button so subsequent columns render correctly
                            ImGui.SetCursorScreenPos(new Vector2(btnMin.X + fullWidth, btnMin.Y));
                        }

                        ImGui.EndTable();
                    }
                }
            }
        }

    }
}
