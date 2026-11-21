using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;

internal static class StructuredPowerHedgeChecks
{
    internal static void Run()
    {
        var definitions=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
        var references=new ReferenceRevision(definitions.CatalogId,Guid.NewGuid());
        var currency=new ReferenceKey(references,"usd");var quantity=new ReferenceKey(references,"energy");
        var unit=new PriceUnit(currency,quantity,ExactDecimal.Parse("1"));
        var curve=new CurveKey(definitions,"power");var axis=new PortfolioRiskAxis("power",definitions,
            new("market_quote",new("parallel",curve)),CurveShockCoordinate.MarketQuote,new ParallelCurveTarget(curve),unit,"Power",[],null);
        var operating=Guid.NewGuid();var hedge=Guid.NewGuid();var book=Guid.NewGuid();
        PortfolioPositionEvidence Position(Guid trade,string type,string measure)=>new(trade,1,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),book,measure,
            definitions.RevisionId,1,new(ExactDecimal.Parse("1"),quantity)){InstrumentType=type};
        var right=Position(operating,"operating_right","remaining_operating_value_v1");
        var swap=Position(hedge,"financial_swap",PortfolioValuation.SwapMeasure);
        ExactMoney Money(string n,ReferenceKey? c=null)=>ExactMoney.Parse(n,c??currency);
        ExactRiskPositionValue Value(PortfolioPositionEvidence p,string n)=>new(p.TradeId,p.BindingId,p.BookId,p.ValuationId,Money(n));
        PortfolioHoldingRisk Risk(PortfolioPositionEvidence p,string b,string u,string d,string up,string down,string delta,ReferenceKey? c=null,string gamma="0")=>
            new(p.Measure,"power",new(p.TradeId,p.BindingId,p.BookId,p.ValuationId,new(Money(b,c),Money(u,c),Money(d,c),Money(up,c),Money(down,c),
                new(Money(delta,c),new(c??currency,unit,1)),new(Money(gamma,c),new(c??currency,unit,2)))));
        var policy=PortfolioRiskPreview.AccountingPricingPolicy;
        var run=new PortfolioValuationResult(Guid.NewGuid(),new("2031-01-01",0,"2030-12-01T00:00:00Z",Guid.NewGuid()),policy,[],[right,swap],
            [new(axis,ExactDecimal.Parse("1"))],[],[],new(2,2,1,3,6,0,0))
        {Detail=RiskDetail.Holdings,BasePositions=[new(right.Measure,Value(right,"185")),new(swap.Measure,Value(swap,"0"))],
            HoldingRisk=[Risk(right,"185","190","180","5","-5","5"),Risk(swap,"0","-5","5","-5","5","-5")]};
        var report=StructuredPowerHedgeReport.Project(run,operating,[hedge],StructuredPowerHedgeReport.AggregationPolicy);
        if(report.BaseValues.Length!=2||report.BaseValues.Single(v=>v.Role=="operating").Value!="185"||
            report.PriceChanges is not [{Operating.UpPnl:"5",Hedges.UpPnl:"-5",Net.UpPnl:"0",Net.DownPnl:"0",Net.Delta:"0",Net.Gamma:"0"}])
            throw new Exception("The independently specified operating value and opposing hedge price changes were not retained exactly.");
        foreach(var (type,measure) in new[]{(FuturesOptionPositions.Type,FuturesOptionPositions.Measure),(HeatRateCallPositions.Type,HeatRateCallPositions.Measure)})
        {
            var call=Position(hedge,type,measure);
            var option=run with{Positions=[right,call],BasePositions=[run.BasePositions[0],new(measure,Value(call,"8"))],
                HoldingRisk=[run.HoldingRisk[0],Risk(call,"8","10","7","2","-1","1.5",gamma:"1")]};
            if(type==FuturesOptionPositions.Type)
            {
                var premium=Position(hedge,type,measure);
                option=option with{Positions=option.Positions.Add(premium),BasePositions=option.BasePositions.Add(new(measure,Value(premium,"-3"))),
                    HoldingRisk=option.HoldingRisk.Add(Risk(premium,"-3","-3","-3","0","0","0"))};
            }
            var comparison=StructuredPowerHedgeReport.Project(option,operating,[hedge],StructuredPowerHedgeReport.AggregationPolicy);
            if(comparison.BaseValues.Single(v=>v.Role=="hedges") is not {Measure:var observed,Value:var value}||observed!=measure||
                value!=(type==FuturesOptionPositions.Type?"5":"8")||
                comparison.PriceChanges is not [{Hedges.Gamma:"1",Net.UpPnl:"7",Net.DownPnl:"-6",Net.Delta:"6.5",Net.Gamma:"1"}])
                throw new Exception("An option hedge lost its own measure, holding values or convexity under the shared scenario.");
        }
        Reject(run with{HoldingRisk=run.HoldingRisk.RemoveAt(1)});
        Reject(run with{BasePositions=run.BasePositions.RemoveAt(1)});
        Reject(run with{Positions=[right,swap with{InstrumentType="physical_gas"}]});
        Reject(run with{HoldingRisk=run.HoldingRisk.Add(run.HoldingRisk[1])});
        Reject(run with{HoldingRisk=[run.HoldingRisk[0],Risk(swap,"1","-5","5","-5","5","-5")]});
        Reject(run, [hedge,hedge]);
        Reject(run, [operating]);
        Reject(run with{Detail=RiskDetail.Books});
        Reject(run with{Factors=[],HoldingRisk=[]});
        var otherCurrency=new ReferenceKey(new(references.CatalogId,Guid.NewGuid()),"usd");
        var other=run with{BasePositions=[run.BasePositions[0],new(swap.Measure,Value(swap,"0") with{Value=Money("0",otherCurrency)})],
            HoldingRisk=[run.HoldingRisk[0],Risk(swap,"0","-5","5","-5","5","-5",otherCurrency)]};
        if(StructuredPowerHedgeReport.Project(other,operating,[hedge],StructuredPowerHedgeReport.AggregationPolicy).PriceChanges.Length!=2)
            throw new Exception("Currencies with the same code and different exact revisions were combined.");
        Console.WriteLine("PASS power hedge report: exact opposing changes, separate measures and currencies, complete position and scenario coverage");
        void Reject(PortfolioValuationResult value,ImmutableArray<Guid>? ids=null)
        {
            try{StructuredPowerHedgeReport.Project(value,operating,ids??[hedge],StructuredPowerHedgeReport.AggregationPolicy);}
            catch(MarketDataException e)when(e.Code=="power_hedge_contract"){return;}
            throw new Exception("An incomplete or ambiguous hedge report was accepted.");
        }
    }
}
