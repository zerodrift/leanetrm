using System.Collections.Immutable;
using OpenEtrm.Shared.Calculation;

namespace OpenEtrm.MarketData.Trading;

public sealed record RateDiscountPillar(DateOnly Maturity, MarketObservation Observation);

// Selected rate inputs and numerical conventions are part of discounting, not
// an unexplained factor attached to a payment. Only the pillars actually used
// by this payment enter its graph dependencies.
public sealed record RateDiscountDefinition(Guid MappingId, CurveKey Curve, ReferenceKey Specification,
    ReferenceKey Currency, ReferenceKey Source, DateOnly ReferenceDate, DateOnly PaymentDate,
    string DayCount, string Compounding, string Implementation, string DateBasis,
    string NumericalPolicy, string InterpolationPolicy, ImmutableArray<RateDiscountPillar> Pillars)
{
    internal object Evidence => new { MappingId, Curve, Specification, Currency, Source, ReferenceDate, PaymentDate,
        DayCount, Compounding, Implementation, DateBasis, NumericalPolicy, InterpolationPolicy,
        Pillars = Pillars.Select(p => new { p.Maturity, p.Observation.Key, p.Observation.Series, p.Observation.Source,
            p.Observation.Role, p.Observation.State, p.Observation.ObservedAt, AnnualRate = p.Observation.Value.ToString() }).ToArray() };
    internal string Identity => ScheduleGraph.Hash(Evidence);
    internal void Require(SidePaymentFactor factor)
    {
        var snapshot = factor.Snapshot;
        if (factor.Source != DiscountFactorSource.CurrencyDefault || MappingId == Guid.Empty || Curve.Revision != snapshot.Definitions ||
            string.IsNullOrWhiteSpace(Curve.Code) || Currency != factor.Payment.Currency || Specification.Revision != Currency.Revision ||
            Source.Revision != Currency.Revision || string.IsNullOrWhiteSpace(Specification.Code) || string.IsNullOrWhiteSpace(Source.Code) ||
            ReferenceDate != DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime) || PaymentDate != DateOnly.FromDateTime(factor.Payment.PaymentAt.UtcDateTime) ||
            DayCount != "actual_365_fixed" || Compounding != "simple" || Implementation != RateConventions.Implementation ||
            DateBasis != "valuation_utc_date" || NumericalPolicy != RateConventions.Policy || InterpolationPolicy != DiscountInterpolation.Policy ||
            Pillars.IsDefault || Pillars.Length is < 1 or > 2 || Pillars.Any(p => p is null || p.Observation is null) ||
            Pillars.Select(p => p.Observation.Series).Distinct().Count() != Pillars.Length ||
            Pillars.Length == 1 && Pillars[0].Maturity != PaymentDate ||
            Pillars.Length == 2 && !(Pillars[0].Maturity < PaymentDate && PaymentDate < Pillars[1].Maturity))
            throw new MarketDataException("discount_curve", "Retain the exact selected currency mapping, rate conventions and payment's used pillars.");
        foreach (var pillar in Pillars)
        {
            var observation = pillar.Observation;
            if (observation.Series.Revision != Curve.Revision || observation.Source != Source || observation.Role != ObservationRole.Parameter ||
                observation.State != PublicationState.Final || !TradingInstant.IsSupported(observation.ObservedAt) || observation.ObservedAt > snapshot.MarketCutoff ||
                !snapshot.Selections.Any(s => s.Input == new InputKey(observation.Series, InputUse.Variable) && s.Observation == observation))
                throw new MarketDataException("rate_observation", "Every graph rate source must be the exact final parameter observation selected by the payment context.");
        }
        if (Factor(Pillars.Select(p => p.Observation.Value).ToImmutableArray()) != factor.DiscountFactor)
            throw new MarketDataException("discount_curve", "The supplied payment factor must equal its retained rate inputs under the selected numerical policy.");
    }
    internal ExactDecimal Factor(ImmutableArray<ExactDecimal> rates)
    {
        if (rates.Length != Pillars.Length) throw new MarketDataException("discount_curve", "Every retained rate pillar requires its exact scalar input.");
        var left = ExactDecimal.Parse(RateConventions.SimpleActual365Fixed(ReferenceDate, Pillars[0].Maturity, rates[0]).Factor);
        if (rates.Length == 1) return left;
        var right = ExactDecimal.Parse(RateConventions.SimpleActual365Fixed(ReferenceDate, Pillars[1].Maturity, rates[1]).Factor);
        return DiscountInterpolation.Factor(Pillars[0].Maturity, left, Pillars[1].Maturity, right, PaymentDate, InterpolationPolicy);
    }
}

public sealed record ScheduleRateNode : ScheduleNode
{
    public SeriesKey Series { get; }
    public ReferenceKey Specification { get; }
    internal ScheduleRateNode(Guid owner, int index, SeriesKey series, ReferenceKey specification) : base(owner,index)
    { Series=series; Specification=specification; }
}
internal abstract partial record ScheduleGraphValue
{
    internal sealed record AnnualRate(ExactDecimal Value) : ScheduleGraphValue;
}
internal abstract partial record ScheduleGraphOperation
{
    internal sealed record RatePillarFactor(int Rate, DateOnly ReferenceDate, DateOnly Maturity) : ScheduleGraphOperation([Rate])
    {
        internal override object Evidence => new { Kind="rate_pillar_factor", ReferenceDate, Maturity, Policy=RateConventions.Policy };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return new ScheduleGraphValue.Factor(ExactDecimal.Parse(RateConventions.SimpleActual365Fixed(ReferenceDate,Maturity,
                ((ScheduleGraphValue.AnnualRate)inputs.Get(Rate)).Value).Factor));
        }
    }
    internal sealed record RatePaymentFactor(RateDiscountDefinition Definition, ImmutableArray<int> Factors) : ScheduleGraphOperation(Factors)
    {
        internal override object Evidence => new { Kind="rate_payment_factor", Definition=Definition.Evidence };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var left=((ScheduleGraphValue.Factor)inputs.Get(Factors[0])).Value;
            return new ScheduleGraphValue.Factor(Factors.Length==1?left:DiscountInterpolation.Factor(Definition.Pillars[0].Maturity,left,
                Definition.Pillars[1].Maturity,((ScheduleGraphValue.Factor)inputs.Get(Factors[1])).Value,Definition.PaymentDate,Definition.InterpolationPolicy));
        }
    }
}
public sealed partial class ScheduleGraphBuilder
{
    private readonly Dictionary<SeriesKey,ScheduleRateNode> rateInputs=[];
    private readonly Dictionary<(int Rate,DateOnly Reference,DateOnly Maturity),int> rateFactors=[];
    public ScheduleRateNode RateInput(SeriesKey series) => rateInputs.TryGetValue(series,out var value)?value:
        throw new MarketDataException("rate_observation","The rate source is not a declared payment dependency.");
    private int RateDiscount(SidePaymentFactor factor)
    {
        var definition=factor.RateDefinition!; definition.Require(factor);
        var factors=ImmutableArray.CreateBuilder<int>(definition.Pillars.Length);
        foreach(var pillar in definition.Pillars)
        {
            var observed=pillar.Observation;
            var key=new ScheduleSourceKey.Rate(definition.Specification,observed.Series,observed.Source,observed.Key,observed.ObservedAt);
            var input=Source(key,ScheduleInputUse.ModelInput,new ScheduleGraphValue.AnnualRate(observed.Value),null,
                n=>new ScheduleRateNode(owner,n,observed.Series,definition.Specification));
            if(rateInputs.TryGetValue(observed.Series,out var prior) && prior!=input)
                throw new MarketDataException("rate_observation","One selected series cannot have conflicting rate source identities in a calculation.");
            rateInputs[observed.Series]=input;
            var factorKey=(input.Index,definition.ReferenceDate,pillar.Maturity);
            if(!rateFactors.TryGetValue(factorKey,out var node))
            { node=Append(new ScheduleGraphOperation.RatePillarFactor(input.Index,definition.ReferenceDate,pillar.Maturity));rateFactors.Add(factorKey,node); }
            factors.Add(node);
        }
        return Append(new ScheduleGraphOperation.RatePaymentFactor(definition,factors.MoveToImmutable()));
    }
}
public sealed partial class ScheduleGraphSession
{
    internal ScheduleGraphSession WithRateInputs(IReadOnlyDictionary<ScheduleRateNode,ExactDecimal> changes,CancellationToken cancellation=default)
        =>Replace(Changes(changes,value=>new ScheduleGraphValue.AnnualRate(value),cancellation),cancellation);
}
