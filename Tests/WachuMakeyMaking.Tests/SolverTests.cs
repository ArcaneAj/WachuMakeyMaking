using System.Collections.Generic;
using System.IO;
using System.Linq;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Services;
using Xunit;

namespace WachuMakeyMaking.Tests
{
    public class SolverTests
    {
        private const double Epsilon = 1e-6;
        private readonly ModItem gil = new(0, "Gil");
        private const int DecimalPlaces = 4;

        private static Dictionary<ModItem, ModItemWithValue> Wiggle(
            Dictionary<ModItem, ModItemWithValue> recipesWithValues
        )
        {
            // Add a tiny additive epsilon to each value based on its index to break ties deterministically.
            return recipesWithValues
                .ToList()
                .Select(
                    (kvp, index) =>
                    {
                        var x = kvp.Value;
                        return (Key: kvp.Key, Value: x with { Value = x.Value + (index + 1) * Epsilon });
                    }
                )
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        [Fact]
        public void CanUseSyntheticIntermediaries_WithNoneInInventory_CanChooseRecipe()
        {
            // Create items
            var ironOre = new ModItem(1, "Iron Ore");
            var coalOre = new ModItem(2, "Coal Ore");

            // Outputs (recipes) - three different result items
            var steelDagger = new ModItem(10, "Steel Dagger");
            var steelIngot = new ModItem(11, "Steel Ingot");

            // Values: steelDagger=50, steelIngot=10
            var steelDaggerValue = new ModItemWithValue(steelDagger, 50.0, gil);
            var steelIngotValue = new ModItemWithValue(steelIngot, 10.0, gil);

            var recipesWithValues = new Dictionary<ModItem, ModItemWithValue>()
            {
                [steelDagger] = steelDaggerValue,
                [steelIngot] = steelIngotValue,
            };

            var wiggled = Wiggle(recipesWithValues);

            // Build recipe variants: steelDagger consumes steelIngot for a large profit, steelIngot consumes 1IronOre+1CoalOre for a smaller profit
            var recipeA = new ModRecipe(
                100,
                steelDagger,
                1,
                new Dictionary<ModItem, byte> { [steelIngot] = 1 },
                0,
                0,
                0,
                0
            );
            var recipeB = new ModRecipe(
                101,
                steelIngot,
                1,
                new Dictionary<ModItem, byte> { [ironOre] = 1, [coalOre] = 1 },
                0,
                0,
                0,
                0
            );

            var fakeRecipeService = new FakeRecipeService()
            {
                SelectedIngredients = [new(ironOre, ironOre.RowId, 4), new(coalOre, coalOre.RowId, 5)],
            };
            fakeRecipeService.SetRecipesForOutput(steelDagger, [recipeA]);
            fakeRecipeService.SetRecipesForOutput(steelIngot, [recipeB]);

            // Create solver
            var solver = new SolverService(_ => { }, _ => { }, fakeRecipeService);

            var result = solver.Solve(recipesWithValues, wiggled);

            Assert.Equal(SolverService.State.Optimal, result.State);

            // Variables: [steelDagger, steelDagger, steelIngot]
            Assert.Equal(3, result.Values.Count);

            // Selling both ingredients is more valuable here (4 each) than crafting, so expect all sold
            Assert.Equal(0, result.Values[0].Quantity); // steelDagger via ingot
            Assert.Equal(4, result.Values[1].Quantity); // steelDagger via raw
            Assert.Equal(0, result.Values[2].Quantity); // steelIngot

            Assert.Equal(-200.0, result.OptimalValue, DecimalPlaces);
        }

        [Fact(Skip = "Temporarily disabled while fixing issues with ingredients")]
        public void IngredientsCanBeSold_WithRecipeC_ChooseBestCombination()
        {
            // Create items
            var itemA = new ModItem(1, "A");
            var itemB = new ModItem(2, "B");

            // Outputs (recipes) - three different result items
            var outputA = new ModItem(10, "recipeA");
            var outputB = new ModItem(11, "recipeB");
            var outputC = new ModItem(12, "recipeC");

            // Values: recipeA=5, recipeB=10; selling itemA and itemB = 4 each; recipeC=40
            var mvA = new ModItemWithValue(outputA, 5.0, gil);
            var mvB = new ModItemWithValue(outputB, 10.0, gil);
            var mvC = new ModItemWithValue(outputC, 40.0, gil);
            var sellA = new ModItemWithValue(itemA, 4.0, gil);
            var sellB = new ModItemWithValue(itemB, 4.0, gil);

            var recipesWithValues = new Dictionary<ModItem, ModItemWithValue>()
            {
                [outputA] = mvA,
                [outputB] = mvB,
                [outputC] = mvC,
                [itemA] = sellA,
                [itemB] = sellB,
            };

            var wiggled = Wiggle(recipesWithValues);

            // Build recipe variants: recipeA consumes 1A+1B, recipeB consumes 1A+2B, recipeC consumes 3A+3B
            var recipeA = new ModRecipe(
                100,
                outputA,
                1,
                new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 1 },
                0,
                0,
                0,
                0
            );
            var recipeB = new ModRecipe(
                101,
                outputB,
                1,
                new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 2 },
                0,
                0,
                0,
                0
            );
            var recipeC = new ModRecipe(
                102,
                outputC,
                1,
                new Dictionary<ModItem, byte> { [itemA] = 3, [itemB] = 3 },
                0,
                0,
                0,
                0
            );

            var fakeRecipeService = new FakeRecipeService()
            {
                SelectedIngredients = [new(itemA, itemA.RowId, 4), new(itemB, itemB.RowId, 5)],
            };
            fakeRecipeService.SetRecipesForOutput(outputA, [recipeA]);
            fakeRecipeService.SetRecipesForOutput(outputB, [recipeB]);
            fakeRecipeService.SetRecipesForOutput(outputC, [recipeC]);

            // Create solver
            var solver = new SolverService(_ => { }, _ => { }, fakeRecipeService);

            var result = solver.Solve(recipesWithValues, wiggled);

            Assert.Equal(SolverService.State.Optimal, result.State);

            // Variables: [recipeA, recipeB, sellA, sellB]
            Assert.Equal(5, result.Values.Count);

            // Selling both ingredients is more valuable here (4 each) than crafting, so expect all sold
            Assert.Equal(0.0, (double)result.Values[0].Quantity, DecimalPlaces); // recipeA
            Assert.Equal(0.0, (double)result.Values[1].Quantity, DecimalPlaces); // recipeB
            Assert.Equal(1.0, (double)result.Values[2].Quantity, DecimalPlaces); // recipeC
            Assert.Equal(1.0, (double)result.Values[3].Quantity, DecimalPlaces); // sellA
            Assert.Equal(2.0, (double)result.Values[4].Quantity, DecimalPlaces); // sellB

            Assert.Equal(-52.0, result.OptimalValue, DecimalPlaces);
        }

        [Fact]
        public void Solve_TwoRecipesSameIngredients_SelectsBest()
        {
            // Create items
            var itemA = new ModItem(1, "A");
            var itemB = new ModItem(2, "B");

            // Outputs (recipes) - two different result items
            var outputA = new ModItem(10, "recipeA");
            var outputB = new ModItem(11, "recipeB");

            // Values (currency is irrelevant for solver)
            var mvA = new ModItemWithValue(outputA, 5.0, gil);
            var mvB = new ModItemWithValue(outputB, 10.0, gil);

            var recipesWithValues = new Dictionary<ModItem, ModItemWithValue>() { [outputA] = mvA, [outputB] = mvB };

            var wiggled = Wiggle(recipesWithValues);

            // Build recipe variants: each recipe consumes 1 of A and 1 of B
            var recipeA = new ModRecipe(
                100,
                outputA,
                1,
                new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 1 },
                0,
                0,
                0,
                0
            );
            var recipeB = new ModRecipe(
                101,
                outputB,
                1,
                new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 2 },
                0,
                0,
                0,
                0
            );

            // Create a fake RecipeService implementation for testing
            var fakeRecipeService = new FakeRecipeService()
            {
                SelectedIngredients = [new(itemA, itemA.RowId, 4), new(itemB, itemB.RowId, 5)],
            };
            fakeRecipeService.SetRecipesForOutput(outputA, [recipeA]);
            fakeRecipeService.SetRecipesForOutput(outputB, [recipeB]);

            // Create solver
            var solver = new SolverService(_ => { }, _ => { }, fakeRecipeService);

            var result = solver.Solve(recipesWithValues, wiggled);

            Assert.Equal(SolverService.State.Optimal, result.State);

            // Because both recipes consume identical ingredients and B has higher value, expect all capacity used by B
            // Variables map to recipe variants in order of outputs passed in; ensure index mapping by checking counts
            Assert.Equal(2, result.Values.Count);
            Assert.Equal(1.0, (double)result.Values[0].Quantity, DecimalPlaces);
            Assert.Equal(2.0, (double)result.Values[1].Quantity, DecimalPlaces);
            Assert.Equal(-25, result.OptimalValue, DecimalPlaces);
        }

        [Fact(Skip = "Temporarily disabled while fixing issues with ingredients")]
        public void IngredientsCanBeSold_ChooseBestCombination()
        {
            // Create items
            var itemA = new ModItem(1, "A");
            var itemB = new ModItem(2, "B");

            // Outputs (recipes) - two different result items
            var outputA = new ModItem(10, "recipeA");
            var outputB = new ModItem(11, "recipeB");

            // Values: recipeA=5, recipeB=10; selling itemA and itemB = 4 each
            var mvA = new ModItemWithValue(outputA, 5.0, gil);
            var mvB = new ModItemWithValue(outputB, 10.0, gil);
            var sellA = new ModItemWithValue(itemA, 4.0, gil);
            var sellB = new ModItemWithValue(itemB, 4.0, gil);

            var recipesWithValues = new Dictionary<ModItem, ModItemWithValue>()
            {
                [outputA] = mvA,
                [outputB] = mvB,
                [itemA] = sellA,
                [itemB] = sellB,
            };

            var wiggled = Wiggle(recipesWithValues);

            // Build recipe variants: recipeA consumes 1A+1B, recipeB consumes 1A+2B
            var recipeA = new ModRecipe(
                100,
                outputA,
                1,
                new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 1 },
                0,
                0,
                0,
                0
            );
            var recipeB = new ModRecipe(
                101,
                outputB,
                1,
                new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 2 },
                0,
                0,
                0,
                0
            );

            var fakeRecipeService = new FakeRecipeService()
            {
                SelectedIngredients = [new(itemA, itemA.RowId, 4), new(itemB, itemB.RowId, 5)],
            };
            fakeRecipeService.SetRecipesForOutput(outputA, [recipeA]);
            fakeRecipeService.SetRecipesForOutput(outputB, [recipeB]);

            var solver = new SolverService(_ => { }, _ => { }, fakeRecipeService);

            var result = solver.Solve(recipesWithValues, wiggled);

            Assert.Equal(SolverService.State.Optimal, result.State);

            // Variables: [recipeA, recipeB, sellA, sellB]
            Assert.Equal(4, result.Values.Count);
            // Selling both ingredients is more valuable here (4 each) than crafting, so expect all sold
            Assert.Equal(0.0, (double)result.Values[0].Quantity, DecimalPlaces); // recipeA
            Assert.Equal(0.0, (double)result.Values[1].Quantity, DecimalPlaces); // recipeB
            Assert.Equal(4.0, (double)result.Values[2].Quantity, DecimalPlaces); // sellA
            Assert.Equal(5.0, (double)result.Values[3].Quantity, DecimalPlaces); // sellB

            Assert.Equal(-36.0, result.OptimalValue, DecimalPlaces);
        }

        [Fact]
        public void GetFlattenedRecipes_CraftedIngredient_ReturnsFlattenedRecipes()
        {
            // Create items
            var ironOre = new ModItem(1, "IronOre");

            // Outputs (recipes) - two different result items
            var ironDagger = new ModItem(10, "IronDagger");
            var ironIngot = new ModItem(11, "IronIngot");

            // Build recipe variants: recipeA consumes 1A+1B, recipeB consumes 1A+2B
            var ironDaggerRecipe = new ModRecipe(
                100,
                ironDagger,
                1,
                new Dictionary<ModItem, byte> { [ironIngot] = 1 },
                0,
                0,
                0,
                0
            );
            var ironIngotRecipe = new ModRecipe(
                101,
                ironIngot,
                1,
                new Dictionary<ModItem, byte> { [ironOre] = 1 },
                0,
                0,
                0,
                0
            );

            var fakeRecipeService = new FakeRecipeService() { };
            fakeRecipeService.SetRecipesForOutput(ironDagger, [ironDaggerRecipe]);
            fakeRecipeService.SetRecipesForOutput(ironIngot, [ironIngotRecipe]);

            var solver = new SolverService(_ => { }, _ => { }, fakeRecipeService);
            var recipes = solver.GetFlattenedRecipes(ironDaggerRecipe);
            Assert.Equal(2, recipes.Count);
            Assert.Equal(11, (int)recipes[0].Ingredients.Keys.First().RowId);
            Assert.Equal(1, (int)recipes[1].Ingredients.Keys.First().RowId);
        }

        [Fact]
        public void GetFlattenedRecipes_MultipleCraftedIngredient_ReturnsFlattenedRecipes()
        {
            // Create items
            var ironOre = new ModItem(1, "IronOre");
            var leather = new ModItem(2, "Leather");

            // Outputs (recipes) - two different result items
            var ironDagger = new ModItem(10, "IronDagger");
            var ironIngot = new ModItem(11, "IronIngot");
            var leatherHandle = new ModItem(12, "LeatherHandle");

            // Build recipe variants: dagger needs ingot and handle, ingot needs ore, handle needs leather
            var ironDaggerRecipe = new ModRecipe(
                100,
                ironDagger,
                1,
                new Dictionary<ModItem, byte> { [ironIngot] = 1, [leatherHandle] = 1 },
                0,
                0,
                0,
                0
            );
            var ironIngotRecipe = new ModRecipe(
                101,
                ironIngot,
                1,
                new Dictionary<ModItem, byte> { [ironOre] = 1 },
                0,
                0,
                0,
                0
            );
            var leatherHandleRecipe = new ModRecipe(
                102,
                leatherHandle,
                1,
                new Dictionary<ModItem, byte> { [leather] = 1 },
                0,
                0,
                0,
                0
            );

            var fakeRecipeService = new FakeRecipeService() { };
            fakeRecipeService.SetRecipesForOutput(ironDagger, [ironDaggerRecipe]);
            fakeRecipeService.SetRecipesForOutput(ironIngot, [ironIngotRecipe]);
            fakeRecipeService.SetRecipesForOutput(leatherHandle, [leatherHandleRecipe]);

            var solver = new SolverService(_ => { }, _ => { }, fakeRecipeService);
            var recipes = solver.GetFlattenedRecipes(ironDaggerRecipe);

            var ironArray = new List<uint>() { ironIngot.RowId, ironOre.RowId };
            var leatherArray = new List<uint>() { leatherHandle.RowId, leather.RowId };

            var product = (from x in ironArray from y in leatherArray select (x, y)).ToList();

            Assert.Equal(product.Count, recipes.Count);
            foreach (var (set, index) in product.Select((p, i) => (p, i)))
            {
                Assert.Equal((int)set.x, (int)recipes[index].Ingredients.Keys.ToList()[0].RowId);
                Assert.Equal((int)set.y, (int)recipes[index].Ingredients.Keys.ToList()[1].RowId);
            }
        }
    }
}
