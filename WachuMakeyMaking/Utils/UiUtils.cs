using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace WachuMakeyMaking.Utils
{
    public static class UiUtils
    {
        private const int TAG_COLS = 4;
        private const float TAG_COL_WIDTH = 200f;

        public static void DrawIcon(uint itemId, double value = -1)
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
                var iconSize = new Vector2(
                    20.0f * ImGui.GetIO().FontGlobalScale,
                    20.0f * ImGui.GetIO().FontGlobalScale
                );
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
            }
        }

        public static int DefineManualCheckbox(
            string categoryName,
            float baseX,
            int manualInsertions,
            string divisionName,
            ref bool primaryFlag,
            ref bool secondaryFlag,
            System.Action onChange
        )
        {
            ImGui.SetCursorPosX(baseX + manualInsertions % TAG_COLS * TAG_COL_WIDTH);
            var isChecked = primaryFlag;
            if (ImGui.Checkbox($"##_RUF_{categoryName}_{divisionName}", ref isChecked))
            {
                primaryFlag = isChecked;
                // If this flag was set true, ensure the opposite flag is cleared. If set false, leave the other flag as-is.
                secondaryFlag = secondaryFlag && !isChecked;
                onChange();
            }
            ImGui.SameLine();
            ImGui.Text(divisionName);
            if ((manualInsertions + 1) % TAG_COLS != 0)
            {
                ImGui.SameLine();
            }
            return manualInsertions + 1;
        }

        // Attempt to open the crafting log on the recipe for `itemId`.
        // Implementation details differ between client versions — this helper:
        // 1) finds the Recipe row for the given result item (if any)
        // 2) calls into the game's UI/agent to open the recipe UI (placeholder)
        // You must hook the exact agent/function from your FFXIVClientStructs version.
        // If you don't have client structs available, you can leave this as a no-op or log.
        public static void OpenRecipeInCraftingLog(uint recipeId)
        {
            // Find a recipe whose result item matches this itemId
            var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();
            var recipe = recipeSheet.GetRow(recipeId);
            var matchingGearSets = EnumerateGearSets().Where(x => x.JobId == recipe.CraftType.RowId);
            if (!matchingGearSets.Any())
                throw new Exception($"No gearset found for job {recipe.CraftType.RowId}");

            var recipeNotebookListSheet = Plugin.DataManager.GetExcelSheet<RecipeNotebookList>();
            var recipeNotebookList = recipeNotebookListSheet.FirstOrDefault(list =>
                list.Recipe.Any(r => r.RowId == recipeId)
            );

            var indexInPage = -1;
            foreach (var (r, index) in recipeNotebookList.Recipe.Select((x, i) => (x, i)))
            {
                Plugin.Log.Info($"Recipe {r.RowId} at index {index} in category {recipeNotebookList.RowId}");
                if (r.RowId == recipe.RowId || r.RowId == 4294967295)
                {
                    indexInPage = index;
                    break;
                }
            }

            var noteBookDivisionId =
                recipe.RecipeNotebookList.RowId != 0 && recipe.RecipeNotebookList.IsValid
                    ? (recipe.RecipeNotebookList.RowId - 1000) / 8 + 1000
                    : ((uint)recipe.RecipeLevelTable.Value.ClassJobLevel - 1) / 5;

            var categoryPage = noteBookDivisionId < 1000 ? 0 : recipeNotebookList.RowOffset / 8;

            unsafe
            {
                if (AgentRecipeNote.Instance()->SelectedRecipeIndex == indexInPage)
                    if (AgentRecipeNote.Instance()->SelectedRecipeCategory == noteBookDivisionId)
                        if (AgentRecipeNote.Instance()->SelectedRecipeCategoryPage == categoryPage)
                            if (AgentRecipeNote.Instance()->SelectedCraftType == recipe.CraftType.RowId)
                                return; // Already open to the right recipe, no need to do anything)

                RaptureGearsetModule.Instance()->EquipGearset(matchingGearSets.First().GearSetId);
                AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipeId);
            }
        }

        public static List<(int GearSetId, int JobId)> EnumerateGearSets()
        {
            var gearSetJobs = new List<(int GearSetId, int JobId)>();
            unsafe
            {
                var gearsetModule = RaptureGearsetModule.Instance();
                var i = -1;
                foreach (ref var gearset in gearsetModule->Entries)
                {
                    i++;
                    if (
                        !gearset.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)
                        || gearset.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.MainHandMissing)
                    )
                        continue;
                    gearSetJobs.Add((i, gearset.ClassJob - 8));
                }
            }

            return gearSetJobs;
        }
    }
}
