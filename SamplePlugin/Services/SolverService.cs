using Lumina.Excel.Sheets;
using SamplePlugin.Models;
using Sonnet;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

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

        internal void Solve(List<ModRecipeWithValue> selectedRecipes)
        {
            this.state = State.InProgress;
            var crystals = this.recipeService.GetCrystals();
            var items = this.recipeService.GetConsolidatedItems();
            var resources = crystals.Concat(items).ToArray();
            var recipes = selectedRecipes.Select(x => new RecipeAssignment(x.Value, x.Item, x.Ingredients));

            var model = new Model();

            model.Name = "RecipeChoice";
            model.Objective = recipes.Sum(a => a.Cost * a.Assign);

            foreach (var resource in resources)
            {
                Plugin.Log.Info($"{resource.Item.Name}");
                var recipesUsingResource = recipes.Where(recipe => recipe.Ingredients.ContainsKey(resource.Item)).ToList();
                foreach (var rur in recipesUsingResource)
                {
                    Plugin.Log.Info($"    {rur.Item.Name}");
                }
                model.Add($"Availability[{resource.Item.Name}]", recipesUsingResource.Sum(a => a.Ingredients[resource.Item] * a.Assign) <= resource.Quantity);
            }
        }
    }

    public class RecipeAssignment(double cost, Item item, Dictionary<Item, byte> ingredients)
    {
        public double Cost { get; private set; } = cost;
        public Item Item { get; } = item;
        public Dictionary<Item, byte> Ingredients { get; } = ingredients;
        public Variable Assign { get; private set; } = default!;
    }
}
