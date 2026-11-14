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
        var sourcePlanB=ConnectedCurves.Compile(exportedB.Definitions,[exportedB.Points[(b,InputUse.Variable)]],resolved,new Visible());
        var exactPriceSource=new CapturedPriceSource(b,ConnectedValuation.Prepare(ConnectedScenario.Base(sourcePlanB),sourceB),exportedB.Points[(b,InputUse.Variable)]);
        var sourceBound=ValuationPortfolio.FromTrades([new(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),new BoundDelivery(inner,sourceB,exactPriceSource),
            new(ExactDecimal.Parse("40"),unit.Quantity))])],Portfolio(sourceB).Setup);
        Equal(RiskEngine.CalculateExact(RiskEngine.PrepareConnected(valuation,bindings,[sourceBound]),[],RiskEngine.ExactNumericalPolicy)
            .Groups.Single().BaseValues.Single().Value,"287.85");
        DerivedSource();
        Console.WriteLine("PASS captured discount sources: ordinary nonunit A/B PV, original B provider receipt, exact admitted source and reuse fences, legacy primary-only contract");

        void DerivedSource()
        {
            var key=new CurvePointKey(new(revisionB,"synthetic_derived_asset"),Guid.NewGuid());var request=new CurveRequest(key.Curve,contractB.Key);
            var assetPoint=plan.Points[bindings[b]];
            var derived=new ConnectedCurveDefinition(key.Curve,[assetPoint with{Key=key,Expression=new ConnectedWeightedSum([
                new(CurvePointReference.Exact(bindings[a]),ExactDecimal.Parse("1")),new(CurvePointReference.Exact(bindings[b]),ExactDecimal.Parse("1"))])}]);
            var definitions=ConnectedCurves.MergeDefinitions(exportedA.Definitions.Concat(exportedB.Definitions).Append(derived));
            var native=ConnectedCurves.Compile(definitions,[key],resolved,new Visible());
            var expected=new CapturedPriceSource(request,ConnectedValuation.Prepare(ConnectedScenario.Base(native),sourceB,[sourceA]),key);
            var combined=ConnectedCurves.Compile(definitions,[bindings[a],key],resolved,new Visible());
            var actual=ConnectedValuation.Prepare(ConnectedScenario.Base(combined),sourceA,[sourceB]);
            var map=ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(a,bindings[a]).Add(request,key);
            var selectedTerms=new GasPhysicalDeliveryTerms(Guid.NewGuid(),terms.Series,terms.Quantity,terms.Currency,terms.Location,
                terms.From,terms.To,terms.CashDueAt,request,terms.Carrying,terms.Implementation);
            var exchange=GasUnfixedPhysicalDeliveryInstrument.Create(selectedTerms,a,unit);
            PreparedConnectedPortfolioRisk Bound(CapturedPriceSource retained)=>RiskEngine.PrepareConnected(actual,map,[ValuationPortfolio.FromTrades(
                [new(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),new BoundDelivery(exchange,sourceB,retained),new(ExactDecimal.Parse("40"),unit.Quantity))])],
                new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new Factors(selectedTerms,sourceB)))]);
            var prepared=Bound(expected);
            // B's explicit derived asset is A+B=1.01; principal remains -1.01.
            Equal(RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"189.88");
            var shifted=RiskEngine.WithShifts(prepared,[new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(a.Curve,[bindings[a].PointId]),unit,
                ExactDecimal.Parse("0.1"),CurveShift.ParentAdditive)]);
            Equal(RiskEngine.CalculateExact(shifted,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"190.48");
            var forgedA=sourceA with{Selections=[sourceA.Selections[0] with{Observation=sourceA.Selections[0].Observation with{Value=ExactDecimal.Parse("-0.01")}}]};
            var forgedExport=modelA.Compile(forgedA,[a],false,resolved);
            var forged=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(forgedExport.Definitions.Concat(exportedB.Definitions).Append(derived)),[key],resolved,new Visible());
            var wrong=new CapturedPriceSource(request,ConnectedValuation.Prepare(ConnectedScenario.Base(forged),sourceB,[forgedA]),key);
            Reject("risk_dependency",()=>RiskEngine.CalculateExact(Bound(wrong),[],RiskEngine.ExactNumericalPolicy));
            var wrongCertificate=sourceA.WithKnowledge(Guid.NewGuid(),at);
            var other=new CapturedPriceSource(request,ConnectedValuation.Prepare(ConnectedScenario.Base(native),sourceB,[wrongCertificate]),key);
            Reject("risk_dependency",()=>RiskEngine.CalculateExact(Bound(other),[],RiskEngine.ExactNumericalPolicy));
            var undeclared=new CapturedPriceSource(b,ConnectedValuation.Prepare(ConnectedScenario.Base(sourcePlanB),sourceB),bindings[b]);
            Reject("risk_dependency",()=>RiskEngine.CalculateExact(Bound(undeclared),[],RiskEngine.ExactNumericalPolicy));
            var wrongPoint=new CapturedPriceSource(request,expected.Valuation,bindings[a]);
            Reject("risk_dependency",()=>RiskEngine.CalculateExact(Bound(wrongPoint),[],RiskEngine.ExactNumericalPolicy));
            var past=sourceA with{Id=Guid.NewGuid(),ValuationAt=at.AddDays(-1),MarketCutoff=at.AddDays(-1),Selections=[sourceA.Selections[0] with{
                Observation=sourceA.Selections[0].Observation with{Key=new(f.Revision.CatalogId,Guid.NewGuid()),ObservedAt=at.AddDays(-1)}}]};
            var pastExport=modelA.Compile(past,[a],true,resolved);var historicalA=pastExport.Points[(a,InputUse.Variable)];
            var historical=derived with{Points=[assetPoint with{Key=key,Expression=new ConnectedWeightedSum([
                new(CurvePointReference.Exact(historicalA),ExactDecimal.Parse("1")),new(CurvePointReference.Exact(bindings[b]),ExactDecimal.Parse("1"))])}]};
            var historicalDefinitions=ConnectedCurves.MergeDefinitions(exportedA.Definitions.Concat(pastExport.Definitions).Concat(exportedB.Definitions).Append(historical));
            var historicalPlan=ConnectedCurves.Compile(historicalDefinitions,[bindings[a],key],resolved,new Visible());
            var historicalSource=new CapturedPriceSource(request,ConnectedValuation.Prepare(ConnectedScenario.Base(historicalPlan),sourceB,[sourceA,past]),key);
            var historicalValuation=ConnectedValuation.Prepare(ConnectedScenario.Base(historicalPlan),sourceA,[sourceB,past]);
            var frozen=RiskEngine.PrepareConnected(historicalValuation,map,[ValuationPortfolio.FromTrades(
                [new(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),new BoundDelivery(exchange,sourceB,historicalSource),new(ExactDecimal.Parse("40"),unit.Quantity))])],
                new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new Factors(selectedTerms,sourceB)))]);
            var frozenMove=RiskEngine.WithShifts(frozen,[new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(a.Curve,[bindings[a].PointId]),unit,
                ExactDecimal.Parse("0.1"),CurveShift.ParentAdditive)]);
            Equal(RiskEngine.CalculateExact(frozenMove,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"180.78");
            Reject("risk_dependency",()=>new CapturedPriceSource(request,expected.Valuation.WithScenario(expected.Valuation.Scenario.WithShifts([
                new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(a.Curve,[bindings[a].PointId]),unit,ExactDecimal.Parse("0.1"),CurveShift.ParentAdditive)])),key));
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try{_=combined.HasSameCalculation(key,native,key,cancelled.Token);throw new Exception("A cached source proof ignored cancellation.");}catch(OperationCanceledException){}
        }
    }
    private sealed record BoundDelivery(GasUnfixedPhysicalDeliveryInstrument Inner,MarketSnapshot? DiscountSource,CapturedPriceSource? PriceSource=null):Instrument(Inner.Id)
    {
        public override ReferenceKey Currency=>Inner.Currency;public override Quantity UnitQuantity=>Inner.UnitQuantity;
        public override bool SupportsQuantityScaling=>true;
        public override UnitPricingContract Declare(UnitValuationContext context)
        {var d=Inner.Declare(context);return d with{DiscountInputs=[..d.DiscountInputs.Select(i=>i with{Source=DiscountSource})]};}
        public override UnitValuation PV(UnitValuationContext context)
        {if(PriceSource is not null)context.Inputs.RequireSource(PriceSource);return Inner.PV(context);}
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
