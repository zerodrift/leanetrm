using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

internal static class LevelPlanSearchChecks
{
    public static int Run()
    {
        var passed = 0;
        void Check(string name, Action action)
        {
            action();
            passed++;
            Console.WriteLine("PASS level plan: " + name);
        }

        Check("complete path listings agree with and without a changed-day count", () =>
        {
            int[] rests = [2, 1, 3, 2];
            foreach (int? maximumChanges in new int?[] { null, 0, 1, 2, 3 })
            foreach (var terminal in Enumerable.Range(4, 9))
            foreach (var payoff in new[] { new[] { -3, 1, -1, 0 }, new[] { -2, -1, -3, -1 }, new[] { 0, 0, 0, 0 } })
            {
                // List every four-day assignment and filter complete paths against the contract.
                var expected = (from a in Enumerable.Range(1, 3)
                                from b in Enumerable.Range(1, 3)
                                from c in Enumerable.Range(1, 3)
                                from d in Enumerable.Range(1, 3)
                                let moves = new[] { a, b, c, d }
                                where a + b + c + d == terminal
                                where maximumChanges is null || moves.Where((m, day) => m != rests[day]).Count() <= maximumChanges
                                let score = a * payoff[0] + b * payoff[1] + c * payoff[2] + d * payoff[3]
                                orderby score descending,
                                    Math.Abs(a - rests[0]), a, Math.Abs(b - rests[1]), b,
                                    Math.Abs(c - rests[2]), c, Math.Abs(d - rests[3]), d
                                select (Moves: moves, Score: score)).FirstOrDefault();

                var layers = Enumerable.Range(0, 5).Select(day => new LevelPlanBounds(day, day * 3, maximumChanges ?? 0)).ToImmutableArray();
                IEnumerable<LevelPlanCandidate> Candidates(int day, LevelPlanState state)
                {
                    foreach (var move in new[] { 3, 1, 2 })
                    {
                        var left = state.Count - (maximumChanges is not null && move != rests[day] ? 1 : 0);
                        if (left >= 0) yield return new(move, new(state.Level + move, left), move * payoff[day]);
                    }
                }

                var result = LevelPlanSearch.Search(layers, new(0, maximumChanges ?? 0), Candidates,
                    day => rests[day], state => state.Level == terminal, 240, new(9, 240, 100, 8));
                if (expected.Moves is null) Failure(result, LevelPlanStatus.Infeasible);
                else Optimal(result, expected.Score, expected.Moves);
            }
        });
        Check("ties prefer the closest shifted rest and then the lower move in chronological order", () =>
        {
            LevelPlanBounds[] layers = [new(0, 0, 0), new(0, 0, 0), new(0, 0, 0)];
            IEnumerable<LevelPlanCandidate> Candidates(int day, LevelPlanState state) =>
                day == 0 ? [new(6, state, 0), new(4, state, 0), new(0, state, 0)] :
                    [new(int.MinValue, state, 0), new(int.MaxValue, state, 0)];
            Optimal(LevelPlanSearch.Search([.. layers], new(0, 0), Candidates, day => day == 0 ? 5 : 1,
                _ => true, 5, new(1, 5, 3, 1)), 0, 4, int.MaxValue);

            Optimal(LevelPlanSearch.Search([new(0, 0, 0), new(0, 0, 0)], new(0, 0),
                (_, state) => [new(0, state, 0), new(5, state, 0)], _ => 5, _ => true, 2, new(1, 2, 2, 1)), 0, 5);
        });
        Check("a missing successor and a disconnected feasible terminal do not yield a plan", () =>
        {
            var result = LevelPlanSearch.Search([new(0, 0, 0), new(0, 1, 0)], new(0, 0),
                (_, state) => [new(0, state, 0), new(2, new(2, 0), 50)], _ => 0, state => state.Level == 1,
                2, new(2, 2, 3, 8));
            Failure(result, LevelPlanStatus.Infeasible);
        });
        Check("terminal-only plans distinguish a feasible exact zero from infeasibility", () =>
        {
            var layers = ImmutableArray.Create(new LevelPlanBounds(0, 0, 0));
            Optimal(LevelPlanSearch.Search(layers, new(0, 0), (_, _) => throw new Exception("No moves are needed."),
                _ => throw new Exception("No rest move is needed."), _ => true, 0, new(1, 0, 1, 1)), 0);
            Failure(LevelPlanSearch.Search(layers, new(0, 0), (_, _) => [], _ => 0, _ => false, 0, new(1, 0, 1, 1)),
                LevelPlanStatus.Infeasible);
        });
        Check("preflight refuses level, candidate and reconstruction limits before invoking callbacks", () =>
        {
            var layers = ImmutableArray.Create(new LevelPlanBounds(0, 1, 1), new LevelPlanBounds(0, 1, 1));
            foreach (var budget in new[] { new LevelPlanBudget(1, 5, 8, 8), new(2, 4, 8, 8), new(2, 5, 7, 8) })
                Failure(LevelPlanSearch.Search(layers, new(0, 1), (_, _) => throw new Exception("Candidate work started."),
                    _ => throw new Exception("Rest work started."), _ => throw new Exception("Terminal work started."),
                    5, budget), LevelPlanStatus.WorkLimit);

            Failure(LevelPlanSearch.Search([new(int.MinValue, int.MaxValue, int.MaxValue)], new(0, 0),
                (_, _) => [], _ => 0, _ => throw new Exception("Terminal work started."), 0,
                new(int.MaxValue, 0, long.MaxValue, 8)), LevelPlanStatus.WorkLimit);
        });
        Check("visited candidates include unreachable successors and cannot exceed the declared work", () =>
        {
            var result = LevelPlanSearch.Search([new(0, 0, 0), new(0, 0, 0)], new(0, 0),
                (_, state) => [new(1, new(1, 0), 0), new(0, state, 0)], _ => 0, _ => true, 1, new(1, 4, 2, 8));
            Failure(result, LevelPlanStatus.WorkLimit);
        });
        Check("exact large scores succeed and both reward and accumulated bit growth are bounded", () =>
        {
            var large = BigInteger.One << 80;
            var layers = ImmutableArray.Create(new LevelPlanBounds(0, 0, 0), new LevelPlanBounds(0, 0, 0), new LevelPlanBounds(0, 0, 0));
            LevelPlanResult Search(BigInteger first, BigInteger second, int bits) => LevelPlanSearch.Search(layers, new(0, 0),
                (day, state) => [new(5, state, day == 0 ? first : second)], _ => 5, _ => true, 2, new(1, 2, 3, bits));
            Optimal(Search(-large, -5, 81), -large - 5, 5, 5);
            Failure(Search(-256, 0, 8), LevelPlanStatus.WorkLimit);
            Failure(Search(200, 100, 8), LevelPlanStatus.WorkLimit);
        });
        Check("both initial and active enumeration observe cancellation", () =>
        {
            using var stopped = new CancellationTokenSource();
            using var stopping = new CancellationTokenSource();
            stopped.Cancel();
            IEnumerable<LevelPlanCandidate> Candidates(int day, LevelPlanState state)
            {
                yield return new(1, state, 0);
                stopping.Cancel();
                yield return new(0, state, 0);
                throw new Exception("Continued enumeration after cancellation.");
            }
            foreach (var token in new[] { stopped.Token, stopping.Token })
            {
                try
                {
                    LevelPlanSearch.Search([new(0, 0, 0), new(0, 0, 0)], new(0, 0),
                        Candidates, _ => 0, _ => true, 2, new(1, 2, 2, 8), token);
                }
                catch (OperationCanceledException error) when (error.CancellationToken == token) { continue; }
                throw new Exception("Expected cancellation.");
            }
        });

        return passed;
    }

    private static void Optimal(LevelPlanResult result, BigInteger score, params int[] moves)
    {
        if (result.Status != LevelPlanStatus.Optimal || result.Score != score || !result.Moves.SequenceEqual(moves))
            throw new Exception($"Expected {score} on moves [{string.Join(",", moves)}], got {result.Status} {result.Score} on moves [{string.Join(",", result.Moves)}].");
    }

    private static void Failure(LevelPlanResult result, LevelPlanStatus status)
    {
        if (result.Status != status || result.Score is not null || !result.Moves.IsEmpty)
            throw new Exception($"Expected {status} without a plan, got {result.Status}.");
    }
}
