using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// One operation owns the pinned provider. Unit contexts add the instrument's
// declaration fence; books remain independent of the curve implementation.
public abstract class PreparedValuationEnvironment
{
    public abstract MarketSnapshot Snapshot{get;}
    public abstract string ScenarioId{get;}
    public abstract DateTimeOffset CalculationAt{get;}
    internal virtual DateTimeOffset LifecycleAt=>Snapshot.ValuationAt;
    internal virtual bool IsLifecycleProjection=>false;
    public abstract string? ClockPolicy{get;}
    internal abstract ExactDecimal VolatilityOffset(Guid input);
    internal abstract ImmutableHashSet<Guid> ShiftedVolatilityInputs{get;}
    internal void RequireInputs(IEnumerable<UnitPricingContract> declarations)
    {
        var contracts=declarations.ToArray();
        foreach(var contract in contracts)
        {
            contract.RequireInputs();contract.Time.Require(LifecycleAt,CalculationAt);
            foreach(var discount in contract.DiscountInputs)Discount(discount);
        }
        var declared=contracts.SelectMany(c=>c.Volatility).GroupBy(v=>v.Id).ToDictionary(g=>g.Key,g=>
        {
            if(g.Distinct().Count()!=1)throw new MarketDataException("risk_dependency","A volatility identity cannot have conflicting calibration inputs.");
            return g.First();
        });
        foreach(var id in ShiftedVolatilityInputs)
        {
            if(!declared.TryGetValue(id,out var input))throw new MarketDataException("risk_dependency","The volatility input is outside the declared valuation closure.");
            OpenEtrm.Shared.Numerics.SignedRatio.TryParse(VolatilityOffset(id).ToString(),out var offset);
            if((input.Ratio+offset).Numerator.Sign<0)throw new MarketDataException("scenario_volatility","A shifted volatility cannot be negative.");
        }
    }
    internal SidePaymentFactor Discount(DiscountInput input)
    {
        var factors=input.Provider.ResolveForValuation([input.Payment],Snapshot,CalculationAt,ClockPolicy);
        if(factors.IsDefault||factors.Length!=1||factors[0] is null||factors[0].Payment!=input.Payment||
            !factors[0].Snapshot.HasSameInputs(Snapshot)||factors[0].DiscountFactor.CompareTo(default)<=0)
            throw new MarketDataException("risk_dependency","A discount provider must return its exact declared payment and retained source context.");
        var factor=factors[0];
        if(factor.Source==DiscountFactorSource.CurrencyDefault&&factor.RateDefinition is null)
            throw new MarketDataException("discount_curve","Currency-default discounting requires its retained rate evidence.");
        if(factor.RateDefinition is {} definition&&(CalculationAt==Snapshot.ValuationAt||definition.Timing is not null||definition.CurveTerms is not null))
        {
            if(definition.Timing is {} timing&&timing.CalculationAt!=CalculationAt)
                throw new MarketDataException("discount_timing","Exact-instant evidence must retain this calculation instant.");
            definition.Require(factor);
        }
        return factors[0];
    }
    public abstract string NumericalPolicy{get;}
    internal abstract object Identity{get;}
    internal abstract CurveBuild? Authority{get;}
    internal virtual CurveBuild? NativeIntervalScenario=>null;
    internal virtual CurveBuild? NativeProgramBasis=>null;
    internal abstract ExactUnitPrice Price(CurveRequest request,CancellationToken cancellation);
    internal virtual UnitPrice WorkingPrice(CurveRequest request,CancellationToken cancellation)=>Price(request,cancellation).ToUnitPriceExact();
    internal abstract ImmutableHashSet<ObservationKey> Lineage(CurveRequest request,CancellationToken cancellation);
    internal abstract ImmutableArray<QuoteFactor> Factors(ImmutableArray<CurveRequest> requests,CancellationToken cancellation);
    internal abstract bool Compatible(PreparedValuationEnvironment prior);
    internal abstract bool Unchanged(PreparedValuationEnvironment prior,ImmutableArray<CurveRequest> requests,CancellationToken cancellation);
    internal abstract SideValuationContext Context(ISideDiscounting? discounting,ImmutableArray<CurveRequest> dependencies,CancellationToken cancellation,PricingInputs? inputs=null);
    internal PreparedValuationEnvironment(){}
}
