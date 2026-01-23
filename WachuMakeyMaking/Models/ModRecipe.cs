using System.Collections.Generic;

namespace WachuMakeyMaking.Models
{
    public record ModRecipe(ModItem Item, Dictionary<ModItem, byte> Ingredients, byte classJobLevel, uint classJobId) {}

    public record ModRecipeWithValue(ModRecipe recipe, double Value, ModItem Currency)
        : ModRecipe(recipe.Item, recipe.Ingredients, recipe.classJobLevel, recipe.classJobId);
}
