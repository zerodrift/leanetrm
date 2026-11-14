using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

internal static class TimedCashChecks
{
    public static void Run()
    {
        var f=new Fixture();var at=f.Snapshot().ValuationAt;var due=at.AddHours(13);
        var cash=new TimedCashPayment(f.R("currency"),due);
        var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(binding,cash,new(ExactDecimal.Parse("16"),cash.Currency))]);
        var owner=new AccountingPositionKey(trade.Id,binding,trade.BookId);
        ExactMoney Value(DateTimeOffset instant,params AccountingFlow[] payments)
        {
            var snapshot=(f.Snapshot() with{ValuationAt=instant}).WithKnowledge(Guid.NewGuid(),at.AddDays(2));
            var state=new RecordedCashValuation(trade,binding,[..payments],RecordedCashValuation.Policy,Guid.NewGuid(),at.AddDays(2));
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit))
                {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)};
            return RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),snapshot,[ValuationPortfolio.FromTrades([trade],setup)]),[],RiskEngine.ExactNumericalPolicy)
                .Groups.Single().BaseValues.Single().Value;
        }
        void Equal(ExactMoney value,string expected){if(value.ToExactString()!=expected)throw new Exception($"Expected {expected}, got {value.ToExactString()}.");}
        Equal(Value(due.AddTicks(-10)),"15.2");Equal(Value(due),"16");Equal(Value(due.AddHours(1)),"16");
        Equal(Value(due.AddHours(1),new AccountingFlow(Guid.NewGuid(),1,owner,due,at.AddDays(1),ExactMoney.Parse("6",cash.Currency),AccountingFlowKind.EconomicCash)),"10");
        var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),at.AddDays(2));
        ICashPaymentValuation retained=new RecordedCashValuation(trade,binding,[new(Guid.NewGuid(),1,owner,due,at.AddDays(1),
            ExactMoney.Parse("6",cash.Currency),AccountingFlowKind.EconomicCash)],RecordedCashValuation.Policy,source.Knowledge!.ContextId,source.Knowledge.KnownAt);
        Equal(retained.UnitOutstanding(source),"1");Equal(retained.UnitOutstanding(source,due),"0.625");
        if(!retained.FutureChanges(source).SequenceEqual(new[]{due})||source.ValuationAt!=at)
            throw new Exception("The cash calculation clock changed its actual source certificate or erased a known future-effective payment.");
        var later=new TimedCashPayment(cash.Currency,due.AddHours(1));
        if(later.Id==cash.Id||cash.EconomicIdentity==later.EconomicIdentity)throw new Exception("Exact cash due instants must distinguish definitions.");
        Console.WriteLine("PASS timed cash: exact due boundary, unpaid continuity, recorded partial payment and definition identity");
    }
}
