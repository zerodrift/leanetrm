using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class UnpaidCashChecks
{
    internal static void Run()
    {
        var f=new IndexFixingChecks.FixingFixture();var at=f.Payment;var cash=new CashPayment(f.Floating().Currency,DateOnly.FromDateTime(at.UtcDateTime));
        var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(binding,cash,new(ExactDecimal.Parse("200"),cash.Currency))]);
        MarketSnapshot Snapshot(DateTimeOffset instant,DateTimeOffset? known=null)=>f.Snapshot(instant,1).WithKnowledge(Guid.NewGuid(),known??instant);
        var discount=new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit);
        ExactMoney Value(MarketSnapshot snapshot,RecordedCashValuation? state,Trade? selected=null)
        {
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discount)
                {BindingStates=state is null?ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty:
                    ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)};
            var portfolio=ValuationPortfolio.FromTrades([selected??trade],setup);
            return RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model,snapshot,[portfolio]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
        }
        var owner=new AccountingPositionKey(trade.Id,binding,trade.BookId);
        AccountingFlow Payment(string amount,DateTimeOffset paid,DateTimeOffset known)=>new(Guid.NewGuid(),1,owner,paid,known,ExactMoney.Parse(amount,cash.Currency),AccountingFlowKind.EconomicCash);
        RecordedCashValuation State(params AccountingFlow[] payments)=>new(trade,binding,[..payments],RecordedCashValuation.Policy,Guid.NewGuid(),at.AddDays(3));
        var after=Snapshot(at.AddDays(1));
        Reject("payment_state",()=>Value(after,null));
        Equal(Value(after,State()),"200"); // Overdue, unpaid is still a receivable.
        Equal(Value(Snapshot(at),State()),"200"); // Due at the boundary is not paid at the boundary.
        Equal(Value(Snapshot(at.AddDays(-1)),State()),"190");
        var part=Payment("50",at,at);
        Equal(Value(after,State(part)),"150");
        var full=Payment("200",at,at);
        Equal(Value(after,State(full)),"0");
        Equal(Value(after,State(Payment("200",at,at.AddDays(2)))),"200"); // Paid fact not yet known.
        Equal(Value(after,State(Payment("200",at.AddDays(2),at))),"200"); // Future effective cash not yet paid.
        var correction=part with{Revision=2,KnownAt=at.AddDays(2),Amount=ExactMoney.Parse("40",cash.Currency)};
        Equal(Value(after,State(part,correction)),"150");Equal(Value(Snapshot(at.AddDays(3)),State(correction,part)),"160");
        // Reconciliation uses the actual PV boundary and its same immutable cash allocations.
        var before=Snapshot(at.AddDays(-1));var close=Snapshot(at.AddDays(1));var state=State(full);
        PnlAccountingPoint Point(MarketSnapshot snapshot)=>new(snapshot.Knowledge!.ContextId,snapshot.Id,snapshot.ValuationAt,snapshot.Knowledge.KnownAt,
            [new(owner,Value(snapshot,state))],state.KnownFlows(snapshot),PnlAccountingCoverage.Complete);
        var pnl=TradingPnlAccounting.Compare(Point(before),Point(close),TradingPnlAccounting.Version1).Positions.Single();
        Equal(pnl.OpeningValue,"190");Equal(pnl.PeriodCash,"200");Equal(pnl.ClosingValue,"0");Equal(pnl.EconomicPnl,"10"); // The selected discount unwind, not 200 of manufactured earnings.
        var moved=new Trade(trade.Id,Guid.NewGuid(),trade.Instruments);
        Reject("settlement_scope",()=>Value(after,State(part),moved));
        Equal(Value(after,State(Payment("1/3",at,at))),"599/3");
        var payable=new Trade(Guid.NewGuid(),trade.BookId,[new(Guid.NewGuid(),cash,new(ExactDecimal.Parse("-200"),cash.Currency))]);
        var negativeBinding=payable.Instruments[0].Id;var negativeOwner=new AccountingPositionKey(payable.Id,negativeBinding,payable.BookId);
        var negative=Payment("-200",at,at) with{Position=negativeOwner};
        var negativeState=new RecordedCashValuation(payable,negativeBinding,[negative],RecordedCashValuation.Policy,Guid.NewGuid(),at.AddDays(3));
        var paired=ValuationPortfolio.FromTrades([trade,payable],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discount)
        {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),State(part)).Add(new(payable.Id,negativeBinding),negativeState)});
        var pairedResult=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model,after,[paired]),[],RiskEngine.ExactNumericalPolicy);
        Equal(pairedResult.Groups.Single().BaseValues.Single().Value,"150");
        if(pairedResult.Work.UniqueValuations!=2)throw new Exception("Different paid states shared a reusable cash unit valuation.");
        var zeroTrade=new Trade(trade.Id,trade.BookId,[new(binding,cash,new(default,cash.Currency))]);
        Reject("payment_state",()=>Value(after,new(zeroTrade,binding,[part],RecordedCashValuation.Policy,Guid.NewGuid(),at.AddDays(3)),zeroTrade));
        Reject("payment_state",()=>Value(Snapshot(at.AddDays(4)),State()));
        Reject("payment_state",()=>State(part,part));
        Reject("payment_state",()=>State(part with{Position=owner with{BindingId=Guid.NewGuid()}}));
        Reject("payment_state",()=>State(part,part with{Revision=2}));
        Console.WriteLine("PASS cash PV excludes only recorded paid cash; due, partial, historical and future-value cash reconcile through the common PV boundary.");
    }
    private static void Equal(ExactMoney actual,string expected)
    {if(actual.CompareTo(ExactMoney.Parse(expected,actual.Currency))!=0)throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
