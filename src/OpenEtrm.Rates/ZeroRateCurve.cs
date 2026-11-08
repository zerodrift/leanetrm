using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData;

namespace OpenEtrm.Rates;

public sealed record RateTenor(string Label,SeriesKey Series,DateOnly Maturity);
// A rate curve as resolved for one close: its tenors' maturities follow from that close's t_0 (D359), and its currency, mark unit
// and conventions are its own. DefinitionId and DefinitionVersion name the published definition an admitted curve pins.
public sealed record RateCurveDefinition(CurveKey Key,ReferenceKey Currency,ReferenceKey Source,RateConventionSet Conventions,
    ImmutableArray<RateTenor> Tenors,Guid DefinitionId,int DefinitionVersion);
public sealed record ZeroRateValue(SeriesKey Series,string Label,DateOnly Maturity,string Mark,ExactDecimal DecimalRate,string DiscountFactor,
    string DisplayDiscountFactor,int Days,ObservationKey Observation);
public sealed record ZeroRateBuild(Guid SnapshotId,DateOnly ReferenceDate,ReferenceKey Currency,RateConventionSet Conventions,string NumericalPolicy,
    ImmutableArray<ZeroRateValue> Values)
{
    public string MarkUnitLabel=>RateCurveMath.MarkUnitLabel(Conventions.MarkUnit);
}

// Each tenor's pillar at one close: its pinned mark in the curve's declared unit and its discount factor under the curve's
// conventions. No automatic date adjustment, implicit convention or global mutable evaluation date.
public sealed class ZeroRateCurve
{
    public const string Policy=RateCurveMath.Policy;
    private readonly RateCurveDefinition definition;
    public ZeroRateCurve(RateCurveDefinition definition)
    {
        this.definition=definition;
        if(definition.Key.Revision.CatalogId==Guid.Empty||definition.Key.Revision.RevisionId==Guid.Empty||string.IsNullOrWhiteSpace(definition.Key.Code)||
           definition.Source.Revision!=definition.Currency.Revision||definition.Currency.Revision.CatalogId==Guid.Empty||definition.Currency.Revision.RevisionId==Guid.Empty||
           string.IsNullOrWhiteSpace(definition.Currency.Code)||string.IsNullOrWhiteSpace(definition.Source.Code)||definition.Conventions is null)
            Fail("rate_definition","The exact rate definition references and conventions are incomplete.");
        if(definition.Tenors.IsDefaultOrEmpty||definition.Tenors.Select(p=>p.Series).Distinct().Count()!=definition.Tenors.Length||
           definition.Tenors.Any(t=>string.IsNullOrWhiteSpace(t.Label))||definition.Tenors.Select(t=>t.Label).Distinct().Count()!=definition.Tenors.Length)
            Fail("rate_definition","Supply distinct labelled tenors with distinct series.");
        DateOnly? previous=null;
        foreach(var tenor in definition.Tenors)
        {
            if(tenor.Series.Revision!=definition.Key.Revision||tenor.Series.Id==Guid.Empty||tenor.Maturity.Year is <1901 or >2199||previous is {} date&&tenor.Maturity<=date)
                Fail("rate_definition",$"Tenor '{tenor.Label}' must have an exact series and mature after the tenor before it.");
            previous=tenor.Maturity;
        }
    }
    public ZeroRateBuild Build(MarketSnapshot snapshot)
    {
        if(snapshot.Definitions!=definition.Key.Revision||snapshot.Id==Guid.Empty||snapshot.ValuationAt.Offset!=TimeSpan.Zero||snapshot.MarketCutoff.Offset!=TimeSpan.Zero||
           snapshot.MarketCutoff>snapshot.ValuationAt||snapshot.MarketCutoff>=new DateTimeOffset(9999,12,31,0,0,0,TimeSpan.Zero)||
           snapshot.SelectionPolicy!=InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering||snapshot.Selections.IsDefault)
            Fail("rate_snapshot","Select an exact supported UTC rate context.");
        var date=DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime);
        if(date.Year is <1901 or >2199)Fail("rate_date","The rate implementation supports dates from 1901 through 2199.");
        if(!snapshot.HasCoherentSelections())Fail("observation_conflict","Selections must have unique inputs and consistent contents for each observation identity.");
        var selections=new Dictionary<InputKey,MarketObservation>();
        foreach(var selection in snapshot.Selections)
            if(!selections.TryAdd(selection.Input,selection.Observation))Fail("observation_conflict","Repeated rate input selection is not allowed.");
        var values=ImmutableArray.CreateBuilder<ZeroRateValue>();
        foreach(var tenor in definition.Tenors)
        {
            if(tenor.Maturity<=date)Fail("rate_maturity",$"Tenor '{tenor.Label}' matures on {tenor.Maturity:yyyy-MM-dd}, which must follow the valuation date {date:yyyy-MM-dd}.");
            if(!selections.TryGetValue(new(tenor.Series,InputUse.Variable),out var observation))Fail("missing_observation",$"Tenor '{tenor.Label}' has no mark at this close.");
            if(observation!.Series!=tenor.Series||observation.Key.CatalogId!=definition.Key.Revision.CatalogId||observation.Key.Id==Guid.Empty||
               observation.Source!=definition.Source||observation.Role!=ObservationRole.Parameter||observation.State!=PublicationState.Final||
               observation.ObservedAt.Offset!=TimeSpan.Zero||observation.ObservedAt>snapshot.MarketCutoff)
                Fail("rate_observation",$"Tenor '{tenor.Label}''s mark does not match the exact source, time and finality requirements.");
            var rate=RateCurveMath.DecimalRate(observation.Value,definition.Conventions.MarkUnit);
            var days=tenor.Maturity.DayNumber-date.DayNumber;
            var factor=RateCurveMath.Factor(rate,days,definition.Conventions);
            values.Add(new(tenor.Series,tenor.Label,tenor.Maturity,observation.Value.ToString(),rate,factor.ToString(),Display(factor),days,observation.Key));
        }
        return new(snapshot.Id,date,definition.Currency,definition.Conventions,Policy,values.ToImmutable());
    }
    // Six places, half-even, for display only.
    private static string Display(ExactDecimal factor)=>
        decimal.Round(decimal.Parse(factor.ToString(),CultureInfo.InvariantCulture),6,MidpointRounding.ToEven).ToString("0.000000",CultureInfo.InvariantCulture);
    private static void Fail(string code,string message)=>throw new MarketDataException(code,message);
}
