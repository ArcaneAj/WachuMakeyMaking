using SamplePlugin.Models;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace SamplePlugin.Services
{
    public class SolverService
    {
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

        public SolverService(RecipeCacheService recipeService)
        {
            this.state = State.Idle;
            this.recipeService = recipeService;
        }

        public State CurrentState { get { return this.state; } }

        internal void Solve(List<ModRecipeWithValue> selectedRecipes, List<int> baseCosts)
        {
            this.state = State.InProgress;
            var crystals = this.recipeService.GetCrystals();
            var items = this.recipeService.GetConsolidatedItems();
            var resources = crystals.Concat(items).ToArray();
            var constraints = resources.Select(x => x.Quantity).ToArray();
            // We slight wiggle the costs in order to prefer one over the other in case of a tie
            var costs = baseCosts.Select((int cost, int index) => cost + 0.001 * index).ToArray();
        }
    }
}
