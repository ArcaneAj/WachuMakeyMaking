using System.Collections.Generic;

namespace WachuMakeyMaking.Models
{
    public record ModRecipe(ModItem Item, Dictionary<ModItem, byte> Ingredients) {}

    public record ModRecipeWithValue(ModItem Item, Dictionary<ModItem, byte> Ingredients, double Value, ModItem Currency)
        : ModRecipe(Item, Ingredients);
}
