using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class PhysicalFuturesValueChecks
{
    internal static void Run()
    {
        var f=new Fixture();var at=f.Snapshot().ValuationAt;var quote=f.Quotes[0] with{Multiplier=ExactDecimal.Parse("2.5")};var unit=PriceUnit.FromQuote(quote);
        var contract=f.Contracts[0];var series=new FuturesSeriesSpecification(f.R("future_jan"),quote.Quantity,quote.Currency,quote.Multiplier,quote.TradingTick,
            f.R("location"),f.R("uniform"),contract.DeliveryFrom,contract.DeliveryTo,contract.Expiry,quote.Quantity,ExactDecimal.Parse("40"),LotBasis.FixedTotal,
            Fixture.Utc("2040-01-01T00:00:00Z"),"physical");
        var a=new CurveRequest(f.C("future"),contract.Key);var market=new FuturesMarketBinding(Guid.NewGuid(),a,contract,series,quote,"physical",FuturesMarketBinding.Implementation);
        var modelA=new CurveModel(f.Revision,f.Reference,f.Quantities,[quote],[contract],[f.Direct("future",contract.Key,quote.Key,SettlementKind.FutureSeries)],
            [series],[],[new(f.S(1),contract.Key,quote.Key,null)]);
        var revisionB=new DefinitionRevision(f.Revision.CatalogId,Guid.NewGuid());var bContract=f.Contracts[1] with{Key=new(revisionB,f.K(2).Id)};
        var b=new CurveRequest(new(revisionB,"inventory"),bContract.Key);var bSeries=new SeriesKey(revisionB,Guid.NewGuid());
        var modelB=new CurveModel(revisionB,f.Reference,f.Quantities,[quote],[bContract],[f.Direct("inventory",bContract.Key,quote.Key,SettlementKind.Index) with{Key=b.Curve}],
            [],[],[new(bSeries,bContract.Key,quote.Key,null)]);
        var context=Guid.NewGuid();var sourceA=(f.Snapshot() with{Selections=[f.Select(1,QuoteKind.Outright,"gas_out","3")]}).WithKnowledge(context,at);
        var sourceB=new MarketSnapshot(Guid.NewGuid(),revisionB,at,at,sourceA.SelectionPolicy,[new(new(bSeries,InputUse.Variable),new(new(revisionB.CatalogId,Guid.NewGuid()),
            bSeries,f.R("source"),ObservationRole.TraderMark,PublicationState.Final,at,ExactDecimal.Parse("5")))]).WithKnowledge(context,at);
        var authority=modelA.Build(sourceA,[a]);var resolved=authority.Graph.Plan.Context;
        var exportA=modelA.Compile(sourceA,[a],false,resolved);var exportB=modelB.Compile(sourceB,[b],false,resolved);
        var bindings=exportA.Points.Concat(exportB.Points).ToImmutableDictionary(p=>p.Key.Request,p=>p.Value);
        var plan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(exportA.Definitions.Concat(exportB.Definitions)),[..bindings.Values],resolved,new Visible());
        var valuation=ConnectedValuation.Prepare(ConnectedScenario.Base(plan),sourceA,[sourceB]);
        var bBuild=modelB.Build(sourceB,[b]);var captured=new CapturedPriceSource(b,bBuild.Connected,bBuild.PricingBindings[b]);
        var gas=new GasPhysicalDeliveryTerms(Guid.NewGuid(),series.Key,unit.Quantity,unit.Currency,f.R("location"),contract.Expiry.AddDays(4),contract.Expiry.AddDays(5),
            contract.Expiry.AddDays(6),b,GasHoldingCarrying.CurrencyCurve,GasPhysicalDeliveryTerms.Version);
        var configuration=new FuturesFinalDeliveryConfiguration(Guid.NewGuid(),1,gas);var final=new FuturesMarginOccurrence(Guid.NewGuid(),contract.Expiry,contract.Expiry.AddDays(1),FuturesMarginPhase.Final);
        var policy=new FuturesMarginPolicy(Guid.NewGuid(),1,contract.Key,a.Curve,at.AddDays(-1),contract.Expiry.AddDays(1),[final],
            new(quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),FuturesMargin.HeldBindingRounding,
            FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.DeliveryVersion){Delivery=configuration};
        var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[FuturesExecution.Bind(binding,market,ExactDecimal.Parse("1"),new(ExactDecimal.Parse("2"),quote),at,FuturesSettlementState.Unprocessed)]);
        var terms=market.Instrument.ProposeMarginTerms(trade,binding,policy,modelA,sourceA);
        var state=new FuturesMarginState(trade.Id,binding,trade.BookId,terms.Quantity,series.Key,ExactUnitPrice.FromPrice(terms.ExecutionPrice),at,at,FuturesMarginState.Version,
            FuturesMarginStatus.Open,null,context,at,1,1);
        var selected=new FuturesMarginValueBinding(terms,[],FuturesMarginValueBinding.FlatReference){Physical=new(FuturesPhysicalBasisValueBinding.Version,
            FuturesPhysicalBasisValueBinding.ValuationProxy,configuration.ProfileId,configuration.ProfileRevision,captured,new Carrying(gas))};
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),
            new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit)){
            BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state),
            FuturesValueBindings=ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding>.Empty.Add(new(trade.Id,binding),selected)};
        var portfolio=ValuationPortfolio.FromTrades([trade],setup);var prepared=RiskEngine.PrepareConnected(valuation,bindings,[portfolio],authority);
        var identity=portfolio.Declare(sourceA).Single().ScheduleIdentity;
        if(identity is null||!identity.Contains(configuration.ProfileId.ToString("D"),StringComparison.Ordinal)||
            !identity.Contains(FuturesPhysicalBasisValueBinding.ValuationProxy,StringComparison.Ordinal)||!identity.Contains(captured.ManifestId,StringComparison.Ordinal))
            throw new Exception("The final portfolio declaration dropped its selected physical profile, mark meaning or captured graph identity.");
        var value=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
        // 100 native units: pending VM=(3-2)*.95; exchange=5*.97-3*.91.
        Equal(value.Groups.Single().BaseValues.Single().Value,"307");
        if(portfolio.Positions.Length!=1||value.Groups.Single().BasePositions.Single().Components.Length!=2)
            throw new Exception("The composite must retain one original native holding with its two product-owned value components.");
        Equal(value.Groups.Single().BasePositions.Single().Components.Single(c=>c.Name=="variation_margin").Value,"95");
        Equal(value.Groups.Single().BasePositions.Single().Components.Single(c=>c.Name=="declared_delivery_basis").Value,"212");
        PreparedConnectedPortfolioRisk Bound(FuturesMarginValueBinding input,ValuationSetup? options=null,Trade? held=null)=>RiskEngine.PrepareConnected(valuation,bindings,
            [ValuationPortfolio.FromTrades([held??trade],(options??setup) with{FuturesValueBindings=ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding>.Empty.Add(new(trade.Id,binding),input)})],authority);
        void Bad(FuturesPhysicalBasisValueBinding input)=>Reject("futures_physical_value",()=>RiskEngine.CalculateExact(Bound(selected with{Physical=input}),[],RiskEngine.ExactNumericalPolicy));
        Bad(selected.Physical! with{Implementation="unknown"});Bad(selected.Physical! with{MarkInterpretation=""});
        Bad(selected.Physical! with{ProfileId=Guid.NewGuid()});Bad(selected.Physical! with{ProfileRevision=2});Bad(selected.Physical! with{Carrying=null});
        var shortTrade=new Trade(trade.Id,trade.BookId,[FuturesExecution.Bind(binding,market,ExactDecimal.Parse("-1"),terms.ExecutionPrice,at,FuturesSettlementState.Unprocessed)]);
        var shortTerms=market.Instrument.ProposeMarginTerms(shortTrade,binding,policy,modelA,sourceA);
        var shortSetup=setup with{BindingStates=setup.BindingStates.SetItem(new(trade.Id,binding),state with{Quantity=shortTerms.Quantity})};
        Equal(RiskEngine.CalculateExact(Bound(selected with{Terms=shortTerms},shortSetup,shortTrade),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"-307");
        var matched=Bound(selected,setup with{Discounting=new ExplicitDiscounting(ExactDecimal.Parse("0.91"),DiscountFactorSource.Explicit)});
        var matchedUp=RiskEngine.WithShifts(matched,[new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(a.Curve,[bindings[a].PointId]),unit,ExactDecimal.Parse("0.1"),CurveShift.ParentAdditive)]);
        Equal(RiskEngine.CalculateExact(matched,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"303");
        Equal(RiskEngine.CalculateExact(matchedUp,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"303");
        var nominalTerms=new GasPhysicalDeliveryTerms(Guid.NewGuid(),gas.Series,gas.Quantity,gas.Currency,gas.Location,gas.From,gas.To,gas.CashDueAt,gas.Mark,GasHoldingCarrying.Nominal,gas.Implementation);
        var nominalProfile=new FuturesFinalDeliveryConfiguration(Guid.NewGuid(),1,nominalTerms);
        var nominalPolicy=policy with{Id=Guid.NewGuid(),Delivery=nominalProfile};var nominal=selected with{Terms=terms with{Policy=nominalPolicy},
            Physical=selected.Physical! with{ProfileId=nominalProfile.ProfileId,Carrying=null}};
        Equal(RiskEngine.CalculateExact(Bound(nominal),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"295");
        Reject("futures_physical_value",()=>RiskEngine.CalculateExact(Bound(nominal with{Physical=nominal.Physical! with{Carrying=new Carrying(gas)}}),[],RiskEngine.ExactNumericalPolicy));
        var negative=RiskEngine.WithShifts(prepared,[
            new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(a.Curve,[bindings[a].PointId]),unit,ExactDecimal.Parse("-4.01"),CurveShift.ParentAdditive),
            new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(b.Curve,[bindings[b].PointId]),unit,ExactDecimal.Parse("-2.98"),CurveShift.ParentAdditive)]);
        // Negative futures quotes remain signed: VM=-301*.95 and the
        // separately dated exchange=202*.97-(-101)*.91.
        Equal(RiskEngine.CalculateExact(negative,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"1.9");
        foreach(var(request,expected)in new[]{(a,"307.4"),(b,"316.7")})
        {
            var shifted=RiskEngine.WithShifts(prepared,[new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(request.Curve,[bindings[request].PointId]),unit,
                ExactDecimal.Parse("0.1"),CurveShift.ParentAdditive)]);
            Equal(RiskEngine.CalculateExact(shifted,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,expected);
        }
        var component=new HeldProjection(ValuationPortfolio.FromTrades([trade],setup with{Discounting=new HeldVm(final.DueAt)}),sourceA,sourceB,a,b);
        var projectionPolicy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,
            LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
        var horizon=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_physical_basis",1),f.R("calendar"),[new(new(2040,12,18),at.AddHours(1),0)]);
        var economic=LifecycleProjection.Prepare(valuation,bindings,authority,horizon,projectionPolicy,[component],[],
            additionalMarkets:[new(bBuild,ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty,[])]);
        var held=economic.Evaluate("independent_asset",[]).Values.Single();
        Equal(held.OpeningValue,"307");Equal(held.ResidualValue,"307");Equal(held.EconomicPnl,"0");
        Console.WriteLine("PASS physical futures composite: one native holding, explicit B source/meaning/carrying, nonunit signed A/B value components");
    }
    private sealed class HeldProjection(ValuationPortfolio portfolio,MarketSnapshot a,MarketSnapshot b,CurveRequest principal,CurveRequest asset):ILifecycleProjection
    {
        public string Implementation=>"synthetic_held_physical_basis_v1";
        public AccountingPositionKey Owner=>new(portfolio.Positions.Single().Owner,portfolio.Positions.Single().BindingId,portfolio.Positions.Single().BookId);
        public ReferenceKey Currency=>portfolio.Instruments.Single().Currency;
        public MarketSnapshot SourceSnapshot=>a;
        public ImmutableArray<MarketSnapshot> MarketSources=>[a,b];
        public MarketSnapshot DependencySource(CurveRequest request)=>request==asset?b:a;
        public ImmutableArray<CurveRequest> Dependencies=>[principal,asset];
        public LifecycleProjectionState Opening=>new([portfolio],[]);
        public ImmutableArray<LifecycleProjectionEvent> Events=>[];
        public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)=>[];
        public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)=>throw new NotSupportedException();
    }
    private sealed class Carrying(GasPhysicalDeliveryTerms terms):ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)=>
            [..payments.Select(p=>new SidePaymentFactor(p,ExactDecimal.Parse(p.PaymentAt==terms.From?"0.97":p.PaymentAt==terms.CashDueAt?"0.91":throw new Exception("Unexpected due.")),snapshot,DiscountFactorSource.Explicit))];
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot source,DateTimeOffset at,string? clock)=>Resolve(payments,source);
    }
    private sealed class HeldVm(DateTimeOffset due):ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot source)=>
            [..payments.Select(p=>new SidePaymentFactor(p,p.PaymentAt==due?ExactDecimal.Parse("0.95"):throw new Exception("Unexpected margin due."),source,DiscountFactorSource.Explicit))];
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot source,DateTimeOffset at,string? clock)=>Resolve(payments,source);
    }
    private static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    private sealed class Visible:ICurveBindingResolver
    {public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new NotSupportedException();public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}}
}
