using OpenEtrm.MarketData.CustomCurves;
using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.MarketData.Trading;

// An authorized operation supplies these immutable reads. No Side definition,
// schedule generator or replacement execution graph enters source resolution.
public sealed partial class ScheduleMarketSources
{
    private readonly ResolvedCalculationContext context;
    private readonly CurveBuild basis;
    private readonly IntervalReferenceData? reference;
    private readonly IntervalReadData? read;
    private readonly PendingActualsPolicy pending;
    private readonly ImmutableArray<IndexPriceLookup> dated;
    private readonly ImmutableArray<PriceSettlementSelection> selections;
    private readonly VolumeContext? volumeContext;
    private readonly ImmutableArray<GasValuationAssignment> gasAssignments=[];

    public ScheduleMarketSources(ResolvedCalculationContext context,CurveBuild basis,
        IntervalReferenceData? reference,IntervalReadData? read,IntervalKnowledgePolicy knowledgePolicy,
        PendingActualsPolicy pending,ImmutableArray<IndexPriceLookup> dated,ImmutableArray<PriceSettlementSelection> selections,
        ImmutableArray<GasValuationAssignment> gasAssignments)
        :this(context,basis,reference,read,knowledgePolicy,pending,dated,selections)
    {
        if(gasAssignments.IsDefaultOrEmpty||gasAssignments.Any(a=>a is null)||
            gasAssignments.Select(a=>(a.Key,a.From,a.To)).Distinct().Count()!=gasAssignments.Length)
            throw Invalid("Gas sources require distinct qualified assignment definitions.");
        this.gasAssignments=gasAssignments;
    }

    public ScheduleMarketSources(ResolvedCalculationContext context,CurveBuild basis,
        IntervalReferenceData? reference,IntervalReadData? read,IntervalKnowledgePolicy knowledgePolicy,
        PendingActualsPolicy pending,ImmutableArray<IndexPriceLookup> dated,ImmutableArray<PriceSettlementSelection> selections)
    {
        if(context is null||basis is null||basis.Scenario.Depth!=0||!Enum.IsDefined(pending)||dated.IsDefault||selections.IsDefault||
            dated.Any(value=>value is null)||selections.Any(value=>value is null)||
            knowledgePolicy is not(IntervalKnowledgePolicy.ResolvedCutoff or IntervalKnowledgePolicy.ValuationInstant)||
            knowledgePolicy==IntervalKnowledgePolicy.ValuationInstant&&context.KnownAt!=context.ValuationAt||
            basis.Snapshot.Knowledge is not {} knowledge||knowledge.ContextId!=context.ContextId||knowledge.KnownAt!=context.KnownAt||
            basis.Graph.Plan.Context!=context||basis.Snapshot.ValuationAt!=context.ValuationAt||basis.Snapshot.MarketCutoff!=context.MarketAsOf||
            (reference is null)!=(read is null))
            throw Invalid("Source reads require one certified context, an unshocked base and explicit source policies.");
        if(reference is not null&&(reference.Definitions!=basis.Definitions||
            reference.Assets.Any(pair=>pair.Key!=pair.Value.Key)||reference.Sources.Any(pair=>pair.Key!=pair.Value.Key)||
            reference.Quantities.Any(pair=>pair.Key!=pair.Value.Key)||read!.Series.Any(pair=>pair.Key!=pair.Value.Id)||read.Curves.Any(pair=>pair.Key!=pair.Value.Key||
            pair.Key.Revision!=reference.Definitions||pair.Value.ValuationAt!=context.ValuationAt||pair.Value.KnownAt!=context.KnownAt)))
            throw Invalid("Native source definitions and prepared curves must retain the exact context coordinates.");
        this.context=context;this.basis=basis;this.reference=reference;this.read=read;this.pending=pending;
        this.dated=dated;this.selections=selections;
        if(reference is not null)volumeContext=new(reference.Definitions,reference.Reference,context.ValuationAt,context.KnownAt,pending);
    }

    public BookedSideEvaluation Evaluate(BookedSideProgram program,Guid sideId,CurveBuild scenario,
        ISideDiscounting discounting,CancellationToken cancellation=default,ValuedAttribute? nativePriceAttribute=null)
    {
        if(program is null||sideId==Guid.Empty||scenario is null||discounting is null||
            scenario.Graph.Plan.Context!=context||!ReferenceEquals(scenario.Model,basis.Model)||!scenario.Snapshot.HasSameInputs(basis.Snapshot))
            throw Invalid("Execution requires the same selected model, snapshot and explicit discount provider.");
        var demand=program.Inputs(context,cancellation);
        var baseId=basis.Pricing.ScenarioId;var scenarioId=scenario.Pricing.ScenarioId;
        var inputs=new Dictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>>();
        var needsNativePrices=demand.Sources.Any(source=>program.Document.Instructions[source.Instruction].Output is{Operation:"price_lookup",Grid:not null});
        var baseResolver=!needsNativePrices||read is null?null:new IntervalPriceResolver(basis,read.Curves);
        var scenarioResolver=ReferenceEquals(scenario,basis)?baseResolver:!needsNativePrices||read is null?null:new IntervalPriceResolver(scenario,read.Curves,baseResolver);
        var currency=program.Document.Instructions[program.Document.Roots.Single(root=>root.Name=="pv").Row].Output.Currency!.Value;
        SidePaymentKey Key(ScheduleDefinitionRow row)=>new(sideId,row.PaymentOrdinal!.Value,row.ValueAt!.Value,currency);
        var keys=demand.Sources.Select(source=>program.Document.Instructions[source.Instruction].Output)
            .Where(row=>row.Operation=="discount").Select(Key).Distinct().ToImmutableArray();
        var factors=keys.IsEmpty?ImmutableArray<SidePaymentFactor>.Empty:discounting.Resolve(keys,basis.Snapshot);
        if(factors.IsDefault||factors.Length!=keys.Length||factors.Any(factor=>factor is null||!keys.Contains(factor.Payment)||
            !factor.Snapshot.HasSameInputs(basis.Snapshot)||!Enum.IsDefined(factor.Source)||factor.DiscountFactor.CompareTo(default)<=0||
            factor.Source==DiscountFactorSource.Undiscounted&&factor.DiscountFactor!=ExactDecimal.Parse("1"))||
            factors.Select(factor=>factor.Payment).Distinct().Count()!=factors.Length)
            throw new MarketDataException("side_payment_factors","Every requested value date requires exactly one positive factor under the selected context.");
        foreach(var source in demand.Sources)
        {
            cancellation.ThrowIfCancellationRequested();
            var instruction=program.Document.Instructions[source.Instruction];
            foreach(var lane in new[]{ScheduleInputLanes.Base,ScheduleInputLanes.Scenario})
            {
                var selected=baseId==scenarioId?ScheduleInputLanes.Base|ScheduleInputLanes.Scenario:lane;
                if(!source.Uses.Any(use=>(use&selected)!=0))continue;
                var id=lane==ScheduleInputLanes.Base?baseId:scenarioId;
                var address=new ScheduleInputAddress(source.Instruction,id);
                if(inputs.ContainsKey(address))continue;
                var market=lane==ScheduleInputLanes.Base?basis:scenario;
                var resolver=lane==ScheduleInputLanes.Base?baseResolver:scenarioResolver;
                var row=instruction.Output;var count=source.Uses.Length;
                var numbers=new ScheduleNumber[count];var states=new ScheduleCellState[count];
                var presence=Enumerable.Repeat(ScheduleCellPresence.NotDemanded,count).ToArray();
                var cells=Enumerable.Range(0,count).Where(cell=>(source.Uses[cell]&selected)!=0).ToArray();
                var grid=row.Grid is {} ordinal?program.Document.Grids[ordinal]:null;
                if(row.Operation=="quantity_input")
                {
                    var values=Volumes(instruction,grid,cells);
                    for(var n=0;n<cells.Length;n++)Set(cells[n],new(values[n].Energy.NumeratorValue,values[n].Energy.DenominatorValue),new(values[n].State,SchedulePriceFacts.None));
                }
                else foreach(var cell in cells)
                {
                    cancellation.ThrowIfCancellationRequested();
                    switch(row.Operation)
                    {
                        case "discount":
                            var factor=factors.Single(value=>value.Payment==Key(row));
                            if(factor.Source==DiscountFactorSource.CurrencyDefault)
                            {
                                if(factor.RateDefinition is null)throw new MarketDataException("side_payment_factors","Currency discounting requires its exact rate evidence.");
                                factor.RateDefinition.Require(factor);
                            }
                            Set(cell,ScheduleNumber.Parse(factor.DiscountFactor.ToString()),default);break;
                        case "asset_capacity":
                            Set(cell,Capacity(instruction,grid,cell),new(VolumeState.Contractual,SchedulePriceFacts.None));break;
                        case "price_lookup":
                            var price=grid is null?Dated(instruction,market):Native(instruction,grid,cell,program.Document.Family,resolver!,nativePriceAttribute);
                            Set(cell,new(price.Price.NumeratorValue,price.Price.DenominatorValue),new(null,price.State));break;
                        default:throw Invalid("The program requested an unsupported dynamic source.");
                    }
                }
                inputs.Add(address,new(context,id,new(row.OutputKind,row.Grid,row.Unit,row.Currency,row.QuoteMultiplier,
                    [..numbers],[..states],[..presence])));
                void Set(int cell,ScheduleNumber number,ScheduleCellState state)
                {numbers[cell]=number;states[cell]=state;presence[cell]=ScheduleCellPresence.Resolved;}
            }
        }
        return program.Execute(demand,baseId,scenarioId,inputs,cancellation);
    }
    private void RequireNative(ScheduleDefinitionGrid? grid)
    {
        if(reference is null||read is null||grid is null||grid.Clock.Revision!=reference.Reference||!reference.Clocks.ContainsKey(grid.Clock))
            throw Invalid("Native inputs require the exact retained grid and reference read.");
    }
    private static void RequireUnit(ExactUnitPrice price,ScheduleDefinitionRow row)
    {
        if(price.Unit.Quantity!=row.Unit||price.Unit.Currency!=row.Currency||price.Unit.Multiplier.ToString()!=row.QuoteMultiplier)
            throw Invalid("A price input must retain its exact stored unit and quote multiplier.");
    }
    private static MarketDataException Invalid(string message)=>new("side_market_sources",message);
}
