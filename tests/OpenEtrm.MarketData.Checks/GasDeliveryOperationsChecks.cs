using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

internal static class GasDeliveryOperationsChecks
{
    internal static void Run()
    {
        var f=new CommodityLookupFixture(1);var unit=f.Quote.Quantity;
        var start=new DateTimeOffset(2021,1,1,6,0,0,TimeSpan.Zero);var end=start.AddDays(1);var at=end.AddDays(2);
        ExactQuantity Q(string n)=>ExactQuantity.FromQuantity(new(ExactDecimal.Parse(n),unit));
        var day=new GasDailyContract("gas/0/quantity",0,0,new(2021,1,1),start,end,Q("100"));
        Equal(GasNominationWorkflow.Phase(day,start.AddHours(-1),at),"before_flow");
        Equal(GasNominationWorkflow.Phase(day,start,at),"intraday");
        Equal(GasNominationWorkflow.Phase(day,end,at),"retro");
        GasSchedulingDecision Decision(string amount,bool approved=true)=>new(approved,approved?Q(amount):null,end,"Synthetic external acknowledgment");
        var accepted=GasNominationWorkflow.Accept(day,Q("100"),Decision("95"),Decision("90"),false,at,GasNominationWorkflow.Profile);
        Equal(accepted.Confirmed.ToExactString(),"95");Equal(accepted.Scheduled.ToExactString(),"90");
        Reject(()=>GasNominationWorkflow.Accept(day,Q("100"),Decision("95"),Decision("96"),false,at,GasNominationWorkflow.Profile));
        Reject(()=>GasNominationWorkflow.Accept(day,Q("100"),Decision("0",false),Decision("90"),false,at,GasNominationWorkflow.Profile));
        Reject(()=>GasNominationWorkflow.Accept(day,Q("100"),Decision("95"),Decision("90"),true,at,GasNominationWorkflow.Profile));
        var scope=new GasBalanceScope(new(unit.Revision,"pipeline"),new(unit.Revision,"zone"),new(unit.Revision,"gas"),unit,start,end);
        GasBalanceStatement Statement(string n,string status="final",Guid? included=null)=>new(Guid.NewGuid(),Q(n),status,included);
        var outgoing=new GasBalanceMovement(Guid.NewGuid(),1,Q("-200"));
        Equal(GasImbalanceTrading.Balance(Statement("250"),[outgoing],GasImbalanceTrading.ReportedPolicy).ToExactString(),"50");
        Equal(GasImbalanceTrading.Balance(Statement("50",included:outgoing.Id),[outgoing],GasImbalanceTrading.ReportedPolicy).ToExactString(),"50");
        Equal(GasImbalanceTrading.Balance(Statement("150"),[outgoing],GasImbalanceTrading.ReportedPolicy).ToExactString(),"-50");
        Equal(GasImbalanceTrading.Balance(Statement("0"),[],GasImbalanceTrading.FinalPolicy).ToExactString(),"0");
        Reject(()=>GasImbalanceTrading.Balance(Statement("250","provisional"),[],GasImbalanceTrading.FinalPolicy));
        Reject(()=>GasImbalanceTrading.Balance(Statement("50",included:Guid.NewGuid()),[outgoing],GasImbalanceTrading.ReportedPolicy));
        var lineage=Enumerable.Range(1,101).Select(i=>new GasBalanceMovement(Guid.NewGuid(),i,Q("-1"))).ToImmutableArray();
        Equal(GasImbalanceTrading.Balance(Statement("250"),lineage,GasImbalanceTrading.FinalPolicy).ToExactString(),"149");
        Equal(GasImbalanceTrading.Balance(Statement("150",included:lineage[99].Id),lineage,GasImbalanceTrading.FinalPolicy).ToExactString(),"149");
        Reject(()=>GasImbalanceTrading.Balance(Statement("250"),[lineage[0],lineage[2]],GasImbalanceTrading.FinalPolicy));
        var terms=new GasImbalanceCashTerms(new(ExactDecimal.Parse("3"),f.Quote),"buyer",2,GasCashBilling.RoundingImplementation,end.AddDays(1),end.AddDays(30));
        var trade=GasImbalanceTrading.Transfer(scope,scope,Q("-200"),Q("250"),Q("200"),terms,at,GasImbalanceTrading.Profile);
        Equal(trade.BuyerBalance.ToExactString(),"0");Equal(trade.SellerBalance.ToExactString(),"50");Equal(trade.Cash.Amount.ToString(),"-600");
        Equal(trade.BuyerMovement.Add(trade.SellerMovement).ToExactString(),"0");
        Equal(GasImbalanceTrading.Transfer(scope,scope,Q("-200"),Q("250"),Q("200"),terms with{OwnedRole="seller"},at,GasImbalanceTrading.Profile).Cash.Amount.ToString(),"600");
        Equal(GasImbalanceTrading.Transfer(scope,scope,Q("-200"),Q("250"),Q("200"),terms with{Price=new(ExactDecimal.Parse("-3"),f.Quote)},at,GasImbalanceTrading.Profile).Cash.Amount.ToString(),"600");
        Reject(()=>GasImbalanceTrading.Transfer(scope,scope,Q("-200"),Q("250"),Q("201"),terms,at,GasImbalanceTrading.Profile));
        Reject(()=>GasImbalanceTrading.Transfer(scope,scope with{End=end.AddHours(1)},Q("-200"),Q("250"),Q("200"),terms,at,GasImbalanceTrading.Profile));
        Reject(()=>GasImbalanceTrading.Transfer(scope,scope,Q("-200"),Q("-50"),Q("1"),terms,at,GasImbalanceTrading.Profile));
        Console.WriteLine("PASS gas operations: native request phases, accepted schedule cuts, statement transfer coverage, corrected deficits, paired conservation and signed cash");
    }
    static void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}, got {actual}");}
    static void Reject(Action action){try{action();}catch(MarketDataException){return;}throw new Exception("Expected explicit operations refusal");}
}
