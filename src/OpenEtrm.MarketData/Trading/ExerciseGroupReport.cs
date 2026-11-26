using OpenEtrm.MarketData.Options;

namespace OpenEtrm.MarketData.Trading;

// Exact held economics from an Instrument evaluation, including gross amounts on decided groups.
public sealed record ExerciseGroupReport(int Right, DateOnly From, DateOnly To, string State,
    string ForwardSum, string Counted, ExerciseModelInputReport? Inputs)
{
    public static ExerciseGroupReport FromValue(ExerciseGroupValue group) => new(group.Right, group.From, group.To, group.State,
        group.ForwardSum.ToExactString(), group.Counted.ToExactString(), group.Inputs is {} inputs ? ExerciseModelInputReport.FromInputs(inputs) : null);

    internal ExerciseSchedulePeriod SchedulePeriod() => new(Right, From, To,
        State is ExerciseDecisions.Undecided or ExerciseDecisions.UndecidedForecast ? "contingent" : State);
}

public sealed record ExerciseSchedulePeriod(int Right, DateOnly From, DateOnly To, string Status);

public sealed record ExerciseModelInputReport(string Receives, string Pays, string Certain, string? ReceivesVolatility,
    string? PaysVolatility, string? Correlation, string Years)
{
    internal static ExerciseModelInputReport FromInputs(HolderChoiceInputs inputs) => new(
        ExactNumberText.Format(inputs.Receives), ExactNumberText.Format(inputs.Pays), ExactNumberText.Format(inputs.Certain),
        inputs.ReceivesVolatility?.ToString(), inputs.PaysVolatility?.ToString(), inputs.Correlation?.ToString(), inputs.Years.ToString());
}
