using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public sealed record InstrumentScheduleMember(Guid SideId,BookedSideProgram Program,string NormalizationIdentity,ValuedAttribute? PriceAttribute);
public sealed record InstrumentQuantityConnection(Guid InputSide,int InputInstruction,Guid OutputSide,string Output,
    ImmutableArray<int> InputCells,ImmutableArray<int> OutputCells);
public sealed record InstrumentScheduleEvaluation(InstrumentScheduleExecution Instrument,Guid SideId,BookedSideEvaluation Value,ImmutableDictionary<Guid,BookedSideEvaluation> MemberValues);

// Connections belong to this exact instrument, never to a Side or its source adapter.
// The caller supplies retained addresses; execution does not discover or infer connections.
public sealed class InstrumentScheduleExecution
{
    public Guid InstrumentId { get; }
    public ImmutableArray<InstrumentScheduleMember> Members { get; }
    public ImmutableArray<InstrumentQuantityConnection> Connections { get; }
    private readonly ImmutableArray<Guid> order;
    private readonly ImmutableDictionary<Guid,InstrumentScheduleMember> members;
    private readonly ImmutableDictionary<(Guid Side,int Input),ImmutableArray<InstrumentQuantityConnection>> connections;

    public InstrumentScheduleExecution(Guid instrumentId,ImmutableArray<InstrumentScheduleMember> members,
        ImmutableArray<InstrumentQuantityConnection> connections)
    {
        if(instrumentId==Guid.Empty||members.IsDefaultOrEmpty||members.Any(member=>member is null||member.SideId==Guid.Empty||member.Program is null||string.IsNullOrWhiteSpace(member.NormalizationIdentity)||
            member.PriceAttribute is {} attribute&&!Enum.IsDefined(attribute))||members.Select(member=>member.SideId).Distinct().Count()!=members.Length||
            members.Select(member=>member.NormalizationIdentity).Distinct(StringComparer.Ordinal).Count()!=1||connections.IsDefault||connections.Any(connection=>connection is null))throw Invalid("Select exact instrument members and explicit quantity connections.");
        InstrumentId=instrumentId;Members=members;Connections=connections;
        this.members=members.ToImmutableDictionary(member=>member.SideId);
        this.connections=connections.GroupBy(connection=>(connection.InputSide,connection.InputInstruction)).ToImmutableDictionary(group=>group.Key,group=>group.ToImmutableArray());
        var assigned=new HashSet<(Guid Side,int Input,int Cell)>();
        foreach(var connection in connections)
        {
            if(!this.members.TryGetValue(connection.InputSide,out var target)||!this.members.TryGetValue(connection.OutputSide,out var source)||
                connection.InputInstruction<0||connection.InputInstruction>=target.Program.Document.Instructions.Length||
                connection.InputCells.IsDefaultOrEmpty||connection.OutputCells.IsDefault||connection.InputCells.Length!=connection.OutputCells.Length)
                throw Invalid("A connection requires exact member identities, stored addresses and complete cell mappings.");
            var input=target.Program.Document.Instructions[connection.InputInstruction];
            var outputs=source.Program.Document.Roots.Where(root=>root.Name==connection.Output&&root.Measure=="schedule_quantity").ToArray();
            if(input.Output.Operation!="quantity_input"||input.Bindings.Length!=1||input.Bindings[0].Kind!="sibling_quantity"||outputs.Length!=1)
                throw Invalid("Connect a declared quantity input to an explicitly named quantity output.");
            var output=source.Program.Document.Instructions[outputs[0].Row].Output;
            if(input.Output.Unit!=output.Unit||output.OutputKind!="quantity"||input.Output.Grid is not {} inputGrid||output.Grid is not {} outputGrid)
                throw Invalid("Connected columns require the same exact quantity unit and native geometry.");
            var to=target.Program.Document.Grids[inputGrid];var from=source.Program.Document.Grids[outputGrid];
            for(var n=0;n<connection.InputCells.Length;n++)
            {
                var a=connection.InputCells[n];var b=connection.OutputCells[n];
                if(a<0||a>=to.CellCount||b<0||b>=from.CellCount||to.Clock!=from.Clock||to.Interval(a)!=from.Interval(b)||
                    !assigned.Add((target.SideId,connection.InputInstruction,a)))
                    throw Invalid("Every input cell needs one exact output interval, including clock and local labels.");
            }
        }
        foreach(var member in members)foreach(var instruction in member.Program.Document.Instructions)
            if(instruction.Bindings.Any(binding=>binding.Kind=="sibling_quantity"))
                for(var cell=0;cell<member.Program.Document.Grids[instruction.Output.Grid!.Value].CellCount;cell++)
                    if(!assigned.Contains((member.SideId,instruction.Output.Ordinal,cell)))throw Invalid("Every declared quantity port must be completely connected by the instrument.");
        var dependencies=members.ToDictionary(member=>member.SideId,member=>connections.Where(connection=>connection.InputSide==member.SideId)
            .Select(connection=>connection.OutputSide).Distinct().ToHashSet());
        var consumers=connections.GroupBy(connection=>connection.OutputSide).ToDictionary(group=>group.Key,group=>group.Select(connection=>connection.InputSide).Distinct().ToArray());
        var ready=new Queue<Guid>(members.Where(member=>dependencies[member.SideId].Count==0).Select(member=>member.SideId));
        var ordered=ImmutableArray.CreateBuilder<Guid>();
        while(ready.TryDequeue(out var side))
        {
            ordered.Add(side);
            foreach(var consumer in consumers.GetValueOrDefault(side,[]))
                if(dependencies[consumer].Remove(side)&&dependencies[consumer].Count==0)ready.Enqueue(consumer);
        }
        if(ordered.Count!=members.Length)throw Invalid("Instrument quantity connections must be acyclic.");
        order=ordered.ToImmutable();
    }

    public InstrumentScheduleEvaluation Evaluate(Guid sideId,ScheduleMarketSources sources,CurveBuild scenario,
        ISideDiscounting discounting,CancellationToken cancellation=default,DeliveryCoverage? coverage=null)
    {
        cancellation.ThrowIfCancellationRequested();
        if(sources is null||scenario is null||discounting is null||!members.TryGetValue(sideId,out var member))
            throw Invalid("Select an exact member and its authorized source context.");
        var context=scenario.Graph.Plan.Context;var baseId=sources.BaseScenarioId;var scenarioId=scenario.Pricing.ScenarioId;
        // Propagate requested cells against the admitted instrument connections.
        // Shared upstream members receive the union of every consumer's demand.
        var demands=new Dictionary<Guid,ScheduleInputDemandSet>{{sideId,member.Program.Inputs(context,cancellation,coverage)}};
        var quantities=new Dictionary<Guid,Dictionary<string,ScheduleInputLanes[]>>();
        foreach(var selectedId in order.Reverse())
        {
            cancellation.ThrowIfCancellationRequested();var selected=members[selectedId];
            if(quantities.TryGetValue(selectedId,out var requests))
                demands.Add(selectedId,selected.Program.QuantityInputs(context,[..requests.Select(request=>new ScheduleQuantityDemand(request.Key,[..request.Value]))],cancellation));
            if(!demands.TryGetValue(selectedId,out var demand))continue;
            foreach(var source in demand.Sources)
            {
                if(!connections.TryGetValue((selectedId,source.Instruction),out var mappings))continue;
                foreach(var connection in mappings)
                {
                    var outputMember=members[connection.OutputSide];var root=outputMember.Program.Document.Roots.Single(root=>root.Name==connection.Output);
                    var row=outputMember.Program.Document.Instructions[root.Row].Output;
                    for(var n=0;n<connection.InputCells.Length;n++)
                    {
                        cancellation.ThrowIfCancellationRequested();var lanes=source.Uses[connection.InputCells[n]];
                        if(lanes==ScheduleInputLanes.None)continue;
                        if(!quantities.TryGetValue(connection.OutputSide,out var outputs))quantities.Add(connection.OutputSide,outputs=new(StringComparer.Ordinal));
                        if(!outputs.TryGetValue(root.Name,out var uses))outputs.Add(root.Name,uses=new ScheduleInputLanes[outputMember.Program.Document.Grids[row.Grid!.Value].CellCount]);
                        uses[connection.OutputCells[n]]|=lanes;
                    }
                }
            }
        }
        var values=new Dictionary<Guid,BookedSideEvaluation>();
        foreach(var selectedId in order)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!demands.TryGetValue(selectedId,out var demand))continue;
            var selected=members[selectedId];
            var portDemands=demand.Sources.Where(source=>connections.ContainsKey((selectedId,source.Instruction)));
            var ports=new Dictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>>();
            foreach(var source in portDemands)
            {
                var row=selected.Program.Document.Instructions[source.Instruction].Output;
                foreach(var lane in new[]{ScheduleInputLanes.Base,ScheduleInputLanes.Scenario})
                {
                    var required=baseId==scenarioId?ScheduleInputLanes.Base|ScheduleInputLanes.Scenario:lane;
                    var id=lane==ScheduleInputLanes.Base?baseId:scenarioId;var address=new ScheduleInputAddress(source.Instruction,id);
                    if(ports.ContainsKey(address)||!source.Uses.Any(use=>(use&required)!=0))continue;
                    var numbers=new ScheduleNumber[source.Uses.Length];var states=new ScheduleCellState[source.Uses.Length];
                    var presence=Enumerable.Repeat(ScheduleCellPresence.NotDemanded,source.Uses.Length).ToArray();
                    foreach(var connection in connections[(selected.SideId,source.Instruction)])
                    {
                        for(var n=0;n<connection.InputCells.Length;n++)
                        {
                            cancellation.ThrowIfCancellationRequested();var inputCell=connection.InputCells[n];
                            if((source.Uses[inputCell]&required)==0)continue;
                            var output=values[connection.OutputSide];var root=output.Roots.Single(root=>root.Name==connection.Output);
                            var column=lane==ScheduleInputLanes.Base?output.Values[root.Row].Base:output.Values[root.Row].Scenario;
                            var outputCell=connection.OutputCells[n];
                            if(column.Presence[outputCell]!=ScheduleCellPresence.Resolved)throw Invalid("A connected output did not resolve its demanded quantity.");
                            numbers[inputCell]=column.Values[outputCell];states[inputCell]=column.States[outputCell];presence[inputCell]=ScheduleCellPresence.Resolved;
                        }
                    }
                    ports.Add(address,new(context,id,new(row.OutputKind,row.Grid,row.Unit,row.Currency,row.QuoteMultiplier,[..numbers],[..states],[..presence])));
                }
            }
            values.Add(selectedId,sources.Evaluate(demand,selected.SideId,scenario,discounting,ports,cancellation,selected.PriceAttribute));
        }
        return new(this,sideId,values[sideId],values.ToImmutableDictionary());
    }

    private static MarketDataException Invalid(string message)=>new("instrument_schedule_connection",message);
}
