using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

public enum ExercisePeriod { DeliveryDay = 1, DeliveryMonth }
public enum ExerciseHolder { Us = 1, Counterparty }
public enum ExerciseSettlement { Physical = 1, CashAutomatic }

[EconomicDefinition("exercise_group_v1")]
public sealed record ExerciseGroup(
    [property: IdentityField] DateOnly From,
    [property: IdentityField] DateOnly To,
    [property: IdentityField] DateTimeOffset ExerciseAt);

[EconomicDefinition("exercise_right_v1")]
public sealed record ExerciseRight(
    [property: IdentityField] int Ordinal,
    [property: IdentityField] ImmutableArray<int> Slots,
    [property: IdentityField] ExercisePeriod Period,
    [property: IdentityField] ExerciseHolder Holder,
    [property: IdentityField] ExerciseSettlement Settlement,
    [property: IdentityField] string Model,
    [property: IdentityField] string DayCount,
    [property: IdentityField] ImmutableArray<ExerciseGroup> Groups);

[EconomicDefinition("exercise_curve_volatility_v1")]
public sealed record CurveVolatilityMark([property: IdentityField] CurveKey Curve, [property: IdentityField] DateOnly Month,
    VolatilityInput Volatility)
{
    [IdentityField] public Guid Id => Volatility.Id;
    [IdentityField] public string Value => Volatility.Value;
    [IdentityField] public string Convention => Volatility.Convention;
}

[EconomicDefinition("exercise_curve_correlation_v1")]
public sealed record CurveCorrelationMark([property: IdentityField] CurveKey First, [property: IdentityField] CurveKey Second,
    [property: IdentityField] DateOnly Month, [property: IdentityField] string Correlation);

[EconomicDefinition("exercise_marks_v1")]
public sealed record ExerciseMarks(ImmutableArray<CurveVolatilityMark> Volatilities, ImmutableArray<CurveCorrelationMark> Correlations,
    [property: IdentityField] string Convention)
{
    public const string HeldLogReturn = "curve_marks_log_return_held_v1";
    [IdentityField] public ImmutableArray<CurveVolatilityMark> OrderedVolatilities => [..Volatilities
        .OrderBy(v => DefinitionIdentity.CanonicalTerm(v.Curve), StringComparer.Ordinal).ThenBy(v => v.Month)];
    [IdentityField] public ImmutableArray<CurveCorrelationMark> OrderedCorrelations => [..Correlations
        .OrderBy(c => DefinitionIdentity.CanonicalTerm(c.First), StringComparer.Ordinal)
        .ThenBy(c => DefinitionIdentity.CanonicalTerm(c.Second), StringComparer.Ordinal).ThenBy(c => c.Month)];

    internal void Validate()
    {
        static bool ValidCurve(CurveKey c) => c.Revision.CatalogId != Guid.Empty && c.Revision.RevisionId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(c.Code);
        if (Convention != HeldLogReturn || Volatilities.IsDefault || Correlations.IsDefault ||
            Volatilities.Any(v => v is null || !ValidCurve(v.Curve) || v.Volatility is null || v.Month == default || v.Month.Day != 1) ||
            Volatilities.DistinctBy(v => (v.Curve, v.Month)).Count() != Volatilities.Length ||
            Volatilities.DistinctBy(v => v.Volatility.Id).Count() != Volatilities.Length ||
            Correlations.Any(c => c is null || !ValidCurve(c.First) || !ValidCurve(c.Second) || c.First == c.Second ||
                c.Month == default || c.Month.Day != 1 || !ValidCorrelation(c.Correlation)) ||
            Correlations.Any(c => Correlations.Count(other => Matches(other, c.First, c.Second, c.Month)) != 1))
            throw Missing();
    }
    private static bool ValidCorrelation(string? text)
    {
        if (text is null) return false;
        try
        {
            var value = LatticeDecimal.Parse(text);
            return value.CompareTo(LatticeDecimal.Parse("-1")) >= 0 && value.CompareTo(LatticeDecimal.Parse("1")) <= 0;
        }
        catch (MarketDataException) { return false; }
    }
    internal VolatilityInput Volatility(CurveKey curve, DateOnly month) =>
        Volatilities.SingleOrDefault(v => v.Month == month && v.Curve == curve)?.Volatility ?? throw Missing();
    internal LatticeDecimal Correlation(CurveKey a, CurveKey b, DateOnly month) =>
        LatticeDecimal.Parse(Correlations.SingleOrDefault(c => Matches(c, a, b, month))?.Correlation ?? throw Missing());
    private static bool Matches(CurveCorrelationMark mark, CurveKey a, CurveKey b, DateOnly month) =>
        mark.Month == month && (mark.First == a && mark.Second == b || mark.First == b && mark.Second == a);
    internal static MarketDataException Missing() =>
        new("exercise_marks", "Supply distinct curve and delivery-month marks under the held log-return convention.");
}

public sealed record ExerciseGroupValue(int Right, DateOnly From, DateOnly To, string State,
    ExactMoney ForwardSum, ExactMoney Counted, HolderChoiceInputs? Inputs);

public readonly record struct ExerciseGroupKey(int Right, DateOnly From, DateOnly To);
public sealed record ExerciseGroupContribution(ExerciseGroupKey Group, ExactMoney Value);
