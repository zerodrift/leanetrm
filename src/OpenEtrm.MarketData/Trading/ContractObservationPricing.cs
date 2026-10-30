using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Trading;

public enum ContractObservationExpiry { RequireLive=1, CarryFinalSettlement }
[EconomicDefinition("contract_observation_source_v1")]
public sealed record ContractObservationLookup(
    [property:IdentityField]CurveRequest Market,[property:IdentityField]MarketContract Contract,
    [property:IdentityField]QuoteSpecification Quote,ContractObservation Observation,
    [property:IdentityField]DateTimeOffset SelectionAt,[property:IdentityField]ReferenceKey Source,
    [property:IdentityField]SeriesKey Series,[property:IdentityField]ObservationRole Role,
    [property:IdentityField]ContractObservationExpiry ExpiryPolicy,[property:IdentityField]string Implementation)
{
    public const string SupportedImplementation="contract_observation_source_v1";
    [IdentityField]public int Ordinal=>Observation.Ordinal;
    [IdentityField]public DateOnly From=>Observation.From;
    [IdentityField]public DateOnly To=>Observation.To;
    [IdentityField]public DateTimeOffset FixingAt=>Observation.FixingAt;
    public void Validate()
    {
        if(Contract is null||Quote is null||Observation is null||Implementation!=SupportedImplementation||
            Market.Contract!=Contract.Key||Market.Curve.Revision!=Contract.Key.Revision||Series.Revision!=Market.Curve.Revision||Series.Id==Guid.Empty||
            Quote.Kind!=QuoteKind.Outright||Source.Revision!=Quote.Key.Revision||string.IsNullOrWhiteSpace(Source.Code)||
            Observation.Ordinal<0||Observation.From>=Observation.To||Observation.To.DayNumber-Observation.From.DayNumber!=1||!TradingInstant.IsSupported(FixingAt)||SelectionAt!=FixingAt||
            Role is not(ObservationRole.DailySettlement or ObservationRole.TraderMark)||!Enum.IsDefined(ExpiryPolicy)||
            Contract.Kind==ContractKind.Future&&Role!=ObservationRole.DailySettlement||Contract.Interval is not null)
            throw Invalid();
        _=PriceUnit.FromQuote(Quote);
        if(FixingAt>=Contract.Expiry&&ExpiryPolicy==ContractObservationExpiry.RequireLive)throw Invalid();
    }
    internal static MarketDataException Invalid()=>new("contract_observation_source","Retain an explicit qualified observation, exact dated source, sampling instant and expiry policy.");
    public CurveRequest? ProjectionAt(MarketSnapshot snapshot)=>FixingAt>snapshot.ValuationAt&&snapshot.ValuationAt<Contract.Expiry?Market:null;
}
public sealed record ContractObservationSample(ContractObservationLookup Lookup,CurveModel Model,MarketSnapshot Snapshot);
public sealed record ContractObservationPrice(ExactUnitPrice Price,ImmutableHashSet<ObservationKey> Lineage,SchedulePriceState State);

// Compiles admitted source snapshots into the shared scenario graph. Publication
// visibility is certified by the reader; ObservedAt remains the economic timestamp.
public sealed class ContractObservationPricing
{
    public ImmutableArray<ConnectedCurveDefinition> Definitions { get; }
    public ImmutableDictionary<ContractObservationLookup,CurvePointKey> Points { get; }
    public ConnectedCurvePlan Plan { get; }
    private readonly ImmutableDictionary<ContractObservationLookup,ContractObservationPrice> metadata;
    private ContractObservationPricing(ImmutableArray<ConnectedCurveDefinition> definitions,ImmutableDictionary<ContractObservationLookup,CurvePointKey> points,
        ImmutableDictionary<ContractObservationLookup,ContractObservationPrice> metadata,ConnectedCurvePlan plan)
    {Definitions=definitions;Points=points;this.metadata=metadata;Plan=plan;}
    public static ContractObservationPricing Compile(ImmutableArray<ContractObservationSample> samples,ResolvedCalculationContext context,ICurveBindingResolver bindings)
    {
        if(samples.IsDefaultOrEmpty||context is null||bindings is null||samples.Any(s=>s is null||s.Lookup is null||s.Model is null||s.Snapshot is null)||samples.Select(s=>s.Lookup).Distinct().Count()!=samples.Length)
            throw ContractObservationLookup.Invalid();
        var exports=new List<ConnectedCurveDefinition>();var points=ImmutableDictionary.CreateBuilder<ContractObservationLookup,CurvePointKey>();
        var values=ImmutableDictionary.CreateBuilder<ContractObservationLookup,ContractObservationPrice>();
        foreach(var sample in samples)
        {
            var lookup=sample.Lookup;lookup.Validate();
            var at=lookup.FixingAt<context.ValuationAt?lookup.FixingAt:context.ValuationAt;
            if(sample.Snapshot.ValuationAt!=at||sample.Snapshot.Definitions!=lookup.Market.Curve.Revision)throw ContractObservationLookup.Invalid();
            var carry=at>=lookup.Contract.Expiry;
            if(carry&&lookup.ExpiryPolicy!=ContractObservationExpiry.CarryFinalSettlement)throw ContractObservationLookup.Invalid();
            var export=sample.Model.Compile(sample.Snapshot,[lookup.Market],at<context.ValuationAt,context,freezeObservation:!carry&&lookup.FixingAt==context.ValuationAt);
            var key=export.Points[(lookup.Market,InputUse.Variable)];
            var point=export.Definitions.SelectMany(d=>d.Points).Single(p=>p.Key==key);
            if(point.Contract!=lookup.Contract||point.Unit!=PriceUnit.FromQuote(lookup.Quote)||point.Expression is not ConnectedSource source||
                source.Quote!=lookup.Quote||source.Input.Series!=lookup.Series||source.Observation.Source!=lookup.Source||
                source.Observation.Role!=(carry?ObservationRole.FinalContractSettlement:lookup.Role)||
                !carry&&lookup.FixingAt<=context.ValuationAt&&source.Observation.ObservedAt!=lookup.FixingAt)
                throw ContractObservationLookup.Invalid();
            exports.AddRange(export.Definitions);points.Add(lookup,key);
            values.Add(lookup,new(ExactUnitPrice.FromPrice(new(source.Observation.Value,lookup.Quote)),[source.Observation.Key],
                lookup.FixingAt<=context.ValuationAt||carry?SchedulePriceState.Actual:SchedulePriceState.Projected));
        }
        var definitions=ConnectedCurves.MergeDefinitions(exports);var retained=points.ToImmutable();
        var plan=ConnectedCurves.Compile(definitions,[..retained.Values.Distinct()],context,bindings);
        return new(definitions,retained,values.ToImmutable(),plan);
    }
    public ContractObservationPrice Resolve(ContractObservationLookup lookup,ConnectedScenario scenario)
    {
        if(!Points.TryGetValue(lookup,out var point)||scenario.Plan.Context!=Plan.Context||
            scenario.Plan.GetPoint(point)!=Plan.GetPoint(point))throw ContractObservationLookup.Invalid();
        return metadata[lookup] with{Price=scenario.Price(point)};
    }
}
