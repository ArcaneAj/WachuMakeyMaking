using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace SamplePlugin.Models
{
    public record ModRecipe(Item Item, Dictionary<Item, byte> Ingredients) {}

    public record ModRecipeWithValue(Item Item, Dictionary<Item, byte> Ingredients, double Value, Item Currency)
        : ModRecipe(Item, Ingredients);
}
