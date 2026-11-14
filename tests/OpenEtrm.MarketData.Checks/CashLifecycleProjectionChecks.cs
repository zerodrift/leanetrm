using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class CashLifecycleProjectionChecks
{
    internal static void Run()
    {
        var f=new Fixture();var source=f.Snapshot();source=source.WithKnowledge(Guid.NewGuid(),source.ValuationAt.AddDays(2));
        var at=source.ValuationAt;var currency=f.R("currency");
        LifecycleProjectionValue Value(string face,string recorded,bool pay,int horizonHours=8,int dueHours=2)
        {
            var instrument=new TimedCashPayment(currency,at.AddHours(dueHours));var binding=Guid.NewGuid();
            var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(binding,instrument,new(ExactDecimal.Parse(face),currency))]);
            var owner=new AccountingPositionKey(trade.Id,binding,trade.BookId);
            var actual=new RecordedCashValuation(trade,binding,[new(Guid.NewGuid(),1,owner,at.AddHours(4),source.Knowledge!.KnownAt,
                ExactMoney.Parse(recorded,currency),AccountingFlowKind.EconomicCash)],RecordedCashValuation.Policy,source.Knowledge.ContextId,source.Knowledge.KnownAt);
            var projection=new CashLifecycleProjection(owner,instrument,actual,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted),source);
            var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Linear,
                pay?LifecycleProjectionPolicy.PayAtDue:LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice);
            var horizon=new LifecycleProjectionHorizon(Guid.NewGuid(),1,f.R("calendar"),[new(new(2040,12,18),at.AddHours(horizonHours),0)]);
            return LifecycleProjection.Prepare(f.Model(),source,horizon,policy,[projection],[]).Evaluate("cash",[]).Values.Single();
        }
        void Equal(ExactMoney value,string expected){if(value.CompareTo(ExactMoney.Parse(expected,currency))!=0)throw new Exception($"Expected {expected}; got {value.ToExactString()}.");}
        var paid=Value("16","6",true);Equal(paid.OpeningValue,"16");Equal(paid.PeriodCash,"16");Equal(paid.ResidualValue,"0");Equal(paid.EconomicPnl,"0");
        var outstanding=Value("16","6",false);Equal(outstanding.PeriodCash,"6");Equal(outstanding.ResidualValue,"10");Equal(outstanding.EconomicPnl,"0");
        var laterActual=Value("16","6",true,3);Equal(laterActual.PeriodCash,"10");Equal(laterActual.ResidualValue,"6");Equal(laterActual.EconomicPnl,"0");
        var payable=Value("-16","-6",true);Equal(payable.PeriodCash,"-16");Equal(payable.ResidualValue,"0");Equal(payable.EconomicPnl,"0");
        var overdue=Value("16","6",true,8,-1);Equal(overdue.PeriodCash,"6");Equal(overdue.ResidualValue,"10");
        var overpaid=Value("16","20",false);Equal(overpaid.PeriodCash,"20");Equal(overpaid.ResidualValue,"-4");Equal(overpaid.EconomicPnl,"0");
        try{Value("16","20",true);throw new Exception("A scheduled-payment assumption invented an opposite-direction refund.");}
        catch(MarketDataException error)when(error.Code=="settlement_conservation"){}
        Console.WriteLine("PASS cash lifecycle: actual known payments stay fixed, projected paid/outstanding choices conserve value and overdue cash is never paid by its date");
    }
}
