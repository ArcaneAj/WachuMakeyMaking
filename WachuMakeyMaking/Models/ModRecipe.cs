using System.Collections.Generic;
using System.Linq;
using WachuMakeyMaking.Services;

namespace WachuMakeyMaking.Models
{
    public record ModRecipe(
        uint RowId,
        ModItem Item,
        int Number,
        Dictionary<ModItem, byte> Ingredients,
        byte classJobLevel,
        uint classJobId,
        uint book,
        uint noteBookDivisionId
    )
    {
        public bool CanMakeWith(Dictionary<ModItem, int> quantityByItem)
        {
            foreach (var (item, quantity) in this.Ingredients)
            {
                if (quantityByItem.TryGetValue(item, out var ownedQuantity) && ownedQuantity >= quantity)
                {
                    quantityByItem[item] = ownedQuantity - quantity;
                }
                else
                {
                    // We need to see if we can craft the ingredient item with what we have
                    var recipes = RecipeService.GetRecipesByOutput(item);
                    if (recipes.Count == 0)
                        return false; // The missing ingredient isn't craftable
                    if (recipes.All(recipe => !recipe.CanMakeWith(quantityByItem)))
                        return false; // If we can't make it with any of the recipes, we can't make it
                }
            }

            return true;
        }
    }

    public record ModRecipeWithValue(ModRecipe recipe, double Value, ModItem Currency)
        : ModRecipe(
            recipe.RowId,
            recipe.Item,
            recipe.Number,
            recipe.Ingredients,
            recipe.classJobLevel,
            recipe.classJobId,
            recipe.book,
            recipe.noteBookDivisionId
        );
}
