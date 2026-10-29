using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Calculation;

internal static class ScheduleProgramDemandChecks
{
    internal static void Run()
    {
        static BookedSideProgram Book(Side side)=>BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side));
        var f=new PpaAcceptanceFixture();var certificate=f.WithCertificate().Memberships.Single(m=>m.Side is CertificateDeliverySide).Side;
        var holding=Book(certificate);var at=f.To.AddDays(1);
        var after=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic_held_inputs_v1",at,at,at,at);
        var demand=holding.Inputs(after).Sources;
        if(demand.Any(input=>holding.Document.Instructions[input.Instruction].Output.Operation=="discount")||
            demand.Where(input=>holding.Document.Instructions[input.Instruction].Output.Operation=="price_lookup").Any(input=>input.Uses.Any(use=>use!=ScheduleInputLanes.Scenario)))
            throw new Exception("Delivered certificate holdings require current mark inputs and zero discount-factor reads, even before their original payment date.");
        var physical=Book(f.Physical().Memberships[0].Side);
        if(!physical.Inputs(after).Sources.Any(input=>physical.Document.Instructions[input.Instruction].Output.Operation=="discount"))
            throw new Exception("A delivered row with a future value date still requires discounting; delivery alone cannot remove it from the measure.");
        var dueAt=f.To.AddDays(20);
        var due=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic_due_inputs_v1",dueAt,dueAt,dueAt,dueAt);
        demand=physical.Inputs(due).Sources;
        if(demand.Any(input=>physical.Document.Instructions[input.Instruction].Output.Operation=="discount")||demand.Any(input=>input.Uses.Any(use=>use!=ScheduleInputLanes.Base)))
            throw new Exception("Delivered physical value requires its base-context facts and zero discount-factor reads.");
        var cash=Book(f.Physical().Memberships[1].Side);
        if(cash.Inputs(due).Sources.Any(input=>cash.Document.Instructions[input.Instruction].Output.Operation=="discount"))
            throw new Exception("Overdue cash must retain amount inputs without loading an unused discount factor.");
        var swap=new CommodityLookupFixture(2,monthly:true);var fixedSide=swap.Swap(monthly:true,monthOrdinal:0).Fixed;
        var dated=Book(fixedSide);var date=swap.Snapshot.ValuationAt;
        var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic_dated_inputs_v1",date,date,date,date);
        if(dated.Inputs(context).Sources.Count(input=>dated.Document.Instructions[input.Instruction].Output.Operation=="discount")!=1)
            throw new Exception("A future dated payment requires exactly one context-derived factor.");
        var payment=fixedSide.Periods[0].PaymentAt;
        try{dated.Inputs(new(Guid.NewGuid(),"synthetic_due_dated_v1",payment,payment,payment,payment));throw new Exception("Due dated payments must refuse before fetching dynamic inputs.");}
        catch(MarketDataException e)when(e.Code=="side_payment_due"){}
        var shared=new ScheduleProgramFixture();var sharedProgram=BookedSideProgram.Compile(shared.Definition());
        var sharedDemand=sharedProgram.Inputs(shared.Context).Sources;
        if(sharedDemand.Single(input=>input.Instruction==1).Uses.Single()!=(ScheduleInputLanes.Base|ScheduleInputLanes.Scenario)||
            sharedDemand.Any(input=>input.Instruction==3)||sharedDemand.Single(input=>input.Instruction==6).Uses.Single()!=ScheduleInputLanes.Scenario)
            throw new Exception("Separate due and future payment rows must retain separate scenario selections, even when they consume one scalar price lookup.");
        var capacity=Book(f.CapacityAgreement().Memberships[0].Side);
        var before=f.From.AddDays(-1);var beforeContext=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic_capacity_inputs_v1",before,before,before,before);
        var capacityDemand=capacity.Inputs(beforeContext).Sources;
        var capacitySources=capacityDemand.Where(input=>capacity.Document.Instructions[input.Instruction].Output.Operation=="asset_capacity").ToArray();
        if(capacitySources.Length!=2||capacitySources.Any(input=>input.Uses.Length!=30||input.Uses.Any(use=>use!=ScheduleInputLanes.Scenario)))
            throw new Exception("A monthly capacity payment must request every original operating day's accreditation and normalization basis, not just its one output cell.");
        var original=sharedProgram.Write();_ =sharedProgram.Inputs(beforeContext);
        if(!sharedProgram.Write().SequenceEqual(original))throw new Exception("Changing context must not replace, mutate or regenerate the booked instructions.");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{sharedProgram.Inputs(shared.Context,cancelled.Token);throw new Exception("A cancelled input selection must refuse.");}
        catch(OperationCanceledException){}
        var definition=shared.Definition();var rows=definition.Rows.ToArray();
        rows[5]=rows[5] with{Parameters=rows[5].Parameters.SetItem("pricePolicy","unsupported_policy")};
        var unsupported=new ScheduleDefinition(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
            definition.SchemaVersion,definition.GeneratorVersion,rows,definition.Edges,definition.Bindings,definition.Roots,definition.Ports,definition.Grids);
        try{BookedSideProgram.Compile(unsupported).Inputs(shared.Context);throw new Exception("An unsupported selection policy must refuse before any source reads.");}
        catch(MarketDataException e)when(e.Code=="side_schedule_program"){}
        Console.WriteLine("PASS common source demand: future DF, holding and overdue DF omission, frozen/live lanes and due dated refusal");
    }
}
