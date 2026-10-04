using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Services;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Tabs
{
    public class ResultsTab
    {
        private ResultsModel model;
        private InventoryService inventoryService;
        private RecipeService recipeService;
        private SolverService solverService;

        public ResultsTab(InventoryService inventoryService, RecipeService recipeService, SolverService solverService)
        {
            this.model = new ResultsModel();
            this.inventoryService = inventoryService;
            this.recipeService = recipeService;
            this.solverService = solverService;
            // Register as a progress listener
            this.solverService.RegisterProgressListener(this.model.OnSolverProgressUpdate);
        }

        public void Draw()
        {
            if (this.model.SolverState == SolverService.State.Idle)
            {
                ImGui.Text("No solution computed yet. Go to the Recipes tab and click 'Solve' to start.");
                return;
            }

            var solverInputs = this.solverService.ItemsWithValues.Values.ToList();

            // Display current state
            ImGui.Text($"Status: {this.model.SolverProgressMessage}");

            if (this.model.SolverState == SolverService.State.FindingInitialSolution)
            {
                ImGui.Text("Finding initial solution...");
            }
            else if (this.model.SolverState == SolverService.State.Optimising)
            {
                ImGui.Text("Optimising...");
                if (this.model.CurrentSolution != null)
                {
                    ImGui.Text($"Current best value: {Math.Floor(-this.model.CurrentSolution.OptimalValue)} gil");
                }
            }
            else if (this.model.SolverState == SolverService.State.Finished && this.model.CurrentSolution != null)
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

                        foreach (var (stack, index) in this.model.CurrentSolution.Values.Select((x, i) => (x, i)))
                        {
                            var quantity = stack.Quantity;
                            if (quantity > 0)
                            {
                                var recipe = stack.Recipe;
                                var item = recipe.Item;
                                var itemWithValue = solverInputs.FirstOrDefault(s => s.Item.RowId == item.RowId);
                                var value =
                                    itemWithValue?.Value
                                    ?? this.recipeService.PricesByItemId.GetValueOrDefault(item.RowId)?.Value
                                    ?? 0.0;
                                ImGui.TableNextRow();
                                ImGui.TableSetColumnIndex(0);

                                var hasRecipe = recipe != null;
                                if (recipe == null)
                                {
                                    recipe = new ModRecipe(
                                        0,
                                        item,
                                        1,
                                        new Dictionary<ModItem, byte> { [item] = 1 },
                                        0,
                                        0,
                                        0,
                                        0
                                    );
                                }
                                var id = $"result_{index}_{item.RowId}";

                                ImGui.PushID(id);
                                // Use our own open-state map and an invisible button overlay so we can intercept clicks.
                                var open = GetOpenState(id);
                                // disable header hover/active highlight for the arrow (we render our own header)
                                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0, 0, 0, 0));
                                ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0, 0, 0, 0));
                                ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0, 0, 0, 0));
                                // ensure ImGui's visual open state matches our map
                                ImGui.SetNextItemOpen(open, ImGuiCond.Always);
                                // draw arrow so it looks like a tree node but don't push into the tree stack
                                ImGui.TreeNodeEx(
                                    "##arrow",
                                    ImGuiTreeNodeFlags.AllowItemOverlap | ImGuiTreeNodeFlags.NoTreePushOnOpen
                                );
                                // If the built-in arrow was clicked, sync our open-state map
                                if (ImGui.IsItemClicked())
                                {
                                    open = !open;
                                    SetOpenState(id, open);
                                }
                                ImGui.PopStyleColor(3);
                                // draw custom header content on the same line
                                ImGui.SameLine();
                                ImGui.BeginGroup();
                                UiUtils.DrawIcon(item.RowId, value);
                                ImGui.SameLine();
                                ImGui.AlignTextToFramePadding();
                                var countToDisplay = recipe.Number > 1 ? $" x{recipe.Number}" : string.Empty;
                                ImGui.TextUnformatted($"{item.Name}{countToDisplay}");
                                if (ImGui.IsItemHovered())
                                {
                                    ImGui.BeginTooltip();
                                    ImGui.Text($"Shift + click to open recipe");
                                    ImGui.EndTooltip();
                                }
                                ImGui.EndGroup();

                                // make whole header clickable and capture modifier keys
                                var rectMin = ImGui.GetItemRectMin();
                                var rectMax = ImGui.GetItemRectMax();
                                ImGui.SetCursorScreenPos(rectMin);
                                if (ImGui.InvisibleButton($"hdr_btn_{id}", rectMax - rectMin))
                                {
                                    var io = ImGui.GetIO();
                                    if (io.KeyShift && recipe.RowId > 0)
                                    {
                                        try
                                        {
                                            UiUtils.OpenRecipeInCraftingLog(recipe.RowId);
                                        }
                                        catch (Exception ex)
                                        {
                                            Plugin.Log.Error(
                                                $"Failed to open crafting log for recipe {item.RowId}: {ex.Message}"
                                            );
                                        }
                                    }
                                    else
                                    {
                                        // Normal click toggles open state
                                        open = !open;
                                        SetOpenState(id, open);
                                    }
                                }

                                var ownedItems = this.inventoryService.GetOwnedItems();
                                var ownedItemsDict = ownedItems.ToDictionary(x => x.Item, x => x);

                                var configuredItems = this.inventoryService.GetOverriddenItems();
                                var configuredItemsDict = configuredItems.ToDictionary(x => x.Item, x => x);

                                // render body if open
                                if (open)
                                {
                                    ImGui.Indent();

                                    recipe
                                        .Ingredients.ToList()
                                        .ForEach(ingredientWithCount =>
                                        {
                                            var ingredient = ingredientWithCount.Key;
                                            var count = ingredientWithCount.Value;
                                            var requiredQuantity = count * quantity;

                                            UiUtils.DrawIcon(ingredient.RowId);
                                            ImGui.SameLine();
                                            var countToDisplay = count > 1 ? $" x{count}" : string.Empty;
                                            ImGui.TextUnformatted($"{ingredient.Name}{countToDisplay}");
                                            ImGui.SameLine();
                                            var amountOwned = ownedItemsDict
                                                .GetValueOrDefault(ingredient, new ModItemStack(ingredient, 0, 0))
                                                .Quantity;
                                            var amountConfigured = configuredItemsDict
                                                .GetValueOrDefault(ingredient, new ModItemStack(ingredient, 0, 0))
                                                .Quantity;
                                            var differenceString =
                                                amountOwned == amountConfigured
                                                    ? string.Empty
                                                    : $"({amountConfigured})";
                                            ImGui.TextUnformatted(
                                                $"{amountOwned}{differenceString}/{requiredQuantity}"
                                            );
                                        });

                                    ImGui.Unindent();
                                }
                                ImGui.PopID();

                                //ImGui.PushID(id);
                                //var open = GetOpenState(id);
                                //// your map/state
                                //// draw arrow (no visible label) so TreeNode behavior draws arrow
                                //ImGui.TreeNodeEx("##arrow", ImGuiTreeNodeFlags.AllowItemOverlap);
                                //// draw custom header content on the same line
                                //ImGui.SameLine();
                                //ImGui.BeginGroup();
                                //DrawIcon(solverRecipes[i].Item.RowId, solverRecipes[i].Value);
                                //ImGui.SameLine();
                                //ImGui.AlignTextToFramePadding();
                                //ImGui.TextUnformatted(solverRecipes[i].Item.Name);
                                //ImGui.EndGroup();
                                //// make whole header clickable
                                //var rectMin = ImGui.GetItemRectMin();
                                //var rectMax = ImGui.GetItemRectMax();
                                //ImGui.SetCursorScreenPos(rectMin);
                                //if (ImGui.InvisibleButton($"hdr_btn_{id}", rectMax - rectMin))
                                //{
                                //    open = !open;
                                //    SetOpenState(id, open);
                                //}
                                //ImGui.SetCursorScreenPos(rectMax);
                                //// render body if open
                                //if (open)
                                //{
                                //    ImGui.Indent();
                                //    ImGui.Text("Body content here");
                                //    ImGui.Unindent();
                                //    ImGui.TreePop();
                                //}
                                //ImGui.PopID();

                                //if (ImGui.TreeNodeEx($"result_{i}_{solverRecipes[i].RowId}", ImGuiTreeNodeFlags.SpanFullWidth))
                                //{
                                //    ImGui.BeginGroup();
                                //    DrawIcon(solverRecipes[i].Item.RowId, solverRecipes[i].Value);
                                //    ImGui.SameLine();
                                //    ImGui.AlignTextToFramePadding();
                                //    ImGui.TextUnformatted(solverRecipes[i].Item.Name);
                                //    ImGui.EndGroup();
                                //    ImGui.TreePop();
                                //}

                                //var rectMin = ImGui.GetItemRectMin();
                                //var rectMax = ImGui.GetItemRectMax();
                                //var size = rectMax - rectMin;
                                //ImGui.SetCursorScreenPos(rectMin);
                                //if (ImGui.InvisibleButton($"cell_btn_result_{solverRecipes[i].RowId}", size))
                                //{
                                //    try
                                //    {
                                //        OpenRecipeInCraftingLog(solverRecipes[i].RowId);
                                //    }
                                //    catch (Exception ex)
                                //    {
                                //        Plugin.Log.Error(
                                //            $"Failed to open crafting log for recipe {solverRecipes[i].RowId}: {ex.Message}"
                                //        );
                                //    }
                                //}

                                //ImGui.SetCursorScreenPos(rectMax);

                                ImGui.TableSetColumnIndex(1);
                                ImGui.Text((recipe.Number * quantity).ToString());
                                ImGui.TableSetColumnIndex(2);
                                ImGui.Text($"{(int)value}");
                                ImGui.TableSetColumnIndex(3);
                                ImGui.Text($"{(int)value * recipe.Number * quantity}");
                            }
                        }

                        ImGui.EndTable();
                    }
                }
            }
            else if (
                this.model.SolverState == SolverService.State.Error
                || this.model.SolverState == SolverService.State.Unbounded
            )
            {
                ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), $"Error: {this.model.SolverProgressMessage}");
            }
        }

        private readonly HashSet<string> openStates = [];

        private void SetOpenState(string id, bool open)
        {
            if (open)
                openStates.Add(id);
            else
                openStates.Remove(id);
        }

        private bool GetOpenState(string id)
        {
            return openStates.Contains(id);
        }
    }
}
