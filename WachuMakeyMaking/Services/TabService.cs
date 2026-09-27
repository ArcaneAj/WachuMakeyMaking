using System;
using WachuMakeyMaking.Models;

namespace WachuMakeyMaking.Services
{
    public class TabService
    {
        private Tab currentTab = Tab.Ingredients;

        public void DrawCurrentTab()
        {
            switch (currentTab)
            {
                case Tab.Ingredients:
                    DrawIngredientsTab();
                    break;
                case Tab.Recipes:
                    DrawRecipesTab();
                    break;
                case Tab.Results:
                    DrawResultsTab();
                    break;
            }
        }

        private void DrawIngredientsTab()
        {
            throw new NotImplementedException();
        }

        private void DrawRecipesTab()
        {
            throw new NotImplementedException();
        }

        private void DrawResultsTab()
        {
            throw new NotImplementedException();
        }
    }
}
