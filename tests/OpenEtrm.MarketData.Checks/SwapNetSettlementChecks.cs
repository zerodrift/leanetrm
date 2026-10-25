using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class SwapNetSettlementChecks
{
    public static int Run()
    {
        var f=new IndexFixingChecks.FixingFixture(monthly:true);
        var currency=f.Swap().Currency;var trade=Guid.NewGuid();var catalog=Guid.NewGuid();
        var terms=new SwapNetTerms(new(catalog,Guid.NewGuid(),1),new(catalog,Guid.NewGuid(),1),
            new(currency,2,CashTieRule.ToEven,SwapSettlementRounding.Implementation),"swap_due_nominal_v1","swap_completed_period_trading_pnl_v1","swap_explicit_correction_due_v1");
        ExactMoney Cash(string value)=>ExactMoney.Parse(value,currency);
        SwapNetCandidate Candidate(string amount)=>new(trade,new(Guid.NewGuid(),0,f.From,f.To,1,f.Payment,Cash(amount),Cash("0")),terms);
        var pay=Candidate("-300");var receive=Candidate("350");
        SwapNetPlan Plan(ImmutableArray<SwapNetCandidate> candidates,ImmutableArray<SwapNetAllocation> allocations,string cash)=>
            SwapNetSettlement.Calculate(candidates,allocations,Cash(cash),SwapNetSettlement.Policy);
        ImmutableArray<SwapNetAllocation> allocations=[new(pay.Contribution.Id,Cash("-300")),new(receive.Contribution.Id,Cash("350"))];
        var full=Plan([pay,receive],allocations,"50");
        Equal(full.Cash.ToExactString(),"50");Equal(full.GrossPaid.ToExactString(),"-300");Equal(full.GrossReceived.ToExactString(),"350");
        if(full.Remaining.Any(c=>c.Outstanding!=Cash("0")))throw new Exception("Gross balances must both clear");
        var zero=Plan([pay,receive with{Contribution=receive.Contribution with{Amount=Cash("300")}}],
            [allocations[0],new(receive.Contribution.Id,Cash("300"))],"0");
        Equal(zero.IsNoncashDischarge,true);
        var partial=Plan([pay,receive],[new(pay.Contribution.Id,Cash("-100")),new(receive.Contribution.Id,Cash("120"))],"20");
        Equal(partial.Remaining[0].Outstanding.ToExactString(),"-200");Equal(partial.Remaining[1].Outstanding.ToExactString(),"230");
        Reject(()=>Plan([pay,receive],allocations,"49"));
        Reject(()=>Plan([pay,receive],[new(pay.Contribution.Id,Cash("-301")),new(receive.Contribution.Id,Cash("350"))],"49"));
        Reject(()=>Plan([pay,receive],[new(pay.Contribution.Id,Cash("1")),new(receive.Contribution.Id,Cash("350"))],"351"));
        Reject(()=>Plan([pay,receive with{TradeId=Guid.NewGuid()}],allocations,"50"));
        Reject(()=>Plan([pay,receive with{Terms=terms with{Counterparty=new(Guid.NewGuid(),Guid.NewGuid(),1)}}],allocations,"50"));
        Reject(()=>Plan([pay,receive with{Contribution=receive.Contribution with{DueAt=f.Payment.AddDays(1)}}],allocations,"50"));
        Reject(()=>Plan([pay,receive],[allocations[0],new(receive.Contribution.Id,Cash("349.999"))],"49.999"));
        Reject(()=>Plan([pay,receive],[allocations[0],allocations[0]],"-600"));
        // Correction contributions use their own balance and explicit due, including opposite directions.
        var correction=receive with{Contribution=receive.Contribution with{Id=Guid.NewGuid(),Ordinal=2,Amount=Cash("-20")}};
        Equal(Plan([correction,receive],[new(correction.Contribution.Id,Cash("-20")),new(receive.Contribution.Id,Cash("30"))],"10").Cash.ToExactString(),"10");
        var foreign=currency with{Revision=currency.Revision with{RevisionId=Guid.NewGuid()}};
        Reject(()=>Plan([pay,receive with{Terms=terms with{Rounding=new(foreign,2,CashTieRule.ToEven,SwapSettlementRounding.Implementation)}}],allocations,"50"));
        Reject(()=>Plan([pay,receive with{Terms=terms with{Rounding=new(currency,3,CashTieRule.ToEven,SwapSettlementRounding.Implementation)}}],allocations,"50"));
        Reject(()=>Plan([pay,receive],[null!,allocations[1]],"50"));
        Reject(()=>Plan([pay],[allocations[0]],"-300"));
        Console.WriteLine("PASS explicit same-trade gross/net allocation, zero discharge, partial balances and eligibility refusals");return 20;
    }
    static void Equal<T>(T actual,T expected){if(!Equals(actual,expected))throw new Exception($"Expected {expected}, got {actual}");}
    static void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="settlement_net_allocation"){return;}throw new Exception("Expected settlement_net_allocation");}
}
