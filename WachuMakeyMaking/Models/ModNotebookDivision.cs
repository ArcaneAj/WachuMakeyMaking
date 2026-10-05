using Lumina.Excel.Sheets;

namespace WachuMakeyMaking.Models
{
    public class ModNotebookDivision(NotebookDivision? division, string? name = null)
    {
        public string Name
        {
            get => name ?? division?.Name.ToString() ?? string.Empty;
        }
        public uint RowId
        {
            get => division?.RowId ?? uint.MaxValue;
        }
        public NotebookDivision? Division
        {
            get => division;
        }

        private readonly string? name = name;
        private readonly NotebookDivision? division = division;
    }
}
