using SamplePlugin.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SamplePlugin.Services
{
    public class SolverService
    {
        private readonly Plugin plugin;
        private State state;
        private RecipeCacheService recipeService;
        private Solution currentBest = null!;
        private double lowerBound = 0.0;
        private List<ModRecipeWithValue> recipes = [];

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

        public Solution Solve(List<ModRecipeWithValue> recipes)
        {
            this.state = State.InProgress;
            this.recipes = recipes;
            var crystals = this.recipeService.GetCrystals();
            var items = this.recipeService.GetConsolidatedItems();
            var resources = crystals.Concat(items).ToArray();
            var usedResources = resources.Where(x => recipes.Any(y => y.Ingredients.ContainsKey(x.Item)));


            var costs = recipes.Select(x => -x.Value).ToArray();

            var constraintsList = new List<int>();
            var assignmentsList = new List<int[]>();

            foreach (var resource in usedResources)
            {
                //Plugin.Log.Info($"{resource.Item.Name}");
                constraintsList.Add(resource.Quantity);
                var row = recipes.Select(recipe => (int)recipe.Ingredients.GetValueOrDefault(resource.Item));
                assignmentsList.Add([..row]);

                var recipesUsingResource = recipes.Where(recipe => recipe.Ingredients.ContainsKey(resource.Item)).ToList();
                //foreach (var rur in recipesUsingResource)
                //{
                //    Plugin.Log.Info($"    {rur.Item.Name}");
                //}
            }

            var branches = new Stack<Branch>();
            var problem = new Problem(assignmentsList.ToArray(), costs, constraintsList.ToArray());
            var result = Solve(problem, branches);
            result.Print(recipes);
            if (result.State == State.Unbounded)
            {
                Plugin.Log.Info("Problem is unbounded - no optimal solution exists.");
                this.state = State.Unbounded;
                return new Solution([], 0, State.Error, []);
            }
            if (result.State != State.Optimal)
            {
                Plugin.Log.Info("Unknown error occurred when finding initial solution.");
                this.state = result.State;
                return new Solution([], 0, State.Error, []);
            }

            this.lowerBound = result.OptimalValue;
            BranchAndBound(problem, result);

            if (this.currentBest == null)
            {
                Plugin.Log.Info("Unable to find integral solution");
                return new Solution([], 0, State.Error, []);
            }

            if (this.currentBest.State == State.Optimal)
            {
                Plugin.Log.Info("Optimal solution found:");
                this.currentBest.Print(recipes);
                this.state = State.Optimal;
            }
            else
            {
                Plugin.Log.Info("No optimal solution found.");
                this.state = State.Error;
            }

            return this.currentBest;
        }

        private bool BranchAndBound(Problem problem, Solution previousResult)
        {
            var valuesToBranch = previousResult.Values.Select((double val, int index) => (val, index)).Where(x => x.val - Math.Floor(x.val) > 1e-10).ToList();
            // If we're integral
            if (valuesToBranch.Count == 0) {
                // And we're better than the existing solution (or 0 if the existing solution doesn't exist)
                if (previousResult.OptimalValue < (this.currentBest?.OptimalValue ?? 0.0)){
                    // This is now the best solution
                    this.currentBest = previousResult;
                    // Return true if we manage to hit the lower bound
                    if (Math.Abs(previousResult.OptimalValue - this.lowerBound) > 1e-10) return true;
                }
                    
                Plugin.Log.Info($"CurrentBest {previousResult.OptimalValue}");
            };

            // For each value to branch, we should create both the upper and lower branches
            var positiveBranches = valuesToBranch.Select(x => new Branch(x.index, (int)Math.Floor(x.val), true));
            var negativeBranches = valuesToBranch.Select(x => new Branch(x.index, (int)Math.Ceiling(x.val), false));

            foreach (var branch in positiveBranches.Concat(negativeBranches))
            {
                previousResult.Branches.Push(branch);
                foreach (var item in previousResult.Branches)
                {
                    Plugin.Log.Info($"Branching on: {item.Index} {item.Value} {item.IsPositive}");
                }

                var newResult = Solve(problem, previousResult.Branches);
                newResult.Print(this.recipes);

                // If what we just did is worse than the current best, we can abandon this recursion path
                if (this.currentBest != null && newResult.OptimalValue > this.currentBest.OptimalValue){
                    // We remove this branch from the stack
                    _ = previousResult.Branches.Pop();
                    // And we try the next branch horizontally
                    continue;
                }
                
                var boundHit = BranchAndBound(problem, newResult);
                if (boundHit) return true;
                _ = previousResult.Branches.Pop();
            }

            return false;
        }

        private Solution Solve(Problem problem, Stack<Branch> branches)
        {
            try
            {
                // Validate input dimensions
                if (problem.Assignments.Length == 0 || problem.Assignments[0].Length == 0 || problem.Assignments.Any(x => x.Length != problem.Assignments[0].Length))
                {
                    return new Solution(new List<double>(), 0, State.Error, branches);
                }

                int m = problem.Assignments.Length; // number of constraints (resources)
                int n = problem.Assignments[0].Length; // number of variables (recipes)

                if (problem.Costs.Length != n || problem.Constraints.Length != m)
                {
                    return new Solution(new List<double>(), 0, State.Error, branches);
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
                int[] basis = new int[m];
                for (int i = 0; i < m; i++)
                {
                    basis[i] = n + i;
                }

                // Initial solution: x = 0, s = b
                double[] x = new double[n + m];
                for (int i = 0; i < m; i++)
                {
                    x[n + i] = fullConstraints[i];
                }

                // Create augmented coefficient matrix [A | I]
                double[][] A_augmented = new double[m][];
                for (int i = 0; i < m; i++)
                {
                    A_augmented[i] = new double[n + m];
                    // Copy original coefficients (convert int to double)
                    for (int j = 0; j < n; j++)
                    {
                        A_augmented[i][j] = fullAssignments[i][j];
                    }
                    // Add identity matrix for slack variables
                    for (int j = 0; j < m; j++)
                    {
                        A_augmented[i][n + j] = (i == j) ? 1 : 0;
                    }
                }

                // Augmented costs: [c | 0] (zeros for slack variables)
                double[] c_augmented = new double[n + m];
                for (int j = 0; j < n; j++)
                {
                    c_augmented[j] = problem.Costs[j];
                }
                // Slack variables have cost 0

                // Solve using revised simplex
                var result = RevisedSimplex(A_augmented, c_augmented, n, m, basis, x);

                if (result.status == "optimal")
                {
                    // Extract solution (only original variables)
                    var solution = new List<double>();
                    for (int i = 0; i < n; i++)
                    {
                        solution.Add(result.x[i]);
                    }
                    return new Solution(solution, result.optimalValue, State.Optimal, branches);
                }
                else if (result.status == "unbounded")
                {
                    return new Solution(new List<double>(), 0, State.Unbounded, branches);
                }
                else
                {
                    return new Solution(new List<double>(), 0, State.Error, branches);
                }
            }
            catch (Exception)
            {
                return new Solution(new List<double>(), 0, State.Error, branches);
            }
        }

        private (string status, double[] x, double optimalValue, int[] basis) RevisedSimplex(
            double[][] A, double[] c, int n, int m, int[] basis, double[] x)
        {
            // Initialize basis inverse (identity matrix)
            double[][] B_inv = new double[m][];
            for (int i = 0; i < m; i++)
            {
                B_inv[i] = new double[m];
                for (int j = 0; j < m; j++)
                {
                    B_inv[i][j] = (i == j) ? 1 : 0;
                }
            }

            int iteration = 0;
            const int maxIterations = 1000;

            while (iteration < maxIterations)
            {
                iteration++;

                // Compute reduced costs: c_j - c_B * B_inv * A_j
                double[] reducedCosts = new double[n + m];

                for (int j = 0; j < n + m; j++)
                {
                    // Get column A_j
                    double[] A_j = new double[m];
                    if (j < n)
                    {
                        // Original variable - use column from A
                        for (int i = 0; i < m; i++)
                        {
                            A_j[i] = A[i][j];
                        }
                    }
                    else
                    {
                        // Slack variable - identity matrix column
                        int slackIndex = j - n;
                        A_j[slackIndex] = 1;
                    }

                    // Compute y = B_inv * A_j
                    double[] y = MatrixVectorMultiply(B_inv, A_j);

                    // Compute reduced cost c_j - c_B * y
                    double reducedCost = c[j];
                    for (int i = 0; i < m; i++)
                    {
                        reducedCost -= c[basis[i]] * y[i];
                    }
                    reducedCosts[j] = reducedCost;
                }

                // Check optimality (for minimization, all reduced costs >= 0)
                double minReducedCost = reducedCosts.Min();
                if (minReducedCost >= -1e-10)
                {
                    // Optimal solution found
                    double optimalValue = 0;
                    for (int i = 0; i < m; i++)
                    {
                        optimalValue += c[basis[i]] * x[basis[i]];
                    }
                    return ("optimal", x, optimalValue, basis);
                }

                // Choose entering variable (most negative reduced cost)
                int enteringVar = Array.IndexOf(reducedCosts, minReducedCost);

                // Get entering column
                double[] A_entering = new double[m];
                if (enteringVar < n)
                {
                    for (int i = 0; i < m; i++)
                    {
                        A_entering[i] = A[i][enteringVar];
                    }
                }
                else
                {
                    int slackIndex = enteringVar - n;
                    A_entering[slackIndex] = 1;
                }

                // Compute direction: d = B_inv * A_entering
                double[] d = MatrixVectorMultiply(B_inv, A_entering);

                // Check if problem is unbounded
                double minRatio = double.PositiveInfinity;
                int leavingVar = -1;

                for (int i = 0; i < m; i++)
                {
                    if (d[i] > 1e-10)
                    {
                        double ratio = x[basis[i]] / d[i];
                        if (ratio < minRatio)
                        {
                            minRatio = ratio;
                            leavingVar = i;
                        }
                    }
                }

                if (leavingVar == -1)
                {
                    return ("unbounded", x, 0, basis);
                }

                // Update solution and basis
                int leavingVarIndex = basis[leavingVar];
                double theta = x[leavingVarIndex] / d[leavingVar];
                x[enteringVar] = theta;
                x[leavingVarIndex] = 0;

                // Update basis
                basis[leavingVar] = enteringVar;

                // Update basis inverse using eta matrix
                double[][] E = new double[m][];
                for (int i = 0; i < m; i++)
                {
                    E[i] = new double[m];
                    for (int j = 0; j < m; j++)
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

            return ("max_iterations", x, 0, basis);
        }

        private static double[] MatrixVectorMultiply(double[][] matrix, double[] vector)
        {
            int m = matrix.Length;
            double[] result = new double[m];

            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < vector.Length; j++)
                {
                    result[i] += matrix[i][j] * vector[j];
                }
            }

            return result;
        }

        private static double[][] MatrixMultiply(double[][] A, double[][] B)
        {
            int m = A.Length;
            int n = B[0].Length;
            int p = A[0].Length;

            double[][] result = new double[m][];
            for (int i = 0; i < m; i++)
            {
                result[i] = new double[n];
                for (int j = 0; j < n; j++)
                {
                    for (int k = 0; k < p; k++)
                    {
                        result[i][j] += A[i][k] * B[k][j];
                    }
                }
            }

            return result;
        }
    }

    public record Problem (int[][] Assignments, double[] Costs, int[] Constraints);

    public record Solution (
        List<double> Values,
        double OptimalValue,
        SolverService.State State,
        Stack<Branch> Branches)
    {
        public void Print(List<ModRecipeWithValue> recipes)
        {
            Plugin.Log.Info($"===============================================================================");
            Plugin.Log.Info($"State: {State}");
            for (var i = 0; i < recipes.Count; i++)
            {
                Plugin.Log.Info($"  Craft [{Values[i]}]: {recipes[i].Item.Name}");
            }

            Plugin.Log.Info($"Total value: {Math.Floor(OptimalValue)} gil");
            Plugin.Log.Info($"===============================================================================");
        }
    }

    public record Branch (int Index, int Value, bool IsPositive);
}
