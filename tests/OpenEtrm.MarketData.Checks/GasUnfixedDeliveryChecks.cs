using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasUnfixedDeliveryChecks
{
    internal static void Run(Action<SidedInstrument, SideValuationContext>? inspect = null)
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
        var otherAssetTerms=new GasPhysicalDeliveryTerms(terms.Id,terms.Series,terms.Quantity,terms.Currency,terms.Location,
            terms.From,terms.To,terms.CashDueAt,a,terms.Carrying,terms.Implementation);
        var otherAsset=new GasUnfixedPhysicalDeliveryInstrument(Guid.NewGuid(),otherAssetTerms,a,unit);
        var otherPrincipal=new GasUnfixedPhysicalDeliveryInstrument(Guid.NewGuid(),terms,b,unit);
        if(DefinitionIdentity.For(unfixed.Sides[1].Side)!=DefinitionIdentity.For(otherAsset.Sides[1].Side)||
            DefinitionIdentity.For(unfixed.Sides[0].Side)!=DefinitionIdentity.For(otherPrincipal.Sides[0].Side))
            throw new Exception("Independent physical and principal Sides must not retain the other Side's pricing terms in their economic identities.");
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
        CheckSides(unfixed);CheckSides(locked);
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

        void CheckSides(Instrument instrument)
        {
            if(instrument is not SidedInstrument sided||sided.Sides.Length!=2||
                sided.Sides[0].Coefficient!=ExactDecimal.Parse("1")||sided.Sides[1].Coefficient!=ExactDecimal.Parse("-1")||
                sided.Sides[0].Side.Id==sided.Sides[1].Side.Id)
                throw new Exception("A physical exchange must own independent delivery and principal-payment Sides, with explicit opposite memberships.");
            var selectedPortfolio=Portfolio(instrument,"40");var selected=RiskEngine.Prepare(model,snapshot,[selectedPortfolio]);
            var context=new UnitValuationContext(snapshot,selectedPortfolio.Setup,selected.Market.Pricing,selected.Contracts[0][0]);
            var value=sided.PV(context.SideContext());
            inspect?.Invoke(sided, context.SideContext());
            Equal(value.Sides[0].Value.PresentValue,"4.8985");Equal(value.Sides[1].Value.PresentValue,"-2.29775");
            Equal(value.PresentValue,"7.19625");
            var physical=sided.Sides[0].Side.GenerateSchedule().Rows.Single();var payment=sided.Sides[1].Side.GenerateSchedule().Rows.Single();
            if(physical.SideId!=sided.Sides[0].Side.Id||physical.DeliveryWindow is not ValueScheduleWindow {From:var from,To:var to}||
                from!=terms.From||to!=terms.To||payment.SideId!=sided.Sides[1].Side.Id||payment.PaymentAt!=terms.CashDueAt)
                throw new Exception("Physical and consideration Sides must expose their genuine delivery window and independent payment date.");
        }
    }
    private sealed class Factors(GasPhysicalDeliveryTerms terms):ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)=>
            [..payments.Select(p=>new SidePaymentFactor(p,ExactDecimal.Parse(p.PaymentAt==terms.From?"0.97":p.PaymentAt==terms.CashDueAt?"0.91":throw new Exception("Unexpected carrying date.")),snapshot,DiscountFactorSource.Explicit))];
    }
    private static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action run){try{run();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
