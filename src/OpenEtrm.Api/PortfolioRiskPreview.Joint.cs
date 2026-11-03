using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

public sealed partial class PortfolioRiskPreview
{
    private static object CalculateJoint(Prepared prepared,CancellationToken ct)
    {
        var request=prepared.Joint!;var before=prepared.Portfolio.Options?.TreePrices??0;
        var run=PortfolioJointCalculation.Calculate(prepared.Portfolio,request,prepared.Stress,prepared.Detail,ct);
        var markets=prepared.Portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var contracts=PortfolioValuation.ModelContracts(run.Partitions.SelectMany(p=>p.Run.Groups).SelectMany(g=>g.Contracts));
        var evidence=prepared.Portfolio.Positions.ToDictionary(p=>(p.TradeId,p.BindingId));
        string Book(Guid id)=>prepared.Books.Single(b=>b.Id("id")==id).Text("name");
        object Value(ExactScenarioValues value)=>new{ @base=value.Base.ToExactString(),value=value.Value.ToExactString(),pnl=value.Pnl.ToExactString(),loss=value.Loss.ToExactString(),
            currency=Currency(markets,value.Base.Currency),currencyReference=value.Base.Currency,
            display=new{@base=Displayed(value.Base),value=Displayed(value.Value),pnl=Displayed(value.Pnl),loss=Displayed(value.Loss)}};
        string DerivativeUnit(ReferenceKey currency,ImmutableArray<PriceUnit> units)=>$"{Currency(markets,currency)} / ({string.Join(" × ",units.Select(u=>$"({Unit(markets,u)})"))})";
        object Estimate(ExactFiniteDifferenceEstimate value,JointDerivativeDefinition definition)=>new{
            half=value.Half.ToExactString(),nominal=value.Nominal.ToExactString(),@double=value.Double.ToExactString(),value.Stable,
            currency=Currency(markets,value.Nominal.Currency),currencyReference=value.Nominal.Currency,unit=DerivativeUnit(value.Nominal.Currency,definition.Units),
            tolerance=new{absolute=definition.Tolerance.Absolute.ToString(),relative=definition.Tolerance.Relative.ToString()},
            display=new{half=Displayed(value.Half),nominal=Displayed(value.Nominal),@double=Displayed(value.Double)}};
        object Position(Guid trade,Guid binding,Guid book,string measure,object values)
        {
            var p=evidence[(trade,binding)];
            return new{tradeId=trade,bindingId=binding,bookId=book,book=Book(book),p.ValuationId,p.TradeReference,p.InstrumentReference,p.InstrumentType,
                measure,measureLabel=MeasureLabel(measure),values};
        }
        var connected=run.Partitions.Select(p=>new PortfolioConnectedScenario(p.Market.Snapshot.Definitions,p.Run.BaseScenario.ManifestId,p.Run.BaseScenario.Id,
            p.Run.BaseScenario.ParentId,p.Run.BaseScenario.Depth,p.Run.GraphWork,p.Market.Preparation)).ToImmutableArray();
        return new{detail=prepared.Detail.ToString(),tradeIds=prepared.Request.TradeIds,convention=Convention,numericalPolicy=RiskEngine.ExactNumericalPolicy,
            engineVersion=RiskJobs.EngineVersion,displayPolicy=new{implementation="risk_decimal_display_v1",decimalPlaces=12,tieRule="to_even"},
            run.Work,snapshotId=prepared.Portfolio.SnapshotId,connectedScenarios=connected,scenario=Scenario(prepared),heldInstruments=prepared.Portfolio.Positions.Select(p=>p.InstrumentId).Distinct().Count(),
            excludedPositions=prepared.Portfolio.Excluded,scenarioPolicy=prepared.Portfolio.Options is null?null:prepared.Portfolio.Policy.FuturesOptions!.ScenarioPolicy,
            optionCalibrations=prepared.Portfolio.OptionCalibrations,
            optionWork=prepared.Portfolio.OptionWork is {} work?work with{ScenarioTreePrices=prepared.Portfolio.Options!.TreePrices-before}:null,
            fallbackPositions=prepared.Portfolio.OptionModes.Values.Count(mode=>mode==OpenEtrm.MarketData.Options.ImpliedVolatility.FallbackMode),
            projectionPolicies=ProjectionPolicyReceipts.For(contracts),ppaMeasures=run.Partitions.SelectMany(p=>p.Run.Groups.Where((g,n)=>p.Market.Measures[n]==PortfolioValuation.PpaMeasure).SelectMany(g=>g.Measures)),
            inputs=new{pricingPolicy=prepared.Portfolio.Policy,ppaReceipt=prepared.Portfolio.Ppa is {} ppa?PpaPortfolio.Receipt(ppa.Receipt):null,
                markets=prepared.Portfolio.Markets.Select(m=>new{definitions=m.Snapshot.Definitions,snapshotId=m.Snapshot.Id,m.Snapshot.ValuationAt,m.Snapshot.MarketCutoff,
                    curveNumericalPolicy=CurveModel.NumericalPolicy,selectionPolicy=m.Snapshot.SelectionPolicy.ToString(),observations=m.Snapshot.Selections.Select(s=>new{s.Input,s.Observation.Key,
                        s.Observation.Series,s.Observation.Source,role=s.Observation.Role.ToString(),state=s.Observation.State.ToString(),s.Observation.ObservedAt,value=s.Observation.Value.ToString()})}),
                positions=prepared.Portfolio.Positions,discountInputs=prepared.Portfolio.DiscountInputs.AddRange(prepared.Portfolio.Ppa is {} p?PpaReads.DiscountInputs(p.Portfolio.Setup.InstrumentDiscounting):[]),
                connectedMarkets=prepared.Portfolio.ConnectedMarkets},
            modelContracts=contracts.Select(c=>new{c.Id,c.ModelVersion,c.Dependencies,c.Quantity,c.Currency,c.ScheduleIdentity,c.SettlementProjections}),
            joint=new{kind=request.Scenarios?"scenarios":"derivatives",request=request.Request,
                baseTotals=run.BaseTotals.Select(v=>new{v.Measure,measureLabel=MeasureLabel(v.Measure),v.BookId,book=Book(v.BookId),value=v.Value.ToExactString(),display=Displayed(v.Value),
                    currency=Currency(markets,v.Value.Currency),currencyReference=v.Value.Currency}),
                axes=request.Axes.Select(a=>new{a.Id,factor=Factor(markets,a.Bump.Axis),bump=a.Bump.Size.ToString()}),
                samples=run.Samples.Select(s=>new{s.Id,s.Label,
                    shifts=request.Samples.Single(x=>x.Id==s.Id).Shifts.Select(x=>new{factor=Factor(markets,x.Axis),size=x.Size.ToString()}),
                    totals=s.Totals.Select(v=>new{v.Measure,measureLabel=MeasureLabel(v.Measure),v.BookId,book=Book(v.BookId),values=Value(v.Values)}),
                    positions=s.Positions.Select(v=>Position(v.Position.TradeId,v.Position.BindingId,v.Position.BookId,v.Measure,Value(v.Position.Values)))}),
                derivatives=run.Derivatives.Select(d=>new{d.Definition.Id,d.Definition.Formula,d.Definition.Axes,factorUnits=d.Definition.Units.Select(u=>new{unit=Unit(markets,u),priceUnit=PriceUnitReference(u)}),
                    totals=d.Totals.Select(v=>new{v.Measure,measureLabel=MeasureLabel(v.Measure),v.BookId,book=Book(v.BookId),values=Estimate(v.Estimate,d.Definition)}),
                    positions=d.Positions.Select(v=>Position(v.Position.TradeId,v.Position.BindingId,v.Position.BookId,v.Measure,Estimate(v.Position.Estimate,d.Definition)))}),
                partitions=run.Partitions.Select(p=>new{definitions=p.Market.Snapshot.Definitions,p.Run.BaseScenario,p.Run.BaseLayers,p.ApplicableAxes,p.Run.Work,p.Run.GraphWork,
                    samples=p.Run.Scenarios.Select((s,n)=>new{s.Id,unchanged=p.Run.Set.Scenarios[n].Unchanged,s.Scenario,s.GraphWork})})}};
    }
}
