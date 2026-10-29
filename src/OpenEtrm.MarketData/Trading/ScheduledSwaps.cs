using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

[EconomicDefinition("ScheduledFinancialSwap_v2")]
public sealed record ScheduledFinancialSwap:SidedInstrument
{
    public const string Implementation="whole_term_scheduled_financial_swap_v1";
    [IdentityField]public bool Discounting { get; }
    public Side First=>Sides[0].Side;
    public FloatingSide Second=>(FloatingSide)Sides[1].Side;
    public override ReferenceKey Currency=>First switch{FixedSide f=>f.Currency,FloatingSide f=>f.Currency,_=>throw new MarketDataException("scheduled_swap_instrument","Unsupported payment side.")};
    public override Quantity UnitQuantity=>First switch{FixedSide f=>f.UnitQuantity,FloatingSide f=>f.UnitQuantity,_=>throw new MarketDataException("scheduled_swap_instrument","Unsupported payment side.")};
    public override DefinitionIdentity PricingBindingIdentity=>DefinitionIdentity.For(new ScheduledProjectionBindings(Sides.Select(m=>m.Side).OfType<FloatingSide>().Select(s=>s.PricingBindingIdentity.Canonical).ToImmutableArray()));
    public ScheduledFinancialSwap(Guid id,ImmutableArray<SideMembership> sides,bool discounting,string implementation):base(id,sides)
    {
        var one=ExactDecimal.Parse("1");var minus=ExactDecimal.Parse("-1");
        if(implementation!=Implementation || sides.Length!=2 || sides[0].Side is not(FixedSide or FloatingSide) || sides[1].Side is not FloatingSide b || b.Periods.IsDefaultOrEmpty ||
            sides[0].Side is FixedSide{Periods.IsEmpty:true} or FloatingSide{Periods.IsEmpty:true} ||
            sides.Any(m=>m.QuantityRatio is null) || sides[0].QuantityRatio!=ExactRatio.One ||
            !(sides[0].Coefficient==one && sides[1].Coefficient==minus || sides[0].Coefficient==minus && sides[1].Coefficient==one))
            throw new MarketDataException("scheduled_swap_instrument","Select independent whole-term fixed/floating or floating/floating sides with explicit relative quantities and opposite directions.");
        if(Currency!=b.Currency || UnitQuantity!=b.UnitQuantity ||
            SwapSidePeriods.Allocations(sides[0].Side).Aggregate((a,c)=>a.Add(c))!=ExactRatio.One || b.Quantities.Aggregate((a,c)=>a.Add(c))!=ExactRatio.One)
            throw new MarketDataException("scheduled_swap_instrument","Scheduled side allocations must sum exactly to one in a common native unit and currency.");
        SwapSidePeriods.Monthly(sides[0].Side);SwapSidePeriods.Monthly(b);Discounting=discounting;
    }
    public override InstrumentValuation PV(SideValuationContext context)=>FinancialSwapInstrument.RequireDiscounting(base.PV(context),Discounting);
}
[EconomicDefinition("scheduled_projection_bindings_v2")]
internal sealed record ScheduledProjectionBindings([property:IdentityField]ImmutableArray<string> Sides);
