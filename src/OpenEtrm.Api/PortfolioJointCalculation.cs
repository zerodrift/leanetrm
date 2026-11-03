using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

internal sealed record JointBookValue(string Measure,Guid BookId,ExactScenarioValues Values);
internal sealed record JointPositionValue(string Measure,ScenarioPositionValue Position);
internal sealed record JointSampleValue(string Id,string Label,ImmutableArray<JointBookValue> Totals,ImmutableArray<JointPositionValue> Positions);
internal sealed record JointDerivativeBook(string Measure,Guid BookId,ExactFiniteDifferenceEstimate Estimate);
internal sealed record JointDerivativePosition(string Measure,DerivativePositionValue Position);
internal sealed record JointDerivativeDefinition(string Id,string Formula,ImmutableArray<string> Axes,ImmutableArray<PriceUnit> Units,FiniteDifferenceTolerance Tolerance);
internal sealed record JointDerivativeValue(JointDerivativeDefinition Definition,ImmutableArray<JointDerivativeBook> Totals,ImmutableArray<JointDerivativePosition> Positions);
internal sealed record JointPartition(PortfolioValuation.MarketPartition Market,ConnectedScenarioRiskRun Run,ImmutableArray<ConnectedDerivativeResult> Derivatives,ImmutableArray<string> ApplicableAxes);
internal sealed record PortfolioJointRun(ImmutableArray<JointPartition> Partitions,ImmutableArray<PortfolioTotal> BaseTotals,
    ImmutableArray<JointSampleValue> Samples,ImmutableArray<JointDerivativeValue> Derivatives,RiskWork Work);

internal static class PortfolioJointCalculation
{
    internal const long MaximumOptionTreeNodes=100_000_000;
    internal static void RequireWork(PortfolioValuation.Prepared portfolio,PortfolioJointRisk.Resolved joint,RiskDetail detail)
    {
        var width=portfolio.Markets.Sum(m=>RiskEngine.ScenarioResultWidth(m.Connected,detail));
        var rowScenarios=portfolio.Ppa is {} ppa?checked(ppa.Rows*(1L+joint.ChildCount)):0;
        // Admission counts nonterminal rollback nodes; terminal rows are linear in the selected step count.
        var treeNodes=portfolio.Options?.Bindings.Values.Where(b=>b.Receipt.Mode==OpenEtrm.MarketData.Trading.FuturesOptionPricing.SolvedMode)
            .Sum(b=>checked((long)b.Policy.Steps*(b.Policy.Steps+1)/2*(1L+joint.ChildCount)))??0;
        RiskValuationAdmission.Require(rowScenarios,treeNodes,checked(width*(1L+joint.ChildCount)),joint.Factors.DistinctBy(a=>a.Id).Count(),1);
    }
    internal static PortfolioJointRun Calculate(PortfolioValuation.Prepared portfolio,PortfolioJointRisk.Resolved joint,
        ImmutableArray<PortfolioCurveShift> stress,RiskDetail detail,CancellationToken ct)
    {
        RequireWork(portfolio,joint,detail);ct.ThrowIfCancellationRequested();
        var partitions=ImmutableArray.CreateBuilder<JointPartition>();
        foreach(var market in portfolio.Markets)
        {
            var parent=market.Connected;
            foreach(var layer in stress.Where(s=>s.Axis.Market==market.Snapshot.Definitions))
                parent=RiskEngine.WithShifts(parent,[new(layer.Axis.Coordinate,layer.Axis.Target,layer.Axis.Unit,layer.Size,CurveShift.ParentAdditive)],ct);
            if(joint.Scenarios)
            {
                var localSamples=joint.Samples.Select(s=>
                {
                    var shifts=s.Shifts.Where(x=>x.Axis.Market==market.Snapshot.Definitions).Select(x=>
                        new CurveShift(x.Axis.Coordinate,x.Axis.Target,x.Axis.Unit,x.Size,CurveShift.ParentAdditive)).ToImmutableArray();
                    return new ConnectedRiskScenario(s.Id,s.Label,shifts){Unchanged=shifts.IsEmpty};
                }).ToImmutableArray();
                var set=joint.Request.Scenarios!;
                var run=RiskEngine.CalculateScenarios(parent,new(set.Id,set.Version,set.Convention,localSamples),portfolio.Policy.RiskNumericalPolicy,ct,detail);
                partitions.Add(new(market,run,[],[]));
            }
            else
            {
                var axes=joint.Axes.Where(a=>a.Bump.Axis.Market==market.Snapshot.Definitions).ToImmutableArray();
                var ids=axes.Select(a=>a.Id).ToHashSet(StringComparer.Ordinal);
                if(axes.IsEmpty)
                {
                    var run=RiskEngine.CalculateScenarios(parent,new("unaffected_partition","1",ConnectedRiskScenarioSet.Independent,
                        [new("unchanged","No selected axis in this market",[]){Unchanged=true}]),portfolio.Policy.RiskNumericalPolicy,ct,detail);
                    partitions.Add(new(market,run,[],[]));
                }
                else
                {
                    var request=new ConnectedFiniteDifferenceRequest(joint.Request.Derivatives!.Convention,
                        [..axes.Select(a=>new ConnectedSensitivityAxis(a.Id,new(a.Bump.Axis.Coordinate,a.Bump.Axis.Target,a.Bump.Axis.Unit,a.Bump.Size,ConnectedRiskBump.Convention),a.DeltaTolerance,a.GammaTolerance))],
                        [..joint.Mixed.Where(p=>ids.Contains(p.FirstAxis)&&ids.Contains(p.SecondAxis))]);
                    var run=RiskEngine.CalculateFiniteDifferences(parent,request,portfolio.Policy.RiskNumericalPolicy,ct,detail);
                    partitions.Add(new(market,run.Repricing,run.Derivatives,[..axes.Select(a=>a.Id)]));
                }
            }
        }
        var parts=partitions.ToImmutable();
        var bases=parts.SelectMany(p=>p.Run.Groups.SelectMany((g,n)=>g.Values.Select(v=>new PortfolioTotal(p.Market.Measures[n],v.BookId,v.Value))))
            .GroupBy(v=>(v.Measure,v.BookId,v.Value.Currency)).Select(g=>new PortfolioTotal(g.Key.Measure,g.Key.BookId,g.Select(v=>v.Value).Aggregate((a,b)=>a.Add(b)))).ToImmutableArray();
        var samples=joint.Samples.Select((sample,index)=>
        {
            var totals=parts.SelectMany(p=>p.Run.Scenarios[index].Groups.SelectMany(g=>g.Totals.Select(v=>new JointBookValue(p.Market.Measures[g.Group],v.BookId,v.Values))))
                .GroupBy(v=>(v.Measure,v.BookId,v.Values.Base.Currency)).Select(g=>new JointBookValue(g.Key.Measure,g.Key.BookId,g.Select(v=>v.Values).Aggregate(Add))).ToImmutableArray();
            var positions=parts.SelectMany(p=>p.Run.Scenarios[index].Groups.SelectMany(g=>g.Positions.Select(v=>new JointPositionValue(p.Market.Measures[g.Group],v)))).ToImmutableArray();
            return new JointSampleValue(sample.Id,sample.Label,totals,positions);
        }).ToImmutableArray();
        var definitions=ImmutableArray.CreateBuilder<JointDerivativeDefinition>();
        foreach(var axis in joint.Axes)
        {
            definitions.Add(new(axis.Id,RiskEngine.CentralDelta,[axis.Id],[axis.Bump.Axis.Unit],axis.DeltaTolerance));
            definitions.Add(new(axis.Id,RiskEngine.CentralGamma,[axis.Id],[axis.Bump.Axis.Unit,axis.Bump.Axis.Unit],axis.GammaTolerance));
        }
        var byId=joint.Axes.ToDictionary(a=>a.Id,StringComparer.Ordinal);
        definitions.AddRange(joint.Mixed.Select(p=>new JointDerivativeDefinition(p.Id,RiskEngine.CentralMixedGamma,[p.FirstAxis,p.SecondAxis],
            [byId[p.FirstAxis].Bump.Axis.Unit,byId[p.SecondAxis].Bump.Axis.Unit],p.Tolerance)));
        var derivatives=definitions.Select(definition=>
        {
            var books=new List<JointDerivativeBook>();var positions=ImmutableArray.CreateBuilder<JointDerivativePosition>();
            foreach(var part in parts)
            {
                var local=part.Derivatives.SingleOrDefault(d=>d.Id==definition.Id&&d.Formula==definition.Formula);
                foreach(var (group,index) in part.Run.Groups.Select((g,n)=>(g,n)))
                {
                    var values=local?.Groups.Single(g=>g.Group==index);
                    foreach(var book in group.Values)
                        books.Add(new(part.Market.Measures[index],book.BookId,values?.Totals.Single(v=>v.BookId==book.BookId&&v.Estimate.Nominal.Currency==book.Value.Currency).Estimate??Zero(book.Value)));
                    foreach(var position in group.Positions)
                    {
                        var value=values?.Positions.Single(v=>v.TradeId==position.TradeId&&v.BindingId==position.BindingId).Estimate??Zero(position.Value);
                        positions.Add(new(part.Market.Measures[index],new(position.TradeId,position.BindingId,position.BookId,position.ValuationId,value)));
                    }
                }
            }
            var totals=books.GroupBy(v=>(v.Measure,v.BookId,v.Estimate.Nominal.Currency))
                .Select(g=>new JointDerivativeBook(g.Key.Measure,g.Key.BookId,PortfolioJointRisk.SumEstimates(g.Select(v=>v.Estimate),definition.Tolerance))).ToImmutableArray();
            return new JointDerivativeValue(definition,totals,positions.ToImmutable());
        }).ToImmutableArray();
        ct.ThrowIfCancellationRequested();
        return new(parts,bases,samples,derivatives,PortfolioValuation.CombineWork(portfolio,parts.Select(p=>PortfolioValuation.Work(p.Run.Work,p.Run.GraphWork)),1+joint.ChildCount));
    }
    private static ExactScenarioValues Add(ExactScenarioValues a,ExactScenarioValues b)=>new(a.Base.Add(b.Base),a.Value.Add(b.Value),a.Pnl.Add(b.Pnl),a.Loss.Add(b.Loss));
    private static ExactFiniteDifferenceEstimate Zero(ExactMoney basis)
    {var zero=basis.Subtract(basis);return new(zero,zero,zero,true);}
}
