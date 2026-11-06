using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Curves;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasForwardSettlementExampleChecks
{
    internal sealed class Example
    {
        internal Fixture F { get; }=new();
        internal DateTimeOffset AsOf { get; }=Fixture.Utc("2026-11-04T16:00:00Z");
        internal DateTimeOffset KnownAt { get; }=Fixture.Utc("2026-11-05T16:00:00Z");
        internal CurveModel Model { get; }
        internal QuoteSpecification Quote { get; }
        internal Curve DailyFutures { get; }
        internal Curve MonthlyFutures { get; }
        internal Curve DailyBasis { get; }
        internal Curve MonthlyBasis { get; }
        internal ImmutableArray<MarketContract> BasisDays { get; }
        internal MarketContract BasisMonth { get; }
        internal ImmutableArray<MarketContract> FutureDays { get; }
        internal MarketContract Monthly { get; }
        internal ImmutableArray<MarketContract> Daily { get; }
        internal ImmutableArray<MarketSeries> Series { get; }
        internal FloatingSide Cash { get; }
        internal IndexedPhysicalGasPurchase Instrument { get; }
        internal Trade Trade { get; }
        internal Guid Binding { get; }=Guid.NewGuid();
        internal ImmutableArray<FinalSidePayment> Finals { get; }
        internal ImmutableArray<SideCashAllocation> Payments { get; }
        internal ExplicitDiscounting Discount { get; }=new(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        internal CustomCurveProjectionBinding Projection { get; }
        private readonly RecipeDefinition dailyRecipe;
        private readonly RecipeDefinition monthlyRecipe;
        private readonly ConnectedRecipeIdentity dailyIdentity;
        private readonly ConnectedRecipeIdentity monthlyIdentity;
        private readonly ConnectedRecipeIdentity combinedIdentity;

        internal Example()
        {
            var quantity=new QuantitySpecification(F.R("synthetic_gas_mmbtu"),F.R("gas"),F.R("gas_energy"),F.R("mmbtu"),F.R("energy"));
            Quote=new(F.R("synthetic_gas_cash_quote"),QuoteKind.Outright,F.R("usd"),quantity.Key,ExactDecimal.Parse("1"),
                ExactDecimal.Parse("0.001"),ExactDecimal.Parse("0.001"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
            var from=new DateOnly(2026,10,1);var to=new DateOnly(2027,1,1);
            var dates=Enumerable.Range(0,to.DayNumber-from.DayNumber).Select(from.AddDays).ToArray();
            DateTimeOffset Publication(DateOnly day)=>new(day.ToDateTime(new TimeOnly(12,0),DateTimeKind.Utc));
            Daily=[..dates.Select((day,n)=>new MarketContract(F.K(100+n),ContractKind.Forward,day,day.AddDays(1),Publication(day),null))];
            Series=[..Daily.Select((day,n)=>new MarketSeries(F.S(100+n),day.Key,Quote.Key,null))];
            var metadata=F.Direct("synthetic_daily_index",Daily[0].Key,Quote.Key,SettlementKind.Index) with{Constituents=[..Daily.Select(d=>d.Key)]};
            // These dated series and weekday-only cutoffs are synthetic inputs,
            // not a claim to implement an exchange listing/holiday calendar.
            DateTimeOffset Expiry(DateOnly day,int weekdays)
            {
                for(var n=0;n<weekdays;n++){do{day=day.AddDays(-1);}while(day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);}
                return new(day.ToDateTime(new TimeOnly(23,0),DateTimeKind.Utc));
            }
            var dailyQuote=Quote with{Key=F.R("ice_hhd_quote"),TradingTick=ExactDecimal.Parse("0.0001"),ClearingIncrement=ExactDecimal.Parse("0.0001")};
            var monthlyQuote=Quote with{Key=F.R("ice_h_quote")};
            FutureDays=[..Enumerable.Range(0,26).Select(n=>new DateOnly(2026,11,5).AddDays(n)).Select((day,n)=>
                new MarketContract(F.K(500+n),ContractKind.Future,day,day.AddDays(1),Expiry(day,1),F.R("ice_hhd_"+day.ToString("yyyyMMdd"))))];
            Monthly=new(F.K(600),ContractKind.Future,new(2026,12,1),to,Expiry(new(2026,12,1),3),F.R("ice_h_20261201"));
            DailyFutures=F.Direct("ice_hhd",FutureDays[0].Key,dailyQuote.Key,SettlementKind.FutureSeries) with{Constituents=[..FutureDays.Select(d=>d.Key)]};
            MonthlyFutures=F.Direct("ice_h",Monthly.Key,monthlyQuote.Key,SettlementKind.FutureSeries);
            FuturesSeriesSpecification Terms(MarketContract c,QuoteSpecification q)=>new(c.FuturesSeries!.Value,quantity.Key,q.Currency,ExactDecimal.Parse("1"),q.TradingTick,
                DailyFutures.Location,DailyFutures.DeliveryProfile,c.DeliveryFrom,c.DeliveryTo,c.Expiry,quantity.Key,ExactDecimal.Parse("2500"),LotBasis.FixedTotal,null,"cash");
            var basisQuote=Quote with{Key=F.R("synthetic_otc_basis"),Kind=QuoteKind.Basis};
            BasisDays=[..FutureDays.Select((contract,n)=>new MarketContract(F.K(700+n),ContractKind.Forward,contract.DeliveryFrom,contract.DeliveryTo,contract.Expiry,null))];
            BasisMonth=new(F.K(800),ContractKind.Forward,Monthly.DeliveryFrom,Monthly.DeliveryTo,Monthly.Expiry,null);
            DailyBasis=F.Direct("henry_daily_basis",BasisDays[0].Key,basisQuote.Key,SettlementKind.IndexDifference) with{Constituents=[..BasisDays.Select(contract=>contract.Key)]};
            MonthlyBasis=F.Direct("henry_monthly_basis",BasisMonth.Key,basisQuote.Key,SettlementKind.IndexDifference);
            Model=new(F.Revision,F.Reference,[quantity],[Quote,dailyQuote,monthlyQuote,basisQuote],[..Daily,..FutureDays,Monthly,..BasisDays,BasisMonth],[metadata,DailyFutures,MonthlyFutures,DailyBasis,MonthlyBasis],
                [..FutureDays.Select(d=>Terms(d,dailyQuote)),Terms(Monthly,monthlyQuote)],[],
                [..Series,..FutureDays.Select((d,n)=>new MarketSeries(F.S(500+n),d.Key,dailyQuote.Key,null)),new(F.S(600),Monthly.Key,monthlyQuote.Key,null),..BasisDays.Select((contract,n)=>new MarketSeries(F.S(700+n),contract.Key,basisQuote.Key,null)),new(F.S(800),BasisMonth.Key,basisQuote.Key,null)]);
            var fixings=Daily.Select((d,n)=>new IndexFixing(F.R("index"),Quote.Key,d.DeliveryFrom,d.DeliveryTo,Publication(d.DeliveryFrom),
                Series[n].Key,F.R("actual_source"),IndexFixing.SupportedImplementation)).ToImmutableArray();
            var clock=GasDayClock.Capture(F.R("synthetic_gas_clock"),TimeZoneInfo.Utc,0);
            var location=new GasDeliveryLocation(F.R("pipeline"),F.R("zone"),F.R("delivery_point"));
            var assignments=Daily.Select((d,n)=>new GasValuationAssignment(F.R("synthetic_day_"+n),location,F.R("gas"),d.DeliveryFrom,d.DeliveryTo,
                PriceSettlementSelection.CurveTarget(F.R("index")),Quote,new(new(metadata.Key,d.Key),d),fixings[n],new(default,Quote))).ToImmutableArray();
            var physical=new PhysicalGasSide(Guid.NewGuid(),location,F.R("gas"),new(clock,from,to,GasQuantityBasis.PerOperatingDay,
                new(ExactDecimal.Parse("1"),quantity.Key),GasFuelConvention.None,default),assignments);
            dailyIdentity=new(F.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version);
            monthlyIdentity=new(F.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version);
            combinedIdentity=new(F.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version);
            Projection=new(F.Revision.CatalogId,combinedIdentity.CurveId,1,"output",null,CustomCurveProjectionBinding.ContainingDeliverySelection,CustomCurveProjectionBinding.SupportedImplementation);
            Cash=new(Guid.NewGuid(),PriceSettlementSelection.CurveTarget(F.R("index")),Quote,new(ExactDecimal.Parse("1"),quantity.Key),
                [..Daily.Select((d,n)=>new FloatingSidePeriod(d.DeliveryFrom,d.DeliveryTo,fixings[n].FixingAt,
                    Fixture.Utc(d.DeliveryFrom.Month==10?"2026-11-03T00:00:00Z":d.DeliveryFrom.Month==11?"2026-12-10T00:00:00Z":"2027-01-10T00:00:00Z"),
                    FloatingAveraging.CalendarDayArithmetic,[new(new(metadata.Key,d.Key),d)],[fixings[n]],null,null,null,Projection,ExactRatio.One,FloatingSide.IndexedImplementation))]);
            Instrument=new(Guid.NewGuid(),physical,Cash);
            Trade=new(Guid.NewGuid(),Guid.NewGuid(),[new(Binding,Instrument,new(ExactDecimal.Parse("100"),quantity.Key))]);
            var determinedAt=Fixture.Utc("2026-11-02T16:00:00Z");
            var authority=Model.Build(Snapshot(determinedAt,determinedAt),[]);
            var context=new SideValuationContext(authority,Discount);
            Finals=[..Enumerable.Range(0,31).Select(n=>FinalSidePayment.Determine(Guid.NewGuid(),Trade,Binding,1,n,context))];
            Payments=[..Finals.Select(d=>new SideCashAllocation(Guid.NewGuid(),d.Id,Fixture.Utc("2026-11-03T00:00:00Z"),KnownAt,ExactMoney.Parse("-300",Quote.Currency)))];
            dailyRecipe=BasisProjection("henry_daily_otc","Henry Hub daily OTC: HHD plus basis",DailyFutures,FutureDays,DailyBasis,BasisDays);
            monthlyRecipe=BasisProjection("henry_monthly_otc","Henry Hub monthly OTC: H plus basis",MonthlyFutures,[Monthly],MonthlyBasis,[BasisMonth]);
        }
        private static RecipeDefinition BasisProjection(string code,string name,Curve future,ImmutableArray<MarketContract> futures,Curve basis,ImmutableArray<MarketContract> spreads)
        {
            RecipeNode Node(string id,string kind,(string,string)[]? parameters=null,(string,string)[]? ports=null)=>SplicedCurveChecks.Node(id,kind,parameters,ports);
            return new(code,name,[Node("future","source",[("curve",future.Key.Code)]),Node("basis","source",[("curve",basis.Key.Code)]),
                Node("mapping","weighted_contracts",[("aggregation",RecipeEngine.SignedSum)],[("source_a","future"),("source_b","basis")]) with
                {ContractWeights=[..futures.SelectMany((contract,n)=>new RecipeContractWeight[]{new(n+1,"source_a",contract.Key.Id,"1"),new(n+1,"source_b",spreads[n].Key.Id,"1")})]},
                Node("dates","dates",[("mode","context"),("sampling","context_utc_time"),("corrections","exact_context_frame_history_latest_visible")]),
                Node("sample","sample",ports:[("selection","mapping"),("dates","dates")]),Node("output","output",ports:[("values","sample")])],"output");
        }
        internal CashflowBindingValuation State(ImmutableArray<SideCashAllocation>? payments=null)=>new(Trade.Id,Binding,Trade.Instruments[0].Quantity,
            [new(Trade,Binding,1,Finals,payments??Payments,PaymentSideState.Policy)]);
        internal MarketSnapshot Snapshot(DateTimeOffset? at=null,DateTimeOffset? known=null)
        {
            var date=at??AsOf;
            SelectedObservation Mark(SeriesKey series,InputUse use,string source,ObservationRole role,DateTimeOffset observed,string value)=>
                new(new(series,use),new(new(F.Revision.CatalogId,Guid.NewGuid()),series,F.R(source),role,PublicationState.Final,observed,ExactDecimal.Parse(value)));
            return(F.Snapshot() with{Id=Guid.NewGuid(),ValuationAt=date,MarketCutoff=date,Selections=[
                ..Daily.Select((d,n)=>(d,n)).Where(x=>x.d.Expiry<=date).Select(x=>Mark(Series[x.n].Key,InputUse.FrozenFact,"actual_source",ObservationRole.IndexFixing,
                    x.d.Expiry,x.d.DeliveryFrom.Month==10?"3":"3.1")),
                ..FutureDays.Select((d,n)=>Mark(F.S(500+n),InputUse.Variable,"source",ObservationRole.DailySettlement,date,
                    ((320+n%7)/100m).ToString(System.Globalization.CultureInfo.InvariantCulture))),
                Mark(F.S(600),InputUse.Variable,"source",ObservationRole.DailySettlement,date,"3.5"),
                ..BasisDays.Select((contract,n)=>Mark(F.S(700+n),InputUse.Variable,"source",ObservationRole.TraderMark,date,
                    ((20+n%5)/1000m).ToString(System.Globalization.CultureInfo.InvariantCulture))),
                Mark(F.S(800),InputUse.Variable,"source",ObservationRole.TraderMark,date,"0.02")]}).WithKnowledge(Guid.NewGuid(),known??KnownAt);
        }
        internal (ValuationPortfolio Portfolio,ConnectedValuation Valuation,ImmutableDictionary<CurveRequest,CurvePointKey> Mapping,CurveBuild Authority) Prepare(
            CashflowBindingValuation? selectedState=null,MarketSnapshot? selectedSnapshot=null)
        {
            var snapshot=selectedSnapshot??Snapshot();var state=selectedState??State();
            var portfolio=ValuationPortfolio.FromTrades([Trade],new(ValuationMeasure.UnpaidCashPresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,Discount)
                {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(Trade.Id,Binding),state)});
            var dependencies=portfolio.Declare(snapshot).Single().Dependencies;
            var date=DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime);
            var context=new ResolvedCalculationContext(snapshot.Knowledge!.ContextId,"synthetic-indexed-gas",snapshot.ValuationAt,snapshot.Knowledge.KnownAt,snapshot.ValuationAt,snapshot.MarketCutoff);
            ConnectedRecipeGraph Project(RecipeDefinition definition,ConnectedRecipeIdentity identity,Curve future,ImmutableArray<MarketContract> futures,Curve basis,ImmutableArray<MarketContract> spreads)
            {
                RecipeSource Source(Curve curve,ImmutableArray<MarketContract> contracts)=>new(curve.Key,PriceUnit.FromQuote(Quote),[..contracts.Select(contract=>new RecipeContract(contract,Fixture.Utc("2026-11-01T00:00:00Z")))]);
                return RecipeEngine.Connect(definition,new(date,snapshot.ValuationAt,
                    ImmutableDictionary<string,RecipeSource>.Empty.Add("future",Source(future,futures)).Add("basis",Source(basis,spreads)),
                    ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty.Add("dates",[date])),identity,
                    [..futures.Select(contract=>new ConnectedRecipeSample(future.Key,date,contract.Key,Model,snapshot,false)),
                     ..spreads.Select(contract=>new ConnectedRecipeSample(basis.Key,date,contract.Key,Model,snapshot,false))],context);
            }
            var daily=Project(dailyRecipe,dailyIdentity,DailyFutures,FutureDays,DailyBasis,BasisDays);
            var monthly=Project(monthlyRecipe,monthlyIdentity,MonthlyFutures,[Monthly],MonthlyBasis,[BasisMonth]);
            var graph=RecipeEngine.Connect(SplicedCurveChecks.Splice(daily,monthly,"2026-11-05","2026-12-01","2027-01-01") with{Code="henry_combined_otc",Name="Henry Hub daily and monthly OTC curve"},
                new(date,snapshot.ValuationAt,[],[]),combinedIdentity,[],context,
                publishedSources:ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("before",daily).Add("after",monthly));
            CurveRequest Request(MarketContract day)=>Projection.Request(new(F.C("synthetic_daily_index"),day.Key),new(day.DeliveryFrom,day.DeliveryTo));
            var needed=dependencies.Select(r=>Daily.Single(d=>Request(d)==r)).ToArray();
            var points=graph.Definitions.SelectMany(d=>d.Points).ToDictionary(p=>p.Key);
            var outputs=new CurvePriceMapping(graph.Outputs.Select(o=>new ProjectionOutput(o.Position,o.Point,new(points[o.Point].DeliveryFrom,points[o.Point].DeliveryTo))));
            var mapping=needed.ToImmutableDictionary(Request,d=>outputs.Resolve(Projection,d));
            var plan=ConnectedCurves.Compile(graph.Definitions,[..mapping.Values.Distinct()],context,new Visible());
            return(portfolio,ConnectedValuation.Prepare(ConnectedScenario.Base(plan),snapshot),mapping,Model.Build(snapshot,[]));
        }
        internal ImmutableArray<ConnectedRiskBump> Bumps()=>[..FutureDays.Select(c=>new ConnectedRiskBump(CurveShockCoordinate.MarketQuote,
            new ContractCurveTarget(DailyFutures.Key,c.Key),PriceUnit.FromQuote(Quote),ExactDecimal.Parse("1"),ConnectedRiskBump.Convention)),
            new(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(MonthlyFutures.Key,Monthly.Key),PriceUnit.FromQuote(Quote),ExactDecimal.Parse("1"),ConnectedRiskBump.Convention),
            ..BasisDays.Select(contract=>new ConnectedRiskBump(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(DailyBasis.Key,contract.Key),PriceUnit.FromQuote(Quote),ExactDecimal.Parse("1"),ConnectedRiskBump.Convention)),
            new(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(MonthlyBasis.Key,BasisMonth.Key),PriceUnit.FromQuote(Quote),ExactDecimal.Parse("1"),ConnectedRiskBump.Convention)];
        internal PaymentSideValuation Inspect(CashflowBindingValuation? selected=null)
        {
            var state=selected??State();var p=Prepare(state);
            return(PaymentSideValuation)Cash.PV(p.Valuation.PaymentContext(p.Mapping,p.Authority,Discount).WithBindingState(state));
        }
    }
    internal static void Run()
    {
        var e=new Example();var p=e.Prepare();
        GasCompositionInstrument Composition(FloatingSide cash)=>new(Guid.NewGuid(),
            [new("delivery",e.Instrument.Delivery,ExactDecimal.Parse("1"),ExactDecimal.Parse("100"),null)],
            [new("payment",cash,"indexed_consideration","delivery",GasQuantityStage.Delivery,ExactDecimal.Parse("-1"))]);
        _=Composition(e.Cash);
        var redistributed=e.Cash.Periods.SetItem(0,e.Cash.Periods[0] with{Allocation=new(3,2)}).SetItem(1,e.Cash.Periods[1] with{Allocation=new(1,2)});
        try{_=Composition(new(Guid.NewGuid(),e.Cash.Settlement,e.Quote,e.Cash.UnitQuantity,redistributed));throw new Exception("Redistributed indexed quantities were accepted.");}
        catch(MarketDataException error)when(error.Code=="gas_composition_input"){}
        var combined=e.Cash.Periods[0] with{DeliveryTo=e.Cash.Periods[1].DeliveryTo,Allocation=new(2,1),Projections=[..e.Cash.Periods[0].Projections,..e.Cash.Periods[1].Projections],Fixings=[..e.Cash.Periods[0].Fixings,..e.Cash.Periods[1].Fixings]};
        try{_=Composition(new(Guid.NewGuid(),e.Cash.Settlement,e.Quote,e.Cash.UnitQuantity,[combined,..e.Cash.Periods.Skip(2)]));throw new Exception("Combined index delivery days were accepted.");}
        catch(MarketDataException error)when(error.Code=="gas_composition_input"){}
        var retainedGas=BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(e.Instrument.Delivery));
        var retainedDays=GasDailyQuantitySelection.ReadContract(retainedGas,new ExactRatio(100,1));
        if(retainedDays.Length!=92||retainedDays.Any(day=>day.Quantity.ToExactString()!="100"))
            throw new Exception("The operational inspector must retain the whole multi-month Side and exact held daily quantities.");
        if(p.Mapping.Count!=57||p.Mapping.Values.Distinct().Count()!=27||p.Valuation.Scenario.Plan.Points.Values.Any(point=>
            point.Expression is ConnectedRecipeProjection projection&&projection.Declaration.Definition.Code=="synthetic_daily_projection"))
            throw new Exception("The physical trade must bind the mixed curve directly, sharing its monthly point across daily requests without a wrapper curve.");
        if(!p.Valuation.Scenario.Plan.Points.Values.Any(point=>point.Expression is ConnectedSource source&&source.Request.Curve.Code=="ice_hhd")||
            !p.Valuation.Scenario.Plan.Points.Values.Any(point=>point.Expression is ConnectedSource source&&source.Request.Curve.Code=="ice_h"))
            throw new Exception("Physical cash PV must consume the configured HHD and H sources through the OTC projections and combined curve.");
        var prepared=RiskEngine.PrepareConnected(p.Valuation,p.Mapping,[p.Portfolio],authority:p.Authority);
        var bumps=e.Bumps();
        var result=RiskEngine.CalculateExact(prepared,bumps,RiskEngine.ExactNumericalPolicy);
        var group=result.Groups.Single();Equal(group.BaseValues.Single().Value,"-20602");
        if(group.Factors.Length!=54)throw new Exception("Separate futures and basis quotes require 54 native factors through ordinary PV repricing.");
        foreach(var factor in group.Factors.Take(26).Concat(group.Factors.Skip(27).Take(26)))
        {Equal(factor.Totals.Single().Values.Up,"-20702");Equal(factor.Totals.Single().Values.Down,"-20502");}
        Equal(group.Factors[26].Totals.Single().Values.Up,"-23702");Equal(group.Factors[26].Totals.Single().Values.Down,"-17502");
        Equal(group.Factors[53].Totals.Single().Values.Up,"-23702");Equal(group.Factors[53].Totals.Single().Values.Down,"-17502");
        var rows=e.Inspect().Payments.Select(r=>r.Scale(ExactDecimal.Parse("-1"),ExactDecimal.Parse("100"))).ToArray();
        if(rows.Length!=92||rows.Count(r=>r.CashBalance!.State==CashPaymentState.Paid)!=31||rows.Count(r=>r.CashBalance!.State==CashPaymentState.FixedUnpaid)!=4||
            rows.Count(r=>r.CashBalance!.State==CashPaymentState.Projected)!=57)throw new Exception("The daily physical purchase lost its paid/fixed/projected history.");
        Equal(rows.Aggregate(ExactMoney.Parse("0",e.Quote.Currency),(sum,r)=>sum.Add(r.Amount)),"-29902");
        if(rows.Where(r=>r.CashBalance!.State==CashPaymentState.Paid).Any(r=>r.RequirePresentValue().ToExactString()!="0"||r.Coverage!=ValueCoverage.UnpaidCashPresentValue))
            throw new Exception("Paid history became overdue cash or re-entered PV.");
        var inspection=rows.Select(r=>SideScheduleInspection.FromValue(r,e.AsOf,d=>new(d.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)))).ToArray();
        if(inspection.Any(r=>r.Quantity!="-100")||inspection.Count(r=>r.Lifecycle.Cash=="Paid")!=31||inspection.Count(r=>r.Lifecycle.Cash=="FixedUnpaid")!=4||
            inspection.Count(r=>r.Lifecycle.Cash=="Projected")!=57||inspection.Where(r=>r.Lifecycle.Cash=="Paid").Any(r=>r.PresentValue!="0"||r.Outstanding!="0"||r.Paid is null))
            throw new Exception("The shared inspector must retain paid, fixed-unpaid and projected state independently of dates and price state.");
        var otherBinding=Guid.NewGuid();var other=new Trade(Guid.NewGuid(),e.Trade.BookId,[new(otherBinding,e.Instrument,e.Trade.Instruments[0].Quantity)]);
        var otherState=new CashflowBindingValuation(other.Id,otherBinding,other.Instruments[0].Quantity,[new(other,otherBinding,1,[],[],PaymentSideState.Policy)]);
        var shared=ValuationPortfolio.FromTrades([e.Trade,other],p.Portfolio.Setup with{BindingStates=p.Portfolio.Setup.BindingStates.Add(new(other.Id,otherBinding),otherState)});
        var both=RiskEngine.CalculateExact(RiskEngine.PrepareConnected(p.Valuation,p.Mapping,[shared],authority:p.Authority),bumps,RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
        Equal(both.Groups.Single().BaseValues.Single().Value,"-50504");
        Equal(both.Groups.Single().BasePositions.Single(v=>v.TradeId==e.Trade.Id).Value,"-20602");
        Equal(both.Groups.Single().BasePositions.Single(v=>v.TradeId==other.Id).Value,"-29902");
        if(both.Work.UniqueInstruments!=1||both.Work.UniqueValuations!=2)throw new Exception("Different held cash states shared a unit calculation.");
        var early=e.Prepare(selectedSnapshot:p.Valuation.Snapshot.WithKnowledge(Guid.NewGuid(),e.KnownAt.AddDays(-1)));
        Equal(RiskEngine.CalculateExact(RiskEngine.PrepareConnected(early.Valuation,early.Mapping,[early.Portfolio],authority:early.Authority),[],RiskEngine.ExactNumericalPolicy)
            .Groups.Single().BaseValues.Single().Value,"-29902");
        var missing=p.Valuation.Snapshot with{Id=Guid.NewGuid(),Selections=[..p.Valuation.Snapshot.Selections.Where(s=>s.Input.Series!=e.Series[34].Key)]};
        var absent=e.Prepare(selectedSnapshot:missing);
        Reject("index_fixing_missing",()=>RiskEngine.CalculateExact(RiskEngine.PrepareConnected(absent.Valuation,absent.Mapping,[absent.Portfolio],authority:absent.Authority),[],RiskEngine.ExactNumericalPolicy));
        var correction=p.Valuation.Snapshot with{Id=Guid.NewGuid(),Selections=[..p.Valuation.Snapshot.Selections.Select(s=>s.Input.Series==e.Series[34].Key?
            s with{Observation=s.Observation with{Key=new(e.F.Revision.CatalogId,Guid.NewGuid()),Value=ExactDecimal.Parse("3.5")}}:s)]};
        var revised=e.Prepare(selectedSnapshot:correction);
        Equal(RiskEngine.CalculateExact(RiskEngine.PrepareConnected(revised.Valuation,revised.Mapping,[revised.Portfolio],authority:revised.Authority),[],RiskEngine.ExactNumericalPolicy)
            .Groups.Single().BaseValues.Single().Value,"-20642");
        Console.WriteLine("PASS physical indexed purchase: 92 native daily rows, actual fixings, HHD/H futures plus independent basis through OTC curves and splice, explicit payments and unchanged generic risk repricing");
    }
    internal static string Export()
    {
        var e=new Example();var p=e.Prepare();
        var state=(CashflowBindingValuation)p.Portfolio.Setup.BindingStates[new(e.Trade.Id,e.Binding)];
        var value=(PaymentSideValuation)e.Cash.PV(p.Valuation.PaymentContext(p.Mapping,p.Authority,e.Discount).WithBindingState(state));
        var prepared=RiskEngine.PrepareConnected(p.Valuation,p.Mapping,[p.Portfolio],authority:p.Authority);
        var bumps=e.Bumps();
        var run=RiskEngine.CalculateExact(prepared,bumps,RiskEngine.ExactNumericalPolicy).Groups.Single();
        var contracts=e.FutureDays.Add(e.Monthly).AddRange(e.BasisDays).Add(e.BasisMonth).ToDictionary(c=>c.Key);
        var rows=value.Payments.Select(r=>r.Scale(ExactDecimal.Parse("-1"),e.Trade.Instruments[0].Quantity.Amount)).ToArray();
        var zero=ExactMoney.Parse("0",e.Quote.Currency);
        return System.Text.Json.JsonSerializer.Serialize(new{
            title="Daily indexed physical gas purchase",synthetic=true,measure="Unpaid contractual cash PV",implementation="indexed_physical_gas_purchase_v1",
            asOf=e.AsOf,knownAt=e.KnownAt,contextId=p.Valuation.Scenario.Plan.Context.ContextId,snapshotId=p.Valuation.Snapshot.Id,scenarioId=p.Valuation.Scenario.Id,
            numericalPolicy=RiskEngine.ExactNumericalPolicy,tradeId=e.Trade.Id,bindingId=e.Binding,quantityUnit="MMBtu",currency="USD",
            totalQuantity=e.Instrument.Delivery.GenerateSchedule().Quantities.TotalDelivery.Scale(e.Trade.Instruments[0].Quantity.Amount).ToExactString(),
            fullPeriodAmount=rows.Aggregate(zero,(sum,r)=>sum.Add(r.Amount)).ToExactString(),paid=rows.Aggregate(zero,(sum,r)=>sum.Add(r.CashBalance!.Paid)).ToExactString(),
            presentValue=run.BaseValues.Single().Value.ToExactString(),
            curves=p.Valuation.Scenario.Plan.Points.Values.Select(point=>point.Expression).OfType<ConnectedRecipeProjection>()
                .Select(projection=>projection.Declaration).DistinctBy(declaration=>declaration.Identity).Select(declaration=>new{
                    code=declaration.Definition.Code,name=declaration.Definition.Name,declaration.Identity.CurveId,declaration.Identity.Version,declaration.ManifestId}),
            risk=run.Factors.Select(f=>{
                var target=(ContractCurveTarget)f.Bump.Target;var contract=contracts[target.Contract];var v=f.Totals.Single().Values;
                return new{sourceCurve=target.Curve.Code,contractId=target.Contract.Id,series=contract.FuturesSeries?.Code??target.Curve.Code+"_"+contract.DeliveryFrom.ToString("yyyyMMdd"),
                    tenorFrom=contract.DeliveryFrom,tenorTo=contract.DeliveryTo,bump="1",@base=v.Base.ToExactString(),up=v.Up.ToExactString(),down=v.Down.ToExactString(),delta=v.Delta.Value.ToExactString()};}),
            rows=rows.Select(r=>new{day=r.Plan.DeliveryFrom,quantity=r.Quantity.ToExactString(),price=r.Prices.Single().ExactPrice.ToUnitPriceExact().Value.ToString(),
                priceState=r.Prices.Single().State.ToString(),state=r.CashBalance!.State.ToString(),paymentAt=r.ValueAt,
                amount=r.Amount.ToExactString(),paid=r.CashBalance.Paid.ToExactString(),outstanding=r.CashBalance.Outstanding.ToExactString(),presentValue=r.RequirePresentValue().ToExactString(),
                determinationId=r.CashBalance.DeterminationId,observations=r.Prices.Single().Lineage.Select(k=>k.Id)})
        },new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
    }
    private sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey logical,ResolvedCalculationContext context)=>throw new Exception("Unexpected logical binding.");
        public void RequireVisible(CurveKey exact,ResolvedCalculationContext context){}
    }
    private static void Equal(ExactMoney actual,string expected)
    {if(actual.ToExactString()!=expected)throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
