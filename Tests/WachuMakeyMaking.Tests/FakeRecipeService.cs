using System.Collections.Generic;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Services;

namespace WachuMakeyMaking.Tests
{
    internal class FakeRecipeService : IRecipeService
    {
        public List<ModItemStack> SelectedIngredients { get; set; } = new();

        private readonly Dictionary<ModItem, List<ModRecipe>> recipes
            = new Dictionary<ModItem, List<ModRecipe>>();

        public void SetRecipesForOutput(ModItem item, List<ModRecipe> recs)
        {
            recipes[item] = recs;
        }

        public List<ModRecipe> GetRecipesByOutput(ModItem item)
        {
            if (recipes.TryGetValue(item, out var r)) return r;
            return new List<ModRecipe>();
        }
    }
}
