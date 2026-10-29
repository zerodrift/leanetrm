using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Power;

internal static class ScheduleProgramExecutionChecks
{
    private static ScheduleNumber N(string text)=>ScheduleNumber.Parse(text);
    private static ScheduleCellState State(VolumeState? quantity,RowPriceState? price)=>ScheduleCellState.From(quantity,price);
    private static BookedSideProgram Load(Side side)=>Load(ScheduleDefinitionCompiler.Generate(side));
    private static BookedSideProgram Load(ScheduleDefinition definition)
    {
        var bytes=BookedSideProgram.Compile(definition).Write();
        return BookedSideProgram.Read(bytes,new(BookedSideProgram.Digest(bytes),definition.Manifest,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity));
    }
    private static ResolvedCalculationContext Context(DateTimeOffset at)=>new(Guid.NewGuid(),"synthetic_program_values_v1",at,at,at,at);
    private static Dictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>> Sources(BookedSideProgram program,ScheduleInputDemandSet demand,string basis,string scenario,
        Func<ScheduleDefinitionRow,int,string,(ScheduleNumber Number,ScheduleCellState State)> resolve)
    {
        var result=new Dictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>>();
        foreach(var input in demand.Sources)
        {
            var row=program.Document.Instructions[input.Instruction].Output;
            foreach(var id in new[]{basis,scenario}.Distinct())
            {
                bool Required(ScheduleInputLanes use)=>id==basis&&(use&ScheduleInputLanes.Base)!=0||id==scenario&&(use&ScheduleInputLanes.Scenario)!=0;
                if(!input.Uses.Any(Required))continue;
                var numbers=ImmutableArray.CreateBuilder<ScheduleNumber>();var states=ImmutableArray.CreateBuilder<ScheduleCellState>();var presence=ImmutableArray.CreateBuilder<ScheduleCellPresence>();
                for(var cell=0;cell<input.Uses.Length;cell++)
                {
                    if(Required(input.Uses[cell])){var value=resolve(row,cell,id);numbers.Add(value.Number);states.Add(value.State);presence.Add(ScheduleCellPresence.Resolved);}
                    else{numbers.Add(default);states.Add(default);presence.Add(ScheduleCellPresence.NotDemanded);}
                }
                result.Add(new(row.Ordinal,id),new(demand.Context,id,new(row.OutputKind,row.Grid,row.Unit,row.Currency,row.QuoteMultiplier,numbers.ToImmutable(),states.ToImmutable(),presence.ToImmutable())));
            }
        }
        return result;
    }
    private static void Equal(string actual,string expected)
    {if(actual!=expected)throw new Exception($"Expected exact amount{expected}; received{actual}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected structured refusal:"+code);}

    internal static void Run()
    {
        var f=new CommodityLookupFixture(2,monthly:true);var original=f.Swap(monthly:true,monthOrdinal:0);var next=f.Swap(monthly:true,monthOrdinal:1);
        var context=Context(f.Snapshot.ValuationAt);var fixedProgram=Load(original.Fixed);var demand=fixedProgram.Inputs(context);
        var fixedInputs=Sources(fixedProgram,demand,"base","base",(_,_,_)=>(N("0.9"),default));
        Equal(fixedProgram.Execute(demand,"base","base",fixedInputs).PresentValue.ToExactString(),"3.6");
        var daily=new CommodityLookupFixture(31);var dailySide=daily.Swap().Floating;var dailyProgram=Load(dailySide);
        var dailyRequest=dailyProgram.Inputs(Context(daily.Snapshot.ValuationAt));
        var dailyColumns=Sources(dailyProgram,dailyRequest,"base","base",(row,_,_)=>row.Operation=="discount"?(N("1"),default):
            (N(row.ObservationFrom==dailySide.DeliveryFrom?"2":"1"),State(null,RowPriceState.Projected)));
        Equal(dailyProgram.Execute(dailyRequest,"base","base",dailyColumns).PresentValue.ToExactString(),"32/31");
        var whole=new FloatingSide(Guid.NewGuid(),original.Floating.Settlement,f.Quote,original.Floating.UnitQuantity,[original.Floating.Periods[0],next.Floating.Periods[0]]);
        var monthly=Load(whole);var selection=monthly.Inputs(context);
        foreach(var (id,shift,expected) in new[]{("base",0,"25"),("up",1,"26.7"),("down",-1,"23.3")})
        {
            var columns=Sources(monthly,selection,"base",id,(row,_,_)=>row.Operation=="discount"?(N(row.PaymentOrdinal==0?"0.9":"0.8"),default):
                (new ScheduleNumber((row.PaymentOrdinal==0?10:20)+shift,1),State(null,RowPriceState.Projected)));
            Equal(monthly.Execute(selection,"base",id,columns).PresentValue.ToExactString(),expected);
        }
        Native();SeparateRows();MixedCells();MixedFacts();LargeCash();QuantityBounds();UnusedIntermediate();Refusals(fixedProgram,demand,fixedInputs);
        Console.WriteLine("PASS stored program execution: exact dated/native arithmetic, independent payments, scenario selection, excluded PV, numeric bounds and missing-input refusal");
    }
    private static void Native()
    {
        foreach(var minutes in new[]{60,15})
        {
            var f=new PpaAcceptanceFixture(minutes);var context=Context(f.From.AddDays(-1));
            var profile=Load(f.Physical().Memberships[0].Side);var request=profile.Inputs(context);
            var columns=Sources(profile,request,"base","base",(row,_,_)=>row.Operation=="discount"?(N("0.9"),default):(N("42"),State(null,RowPriceState.Projected)));
            // November has721 actual hours in the retained delivery clock.
            Equal(profile.Execute(request,"base","base",columns).PresentValue.ToExactString(),"27253.8");
            var indexed=new FloatingSide(Guid.NewGuid(),f.Terms(f.Profile),new(f.C("synthetic_energy"),ExactDecimal.Parse("2"),new(ExactDecimal.Parse("3"),f.EnergyPrice),null));
            var floating=Load(indexed);request=floating.Inputs(context);
            columns=Sources(floating,request,"base","base",(row,_,_)=>row.Operation=="discount"?(N("0.9"),default):(N("10"),State(null,RowPriceState.Projected)));
            Equal(floating.Execute(request,"base","base",columns).PresentValue.ToExactString(),"14924.7");
            var volume=Load(f.Physical(volume:true).Memberships[0].Side);request=volume.Inputs(context);
            columns=Sources(volume,request,"base","base",(row,cell,_)=>row.Operation switch
            {
                "price_lookup"=>(N("42"),State(null,RowPriceState.Projected)),"discount"=>(N("0.9"),default),
                "asset_capacity"=>(N("200"),State(VolumeState.Contractual,null)),
                _=>(new ScheduleNumber(50*(BigInteger)volume.Document.Grids[row.Grid!.Value].StepTicks*volume.Document.Grids[row.Grid.Value].Lengths[cell],TimeSpan.TicksPerHour),State(VolumeState.Forecast,null))
            });
            Equal(volume.Execute(request,"base","base",columns).PresentValue.ToExactString(),"6813.45");
            var capacity=Load(f.CapacityAgreement().Memberships[0].Side);request=capacity.Inputs(context);
            columns=Sources(capacity,request,"base","base",(row,_,_)=>row.Operation switch
            {
                "price_lookup"=>(N("4"),State(null,RowPriceState.Projected)),"discount"=>(N("0.9"),default),
                _=>(N(row.Parameters["measure"]=="accredited_capacity_v1"?"160":"200"),State(VolumeState.Contractual,null))
            });
            Equal(capacity.Execute(request,"base","base",columns).PresentValue.ToExactString(),"86.4");
            var certificate=Load(f.WithCertificate().Memberships.Single(member=>member.Side is CertificateDeliverySide).Side);
            request=certificate.Inputs(Context(f.To.AddDays(1)));
            columns=Sources(certificate,request,"base","up",(row,cell,_)=>row.Operation switch
            {
                "price_lookup"=>(N("6"),State(null,RowPriceState.Projected)),"asset_capacity"=>(N("200"),State(VolumeState.Contractual,null)),
                _=>(new ScheduleNumber(50*(BigInteger)certificate.Document.Grids[row.Grid!.Value].StepTicks*certificate.Document.Grids[row.Grid.Value].Lengths[cell],TimeSpan.TicksPerHour),State(VolumeState.Metered,null))
            });
            var held=certificate.Execute(request,"base","up",columns);Equal(held.PresentValue.ToExactString(),"1081.5");
            if(held.Contributions.SelectMany(contribution=>contribution.States).Any(state=>state.Kind!=ComponentKind.Holding||state.Class!=EconomicClass.Held||state.Coverage!=ValueCoverage.HoldingValue))
                throw new Exception("Delivered metered certificates retain current scenario holding value without a discount factor.");
        }
    }
    private static void SeparateRows()
    {
        var f=new ScheduleProgramFixture();var program=Load(f.Definition());var request=program.Inputs(f.Context);
        var columns=Sources(program,request,"base","up",(row,_,id)=>row.Operation=="discount"?(N("0.9"),default):(N(id=="base"?"10":"15"),State(null,RowPriceState.Estimated)));
        var evaluated=program.Execute(request,"base","up",columns);
        Equal(evaluated.PresentValue.ToExactString(),"13.5");
        var due=evaluated.Contributions.Single(row=>row.Instruction==5);var future=evaluated.Contributions.Single(row=>row.Instruction==8);
        if(due.Amount.Values.Any(value=>value!=N("5"))||future.Amount.Values.Any(value=>value!=N("7.5"))||
            due.PresentValue.Presence.Any(value=>value!=ScheduleCellPresence.OutsideMeasure)||!evaluated.HasOverdue||evaluated.CompletePresentValue is not null||
            due.States.Any(state=>state.Coverage!=ValueCoverage.OverdueEstimate||state.Class!=EconomicClass.Estimated||state.Lifecycle!=PaymentLifecycle.DeliveredUnsettled))
            throw new Exception("Separate payment rows must retain base overdue cash and shocked future value, with absent excluded PV and truthful row states.");
        Reject("side_schedule_program",()=>program.Execute(request,"base","other_scenario",columns));
        columns.Remove(new(1,"base"));Reject("side_schedule_program",()=>program.Execute(request,"base","up",columns));
        var zero=Sources(program,request,"base","base",(row,_,_)=>row.Operation=="discount"?(N("1"),default):(N("0"),State(null,RowPriceState.Estimated)));
        var zeroValue=program.Execute(request,"base","base",zero);Equal(zeroValue.PresentValue.ToExactString(),"0");
        if(!zeroValue.HasOverdue||zeroValue.CompletePresentValue is not null)throw new Exception("A resolved zero price does not settle overdue cash or remove its explicit policy requirement.");
    }
    private static void MixedFacts()
    {
        var f=new ScheduleProgramFixture();var d=f.Definition();int Map(int ordinal)=>ordinal<2?ordinal:ordinal+2;
        var rows=d.Rows.Select(row=>row with{Ordinal=Map(row.Ordinal)}).ToList();
        rows.Insert(2,d.Rows[1] with{Ordinal=2,Key="modeled_price"});
        rows.Insert(3,d.Rows[1] with{Ordinal=3,Key="mixed_price",Role="intermediate",Operation="price_add",Parameters=ScheduleProgramFixture.P()});
        var edges=d.Edges.Select(edge=>new ScheduleDefinitionEdge(Map(edge.Target),edge.Port,edge.Ordinal,edge.Target==2&&edge.Port=="price"?3:Map(edge.Source))).ToList();
        edges.Add(new(3,"input",0,1));edges.Add(new(3,"input",1,2));
        var bindings=d.Bindings.Concat(d.Bindings.Select(binding=>binding with{Row=2,Code="synthetic_other_price"})).ToArray();
        var definition=new ScheduleDefinition(d.SideId,d.Family,d.EconomicIdentity,d.PricingBindingIdentity,d.SchemaVersion,d.GeneratorVersion,rows,edges,bindings,
            d.Roots.Select(root=>root with{Row=Map(root.Row)}).ToArray(),d.Ports,d.Grids);
        var program=Load(definition);var future=program.Inputs(Context(f.From.AddDays(-1)));
        (ScheduleNumber,ScheduleCellState) Resolve(ScheduleDefinitionRow row,int cell,string id)=>row.Operation=="discount"?(N("1"),default):
            row.Ordinal==1?(N("10"),State(null,RowPriceState.Projected)):(N("5"),State(null,RowPriceState.Modeled));
        var evaluated=program.Execute(future,"base","base",Sources(program,future,"base","base",Resolve));Equal(evaluated.PresentValue.ToExactString(),"30");
        if(evaluated.Contributions.Any(contribution=>contribution.Amount.States.Any(state=>state.Prices!=(SchedulePriceFacts.Projected|SchedulePriceFacts.Modeled))||contribution.States.Any(state=>!state.Modeled)))
            throw new Exception("Mixed projected and modeled components must preserve both states and modeled-amount evidence.");
        var due=program.Inputs(f.Context);
        Reject("side_due_leg_unresolved",()=>program.Execute(due,"base","base",Sources(program,due,"base","base",Resolve)));
    }
    private static void MixedCells()
    {
        var f=new ScheduleProgramFixture();var d=f.Definition();var rows=d.Rows.Select(row=>row.Operation=="output_guard"?
            row with{ValueAt=f.From,Parameters=row.Parameters.SetItem("lifecycle","certificate_holding_v1")}:
            row.Ordinal is 1 or 2 or 3 or 4 or 6 or 7?row with{Grid=0}:row).ToArray();
        var definition=new ScheduleDefinition(d.SideId,d.Family,d.EconomicIdentity,d.PricingBindingIdentity,d.SchemaVersion,d.GeneratorVersion,
            rows,d.Edges,d.Bindings,d.Roots,d.Ports,d.Grids);
        var program=Load(definition);var request=program.Inputs(Context(f.From.AddHours(1)));
        var inputs=Sources(program,request,"base","up",(_,_,id)=>(N(id=="base"?"10":"15"),State(null,RowPriceState.Estimated)));
        var evaluated=program.Execute(request,"base","up",inputs);Equal(evaluated.PresentValue.ToExactString(),"15");
        foreach(var row in evaluated.Contributions)
            if(row.PresentValue.Presence[0]!=ScheduleCellPresence.Resolved||row.PresentValue.Presence[1]!=ScheduleCellPresence.OutsideMeasure||
                row.Amount.Values[0]!=N("7.5")||row.Amount.Values[1]!=N("5")||row.States[0].Class!=EconomicClass.Held||row.States[1].Class!=EconomicClass.Unrealised)
                throw new Exception("A vector block must preserve each cell's scenario selection and distinguish held value from an excluded PV cell.");
        var key=new ScheduleInputAddress(1,"base");var source=inputs[key];
        inputs[key]=source with{Value=source.Value with{Presence=source.Value.Presence.SetItem(1,ScheduleCellPresence.NotDemanded)}};
        Reject("side_input_column",()=>program.Execute(request,"base","up",inputs));
        inputs[key]=source with{Value=source.Value with{Values=source.Value.Values.SetItem(0,N("1")),States=source.Value.States.SetItem(0,State(null,RowPriceState.Estimated)),
            Presence=source.Value.Presence.SetItem(0,ScheduleCellPresence.Resolved)}};
        Reject("side_input_column",()=>program.Execute(request,"base","up",inputs));
        var oneFrame=Sources(program,request,"same","same",(_,_,_)=>(N("10"),State(null,RowPriceState.Estimated)));
        if(oneFrame.Count!=1)throw new Exception("Equal actual scenario identities must resolve a shared source once with unioned cell uses.");
        Equal(program.Execute(request,"same","same",oneFrame).PresentValue.ToExactString(),"10");
    }
    private static void LargeCash()
    {
        var f=new ScheduleProgramFixture();var definition=f.Definition();var rows=definition.Rows.ToArray();var magnitude=BigInteger.One<<3000;
        rows[0]=rows[0] with{Parameters=rows[0].Parameters.SetItem("numerator",magnitude.ToString()).SetItem("denominator","1")};
        var revised=new ScheduleDefinition(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,definition.SchemaVersion,definition.GeneratorVersion,
            rows,definition.Edges,definition.Bindings,definition.Roots,definition.Ports,definition.Grids);
        var program=Load(revised);var request=program.Inputs(Context(f.From.AddDays(-1)));
        var columns=Sources(program,request,"base","base",(row,_,_)=>row.Operation=="price_lookup"?(new ScheduleNumber(magnitude,1),State(null,RowPriceState.Projected)):(N("1"),default));
        // Two cells times two payments, each2^6000; no cash rounding or cap.
        Equal(program.Execute(request,"base","base",columns).PresentValue.Numerator,(BigInteger.One<<6002).ToString());
        var market=new CommodityLookupFixture(2,monthly:true);var first=market.Swap(monthly:true,monthOrdinal:0);var second=market.Swap(monthly:true,monthOrdinal:1);
        var floating=new FloatingSide(Guid.NewGuid(),first.Floating.Settlement,market.Quote,first.Floating.UnitQuantity,[first.Floating.Periods[0],second.Floating.Periods[0]]);
        var original=ScheduleDefinitionCompiler.Generate(floating);
        var expanded=new ScheduleDefinition(original.SideId,original.Family,original.EconomicIdentity,original.PricingBindingIdentity,original.SchemaVersion,original.GeneratorVersion,
            original.Rows.Select(row=>row.Operation=="quantity_literal"?row with{Parameters=row.Parameters.SetItem("numerator",magnitude.ToString()).SetItem("denominator","1")}:row).ToArray(),
            original.Edges,original.Bindings,original.Roots,original.Ports,original.Grids);
        var net=Load(expanded);var netRequest=net.Inputs(Context(market.Snapshot.ValuationAt));
        var netInputs=Sources(net,netRequest,"base","base",(row,_,_)=>row.Operation=="discount"?(N("1"),default):
            (new ScheduleNumber(row.PaymentOrdinal==0?magnitude:-magnitude,1),State(null,RowPriceState.Projected)));
        Equal(net.Execute(netRequest,"base","base",netInputs).PresentValue.ToExactString(),"0");
    }
    private static void Refusals(BookedSideProgram program,ScheduleInputDemandSet request,Dictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>> original)
    {
        var address=original.Keys.Single();var row=program.Document.Instructions[address.Instruction].Output;
        var inputs=new Dictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>>(original);
        inputs[address]=new(request.Context,"base",ScheduleValueColumn.Input(row,[N("1/3")],[default]));
        Reject("precision_loss",()=>program.Execute(request,"base","base",inputs));
        inputs[address]=new(request.Context,"base",ScheduleValueColumn.Input(row,[default(ScheduleNumber)],[default]));
        Reject("side_input_column",()=>program.Execute(request,"base","base",inputs));
        inputs.Clear();Reject("side_schedule_program",()=>program.Execute(request,"base","base",inputs));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{program.Execute(request,"base","base",original,cancelled.Token);throw new Exception("A cancelled evaluation cannot return a value.");}catch(OperationCanceledException){}
    }
    private static void QuantityBounds()
    {
        var f=new ScheduleProgramFixture();var d=f.Definition();var magnitude=(BigInteger.One<<4095).ToString();int Map(int ordinal)=>ordinal==0?0:ordinal+3;
        var literal=d.Rows[0] with{Parameters=d.Rows[0].Parameters.SetItem("numerator",magnitude).SetItem("denominator","1")};
        var rows=d.Rows.Select(row=>row.Ordinal==0?literal:row with{Ordinal=Map(row.Ordinal)}).ToList();
        rows.Insert(1,literal with{Ordinal=1,Key="second_quantity"});
        rows.Insert(2,literal with{Ordinal=2,Key="third_quantity",Parameters=literal.Parameters.SetItem("numerator","-"+magnitude)});
        rows.Insert(3,literal with{Ordinal=3,Key="quantity_total",Role="intermediate",Operation="quantity_add",Parameters=ScheduleProgramFixture.P()});
        var edges=d.Edges.Select(edge=>new ScheduleDefinitionEdge(Map(edge.Target),edge.Port,edge.Ordinal,edge.Target==2&&edge.Port=="quantity"?3:Map(edge.Source))).ToList();
        edges.Add(new(3,"input",0,0));edges.Add(new(3,"input",1,1));edges.Add(new(3,"input",2,2));
        var definition=new ScheduleDefinition(d.SideId,d.Family,d.EconomicIdentity,d.PricingBindingIdentity,d.SchemaVersion,d.GeneratorVersion,rows,edges,
            d.Bindings.Select(binding=>binding with{Row=Map(binding.Row)}).ToArray(),d.Roots.Select(root=>root with{Row=Map(root.Row)}).ToArray(),d.Ports,d.Grids);
        var program=Load(definition);var request=program.Inputs(Context(f.From.AddDays(-1)));
        var inputs=Sources(program,request,"base","base",(row,_,_)=>row.Operation=="discount"?(N("1"),default):(N("1"),State(null,RowPriceState.Projected)));
        Reject("numeric_range",()=>program.Execute(request,"base","base",inputs));
    }
    private static void UnusedIntermediate()
    {
        var f=new ScheduleProgramFixture();var d=f.Definition();int Map(int ordinal)=>ordinal<3?ordinal:ordinal+2;
        var rows=d.Rows.Select(row=>row with{Ordinal=Map(row.Ordinal)}).ToList();
        rows.Insert(3,d.Rows[1] with{Ordinal=3,Key="future_scaled_price",Role="intermediate",Operation="price_scale",Parameters=ScheduleProgramFixture.P(("numerator","2"),("denominator","1"))});
        rows.Insert(4,d.Rows[2] with{Ordinal=4,Key="future_amount"});
        var edges=d.Edges.Select(edge=>new ScheduleDefinitionEdge(Map(edge.Target),edge.Port,edge.Ordinal,
            edge.Source==2&&edge.Target is 7 or 8?4:Map(edge.Source))).ToList();
        edges.Add(new(3,"input",0,1));edges.Add(new(4,"quantity",0,0));edges.Add(new(4,"price",0,3));
        var definition=new ScheduleDefinition(d.SideId,d.Family,d.EconomicIdentity,d.PricingBindingIdentity,d.SchemaVersion,d.GeneratorVersion,rows,edges,d.Bindings,
            d.Roots.Select(root=>root with{Row=root.Name=="second/cash"?4:Map(root.Row)}).ToArray(),d.Ports,d.Grids);
        var program=Load(definition);var request=program.Inputs(f.Context);
        var inputs=Sources(program,request,"base","up",(row,_,id)=>row.Operation=="discount"?(N("0.9"),default):
            (id=="base"?new ScheduleNumber(BigInteger.One<<4095,1):N("1"),State(null,RowPriceState.Estimated)));
        Equal(program.Execute(request,"base","up",inputs).PresentValue.ToExactString(),"1.8");
    }
}
