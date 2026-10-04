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

        private static Dictionary<ModItem, ModItemWithValue> Wiggle(Dictionary<ModItem, ModItemWithValue> recipesWithValues)
        {
            // Add a tiny additive epsilon to each value based on its index to break ties deterministically.
            return recipesWithValues.ToList()
                .Select((kvp, index) =>
                {
                    var x = kvp.Value;
                    return (Key: kvp.Key, Value: x with { Value = x.Value + (index + 1) * Epsilon });
                })
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        [Fact]
        public void CanUseSyntheticIntermediaries_WithNoneInInventory_CanChooseRecipe()
        {
            // Create items
            var itemA = new ModItem(1, "A");
            var itemB = new ModItem(2, "B");
            var itemC = new ModItem(3, "C");

            // Outputs (recipes) - three different result items
            var outputA = new ModItem(10, "recipeA");
            var outputB = new ModItem(11, "recipeB");

            // Values: recipeA=5, recipeB=10; selling itemA and itemB = 4 each; recipeC=40
            var mvA = new ModItemWithValue(outputA, 50.0, gil);
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

            // Build recipe variants: recipeA consumes outputB for a large profit, recipeB consumes 1A+1B for a smaller profit
            var recipeA = new ModRecipe(100, outputA, 1, new Dictionary<ModItem, byte> { [outputB] = 1 }, 0, 0, 0, 0);
            var recipeB = new ModRecipe(101, outputB, 1, new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 1 }, 0, 0, 0, 0);

            var fakeRecipeService = new FakeRecipeService()
            {
                SelectedIngredients =
                [
                    new(itemA, itemA.RowId, 4),
                    new(itemB, itemB.RowId, 5),
                ]
            };
            fakeRecipeService.SetRecipesForOutput(outputA, [recipeA]);
            fakeRecipeService.SetRecipesForOutput(outputB, [recipeB]);

            // Create solver
            var solver = new SolverService(_ => { }, _ => { }, fakeRecipeService);

            var result = solver.Solve(recipesWithValues, wiggled);

            Assert.Equal(SolverService.State.Optimal, result.State);

            // Variables: [recipeA, recipeB, sellA, sellB]
            Assert.Equal(4, result.Values.Count);

            // Selling both ingredients is more valuable here (4 each) than crafting, so expect all sold
            Assert.Equal(4.0, (double)result.Values[0].Quantity, DecimalPlaces); // recipeA
            Assert.Equal(0.0, (double)result.Values[1].Quantity, DecimalPlaces); // recipeB
            Assert.Equal(0.0, (double)result.Values[2].Quantity, DecimalPlaces); // sellA
            Assert.Equal(1.0, (double)result.Values[3].Quantity, DecimalPlaces); // sellB

            Assert.Equal(-204.0, result.OptimalValue, DecimalPlaces);
        }

        [Fact]
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
            var recipeA = new ModRecipe(100, outputA, 1, new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 1 }, 0, 0, 0, 0);
            var recipeB = new ModRecipe(101, outputB, 1, new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 2 }, 0, 0, 0, 0);
            var recipeC = new ModRecipe(102, outputC, 1, new Dictionary<ModItem, byte> { [itemA] = 3, [itemB] = 3 }, 0, 0, 0, 0);

            var fakeRecipeService = new FakeRecipeService()
            {
                SelectedIngredients =
                [
                    new(itemA, itemA.RowId, 4),
                    new(itemB, itemB.RowId, 5),
                ]
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
        public void TwoRecipes_SameIngredients_SelectsBest()
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

            var recipesWithValues = new Dictionary<ModItem, ModItemWithValue>()
            {
                [outputA] = mvA,
                [outputB] = mvB,
            };

            var wiggled = Wiggle(recipesWithValues);

            // Build recipe variants: each recipe consumes 1 of A and 1 of B
            var recipeA = new ModRecipe(100, outputA, 1, new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 1 }, 0, 0, 0, 0);
            var recipeB = new ModRecipe(101, outputB, 1, new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 2 }, 0, 0, 0, 0);

            // Create a fake RecipeService implementation for testing
            var fakeRecipeService = new FakeRecipeService()
            {
                SelectedIngredients =
                [
                    new(itemA, itemA.RowId, 4),
                    new(itemB, itemB.RowId, 5),
                ]
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

        [Fact]
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
            var recipeA = new ModRecipe(100, outputA, 1, new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 1 }, 0, 0, 0, 0);
            var recipeB = new ModRecipe(101, outputB, 1, new Dictionary<ModItem, byte> { [itemA] = 1, [itemB] = 2 }, 0, 0, 0, 0);

            var fakeRecipeService = new FakeRecipeService()
            {
                SelectedIngredients =
                [
                    new(itemA, itemA.RowId, 4),
                    new(itemB, itemB.RowId, 5),
                ]
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
    }
}
