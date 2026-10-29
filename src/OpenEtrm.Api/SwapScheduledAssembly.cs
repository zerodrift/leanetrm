using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

internal sealed record ResolvedFixedSwapPeriod(QuoteSpecification Quote,Quantity UnitQuantity,UnitPrice Price,FixedSidePeriod Period);
internal sealed record ResolvedFloatingSwapPeriod(PriceSettlementSelection Settlement,QuoteSpecification Quote,Quantity UnitQuantity,FloatingSidePeriod Period);

// Authoring resolves parameter records. Only the resulting whole-term definition
// is a Side, so a generated payment period never acquires an independent side ID.
internal static class SwapScheduledAssembly
{
    internal static UnitPrice? Spread(string? value,QuoteSpecification quote)
    {
        if(value is null)return null;
        var amount=ExactDecimal.Parse(value);
        return amount==default?null:new(amount,PriceUnit.FromQuote(quote));
    }
    internal static FixedSide Fixed(Guid id,ImmutableArray<ResolvedFixedSwapPeriod> periods,ImmutableArray<ExactRatio> quantities)
    {
        Require(periods.Length,quantities);
        if(periods.IsDefaultOrEmpty || periods.Any(p=>p is null) || periods.Any(p=>p.Quote!=periods[0].Quote || p.UnitQuantity!=periods[0].UnitQuantity))
            throw new MarketDataException("swap_definition","Every fixed period requires the same exact quote and external quantity basis.");
        var prices=periods.Select(p=>p.Price).Distinct().ToArray();
        FixedPriceTerm price=prices.Length==1?new FlatPrice(prices[0]):
            new SteppedPrice([..periods.Select(p=>new PriceStep(p.Period.DeliveryFrom,p.Price))]);
        return new(id,price,periods[0].Quote,periods[0].UnitQuantity,[..periods.Select((p,n)=>p.Period with{Allocation=quantities[n]})]);
    }
    internal static FloatingSide Floating(Guid id,ImmutableArray<ResolvedFloatingSwapPeriod> periods,ImmutableArray<ExactRatio> quantities)
    {
        Require(periods.Length,quantities);
        if(periods.IsDefaultOrEmpty || periods.Any(p=>p is null) || periods.Any(p=>p.Settlement!=periods[0].Settlement || p.Quote!=periods[0].Quote || p.UnitQuantity!=periods[0].UnitQuantity))
            throw new MarketDataException("swap_definition","Every floating period requires the same exact index, quote and external quantity basis.");
        return new(id,periods[0].Settlement,periods[0].Quote,periods[0].UnitQuantity,[..periods.Select((p,n)=>p.Period with{Allocation=quantities[n]})]);
    }
    private static void Require(int periods,ImmutableArray<ExactRatio> quantities)
    {
        if(quantities.IsDefaultOrEmpty || quantities.Length!=periods || quantities.Any(q=>q is null) || quantities.Aggregate((a,b)=>a.Add(b))!=ExactRatio.One)
            throw new MarketDataException("scheduled_quantity","Supply one positive exact allocation for each period, summing to one.");
    }
}
