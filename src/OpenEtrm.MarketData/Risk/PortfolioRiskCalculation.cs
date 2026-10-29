using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Risk;

public static partial class RiskEngine
{
    private sealed record PortfolioRiskMeasurement(ImmutableArray<ExactRiskBookTotal> Totals,
        ImmutableArray<ExactRiskPositionResult> Positions,int UpPricerCalls,int DownPricerCalls);

    // One execution path values declared instruments, applies external quantities
    // and measures finite differences. Coordinate adapters supply immutable
    // scenario providers and retain their own typed receipts and work evidence.
    private sealed class PortfolioRiskCalculation
    {
        private readonly PreparedValuationMarket market;
        private readonly ImmutableArray<ValuationPortfolio> portfolios;
        private readonly ImmutableArray<ImmutableArray<UnitPricingContract>> contracts;
        private readonly RiskDetail detail;
        internal PortfolioValue[] Bases{get;}
        internal int[] Calls{get;}
        internal int[] Applications{get;}
        internal IntervalWorkCount[] IntervalWork{get;}
        internal sealed class IntervalWorkCount
        {
            internal int Sides,Resolved,Reused,Calibrated,ReusedBlocks;
            internal long Rows,GraphNodes,GraphReused;
            internal void Add(IntervalValuationSession? session)
            {
                if(session is null)return;
                Sides+=session.UniqueSideCalculations;Rows+=session.RowEvaluations;
                GraphNodes+=session.GraphEvaluatedNodes;GraphReused+=session.GraphReusedReads;
                Resolved+=session.Resolver.Resolved;Reused+=session.Resolver.ReusedMonths;
                Calibrated+=session.Resolver.CalibratedBlocks;ReusedBlocks+=session.Resolver.ReusedBlocks;
            }
        }
        internal PortfolioRiskCalculation(PreparedValuationMarket market,ImmutableArray<ValuationPortfolio> portfolios,
            ImmutableArray<ImmutableArray<UnitPricingContract>> contracts,RiskDetail detail,CancellationToken cancellation)
        {
            this.market=market;this.portfolios=portfolios;this.contracts=contracts;this.detail=detail;
            Bases=portfolios.Select((portfolio,n)=>portfolio.PV(market,contracts[n],ct:cancellation,detail:detail)).ToArray();
            Calls=Bases.Select(value=>value.UnitValuations).ToArray();
            Applications=Bases.Select(value=>value.QuantityApplications).ToArray();
            IntervalWork=Bases.Select(value=>{var work=new IntervalWorkCount();work.Add(value.IntervalSession);work.Sides+=value.SideCalculations;return work;}).ToArray();
        }
        internal PortfolioRiskMeasurement[] Measure(PreparedValuationMarket upMarket,PreparedValuationMarket downMarket,
            ExactDecimal size,PriceUnit unit,CancellationToken cancellation)
        {
            var measured=new PortfolioRiskMeasurement[portfolios.Length];
            for(var group=0;group<portfolios.Length;group++)
            {
                cancellation.ThrowIfCancellationRequested();
                var portfolio=portfolios[group];var basis=Bases[group];
                var up=portfolio.PV(upMarket,contracts[group],market,basis,cancellation,detail);
                var down=portfolio.PV(downMarket,contracts[group],market,basis,cancellation,detail);
                var values=MeasurePortfolio(portfolio,basis,up,down,size,unit,detail,cancellation);
                measured[group]=new(values.Totals,values.Positions,up.UnitValuations,down.UnitValuations);
                IntervalWork[group].Add(up.IntervalSession);IntervalWork[group].Add(down.IntervalSession);
                IntervalWork[group].Sides+=up.SideCalculations+down.SideCalculations;
                Calls[group]+=up.UnitValuations+down.UnitValuations;
                Applications[group]+=up.QuantityApplications+down.QuantityApplications;
            }
            cancellation.ThrowIfCancellationRequested();
            return measured;
        }
    }
    private static ImmutableArray<ExactRiskPositionValue> BasePositions(ValuationPortfolio portfolio,PortfolioValue value,RiskDetail detail)=>
        detail==RiskDetail.Books?[]:portfolio.Positions.Select((position,n)=>new ExactRiskPositionValue(position.TradeId,position.BindingId,position.BookId,position.ValuationId,value.Positions[n])).ToImmutableArray();
    private static (ImmutableArray<ExactRiskBookTotal> Totals,ImmutableArray<ExactRiskPositionResult> Positions) MeasurePortfolio(
        ValuationPortfolio portfolio,PortfolioValue basis,PortfolioValue up,PortfolioValue down,ExactDecimal size,PriceUnit unit,RiskDetail detail,CancellationToken cancellation)
    {
        var totals=ImmutableArray.CreateBuilder<ExactRiskBookTotal>(basis.Totals.Length);
        for(var n=0;n<basis.Totals.Length;n++)
        {
            cancellation.ThrowIfCancellationRequested();var value=basis.Totals[n];
            totals.Add(new(value.BookId,ExactRepricing.Measure(value.Value,up.Totals[n].Value,down.Totals[n].Value,size,unit)));
        }
        var positions=ImmutableArray.CreateBuilder<ExactRiskPositionResult>(detail==RiskDetail.Books?0:portfolio.Positions.Length);
        for(var n=0;detail!=RiskDetail.Books&&n<portfolio.Positions.Length;n++)
        {
            cancellation.ThrowIfCancellationRequested();var position=portfolio.Positions[n];
            positions.Add(new(position.TradeId,position.BindingId,position.BookId,position.ValuationId,
                ExactRepricing.Measure(basis.Positions[n],up.Positions[n],down.Positions[n],size,unit)));
        }
        return(totals.MoveToImmutable(),positions.MoveToImmutable());
    }
}
