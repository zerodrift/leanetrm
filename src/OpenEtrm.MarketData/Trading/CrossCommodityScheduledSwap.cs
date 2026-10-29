using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

[EconomicDefinition("CrossCommodityScheduledSwap_v2")]
public sealed record CrossCommodityScheduledSwap:SidedInstrument
{
    public const string Implementation="factor_scheduled_financial_swap_v1";
    [IdentityField] public bool Discounting { get; }
    [IdentityField] public QuantityFactor Factor { get; }
    [IdentityField] public string UnitConversionImplementation=>UnitConverter.Implementation;
    public FloatingSide First=>(FloatingSide)Sides[0].Side;
    public FloatingSide Second=>(FloatingSide)Sides[1].Side;
    public override ReferenceKey Currency=>First.Currency;
    public override Quantity UnitQuantity=>First.UnitQuantity;
    public override DefinitionIdentity PricingBindingIdentity=>DefinitionIdentity.For(new ScheduledProjectionBindings(Sides.Select(m=>m.Side.PricingBindingIdentity!.Canonical).ToImmutableArray()));
    public CrossCommodityScheduledSwap(Guid id,ImmutableArray<SideMembership> sides,QuantityFactor factor,UnitConverter converter,bool discounting,string implementation):base(id,sides)
    {
        var one=ExactDecimal.Parse("1");var minus=ExactDecimal.Parse("-1");
        if(implementation!=Implementation||factor is null||converter is null||sides.Length!=2||sides[0].Side is not FloatingSide a||sides[1].Side is not FloatingSide b||a.Periods.IsDefaultOrEmpty||b.Periods.IsDefaultOrEmpty||a.Currency!=b.Currency||
            !(sides[0].Coefficient==one&&sides[1].Coefficient==minus||sides[0].Coefficient==minus&&sides[1].Coefficient==one))
            throw new MarketDataException("cross_commodity_swap","Select two floating scheduled sides, one exact currency, explicit directions and a typed contractual quantity factor.");
        if(converter.Specification(a.UnitQuantity.Specification).Commodity==converter.Specification(b.UnitQuantity.Specification).Commodity)
            throw new MarketDataException("cross_commodity_swap","The factor contract requires distinct commodities; use the existing contract for same-commodity sides.");
        if(sides[0].QuantityRatio!=ExactRatio.One||sides[1].QuantityRatio!=factor.NativeRatio(a.UnitQuantity.Specification,b.UnitQuantity.Specification,converter))
            throw new MarketDataException("quantity_mapping","The native side sizes must match the explicitly selected factor and unit conversions exactly.");
        SwapSidePeriods.Monthly(a);SwapSidePeriods.Monthly(b);
        var first=a.Periods;var second=b.Periods;
        if(first.Length!=second.Length||first.Where((p,n)=>p.DeliveryFrom!=second[n].DeliveryFrom||p.DeliveryTo!=second[n].DeliveryTo).Any()||
            !a.Quantities.SequenceEqual(b.Quantities)||a.Quantities.Aggregate((x,y)=>x.Add(y))!=ExactRatio.One)
            throw new MarketDataException("quantity_mapping","A constant factor requires the same delivery periods and proportional quantities in every period. Payment and observation rules remain independent.");
        Factor=factor;Discounting=discounting;
    }
    public override InstrumentValuation PV(SideValuationContext context)=>FinancialSwapInstrument.RequireDiscounting(base.PV(context),Discounting);
}
