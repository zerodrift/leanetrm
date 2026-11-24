using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class CashSideAlignmentChecks
{
    internal static void Run(Action<SidedInstrument, SideValuationContext>? inspect = null)
    {
        var f=new Fixture();var at=f.Snapshot().ValuationAt;var due=at.AddHours(13);
        Instrument cash=new TimedCashPayment(f.R("currency"),due);
        if(cash is not SidedInstrument sided||sided.Sides.Length!=1||sided.Sides[0].Coefficient!=ExactDecimal.Parse("1"))
            throw new Exception("A real cash Instrument must own one independently valued cash Side, with the signed amount held outside it.");
        var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(binding,cash,new(ExactDecimal.Parse("16"),cash.Currency))]);
        var owner=new AccountingPositionKey(trade.Id,binding,trade.BookId);
        Check(at,[],"15.2","0","16");
        Check(due.AddTicks(-10),[],"15.2","0","16");
        Check(due,[],"16","0","16");
        var partial=new AccountingFlow(Guid.NewGuid(),1,owner,due,at.AddDays(1),ExactMoney.Parse("6",cash.Currency),AccountingFlowKind.EconomicCash);
        Check(due.AddHours(1),[partial],"10","6","10");
        var paid=partial with{Amount=ExactMoney.Parse("16",cash.Currency)};
        Check(due.AddHours(1),[paid],"0","16","0");
        if(sided.Sides[0].Side.GenerateSchedule().Rows.Count!=1)
            throw new Exception("The cash Side must expose its genuine one-payment schedule without a delivery calendar.");
        var book=Guid.NewGuid();var positive=new Trade(Guid.NewGuid(),book,[new(Guid.NewGuid(),cash,new(ExactDecimal.Parse("16"),cash.Currency))]);
        var negative=new Trade(Guid.NewGuid(),book,[new(Guid.NewGuid(),cash,new(ExactDecimal.Parse("-16"),cash.Currency))]);
        var positiveBinding=positive.Instruments.Single().Id;var negativeBinding=negative.Instruments.Single().Id;
        var later=(f.Snapshot() with{ValuationAt=due}).WithKnowledge(Guid.NewGuid(),at.AddDays(2));
        RecordedCashValuation State(Trade held,params AccountingFlow[] payments)=>new(held,held.Instruments.Single().Id,[..payments],
            RecordedCashValuation.Policy,later.Knowledge!.ContextId,later.Knowledge.KnownAt);
        var negativePayment=new AccountingFlow(Guid.NewGuid(),1,new(negative.Id,negativeBinding,book),due,at.AddDays(1),
            ExactMoney.Parse("-6",cash.Currency),AccountingFlowKind.EconomicCash);
        var noProvider=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
            {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty
                .Add(new(positive.Id,positiveBinding),State(positive)).Add(new(negative.Id,negativeBinding),State(negative,negativePayment))};
        var isolated=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),later,[ValuationPortfolio.FromTrades([positive,negative],noProvider)]),[],RiskEngine.ExactNumericalPolicy);
        if(isolated.Groups.Single().BaseValues.Single().Value.ToExactString()!="6")
            throw new Exception("One reusable cash Side must keep independent paid states and external signs; due cash needs no discount provider.");
        var fullPayment=negativePayment with{Amount=ExactMoney.Parse("-16",cash.Currency)};
        var paidWithoutProvider=noProvider with{BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(negative.Id,negativeBinding),State(negative,fullPayment))};
        var fullyPaid=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),later,[ValuationPortfolio.FromTrades([negative],paidWithoutProvider)]),[],RiskEngine.ExactNumericalPolicy);
        if(fullyPaid.Groups.Single().BaseValues.Single().Value.ToExactString()!="0"||!fullyPaid.Groups.Single().Contracts.Single().DiscountInputs.IsEmpty)
            throw new Exception("Fully paid signed cash must leave no remaining PV or unused discount-provider dependency.");
        Console.WriteLine("PASS cash Side: one real payment, unit-sign ownership, discount/due/paid PV and native inspection");

        void Check(DateTimeOffset instant,ImmutableArray<AccountingFlow> history,string expected,string expectedPaid,string expectedOutstanding)
        {
            var snapshot=(f.Snapshot() with{ValuationAt=instant}).WithKnowledge(Guid.NewGuid(),at.AddDays(2));
            var state=new RecordedCashValuation(trade,binding,history,RecordedCashValuation.Policy,snapshot.Knowledge!.ContextId,snapshot.Knowledge.KnownAt);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit))
                {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)};
            var prepared=RiskEngine.Prepare(f.Model(),snapshot,[ValuationPortfolio.FromTrades([trade],setup)]);
            var run=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy);
            if(run.Groups.Single().BaseValues.Single().Value.ToExactString()!=expected)throw new Exception("The Side path changed genuine cash remaining PV.");
            var context=new UnitValuationContext(snapshot,setup,prepared.Market.Pricing,prepared.Contracts[0][0],bindingState:state);
            var value=sided.PV(context.SideContext());
            inspect?.Invoke(sided, context.SideContext());
            var side=value.Sides.Single().Value;
            var inspected=side.InspectSchedule("synthetic_cash_side",ExactDecimal.Parse("16"),new SignedRatio(1,1),
                day=>new(day.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero)).Read(0,4).Rows.Single();
            if(inspected.Kind!="Payment"||inspected.ValueAt!=due||inspected.Start is not null||inspected.End is not null||
                inspected.PresentValue!=expected||inspected.Paid!=expectedPaid||inspected.Outstanding!=expectedOutstanding)
                throw new Exception("Cash Side inspection must expose the evaluated payment and paid state without fabricating physical delivery dates.");
            if(inspected.QuantityReferenceKind!=ScheduleReferenceKinds.Currency)throw new Exception("Cash quantity inspection must declare currency identity, independently of the reference code.");
            if(inspected.PaymentTiming!="instant")
                throw new Exception("Exact cash inspection must explicitly retain instant timing rather than truncate it to a payment date.");
            if(inspected.Lifecycle.Due!=(instant>=due?"Due":"NotDue"))
                throw new Exception("An exact-instant cash Side must not label cash due before its contractual microsecond.");
        }
    }
}
