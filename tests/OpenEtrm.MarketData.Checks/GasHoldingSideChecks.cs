using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasHoldingSideChecks
{
    internal static void Run(Action<SidedInstrument, SideValuationContext>? inspect = null)
    {
        var f=new GasTransportDirectRightsChecks.MarkFixture("20");var price=(ConnectedSource)f.Gas.Expression;
        Instrument stock=new GasCommodityHoldingInstrument(Guid.NewGuid(),price.Request,f.Gas.Unit.Quantity,f.Terms.Currency,null,GasHoldingCarrying.Nominal);
        if(stock is not SidedInstrument sided||sided.Sides.Length!=1||sided.Sides[0].Coefficient!=ExactDecimal.Parse("1"))
            throw new Exception("A gas claim must own its genuine marked-value Side, with title quantity and sign held independently.");
        var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),stock,new(ExactDecimal.Parse("100"),stock.UnitQuantity.Specification))]);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null);
        var prepared=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(f.Base,f.Snapshot),
            ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(price.Request,f.Gas.Key),[ValuationPortfolio.FromTrades([trade],setup)]);
        var bump=new ConnectedRiskBump(CurveShockCoordinate.MarketQuote,new PointCurveTarget(f.Gas.Key.Curve,[f.Gas.Key.PointId]),
            f.Gas.Unit,ExactDecimal.Parse("1"),ConnectedRiskBump.Convention);
        var result=RiskEngine.CalculateExact(prepared,[bump],RiskEngine.ExactNumericalPolicy).Groups.Single().Factors.Single().Totals.Single().Values;
        if(result.Base.ToExactString()!="300"||result.Up.ToExactString()!="400"||result.Down.ToExactString()!="200")
            throw new Exception("The gas Side must value 100 real units at 3, with price-only shocks applied once.");
        var context=new UnitValuationContext(f.Snapshot,setup,prepared.Market,prepared.Contracts[0][0]);
        var value=sided.PV(context.SideContext()).Sides.Single().Value;
        inspect?.Invoke(sided, context.SideContext());
        var row=value.InspectSchedule("synthetic_gas_claim",ExactDecimal.Parse("100"),new SignedRatio(1,1),
            day=>new(day.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero)).Read(0,4).Rows.Single();
        if(row.Kind!="Holding"||row.Quantity!="100"||row.Price!="3"||row.PresentValue!="300"||
            row.PaymentAt is not null||row.Start is not null||row.End is not null||row.DiscountFactor is not null)
            throw new Exception("A nominal commodity holding Side must expose a market value, without inventing cash or delivery dates.");
        Console.WriteLine("PASS gas holding Side: actual stock mark, independent signed size, shared factor PV and honest holding inspection");
    }
}
