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

                var layers = Enumerable.Range(0, 5).Select(day => LevelPlanLayer.Sparse(
                    [.. from level in Enumerable.Range(day, day * 2 + 1)
                        from count in Enumerable.Range(0, (maximumChanges ?? 0) + 1)
                        select new LevelPlanState(level, count)])).ToImmutableArray();
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
        Check("three sparse day-one coordinates fit their budget across a million-level span", () =>
        {
            // Independently list two-day schedules with one day at the base of 1.
            // Allow at most one changed day and require total [1,000,000, 1,000,001].
            var feasible = new List<(int First, int Second)>();
            for (var quantity = 0; quantity <= 1_000_000; quantity++)
            {
                if (quantity + 1 is < 1_000_000 or > 1_000_001) continue;
                feasible.Add((quantity, 1));
                feasible.Add((1, quantity));
            }
            var expected = feasible.MaxBy(path => 2 * path.First + 3 * path.Second);
            LevelPlanState[] dayOne = [.. feasible.Select(path => new LevelPlanState(path.First, path.First == 1 ? 1 : 0))
                .Distinct().OrderBy(state => state.Level)];
            LevelPlanState[] terminal = [.. feasible.Select(path => new LevelPlanState(path.First + path.Second, 0))
                .Distinct().OrderBy(state => state.Level)];
            if (!dayOne.SequenceEqual([new(1, 1), new(999_999, 0), new(1_000_000, 0)]))
                throw new Exception("The listing should have exactly three distinct day-one coordinates.");

            var layers = ImmutableArray.Create(LevelPlanLayer.Sparse([new(0, 1)]),
                LevelPlanLayer.Sparse([.. dayOne]), LevelPlanLayer.Sparse([.. terminal]));
            IEnumerable<LevelPlanCandidate> Candidates(int day, LevelPlanState state)
            {
                yield return new(1, new(state.Level + 1, state.Count), 2 + day);
                if (state.Count == 0) yield break;
                foreach (var change in new[] { 500_000, 999_999, 1_000_000 })
                {
                    // A high reward for a missing coordinate must not create a path.
                    var reward = change == 500_000 ? 4_000_000 : change * (2 + day);
                    yield return new(change, new(state.Level + change, 0), reward);
                }
            }

            var result = LevelPlanSearch.Search(layers, new(0, 1), Candidates, _ => 1, _ => true,
                10, new(3, 10, 6, 22));
            Optimal(result, 2 * expected.First + 3 * expected.Second, expected.First, expected.Second);
        });
        Check("ties prefer the closest shifted rest and then the lower move in chronological order", () =>
        {
            var layers = ImmutableArray.Create(LevelPlanLayer.Range(0, 0), LevelPlanLayer.Range(0, 0), LevelPlanLayer.Range(0, 0));
            IEnumerable<LevelPlanCandidate> Candidates(int day, LevelPlanState state) =>
                day == 0 ? [new(6, state, 0), new(4, state, 0), new(0, state, 0)] :
                    [new(int.MinValue, state, 0), new(int.MaxValue, state, 0)];
            Optimal(LevelPlanSearch.Search(layers, new(0, 0), Candidates, day => day == 0 ? 5 : 1,
                _ => true, 5, new(1, 5, 3, 1)), 0, 4, int.MaxValue);

            Optimal(LevelPlanSearch.Search([LevelPlanLayer.Range(0, 0), LevelPlanLayer.Range(0, 0)], new(0, 0),
                (_, state) => [new(0, state, 0), new(5, state, 0)], _ => 5, _ => true, 2, new(1, 2, 2, 1)), 0, 5);
        });
        Check("a missing successor and a disconnected feasible terminal do not yield a plan", () =>
        {
            var result = LevelPlanSearch.Search([LevelPlanLayer.Range(0, 0), LevelPlanLayer.Range(0, 1)], new(0, 0),
                (_, state) => [new(0, state, 0), new(2, new(2, 0), 50)], _ => 0, state => state.Level == 1,
                2, new(2, 2, 3, 8));
            Failure(result, LevelPlanStatus.Infeasible);
        });
        Check("sparse coordinates are validated and an empty successor layer is infeasible", () =>
        {
            var origin = LevelPlanLayer.Sparse([new(0, 0)]);
            LevelPlanResult Search(LevelPlanLayer terminal, LevelPlanState opening) =>
                LevelPlanSearch.Search([origin, terminal], opening, (_, _) => throw new Exception("Candidate work started."),
                    _ => 0, _ => throw new Exception("Terminal work started."), 0, new(3, 0, 4, 1));

            Failure(Search(LevelPlanLayer.Sparse([]), new(0, 0)), LevelPlanStatus.Infeasible);
            foreach (var layer in new[] {
                default, LevelPlanLayer.Sparse([new(2, 0), new(1, 0)]),
                LevelPlanLayer.Sparse([new(1, 0), new(1, 0)]), LevelPlanLayer.Sparse([new(1, -1)]),
                LevelPlanLayer.Sparse([new(1, 2), new(1, 1)]) })
                RejectArgument(() => Search(layer, new(0, 0)));
            RejectArgument(() => Search(LevelPlanLayer.Sparse([new(1, 0)]), new(0, 1)));
        });
        Check("terminal-only plans distinguish a feasible exact zero from infeasibility", () =>
        {
            var layers = ImmutableArray.Create(LevelPlanLayer.Range(0, 0));
            Optimal(LevelPlanSearch.Search(layers, new(0, 0), (_, _) => throw new Exception("No moves are needed."),
                _ => throw new Exception("No rest move is needed."), _ => true, 0, new(1, 0, 1, 1)), 0);
            Failure(LevelPlanSearch.Search(layers, new(0, 0), (_, _) => [], _ => 0, _ => false, 0, new(1, 0, 1, 1)),
                LevelPlanStatus.Infeasible);
        });
        Check("preflight refuses level, candidate and reconstruction limits before invoking callbacks", () =>
        {
            var layer = LevelPlanLayer.Sparse([new(0, 0), new(0, 1), new(1, 0), new(1, 1)]);
            var layers = ImmutableArray.Create(layer, layer);
            foreach (var budget in new[] { new LevelPlanBudget(1, 5, 8, 8), new(2, 4, 8, 8), new(2, 5, 7, 8) })
                Failure(LevelPlanSearch.Search(layers, new(0, 1), (_, _) => throw new Exception("Candidate work started."),
                    _ => throw new Exception("Rest work started."), _ => throw new Exception("Terminal work started."),
                    5, budget), LevelPlanStatus.WorkLimit);

            Failure(LevelPlanSearch.Search([LevelPlanLayer.Range(int.MinValue, int.MaxValue)], new(0, 0),
                (_, _) => [], _ => 0, _ => throw new Exception("Terminal work started."), 0,
                new(int.MaxValue, 0, long.MaxValue, 8)), LevelPlanStatus.WorkLimit);
        });
        Check("visited candidates include unreachable successors and cannot exceed the declared work", () =>
        {
            var result = LevelPlanSearch.Search([LevelPlanLayer.Range(0, 0), LevelPlanLayer.Range(0, 0)], new(0, 0),
                (_, state) => [new(1, new(1, 0), 0), new(0, state, 0)], _ => 0, _ => true, 1, new(1, 4, 2, 8));
            Failure(result, LevelPlanStatus.WorkLimit);
        });
        Check("exact large scores succeed and both reward and accumulated bit growth are bounded", () =>
        {
            var large = BigInteger.One << 80;
            var layers = ImmutableArray.Create(LevelPlanLayer.Range(0, 0), LevelPlanLayer.Range(0, 0), LevelPlanLayer.Range(0, 0));
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
                    LevelPlanSearch.Search([LevelPlanLayer.Range(0, 0), LevelPlanLayer.Range(0, 0)], new(0, 0),
                        Candidates, _ => 0, _ => true, 2, new(1, 2, 2, 8), token);
                }
                catch (OperationCanceledException error) when (error.CancellationToken == token) { continue; }
                throw new Exception("Expected cancellation.");
            }
        });

        return passed;
    }

    private static void RejectArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Expected an invalid layer or opening coordinate to be refused.");
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
