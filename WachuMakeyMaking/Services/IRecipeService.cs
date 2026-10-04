using System.Collections.Generic;
using WachuMakeyMaking.Models;

namespace WachuMakeyMaking.Services
{
    public interface IRecipeService
    {
        List<ModItemStack> SelectedIngredients { get; }
        List<ModRecipe> GetRecipesByOutput(ModItem item);
    }
}
