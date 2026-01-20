using SamplePlugin.Models;
using System.Collections.Generic;
using System.Linq;

namespace SamplePlugin.Services
{
    public class SolverService
    {
        private readonly Plugin plugin;
        private State state;
        private RecipeCacheService recipeService;

        public enum State
        {
            Idle,
            InProgress,
            Optimal,
            Unbounded,
            Error
        }

        public SolverService(Plugin plugin, RecipeCacheService recipeService)
        {
            this.plugin = plugin;
            this.state = State.Idle;
            this.recipeService = recipeService;
        }

        public State CurrentState { get { return this.state; } }

        internal void Solve(List<ModRecipeWithValue> recipes)
        {
            this.state = State.InProgress;
            var crystals = this.recipeService.GetCrystals();
            var items = this.recipeService.GetConsolidatedItems();
            var resources = crystals.Concat(items).ToArray();
            var usedResources = resources.Where(x => recipes.Any(y => y.Ingredients.ContainsKey(x.Item)));


            var costs = recipes.Select(x => x.Value).ToArray();

            var constraints = new List<int>();
            var assignments = new List<int[]>();

            foreach (var resource in usedResources)
            {
                Plugin.Log.Info($"{resource.Item.Name}");
                constraints.Add(resource.Quantity);
                var row = recipes.Select(recipe => (int)recipe.Ingredients.GetValueOrDefault(resource.Item)).ToArray();
                assignments.Add(row);

                var recipesUsingResource = recipes.Where(recipe => recipe.Ingredients.ContainsKey(resource.Item)).ToList();
                foreach (var rur in recipesUsingResource)
                {
                    Plugin.Log.Info($"    {rur.Item.Name}");
                }
            }
        }

        //private (List<double> solution, double optimalValue, State state) Solve()
        //{

        //}
    }
}
