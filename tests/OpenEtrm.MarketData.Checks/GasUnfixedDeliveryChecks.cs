using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasUnfixedDeliveryChecks
{
    internal static void Run()
    {
        var f=new Fixture();var at=f.Snapshot().ValuationAt;
        var quote=f.Quotes[0] with{Multiplier=ExactDecimal.Parse("2.5")};var unit=PriceUnit.FromQuote(quote);
        var a=new CurveRequest(f.C("principal"),f.K(2));var b=new CurveRequest(f.C("asset"),f.K(5));
        var seriesA=f.S(8);var seriesB=f.S(9);
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,[quote],[f.Contracts[1],f.Contracts[4]],
            [f.Direct("principal",f.K(2),quote.Key,SettlementKind.Index),f.Direct("asset",f.K(5),quote.Key,SettlementKind.Index)],[],[],
            [new(seriesA,f.K(2),quote.Key,null),new(seriesB,f.K(5),quote.Key,null)]);
        SelectedObservation Select(SeriesKey series,string value)=>new(new(series,InputUse.Variable),new(new(f.Revision.CatalogId,Guid.NewGuid()),series,f.R("source"),
            ObservationRole.TraderMark,PublicationState.Final,at,ExactDecimal.Parse(value)));
        var snapshot=f.Snapshot() with{Selections=[Select(seriesA,"-1.01"),Select(seriesB,"2.02")]};
        var terms=new GasPhysicalDeliveryTerms(Guid.NewGuid(),f.R("physical_future"),unit.Quantity,unit.Currency,f.R("location"),
            at.AddDays(2),at.AddDays(3),at.AddDays(5),b,GasHoldingCarrying.CurrencyCurve,GasPhysicalDeliveryTerms.Version);
        var unfixed=GasUnfixedPhysicalDeliveryInstrument.Create(terms,a,unit);
        if(unfixed.Id!=GasUnfixedPhysicalDeliveryInstrument.Create(terms,a,new(unit.Currency,unit.Quantity,ExactDecimal.Parse("2.50"))).Id||
            unfixed.Id==GasUnfixedPhysicalDeliveryInstrument.Create(terms,b,unit).Id)
            throw new Exception("Unfixed exchange identities must retain exact canonical terms and principal source.");
        Reject("gas_unfixed_delivery_terms",()=>GasUnfixedPhysicalDeliveryInstrument.Create(null!,a,unit));
        Reject("gas_unfixed_delivery_terms",()=>GasUnfixedPhysicalDeliveryInstrument.Create(terms,default,unit));
        var locked=terms.CreateObligation(ExactUnitPrice.Parse("-1.01",unit));
        var provider=new Factors(terms);
        ValuationPortfolio Portfolio(Instrument instrument,string quantity)=>ValuationPortfolio.FromTrades(
            [new(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),instrument,new(ExactDecimal.Parse(quantity),unit.Quantity))])],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,provider));
        ExactMoney Value(Instrument instrument,string quantity,MarketSnapshot selected)=>RiskEngine.CalculateExact(
            RiskEngine.Prepare(model,selected,[Portfolio(instrument,quantity)]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
        // Independent arithmetic: 40*2.5*(2.02*.97-(-1.01)*.91)=287.85.
        Equal(Value(unfixed,"40",snapshot),"287.85");Equal(Value(unfixed,"-40",snapshot),"-287.85");Equal(Value(unfixed,"0.5",snapshot),"3.598125");
        Equal(Value(locked,"40",snapshot),"287.85");
        var portfolio=Portfolio(unfixed,"40");var prepared=RiskEngine.Prepare(model,snapshot,[portfolio]);
        var shifts=new[]{new QuoteShift(new(seriesA,InputUse.Variable),quote.Key,unit,ExactDecimal.Parse("0.1"),QuoteShift.Convention),
            new QuoteShift(new(seriesB,InputUse.Variable),quote.Key,unit,ExactDecimal.Parse("0.1"),QuoteShift.Convention)};
        Equal(RiskEngine.CalculateExact(RiskEngine.WithShifts(prepared,[shifts[0]]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"278.75");
        Equal(RiskEngine.CalculateExact(RiskEngine.WithShifts(prepared,[shifts[1]]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"297.55");
        var lockedPrepared=RiskEngine.Prepare(model,snapshot,[Portfolio(locked,"40")]);
        Equal(RiskEngine.CalculateExact(RiskEngine.WithShifts(lockedPrepared,[shifts[0]]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"287.85");
        Equal(RiskEngine.CalculateExact(RiskEngine.WithShifts(lockedPrepared,[shifts[1]]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"297.55");
        if(!portfolio.Declare(snapshot).Single().Dependencies.ToHashSet().SetEquals([a,b])||
            !Portfolio(locked,"40").Declare(snapshot).Single().Dependencies.SequenceEqual([b]))
            throw new Exception("Principal fixing must remove exactly its forward mark dependency.");
        Equal(Value(unfixed,"40",snapshot with{ValuationAt=terms.From}),"293.91");
        Equal(Value(unfixed,"40",snapshot with{ValuationAt=terms.CashDueAt}),"303");
        var nominal=new GasPhysicalDeliveryTerms(Guid.NewGuid(),terms.Series,terms.Quantity,terms.Currency,terms.Location,terms.From,terms.To,terms.CashDueAt,b,GasHoldingCarrying.Nominal,GasPhysicalDeliveryTerms.Version);
        var nominalUnfixed=new GasUnfixedPhysicalDeliveryInstrument(Guid.NewGuid(),nominal,a,unit);
        Equal(Value(nominalUnfixed,"40",snapshot),"303");
        if(!nominalUnfixed.RequiredDiscounts(at).IsEmpty)throw new Exception("Nominal carrying invented discount inputs.");
        var wrongUnit=new GasUnfixedPhysicalDeliveryInstrument(Guid.NewGuid(),terms,a,new(unit.Currency,unit.Quantity,ExactDecimal.Parse("1")));
        Reject("gas_delivery_unit",()=>Value(wrongUnit,"1",snapshot));
        if(unfixed.EconomicIdentity==new GasUnfixedPhysicalDeliveryInstrument(Guid.NewGuid(),terms,b,unit).EconomicIdentity)
            throw new Exception("The principal mark must belong to the economic definition.");
        Console.WriteLine("PASS unfixed physical exchange: independent A/B shocks, negative marks, signed fractional/nonunit quantities, exact distinct carrying dates and fixed-principal handoff");
    }
    private sealed class Factors(GasPhysicalDeliveryTerms terms):ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)=>
            [..payments.Select(p=>new SidePaymentFactor(p,ExactDecimal.Parse(p.PaymentAt==terms.From?"0.97":p.PaymentAt==terms.CashDueAt?"0.91":throw new Exception("Unexpected carrying date.")),snapshot,DiscountFactorSource.Explicit))];
    }
    private static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action run){try{run();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
