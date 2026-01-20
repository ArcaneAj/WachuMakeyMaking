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

        public void Solve(List<ModRecipeWithValue> recipes)
        {
            this.state = State.InProgress;
            var crystals = this.recipeService.GetCrystals();
            var items = this.recipeService.GetConsolidatedItems();
            var resources = crystals.Concat(items).ToArray();
            var usedResources = resources.Where(x => recipes.Any(y => y.Ingredients.ContainsKey(x.Item)));


            var costs = recipes.Select(x => -x.Value).ToArray();

            var constraints = new List<int>();
            var assignments = new List<int[]>();

            foreach (var resource in usedResources)
            {
                //Plugin.Log.Info($"{resource.Item.Name}");
                constraints.Add(resource.Quantity);
                var row = recipes.Select(recipe => (int)recipe.Ingredients.GetValueOrDefault(resource.Item));
                assignments.Add([..row]);

                var recipesUsingResource = recipes.Where(recipe => recipe.Ingredients.ContainsKey(resource.Item)).ToList();
                //foreach (var rur in recipesUsingResource)
                //{
                //    Plugin.Log.Info($"    {rur.Item.Name}");
                //}
            }

            var result = Solve([.. assignments], costs, [.. constraints]);

            if (result.state == State.Optimal)
            {
                Plugin.Log.Info("Optimal solution found:");
                for (var i = 0; i < recipes.Count; i++)
                {
                    Plugin.Log.Info($"  Craft [{result.solution[i]}]: {recipes[i].Item.Name}");
                }

                Plugin.Log.Info($"Total value: {Math.Round(result.optimalValue)} gil");
                this.state = State.Optimal;
            }
            else if (result.state == State.Unbounded)
            {
                Plugin.Log.Info("Problem is unbounded - no optimal solution exists.");
                this.state = State.Unbounded;
            }
            else
            {
                Plugin.Log.Info("No optimal solution found.");
                this.state = State.Error;
            }
        }

        private (List<double> solution, double optimalValue, State state) Solve(int[][] assignments, double[] costs, int[] constraints)
        {
            try
            {
                // Validate input dimensions
                if (assignments.Length == 0 || assignments[0].Length == 0 || assignments.Any(x => x.Length != assignments[0].Length))
                {
                    return (new List<double>(), 0, State.Error);
                }

                int m = assignments.Length; // number of constraints (resources)
                int n = assignments[0].Length; // number of variables (recipes)

                if (costs.Length != n || constraints.Length != m)
                {
                    return (new List<double>(), 0, State.Error);
                }

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
                    x[n + i] = constraints[i];
                }

                // Create augmented coefficient matrix [A | I]
                double[][] A_augmented = new double[m][];
                for (int i = 0; i < m; i++)
                {
                    A_augmented[i] = new double[n + m];
                    // Copy original coefficients (convert int to double)
                    for (int j = 0; j < n; j++)
                    {
                        A_augmented[i][j] = assignments[i][j];
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
                    c_augmented[j] = costs[j];
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
                    return (solution, result.optimalValue, State.Optimal);
                }
                else if (result.status == "unbounded")
                {
                    return (new List<double>(), 0, State.Unbounded);
                }
                else
                {
                    return (new List<double>(), 0, State.Error);
                }
            }
            catch (Exception)
            {
                return (new List<double>(), 0, State.Error);
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
}
