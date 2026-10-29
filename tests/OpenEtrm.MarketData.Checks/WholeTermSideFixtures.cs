using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

// Fixture translation keeps primitive booking examples while the economic side
// stores only whole-term parameters. No valued result is used to construct terms.
internal static class WholeTermSideFixtures
{
    internal static FixedSide Fixed(ImmutableArray<FixedSide> periods,QuoteSpecification quote,ImmutableArray<ExactRatio> quantities)
    {
        if(periods.IsDefaultOrEmpty || quantities.IsDefault || quantities.Length!=periods.Length)
            throw new MarketDataException("swap_side_periods","Supply all explicit fixture periods and quantities.");
        if(periods.Any(p=>p.QuoteKey!=quote.Key || p.Price.Unit!=PriceUnit.FromQuote(quote)))
            throw new MarketDataException("swap_side_periods","Fixture periods must retain their common exact quote.");
        FixedPriceTerm price=periods.All(p=>p.Price==periods[0].Price)?periods[0].Price:
            new SteppedPrice([..periods.Select(p=>new PriceStep(p.DeliveryFrom,((FlatPrice)p.Price).Price))]);
        return new(Guid.NewGuid(),price,quote,periods[0].UnitQuantity,[..periods.Select((p,n)=>p.Periods.Single() with{Allocation=quantities[n]})]);
    }
    internal static FloatingSide Floating(ImmutableArray<FloatingSide> periods,ImmutableArray<ExactRatio> quantities)
    {
        if(periods.IsDefaultOrEmpty || quantities.IsDefault || quantities.Length!=periods.Length ||
            periods.Any(p=>p.Index!=periods[0].Index || p.Quote!=periods[0].Quote || p.UnitQuantity!=periods[0].UnitQuantity))
            throw new MarketDataException("swap_side_periods","Supply coherent fixture periods and explicit quantities.");
        return new(Guid.NewGuid(),periods[0].Settlement,periods[0].Quote,periods[0].UnitQuantity,[..periods.Select((p,n)=>p.Periods.Single() with{Allocation=quantities[n]})]);
    }
    internal static Side Monthly(ImmutableArray<Side> periods,QuoteSpecification quote,MonthlyQuantityBasis basis)
    {
        if(periods.IsDefaultOrEmpty)throw new MarketDataException("swap_side_periods","Supply the full fixture term.");
        var windows=periods.Select(p=>p switch {
            FixedSide f=>new QuantityPeriod(f.DeliveryFrom,f.DeliveryTo),FloatingSide f=>new(f.DeliveryFrom,f.DeliveryTo),
            _=>throw new MarketDataException("swap_side_periods","Only payment-side parameters belong to the fixture.")}).ToImmutableArray();
        var allocations=SwapSidePeriods.MonthlyAllocations(windows,basis);
        return periods[0] is FixedSide?Fixed([..periods.Cast<FixedSide>()],quote,allocations):Floating([..periods.Cast<FloatingSide>()],allocations);
    }
}
