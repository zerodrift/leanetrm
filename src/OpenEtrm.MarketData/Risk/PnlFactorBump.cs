using System.Collections.Immutable;
using System.Numerics;

namespace OpenEtrm.MarketData.Risk;

// The price bump of a P&L report's opening Greeks, as its request selects it. A factor priced by a holding that declares
// a price step is bumped by PriceSteps steps, rounded up to whole trading ticks. Every other factor, and every factor
// when no price steps are selected, is bumped by Ticks trading ticks.
public sealed record PnlFactorBump(int Ticks,int? PriceSteps)
{
    public const int MaximumTicks=1000;
    // Estimates are taken at half, the whole and double the bump. Two and four steps put all three on whole steps of the
    // holding that declared the step. An odd count puts the half step between two bends, one step puts it inside a
    // step, and more than four put the double step more than eight steps out.
    public static readonly ImmutableArray<int> SelectablePriceSteps=[2,4];

    // A selection is explicit and bounded.
    public void Require()
    {
        if(Ticks is <1 or >MaximumTicks||PriceSteps is {} steps&&!SelectablePriceSteps.Contains(steps))
            throw Error($"Select 1 to {MaximumTicks} trading ticks and, where holdings declare a price step, {string.Join(" or ",SelectablePriceSteps)} steps or none.");
    }

    // One factor's nominal bump, from its trading tick and the largest price step its holdings declare, if any.
    public ExactDecimal Size(ExactDecimal tick,ExactDecimal? priceStep)
    {
        Require();
        if(tick.CompareTo(default)<=0||priceStep is {} declared&&declared.CompareTo(default)<=0)
            throw Error("A factor needs a positive trading tick and, where one is declared, a positive price step.");
        // The whole ticks that span this many declared steps.
        BigInteger Spanning(int steps)=>priceStep is {} step?(step.ScaledValue*steps+tick.ScaledValue-1)/tick.ScaledValue:BigInteger.Zero;
        // However it is selected, a bump is never under two declared steps: its half step would lie inside one step,
        // where a second difference reads the model's grid, not its curvature.
        if(PriceSteps is null&&Spanning(SelectablePriceSteps[0]) is var needed&&needed>Ticks)
            throw Error($"The selected bump of {(Ticks==1?"1 tick":Ticks+" ticks")} is smaller than two price steps that a holding declares for this factor, which span {needed} ticks. "+
                (needed<=MaximumTicks?$"Select price steps, or at least {needed} ticks.":"Select price steps."));
        var ticks=priceStep is not null&&PriceSteps is {} count?Spanning(count):Ticks;
        try
        {
            var size=ExactDecimal.FromScaled(ticks*tick.ScaledValue);
            _=size.DivideExact(ExactDecimal.Parse("2"));_=size+size;
            return size;
        }
        catch(MarketDataException e){throw new MarketDataException("pnl_factor_bump","Each bump must admit exact positive half, nominal and double steps.",e);}
    }
    private static MarketDataException Error(string message)=>new("pnl_factor_bump",message);
}
