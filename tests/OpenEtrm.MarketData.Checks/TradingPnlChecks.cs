using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class TradingPnlChecks
{
    internal static void Run()
    {
        var currency=new ReferenceKey(new(Guid.NewGuid(),Guid.NewGuid()),"usd");
        var key=new AccountingPositionKey(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        var opening=DateTimeOffset.Parse("2020-12-18T21:00:00Z");var closing=opening.AddDays(3);
        ExactMoney Money(string value)=>ExactMoney.Parse(value,currency);
        AccountingFlow Cash(string value,DateTimeOffset at,DateTimeOffset known)=>new(Guid.NewGuid(),1,key,at,known,Money(value),AccountingFlowKind.EconomicCash);
        PnlAccountingPoint Point(DateTimeOffset at,string? value,ImmutableArray<AccountingFlow> cash)=>new(Guid.NewGuid(),Guid.NewGuid(),at,at,
            value is null?[]:[new(key,Money(value))],cash,PnlAccountingCoverage.Complete);
        var paid=Cash("100",closing,closing);
        var settled=TradingPnlAccounting.Compare(Point(opening,"100",[]),Point(closing,"0",[paid]),TradingPnlAccounting.Version1).Positions.Single();
        Equal(settled.ValueMovement,"-100");Equal(settled.PeriodCash,"100");Equal(settled.EconomicPnl,"0");
        // A new purchase's negative premium offsets its closing asset. PV growth alone is not profit.
        var purchase=TradingPnlAccounting.Compare(Point(opening,null,[]),Point(closing,"105",[Cash("-100",closing,closing)]),TradingPnlAccounting.Version1).Positions.Single();
        Equal(purchase.EconomicPnl,"5");if(purchase.HeldAtOpening||!purchase.HeldAtClosing)throw new Exception("New position identity was lost.");
        // A later-known correction to prior cash is separate from this period's effective cash.
        var prior=Cash("25",opening.AddDays(-1),opening.AddDays(-1));var corrected=prior with{Revision=2,KnownAt=closing,Amount=Money("27")};
        var restated=TradingPnlAccounting.Compare(Point(opening,"10",[prior]),Point(closing,"10",[corrected]),TradingPnlAccounting.Version1).Positions.Single();
        Equal(restated.PeriodCash,"0");Equal(restated.PriorCashKnowledgeAdjustment,"2");Equal(restated.EconomicPnl,"2");
        var transfer=Cash("100",closing,closing) with{Kind=AccountingFlowKind.NonEarningTransfer};
        var moved=TradingPnlAccounting.Compare(Point(opening,null,[]),Point(closing,"100",[transfer]),TradingPnlAccounting.Version1).Positions.Single();
        Equal(moved.NonEarningTransfers,"100");Equal(moved.EconomicPnl,"0");
        var boundary=Cash("7",opening,opening);
        Equal(TradingPnlAccounting.Compare(Point(opening,"0",[boundary]),Point(closing,"0",[boundary]),TradingPnlAccounting.Version1).Positions.Single().EconomicPnl,"0");
        // Explicit zero holdings and an absent holding have different position evidence, even when values agree.
        var closed=TradingPnlAccounting.Compare(Point(opening,"0",[]),Point(closing,null,[]),TradingPnlAccounting.Version1).Positions.Single();
        if(!closed.HeldAtOpening||closed.HeldAtClosing)throw new Exception("Closed position identity was lost.");
        Reject("pnl_cash_revision",()=>TradingPnlAccounting.Compare(Point(opening,"0",[]),Point(closing,"0",[paid,paid]),TradingPnlAccounting.Version1));
        var deferred=prior with{Revision=2,KnownAt=closing,EffectiveAt=closing.AddDays(1)};
        var deferredReport=TradingPnlAccounting.Compare(Point(opening,"0",[prior]),Point(closing,"25",[deferred]),TradingPnlAccounting.Version1).Positions.Single();
        Equal(deferredReport.PriorCashKnowledgeAdjustment,"-25");Equal(deferredReport.PeriodCash,"0");Equal(deferredReport.EconomicPnl,"0");
        Reject("pnl_cash_context",()=>TradingPnlAccounting.Compare(Point(opening,"0",[]),Point(closing,"0",[paid with{KnownAt=closing.AddYears(2)}]),TradingPnlAccounting.Version1));
        Reject("pnl_accounting_coverage",()=>TradingPnlAccounting.Compare(Point(opening,"0",[]) with{Coverage=PnlAccountingCoverage.RemainingValueOnly},Point(closing,"0",[]),TradingPnlAccounting.Version1));
        Reject("pnl_policy",()=>TradingPnlAccounting.Compare(Point(opening,"0",[]),Point(closing,"0",[]),""));
        Reject("pnl_context",()=>TradingPnlAccounting.Compare(Point(closing,"0",[]),Point(opening,"0",[]),TradingPnlAccounting.Version1));
        // Posted fees remain earnings even when no asset remains at either endpoint.
        Equal(TradingPnlAccounting.Compare(Point(opening,null,[]),Point(closing,null,[Cash("-3",closing,closing)]),TradingPnlAccounting.Version1).Positions.Single().EconomicPnl,"-3");
        // Late knowledge is not cash received in the current business interval.
        var late=Cash("9",opening.AddDays(-2),closing);
        var lateRow=TradingPnlAccounting.Compare(Point(opening,"0",[]),Point(closing,"0",[late]),TradingPnlAccounting.Version1).Positions.Single();
        Equal(lateRow.PeriodCash,"0");Equal(lateRow.PriorCashKnowledgeAdjustment,"9");
        foreach(var changed in new[]{prior with{Amount=Money("26")},prior with{EffectiveAt=prior.EffectiveAt.AddHours(1)},prior with{KnownAt=prior.KnownAt.AddHours(1)}})
            Reject("pnl_cash_revision",()=>TradingPnlAccounting.Compare(Point(opening,"0",[prior]),Point(closing,"0",[changed]),TradingPnlAccounting.Version1));
        Reject("pnl_cash_revision",()=>TradingPnlAccounting.Compare(Point(opening,"0",[corrected with{KnownAt=opening,Revision=3}]),Point(closing,"0",[corrected]),TradingPnlAccounting.Version1));
        Reject("pnl_cash_revision",()=>TradingPnlAccounting.Compare(Point(opening,"0",[prior]),Point(closing,"0",[]),TradingPnlAccounting.Version1));
        Reject("pnl_cash_revision",()=>TradingPnlAccounting.Compare(Point(opening,"0",[prior]),Point(closing,"0",[prior with{Position=key with{BookId=Guid.NewGuid()}}]),TradingPnlAccounting.Version1));
        Reject("pnl_cash_revision",()=>TradingPnlAccounting.Compare(Point(opening,"0",[prior]),Point(closing,"0",[prior with{Kind=AccountingFlowKind.NonEarningTransfer}]),TradingPnlAccounting.Version1));
        var otherCurrency=currency with{Revision=new(currency.Revision.CatalogId,Guid.NewGuid())};
        Reject("pnl_currency_transition",()=>TradingPnlAccounting.Compare(Point(opening,"1",[]),Point(closing,"1",[]) with{Positions=[new(key,ExactMoney.Parse("1",otherCurrency))]},TradingPnlAccounting.Version1));
        Reject("pnl_position",()=>TradingPnlAccounting.Compare(Point(opening,"1",[]) with{Positions=[new(key,Money("1")),new(key,Money("1"))]},Point(closing,"1",[]),TradingPnlAccounting.Version1));
        Reject("pnl_context",()=>TradingPnlAccounting.Compare(Point(opening,"1",[]) with{Flows=default},Point(closing,"1",[]),TradingPnlAccounting.Version1));
        Reject("pnl_cash_revision",()=>TradingPnlAccounting.Compare(Point(opening,"0",[]),Point(closing,"0",[paid with{Kind=(AccountingFlowKind)42}]),TradingPnlAccounting.Version1));
        var transferred=key with{BookId=Guid.NewGuid()};
        var pair=Point(closing,null,[transfer with{Position=transferred},Cash("-100",closing,closing) with{Kind=AccountingFlowKind.NonEarningTransfer}]) with{Positions=[new(transferred,Money("100"))]};
        var books=TradingPnlAccounting.Compare(Point(opening,"100",[]),pair,TradingPnlAccounting.Version1).Positions;
        if(books.Length!=2)throw new Exception("Book transfer lost an owned binding.");foreach(var row in books)Equal(row.EconomicPnl,"0");
        Equal(TradingPnlAccounting.Compare(Point(opening,"1/3",[]),Point(closing,"2/3",[]),TradingPnlAccounting.Version1).Positions.Single().EconomicPnl,"1/3");
        Console.WriteLine("PASS generic trading P&L reconciles settlement, new holdings, prior-cash revisions and exact interval boundaries.");
    }
    private static void Equal(ExactMoney actual,string expected)
    {if(actual.CompareTo(ExactMoney.Parse(expected,actual.Currency))!=0)throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
