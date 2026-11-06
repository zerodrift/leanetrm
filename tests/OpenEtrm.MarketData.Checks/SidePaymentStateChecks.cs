using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class SidePaymentStateChecks
{
    internal static void Run()
    {
        var f=new IndexFixingChecks.FixingFixture(monthly:true);var side=f.Floating();
        var instrument=new CashSpecimen(Guid.NewGuid(),side);
        var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(binding,instrument,new(ExactDecimal.Parse("200"),side.UnitQuantity.Specification))]);
        var at=f.Payment;var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var determinedAt=f.Fixings[0].FixingAt.AddDays(1);
        var before=f.Snapshot(determinedAt,1).WithKnowledge(Guid.NewGuid(),determinedAt);
        var receipt=FinalSidePayment.Determine(Guid.NewGuid(),trade,binding,0,0,new(f.Model.Build(before,[]),discount));
        var cash=new SideCashAllocation(Guid.NewGuid(),receipt.Id,at,at,ExactMoney.Parse("500",instrument.Currency));
        var snapshot=f.Snapshot(at,1).WithKnowledge(Guid.NewGuid(),at);
        CashflowBindingValuation State(ImmutableArray<SideCashAllocation> allocations)=>new(trade.Id,binding,trade.Instruments[0].Quantity,
            [new(trade,binding,0,[receipt],allocations,PaymentSideState.Policy)]);
        ExactMoney Value(CashflowBindingValuation state,MarketSnapshot? selected=null)
        {
            var portfolio=ValuationPortfolio.FromTrades([trade],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discount)
                {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)});
            return RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model,selected??snapshot,[portfolio]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
        }
        // The normal inherited Instrument.PV and FloatingSide.PV must retain
        // fixed unpaid cash after its due date, then remove recorded payments.
        Equal(Value(State([])),"2000");
        Equal(Value(State([cash])),"1500");
        Equal(Value(State([cash with{Amount=ExactMoney.Parse("2000",instrument.Currency)}])),"0");
        Equal(Value(State([cash]),snapshot.WithKnowledge(Guid.NewGuid(),at.AddDays(-1))),"2000");
        var revised=snapshot with{Selections=[..snapshot.Selections.Select(s=>s.Input.Use==InputUse.FrozenFact?s with{Observation=s.Observation with{Value=ExactDecimal.Parse("99")}}:s)]};
        Equal(Value(State([cash]),revised),"1500");
        foreach(var invalid in new[]{"-1","2001"})
            Reject("settlement_conservation",()=>Value(State([cash with{Amount=ExactMoney.Parse(invalid,instrument.Currency)}])));
        var ordinary=new SideValuationContext(f.Model.Build(snapshot,[]),discount);
        Reject("side_payment_due",()=>side.PV(ordinary));
        Reject("side_context",()=>side.PV(null!));
        var currentState=State([cash]);
        var unpaidContext=ordinary.WithBindingState(currentState);
        var direct=(PaymentSideValuation)side.PV(unpaidContext);
        if(direct.Payments[0].CashBalance!.State!=CashPaymentState.PartPaidOverdue||direct.Payments[0].Coverage!=ValueCoverage.UnpaidCashPresentValue)
            throw new Exception("Dated cash was classified under the interval overdue measure.");
        var inspect=direct.InspectSchedule(ScheduleDefinitionCompiler.Generate(side).Manifest,ExactDecimal.Parse("200"),new(1,1),day=>new(day.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero));
        var inspected=inspect.Read(0,8).Rows.Single();
        if(inspected.Paid!="500"||inspected.Outstanding!="1500"||inspected.PresentValue!="1500"||inspected.Lifecycle.Cash!="PartPaidOverdue")throw new Exception("The common inspector must retain native paid and outstanding holding balances.");
        var calculation=inspect.Calculation(inspected.Key,0,128);
        if(calculation.Rows.Single(r=>r.Key=="pv").Inputs.Single().Key!="outstanding"||calculation.Rows.Single(r=>r.Key=="outstanding").Parameters["determination"]!=receipt.Id.ToString())throw new Exception("Cash inspection must show outstanding as the source of PV and preserve its exact determination.");
        var prepaidAt=determinedAt.AddDays(2);var prepaidSnapshot=f.Snapshot(prepaidAt,1).WithKnowledge(Guid.NewGuid(),prepaidAt);
        var prepayment=cash with{PaidAt=determinedAt.AddDays(1),KnownAt=determinedAt.AddDays(1),Amount=ExactMoney.Parse("2000",instrument.Currency)};
        var prepaid=(PaymentSideValuation)side.PV(new SideValuationContext(f.Model.Build(prepaidSnapshot,[]),discount).WithBindingState(State([prepayment])));
        Equal(prepaid.PresentValue,"0");
        if(prepaid.Payments[0].CashBalance!.State!=CashPaymentState.Paid||prepaid.Payments[0].Coverage!=ValueCoverage.UnpaidCashPresentValue||prepaid.Payments[0].Discount is not null)
            throw new Exception("A prepaid future row became overdue or requested a discount factor.");
        var noFinals=new CashflowBindingValuation(trade.Id,binding,trade.Instruments[0].Quantity,[new(trade,binding,0,[],[],PaymentSideState.Policy)]);
        var changed=new FloatingSide(side.Id,side.Settlement,side.Quote,side.UnitQuantity,[side.Periods[0] with{Allocation=new(2,1)}]);
        Reject("payment_state",()=>changed.PV(ordinary.WithBindingState(noFinals)));
        var zeroSnapshot=snapshot with{Selections=[..snapshot.Selections.Select(s=>s with{Observation=s.Observation with{Value=default}})]};
        var zeroValue=(PaymentSideValuation)side.PV(new SideValuationContext(f.Model.Build(zeroSnapshot,[]),discount).WithBindingState(noFinals));
        Equal(zeroValue.PresentValue,"0");
        if(zeroValue.Payments[0].CashBalance!.State!=CashPaymentState.NoAmountDue)throw new Exception("A zero final price invented a payment.");
        var liveAt=f.Fixings[0].FixingAt.AddDays(-2);
        var live=f.Snapshot(liveAt).WithKnowledge(Guid.NewGuid(),liveAt);
        live=live with{Selections=[..live.Selections.Select(s=>s with{Observation=s.Observation with{Value=default}})]};
        var zeroForward=(PaymentSideValuation)side.PV(new SideValuationContext(f.Model.Build(live,side.DependenciesAt(live)),discount).WithBindingState(noFinals));
        if(zeroForward.Payments[0].CashBalance!.State!=CashPaymentState.Projected||zeroForward.Payments[0].Coverage!=ValueCoverage.UnpaidCashPresentValue)
            throw new Exception("A zero forecast invented a payment or overdue balance.");
        var livePortfolio=ValuationPortfolio.FromTrades([trade],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discount)
            {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),noFinals)});
        var factor=f.Model.QuoteFactors(live).Single();
        var zeroRisk=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model,live,[livePortfolio]),
            [new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("1"),QuoteBump.Convention)],RiskEngine.ExactNumericalPolicy);
        Equal(zeroRisk.Groups.Single().Factors.Single().Totals.Single().Values.Up,"200");
        Equal(zeroRisk.Groups.Single().Factors.Single().Totals.Single().Values.Down,"-200");
        var unqualified=f.Snapshot(at,1);
        var unqualifiedContext=new SideValuationContext(f.Model.Build(unqualified,[]),discount);
        Reject("payment_state",()=>side.PV(unqualifiedContext.WithBindingState(currentState)));
        Reject("payment_determination",()=>FinalSidePayment.Determine(Guid.NewGuid(),trade,binding,0,0,unqualifiedContext));
        var fraction=new FloatingSide(Guid.NewGuid(),side.Settlement,side.Quote,side.UnitQuantity,[side.Periods[0] with{Allocation=new(1,3)}]);
        var fractionalInstrument=new CashSpecimen(Guid.NewGuid(),fraction);var fractionalBinding=Guid.NewGuid();
        var fractionalTrade=new Trade(Guid.NewGuid(),trade.BookId,[new(fractionalBinding,fractionalInstrument,new(ExactDecimal.Parse("1"),side.UnitQuantity.Specification))]);
        var fractionalReceipt=FinalSidePayment.Determine(Guid.NewGuid(),fractionalTrade,fractionalBinding,0,0,new(f.Model.Build(before,[]),discount));
        var fractionalPayment=new SideCashAllocation(Guid.NewGuid(),fractionalReceipt.Id,at,at,ExactMoney.Parse("1",instrument.Currency));
        var fractionalState=new CashflowBindingValuation(fractionalTrade.Id,fractionalBinding,fractionalTrade.Instruments[0].Quantity,
            [new(fractionalTrade,fractionalBinding,0,[fractionalReceipt],[fractionalPayment],PaymentSideState.Policy)]);
        Equal(fraction.PV(ordinary.WithBindingState(fractionalState)).PresentValue,"7/3");
        var based=new RiskPosition(trade.Id,binding,trade.BookId,instrument.Id,trade.Instruments[0].Quantity,
            new(new Money(ExactDecimal.Parse("1"),instrument.Currency),instrument.UnitQuantity.Specification),new(default,instrument.Currency));
        Reject("valuation_measure",()=>new ValuationPortfolio([instrument],[based],new(ValuationMeasure.UnpaidCashPresentValue,
            ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discount)));
        Console.WriteLine("PASS common cash state: inherited Instrument/Side PV, fixed overdue balance, partial/full payment, knowledge replay and frozen determination");
    }

    [EconomicDefinition("synthetic_cash_state_specimen_v1")]
    private sealed record CashSpecimen:SidedInstrument
    {
        public override ReferenceKey Currency=>((FloatingSide)Sides[0].Side).Currency;
        public override Quantity UnitQuantity=>((FloatingSide)Sides[0].Side).UnitQuantity;
        public CashSpecimen(Guid id,FloatingSide side):base(id,[new(side,ExactDecimal.Parse("1"))]){}
        public override void ValidateValuationState(IBindingValuationState? state)
        {
            if(state is not CashflowBindingValuation input||input.Sides.Length!=1)throw new MarketDataException("payment_state","Select the explicit cash state.");
            input.Sides[0].Require(Sides[0].Side,Sides[0],input.Quantity);
        }
        internal override UnitPricingContract DeclareScope(UnitValuationContext context,bool lifecycle)=>Declare(context);
    }
    private static void Equal(ExactMoney actual,string expected)
    {if(actual.ToExactString()!=expected)throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
