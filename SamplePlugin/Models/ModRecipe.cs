using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace SamplePlugin.Models
{
    public record ModRecipe(Item Item, Dictionary<Item, byte> Ingredients) {}
}
