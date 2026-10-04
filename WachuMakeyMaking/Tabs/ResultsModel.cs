using WachuMakeyMaking.Services;

namespace WachuMakeyMaking.Tabs
{
    public class ResultsModel
    {
        public SolverService.State SolverState { get; private set; }
        public string SolverProgressMessage { get; private set; }
        public Solution? CurrentSolution { get; private set; }

        public ResultsModel()
        {
            this.SolverState = SolverService.State.Idle;
            this.SolverProgressMessage = string.Empty;
            this.CurrentSolution = null;
        }

        public void OnSolverProgressUpdate(SolverService.State state, string message, Solution? solution)
        {
            this.SolverState = state;
            this.SolverProgressMessage = message;
            this.CurrentSolution = solution;
        }
    }
}
