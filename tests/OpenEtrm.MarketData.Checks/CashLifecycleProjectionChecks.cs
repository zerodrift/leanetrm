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
                LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
            var horizon=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,18),at.AddHours(horizonHours),0)]);
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
        LifecycleProjectionValue Timeline(string face,ImmutableArray<(Guid Id,int Revision,int Hour,string Amount,int KnownDay)> rows,int horizonHours,int knownDay=2)
        {
            var selected=f.Snapshot().WithKnowledge(Guid.NewGuid(),at.AddDays(knownDay));
            var cash=new TimedCashPayment(currency,at.AddHours(2));var binding=Guid.NewGuid();
            var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(binding,cash,new(ExactDecimal.Parse(face),currency))]);
            var owner=new AccountingPositionKey(trade.Id,binding,trade.BookId);
            var actual=new RecordedCashValuation(trade,binding,[..rows.Where(r=>r.KnownDay<=knownDay).Select(r=>new AccountingFlow(r.Id,r.Revision,owner,
                at.AddHours(r.Hour),at.AddDays(r.KnownDay),ExactMoney.Parse(r.Amount,currency),AccountingFlowKind.EconomicCash))],RecordedCashValuation.Policy,
                selected.Knowledge!.ContextId,selected.Knowledge.KnownAt);
            var projection=new CashLifecycleProjection(owner,cash,actual,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted),selected);
            var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,
                LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
            var horizon=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_cash_reversal",1),f.R("calendar"),[new(new(2040,12,18),at.AddHours(horizonHours),0)]);
            return LifecycleProjection.Prepare(f.Model(),selected,horizon,policy,[projection],[]).Evaluate("cash_reversal",[]).Values.Single();
        }
        foreach(var sign in new[]{1,-1})
        {
            string S(string amount)=>sign<0?"-"+amount:amount;
            var receipt=Guid.NewGuid();var reversal=Guid.NewGuid();
            ImmutableArray<(Guid,int,int,string,int)> flows=[(receipt,1,4,S("2"),1),(receipt,2,4,S("6"),2),(reversal,1,7,sign<0?"6":"-6",2)];
            var during=Timeline(S("16"),flows,5);Equal(during.PeriodCash,S("16"));Equal(during.ResidualValue,"0");Equal(during.EconomicPnl,"0");
            var after=Timeline(S("16"),flows,8);Equal(after.PeriodCash,S("10"));Equal(after.ResidualValue,S("6"));Equal(after.EconomicPnl,"0");
            var earlier=Timeline(S("16"),flows,3,1);Equal(earlier.PeriodCash,S("14"));Equal(earlier.ResidualValue,S("2"));
            var latest=Timeline(S("16"),flows,3);Equal(latest.PeriodCash,S("10"));Equal(latest.ResidualValue,S("6"));
            ImmutableArray<(Guid,int,int,string,int)> simultaneous=[(Guid.Parse("00000000-0000-0000-0000-000000000001"),1,4,S("10"),1),
                (Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),1,4,sign<0?"4":"-4",1),(Guid.NewGuid(),1,7,sign<0?"6":"-6",1)];
            var net=Timeline(S("16"),simultaneous,5);Equal(net.PeriodCash,S("16"));Equal(net.ResidualValue,"0");
        }
        var face=new TimedCashPayment(currency,at.AddHours(8));var faceBinding=Guid.NewGuid();
        var faceTrade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(faceBinding,face,new(ExactDecimal.Parse("100"),currency))]);
        var faceOwner=new AccountingPositionKey(faceTrade.Id,faceBinding,faceTrade.BookId);
        var faceActual=new RecordedCashValuation(faceTrade,faceBinding,[],RecordedCashValuation.Policy,source.Knowledge!.ContextId,source.Knowledge.KnownAt);
        var faceProjection=new CashLifecycleProjection(faceOwner,face,faceActual,new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit),source);
        var facePolicy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,
            LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
        var faceHorizon=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,18),at.AddHours(8),0)]);
        var facePrepared=LifecycleProjection.Prepare(f.Model(),source,faceHorizon,facePolicy,[faceProjection],[]);
        var deterministic=LifecycleEconomicRisk.Deterministic([facePrepared],new(LifecycleEconomicRiskPolicy.Deterministic,"0.975",HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail));
        Equal(deterministic.NoShockTimeCarry.Single().OpeningValue,"95");Equal(deterministic.NoShockTimeCarry.Single().EconomicPnl,"5");
        if(deterministic.Economic.Single().ValueAtRisk!="-5"||deterministic.Economic.Single().ExpectedShortfall!="-5"||
            deterministic.MarketOnlyDiagnostic.Single().ValueAtRisk!="0"||deterministic.Economic.Single().Samples.Single().Id!="no_variable_factors")
            throw new Exception("A deterministic five-unit carry gain lost its signed point-mass risk or invented historical samples.");
        Console.WriteLine("PASS cash lifecycle: actual known payments stay fixed, projected paid/outstanding choices conserve value and overdue cash is never paid by its date");
    }
}
