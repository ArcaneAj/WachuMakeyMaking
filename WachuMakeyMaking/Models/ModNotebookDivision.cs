using Lumina.Excel.Sheets;

namespace WachuMakeyMaking.Models
{
    public class ModNotebookDivision
    {
        public string Name { get => name ?? division?.Name.ToString() ?? string.Empty; }
        public uint RowId { get => division?.RowId ?? uint.MaxValue; }
        public NotebookDivision? Division { get => division; }

        private string? name;
        private NotebookDivision? division;

        public ModNotebookDivision(NotebookDivision? division, string? name = null)
        {
            this.division = division;
            this.name = name;
        }
    }
}
