using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class CapturedDiscountSourceChecks
{
    internal static void Run()
    {
        var f=new Fixture();var at=f.Snapshot().ValuationAt;var context=Guid.NewGuid();
        var quote=f.Quotes[0] with{Multiplier=ExactDecimal.Parse("2.5")};var unit=PriceUnit.FromQuote(quote);
        var a=new CurveRequest(f.C("principal"),f.K(2));var seriesA=f.S(8);
        var modelA=new CurveModel(f.Revision,f.Reference,f.Quantities,[quote],[f.Contracts[1]],
            [f.Direct("principal",f.K(2),quote.Key,SettlementKind.Index)],[],[],[new(seriesA,f.K(2),quote.Key,null)]);
        var revisionB=new DefinitionRevision(f.Revision.CatalogId,Guid.NewGuid());
        var contractB=f.Contracts[4] with{Key=new(revisionB,f.K(5).Id)};
        var b=new CurveRequest(new(revisionB,"asset"),contractB.Key);var seriesB=new SeriesKey(revisionB,Guid.NewGuid());
        var modelB=new CurveModel(revisionB,f.Reference,f.Quantities,[quote],[contractB],
            [f.Direct("asset",contractB.Key,quote.Key,SettlementKind.Index) with{Key=b.Curve}],[],[],[new(seriesB,contractB.Key,quote.Key,null)]);
        MarketSnapshot Snapshot(DefinitionRevision revision,SeriesKey series,string value)=>new MarketSnapshot(Guid.NewGuid(),revision,at,at,
            InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[new(new(series,InputUse.Variable),new(new(revision.CatalogId,Guid.NewGuid()),series,
                f.R("source"),ObservationRole.TraderMark,PublicationState.Final,at,ExactDecimal.Parse(value)))]).WithKnowledge(context,at);
        var sourceA=Snapshot(f.Revision,seriesA,"-1.01");var sourceB=Snapshot(revisionB,seriesB,"2.02");
        var resolved=new ResolvedCalculationContext(context,"synthetic_captured_discount_sources",at,at,at,at);
        var exportedA=modelA.Compile(sourceA,[a],false,resolved);var exportedB=modelB.Compile(sourceB,[b],false,resolved);
        var bindings=exportedA.Points.Concat(exportedB.Points).ToImmutableDictionary(p=>p.Key.Request,p=>p.Value);
        var plan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(exportedA.Definitions.Concat(exportedB.Definitions)),[..bindings.Values],resolved,new Visible());
        var valuation=ConnectedValuation.Prepare(ConnectedScenario.Base(plan),sourceA,[sourceB]);
        var terms=new GasPhysicalDeliveryTerms(Guid.NewGuid(),f.R("physical_future"),unit.Quantity,unit.Currency,f.R("location"),
            at.AddDays(2),at.AddDays(3),at.AddDays(5),b,GasHoldingCarrying.CurrencyCurve,GasPhysicalDeliveryTerms.Version);
        var inner=GasUnfixedPhysicalDeliveryInstrument.Create(terms,a,unit);var provider=new Factors(terms,sourceB);
        ValuationPortfolio Portfolio(MarketSnapshot? selected,ISideDiscounting? supplied=null)=>ValuationPortfolio.FromTrades(
            [new(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),new BoundDelivery(inner,selected),new(ExactDecimal.Parse("40"),unit.Quantity))])],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,supplied??provider));
        PreparedConnectedPortfolioRisk Prepare(MarketSnapshot? selected,ISideDiscounting? supplied=null)=>RiskEngine.PrepareConnected(valuation,bindings,[Portfolio(selected,supplied)]);
        // Ordinary exchange economics: 40*2.5*(2.02*.97 - (-1.01)*.91).
        Equal(RiskEngine.CalculateExact(Prepare(sourceB),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"287.85");
        if(provider.Seen is null||!provider.Seen.HasSameInputs(sourceB))throw new Exception("The discount provider did not receive its original admitted B source.");
        foreach(var (dependency,expected) in new[]{(a,"278.75"),(b,"297.55")})
        {
            var child=RiskEngine.WithShifts(Prepare(sourceB),[new(CurveShockCoordinate.MarketQuote,
                new PointCurveTarget(dependency.Curve,[bindings[dependency].PointId]),unit,ExactDecimal.Parse("0.1"),CurveShift.ParentAdditive)]);
            Equal(RiskEngine.CalculateExact(child,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,expected);
            if(!ReferenceEquals(child.Valuation.Snapshot,sourceA)||!child.Valuation.SourceSnapshots.Single().HasSameInputs(sourceB)||
                provider.Seen is null||!provider.Seen.HasSameInputs(sourceB))throw new Exception("A shared price child changed its retained A/B source or B discount provider evidence.");
        }
        var declaration=Portfolio(sourceB).Declare(sourceA).Single();
        var same=Portfolio(sourceB with{Selections=[..sourceB.Selections.Reverse()]}).Declare(sourceA).Single();
        if(!declaration.SameInputFence(same)||declaration.InputFenceHash()!=same.InputFenceHash())
            throw new Exception("Equivalent exact source receipts must retain identical declaration reuse fences.");
        var changed=sourceB with{Selections=[sourceB.Selections[0] with{Observation=sourceB.Selections[0].Observation with{Value=ExactDecimal.Parse("3")}}]};
        if(declaration.SameInputFence(Portfolio(changed).Declare(sourceA).Single())||declaration.SameInputFence(Portfolio(null).Declare(sourceA).Single()))
            throw new Exception("A declaration may not reuse another selected source or replace its explicit source with primary-only semantics.");
        var explicitReceipt=JsonSerializer.SerializeToElement(declaration.DiscountInputs[0]);
        var legacyReceipt=JsonSerializer.SerializeToElement(Portfolio(null).Declare(sourceA).Single().DiscountInputs[0]);
        if(explicitReceipt.GetProperty("Source").GetProperty("Id").GetGuid()!=sourceB.Id||legacyReceipt.TryGetProperty("Source",out _))
            throw new Exception("Selected-source evidence must be retained while legacy primary-only receipt shape remains unchanged.");
        Reject("risk_dependency",()=>Prepare(changed));
        Reject("risk_dependency",()=>Prepare(sourceB with{Id=Guid.NewGuid()}));
        Reject("risk_dependency",()=>Prepare(sourceB.WithKnowledge(Guid.NewGuid(),at)));
        Reject("risk_dependency",()=>Prepare(sourceB with{ValuationAt=at.AddDays(-1),MarketCutoff=at.AddDays(-1)}));
        Reject("risk_dependency",()=>Prepare(sourceB,new Factors(terms,sourceA)));
        Reject("risk_dependency",()=>Prepare(null));
        var original=RiskEngine.CalculateExact(Prepare(null,new Factors(terms,sourceA)),[],RiskEngine.ExactNumericalPolicy);
        Equal(original.Groups.Single().BaseValues.Single().Value,"287.85");
        Console.WriteLine("PASS captured discount sources: ordinary nonunit A/B PV, original B provider receipt, exact admitted source and reuse fences, legacy primary-only contract");
    }
    private sealed record BoundDelivery(GasUnfixedPhysicalDeliveryInstrument Inner,MarketSnapshot? DiscountSource):Instrument(Inner.Id)
    {
        public override ReferenceKey Currency=>Inner.Currency;public override Quantity UnitQuantity=>Inner.UnitQuantity;
        public override bool SupportsQuantityScaling=>true;
        public override UnitPricingContract Declare(UnitValuationContext context)
        {var d=Inner.Declare(context);return d with{DiscountInputs=[..d.DiscountInputs.Select(i=>i with{Source=DiscountSource})]};}
        public override UnitValuation PV(UnitValuationContext context)=>Inner.PV(context);
    }
    private sealed class Factors(GasPhysicalDeliveryTerms terms,MarketSnapshot retained):ISideDiscounting
    {
        public MarketSnapshot? Seen{get;private set;}
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)
        {
            Seen=snapshot;
            if(!snapshot.HasSameInputs(retained))throw new MarketDataException("risk_dependency","The synthetic provider requires its exact retained source.");
            return [..payments.Select(p=>new SidePaymentFactor(p,ExactDecimal.Parse(p.PaymentAt==terms.From?"0.97":p.PaymentAt==terms.CashDueAt?"0.91":throw new Exception("Unexpected due.")),retained,DiscountFactorSource.Explicit))];
        }
    }
    private static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    private sealed class Visible:ICurveBindingResolver
    {public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new NotSupportedException();public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}}
}
