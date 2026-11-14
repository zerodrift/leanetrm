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
        if(delta.Amount is not null||delta.UnavailableReason!=TradingPnlAttribution.UnstableEstimate)throw new Exception("Unstable Greeks were presented as usable values.");
        Equal(cubic.Positions.Single().ResidualValueMovement,"8");
        Reject("pnl_factor_overlap",()=>Report(new((x,_)=>x*x,"3"),"2","2","12",alias:true));
        // An effect whose every estimate is unavailable has no amount; an available zero remains a zero.
        var steep=Report(new((x,_)=>x*x*x),"2","0","8",onlyX:true);
        if(TradingPnlAttribution.Effect(steep.Positions,RiskEngine.CentralDelta) is not{Amount:null,Unavailable:1})throw new Exception("An unavailable Greek effect was given an amount.");
        var flat=TradingPnlAttribution.Effect(steep.Positions,RiskEngine.CentralGamma);
        if(flat.Unavailable!=0)throw new Exception("An available estimate was counted as unavailable.");
        Equal(flat.Amount!,"0");
        // Beside an unavailable estimate the available ones still sum and the gap stays counted: on x*x*x + 2*y the
        // delta on x is unstable, while y's delta of 2 explains 6 of its move of 3.
        var uneven=Report(new((x,y)=>x*x*x+ExactDecimal.Parse("2")*y),"2","3","14");
        var partial=TradingPnlAttribution.Effect(uneven.Positions,RiskEngine.CentralDelta);
        if(partial.Unavailable!=1)throw new Exception("A partly available effect lost its unavailable count.");
        Equal(partial.Amount!,"6");Equal(uneven.Positions.Single().ResidualValueMovement,"8");
        var none=TradingPnlAttribution.Effect(steep.Positions,RiskEngine.CentralMixedGamma);
        if(none.Unavailable!=0)throw new Exception("An effect without estimates reported missing coverage.");
        Equal(none.Amount!,"0");
        Reject("pnl_position",()=>TradingPnlAttribution.Effect([],RiskEngine.CentralDelta));
        Reject("pnl_attribution_policy",()=>TradingPnlAttribution.Effect(steep.Positions,"central_third_difference_v1"));
        // A factor with no exact closing price has no movement. Its opening estimates are kept, explain nothing and say why.
        var unpriced=Report(new((x,_)=>x*x,"3"),null,"0","12",onlyX:true);var unexplained=unpriced.Positions.Single();
        if(unexplained.Contributions.Length!=2||unexplained.Contributions.Any(c=>c.Amount is not null||c.UnavailableReason!=TradingPnlAttribution.ClosingPriceUnavailable))
            throw new Exception("A factor without a closing price was given an explained amount.");
        Equal(unexplained.ResidualValueMovement,"12");
        if(TradingPnlAttribution.Effect(unpriced.Positions,RiskEngine.CentralGamma) is not{Amount:null,Unavailable:1})
            throw new Exception("An effect on a factor without a closing price was given an amount.");
        // What every opening Greek explains together follows the same rule: holdings none of whose estimates is
        // available have no explained amount, a partly available explanation keeps its amount with its gaps counted,
        // and available estimates that explain nothing are a zero.
        if(TradingPnlAttribution.Explained(unpriced.Positions) is not{Amount:null,Unavailable:2})
            throw new Exception("Holdings with no available estimate were given an explained amount.");
        var together=TradingPnlAttribution.Explained(uneven.Positions);
        if(together.Unavailable!=1)throw new Exception("A partly available explanation lost its unavailable count.");
        Equal(together.Amount!,"6");
        var whole=TradingPnlAttribution.Explained(Report(new((x,_)=>x*x,"3"),"0","0","0",onlyX:true).Positions);
        if(whole.Unavailable!=0)throw new Exception("An available explanation reported missing coverage.");
        Equal(whole.Amount!,"0");
        Reject("pnl_position",()=>TradingPnlAttribution.Explained([]));

        // A holding is explained by the price inputs its own valuation declares. Here one holding prices x*y and its
        // neighbour only z: neither lists an estimate for the other's inputs, and each reconciles exactly.
        var two=new JointScenarioRiskChecks.Fixture((x,y)=>x*y,"5",unaffected:true,accounting:true);
        var pair=two.Portfolio.Positions.Single(p=>p.ValuationId!=two.UnaffectedId);var single=two.Portfolio.Positions.Single(p=>p.ValuationId==two.UnaffectedId);
        InputKey Source(int point)=>((ConnectedSource)two.Points[point].Expression).Input;
        var reached=two.Prepared.DeclaredPriceInputs;
        if(reached.Count!=2||!reached[(pair.TradeId,pair.BindingId)].SetEquals([Source(0),Source(1)])||!reached[(single.TradeId,single.BindingId)].SetEquals([Source(2)]))
            throw new Exception("Each holding must name exactly the live price inputs its declared dependencies reach.");
        var exact=new FiniteDifferenceTolerance(default,default);var both=JointScenarioRiskChecks.Derivatives(two);
        // Cross terms are requested only where one holding prices both factors: x with y, and neither with z.
        var joint=TradingPnlAttribution.JointPairs(two.Prepared,[("x",Source(0)),("y",Source(1)),("z",Source(2))],exact);
        if(joint.Length!=1||joint[0].FirstAxis!="x"||joint[0].SecondAxis!="y"||joint[0].Tolerance!=exact)
            throw new Exception("Mixed differences must cover exactly the factor pairs one holding prices together.");
        // A pair across the two holdings is requested here anyway: it must add no estimate to either of them.
        var greeks=PnlGreekRun.Capture(two.Prepared,RiskEngine.CalculateFiniteDifferences(two.Prepared,
            both with{Axes=both.Axes.Add(new("z",two.Bump(2),exact,exact)),Mixed=both.Mixed.Add(new("xz","x","z",exact))},RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings));
        var start=RiskEngine.CaptureAccounting(two.Prepared,greeks);
        // x and y move from 0 to 2 and 3 on 5 units of x*y; z moves from 4 to 5 on 5 units of z.
        var later=start with{ContextId=Guid.NewGuid(),SnapshotId=Guid.NewGuid(),At=start.At.AddDays(3),KnownAt=start.KnownAt.AddDays(3),
            Positions=[new(new(pair.TradeId,pair.BindingId,pair.BookId),ExactMoney.Parse("30",two.Unit.Currency)),new(new(single.TradeId,single.BindingId,single.BookId),ExactMoney.Parse("25",two.Unit.Currency))]};
        var explained=TradingPnlAttribution.Explain(TradingPnlAccounting.Compare(start,later,TradingPnlAccounting.Version1),[greeks],
            [new("x",two.Unit,ExactDecimal.Parse("2")),new("y",two.Unit,ExactDecimal.Parse("3")),new("z",two.Unit,ExactDecimal.Parse("1"))],TradingPnlAttribution.OpeningGreeks);
        var crossed=explained.Positions.Single(p=>p.Accounting.Position.BindingId==pair.BindingId);var linear=explained.Positions.Single(p=>p.Accounting.Position.BindingId==single.BindingId);
        if(crossed.Contributions.Length!=5||crossed.Contributions.Any(c=>c.Axes.Contains("z"))||linear.Contributions.Length!=2||linear.Contributions.Any(c=>!c.Axes.SequenceEqual(["z"])))
            throw new Exception("A holding listed an estimate for a price input its valuation does not declare.");
        Equal(crossed.ExplainedValueMovement,"30");Equal(crossed.ResidualValueMovement,"0");Equal(linear.ExplainedValueMovement,"5");Equal(linear.ResidualValueMovement,"0");
        Equal(TradingPnlAttribution.Effect(explained.Positions,RiskEngine.CentralMixedGamma).Amount!,"30");
        // An effect can be read for one factor alone: z's delta explains 5, while x and y have none at the origin and
        // x's unstable delta on the uneven holding is unavailable by itself.
        Equal(TradingPnlAttribution.Effect(explained.Positions,RiskEngine.CentralDelta,"z").Amount!,"5");
        Equal(TradingPnlAttribution.Effect(explained.Positions,RiskEngine.CentralDelta,"x").Amount!,"0");
        Equal(TradingPnlAttribution.Effect(explained.Positions,RiskEngine.CentralMixedGamma,"y").Amount!,"30");
        Equal(TradingPnlAttribution.Effect(uneven.Positions,RiskEngine.CentralDelta,"y").Amount!,"6");
        if(TradingPnlAttribution.Effect(uneven.Positions,RiskEngine.CentralDelta,"x") is not{Amount:null,Unavailable:1})
            throw new Exception("One factor's unavailable effect was given an amount.");
        // The same market prepared for other holdings cannot stand in as these Greeks' valuation source.
        var others=RiskEngine.PrepareConnected(two.Prepared.Valuation,two.Bindings,[new(two.Portfolio.Instruments,
            [..two.Portfolio.Positions.Select(p=>p with{TradeId=Guid.NewGuid(),BindingId=Guid.NewGuid()})],two.Portfolio.Setup with{AccountingFacts=ImmutableDictionary<MeasureBinding,BindingAccountingFacts>.Empty})]);
        var misplaced=PnlGreekRun.Capture(others,greeks.Risk);
        Reject("pnl_greek_context",()=>TradingPnlAttribution.Explain(TradingPnlAccounting.Compare(misplaced.OpeningPoint([],PnlAccountingCoverage.Complete),later,TradingPnlAccounting.Version1),
            [misplaced],explained.Moves,TradingPnlAttribution.OpeningGreeks));
        // Evidence in which a holding's value moves with an input its valuation does not declare is refused, not dropped.
        var steady=TradingPnlAccounting.Compare(start,later,TradingPnlAccounting.Version1);var one=ExactMoney.Parse("1",two.Unit.Currency);
        var zDelta=greeks.Risk.Derivatives.Single(d=>d.Id=="z"&&d.Formula==RiskEngine.CentralDelta);
        var moving=zDelta with{Groups=[..zDelta.Groups.Select(g=>g with{Positions=[..g.Positions.Select(p=>p.BindingId==pair.BindingId?p with{Estimate=new(one,one,one,true)}:p)]})]};
        Reject("pnl_greek_context",()=>TradingPnlAttribution.Explain(steady,[PnlGreekRun.Capture(two.Prepared,greeks.Risk with{Derivatives=greeks.Risk.Derivatives.Replace(zDelta,moving)})],
            explained.Moves,TradingPnlAttribution.OpeningGreeks));
        // A mixed derivative must address the axes its own run selected.
        var xy=greeks.Risk.Derivatives.Single(d=>d.Id=="xy");
        var stray=greeks.Risk with{Request=greeks.Risk.Request with{Mixed=[..greeks.Risk.Request.Mixed.Select(m=>m.Id=="xy"?m with{SecondAxis="w"}:m)]},
            Derivatives=greeks.Risk.Derivatives.Replace(xy,xy with{Axes=["x","w"]})};
        Reject("pnl_greek_context",()=>TradingPnlAttribution.Explain(steady,[PnlGreekRun.Capture(two.Prepared,stray)],
            [..explained.Moves,new("w",two.Unit,ExactDecimal.Parse("1"))],TradingPnlAttribution.OpeningGreeks));
        DeclaredPriceSteps();
        FactorBumps();
        Console.WriteLine("PASS P&L attribution reuses black-box finite differences, exact quadratic/mixed effects and explicit unstable residuals.");
        Console.WriteLine("PASS P&L attribution lists each holding's own declared price inputs and never reads an unavailable Greek as zero.");
    }
    // A valuation may declare the price step of one of its inputs: the smallest move of that price its model resolves.
    // The prepared portfolio states, for each directly quoted input, the largest step its holdings declare.
    private static void DeclaredPriceSteps()
    {
        var plain=new JointScenarioRiskChecks.Fixture((x,_)=>x);
        if(!plain.Prepared.DeclaredPriceSteps.IsEmpty)throw new Exception("Holdings that declare no price step were given one.");
        var stepped=new JointScenarioRiskChecks.Fixture((x,_)=>x,priceStep:"0.25",sharedPriceStep:"0.75");
        InputKey Source(JointScenarioRiskChecks.Fixture f,int point)=>((ConnectedSource)f.Points[point].Expression).Input;
        var steps=stepped.Prepared.DeclaredPriceSteps;
        if(steps.Count!=1||steps[Source(stepped,0)]!=ExactDecimal.Parse("0.75"))throw new Exception("An input's price step must be the largest its holdings declare.");
        var smaller=new JointScenarioRiskChecks.Fixture((x,_)=>x,priceStep:"0.75",sharedPriceStep:"0.25").Prepared.DeclaredPriceSteps;
        if(smaller.Single().Value!=ExactDecimal.Parse("0.75"))throw new Exception("The largest declared price step must not depend on the order of holdings.");
        // A step belongs to an input the valuation itself declares, and is positive.
        Reject("risk_contract",()=>new JointScenarioRiskChecks.Fixture((x,_)=>x,priceStep:"0.25",priceStepInput:2));
        Reject("risk_contract",()=>new JointScenarioRiskChecks.Fixture((x,_)=>x,priceStep:"0"));
        // It states a move of one directly quoted price. A dependency built from several inputs, or from another curve's quote, has none:
        // a step declared on the mean of three quoted points is refused when the declared steps are read.
        Reject("risk_price_step",()=>_=new JointScenarioRiskChecks.Fixture((x,_)=>x,derivedPriceStep:"0.25").Prepared.DeclaredPriceSteps);
        var factors=stepped.Prepared.Factors;var onX=new DeclaredPriceStep(stepped.Requests[0],ExactDecimal.Parse("0.25"));
        var x=factors.Single(f=>f.Input==Source(stepped,0));var y=factors.Single(f=>f.Input==Source(stepped,1));
        if(PreparedConnectedPortfolioRisk.DirectInput(onX,[x])!=x.Input)throw new Exception("A step on a directly quoted input must name that input.");
        Reject("risk_price_step",()=>PreparedConnectedPortfolioRisk.DirectInput(onX,[x,y]));
        Reject("risk_price_step",()=>PreparedConnectedPortfolioRisk.DirectInput(onX,[y]));
        Reject("risk_price_step",()=>PreparedConnectedPortfolioRisk.DirectInput(onX,[]));
        // A step is declared at the base price. At another price of the same input, such as a stressed one, it is taken in
        // proportion to the price and rounded up: 0.0823 at 3.25 is 0.1076230769... at 4.25.
        static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
        if(DeclaredPriceStep.AtPrice(D("0.0823"),D("3.25"),D("4.25"))!=D("0.107623076923076924")||DeclaredPriceStep.AtPrice(D("0.75"),D("3"),D("1.5"))!=D("0.375")||
            DeclaredPriceStep.AtPrice(D("0.0823"),D("3.25"),D("3.25"))!=D("0.0823"))
            throw new Exception("A step at another price is the declared step in proportion to the price, rounded up.");
        foreach(var (basePrice,price) in new[]{("0","3"),("3","0"),("-3","3"),("3","-1")})
            Reject("risk_price_step",()=>DeclaredPriceStep.AtPrice(D("0.1"),D(basePrice),D(price)));
    }
    // The bump a report's request selects: price steps where a holding declares one, and trading ticks elsewhere or
    // everywhere.
    private static void FactorBumps()
    {
        static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
        static void Size(PnlFactorBump bump,string tick,string? step,string expected)
        {
            var size=bump.Size(D(tick),step is null?null:D(step));
            if(size!=D(expected))throw new Exception($"Expected a bump of {expected}, got {size}.");
        }
        static string Refused(PnlFactorBump bump,string tick,string step)
        {
            try{bump.Size(D(tick),D(step));}catch(MarketDataException e)when(e.Code=="pnl_factor_bump"){return e.Message;}
            throw new Exception("A tick bump below two declared price steps must be refused.");
        }
        var steps=new PnlFactorBump(1,2);
        // Two declared steps of 0.0629 are 0.1258, which is 126 ticks of 0.001 once rounded up to whole ticks. The bump is
        // rounded, not each step: two steps of 2.3 ticks are 5 ticks, not 6.
        Size(steps,"0.001","0.0629","0.126");Size(steps,"0.001","0.063","0.126");Size(new(1,4),"0.01","1.6501","6.61");
        Size(steps,"0.001","0.0023","0.005");
        // Where a step is declared the steps decide, whatever the ticks; a factor without one is bumped by the ticks.
        Size(new(500,2),"0.001","0.0629","0.126");Size(steps,"0.001",null,"0.001");Size(new(5,2),"0.01",null,"0.05");
        // Ticks alone bump every factor alike, but never by less than two declared price steps: the half step would lie
        // inside one step, where a second difference reads the model's grid, not its curvature. The refusal says how many
        // ticks two steps span, and advises ticks only where that many can be selected.
        var ticks=new PnlFactorBump(330,null);
        Size(ticks,"0.01",null,"3.3");Size(ticks,"0.01","1.65","3.3");Size(new(126,null),"0.001","0.0629","0.126");
        foreach(var small in new[]{1,63,125})
            if(Refused(new(small,null),"0.001","0.0629") is var message&&!(message.Contains("span 126 ticks")&&message.Contains("or at least 126 ticks")))
                throw new Exception("The refusal must name the ticks two declared price steps span: "+message);
        if(Refused(new(1000,null),"0.001","1.5") is var beyond&&(!beyond.Contains("span 3000 ticks")||beyond.Contains("at least")))
            throw new Exception("The refusal must not advise more ticks than can be selected: "+beyond);
        // A selection is explicit and bounded, and its half step is exact. Price steps are two or four: an odd count puts
        // the half step between two bends, and more than four put the double step more than eight steps out.
        foreach(var invalid in new PnlFactorBump[]{new(0,2),new(1001,null),new(1,1),new(1,3),new(1,5),new(1,6)})Reject("pnl_factor_bump",()=>invalid.Size(D("0.001"),null));
        Reject("pnl_factor_bump",()=>steps.Size(D("0"),null));
        Reject("pnl_factor_bump",()=>steps.Size(D("0.001"),D("0")));
        Reject("pnl_factor_bump",()=>steps.Size(D("0.000000000000000001"),null));
    }
    private static void Check(Func<ExactDecimal,ExactDecimal,ExactDecimal> price,string quantity,string dx,string dy,string actual,string formula,string explained)
    {
        var result=Report(new(price,quantity),dx,dy,actual);var row=result.Positions.Single();
        Equal(row.Contributions.Single(c=>c.Formula==formula&&(formula==RiskEngine.CentralMixedGamma||c.Axes.Single()=="x")).Amount!,explained);
        Equal(row.ExplainedValueMovement,explained);Equal(row.ResidualValueMovement,"0");
    }
    private static PnlAttributionReport Report(JointScenarioRiskChecks.Fixture f,string? dx,string dy,string actual,bool alias=false,bool onlyX=false)
    {
        var context=f.Prepared.Scenario.Plan.Context;
        var request=JointScenarioRiskChecks.Derivatives(f,mixed:!alias&&!onlyX);
        if(alias)request=request with{Axes=[request.Axes[0],request.Axes[0] with{Id="y"}]};
        if(onlyX)request=request with{Axes=[request.Axes[0]]};
        var risks=RiskEngine.CalculateFiniteDifferences(f.Prepared,request,RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
        var position=risks.Repricing.Groups.Single().Positions.Single();var owner=new AccountingPositionKey(position.TradeId,position.BindingId,position.BookId);
        var captured=PnlGreekRun.Capture(f.Prepared,risks);
        var opening=captured.OpeningPoint([],PnlAccountingCoverage.Complete);
        var closing=new PnlAccountingPoint(Guid.NewGuid(),Guid.NewGuid(),context.ValuationAt.AddDays(3),context.KnownAt.AddDays(3),[new(owner,ExactMoney.Parse(actual,f.Unit.Currency))],[],PnlAccountingCoverage.Complete);
        var accounting=TradingPnlAccounting.Compare(opening,closing,TradingPnlAccounting.Version1);
        PnlFactorMovement[] moves=[new("x",f.Unit,dx is null?null:ExactDecimal.Parse(dx)),new("y",f.Unit,ExactDecimal.Parse(dy))];
        return TradingPnlAttribution.Explain(accounting,[captured],[..moves.Take(onlyX?1:2)],TradingPnlAttribution.OpeningGreeks);
    }
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    private static void Equal(ExactMoney value,string expected)
    {if(value.CompareTo(ExactMoney.Parse(expected,value.Currency))!=0)throw new Exception($"Expected {expected}, got {value.ToExactString()}.");}
}
