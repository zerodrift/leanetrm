using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData.Trading;

[Flags]
public enum ScheduleInputLanes:byte { None=0,Base=1,Scenario=2 }
public sealed record ScheduleInputDemand(int Instruction,ImmutableArray<ScheduleInputLanes> Uses);

// Context-dependent source selection, not another execution plan. Instruction
// addresses, grids, operands and consumers remain the published program's.
public sealed class ScheduleInputDemandSet
{
    internal BookedSideProgram Program { get; }
    public ResolvedCalculationContext Context { get; }
    public ImmutableArray<ScheduleInputDemand> Sources { get; }
    internal ScheduleInputDemandSet(BookedSideProgram program,ResolvedCalculationContext context,ImmutableArray<ScheduleInputDemand> sources)
    {Program=program;Context=context;Sources=sources;}
}

public sealed partial class BookedSideProgram
{
    public ScheduleInputDemandSet Inputs(ResolvedCalculationContext context,CancellationToken cancellation=default)
    {
        if(context is null)throw Error("Select one resolved context before binding dynamic input values.");
        cancellation.ThrowIfCancellationRequested();
        var needed=Document.Instructions.Select(instruction=>
        {cancellation.ThrowIfCancellationRequested();return new ScheduleInputLanes[CellCount(instruction.Output)];}).ToArray();
        void Add(int address,int cell,ScheduleInputLanes lanes)=>needed[address][cell]|=lanes;
        foreach(var instruction in Document.Instructions)
        {
            cancellation.ThrowIfCancellationRequested();var guard=instruction.Output;
            if(guard.Operation=="discount"&&(guard.Parameters["policy"]!="context_required_v1"||guard.ValueAt is null))
                throw Error("A discount input requires its supported explicit policy and payment date.");
            if(guard.Operation!="output_guard")continue;
            if(guard.Parameters["pricePolicy"]!="selected_context_v1")throw Error("The output price policy is unsupported.");
            if(guard.Parameters["lifecycle"]=="undue_payment_v1"&&context.ValuationAt>=guard.ValueAt)
                throw new MarketDataException("side_payment_due","A due dated payment requires its explicit lifecycle treatment.");
            for(var cell=0;cell<needed[guard.Ordinal].Length;cell++)
            {
                cancellation.ThrowIfCancellationRequested();
                var holding=guard.Parameters["lifecycle"]=="certificate_holding_v1"&&Document.Grids[guard.Grid!.Value].Interval(cell).End<=context.ValuationAt;
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
        foreach(var instruction in Document.Instructions)
            if(IsInput(instruction.Output.Operation)&&needed[instruction.Output.Ordinal].Any(lanes=>lanes!=ScheduleInputLanes.None))
            {
                result.Add(new(instruction.Output.Ordinal,[..needed[instruction.Output.Ordinal]]));
            }
        return new(this,context,result.ToImmutable());
    }

    private int CellCount(ScheduleDefinitionRow row)=>row.Operation=="discount"?1:row.Grid is {} grid?Document.Grids[grid].CellCount:1;
}
