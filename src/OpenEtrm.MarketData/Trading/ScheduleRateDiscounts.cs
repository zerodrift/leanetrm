using System.Collections.Immutable;
using OpenEtrm.Shared.Calculation;

namespace OpenEtrm.MarketData.Trading;

public sealed record RateDiscountPillar(DateOnly Maturity, MarketObservation Observation);
public sealed record RateDiscountTenor(SeriesKey Series,DateOnly Maturity);
public sealed record RateDiscountCurveTerms(Guid DefinitionId,int DefinitionVersion,ImmutableArray<RateDiscountTenor> Tenors);

// Selected rate inputs and numerical conventions are part of discounting, not
// an unexplained factor attached to a payment. Only the pillars actually used
// by this payment enter its graph dependencies.
public sealed record RateDiscountDefinition(Guid MappingId, CurveKey Curve, ReferenceKey Currency, ReferenceKey Source,
    DateOnly ReferenceDate, DateOnly PaymentDate, RateConventionSet Conventions, string NumericalPolicy, ImmutableArray<RateDiscountPillar> Pillars)
{
    public RateDiscountTiming? Timing{get;init;}
    public RateDiscountCurveTerms? CurveTerms{get;init;}
    internal object Evidence=>Timing is null?DatedEvidence:new{Dated=DatedEvidence,Timing,CurveTerms};
    private object DatedEvidence => new { MappingId, Curve, Currency, Source, ReferenceDate, PaymentDate, Conventions, NumericalPolicy,
        Pillars = Pillars.Select(p => new { p.Maturity, p.Observation.Key, p.Observation.Series, p.Observation.Source,
            p.Observation.Role, p.Observation.State, p.Observation.ObservedAt, Mark = p.Observation.Value.ToString() }).ToArray() };
    internal string Identity => ScheduleGraph.Hash(Evidence);
    internal void Require(SidePaymentFactor factor)
    {
        if((Timing is null)!=(CurveTerms is null))
            throw new MarketDataException("discount_curve","Exact-instant timing and retained curve geometry must be supplied together.");
        var snapshot = factor.Snapshot;
        if (factor.Source != DiscountFactorSource.CurrencyDefault || MappingId == Guid.Empty || Curve.Revision != snapshot.Definitions ||
            string.IsNullOrWhiteSpace(Curve.Code) || Currency != factor.Payment.Currency || Source.Revision != Currency.Revision || string.IsNullOrWhiteSpace(Source.Code) ||
            ReferenceDate != DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime) || PaymentDate != DateOnly.FromDateTime(factor.Payment.PaymentAt.UtcDateTime) ||
            Conventions is null || NumericalPolicy != RateCurveMath.Policy ||
            Pillars.IsDefault || Pillars.Length<1||Pillars.Length>(Timing is null?2:8) || Pillars.Any(p => p is null || p.Observation is null) ||
            Pillars.Select(p => p.Observation.Series).Distinct().Count() != Pillars.Length)
            throw new MarketDataException("discount_curve", "Retain the exact selected currency mapping, rate conventions and payment's used pillars.");
        if(Timing is not null&&(Timing.CalculationAt!=snapshot.ValuationAt||Timing.PaymentAt!=factor.Payment.PaymentAt))
            throw new MarketDataException("discount_timing","Exact-instant rate evidence must retain this valuation and payment instant.");
        if(Timing is not null)RequireInstantGeometry();
        foreach (var pillar in Pillars)
        {
            var observation = pillar.Observation;
            if (observation.Series.Revision != Curve.Revision || observation.Source != Source || observation.Role != ObservationRole.Parameter ||
                observation.State != PublicationState.Final || !TradingInstant.IsSupported(observation.ObservedAt) || observation.ObservedAt > snapshot.MarketCutoff ||
                !snapshot.Selections.Any(s => s.Input == new InputKey(observation.Series, InputUse.Variable) && s.Observation == observation))
                throw new MarketDataException("rate_observation", "Every graph rate source must be the exact final parameter observation selected by the payment context.");
        }
        if (Factor([..Pillars.Select(p => RateCurveMath.DecimalRate(p.Observation.Value, Conventions.MarkUnit))]) != factor.DiscountFactor)
            throw new MarketDataException("discount_curve", "The supplied payment factor must equal its retained rate inputs under the selected numerical policy.");
    }
    private void RequireInstantGeometry()
    {
        if(CurveTerms is not {} terms||terms.DefinitionId==Guid.Empty||terms.DefinitionVersion<1||terms.Tenors.IsDefaultOrEmpty||
            terms.Tenors.Any(t=>t is null||t.Series.Revision!=Curve.Revision||t.Series.Id==Guid.Empty)||
            terms.Tenors.Select(t=>t.Series).Distinct().Count()!=terms.Tenors.Length)
            throw new MarketDataException("discount_curve","Exact-instant evidence requires its retained definition version and complete tenor geometry.");
        var dates=new[]{Timing!.CalculationAt,Timing.PaymentAt}.SelectMany(at=>
        {
            var date=DateOnly.FromDateTime(at.UtcDateTime);
            return at.TimeOfDay==TimeSpan.Zero?new[]{date}:new[]{date,date.AddDays(1)};
        }).Distinct();
        var maturities=terms.Tenors.Select(t=>t.Maturity).ToImmutableArray();
        var required=dates.SelectMany(date=>RateCurveMath.RequiredPillars(ReferenceDate,maturities,date,Conventions)).ToHashSet();
        var selected=terms.Tenors.Where((_,i)=>required.Contains(i));
        if(!selected.SequenceEqual(Pillars.Select(p=>new RateDiscountTenor(p.Observation.Series,p.Maturity))))
            throw new MarketDataException("discount_curve","The consumed observations must exactly match the required native tenor brackets, independently of mark levels.");
    }
    // The payment's factor from its used pillars' decimal annual rates; those pillars must be exactly the ones its bracket needs.
    internal ExactDecimal Factor(ImmutableArray<ExactDecimal> rates)
    {
        if (rates.Length != Pillars.Length) throw new MarketDataException("discount_curve", "Every retained rate pillar requires its exact scalar input.");
        var pillars=Pillars.Select((p,i)=>new RatePillarRate(p.Maturity,rates[i])).ToImmutableArray();
        var discount=Timing is null?RateCurveMath.Discount(ReferenceDate,pillars,PaymentDate,Conventions):
            Instant(pillars);
        if (!discount.UsedPillars.SequenceEqual(Enumerable.Range(0, Pillars.Length)))
            throw new MarketDataException("discount_curve", "Retain exactly the pillars this payment's discount factor uses.");
        return discount.Factor;
    }
    private RateDiscount Instant(ImmutableArray<RatePillarRate> pillars)
    {
        var instant=RateCurveMath.InstantDiscount(ReferenceDate,pillars,Timing!,Conventions);
        return new(instant.Factor,instant.UsedPillars);
    }
}

public sealed record ScheduleRateNode : ScheduleNode
{
    public SeriesKey Series { get; }
    public ReferenceKey Currency { get; }
    internal ScheduleRateNode(Guid owner, int index, SeriesKey series, ReferenceKey currency) : base(owner,index)
    { Series=series; Currency=currency; }
}
internal abstract partial record ScheduleGraphValue
{
    internal sealed record AnnualRate(ExactDecimal Value) : ScheduleGraphValue;
}
internal abstract partial record ScheduleGraphOperation
{
    // A payment's discount factor from its used pillars' decimal annual rates under its curve's conventions.
    internal sealed record RatePaymentFactor(RateDiscountDefinition Definition, ImmutableArray<int> Rates) : ScheduleGraphOperation(Rates)
    {
        internal override object Evidence => new { Kind="rate_payment_factor", Definition=Definition.Evidence };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var rates=ImmutableArray.CreateBuilder<ExactDecimal>(Rates.Length);
            foreach(var rate in Rates)rates.Add(((ScheduleGraphValue.AnnualRate)inputs.Get(rate)).Value);
            return new ScheduleGraphValue.Factor(Definition.Factor(rates.MoveToImmutable()));
        }
    }
}
public sealed partial class ScheduleGraphBuilder
{
    private readonly Dictionary<SeriesKey,ScheduleRateNode> rateInputs=[];
    public ScheduleRateNode RateInput(SeriesKey series) => rateInputs.TryGetValue(series,out var value)?value:
        throw new MarketDataException("rate_observation","The rate source is not a declared payment dependency.");
    private int RateDiscount(SidePaymentFactor factor)
    {
        var definition=factor.RateDefinition!; definition.Require(factor);
        var rates=ImmutableArray.CreateBuilder<int>(definition.Pillars.Length);
        foreach(var pillar in definition.Pillars)
        {
            var observed=pillar.Observation;
            // Graph rate inputs are decimal annual rates, whatever unit the curve's marks are recorded in.
            var key=new ScheduleSourceKey.Rate(definition.Currency,observed.Series,observed.Source,observed.Key,observed.ObservedAt);
            var input=Source(key,ScheduleInputUse.ModelInput,new ScheduleGraphValue.AnnualRate(RateCurveMath.DecimalRate(observed.Value,definition.Conventions.MarkUnit)),null,
                n=>new ScheduleRateNode(owner,n,observed.Series,definition.Currency));
            if(rateInputs.TryGetValue(observed.Series,out var prior) && prior!=input)
                throw new MarketDataException("rate_observation","One selected series cannot have conflicting rate source identities in a calculation.");
            rateInputs[observed.Series]=input;
            rates.Add(input.Index);
        }
        return Append(new ScheduleGraphOperation.RatePaymentFactor(definition,rates.MoveToImmutable()));
    }
}
public sealed partial class ScheduleGraphSession
{
    internal ScheduleGraphSession WithRateInputs(IReadOnlyDictionary<ScheduleRateNode,ExactDecimal> changes,CancellationToken cancellation=default)
        =>Replace(Changes(changes,value=>new ScheduleGraphValue.AnnualRate(value),cancellation),cancellation);
}
