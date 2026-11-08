using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Crude;
using OpenEtrm.MarketData.Gas;
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
        StartedGasDelivery();
    }

    // Three gas days priced 1, 10 and 100 with unit daily quantity and unit
    // discount factors: before start the side is worth 111, and once day 1 has
    // ended (its end equals the valuation instant) only 110 remains.
    private static void StartedGasDelivery()
    {
        static ScheduleDefinition Relabel(ScheduleDefinition d,string lifecycle,string? family=null)=>new(d.SideId,family??d.Family,d.EconomicIdentity,d.PricingBindingIdentity,
            d.SchemaVersion,d.GeneratorVersion,d.Rows.Select(r=>r.Operation=="output_guard"?r with{Parameters=r.Parameters.SetItem("lifecycle",lifecycle)}:r).ToArray(),
            d.Edges,d.Bindings,d.Roots,d.Ports,d.Grids);
        static ResolvedCalculationContext At(DateTimeOffset at)=>new(Guid.NewGuid(),"synthetic_started_delivery_v1",at,at,at,at);
        static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected structured refusal:"+code);}
        var (f,clock,from,gasSide,cashSide,crudeSide)=Sides();
        var gas=ScheduleDefinitionCompiler.Generate(gasSide);
        var future=BookedSideProgram.Compile(gas);var started=BookedSideProgram.Compile(Relabel(gas,ScheduleLifecycles.WholeGasDayDelivery));
        var dayEnd=clock.Boundary(from.AddDays(1));var before=future.Inputs(At(clock.Boundary(from).AddDays(-1))).Sources;
        var after=started.Inputs(At(dayEnd)).Sources;
        // Oracle: the pre-start demand without the first day's own price and
        // value-date rows; nothing else may change. A day still in progress
        // keeps its complete pre-start demand.
        static bool Same(ScheduleInputDemand[] a,ScheduleInputDemand[] b)=>a.Length==b.Length&&a.Zip(b).All(p=>p.First.Instruction==p.Second.Instruction&&p.First.Uses.SequenceEqual(p.Second.Uses));
        var expected=before.Where(input=>future.Document.Instructions[input.Instruction].Output.ValueAt>dayEnd).ToArray();
        if(expected.Length!=4||expected.Count(input=>future.Document.Instructions[input.Instruction].Output.Operation=="discount")!=2||!Same([..after],expected)||
            !Same([..started.Inputs(At(dayEnd.AddTicks(-1))).Sources],[..before]))
            throw new Exception("An ended gas day must read no inputs while remaining and in-progress days keep their pre-start price and discount demand.");
        (ScheduleNumber,ScheduleCellState) Price(ScheduleDefinitionRow row,int cell,string id)=>row.Operation=="discount"?(ScheduleNumber.Parse("1"),default):
            row.Operation is "price_lookup" or CurvePriceScheduleSource.Operation?(ScheduleNumber.Parse(((int)Math.Pow(10,(row.ValueAt!.Value-clock.Boundary(from)).Days-1)).ToString()),ScheduleCellState.From(null,RowPriceState.Projected)):
            throw new Exception("Unexpected gas input "+row.Operation);
        var beforeDemand=future.Inputs(At(clock.Boundary(from).AddDays(-1)));var afterDemand=started.Inputs(At(dayEnd));
        var whole=future.Execute(beforeDemand,"base","base",ScheduleProgramExecutionChecks.Sources(future,beforeDemand,"base","base",Price)).PresentValue.ToExactString();
        var remaining=started.Execute(afterDemand,"base","base",ScheduleProgramExecutionChecks.Sources(started,afterDemand,"base","base",Price)).PresentValue.ToExactString();
        if(whole!="111"||remaining!="110")throw new Exception($"Ended gas days must contribute nothing: expected 111 then 110, received {whole} then {remaining}.");
        Reject("gas_delivery_lifecycle",()=>future.Inputs(At(dayEnd)));

        var cash=ScheduleDefinitionCompiler.Generate(cashSide);
        var undue=BookedSideProgram.Compile(cash);var accrued=BookedSideProgram.Compile(Relabel(cash,ScheduleLifecycles.AccruedPayment));
        var paid=clock.Boundary(from.AddDays(3));
        if(accrued.Document.Instructions.Count(i=>i.Output is{Operation:"output_guard",Grid:null})!=3)
            throw new Exception("The cash fixture must retain three dated payment rows.");
        var dueDemand=accrued.Inputs(At(paid));
        if(dueDemand.Sources.Any(input=>accrued.Document.Instructions[input.Instruction].Output.Operation=="discount")||
            dueDemand.Sources.Any(input=>input.Uses.Any(use=>use!=ScheduleInputLanes.Base&&use!=ScheduleInputLanes.None)))
            throw new Exception("Due accrued cash requires base-lane amounts and zero discount-factor reads.");
        Reject("side_payment_due",()=>undue.Inputs(At(paid)));
        var dueStates=accrued.Execute(dueDemand,"base","base",ScheduleProgramExecutionChecks.Sources(accrued,dueDemand,"base","base",(row,cell,id)=>throw new Exception("Fixed cash reads no inputs.")))
            .Contributions.SelectMany(c=>c.States).ToArray();
        if(dueStates.Length!=3||dueStates.Any(state=>state.Coverage!=ValueCoverage.OverdueBalance||state.Kind!=ComponentKind.Payment))
            throw new Exception("Due accrued cash with a fixed price must remain an overdue balance.");
        var floating=ScheduleDefinitionCompiler.Generate(new CommodityLookupFixture(2,monthly:true).Swap(monthly:true,monthOrdinal:0).Floating);
        var indexed=BookedSideProgram.Compile(Relabel(floating,ScheduleLifecycles.AccruedPayment));
        var due=indexed.Inputs(At(indexed.Document.Instructions.Single(i=>i.Output.Operation=="output_guard").Output.ValueAt!.Value));
        (ScheduleNumber,ScheduleCellState) Projected(ScheduleDefinitionRow row,int cell,string id)=>(ScheduleNumber.Parse("4"),ScheduleCellState.From(null,RowPriceState.Projected));
        Reject("side_due_leg_unresolved",()=>indexed.Execute(due,"base","base",ScheduleProgramExecutionChecks.Sources(indexed,due,"base","base",Projected)));

        Reject("physical_lifecycle_unsupported",()=>BookedSideProgram.Compile(Relabel(gas,ScheduleLifecycles.WholeGasDayDelivery,"lng")));
        var crude=ScheduleDefinitionCompiler.Generate(crudeSide);
        Reject("physical_lifecycle_unsupported",()=>BookedSideProgram.Compile(Relabel(crude,ScheduleLifecycles.WholeGasDayDelivery)));
        Console.WriteLine("PASS started delivery lifecycles: ended gas days drop out at their end, due accrued cash stays nominal, old lifecycles still refuse");
    }

    internal static (CommodityLookupFixture F,GasDayClock Clock,DateOnly From,PhysicalGasSide Gas,FixedSide Cash,PhysicalCrudeSide Crude) Sides()
    {
        var f=new CommodityLookupFixture(31);ReferenceKey R(string code)=>new(f.Quote.Key.Revision,code);
        var lookups=f.Swap().Floating.GenerateSchedule().Rows[0].Prices.Take(3).Select(p=>p.Lookup!).ToArray();
        var from=lookups[0].Contract.DeliveryFrom;var clock=GasDayClock.Capture(R("gas_clock"),TimeZoneInfo.Utc,0);
        var location=new GasDeliveryLocation(R("pipeline"),R("zone"),R("delivery_point"));
        var assignments=lookups.Select(l=>new GasValuationAssignment(R("gas_value_"+l.Contract.DeliveryFrom.Day),location,R("gas_product"),l.Contract.DeliveryFrom,l.Contract.DeliveryTo,
            l.Settlement,l.Quote,new(l.Market,l.Contract),l.Fixing,new UnitPrice(default,f.Quote))).ToImmutableArray();
        var terms=new GasQuantityTerms(clock,from,from.AddDays(3),GasQuantityBasis.PerOperatingDay,new(ExactDecimal.Parse("1"),f.Quote.Quantity),GasFuelConvention.None,default);
        var gas=new PhysicalGasSide(Guid.NewGuid(),location,R("gas_product"),terms,assignments);
        FixedSidePeriod Period(int day)=>new(from.AddDays(day),from.AddDays(day+1),clock.Boundary(from.AddDays(day+1)),new(1,1),null);
        var cash=new FixedSide(Guid.NewGuid(),new FlatPrice(new UnitPrice(ExactDecimal.Parse("3"),f.Quote)),f.Quote,
            new(ExactDecimal.Parse("1"),f.Quote.Quantity),[Period(0),Period(1),Period(2)]);
        var crudeFixture=new CommodityLookupFixture(31,monthly:true,volume:true);
        var crudeLookup=crudeFixture.Swap(true,0).Floating.GenerateSchedule().Rows[0].Prices[0].Lookup!;
        ReferenceKey C(string code)=>new(crudeFixture.Quote.Key.Revision,code);
        var crudeQuantity=new QuantitySpecification(crudeFixture.Quote.Quantity,C("test_crude"),C("standard_volume"),C("unit"),C("volume"));
        var crudeAssignment=new CrudeValuationAssignment(C("crude_assignment"),crudeQuantity.Commodity,C("test_grade"),new(C("test_terminal"),C("test_custody_point")),
            crudeQuantity,C("utc"),ExactDecimal.Parse("60"),"fahrenheit",crudeLookup.Settlement,crudeFixture.Quote,new(crudeLookup.Market,crudeLookup.Contract),crudeLookup.Fixing!);
        var crudeFrom=new DateTimeOffset(2041,1,10,12,0,0,TimeSpan.Zero);
        var crude=new PhysicalCrudeSide(Guid.NewGuid(),crudeAssignment,crudeFrom,crudeFrom.AddDays(3),default);
        return (f,clock,from,gas,cash,crude);
    }
}
