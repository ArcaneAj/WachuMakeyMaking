using System;

namespace WachuMakeyMaking.Models
{
    public record ModItem(uint RowId, string Name);
    public record ModItemWithValue(ModItem Item, double Value, ModItem Currency)
        : ModItem(
            Item.RowId,
            Item.Name
        );
}
