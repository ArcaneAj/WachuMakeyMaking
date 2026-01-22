using WachuMakeyMaking.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace WachuMakeyMaking.Services
{
    public class SolverService
    {
        private readonly Action<string> log;
        private readonly Action<string> logError;
        private readonly List<Action<State, string, Solution?>> progressListeners = [];
        private State state;
        private Solution currentBest = null!;
        private double lowerBound = 0.0;
        private string progressMessage = string.Empty;

        public enum State
        {
            Idle,
            FindingInitialSolution,
            Optimising,
            Finished,
            Optimal,
            Unbounded,
            Error
        }

        public SolverService(Action<string> log, Action<string> logError)
        {
            this.log = log;
            this.logError = logError;
            this.state = State.Idle;
        }

        public void RegisterProgressListener(Action<State, string, Solution?> listener)
        {
            this.progressListeners.Add(listener);
        }

        public void Reset()
        {
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

        public Solution Solve(List<ModRecipeWithValue> recipes, ModItemStack[] resources)
        {
            if (recipes.Count == 0)
            {
                Reset();
                return new Solution([], 0, State.Error, []);
            }

            this.currentBest = null!;
            UpdateProgress(State.FindingInitialSolution, "Finding initial solution...");

            if (false) {
                // Serialize recipes and resources to JSON and log
                var serializableRecipes = recipes.Select(r => new
                {
                    Item = new { r.Item.RowId, r.Item.Name },
                    Ingredients = r.Ingredients.Select(kvp => new
                    {
                        Item = new { kvp.Key.RowId, kvp.Key.Name },
                        Quantity = kvp.Value
                    }).ToList(),
                    r.Value,
                    Currency = new { r.Currency.RowId, r.Currency.Name }
                }).ToList();

                var serializableResources = resources.Select(r => new
                {
                    Item = new { r.Item.RowId, r.Item.Name },
                    r.Quantity
                }).ToList();

                var inputData = new
                {
                    Recipes = serializableRecipes,
                    Resources = serializableResources
                };
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                var jsonString = JsonSerializer.Serialize(inputData, jsonOptions);
                this.log($"{jsonString}");
            }

            var usedResources = resources.Where(x => recipes.Any(y => y.Ingredients.ContainsKey(x.Item)));

            var costs = recipes.Select(x => -x.Value).ToArray();

            var constraintsList = new List<int>();
            var assignmentsList = new List<int[]>();

            foreach (var resource in usedResources)
            {
                constraintsList.Add(resource.Quantity);
                var row = recipes.Select(recipe => (int)recipe.Ingredients.GetValueOrDefault(resource.Item));
                assignmentsList.Add([..row]);

                var recipesUsingResource = recipes.Where(recipe => recipe.Ingredients.ContainsKey(resource.Item)).ToList();
            }

            var branches = new Stack<Branch>();
            var problem = new Problem(assignmentsList.ToArray(), costs, constraintsList.ToArray());
            var result = Solve(problem, branches);
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
            BranchAndBound(problem, result);

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

            return this.currentBest;
        }

        private bool BranchAndBound(Problem problem, Solution previousResult)
        {
            var valuesToBranch = previousResult.Values.Select((double val, int index) => (val, index)).Where(x => x.val - Math.Floor(x.val) > 1e-10).ToList();

            // If we're integral, check if this is the best solution so far
            if (valuesToBranch.Count == 0) {
                if (previousResult.OptimalValue < (this.currentBest?.OptimalValue ?? 0.0)){
                    this.currentBest = previousResult;
                    if (this.state == State.FindingInitialSolution)
                    {
                        UpdateProgress(State.Optimising, "Optimising...", this.currentBest);
                    }
                    else
                    {
                        UpdateProgress(State.Optimising, this.progressMessage, this.currentBest);
                    }
                }
                return Math.Abs(previousResult.OptimalValue - this.lowerBound) < 1e-10;
            }

            // Branch on the first fractional variable
            var branchVar = valuesToBranch[0];
            var floorVal = (int)Math.Floor(branchVar.val);
            var ceilVal = (int)Math.Ceiling(branchVar.val);

            // Create positive branch: x[index] <= floor(val)
            var positiveBranch = new Branch(branchVar.index, floorVal, true);
            var positiveBranches = new Stack<Branch>(previousResult.Branches.Reverse());
            positiveBranches.Push(positiveBranch);

            var positiveResult = Solve(problem, positiveBranches);
            if (positiveResult.OptimalValue < this.lowerBound)
            {
                this.logError("Branch result was below the lower bound, something very wrong must have happened.");
            }

            if (positiveResult.State == State.Optimal)
            {
                // Verify the solution satisfies the branch constraint
                bool feasible = positiveResult.Values[branchVar.index] <= floorVal + 1e-10;
                if (!feasible)
                {
                    this.log($"Branch constraint violated: x[{branchVar.index}] = {positiveResult.Values[branchVar.index]} > {floorVal}, skipping");
                }
                else
                {
                    // Only explore further if this could be better than current best
                    if (this.currentBest == null || positiveResult.OptimalValue < this.currentBest.OptimalValue)
                    {
                        if (this.currentBest != null)
                        {
                            var message = $"Optimising... Current best: {Math.Floor(this.currentBest.OptimalValue)} gil Lower bound: {Math.Floor(this.lowerBound)}";
                            this.progressMessage = message;
                            UpdateProgress(State.Optimising, message, this.currentBest);
                        }
                        BranchAndBound(problem, positiveResult);
                    }
                }
            }

            // Create negative branch: x[index] >= ceil(val)
            var negativeBranch = new Branch(branchVar.index, ceilVal, false);
            var negativeBranches = new Stack<Branch>(previousResult.Branches.Reverse());
            negativeBranches.Push(negativeBranch);

            var negativeResult = Solve(problem, negativeBranches);
            if (negativeResult.State == State.Optimal)
            {
                // Verify the solution satisfies the branch constraint
                bool feasible = negativeResult.Values[branchVar.index] >= ceilVal - 1e-10;
                if (!feasible)
                {
                    this.log($"Branch constraint violated: x[{branchVar.index}] = {negativeResult.Values[branchVar.index]} < {ceilVal}, skipping");
                }
                else
                {
                    // Only explore further if this could be better than current best
                    if (this.currentBest == null || negativeResult.OptimalValue < this.currentBest.OptimalValue)
                    {
                        if (this.currentBest != null)
                        {
                            var message = $"Optimising... Current best: {Math.Floor(this.currentBest.OptimalValue)} gil Lower bound: {Math.Floor(this.lowerBound)}";
                            this.progressMessage = message;
                            UpdateProgress(State.Optimising, message, this.currentBest);
                        }
                        BranchAndBound(problem, negativeResult);
                    }
                }
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

                // Update all basic variables: x_B = x_B - theta * d
                for (int i = 0; i < m; i++)
                {
                    x[basis[i]] -= theta * d[i];
                }

                // Set entering variable to theta and leaving variable to 0
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
        public virtual bool Equals(Solution? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return State == other.State &&
                   Math.Abs(OptimalValue - other.OptimalValue) < 1e-10 &&
                   Values.SequenceEqual(other.Values) &&
                   Branches.SequenceEqual(other.Branches);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + State.GetHashCode();
                hash = hash * 23 + OptimalValue.GetHashCode();
                hash = hash * 23 + Values.GetHashCode();
                hash = hash * 23 + Branches.GetHashCode();
                return hash;
            }
        }

        public void Print(List<ModRecipeWithValue> recipes, Action<string> log)
        {
            log($"===============================================================================");
            log($"State: {State}");
            for (var i = 0; i < recipes.Count; i++)
            {
                log($"  Craft [{Values[i]}]: {recipes[i].Item.Name}");
            }

            log($"Total value: {Math.Floor(OptimalValue)} gil");
            log($"===============================================================================");
        }
    }

    public record Branch (int Index, int Value, bool IsPositive);
}
