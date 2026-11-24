using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasRightsSideChecks
{
    internal static void Run(Action<SidedInstrument, SideValuationContext>? inspect = null)
    {
        var f=new GasTransportDirectRightsChecks.MarkFixture("20");var source=(ConnectedSource)f.Service.Expression;
        var captured=new CapturedPriceSource(source.Request,ConnectedValuation.Prepare(f.Base,f.Snapshot),f.Service.Key);
        var id=Guid.NewGuid();var through=f.At.AddDays(1);var policy=GasTransportDirectRightsPolicy.ExpiryAtThrough;
        var definition=GasTransportDirectRightsInstrument.DefineSide(id,f.Terms,f.State,through,policy,null);
        Instrument rights=new GasTransportDirectRightsInstrument(id,f.Terms,f.State,through,policy,null,captured,f.Mark,f.Evaluation);
        if(rights is not SidedInstrument sided||sided.Sides.Length!=1||sided.Sides[0].Side.Id!=definition.Id||
            sided.Sides[0].Coefficient!=ExactDecimal.Parse("1")||rights.SupportsQuantityScaling||!rights.SupportsWholePosition)
            throw new Exception("The whole signed rights Instrument must use the same genuine Side definition before and after mark selection, without size scaling.");
        if(DefinitionIdentity.For(definition)!=DefinitionIdentity.For(sided.Sides[0].Side)||definition.PricingBindingIdentity is not null||sided.Sides[0].Side.PricingBindingIdentity is null)
            throw new Exception("Capturing a rights price must preserve Side economics and add only its exact pricing binding.");
        var changed=GasTransportDirectRightsInstrument.DefineSide(id,f.Terms,f.State with{CapacityVersionId=Guid.NewGuid()},through,policy,null);
        var extended=GasTransportDirectRightsInstrument.DefineSide(id,f.Terms,f.State,through.AddDays(1),policy,null);
        if(DefinitionIdentity.For(definition)==DefinitionIdentity.For(changed)||DefinitionIdentity.For(definition)==DefinitionIdentity.For(extended))
            throw new Exception("Legal rights state and horizon changes must remain changes of Side economic definition.");
        var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),rights,rights.UnitQuantity)]);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null);
        var prepared=RiskEngine.PrepareConnected(captured.Valuation,ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(source.Request,f.Service.Key),[ValuationPortfolio.FromTrades([trade],setup)]);
        var context=new UnitValuationContext(f.Snapshot,setup,prepared.Market,prepared.Contracts[0][0]).SideContext();
        try{definition.PV(context);throw new Exception("An unpriced rights Side returned a value.");}
        catch(MarketDataException error)when(error.Code=="gas_transport_rights_mark"){}
        var actual=sided.PV(context).Sides.Single().Value;
        inspect?.Invoke(sided, context);
        var row=actual.InspectSchedule("synthetic_rights_side",ExactDecimal.Parse("1"),new SignedRatio(1,1),
            date=>new(date.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero)).Read(0,4).Rows.Single();
        if(actual.PresentValue.ToExactString()!="20"||row.Kind!="Holding"||row.PresentValue!="20"||row.PaymentAt is not null||
            row.DiscountFactor is not null||row.Start is not null||row.End is not null||definition.GenerateSchedule().Rows.Count!=1)
            throw new Exception("A current rights Side must expose the exact supplied PV, without fake cash, physical performance or a second discount.");
        Console.WriteLine("PASS whole rights Side: unpriced definition, stable real membership, signed current PV and noncash inspection");
    }
}
