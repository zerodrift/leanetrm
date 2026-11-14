using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Calculation;

internal static class GasOwnedHoldingsChecks
{
    public static void Run()
    {
        var f=new Fixture();var quote=f.Quotes[0];var at=f.Snapshot().ValuationAt;
        var account=Guid.NewGuid();var evidence=Guid.NewGuid();
        var lot=new GasOwnedLot(Guid.NewGuid(),account,new(ExactDecimal.Parse("100"),quote.Quantity),at.AddDays(-2),at.AddDays(-1),at.AddDays(-2),evidence);
        var reservation=new GasStockReservation(Guid.NewGuid(),lot.Id,new(ExactDecimal.Parse("60"),quote.Quantity),at.AddHours(-2),at.AddHours(-2),null);
        var movement=new GasStockConsumption(Guid.NewGuid(),lot.Id,new(ExactDecimal.Parse("25"),quote.Quantity),GasStockUse.Delivery,
            at.AddHours(-1),at.AddHours(-1),Guid.NewGuid(),reservation.Id);
        var balance=GasOwnedLedger.Balance(lot,[reservation],[movement],at,at);
        Equal(balance.Owned.Amount,"75");Equal(balance.Reserved.Amount,"35");Equal(balance.Available.Amount,"40");
        var awaiting=GasOwnedLedger.Balance(lot,[],[],at.AddHours(-25),at);
        Equal(awaiting.Owned.Amount,"100");Equal(awaiting.Available.Amount,"0");
        var historical=GasOwnedLedger.Balance(lot,[reservation],[movement],at,at.AddHours(-3));
        Equal(historical.Owned.Amount,"100");Equal(historical.Reserved.Amount,"0");
        var later=movement with{KnownAt=at.AddHours(1)};
        Equal(GasOwnedLedger.Balance(lot,[reservation],[later],at,at).Owned.Amount,"100");
        Reject("gas_stock_capacity",()=>GasOwnedLedger.Balance(lot,[reservation],[movement with{Quantity=new(ExactDecimal.Parse("101"),quote.Quantity)}],at,at));
        Reject("gas_stock_reservation",()=>GasOwnedLedger.Balance(lot,[reservation],[movement with{Quantity=new(ExactDecimal.Parse("61"),quote.Quantity)}],at,at));
        Reject("gas_stock_availability",()=>GasOwnedLedger.Balance(lot,[],[movement with{At=at.AddDays(-3),KnownAt=at.AddDays(-3),ReservationId=null}],at,at));
        var loan=new GasCommodityLoan(Guid.NewGuid(),account,lot.Id,new(ExactDecimal.Parse("100"),quote.Quantity),at.AddDays(-1),at.AddDays(5),at.AddDays(-1),
            GasCommodityLoan.TitleTransfer,Guid.NewGuid());
        var repayment=new GasLoanRepayment(Guid.NewGuid(),loan.Id,lot.Id,new(ExactDecimal.Parse("30"),quote.Quantity),at,at,Guid.NewGuid());
        Equal(GasOwnedLedger.ReturnOutstanding(loan,[repayment],at,at).Amount,"70");
        Equal(GasOwnedLedger.ReturnOutstanding(loan,[],at.AddDays(6),at.AddDays(6)).Amount,"100");
        Reject("gas_loan_return",()=>GasOwnedLedger.ReturnOutstanding(loan,[repayment with{Quantity=new(ExactDecimal.Parse("101"),quote.Quantity)}],at,at));
        // A loan transfers title to fungible gas. The stock and return obligation
        // have opposite signed quantities; selling stock never extinguishes debt.
        var mark=new CurveRequest(f.C("future"),f.K(1));
        var stock=new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,null,GasHoldingCarrying.Nominal);
        var debt=new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,at.AddDays(5),GasHoldingCarrying.Nominal);
        var owner=Guid.NewGuid();var book=Guid.NewGuid();
        var held=new Trade(owner,book,[new(Guid.NewGuid(),stock,new(ExactDecimal.Parse("100"),quote.Quantity)),
            new(Guid.NewGuid(),debt,new(ExactDecimal.Parse("-100"),quote.Quantity))]);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null);
        var prepared=RiskEngine.Prepare(f.Model(),f.Snapshot(),[ValuationPortfolio.FromTrades([held],setup)]);
        var values=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy);
        Equal(values.Groups.Single().BaseValues.Single().Value,"0");
        var remaining=new Trade(owner,book,[new(Guid.NewGuid(),debt,new(ExactDecimal.Parse("-100"),quote.Quantity))]);
        var risk=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),f.Snapshot(),[ValuationPortfolio.FromTrades([remaining],setup)]),[],RiskEngine.ExactNumericalPolicy);
        if(risk.Groups.Single().BaseValues.Single().Value.Numerator=="0")throw new Exception("A consumed borrowed lot must retain its return liability.");
        var delivery=new GasPhysicalDeliveryTerms(Guid.NewGuid(),series:f.R("gas_future"),quantity:quote.Quantity,currency:quote.Currency,
            location:f.R("location"),from:at,to:at.AddDays(1),cashDueAt:at.AddDays(2),mark:mark,carrying:GasHoldingCarrying.Nominal,
            implementation:GasPhysicalDeliveryTerms.Version);
        var title=GasPhysicalFinalHandoff.Title(delivery,new(ExactDecimal.Parse("10"),quote.Quantity),
            new(ExactDecimal.Parse("10"),quote.Quantity),ExactUnitPrice.Parse("12",PriceUnit.FromQuote(quote)),at,default);
        Equal(title.StockChange.Amount,"10");Equal(title.Consideration,"-120");Equal(title.Remaining.Amount,"0");
        // At an unchanged delivery mark of 15 the pre-title obligation is 30;
        // post-title stock 150 plus consideration -120 remains exactly 30.
        Equal(ExactMoney.Parse("150",quote.Currency).Add(title.Consideration),"30");
        Reject("gas_delivery_supply",()=>GasPhysicalFinalHandoff.Title(delivery,new(ExactDecimal.Parse("-10"),quote.Quantity),
            new(ExactDecimal.Parse("10"),quote.Quantity),ExactUnitPrice.Parse("12",PriceUnit.FromQuote(quote)),at,default));
        PhysicalFinalPlan();RationalMark();DiscountTiming();
        Console.WriteLine("PASS gas owned holdings: exact title/availability, reservations, no negative stock, bitemporal facts, surviving loan return and ordinary PV conservation");
    }
    private static void PhysicalFinalPlan()
    {
        var f=new Fixture();var quote=f.Quotes[0];var contract=f.Contracts[0];
        var series=new FuturesSeriesSpecification(f.R("gas_future"),quote.Quantity,quote.Currency,quote.Multiplier,quote.TradingTick,
            f.R("location"),f.R("uniform"),contract.DeliveryFrom,contract.DeliveryTo,contract.Expiry,quote.Quantity,ExactDecimal.Parse("40"),
            LotBasis.FixedTotal,Fixture.Utc("2040-01-01T00:00:00Z"),"physical");
        var future=new FuturesContract(series);
        var terms=new GasPhysicalDeliveryTerms(Guid.NewGuid(),series.Key,quote.Quantity,quote.Currency,series.Location,contract.Expiry,
            contract.Expiry.AddDays(35),contract.Expiry.AddDays(40),new(f.C("future"),contract.Key),GasHoldingCarrying.Nominal,GasPhysicalDeliveryTerms.Version);
        var price=ExactUnitPrice.FromPrice(new(ExactDecimal.Parse("12"),quote));
        var first=terms.CreateObligation(price);var second=terms.CreateObligation(ExactUnitPrice.Parse("13",price.Unit));
        if(first.Id==second.Id)throw new Exception("Two native final bases reused one physical obligation economic identity.");
        if(first.Id!=terms.CreateObligation(ExactUnitPrice.Parse("24/2",price.Unit)).Id||first.EconomicIdentity==second.EconomicIdentity)
            throw new Exception("Physical obligation identity must canonicalize exact final price and retain changed economic terms.");
        var mixedPositions=new[]{first,second}.Select(i=>new RiskPosition(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),Guid.NewGuid(),
            i.Id,new(ExactDecimal.Parse("40"),quote.Quantity),new(new Money(default,quote.Currency),quote.Quantity),new(default,quote.Currency))).ToImmutableArray();
        var mixed=new ValuationPortfolio([first,second],mixedPositions,new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        var mixedSnapshot=f.Snapshot() with{Selections=[f.Select(1,QuoteKind.Outright,"gas_out","15")]};
        var mixedResult=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),mixedSnapshot,[mixed]),[],RiskEngine.ExactNumericalPolicy);
        Equal(mixedResult.Groups.SelectMany(g=>g.BaseValues).Select(v=>v.Value).Aggregate((x,y)=>x.Add(y)),"200");
        foreach(var signed in new[]{"40","-40"})
        {
            var plan=FuturesFinalDeliveryPlan.Create(future,new(ExactDecimal.Parse(signed),quote.Quantity),terms,price);
            if(plan.Obligation is not GasPhysicalDeliveryObligationInstrument)throw new Exception("A final delivery plan created commodity stock before title evidence.");
            var owner=new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid());
            var position=new RiskPosition(owner,Guid.NewGuid(),Guid.NewGuid(),plan.Obligation.Id,plan.Quantity,
                new(new Money(default,quote.Currency),quote.Quantity),new(default,quote.Currency));
            var portfolio=new ValuationPortfolio([plan.Obligation],[position],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
            var snapshot=f.Snapshot() with{Selections=[f.Select(1,QuoteKind.Outright,"gas_out","15")]};
            var result=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),snapshot,[portfolio]),[],RiskEngine.ExactNumericalPolicy);
            Equal(result.Groups.Single().BaseValues.Single().Value,signed=="40"?"120":"-120");
        }
        Reject("futures_delivery_transition",()=>FuturesFinalDeliveryPlan.Create(new(series with{SettlementMethod="cash"}),new(ExactDecimal.Parse("40"),quote.Quantity),terms,price));
        Reject("futures_delivery_transition",()=>FuturesFinalDeliveryPlan.Create(future,new(ExactDecimal.Parse("1"),quote.Quantity),terms,price));
        Console.WriteLine("PASS physical final plan: signed ordinary obligation PV, fixed principal, exact held units and no stock before title");
    }
    private static void RationalMark()
    {
        var f=new ConnectedCurveChecks.Fixture();var unit=f.Mean.Unit;
        var plan=ConnectedCurves.Compile(f.Definitions,[f.Mean.Key],f.Context,f.Resolver);
        var snapshot=new MarketSnapshot(f.Context.ContextId,f.Mean.Key.Curve.Revision,f.Context.ValuationAt,f.Context.MarketAsOf,
            InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            f.RawPoints.Select(p=>(ConnectedSource)p.Expression).Select(s=>new SelectedObservation(s.Input,s.Observation)).ToImmutableArray());
        var mark=new CurveRequest(f.Mean.Key.Curve,((ConnectedSource)f.RawPoints[0].Expression).Request.Contract);
        var instrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,unit.Quantity,unit.Currency,null,GasHoldingCarrying.Nominal);
        var owner=new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid());
        var holding=new RiskPosition(owner,Guid.NewGuid(),Guid.NewGuid(),instrument.Id,new(ExactDecimal.Parse("3"),unit.Quantity),
            new(new Money(default,unit.Currency),unit.Quantity),new(default,unit.Currency));
        var portfolio=new ValuationPortfolio([instrument],[holding],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        var prepared=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(ConnectedScenario.Base(plan),snapshot),
            ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(mark,f.Mean.Key),[portfolio]);
        // Independent arithmetic: mean(1,2,4) times three units equals seven.
        Equal(RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"7");
    }
    private static void DiscountTiming()
    {
        var f=new Fixture();var source=f.Snapshot();source=source.WithKnowledge(Guid.NewGuid(),source.ValuationAt.AddDays(1));
        var due=source.ValuationAt.AddHours(4);var quote=f.Quotes[0];var mark=new CurveRequest(f.C("future"),f.K(1));
        var instrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,due,GasHoldingCarrying.CurrencyCurve);
        var owner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),Guid.NewGuid());
        var position=new RiskPosition(owner.Owner,owner.BindingId,owner.BookId,instrument.Id,new(ExactDecimal.Parse("10"),quote.Quantity),
            new(new Money(default,quote.Currency),quote.Quantity),new(default,quote.Currency));
        var portfolio=new ValuationPortfolio([instrument],[position],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
            new DatedCurve(source,quote.Currency,due)));
        var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,
            LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry);
        var projection=new Held(owner,source,mark,portfolio);
        LifecycleProjectionValue Value(DateTimeOffset horizon)=>LifecycleProjection.Prepare(f.Model(),source,
            new(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,18),horizon,0)]),policy,[projection],[])
            .Evaluate("discount_clock",[]).Values.Single();
        var before=Value(due.AddMicroseconds(-1));Equal(before.OpeningValue,"28.5");Equal(before.ResidualValue,"28.5");
        var arrived=Value(due);Equal(arrived.ResidualValue,"30");Equal(arrived.PeriodCash,"0");Equal(arrived.EconomicPnl,"1.5");
        Equal(Value(due.AddHours(1)).ResidualValue,"30");
        if(source.ValuationAt!=due.AddHours(-4))throw new Exception("A projected return must retain its original actual source certificate.");
    }
    private sealed class DatedCurve(MarketSnapshot source,ReferenceKey currency,DateTimeOffset due):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>currency;
        public MarketSnapshot SourceSnapshot=>source;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(source.Definitions,"synthetic_gas_rates"),Guid.NewGuid(),Guid.NewGuid(),1);
        public string TimingPolicy=>RateDiscountTiming.Version;
        private ExactDecimal Factor(DateTimeOffset at)=>ExactDecimal.Parse(at>=due?"0.95":"1");
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt),snapshot,DiscountFactorSource.Explicit))];
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot,DateTimeOffset at,string? policy)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt).DivideExact(Factor(at)),snapshot,DiscountFactorSource.Explicit))];
    }
    private sealed class Held(AccountingPositionKey owner,MarketSnapshot source,CurveRequest mark,ValuationPortfolio portfolio):ILifecycleProjection
    {
        public string Implementation=>"test_held_gas_clock_v1";
        public AccountingPositionKey Owner=>owner;
        public ReferenceKey Currency=>portfolio.Instruments.Single().Currency;
        public MarketSnapshot SourceSnapshot=>source;
        public ImmutableArray<CurveRequest> Dependencies=>[mark];
        public LifecycleProjectionState Opening=>new([portfolio],[]);
        public ImmutableArray<LifecycleProjectionEvent> Events=>[];
        public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)=>[];
        public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)=>throw new Exception("No transfer was selected.");
    }
    static void Equal(ExactDecimal actual,string expected){if(actual!=ExactDecimal.Parse(expected))throw new Exception($"Expected {expected}, got {actual}.");}
    static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    static void Reject(string code,Action action){try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
