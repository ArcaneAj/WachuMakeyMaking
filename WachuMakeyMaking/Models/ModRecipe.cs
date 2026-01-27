using System.Collections.Generic;

namespace WachuMakeyMaking.Models
{
    public record ModRecipe(
        uint RowId,
        ModItem Item,
        int Number,
        Dictionary<ModItem, byte> Ingredients,
        byte classJobLevel,
        uint classJobId,
        uint book
    ) { }

    public record ModRecipeWithValue(ModRecipe recipe, double Value, ModItem Currency)
        : ModRecipe(
            recipe.RowId,
            recipe.Item,
            recipe.Number,
            recipe.Ingredients,
            recipe.classJobLevel,
            recipe.classJobId,
            recipe.book
        );
}
