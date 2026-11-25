using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed partial class PortfolioRiskPreview
{
    private sealed record JointSampleReport(string Id,string Label,IEnumerable<object> Shifts,IEnumerable<object> Totals,IEnumerable<object> Positions,
        [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<PortfolioVolatilityShiftRequest> Volatility);

    private static object CalculateJoint(Prepared prepared,CancellationToken ct)
    {
        var request=prepared.Joint!;var before=prepared.Portfolio.Options?.TreePrices??0;
        var run=PortfolioJointCalculation.Calculate(prepared.Portfolio,request,prepared.Stress,prepared.Detail,ct);
        var markets=prepared.Portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var contracts=PortfolioValuation.ModelContracts(run.Partitions.SelectMany(p=>p.Run.Groups).SelectMany(g=>g.Contracts));
        var evidence=prepared.Portfolio.Positions.ToDictionary(p=>(p.Owner,p.BindingId));
        var coverage=run.Partitions.SelectMany(part=>part.Run.Groups).SelectMany(group=>group.Positions)
            .ToDictionary(position=>(position.Owner,position.BindingId),position=>position.UnassessedDays);
        string Book(Guid id)=>prepared.Books.Single(b=>b.Id("id")==id).Text("name");
        var bookCoverage=run.BaseTotals.ToDictionary(total=>(total.Measure,total.BookId,total.Value.Currency),total=>total.UnassessedDays);
        object BookTotal(string measure,Guid book,ReferenceKey currency,object values)
        {
            var count=bookCoverage[(measure,book,currency)];
            return new{measure,measureLabel=MeasureLabel(measure),bookId=book,book=Book(book),unassessedDays=count,incomplete=count!=0,values};
        }
        object Value(ExactScenarioValues value)=>new{ @base=value.Base.ToExactString(),value=value.Value.ToExactString(),pnl=value.Pnl.ToExactString(),loss=value.Loss.ToExactString(),
            currency=Currency(markets,value.Base.Currency),currencyReference=value.Base.Currency,
            display=new{@base=Displayed(value.Base),value=Displayed(value.Value),pnl=Displayed(value.Pnl),loss=Displayed(value.Loss)}};
        string DerivativeUnit(ReferenceKey currency,ImmutableArray<PriceUnit> units)=>$"{Currency(markets,currency)} / ({string.Join(" × ",units.Select(u=>$"({Unit(markets,u)})"))})";
        object Estimate(ExactFiniteDifferenceEstimate value,JointDerivativeDefinition definition)=>new{
            half=value.Half.ToExactString(),nominal=value.Nominal.ToExactString(),@double=value.Double.ToExactString(),value.Stable,
            currency=Currency(markets,value.Nominal.Currency),currencyReference=value.Nominal.Currency,unit=DerivativeUnit(value.Nominal.Currency,definition.Units),
            tolerance=new{absolute=definition.Tolerance.Absolute.ToString(),relative=definition.Tolerance.Relative.ToString()},
            display=new{half=Displayed(value.Half),nominal=Displayed(value.Nominal),@double=Displayed(value.Double)}};
        object Position(AccountingOwnerKey owner,Guid binding,Guid book,string measure,object values)
        {
            var p=evidence[(owner,binding)];
            var unknown=coverage[(owner,binding)];
            return new{unassessedDays=unknown,incomplete=unknown!=0,owner,tradeId=owner.TradeId,bindingId=binding,bookId=book,book=Book(book),p.ValuationId,p.TradeReference,p.InstrumentReference,p.InstrumentType,
                measure,measureLabel=MeasureLabel(measure),values};
        }
        object ScenarioPosition(JointPositionValue holding)
        {
            var p=holding.Position;
            var position=Position(p.Owner,p.BindingId,p.BookId,holding.Measure,Value(p.Values));
            if(p.Groups.IsEmpty&&p.SwingValues.IsEmpty)return position;
            var row=JsonSerializer.SerializeToNode(position,RiskJobs.Json)!.AsObject();
            if(!p.SwingValues.IsEmpty)row["swingRights"]=JsonSerializer.SerializeToNode(new{@base=p.BaseSwingValues.Select(SwingRightReport.FromValue),
                value=p.SwingValues.Select(SwingRightReport.FromValue)},RiskJobs.Json);
            if(!p.Groups.IsEmpty)row["groups"]=JsonSerializer.SerializeToNode(new{@base=p.BaseGroups.Select(ExerciseGroupReport.FromValue),
                value=p.Groups.Select(ExerciseGroupReport.FromValue)},RiskJobs.Json);
            return row;
        }
        JointSampleReport Sample(JointSampleValue sample)
        {
            var selected=request.Samples.Single(s=>s.Id==sample.Id);
            return new(sample.Id,sample.Label,selected.Shifts.Select(x=>new{factor=Factor(markets,x.Axis),size=x.Size.ToString()}),
                sample.Totals.Select(v=>BookTotal(v.Measure,v.BookId,v.Values.Base.Currency,Value(v.Values))),
                sample.Positions.Select(ScenarioPosition),
                selected.Volatility.IsEmpty?default:[..selected.Volatility.Select(v=>new PortfolioVolatilityShiftRequest(v.InputId,v.Size.ToString(),v.Convention))]);
        }
        var connected=run.Partitions.Select(p=>new PortfolioConnectedScenario(p.Market.Snapshot.Definitions,p.Run.BaseScenario.ManifestId,p.Run.BaseScenario.Id,
            p.Run.BaseScenario.ParentId,p.Run.BaseScenario.Depth,p.Run.GraphWork,p.Market.Preparation)).ToImmutableArray();
        return new{unassessedDays=run.BaseTotals.Sum(total=>total.UnassessedDays),incomplete=run.BaseTotals.Any(total=>total.Incomplete),detail=prepared.Detail.ToString(),tradeIds=prepared.Request.TradeIds,convention=Convention,numericalPolicy=RiskEngine.ExactNumericalPolicy,
            engineVersion=RiskJobs.EngineVersion,displayPolicy=new{implementation="risk_decimal_display_v1",decimalPlaces=12,tieRule="to_even"},
            run.Work,snapshotId=prepared.Portfolio.SnapshotId,connectedScenarios=connected,scenario=Scenario(prepared),heldInstruments=prepared.Portfolio.Positions.Select(p=>p.InstrumentId).Distinct().Count(),
            excludedPositions=prepared.Portfolio.Excluded,scenarioPolicy=prepared.Portfolio.Options is null?null:prepared.Portfolio.Policy.FuturesOptions!.ScenarioPolicy,
            optionCalibrations=prepared.Portfolio.OptionCalibrations,
            optionWork=prepared.Portfolio.OptionWork is {} work?work with{ScenarioTreePrices=prepared.Portfolio.Options!.TreePrices-before}:null,
            fallbackPositions=prepared.Portfolio.OptionModes.Values.Count(OpenEtrm.MarketData.Options.ImpliedVolatility.IsFallback),
            projectionPolicies=ProjectionPolicyReceipts.For(contracts),ppaMeasures=run.Partitions.SelectMany(p=>p.Run.Groups.Where((g,n)=>p.Market.Measures[n]==PortfolioValuation.PpaMeasure).SelectMany(g=>g.Measures)),
            inputs=new{pricingPolicy=prepared.Portfolio.Policy,ppaReceipts=prepared.Portfolio.Ppas.Select(p=>PpaPortfolio.Receipt(p.Receipt)),
                markets=prepared.Portfolio.Markets.Select(m=>new{definitions=m.Snapshot.Definitions,snapshotId=m.Snapshot.Id,m.Snapshot.ValuationAt,m.Snapshot.MarketCutoff,
                    curveNumericalPolicy=CurveModel.NumericalPolicy,selectionPolicy=m.Snapshot.SelectionPolicy.ToString(),observations=m.Snapshot.Selections.Select(s=>new{s.Input,s.Observation.Key,
                        s.Observation.Series,s.Observation.Source,role=s.Observation.Role.ToString(),state=s.Observation.State.ToString(),s.Observation.ObservedAt,value=s.Observation.Value.ToString()})}),
                positions=prepared.Portfolio.Positions,discountInputs=prepared.Portfolio.DiscountInputs.AddRange(prepared.Portfolio.Ppas.SelectMany(p=>PpaReads.DiscountInputs(p.Portfolio.Setup.InstrumentDiscounting))),
                prepared.Portfolio.InstantDiscountInputs,
                connectedMarkets=prepared.Portfolio.ConnectedMarkets},
            modelContracts=contracts.Select(c=>new{c.Id,c.ModelVersion,c.Dependencies,c.Quantity,c.Currency,c.ScheduleIdentity,c.SettlementProjections}),
            joint=new{kind=request.Scenarios?"scenarios":"derivatives",request=request.Request,
                baseTotals=run.BaseTotals.Select(v=>new{v.UnassessedDays,v.Incomplete,v.Measure,measureLabel=MeasureLabel(v.Measure),v.BookId,book=Book(v.BookId),value=v.Value.ToExactString(),display=Displayed(v.Value),
                    currency=Currency(markets,v.Value.Currency),currencyReference=v.Value.Currency}),
                axes=request.Axes.Select(a=>new{a.Id,factor=Factor(markets,a.Bump.Axis),bump=a.Bump.Size.ToString()}),
                samples=run.Samples.Select(Sample),
                derivatives=run.Derivatives.Select(d=>new{d.Definition.Id,d.Definition.Formula,d.Definition.Axes,factorUnits=d.Definition.Units.Select(u=>new{unit=Unit(markets,u),priceUnit=PriceUnitReference(u)}),
                    totals=d.Totals.Select(v=>BookTotal(v.Measure,v.BookId,v.Estimate.Nominal.Currency,Estimate(v.Estimate,d.Definition))),
                    positions=d.Positions.Select(v=>Position(v.Position.Owner,v.Position.BindingId,v.Position.BookId,v.Measure,Estimate(v.Position.Estimate,d.Definition)))}),
                partitions=run.Partitions.Select(p=>new{definitions=p.Market.Snapshot.Definitions,p.Run.BaseScenario,p.Run.BaseLayers,p.ApplicableAxes,p.Run.Work,p.Run.GraphWork,
                    samples=p.Run.Scenarios.Select((s,n)=>new{s.Id,unchanged=p.Run.Set.Scenarios[n].Unchanged,s.Scenario,s.GraphWork})})}};
    }
}
