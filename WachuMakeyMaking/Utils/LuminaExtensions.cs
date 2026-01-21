using Lumina.Excel.Sheets;
using WachuMakeyMaking.Models;

namespace WachuMakeyMaking.Utils
{
    public static class LuminaExtensions
    {
        public static ModItem ToMod(this Item item)
        {
            return new ModItem(item.RowId, item.Name.ToString());
        }
    }
}
