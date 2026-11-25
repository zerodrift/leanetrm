using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.Shared.Numerics;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Options;

internal sealed record SwingForwardPlan(ImmutableArray<SignedRatio> QuantityRatios, SignedRatio ValueToUs)
{
    // One signed amount to us for an entire base parcel on each open day, in
    // grid.Days order. Each returned fraction multiplies that same parcel. The
    // score excludes fixed elections, whose economics belong to the ordinary Sides.
    internal static SwingForwardPlan Search(SwingPlanGrid grid, ImmutableArray<SignedRatio> baseParcelValuesToUs,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (baseParcelValuesToUs.IsDefault || baseParcelValuesToUs.Length != grid.Days.Length ||
            baseParcelValuesToUs.Any(amount => amount.Denominator.Sign <= 0))
            throw new MarketDataException("swing_model_scope", "Supply a signed reference-parcel value for each open swing day.");
        var terms = grid.Period.Right;
        int sign = terms.Holder == ExerciseHolder.Us ? 1 : -1;
        var arithmetic = new SwingPlanArithmetic(grid.Budget.MaximumIntegerBits);
        var amounts = new (SignedRatio Minimum, SignedRatio Step)[grid.Days.Length];
        BigInteger scale = 1;
        for (var day = 0; day < amounts.Length; day++)
        {
            cancellation.ThrowIfCancellationRequested();
            var amount = baseParcelValuesToUs[day];
            var minimum = arithmetic.Multiply(amount, terms.Minimum);
            var step = arithmetic.Multiply(amount, terms.Step);
            amounts[day] = (minimum, step);
            scale = arithmetic.Lcm(arithmetic.Lcm(scale, minimum.Denominator), step.Denominator);
        }
        var scores = amounts.Select(pair => (Minimum: sign * arithmetic.Product(pair.Minimum.Numerator, scale / pair.Minimum.Denominator),
            Step: sign * arithmetic.Product(pair.Step.Numerator, scale / pair.Step.Denominator))).ToArray();
        IEnumerable<LevelPlanCandidate> Candidates(int day, LevelPlanState state)
        {
            var score = scores[day];
            var moves = grid.Moves(day, state);
            for (var rank = 0; rank < moves.Count; rank++)
            {
                var index = moves.IndexAt(rank);
                var reward = arithmetic.Add(score.Minimum, arithmetic.Product(index, score.Step));
                yield return new(rank, grid.Successor(day, state, index), reward);
            }
        }
        var result = LevelPlanSearch.Search([..grid.Coordinates.Select(LevelPlanLayer.Sparse)], grid.Opening, Candidates,
            _ => 0, _ => true, grid.ActionCounts.Sum(), grid.Budget with { MaximumIntegerBits = arithmetic.Bits }, cancellation);
        if (result.Status == LevelPlanStatus.WorkLimit) throw SwingPlanGrid.Scope("integer score bits", arithmetic.Bits);
        if (result.Status != LevelPlanStatus.Optimal) throw SwingPlanGrid.NoCompletion();
        var plan = ImmutableArray.CreateBuilder<SignedRatio>(result.Moves.Length);
        var state = grid.Opening;
        for (var day = 0; day < result.Moves.Length; day++)
        {
            cancellation.ThrowIfCancellationRequested();
            var index = grid.Moves(day, state).IndexAt(result.Moves[day]);
            plan.Add(arithmetic.Add(terms.Minimum, arithmetic.Multiply(terms.Step, new(index, 1))));
            state = grid.Successor(day, state, index);
        }
        return new(plan.MoveToImmutable(), new(sign * result.Score!.Value, scale));
    }
}
