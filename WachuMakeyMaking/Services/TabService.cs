using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Tabs;

namespace WachuMakeyMaking.Services
{
    public class TabService
    {
        private readonly IngredientsTab ingredientsTab;
        private readonly RecipesTab recipesTab;
        private readonly ResultsTab resultsTab;

        // When SetActiveTab is called from outside we store the request here so we can
        // apply ImGuiTabItemFlags.SetSelected for the next Draw pass. This avoids
        // races where ImGui only honors SetSelected when provided at the right time.
        private Tab? pendingTab;

        public TabService(RecipeService recipeService, InventoryService inventoryService, SolverService solverService)
        {
            this.ingredientsTab = new IngredientsTab(recipeService, inventoryService);
            this.recipesTab = new RecipesTab(this, recipeService, solverService);
            this.resultsTab = new ResultsTab(inventoryService, recipeService, solverService);
        }

        public void Draw()
        {
            // Create tabs
            using (var tabBar = ImRaii.TabBar("MainTabs"))
            {
                if (tabBar.Success)
                {
                    // Tab 1: Resources
                    using (
                        var tab = ImRaii.TabItem(
                            "Inputs",
                            this.pendingTab == Tab.Ingredients ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None
                        )
                    )
                    {
                        if (tab.Success)
                        {
                            // Clear any pending request once the tab has actually been opened.
                            this.pendingTab = null;
                            ingredientsTab.Draw();
                        }
                    }

                    // Tab 2: Recipes
                    using (
                        var tab = ImRaii.TabItem(
                            "Outputs",
                            this.pendingTab == Tab.Recipes ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None
                        )
                    )
                    {
                        if (tab.Success)
                        {
                            this.pendingTab = null;
                            recipesTab.Draw();
                        }
                    }

                    // Tab 3: Results
                    using (
                        var tab = ImRaii.TabItem(
                            "Results",
                            this.pendingTab == Tab.Results ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None
                        )
                    )
                    {
                        if (tab.Success)
                        {
                            this.pendingTab = null;
                            this.resultsTab.Draw();
                        }
                    }
                }
            }
        }

        public void SetActiveTab(Tab tab)
        {
            // Request that the given tab be selected on the next Draw. We don't set
            // activeTab immediately because ImGui only honors SetSelected when the
            // flag is provided during BeginTabItem; using pendingActiveTab ensures the
            // SetSelected flag is applied for the upcoming Draw call.
            this.pendingTab = tab;
        }
    }
}
