
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Services;
using static WachuMakeyMaking.Services.SolverService;
using System.Text.Json;
using System.Reflection;

namespace Test
{
    public class Tests
    {
        public Action<string> Log { get; private set; }

        [SetUp]
        public void Setup()
        {
            this.Log = (string l) => { };
        }

        [Test]
        public void Solve_EmptyProblem_ShouldReturnError()
        {
            var expected = new Solution([], 0, State.Error, []);
            var solver = new SolverService(Log, Log);
            var solution = solver.Solve([], []);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(solver.CurrentState, Is.EqualTo(State.Error));
                Assert.That(solution, Is.EqualTo(expected));
            }
        }

        //[Test]
        //public void Solve_RealData_ShouldReturnOptimal()
        //{
        //    (var recipes, var resources) = LoadTestData("TestData1.json");
        //    var solver = new SolverService(Log, Log);
        //    var solution = solver.Solve(recipes, resources);
        //    Assert.That(solution.State, Is.EqualTo(State.Optimal));
        //}

        //private (List<ModRecipeWithValue>, ModItemStack[]) LoadTestData(string fileName)
        //{
        //    var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        //    var testDirectory = Path.GetDirectoryName(assemblyLocation);
        //    var projectDirectory = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(testDirectory))));
        //    var jsonFilePath = Path.Combine(projectDirectory!, "Data", fileName);
        //    var jsonText = File.ReadAllText(jsonFilePath);
        //    var data = JsonSerializer.Deserialize<TestData>(jsonText);

        //    // Convert recipes
        //    var recipes = data.Recipes.Select(r => new ModRecipeWithValue(
        //        new ModItem(r.Item.RowId, r.Item.Name),
        //        r.Ingredients.ToDictionary(
        //            i => new ModItem(i.Item.RowId, i.Item.Name),
        //            i => (byte)i.Quantity
        //        ),
        //        r.Value,
        //        new ModItem(r.Currency.RowId, r.Currency.Name)
        //    )).ToList();

        //    // Convert resources
        //    var resources = data.Resources.Select(r =>
        //        new ModItemStack(new ModItem(r.Item.RowId, r.Item.Name), r.Item.RowId, r.Quantity)
        //    ).ToArray();

        //    return (recipes, resources);
        //}

        private record TestData(List<TestRecipe> Recipes, List<TestResource> Resources);

        private record TestRecipe(
            TestItem Item,
            List<TestIngredient> Ingredients,
            double Value,
            TestItem Currency
        );

        private record TestResource(TestItem Item, int Quantity);

        private record TestItem(uint RowId, string Name);

        private record TestIngredient(TestItem Item, int Quantity);
    }
}
