using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class TradingPnlAttributionChecks
{
    internal static void Run()
    {
        Reject("pnl_accounting_coverage",()=>RiskEngine.CaptureAccounting(new JointScenarioRiskChecks.Fixture((x,_)=>x).Prepared));
        var accounted=new JointScenarioRiskChecks.Fixture((_,_)=>ExactDecimal.Parse("100"),"2",accounting:true);
        Equal(RiskEngine.CaptureAccounting(accounted.Prepared).Positions.Single().RemainingValue,"200");
        Reject("pnl_actual_context",()=>RiskEngine.CaptureAccounting(RiskEngine.WithClock(accounted.Prepared,new(accounted.Prepared.Scenario.CalculationAt.AddDays(1),ClockChange.FrozenInputs))));
        var prices=0;
        var shared=new JointScenarioRiskChecks.Fixture((x,_)=>{prices++;return ExactDecimal.Parse("100")+x;},"2",accounting:true);
        var sharedRun=PnlGreekRun.Capture(shared.Prepared,RiskEngine.CalculateFiniteDifferences(shared.Prepared,JointScenarioRiskChecks.Derivatives(shared,mixed:false),
            RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings));
        var priced=prices;
        Equal(RiskEngine.CaptureAccounting(shared.Prepared,sharedRun).Positions.Single().RemainingValue,"200");
        if(prices!=priced)throw new Exception("Accounting repriced an already valued Greek base.");
        Reject("pnl_greek_context",()=>RiskEngine.CaptureAccounting(accounted.Prepared,sharedRun));
        var retired=new JointScenarioRiskChecks.Fixture((_,_)=>ExactDecimal.Parse("100"),accounting:true,ledgerOnlyCash:true);
        var retiredOpening=RiskEngine.CaptureAccounting(retired.Prepared);
        var retiredReport=TradingPnlAccounting.Compare(retiredOpening,retiredOpening with{ContextId=Guid.NewGuid(),SnapshotId=Guid.NewGuid(),
            At=retiredOpening.At.AddDays(2),KnownAt=retiredOpening.KnownAt.AddDays(2)},TradingPnlAccounting.Version1);
        var retiredRow=retiredReport.Positions.Single(p=>p.Position==retired.LedgerOnly);
        if(retiredRow.HeldAtOpening||retiredRow.HeldAtClosing)throw new Exception("A cash-only retired binding became an endpoint holding.");
        Equal(retiredRow.PeriodCash,"7");Equal(retiredRow.EconomicPnl,"7");
        Reject("pnl_pv_basis",()=>RiskEngine.CaptureAccounting(new JointScenarioRiskChecks.Fixture((x,_)=>x,measure:ValuationMeasure.ExecutionPnl,accounting:true).Prepared));
        Reject("pnl_accounting_coverage",()=>RiskEngine.CaptureAccounting(new JointScenarioRiskChecks.Fixture((x,_)=>x,accounting:true,accountingContext:Guid.NewGuid()).Prepared));
        Check((x,_)=>x*x,"3","2","0","12",RiskEngine.CentralGamma,"12");
        Check((x,y)=>x*y,"5","2","3","30",RiskEngine.CentralMixedGamma,"30");
        // This cubic's central delta fails the declared zero tolerance across the three step sizes.
        // Its value move remains in the residual, not an invented zero delta.
        var cubic=Report(new((x,_)=>x*x*x),"2","0","8");
        var delta=cubic.Positions.Single().Contributions.Single(c=>c.Formula==RiskEngine.CentralDelta&&c.Axes.Single()=="x");
        if(delta.Amount is not null||delta.UnavailableReason!="finite_difference_unstable")throw new Exception("Unstable Greeks were presented as usable values.");
        Equal(cubic.Positions.Single().ResidualValueMovement,"8");
        Reject("pnl_factor_overlap",()=>Report(new((x,_)=>x*x,"3"),"2","2","12",alias:true));
        Console.WriteLine("PASS P&L attribution reuses black-box finite differences, exact quadratic/mixed effects and explicit unstable residuals.");
    }
    private static void Check(Func<ExactDecimal,ExactDecimal,ExactDecimal> price,string quantity,string dx,string dy,string actual,string formula,string explained)
    {
        var result=Report(new(price,quantity),dx,dy,actual);var row=result.Positions.Single();
        Equal(row.Contributions.Single(c=>c.Formula==formula&&(formula==RiskEngine.CentralMixedGamma||c.Axes.Single()=="x")).Amount!,explained);
        Equal(row.ExplainedValueMovement,explained);Equal(row.ResidualValueMovement,"0");
    }
    private static PnlAttributionReport Report(JointScenarioRiskChecks.Fixture f,string dx,string dy,string actual,bool alias=false)
    {
        var context=f.Prepared.Scenario.Plan.Context;
        var request=JointScenarioRiskChecks.Derivatives(f,mixed:!alias);
        if(alias)request=request with{Axes=[request.Axes[0],request.Axes[0] with{Id="y"}]};
        var risks=RiskEngine.CalculateFiniteDifferences(f.Prepared,request,RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
        var position=risks.Repricing.Groups.Single().Positions.Single();var owner=new AccountingPositionKey(position.TradeId,position.BindingId,position.BookId);
        var captured=PnlGreekRun.Capture(f.Prepared,risks);
        var opening=captured.OpeningPoint([],PnlAccountingCoverage.Complete);
        var closing=new PnlAccountingPoint(Guid.NewGuid(),Guid.NewGuid(),context.ValuationAt.AddDays(3),context.KnownAt.AddDays(3),[new(owner,ExactMoney.Parse(actual,f.Unit.Currency))],[],PnlAccountingCoverage.Complete);
        var accounting=TradingPnlAccounting.Compare(opening,closing,TradingPnlAccounting.Version1);
        return TradingPnlAttribution.Explain(accounting,[captured],[new("x",f.Unit,ExactDecimal.Parse(dx)),new("y",f.Unit,ExactDecimal.Parse(dy))],TradingPnlAttribution.OpeningGreeks);
    }
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    private static void Equal(ExactMoney value,string expected)
    {if(value.CompareTo(ExactMoney.Parse(expected,value.Currency))!=0)throw new Exception($"Expected {expected}, got {value.ToExactString()}.");}
}
