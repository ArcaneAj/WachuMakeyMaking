
using WachuMakeyMaking.Services;
using static WachuMakeyMaking.Services.SolverService;

namespace Test
{
    public class Tests
    {
        public Action<string> Log { get; private set; }

        [SetUp]
        public void Setup()
        {
            this.Log = (string l) => { };
        }

        [Test]
        public void Solve_EmptyProblem_ShouldReturnError()
        {
            var expected = new Solution([], 0, State.Error, []);
            var solver = new SolverService(Log);
            var solution = solver.Solve([], []);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(solver.CurrentState, Is.EqualTo(State.Error));
                Assert.That(solution, Is.EqualTo(expected));
            }
        }
    }
}
