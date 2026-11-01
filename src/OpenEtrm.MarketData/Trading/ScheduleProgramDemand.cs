using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

[Flags]
public enum ScheduleInputLanes:byte { None=0,Base=1,Scenario=2 }
public sealed record ScheduleQuantityDemand(string Output,ImmutableArray<ScheduleInputLanes> Uses);
public sealed record ScheduleInputDemand(int Instruction,ImmutableArray<ScheduleInputLanes> Uses);

// Context-dependent source selection, not another execution plan. Instruction
// addresses, grids, operands and consumers remain the published program's.
public sealed class ScheduleInputDemandSet
{
    internal BookedSideProgram Program { get; }
    public ResolvedCalculationContext Context { get; }
    public DeliveryCoverage? Coverage { get; }
    public ImmutableArray<ScheduleInputDemand> Sources { get; }
    public ImmutableArray<string> RequestedOutputs { get; }
    public ImmutableArray<ScheduleQuantityDemand> QuantityOutputs { get; }
    internal ImmutableArray<ImmutableArray<ScheduleInputLanes>> Uses { get; }
    internal ImmutableDictionary<int,ImmutableArray<bool>> Contributions { get; }
    internal ScheduleInputDemandSet(BookedSideProgram program,ResolvedCalculationContext context,ImmutableArray<ScheduleInputDemand> sources,
        DeliveryCoverage? coverage,ImmutableArray<string> outputs,ImmutableArray<ScheduleQuantityDemand> quantities,ImmutableArray<ImmutableArray<ScheduleInputLanes>> uses,ImmutableDictionary<int,ImmutableArray<bool>> contributions)
    {Program=program;Context=context;Coverage=coverage;Sources=sources;RequestedOutputs=outputs;QuantityOutputs=quantities;Uses=uses;Contributions=contributions;}
}

public sealed partial class BookedSideProgram
{
    public ScheduleInputDemandSet QuantityInputs(ResolvedCalculationContext context,ImmutableArray<ScheduleQuantityDemand> outputs,CancellationToken cancellation=default)
    {
        if(outputs.IsDefaultOrEmpty||outputs.Any(output=>output is null)||outputs.Select(output=>output.Output).Distinct(StringComparer.Ordinal).Count()!=outputs.Length)
            throw Error("Select distinct explicit named quantity outputs and cell demands.");
        return InputsCore(context,cancellation,null,outputs);
    }

    // Coverage selects whole contribution cells. Instrument admission separately
    // validates contractual term bounds and native boundaries, including TOU gaps.
    public ScheduleInputDemandSet Inputs(ResolvedCalculationContext context,CancellationToken cancellation=default,DeliveryCoverage? coverage=null)
        =>InputsCore(context,cancellation,coverage,null);

    private ScheduleInputDemandSet InputsCore(ResolvedCalculationContext context,CancellationToken cancellation,DeliveryCoverage? coverage,
        ImmutableArray<ScheduleQuantityDemand>? quantities)
    {
        if(context is null)throw Error("Select one resolved context before binding dynamic input values.");
        cancellation.ThrowIfCancellationRequested();
        var needed=Document.Instructions.Select(instruction=>
        {cancellation.ThrowIfCancellationRequested();return new ScheduleInputLanes[CellCount(instruction.Output)];}).ToArray();
        void Add(int address,int cell,ScheduleInputLanes lanes)=>needed[address][cell]|=lanes;
        var contributions=ImmutableDictionary.CreateBuilder<int,ImmutableArray<bool>>();
        var pv=Document.Roots.Single(root=>root.Name=="pv").Row;
        if(quantities is null)for(var cell=0;cell<needed[pv].Length;cell++)Add(pv,cell,ScheduleInputLanes.Base|ScheduleInputLanes.Scenario);
        else foreach(var request in quantities.Value)
        {
            var matches=Document.Roots.Where(root=>root.Name==request.Output&&root.Measure=="schedule_quantity").ToArray();
            if(matches.Length!=1||request.Uses.IsDefault||request.Uses.Length!=needed[matches[0].Row].Length||
                request.Uses.Any(use=>(use&~(ScheduleInputLanes.Base|ScheduleInputLanes.Scenario))!=0)||request.Uses.All(use=>use==ScheduleInputLanes.None))
                throw Error("A quantity demand requires an exact named output and its explicit nonempty cell lanes.");
            var root=matches[0];
            if(Document.Instructions[root.Row].Output.OutputKind!="quantity")throw Error("A quantity output must retain its declared type.");
            for(var cell=0;cell<request.Uses.Length;cell++)Add(root.Row,cell,request.Uses[cell]);
        }
        foreach(var instruction in Document.Instructions)
        {
            cancellation.ThrowIfCancellationRequested();var guard=instruction.Output;
            if(guard.Operation=="discount"&&(guard.Parameters["policy"]!="context_required_v1"||guard.ValueAt is null))
                throw Error("A discount input requires its supported explicit policy and payment date.");
            if(guard.Operation!="output_guard")continue;
            if(quantities is not null)
            {
                contributions.Add(guard.Ordinal,[..Enumerable.Repeat(false,needed[guard.Ordinal].Length)]);continue;
            }
            var selected=Enumerable.Repeat(true,needed[guard.Ordinal].Length).ToArray();
            if(coverage is not null)
            {
                if(guard.Grid is not {} gridId)
                    throw new MarketDataException("ppa_segment_coverage","Delivery coverage requires retained native contribution intervals.");
                var grid=Document.Grids[gridId];
                for(var cell=0;cell<selected.Length;cell++)
                {
                    cancellation.ThrowIfCancellationRequested();var interval=grid.Interval(cell);
                    var overlaps=interval.Start<coverage.To&&coverage.From<interval.End;
                    selected[cell]=coverage.Contains(interval.Start,interval.End);
                    if(overlaps&&!selected[cell])
                        throw new MarketDataException("ppa_segment_boundary","Coverage cannot split a retained contribution interval.");
                }
            }
            contributions.Add(guard.Ordinal,[..selected]);
            if(guard.Parameters["pricePolicy"]!="selected_context_v1")throw Error("The output price policy is unsupported.");
            if(guard.Parameters["lifecycle"]=="undue_payment_v1"&&context.ValuationAt>=guard.ValueAt)
                throw new MarketDataException("side_payment_due","A due dated payment requires its explicit lifecycle treatment.");
            if(guard.Parameters["lifecycle"]=="future_gas_delivery_v1"&&
                Document.Grids[guard.Grid!.Value].Interval(0).Start<=context.ValuationAt)
                throw new MarketDataException("gas_delivery_lifecycle","A started gas delivery requires its separately supported physical lifecycle and actuals policy.");
            if(guard.Parameters["lifecycle"]=="future_crude_delivery_v1"&&
                Document.Grids[guard.Grid!.Value].Interval(0).Start<=context.ValuationAt)
                throw new MarketDataException("crude_delivery_lifecycle","A started crude delivery requires a separately supported custody and actuals lifecycle.");
            for(var cell=0;cell<needed[guard.Ordinal].Length;cell++)
            {
                cancellation.ThrowIfCancellationRequested();
                if(!selected[cell])continue;
                var holding=guard.Parameters["lifecycle"]=="certificate_holding_v1"&&Document.Grids[guard.Grid!.Value].EndTicks(cell)<=context.ValuationAt.Ticks;
                var lane=holding||guard.ValueAt>context.ValuationAt?ScheduleInputLanes.Scenario:ScheduleInputLanes.Base;
                var amount=instruction.Inputs["amount"][0];var discounted=instruction.Inputs["discounted"][0];
                Add(amount,needed[amount].Length==1?0:cell,lane);
                if(!holding&&guard.ValueAt>context.ValuationAt)Add(discounted,needed[discounted].Length==1?0:cell,ScheduleInputLanes.Scenario);
            }
        }
        // Propagate only eligibility bits through the stored operand addresses.
        // Reductions require their full original window; scalar broadcasts OR
        // consumer uses. No rows, edges, alignment or execution order are built.
        for(var ordinal=Document.Instructions.Length-1;ordinal>=0;ordinal--)
        {
            cancellation.ThrowIfCancellationRequested();var instruction=Document.Instructions[ordinal];var row=instruction.Output;
            if(row.Operation=="output_guard")continue;
            foreach(var port in instruction.Inputs)
                foreach(var source in port.Value)
                {
                    if(row.Operation is "sum_cells" or "quantity_sum_cells")
                    {
                        var lanes=needed[ordinal].Aggregate(ScheduleInputLanes.None,(a,b)=>a|b);
                        for(var cell=0;cell<needed[source].Length;cell++){cancellation.ThrowIfCancellationRequested();Add(source,cell,lanes);}
                    }
                    else for(var cell=0;cell<needed[ordinal].Length;cell++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        Add(source,needed[source].Length==1?0:cell,needed[ordinal][cell]);
                    }
                }
        }
        var result=ImmutableArray.CreateBuilder<ScheduleInputDemand>();
        var frozen=needed.Select(cells=>cells.ToImmutableArray()).ToImmutableArray();
        foreach(var instruction in Document.Instructions)
            if(IsInput(instruction.Output.Operation)&&needed[instruction.Output.Ordinal].Any(lanes=>lanes!=ScheduleInputLanes.None))
            {
                result.Add(new(instruction.Output.Ordinal,frozen[instruction.Output.Ordinal]));
            }
        return new(this,context,result.ToImmutable(),coverage,quantities is null?["pv"]:[..quantities.Value.Select(output=>output.Output)],quantities??[],frozen,contributions.ToImmutable());
    }

    private int CellCount(ScheduleDefinitionRow row)=>row.Operation=="discount"?1:row.Grid is {} grid?Document.Grids[grid].CellCount:1;
}
