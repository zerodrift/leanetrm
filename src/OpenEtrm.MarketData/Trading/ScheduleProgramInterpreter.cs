using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Trading;

public sealed partial class BookedSideProgram
{
    public BookedSideEvaluation Execute(ScheduleInputDemandSet demand,string baseScenarioId,string scenarioId,
        IReadOnlyDictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>> inputs,CancellationToken cancellation=default)
    {
        if(demand is null||!ReferenceEquals(demand.Program,this)||string.IsNullOrWhiteSpace(baseScenarioId)||string.IsNullOrWhiteSpace(scenarioId)||inputs is null)
            throw Error("Supply this program's context-owned input selection and both immutable scenario identities.");
        cancellation.ThrowIfCancellationRequested();var context=demand.Context;
        var supplied=inputs.ToDictionary(pair=>pair.Key,pair=>pair.Value);
        var expected=new Dictionary<ScheduleInputAddress,ScheduleInputLanes>();
        foreach(var source in demand.Sources)
            foreach(var lane in new[]{ScheduleInputLanes.Base,ScheduleInputLanes.Scenario})
                if(source.Uses.Any(use=>(use&lane)!=0))
                {
                    var address=new ScheduleInputAddress(source.Instruction,lane==ScheduleInputLanes.Base?baseScenarioId:scenarioId);
                    expected[address]=expected.GetValueOrDefault(address)|lane;
                }
        if(!expected.Keys.ToHashSet().SetEquals(supplied.Keys))throw Error("Supply exactly the selected source/scenario columns; missing inputs cannot become zero.");
        var masks=demand.Sources.ToDictionary(source=>source.Instruction,source=>source.Uses);
        foreach(var pair in expected)
            {
                cancellation.ThrowIfCancellationRequested();var input=supplied[pair.Key];var row=Document.Instructions[pair.Key.Instruction].Output;
                if(input is null||input.Context!=context||input.ScenarioId!=pair.Key.ScenarioId||input.Value is null)
                    throw Error("A source column belongs to another resolved context or immutable scenario.");
                input.Value.Require(row,CellCount(row),masks[pair.Key.Instruction],pair.Value,cancellation);
            }
        var values=new ScheduleLaneColumns[Document.Instructions.Length];
        var missing=new Dictionary<int,(ImmutableArray<ScheduleNumber> Numbers,ImmutableArray<ScheduleCellState> States,ImmutableArray<ScheduleCellPresence> Presence)>();
        var contributions=ImmutableArray.CreateBuilder<ScheduleContribution>();
        var pvRoot=Document.Roots.Single(root=>root.Name=="pv").Row;
        foreach(var instruction in Document.Instructions)
        {
            cancellation.ThrowIfCancellationRequested();var row=instruction.Output;var count=CellCount(row);
            if(row.Operation=="quantity_transform"&&row.Parameters["implementation"] is not("whole_operating_day_entitlement_v1" or "certificate_per_energy_v1"))
                throw Error("The quantity transform implementation is unsupported.");
            ScheduleValueColumn Column(ImmutableArray<ScheduleNumber> numbers,ImmutableArray<ScheduleCellState> states,ImmutableArray<ScheduleCellPresence> presence)=>
                new(row.OutputKind,row.Grid,row.Unit,row.Currency,row.QuoteMultiplier,numbers,states,presence);
            ScheduleValueColumn Missing()
            {
                if(!missing.TryGetValue(count,out var cells))
                {
                    cells=([..Enumerable.Repeat(default(ScheduleNumber),count)],[..Enumerable.Repeat(default(ScheduleCellState),count)],
                        [..Enumerable.Repeat(ScheduleCellPresence.NotDemanded,count)]);missing.Add(count,cells);
                }
                return Column(cells.Numbers,cells.States,cells.Presence);
            }
            ScheduleValueColumn Arg(string port,ScheduleInputLanes lane)
            {
                var pair=values[instruction.Inputs[port][0]];return lane==ScheduleInputLanes.Base?pair.Base:pair.Scenario;
            }
            if(IsInput(row.Operation))
            {
                ScheduleValueColumn? absent=null;
                ScheduleValueColumn Get(string id)=>supplied.TryGetValue(new(row.Ordinal,id),out var source)?source.Value:absent??=Missing();
                values[row.Ordinal]=new(Get(baseScenarioId),Get(scenarioId));continue;
            }
            if(row.Operation=="output_guard")
            {
                var cash=ImmutableArray.CreateBuilder<ScheduleNumber>(count);var pv=ImmutableArray.CreateBuilder<ScheduleNumber>(count);
                var states=ImmutableArray.CreateBuilder<ScheduleCellState>(count);var presence=ImmutableArray.CreateBuilder<ScheduleCellPresence>(count);
                var classifications=ImmutableArray.CreateBuilder<ScheduleContributionState>(count);
                for(var cell=0;cell<count;cell++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var holding=row.Parameters["lifecycle"]=="certificate_holding_v1"&&Document.Grids[row.Grid!.Value].EndTicks(cell)<=context.ValuationAt.Ticks;
                    var future=row.ValueAt>context.ValuationAt;var lane=holding||future?ScheduleInputLanes.Scenario:ScheduleInputLanes.Base;
                    var amount=Arg("amount",lane);var index=Index(amount,cell);
                    if(amount.Presence[index]!=ScheduleCellPresence.Resolved)throw Error("A contribution is missing its demanded cash amount.");
                    var state=amount.States[index];cash.Add(amount.Values[index]);states.Add(state);
                    classifications.Add(Classify(row,cell,state,context.ValuationAt,holding));
                    if(holding){pv.Add(amount.Values[index]);presence.Add(ScheduleCellPresence.Resolved);}
                    else if(future)
                    {
                        var discounted=Arg("discounted",ScheduleInputLanes.Scenario);var n=Index(discounted,cell);
                        if(discounted.Presence[n]!=ScheduleCellPresence.Resolved)throw Error("An included contribution requires its exact discounted value.");
                        pv.Add(discounted.Values[n]);presence.Add(ScheduleCellPresence.Resolved);
                    }
                    else{pv.Add(default);presence.Add(ScheduleCellPresence.OutsideMeasure);}
                }
                var selectedStates=states.MoveToImmutable();var selected=Column(pv.MoveToImmutable(),selectedStates,presence.MoveToImmutable());
                contributions.Add(new(row.Ordinal,Column(cash.MoveToImmutable(),selectedStates,[..Enumerable.Repeat(ScheduleCellPresence.Resolved,count)]),selected,classifications.MoveToImmutable()));
                values[row.Ordinal]=new(selected,selected);continue;
            }
            ScheduleValueColumn Evaluate(ScheduleInputLanes lane)
            {
                var requested=baseScenarioId==scenarioId?ScheduleInputLanes.Base|ScheduleInputLanes.Scenario:lane;
                if(!demand.Uses[row.Ordinal].Any(use=>(use&requested)!=0))return Missing();
                var operands=instruction.Inputs.Values.SelectMany(addresses=>addresses).Select(address=>lane==ScheduleInputLanes.Base?values[address].Base:values[address].Scenario).ToArray();
                var folded=row.Operation is "sum_cells" or "quantity_sum_cells";
                var canOmit=folded||row.Operation=="sum"&&row.Ordinal==pvRoot;
                var coefficient=row.Parameters.ContainsKey("numerator")?new ScheduleNumber(ScheduleOperations.Integer(row.Parameters["numerator"]),ScheduleOperations.Integer(row.Parameters["denominator"])):new ScheduleNumber(1,1);
                var multiplier=row.Operation=="amount"?ScheduleNumber.Parse(Arg("price",lane).QuoteMultiplier!):new ScheduleNumber(1,1);
                ScheduleNumber At(string port,int cell)
                {var column=Arg(port,lane);return column.Values[Index(column,cell)];}
                var numbers=ImmutableArray.CreateBuilder<ScheduleNumber>(count);var states=ImmutableArray.CreateBuilder<ScheduleCellState>(count);
                var presence=ImmutableArray.CreateBuilder<ScheduleCellPresence>(count);
                for(var cell=0;cell<count;cell++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if((demand.Uses[row.Ordinal][cell]&requested)==0)
                    {numbers.Add(default);states.Add(default);presence.Add(ScheduleCellPresence.NotDemanded);continue;}
                    var state=default(ScheduleCellState);var absent=false;var outside=false;var resolved=0;var total=new ScheduleNumber(0,1);
                    foreach(var operand in operands)
                    {
                        var first=folded?0:Index(operand,cell);var end=folded?operand.Values.Length:first+1;
                        for(var n=first;n<end;n++)
                        {
                            cancellation.ThrowIfCancellationRequested();state=Merge(state,operand.States[n]);
                            if(operand.Presence[n]==ScheduleCellPresence.NotDemanded)absent=true;
                            else if(operand.Presence[n]==ScheduleCellPresence.OutsideMeasure)outside=true;
                            else
                            {
                                resolved++;
                                if(folded||row.Operation is "price_add" or "weighted_price" or "quantity_add" or "sum")
                                {
                                    total=total.Add(operand.Values[n]);
                                    if(row.OutputKind=="quantity")ExactQuantity.RequireRange(total.Numerator,total.Denominator);
                                }
                            }
                        }
                    }
                    if(outside&&!canOmit)throw Error("An excluded PV cell cannot be used as an arithmetic input.");
                    if(absent)
                    {numbers.Add(default);states.Add(state);presence.Add(ScheduleCellPresence.NotDemanded);continue;}
                    if(operands.Length>0&&resolved==0&&row.Ordinal!=pvRoot)
                    {numbers.Add(default);states.Add(state);presence.Add(ScheduleCellPresence.OutsideMeasure);continue;}
                    ScheduleNumber number;
                    switch(row.Operation)
                    {
                        case "constant_price":number=coefficient;state=ScheduleCellState.From(null,RowPriceState.Fixed);break;
                        case "quantity_literal":number=coefficient;state=ScheduleCellState.From(VolumeState.Contractual,null);break;
                        case "profile_quantity":
                            var axis=Document.Grids[row.Grid??throw Error("A duration quantity requires its exact retained grid.")];
                            number=new((System.Numerics.BigInteger)axis.StepTicks*axis.Lengths[cell],TimeSpan.TicksPerHour);state=ScheduleCellState.From(VolumeState.Contractual,null);break;
                        case "price_application":case "price_scale":case "quantity_scale":case "quantity_transform":
                            number=At("input",cell).Multiply(coefficient);
                            if(row.Operation=="price_scale")ExactUnitPrice.RequireFractionRange(number.Numerator,number.Denominator);
                            break;
                        case "quantity_normalize":number=At("input",cell).Divide(At("basis",cell));state=Arg("input",lane).States[Index(Arg("input",lane),cell)];break;
                        case "price_add":case "weighted_price":case "quantity_add":case "sum":case "quantity_sum_cells":case "sum_cells":number=total;break;
                        case "amount":number=At("quantity",cell).Multiply(At("price",cell)).Multiply(multiplier);break;
                        case "pv":number=At("amount",cell).Multiply(Arg("discount",lane).Values[0]);break;
                        default:throw Error("The stored operation has no supported evaluator.");
                    }
                    if(row.OutputKind=="quantity")ExactQuantity.RequireRange(number.Numerator,number.Denominator);
                    numbers.Add(number);states.Add(state);presence.Add(ScheduleCellPresence.Resolved);
                }
                return Column(numbers.MoveToImmutable(),states.MoveToImmutable(),presence.MoveToImmutable());
            }
            var basis=Evaluate(ScheduleInputLanes.Base);
            var identical=baseScenarioId==scenarioId||demand.Uses[row.Ordinal].All(use=>((use&ScheduleInputLanes.Base)!=0)==((use&ScheduleInputLanes.Scenario)!=0))&&
                instruction.Inputs.Values.SelectMany(addresses=>addresses).All(address=>ReferenceEquals(values[address].Base,values[address].Scenario));
            values[row.Ordinal]=new(basis,identical?basis:Evaluate(ScheduleInputLanes.Scenario));
        }
        cancellation.ThrowIfCancellationRequested();
        var output=values[pvRoot].Scenario;
        if(output.Presence.Any(presence=>presence!=ScheduleCellPresence.Resolved))throw Error("The side PV cannot omit a missing demanded input.");
        return new(context,baseScenarioId,scenarioId,Document.ScheduleManifest,[..values],Document.Roots,contributions.ToImmutable());
    }

    private static int Index(ScheduleValueColumn column,int cell)=>column.Values.Length==1?0:cell;
    private ScheduleContributionState Classify(ScheduleDefinitionRow row,int cell,ScheduleCellState state,DateTimeOffset valuation,bool holding)
    {
        var modeled=(state.Prices&SchedulePriceFacts.Modeled)!=0;
        if(row.Grid is null)return new(ComponentKind.Payment,null,null,ValueCoverage.Discounted,modeled,null);
        var delivered=Document.Grids[row.Grid.Value].EndTicks(cell)<=valuation.Ticks;
        var payment=row.Parameters["lifecycle"]=="interval_payment_v1";
        var knownQuantity=state.Quantity is VolumeState.Contractual or VolumeState.Metered;
        var known=knownQuantity&&(state.Prices&~(SchedulePriceFacts.Fixed|SchedulePriceFacts.Actual|SchedulePriceFacts.PublishedFixing))==0;
        var classification=holding&&knownQuantity?EconomicClass.Held:!delivered?EconomicClass.Unrealised:known?EconomicClass.Realised:EconomicClass.Estimated;
        ValueCoverage coverage;string? unavailable=null;
        if(holding)coverage=ValueCoverage.HoldingValue;
        else if(row.ValueAt>valuation)coverage=ValueCoverage.Discounted;
        else if(payment)
        {
            if((state.Prices&(SchedulePriceFacts.Projected|SchedulePriceFacts.Modeled))!=0)
                throw new MarketDataException("side_due_leg_unresolved","A due payment requires its eligible fixed, actual or explicitly estimated amount.");
            coverage=known?ValueCoverage.OverdueBalance:ValueCoverage.OverdueEstimate;unavailable="due_cash_policy_required";
        }
        else coverage=ValueCoverage.DeliveredValue;
        return new(holding?ComponentKind.Holding:payment?ComponentKind.Payment:ComponentKind.Delivery,classification,
            payment?delivered?PaymentLifecycle.DeliveredUnsettled:PaymentLifecycle.RemainingProjected:null,coverage,modeled,unavailable);
    }
    private static ScheduleCellState Merge(ScheduleCellState current,ScheduleCellState next)
    {
        var quantity=current.Quantity;
        if(next.Quantity is {} q)
            quantity=quantity is null?q:quantity==VolumeState.Estimated||q==VolumeState.Estimated?VolumeState.Estimated:
                quantity==VolumeState.Forecast||q==VolumeState.Forecast?VolumeState.Forecast:
                quantity==VolumeState.Metered||q==VolumeState.Metered?VolumeState.Metered:VolumeState.Contractual;
        return new(quantity,current.Prices|next.Prices);
    }
}
