using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Utils;

namespace WachuMakeyMaking.Services
{
    public class SolverService(Action<string> log, Action<string> logError, IRecipeService recipeService)
    {
        private readonly Action<string> log = log;
        private readonly Action<string> logError = logError;
        private readonly IRecipeService recipeService = recipeService;
        private readonly List<Action<State, string, Solution?>> progressListeners = [];
        private State state = State.Idle;
        private Solution currentBest = null!;
        private Dictionary<int, ModRecipe> variableIndexToRecipe = [];
        private double lowerBound = 0.0;
        private string progressMessage = string.Empty;
        private CancellationTokenSource cancellationTokenSource = new();
        private readonly HashSet<string> infeasibleBranchSignatures = new();

        // Last raw LP tuple result from RevisedSimplex for diagnostics (status, full x, optimalValue, basis)
        private static (string status, double[] x, double optimalValue, int[] basis) LastLpTuple = (
            "",
            Array.Empty<double>(),
            0.0,
            Array.Empty<int>()
        );
        private static string LastBasisDiagnostics = string.Empty;

        public Dictionary<ModItem, ModItemWithValue> ItemsWithValues { get; private set; } = [];
        public Dictionary<ModItem, ModItemWithValue> WiggledItemsWithValues { get; private set; } = [];

        public enum State
        {
            Idle,
            FindingInitialSolution,
            Optimising,
            Finished,
            Optimal,
            Unbounded,
            Error,
        }

        public void RegisterProgressListener(Action<State, string, Solution?> listener)
        {
            this.progressListeners.Add(listener);
        }

        public void Reset()
        {
            this.cancellationTokenSource.Cancel();
            this.state = State.Idle;
            this.currentBest = null!;
            this.lowerBound = 0.0;
            this.progressMessage = string.Empty;
            UpdateProgress(State.Idle, string.Empty, null);
        }

        private void UpdateProgress(State newState, string message, Solution? solution = null)
        {
            this.state = newState;
            this.progressMessage = message;
            foreach (var listener in this.progressListeners)
            {
                listener(newState, message, solution);
            }
        }

        public Solution Solve(
            Dictionary<ModItem, ModItemWithValue> itemsWithValues,
            Dictionary<ModItem, ModItemWithValue> wiggledItemsWithValues
        )
        {
            this.ItemsWithValues = itemsWithValues;
            this.WiggledItemsWithValues = wiggledItemsWithValues;

            try
            {
                var outputs = this.WiggledItemsWithValues.Values.ToList();
                var recipes = new List<ModRecipeWithValue>();

                var resources = this.recipeService.SelectedIngredients ?? [];
                foreach (var output in outputs)
                {
                    var serviceRecipes = this.recipeService.GetRecipesByOutputTestable(output.Item) ?? [];
                    var value = output.Value;
                    var currency = output.Currency;

                    if (serviceRecipes.Count > 0)
                    {
                        foreach (var r in serviceRecipes)
                        {
                            var flattenedRecipes = GetFlattenedRecipes(r);
                            recipes.AddRange(
                                flattenedRecipes.Select(fr => new ModRecipeWithValue(fr, value, currency))
                            );
                        }
                    }
                    else
                    {
                        if (FeatureFlags.AllowSellingIngredients)
                        {
                            // If the wiggled output has no service recipes, add a synthetic self-recipe so it can be selected.
                            var synthetic = new ModRecipe(
                                0,
                                output.Item,
                                1,
                                new Dictionary<ModItem, byte> { [output.Item] = 1 },
                                0,
                                0,
                                0,
                                0
                            );
                            recipes.Add(new ModRecipeWithValue(synthetic, value, currency));
                        }
                    }
                }

                // Track recipe indices so we can reconstruct solution output later
                this.variableIndexToRecipe = [];
                for (var i = 0; i < recipes.Count; i++)
                {
                    this.variableIndexToRecipe[i] = recipes[i];
                }

                if (recipes.Count == 0)
                {
                    Reset();
                    return new Solution([], 0, State.Error, []);
                }

                this.cancellationTokenSource = new();
                var cancellationToken = this.cancellationTokenSource.Token;

                this.currentBest = null!;
                UpdateProgress(State.FindingInitialSolution, "Finding initial solution...");

                var usedResources = resources.Where(x => recipes.Any(y => y.Ingredients.ContainsKey(x.Item)));
                var resourcesWeDontHave = recipes
                    .SelectMany(x => x.Ingredients.Keys)
                    .Where(x => !resources.Any(r => r.Item.RowId == x.RowId))
                    .Distinct();

                var costs = recipes.Select(x => -x.Value * x.Number).ToArray();

                var constraintsList = new List<int>();
                var assignmentsList = new List<int[]>();

                foreach (var resource in usedResources)
                {
                    constraintsList.Add(resource.Quantity);
                    var row = recipes.Select(recipe => (int)recipe.Ingredients.GetValueOrDefault(resource.Item));
                    assignmentsList.Add([.. row]);
                }

                foreach (var resource in resourcesWeDontHave)
                {
                    constraintsList.Add(0);
                    var row = recipes.Select(recipe => (int)recipe.Ingredients.GetValueOrDefault(resource));
                    assignmentsList.Add([.. row]);
                }

                var branches = new Stack<Branch>();
                var problem = new Problem([.. assignmentsList], costs, [.. constraintsList]);
                var result = Solve(problem, branches, cancellationToken);
                if (result.State == State.Unbounded)
                {
                    this.log("Problem is unbounded - no optimal solution exists.");
                    UpdateProgress(State.Unbounded, "Problem is unbounded - no optimal solution exists.");
                    return new Solution([], 0, State.Error, []);
                }
                if (result.State != State.Optimal)
                {
                    this.log("Unknown error occurred when finding initial solution.");
                    UpdateProgress(State.Error, "Unknown error occurred when finding initial solution.");
                    return new Solution([], 0, State.Error, []);
                }

                this.lowerBound = result.OptimalValue;
                UpdateProgress(State.Optimising, "Optimising...");
                BranchAndBound(problem, result, cancellationToken);

                if (this.currentBest == null)
                {
                    UpdateProgress(State.Error, "Unable to find integral solution");
                    return new Solution([], 0, State.Error, []);
                }

                if (this.currentBest.State == State.Optimal)
                {
                    UpdateProgress(State.Finished, "Finished", this.currentBest);
                }
                else
                {
                    UpdateProgress(State.Error, "No optimal solution found.");
                }
            }
            catch (OperationCanceledException)
            {
                // Quietly accept that the operation was cancelled
            }

            return this.currentBest;
        }

        public List<ModRecipe> GetFlattenedRecipes(ModRecipe recipe)
        {
            var combinations = GetRecipeCombinations(recipe);

            return
            [
                .. combinations.Select(dict =>
                {
                    var ingredients = dict.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (byte)Math.Min(byte.MaxValue, kvp.Value)
                    );

                    // Construct the new recipe variant with the flattened ingredients
                    // Adjust parameters to match your ModRecipe constructor definition
                    return recipe with
                    {
                        Ingredients = ingredients,
                    }; //new ModRecipe(recipe.RowId, recipe.Item, recipe.Number, ingredients, recipe.classJobLevel, recipe.classJobId, recipe.book, recipe.noteBookDivisionId);
                }),
            ];
        }

        private List<Dictionary<ModItem, byte>> GetRecipeCombinations(ModRecipe recipe)
        {
            var allIngredientOptions = new List<List<Dictionary<ModItem, byte>>>();

            // Collect all branching options for each ingredient in this recipe
            foreach (var (ingredient, amountRequired) in recipe.Ingredients)
            {
                var optionsForThisIngredient = GetIngredientOptions(ingredient, amountRequired);
                allIngredientOptions.Add(optionsForThisIngredient);
            }

            // Combine options across all ingredients via Cartesian Product
            return CartesianProduct(allIngredientOptions);
        }

        private List<Dictionary<ModItem, byte>> GetIngredientOptions(ModItem item, byte amountRequired)
        {
            var options = new List<Dictionary<ModItem, byte>>
            {
                // Option 1: Keep the ingredient as-is
                new() { [item] = amountRequired },
            };

            // Option 2: Expand via sub-recipes
            var serviceRecipes = this.recipeService.GetRecipesByOutputTestable(item) ?? [];
            foreach (var subRecipe in serviceRecipes)
            {
                // Calculate crafting cycles required based on the sub-recipe's yield (Number).
                // e.g., requiring 3 ingots from a recipe with yield 2 requires ceil(3/2) = 2 crafts.
                var craftsNeeded =
                    subRecipe.Number > 0
                        ? (int)Math.Ceiling((double)amountRequired / subRecipe.Number)
                        : amountRequired;

                // Recursive expansion (no cycle protection needed)
                var subCombinations = GetRecipeCombinations(subRecipe);

                foreach (var subCombo in subCombinations)
                {
                    var scaledCombo = new Dictionary<ModItem, byte>();
                    foreach (var (subItem, subAmount) in subCombo)
                    {
                        scaledCombo[subItem] = (byte)(subAmount * craftsNeeded);
                    }

                    options.Add(scaledCombo);
                }
            }

            return options;
        }

        private static List<Dictionary<ModItem, byte>> CartesianProduct(
            List<List<Dictionary<ModItem, byte>>> optionsPerIngredient
        )
        {
            var result = new List<Dictionary<ModItem, byte>> { new() };

            foreach (var ingredientOptions in optionsPerIngredient)
            {
                var nextResult = new List<Dictionary<ModItem, byte>>();

                foreach (var currentDict in result)
                {
                    foreach (var optionDict in ingredientOptions)
                    {
                        var combined = new Dictionary<ModItem, byte>(currentDict);

                        foreach (var (item, count) in optionDict)
                        {
                            combined[item] = (byte)(combined.GetValueOrDefault(item, (byte)0) + count);
                        }

                        nextResult.Add(combined);
                    }
                }

                result = nextResult;
            }

            return result;
        }

        private bool BranchAndBound(
            Problem problem,
            ContinuousSolution previousResult,
            CancellationToken cancellationToken
        )
        {
            var valuesToBranch = previousResult
                .Values.Select((val, index) => (val, index))
                .Where(x => x.val - Math.Floor(x.val) > 1e-6)
                .ToList();

            // If we're integral, check if this is the best solution so far
            if (valuesToBranch.Count == 0)
            {
                if (previousResult.OptimalValue < (this.currentBest?.OptimalValue ?? 0.0))
                {
                    // Convert continuous integral result to discrete Solution mapping variables to ModRecipeStacks
                    var stacks = new List<ModRecipeStack>();
                    for (var i = 0; i < previousResult.Values.Count; i++)
                    {
                        var qty = (int)Math.Round(previousResult.Values[i]);
                        var recipe = this.variableIndexToRecipe.GetValueOrDefault(i);
                        if (recipe != null)
                        {
                            stacks.Add(new ModRecipeStack(recipe, recipe.RowId, qty));
                        }
                    }
                    this.currentBest = new Solution(
                        stacks,
                        previousResult.OptimalValue,
                        State.Optimal,
                        previousResult.Branches
                    );
                    if (this.state == State.FindingInitialSolution)
                    {
                        UpdateProgress(State.Optimising, "Optimising...", this.currentBest);
                    }
                    else
                    {
                        UpdateProgress(State.Optimising, this.progressMessage, this.currentBest);
                    }
                }
                return Math.Abs(previousResult.OptimalValue - this.lowerBound) < 1e-6;
            }

            // Branch on the least fractional variable (closest to an integer)
            var branchVar = valuesToBranch
                .OrderBy(x =>
                {
                    var frac = x.val - Math.Floor(x.val);
                    return Math.Min(frac, 1 - frac); // Distance to nearest integer
                })
                .First();

            var floorVal = (int)Math.Floor(branchVar.val);
            var ceilVal = (int)Math.Ceiling(branchVar.val);

            // Create positive branch: x[index] <= floor(val)
            var positiveBranch = new Branch(branchVar.index, floorVal, true);
            var positiveBranches = new Stack<Branch>(previousResult.Branches.Reverse());
            positiveBranches.Push(positiveBranch);

            // Build signature for this branch stack to avoid re-exploring provably infeasible branches
            // Use a canonical representation (sort by index and sign) so the same set of constraints
            // has the same signature regardless of push order.
            static string BuildSignature(Stack<Branch> s)
            {
                var arr = s.ToArray(); // top-first
                var normalized = arr.Select(b => (Index: b.Index, IsPos: b.IsPositive ? 'P' : 'N', Value: b.Value))
                    .OrderBy(t => t.Index)
                    .ThenBy(t => t.IsPos)
                    .ThenBy(t => t.Value)
                    .Select(t => $"{t.Index}:{t.IsPos}:{t.Value}");
                return string.Join("|", normalized);
            }
            var posSig = BuildSignature(positiveBranches);
            if (this.infeasibleBranchSignatures.Contains(posSig))
            {
                this.log($"Skipping previously infeasible branch: {posSig}");
            }
            else
            {
                var positiveResult = Solve(problem, positiveBranches, cancellationToken);
                // Runtime verification: rebuild the full constraint matrix used by Solve and verify all
                // appended branch rows are satisfied by the returned continuous solution. Log details
                // if any violation is detected so we can decide whether the LP returned an invalid
                // solution or the branch wasn't appended correctly.
                void VerifyBranchRows(Stack<Branch> branchStack, ContinuousSolution sol, string which)
                {
                    try
                    {
                        var origM = problem.Assignments.Length;
                        var nVars = problem.Assignments[0].Length;
                        if (sol?.Values == null || sol.Values.Count < nVars)
                        {
                            this.log(
                                $"[Verify] Skipping {which} branch verification: solution has {sol?.Values?.Count ?? 0} vars, expected {nVars}"
                            );
                            return;
                        }
                        var branchConstraintsLocal = new List<int>();
                        var branchAssignmentsLocal = new List<int[]>();
                        foreach (var b in branchStack)
                        {
                            var coeff = b.IsPositive ? 1 : -1;
                            var row = new int[nVars];
                            row[b.Index] = coeff;
                            branchConstraintsLocal.Add(b.Value * coeff);
                            branchAssignmentsLocal.Add(row);
                        }

                        var fullConstraintsLocal = problem.Constraints.Concat(branchConstraintsLocal).ToArray();
                        var fullAssignmentsLocal = problem.Assignments.Concat(branchAssignmentsLocal).ToArray();

                        var tol = 1e-6;
                        for (var k = 0; k < branchAssignmentsLocal.Count; k++)
                        {
                            var rowIndex = origM + k;
                            double lhs = 0.0;
                            var coeffRow = fullAssignmentsLocal[rowIndex];
                            for (var j = 0; j < nVars; j++)
                            {
                                lhs += coeffRow[j] * sol.Values[j];
                            }
                            var rhs = fullConstraintsLocal[rowIndex];
                            if (lhs > rhs + tol)
                            {
                                // Log detailed diagnostics
                                this.log(
                                    $"[Verify] {which} branch row violated: row={rowIndex} lhs={lhs} rhs={rhs} tol={tol}"
                                );
                                this.log($"[Verify] Branch signature={BuildSignature(branchStack)}");
                                this.log(
                                    $"[Verify] Row coeffs: {string.Join(",", coeffRow.Select(v => v.ToString()))}"
                                );
                                this.log(
                                    $"[Verify] Var values (sample first 20): {string.Join(",", sol.Values.Take(Math.Min(20, sol.Values.Count)).Select(v => v.ToString()))}"
                                );
                                try
                                {
                                    var basis = LastLpTuple.basis;
                                    var rawX = LastLpTuple.x;
                                    if (basis != null && basis.Length > 0)
                                    {
                                        this.log(
                                            $"[Verify] LP basis (sample first 40): {string.Join(",", basis.Take(Math.Min(40, basis.Length)))}"
                                        );
                                    }
                                    if (rawX != null && rawX.Length > 0)
                                    {
                                        this.log(
                                            $"[Verify] LP raw x (sample first 40): {string.Join(",", rawX.Take(Math.Min(40, rawX.Length)).Select(v => v.ToString()))}"
                                        );
                                    }
                                }
                                catch (Exception ex)
                                {
                                    this.logError($"[Verify] Failed to log LP internals: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        this.logError($"[Verify] Failed to verify branch rows: {ex.Message}");
                    }
                }

                if (FeatureFlags.VerifyBranchRows)
                {
                    VerifyBranchRows(positiveBranches, positiveResult, "positive");
                }

                // If the LP failed or is not optimal, treat this branch stack as infeasible to avoid retrying
                if (positiveResult.State != State.Optimal)
                {
                    this.infeasibleBranchSignatures.Add(posSig);
                    this.log($"Branch not optimal (state={positiveResult.State}), marking infeasible: {posSig}");
                }
                else
                {
                    // Verify the solution satisfies the branch constraint
                    var feasible = positiveResult.Values[branchVar.index] <= floorVal + 1e-6;
                    if (!feasible)
                    {
                        this.log(
                            $"Branch constraint violated: x[{branchVar.index}] = {positiveResult.Values[branchVar.index]} > {floorVal}, skipping"
                        );
                        // Mark this branch signature as infeasible to avoid re-exploration
                        this.infeasibleBranchSignatures.Add(posSig);
                    }
                    else
                    {
                        // Only explore further if this could be better than current best
                        if (this.currentBest == null || positiveResult.OptimalValue < this.currentBest.OptimalValue)
                        {
                            if (this.currentBest != null)
                            {
                                var message =
                                    $"Optimising... Current best: {-Math.Floor(this.currentBest.OptimalValue)} gil with Upper bound: {-Math.Floor(this.lowerBound)}";
                                this.progressMessage = message;
                                UpdateProgress(State.Optimising, message, this.currentBest);
                            }
                            if (BranchAndBound(problem, positiveResult, cancellationToken))
                                return true;
                        }
                    }
                }
            }

            // Create negative branch: x[index] >= ceil(val)
            var negativeBranch = new Branch(branchVar.index, ceilVal, false);
            var negativeBranches = new Stack<Branch>(previousResult.Branches.Reverse());
            negativeBranches.Push(negativeBranch);
            var negSig = BuildSignature(negativeBranches);
            if (this.infeasibleBranchSignatures.Contains(negSig))
            {
                this.log($"Skipping previously infeasible branch: {negSig}");
            }
            else
            {
                var negativeResult = Solve(problem, negativeBranches, cancellationToken);
                // Verify branch rows for negative branch stack as well
                void VerifyBranchRowsNeg(Stack<Branch> branchStack, ContinuousSolution sol, string which)
                {
                    try
                    {
                        var origM = problem.Assignments.Length;
                        var nVars = problem.Assignments[0].Length;
                        if (sol?.Values == null || sol.Values.Count < nVars)
                        {
                            this.log(
                                $"[Verify] Skipping {which} branch verification: solution has {sol?.Values?.Count ?? 0} vars, expected {nVars}"
                            );
                            return;
                        }
                        var branchConstraintsLocal = new List<int>();
                        var branchAssignmentsLocal = new List<int[]>();
                        foreach (var b in branchStack)
                        {
                            var coeff = b.IsPositive ? 1 : -1;
                            var row = new int[nVars];
                            row[b.Index] = coeff;
                            branchConstraintsLocal.Add(b.Value * coeff);
                            branchAssignmentsLocal.Add(row);
                        }

                        var fullConstraintsLocal = problem.Constraints.Concat(branchConstraintsLocal).ToArray();
                        var fullAssignmentsLocal = problem.Assignments.Concat(branchAssignmentsLocal).ToArray();

                        var tol = 1e-6;
                        for (var k = 0; k < branchAssignmentsLocal.Count; k++)
                        {
                            var rowIndex = origM + k;
                            double lhs = 0.0;
                            var coeffRow = fullAssignmentsLocal[rowIndex];
                            for (var j = 0; j < nVars; j++)
                            {
                                lhs += coeffRow[j] * sol.Values[j];
                            }
                            var rhs = fullConstraintsLocal[rowIndex];
                            if (lhs > rhs + tol)
                            {
                                this.log(
                                    $"[Verify] {which} branch row violated: row={rowIndex} lhs={lhs} rhs={rhs} tol={tol}"
                                );
                                this.log($"[Verify] Branch signature={BuildSignature(branchStack)}");
                                this.log(
                                    $"[Verify] Row coeffs: {string.Join(",", coeffRow.Select(v => v.ToString()))}"
                                );
                                this.log(
                                    $"[Verify] Var values (sample first 20): {string.Join(",", sol.Values.Take(Math.Min(20, sol.Values.Count)).Select(v => v.ToString()))}"
                                );
                                try
                                {
                                    var basis = LastLpTuple.basis;
                                    var rawX = LastLpTuple.x;
                                    if (basis != null && basis.Length > 0)
                                    {
                                        this.log(
                                            $"[Verify] LP basis (sample first 40): {string.Join(",", basis.Take(Math.Min(40, basis.Length)))}"
                                        );
                                    }
                                    if (rawX != null && rawX.Length > 0)
                                    {
                                        this.log(
                                            $"[Verify] LP raw x (sample first 40): {string.Join(",", rawX.Take(Math.Min(40, rawX.Length)).Select(v => v.ToString()))}"
                                        );
                                    }
                                }
                                catch (Exception ex)
                                {
                                    this.logError($"[Verify] Failed to log LP internals: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        this.logError($"[Verify] Failed to verify branch rows: {ex.Message}");
                    }
                }

                if (FeatureFlags.VerifyBranchRows)
                {
                    VerifyBranchRowsNeg(negativeBranches, negativeResult, "negative");
                }

                if (negativeResult.State == State.Optimal)
                {
                    // Verify the solution satisfies the branch constraint
                    var feasible = negativeResult.Values[branchVar.index] >= ceilVal - 1e-6;
                    if (!feasible)
                    {
                        this.log(
                            $"Branch constraint violated: x[{branchVar.index}] = {negativeResult.Values[branchVar.index]} < {ceilVal}, skipping"
                        );
                        this.infeasibleBranchSignatures.Add(negSig);
                    }
                    else
                    {
                        // Only explore further if this could be better than current best
                        if (this.currentBest == null || negativeResult.OptimalValue < this.currentBest.OptimalValue)
                        {
                            if (this.currentBest != null)
                            {
                                var message =
                                    $"Optimising... Current best: {-Math.Floor(this.currentBest.OptimalValue)} gil with Upper bound: {-Math.Floor(this.lowerBound)}";
                                this.progressMessage = message;
                                UpdateProgress(State.Optimising, message, this.currentBest);
                            }
                            if (BranchAndBound(problem, negativeResult, cancellationToken))
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        private static ContinuousSolution Solve(
            Problem problem,
            Stack<Branch> branches,
            CancellationToken cancellationToken
        )
        {
            try
            {
                // Validate input dimensions
                if (
                    problem.Assignments.Length == 0
                    || problem.Assignments[0].Length == 0
                    || problem.Assignments.Any(x => x.Length != problem.Assignments[0].Length)
                )
                {
                    return new ContinuousSolution(new List<double>(), 0, State.Error, branches);
                }

                var m = problem.Assignments.Length; // number of constraints (resources)
                var n = problem.Assignments[0].Length; // number of variables (recipes)

                if (problem.Costs.Length != n || problem.Constraints.Length != m)
                {
                    return new ContinuousSolution(new List<double>(), 0, State.Error, branches);
                }

                var branchConstraints = new List<int>();
                var branchAssignments = new List<int[]>();

                foreach (var branch in branches)
                {
                    var coeff = branch.IsPositive ? 1 : -1;
                    var row = new int[problem.Costs.Length];
                    row[branch.Index] = coeff;
                    branchConstraints.Add(branch.Value * coeff);
                    branchAssignments.Add([.. row]);
                }

                // Append branch constraints to create the full constraints array
                var fullConstraints = problem.Constraints.Concat(branchConstraints).ToArray();

                // Append branch assignments to create the full assignments array
                var fullAssignments = problem.Assignments.Concat(branchAssignments).ToArray();

                // Update m to include branch constraints
                m = fullConstraints.Length;

                // Convert to standard form: Ax ≤ b becomes Ax + s = b
                // Initial basis: slack variables (indices n to n+m-1)
                var basis = new int[m];
                for (var i = 0; i < m; i++)
                {
                    basis[i] = n + i;
                }

                // Initial solution: x = 0, s = b
                var x = new double[n + m];
                for (var i = 0; i < m; i++)
                {
                    x[n + i] = fullConstraints[i];
                }

                // Create augmented coefficient matrix [A | I] (we may need artificials for Phase I)
                var fullAssignmentsCopy = fullAssignments.Select(row => row.ToArray()).ToArray();
                var fullConstraintsCopy = fullConstraints.ToArray();

                // Track rows that were flipped to make RHS non-negative
                var flipped = new bool[m];
                for (var i = 0; i < m; i++)
                {
                    if (fullConstraintsCopy[i] < 0)
                    {
                        flipped[i] = true;
                        fullConstraintsCopy[i] = -fullConstraintsCopy[i];
                        for (var j = 0; j < n; j++)
                            fullAssignmentsCopy[i][j] = -fullAssignmentsCopy[i][j];
                    }
                }

                // Build A_augmented for Phase II (original variables + slack)
                var A_augmented = new double[m][];
                for (var i = 0; i < m; i++)
                {
                    A_augmented[i] = new double[n + m];
                    // Copy original coefficients (convert int to double)
                    for (var j = 0; j < n; j++)
                    {
                        A_augmented[i][j] = fullAssignmentsCopy[i][j];
                    }
                    // Add slack variables; if row was flipped the slack coefficient is -1
                    for (var j = 0; j < m; j++)
                    {
                        A_augmented[i][n + j] = (i == j) ? (flipped[i] ? -1 : 1) : 0;
                    }
                }

                // Augmented costs: [c | 0] (zeros for slack variables)
                var c_augmented = new double[n + m];
                for (var j = 0; j < n; j++)
                    c_augmented[j] = problem.Costs[j];

                // Initial solution x: original variables zero, slack = b
                var x_phase2 = new double[n + m];
                for (var i = 0; i < m; i++)
                    x_phase2[n + i] = fullConstraintsCopy[i];

                // Solve using two-phase revised simplex if any slack initial RHS is negative (infeasible)
                var tupleResultFinal = (status: "", x: new double[0], optimalValue: 0.0, basis: new int[0]);
                var needPhaseI = x_phase2.Any(v => v < -1e-12);
                if (!needPhaseI)
                {
                    // Basis is slack variables n..n+m-1
                    for (var i = 0; i < m; i++)
                        basis[i] = n + i;
                    // Pre-check basis feasibility: B * x_B == b ?
                    try
                    {
                        var tol = 1e-6;
                        var B = new double[m][];
                        for (var r = 0; r < m; r++)
                            B[r] = new double[m];
                        for (var col = 0; col < m; col++)
                        {
                            var varIndex = basis[col];
                            for (var row = 0; row < m; row++)
                            {
                                B[row][col] =
                                    (varIndex >= 0 && varIndex < A_augmented[row].Length)
                                        ? A_augmented[row][varIndex]
                                        : 0;
                            }
                        }
                        var xB = new double[m];
                        for (var col = 0; col < m; col++)
                            xB[col] = x_phase2[basis[col]];
                        // Compute B * xB
                        var residual = new double[m];
                        for (var row = 0; row < m; row++)
                        {
                            double s = 0;
                            for (var col = 0; col < m; col++)
                                s += B[row][col] * xB[col];
                            residual[row] = s - fullConstraintsCopy[row];
                        }
                        var maxResidual = residual.Max(r => Math.Abs(r));
                        if (maxResidual > tol)
                        {
                            LastBasisDiagnostics =
                                $"Pre-basis check failed: maxResidual={maxResidual}; sample residuals={string.Join(",", residual.Take(Math.Min(10, residual.Length)))}; basisSample={string.Join(",", basis.Take(Math.Min(40, basis.Length)))}";
                            // Return error with zero-filled values to preserve expected variable count
                            return new ContinuousSolution(Enumerable.Repeat(0.0, n).ToList(), 0, State.Error, branches);
                        }
                    }
                    catch (Exception)
                    {
                        // ignore and continue to solver
                    }
                    tupleResultFinal = RevisedSimplex(
                        A_augmented,
                        c_augmented,
                        n,
                        m,
                        basis,
                        x_phase2,
                        cancellationToken
                    );
                }
                else
                {
                    // Phase I: build matrix with artificials appended
                    // Columns: [original (n) | slack (m) | artificials (m)] -> totalCols = n + 2*m
                    var totalColsPhaseI = n + 2 * m;
                    var A_phaseI = new double[m][];
                    for (var i = 0; i < m; i++)
                    {
                        A_phaseI[i] = new double[totalColsPhaseI];
                        // original
                        for (var j = 0; j < n; j++)
                            A_phaseI[i][j] = fullAssignmentsCopy[i][j];
                        // slack
                        for (var j = 0; j < m; j++)
                            A_phaseI[i][n + j] = (i == j) ? (flipped[i] ? -1 : 1) : 0;
                        // artificials (identity)
                        for (var j = 0; j < m; j++)
                            A_phaseI[i][n + m + j] = (i == j) ? 1 : 0;
                    }

                    // Phase I costs: zeros for original+slack, ones for artificials
                    var c_phaseI = new double[totalColsPhaseI];
                    for (var j = n + m; j < totalColsPhaseI; j++)
                        c_phaseI[j] = 1.0;

                    // Initial basis: artificials (indices n+m .. n+2m-1)
                    var basisPhaseI = new int[m];
                    for (var i = 0; i < m; i++)
                        basisPhaseI[i] = n + m + i;

                    // Initial x for phase I: artificials = b
                    var x_phaseI = new double[totalColsPhaseI];
                    for (var i = 0; i < m; i++)
                        x_phaseI[n + m + i] = fullConstraintsCopy[i];

                    var phaseIResult = RevisedSimplex(
                        A_phaseI,
                        c_phaseI,
                        n,
                        m,
                        basisPhaseI,
                        x_phaseI,
                        cancellationToken
                    );
                    if (phaseIResult.status != "optimal")
                    {
                        return new ContinuousSolution(Enumerable.Repeat(0.0, n).ToList(), 0, State.Error, branches);
                    }
                    // If Phase I objective > eps, infeasible
                    if (phaseIResult.optimalValue > 1e-6)
                    {
                        return new ContinuousSolution(Enumerable.Repeat(0.0, n).ToList(), 0, State.Unbounded, branches);
                    }

                    // Prepare Phase II: remove artificials and use basis from phaseI, replacing any artificial basics
                    // Build A_augmented (already built) and c_augmented (already built)
                    // Start basis for phase II from phaseIResult.basis, but replace artificials
                    var basisPhase2 = phaseIResult.basis.ToArray();
                    for (var i = 0; i < m; i++)
                    {
                        if (basisPhase2[i] >= n + m)
                        {
                            // try to find a non-artificial column with non-zero coeff in this row that is not already in basis
                            var found = false;
                            for (var j = 0; j < n + m; j++)
                            {
                                if (Math.Abs(A_augmented[i][j]) > 1e-12 && !basisPhase2.Contains(j))
                                {
                                    basisPhase2[i] = j;
                                    found = true;
                                    break;
                                }
                            }
                            if (!found)
                            {
                                // fallback to slack column for this row
                                basisPhase2[i] = n + i;
                            }
                        }
                    }

                    // Prepare x for phase II from phaseI result (trim artificials)
                    var x_phase2_from_phaseI = new double[n + m];
                    for (var j = 0; j < n + m && j < phaseIResult.x.Length; j++)
                        x_phase2_from_phaseI[j] = phaseIResult.x[j];

                    // Pre-check basis feasibility for phase II basis
                    try
                    {
                        var tol = 1e-6;
                        var B = new double[m][];
                        for (var r = 0; r < m; r++)
                            B[r] = new double[m];
                        for (var col = 0; col < m; col++)
                        {
                            var varIndex = basisPhase2[col];
                            for (var row = 0; row < m; row++)
                            {
                                B[row][col] =
                                    (varIndex >= 0 && varIndex < A_augmented[row].Length)
                                        ? A_augmented[row][varIndex]
                                        : 0;
                            }
                        }
                        var xB = new double[m];
                        for (var col = 0; col < m; col++)
                            xB[col] = x_phase2_from_phaseI[basisPhase2[col]];
                        var residual = new double[m];
                        for (var row = 0; row < m; row++)
                        {
                            double s = 0;
                            for (var col = 0; col < m; col++)
                                s += B[row][col] * xB[col];
                            residual[row] = s - fullConstraintsCopy[row];
                        }
                        var maxResidual = residual.Max(r => Math.Abs(r));
                        if (maxResidual > tol)
                        {
                            LastBasisDiagnostics =
                                $"Pre-basis check (phaseI) failed: maxResidual={maxResidual}; sample residuals={string.Join(",", residual.Take(Math.Min(10, residual.Length)))}; basisSample={string.Join(",", basisPhase2.Take(Math.Min(40, basisPhase2.Length)))}";
                            return new ContinuousSolution(Enumerable.Repeat(0.0, n).ToList(), 0, State.Error, branches);
                        }
                    }
                    catch (Exception)
                    {
                        // ignore and continue
                    }

                    tupleResultFinal = RevisedSimplex(
                        A_augmented,
                        c_augmented,
                        n,
                        m,
                        basisPhase2,
                        x_phase2_from_phaseI,
                        cancellationToken
                    );
                }

                // Save raw LP tuple for diagnostics (status, full x vector, optimalValue, basis)
                // Store before we convert to ContinuousSolution so BranchAndBound diagnostics
                // can inspect the actual LP basis/x that the revised simplex produced.
                LastLpTuple = tupleResultFinal;

                // Post-check: verify returned x satisfies Ax <= b for the augmented A and fullConstraintsCopy
                try
                {
                    var tol = 1e-6;
                    var violated = false;
                    for (var i = 0; i < m; i++)
                    {
                        double lhs = 0.0;
                        for (var j = 0; j < A_augmented[i].Length && j < tupleResultFinal.x.Length; j++)
                        {
                            lhs += A_augmented[i][j] * tupleResultFinal.x[j];
                        }
                        var rhs = fullConstraintsCopy[i];
                        if (lhs > rhs + tol)
                        {
                            // LP returned solution violating row {i}; mark as violated so caller treats as error
                            violated = true;
                        }
                    }
                    if (violated)
                    {
                        // Treat as error so BranchAndBound will mark branch infeasible
                        tupleResultFinal = (
                            "error",
                            tupleResultFinal.x,
                            tupleResultFinal.optimalValue,
                            tupleResultFinal.basis
                        );
                        // Log diagnostics: include LP status, pre-basis diagnostics and the normalized x length
                        try
                        {
                            var status = tupleResultFinal.status;
                            var diag =
                                $"LP post-check failed: status={status} optimalValue={tupleResultFinal.optimalValue} xLen={tupleResultFinal.x.Length} basisLen={(tupleResultFinal.basis == null ? 0 : tupleResultFinal.basis.Length)}";
                            if (!string.IsNullOrEmpty(LastBasisDiagnostics))
                                diag += " | lastBasisDiagnostics=" + LastBasisDiagnostics;
                            LastBasisDiagnostics = diag;
                        }
                        catch { }
                    }
                }
                catch (Exception)
                {
                    // ignore verification errors in static Solve
                }

                if (tupleResultFinal.status == "optimal")
                {
                    // Extract solution (only original variables)
                    var solution = new List<double>();
                    for (var i = 0; i < n; i++)
                    {
                        solution.Add(tupleResultFinal.x[i]);
                    }
                    return new ContinuousSolution(solution, tupleResultFinal.optimalValue, State.Optimal, branches);
                }
                else if (tupleResultFinal.status == "unbounded")
                {
                    return new ContinuousSolution(new List<double>(), 0, State.Unbounded, branches);
                }
                else
                {
                    return new ContinuousSolution(new List<double>(), 0, State.Error, branches);
                }
            }
            catch (Exception)
            {
                return new ContinuousSolution(new List<double>(), 0, State.Error, branches);
            }
        }

        // Public helper used by tests to run the continuous LP solver directly
        public static ContinuousSolution SolveProblem(
            Problem problem,
            Stack<Branch> branches,
            CancellationToken cancellationToken
        )
        {
            return Solve(problem, branches, cancellationToken);
        }

        private static (string status, double[] x, double optimalValue, int[] basis) RevisedSimplex(
            double[][] A,
            double[] c,
            int n,
            int m,
            int[] basis,
            double[] x,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Build initial basis inverse from the provided basis columns
            var totalCols = c.Length;
            // Helper to normalize returned x to totalCols length
            double[] NormalizeX(double[] src)
            {
                var full = new double[totalCols];
                if (src != null)
                {
                    for (var i = 0; i < Math.Min(src.Length, totalCols); i++)
                        full[i] = src[i];
                }
                return full;
            }
            // Build basis matrix B (m x m) where column k = A[:, basis[k]]
            var B = new double[m][];
            for (var i = 0; i < m; i++)
                B[i] = new double[m];
            for (var col = 0; col < m; col++)
            {
                var varIndex = basis[col];
                for (var row = 0; row < m; row++)
                {
                    // If varIndex is within A columns, take value, otherwise assume 0
                    if (varIndex >= 0 && varIndex < A[row].Length)
                        B[row][col] = A[row][varIndex];
                    else
                        B[row][col] = 0;
                }
            }

            var B_inv = InvertMatrix(B);

            var iteration = 0;
            const int maxIterations = 1000;

            while (iteration < maxIterations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                iteration++;

                // Compute reduced costs: c_j - c_B * B_inv * A_j
                var reducedCosts = new double[totalCols];

                for (var j = 0; j < totalCols; j++)
                {
                    // Get column A_j from A (generalized)
                    var A_j = new double[m];
                    for (var i = 0; i < m; i++)
                    {
                        if (j >= 0 && j < A[i].Length)
                            A_j[i] = A[i][j];
                        else
                            A_j[i] = 0;
                    }

                    // Compute y = B_inv * A_j
                    var y = MatrixVectorMultiply(B_inv, A_j);

                    // Compute reduced cost c_j - c_B * y
                    var reducedCost = c[j];
                    for (var i = 0; i < m; i++)
                    {
                        reducedCost -= c[basis[i]] * y[i];
                    }
                    reducedCosts[j] = reducedCost;
                }

                // Check optimality (for minimization, all reduced costs >= 0)
                var minReducedCost = reducedCosts.Min();
                if (minReducedCost >= -1e-6)
                {
                    // Optimal solution found
                    double optimalValue = 0;
                    for (var i = 0; i < m; i++)
                    {
                        // basis[i] may be >= totalCols; guard index
                        var bi = basis[i];
                        var xi = (bi >= 0 && bi < x.Length) ? x[bi] : 0.0;
                        optimalValue += c[basis[i]] * xi;
                    }
                    return ("optimal", NormalizeX(x), optimalValue, basis);
                }

                // Choose entering variable (most negative reduced cost)
                var enteringVar = Array.IndexOf(reducedCosts, minReducedCost);

                // Get entering column from A
                var A_entering = new double[m];
                for (var i = 0; i < m; i++)
                {
                    if (enteringVar >= 0 && enteringVar < A[i].Length)
                        A_entering[i] = A[i][enteringVar];
                    else
                        A_entering[i] = 0;
                }

                // Compute direction: d = B_inv * A_entering
                var d = MatrixVectorMultiply(B_inv, A_entering);

                // Check if problem is unbounded
                var minRatio = double.PositiveInfinity;
                var leavingVar = -1;

                for (var i = 0; i < m; i++)
                {
                    if (d[i] > 1e-6)
                    {
                        var ratio = x[basis[i]] / d[i];
                        if (ratio < minRatio)
                        {
                            minRatio = ratio;
                            leavingVar = i;
                        }
                    }
                }

                if (leavingVar == -1)
                {
                    return ("unbounded", NormalizeX(x), 0, basis);
                }

                // Update solution and basis
                var leavingVarIndex = basis[leavingVar];
                var theta = x[leavingVarIndex] / d[leavingVar];

                // Update all basic variables: x_B = x_B - theta * d
                for (var i = 0; i < m; i++)
                    x[basis[i]] -= theta * d[i];

                // Set entering variable to theta and leaving variable to 0
                x[enteringVar] = theta;
                x[leavingVarIndex] = 0;

                // Update basis
                basis[leavingVar] = enteringVar;

                // Update basis inverse using eta matrix
                var E = new double[m][];
                for (var i = 0; i < m; i++)
                {
                    E[i] = new double[m];
                    for (var j = 0; j < m; j++)
                    {
                        if (j == leavingVar)
                        {
                            E[i][j] = (i == leavingVar) ? (1 / d[leavingVar]) : (-d[i] / d[leavingVar]);
                        }
                        else
                        {
                            E[i][j] = (i == j) ? 1 : 0;
                        }
                    }
                }

                B_inv = MatrixMultiply(E, B_inv);
            }

            return ("max_iterations", NormalizeX(x), 0, basis);
        }

        private static double[][] InvertMatrix(double[][] matrix)
        {
            var n = matrix.Length;
            var A = new double[n][];
            for (var i = 0; i < n; i++)
            {
                A[i] = new double[n * 2];
                for (var j = 0; j < n; j++)
                    A[i][j] = matrix[i][j];
                for (var j = 0; j < n; j++)
                    A[i][n + j] = (i == j) ? 1 : 0;
            }

            // Gauss-Jordan
            for (var col = 0; col < n; col++)
            {
                // find pivot
                var pivot = col;
                for (var r = col; r < n; r++)
                    if (Math.Abs(A[r][col]) > Math.Abs(A[pivot][col]))
                        pivot = r;
                if (Math.Abs(A[pivot][col]) < 1e-12)
                    throw new Exception("Singular matrix");
                // swap
                if (pivot != col)
                {
                    var tmp = A[col];
                    A[col] = A[pivot];
                    A[pivot] = tmp;
                }
                // normalize
                var div = A[col][col];
                for (var j = 0; j < 2 * n; j++)
                    A[col][j] /= div;
                // eliminate
                for (var r = 0; r < n; r++)
                    if (r != col)
                    {
                        var factor = A[r][col];
                        if (Math.Abs(factor) < 1e-15)
                            continue;
                        for (var j = 0; j < 2 * n; j++)
                            A[r][j] -= factor * A[col][j];
                    }
            }

            var inv = new double[n][];
            for (var i = 0; i < n; i++)
            {
                inv[i] = new double[n];
                for (var j = 0; j < n; j++)
                    inv[i][j] = A[i][n + j];
            }
            return inv;
        }

        private static double[] MatrixVectorMultiply(double[][] matrix, double[] vector)
        {
            var m = matrix.Length;
            var result = new double[m];

            for (var i = 0; i < m; i++)
            {
                for (var j = 0; j < vector.Length; j++)
                {
                    result[i] += matrix[i][j] * vector[j];
                }
            }

            return result;
        }

        private static double[][] MatrixMultiply(double[][] A, double[][] B)
        {
            var m = A.Length;
            var n = B[0].Length;
            var p = A[0].Length;

            var result = new double[m][];
            for (var i = 0; i < m; i++)
            {
                result[i] = new double[n];
                for (var j = 0; j < n; j++)
                {
                    for (var k = 0; k < p; k++)
                    {
                        result[i][j] += A[i][k] * B[k][j];
                    }
                }
            }

            return result;
        }
    }

    public record Problem(int[][] Assignments, double[] Costs, int[] Constraints);

    // Continuous solution used by LP solver and tests
    public record ContinuousSolution(
        List<double> Values,
        double OptimalValue,
        SolverService.State State,
        Stack<Branch> Branches
    )
    {
        public virtual bool Equals(ContinuousSolution? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;

            return State == other.State
                && Math.Abs(OptimalValue - other.OptimalValue) < 1e-6
                && Values.SequenceEqual(other.Values)
                && Branches.SequenceEqual(other.Branches);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(State, OptimalValue, Values, Branches);
        }
    }

    // Public discrete solution mapping variables back to actual items with quantities
    public record Solution(
        List<ModRecipeStack> Values,
        double OptimalValue,
        SolverService.State State,
        Stack<Branch> Branches
    )
    {
        public virtual bool Equals(Solution? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;

            return State == other.State
                && Math.Abs(OptimalValue - other.OptimalValue) < 1e-6
                && Values.SequenceEqual(other.Values)
                && Branches.SequenceEqual(other.Branches);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(State, OptimalValue, Values, Branches);
        }

        public void Print(Action<string> log)
        {
            log($"===============================================================================");
            log($"State: {State}");
            for (var i = 0; i < Values.Count; i++)
            {
                var v = Values[i];
                log($"  Craft [{v.Quantity}]: {v.Recipe.Item.Name}");
            }

            log($"Total value: {Math.Floor(OptimalValue)} gil");
            log($"===============================================================================");
        }
    }

    public record Branch(int Index, int Value, bool IsPositive);
}
