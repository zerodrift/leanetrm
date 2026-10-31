using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;

internal static class ScheduleMarketSourceChecks
{
    internal static void Run()
    {
        var fixture=new CommodityLookupFixture(2,monthly:true);var swap=fixture.Swap(monthly:true,monthOrdinal:0);
        var program=Load(swap.Floating);var snapshot=fixture.Snapshot.WithKnowledge(Guid.NewGuid(),fixture.Snapshot.ValuationAt);
        var market=fixture.Model.Build(snapshot,swap.Floating.DependenciesAt(snapshot));
        var sources=new ScheduleMarketSources(market.Graph.Plan.Context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,
            [..swap.Floating.GenerateSchedule().Rows.SelectMany(r=>r.Prices).Select(p=>p.Lookup!).Distinct()],[]);
        var factors=new ExplicitDiscounting(ExactDecimal.Parse("0.9"),DiscountFactorSource.Explicit);
        var result=sources.Evaluate(program,swap.Floating.Id,market,factors);
        Equal(result.PresentValue.ToExactString(),"2.925");
        var receipt=result.SourceReceipt??throw new Exception("Source evaluation must retain input evidence.");
        Equal(receipt.Program.ContentDigest,BookedSideProgram.Digest(program.Write()));
        if(receipt.SideId!=swap.Floating.Id||receipt.Cells.IsEmpty||
            !receipt.Cells.Values.OfType<ScheduleDatedPriceEvidence>().Any(value=>value.Resolution.Lineage.Count>0)||
            !receipt.Cells.Values.OfType<ScheduleDiscountEvidence>().Any(value=>value.Factor.DiscountFactor==ExactDecimal.Parse("0.9")))
            throw new Exception("Dated inputs must retain exact price lineage and discount evidence.");
        // Standalone stored programs have the same admission boundary as API
        // booking: a dated scalar price cannot discard its actual-fixing source.
        var definition=ScheduleDefinitionCompiler.Generate(swap.Floating);
        Reject("side_schedule_definition",()=>new ScheduleDefinition(definition.SideId,definition.Family,definition.EconomicIdentity,
            definition.PricingBindingIdentity,definition.SchemaVersion,definition.GeneratorVersion,definition.Rows,definition.Edges,
            [..definition.Bindings.Where(binding=>binding.Port!="fixing")],definition.Roots,definition.Ports,definition.Grids));

        Reject("side_market_sources",()=>sources.Evaluate(BookedSideProgram.Compile(new ScheduleProgramFixture().Definition()),Guid.NewGuid(),market,factors));
        foreach(var minutes in new[]{60,15})
        {
            var f=new PpaAcceptanceFixture(minutes);var m=f.Market(f.From.AddDays(-1));var instrument=f.Physical(volume:true);
            // Raw source fixtures are shared with the independent established consumer.
            // Runtime sources receive no Side, generator, prepared rows or graph compiler.
            var oracle=f.Prepare(m,[instrument]);
            var native=new ScheduleMarketSources(m.Build.Graph.Plan.Context,m.Build,oracle.Reference,oracle.Read,oracle.KnowledgePolicy,oracle.Policy,[],[]);
            var physical=instrument.Memberships[0].Side;var stored=Load(physical);
            var df=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
            var priced=native.Evaluate(stored,physical.Id,m.Build,df);
            CheckReceipt(priced,stored);
            var volumeEvidence=priced.SourceReceipt!.Cells.Values.OfType<ScheduleVolumeEvidence>().ToArray();
            var priceEvidence=priced.SourceReceipt.Cells.Values.OfType<ScheduleNativePriceEvidence>().ToArray();
            if(volumeEvidence.Length==0||priceEvidence.Length==0||volumeEvidence.Any(value=>value.Volume.Asset!=f.Asset.Key)||
                priceEvidence.Any(value=>value.Resolution.ForwardLineage.Count==0))throw new Exception("Native evidence lost quantity or price lineage.");
            var partial=native.Evaluate(stored,physical.Id,m.Build,df,coverage:new(f.From.AddHours(1),f.From.AddHours(3)));
            CheckReceipt(partial,stored);
            if(partial.SourceReceipt!.Cells.Values.OfType<ScheduleVolumeEvidence>().Any(value=>value.Volume.Interval.Start<f.From.AddHours(1)||value.Volume.Interval.End>f.From.AddHours(3)))
                throw new Exception("Excluded cells must not produce source receipts.");
            Equal(priced.PresentValue.ToExactString(),"7570.5"); //721h ×0.25 ×42
            var bumped=m.Model.Scenarios(m.Build,[m.Bumps["synthetic_energy"]]).Single();
            var shocked=native.Evaluate(stored,physical.Id,bumped.Up,df);
            CheckReceipt(shocked,stored);
            if(!shocked.SourceReceipt!.Cells.Keys.Any(key=>key.ScenarioId==shocked.ScenarioId))throw new Exception("Shocked inputs must retain their scenario identity.");
            Equal(shocked.PresentValue.ToExactString(),"7750.75");
            Equal(native.Evaluate(stored,physical.Id,bumped.Down,df).PresentValue.ToExactString(),"7390.25");
            Equal(native.Evaluate(stored,physical.Id,m.Build,df).PresentValue.ToExactString(),"7570.5");
            Equal(native.Evaluate(stored,physical.Id,m.Build,df,coverage:new(f.From.AddHours(1),f.From.AddHours(3))).PresentValue.ToExactString(),"21");
            Reject("side_market_sources",()=>new ScheduleMarketSources(m.Build.Graph.Plan.Context,bumped.Up,oracle.Reference,oracle.Read,
                oracle.KnowledgePolicy,oracle.Policy,[],[]));
            Reject("side_market_sources",()=>new ScheduleMarketSources(new ResolvedCalculationContext(Guid.NewGuid(),"different",m.Snapshot.ValuationAt,oracle.KnownAt,m.Snapshot.ValuationAt,m.Snapshot.MarketCutoff),m.Build,oracle.Reference,
                oracle.Read,
                oracle.KnowledgePolicy,oracle.Policy,[],[]));
            Reject("side_market_sources",()=>new ScheduleMarketSources(m.Build.Graph.Plan.Context,m.Build,
                oracle.Reference with{Assets=oracle.Reference.Assets.SetItem(f.Asset.Key,f.Asset with{Key=new(f.Definitions,"another_asset")})},
                oracle.Read,oracle.KnowledgePolicy,oracle.Policy,[],[]));
            var wrongManifest=new ResolvedCalculationContext(m.Snapshot.Knowledge!.ContextId,"wrong_manifest",m.Snapshot.ValuationAt,
                oracle.KnownAt,m.Snapshot.ValuationAt,m.Snapshot.MarketCutoff);
            Reject("side_market_sources",()=>new ScheduleMarketSources(wrongManifest,m.Build,oracle.Reference,oracle.Read,oracle.KnowledgePolicy,oracle.Policy,[],[]));
            var fixedVolume=new FixedSide(Guid.NewGuid(),f.Terms(f.Volume),new FlatPrice(new(ExactDecimal.Parse("40"),f.EnergyPrice)));
            var fixedInputs=new ScheduleMarketSources(m.Build.Graph.Plan.Context,m.Build,oracle.Reference,
                oracle.Read with{Curves=ImmutableDictionary<CurveKey,PreparedCurve>.Empty},oracle.KnowledgePolicy,oracle.Policy,[],[]);
            Equal(fixedInputs.Evaluate(Load(fixedVolume),fixedVolume.Id,m.Build,df).PresentValue.ToExactString(),"7210");
            var capacity=f.CapacityAgreement();oracle=f.Prepare(m,[capacity]);
            native=new(m.Build.Graph.Plan.Context,m.Build,oracle.Reference,oracle.Read,oracle.KnowledgePolicy,oracle.Policy,[],[]);
            Equal(native.Evaluate(Load(capacity.Memberships[0].Side),capacity.Memberships[0].Side.Id,m.Build,df).PresentValue.ToExactString(),"96"); //30 ×160/200 ×4
            var boundary=f.Clock.StartOfDay(f.Month.AddDays(15)).ToUniversalTime();
            var changed=f.Asset with{Versions=[new(f.From,boundary,ExactDecimal.Parse("200"),ExactDecimal.Parse("160")),
                new(boundary,f.To,ExactDecimal.Parse("400"),ExactDecimal.Parse("160"))]};
            var changedSources=new ScheduleMarketSources(m.Build.Graph.Plan.Context,m.Build,
                oracle.Reference with{Assets=oracle.Reference.Assets.SetItem(f.Asset.Key,changed)},oracle.Read,oracle.KnowledgePolicy,oracle.Policy,[],[]);
            Equal(changedSources.Evaluate(Load(capacity.Memberships[0].Side),capacity.Memberships[0].Side.Id,m.Build,df).PresentValue.ToExactString(),"72");
        }
        ConnectedProjectionSources();
        InstrumentConnections();
        InstrumentPorts();
        FixingLookups();
        HistoricalSources();
        NativeIndexedCadence();
        CertificateSources();
        PhysicalQuoteScale();
        Console.WriteLine("PASS common schedule market sources: stored dated/native instructions resolve prices, volumes, capacity and discount inputs without Side terms");
    }
    private static void ConnectedProjectionSources()
    {
        var f=new IndexFixingChecks.FixingFixture(monthly:true);
        var at=f.Fixings[0].FixingAt.AddDays(-2);var snapshot=f.Snapshot(at).WithKnowledge(Guid.NewGuid(),at);
        var native=f.Model.Build(snapshot,f.Floating().Dependencies);
        var projection=new CustomCurveProjectionBinding(f.Base.Revision.CatalogId,Guid.NewGuid(),1,"synthetic_output",0,
            CustomCurveProjectionBinding.SupportedSelection,CustomCurveProjectionBinding.SupportedImplementation);
        var side=f.Floating().WithCustomProjection(projection);var program=Load(side);
        var export=f.Model.Compile(snapshot,f.Floating().Dependencies,false,native.Graph.Plan.Context);
        var original=export.Points[(f.Projections[0].Market,InputUse.Variable)];
        var source=export.Definitions.SelectMany(d=>d.Points).Single(p=>p.Key==original);
        var output=source with{Key=new(new(f.Base.Revision,"synthetic_custom_projection"),Guid.NewGuid()),
            Expression=new ConnectedWeightedSum([new(CurvePointReference.Exact(original),ExactDecimal.Parse("1.5"))])};
        var plan=ConnectedCurves.Compile(export.Definitions.Add(new(output.Key.Curve,[output])),[output.Key],native.Graph.Plan.Context,new ExactBindings());
        var graph=ConnectedScenario.Base(plan);var connected=ConnectedValuation.Prepare(graph,snapshot);
        var mapping=ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(side.Dependencies.Single(),output.Key);
        var basis=native.BindConnected(connected,mapping,default);
        ScheduleMarketSources Sources(CurveBuild market)=>new(plan.Context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,
            PendingActualsPolicy.RequireActuals,[..side.GenerateSchedule().Rows.SelectMany(row=>row.Prices).Select(price=>price.Lookup!).Distinct()],[]);
        var adapter=Sources(basis);var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var value=adapter.Evaluate(program,side.Id,basis,discount);
        Equal(value.PresentValue.ToExactString(),"16.5");
        CheckReceipt(value,program);
        if(value.SourceReceipt!.Cells.Values.OfType<ScheduleDatedPriceEvidence>().Single().Lookup.CustomProjection!=projection)
            throw new Exception("Connected output evidence must retain the exact contractual projection.");
        var child=graph.WithShifts([new(CurveShockCoordinate.ComputedOutput,new ParallelCurveTarget(output.Key.Curve),
            source.Unit,ExactDecimal.Parse("2"),CurveShift.ParentAdditive)]);
        var shifted=native.BindConnected(connected.WithScenario(child),mapping,default);
        Equal(adapter.Evaluate(program,side.Id,shifted,discount).PresentValue.ToExactString(),"18.5");
        Reject("side_market_sources",()=>Sources(shifted));
        Reject("side_market_sources",()=>adapter.Evaluate(program,side.Id,native,discount));
        Reject("risk_dependency",()=>Sources(native).Evaluate(program,side.Id,native,discount));
    }
    private sealed class ExactBindings:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new Exception("Exact references required.");
        public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}
    }
    private static void InstrumentConnections()
    {
        foreach(var minutes in new[]{60,15})
        {
            var f=new PpaAcceptanceFixture(minutes);var m=f.Market(f.From.AddDays(-1));var contract=f.Physical(volume:true);
            var oracle=f.Prepare(m,[contract]);var context=m.Build.Graph.Plan.Context;
            var sources=new ScheduleMarketSources(context,m.Build,oracle.Reference,oracle.Read with{Curves=ImmutableDictionary<CurveKey,PreparedCurve>.Empty},
                oracle.KnowledgePolicy,oracle.Policy,[],[]);
            var delivery=contract.Memberships[0].Side;var payment=contract.Memberships[1].Side;
            var output=Load(delivery);var input=Load(payment);
            var normalization=DefinitionIdentity.For(delivery.Terms.Normalization).Canonical;
            var members=ImmutableArray.Create(new InstrumentScheduleMember(delivery.Id,output,normalization,null),new InstrumentScheduleMember(payment.Id,input,normalization,null));
            var root=output.Document.Roots.Single(root=>root.Measure=="schedule_quantity");
            var connections=input.Document.Instructions.Where(row=>row.Bindings.Any(binding=>binding.Kind=="sibling_quantity")).Select(row=>
                new InstrumentQuantityConnection(payment.Id,row.Output.Ordinal,delivery.Id,root.Name,
                    [..Enumerable.Range(0,input.Document.Grids[row.Output.Grid!.Value].CellCount)],
                    [..Enumerable.Range(0,input.Document.Grids[row.Output.Grid!.Value].CellCount)])).ToImmutableArray();
            var execution=new InstrumentScheduleExecution(contract.Id,members,connections);
            var df=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
            var connected=execution.Evaluate(payment.Id,sources,m.Build,df);
            if(!connected.Value.SourceReceipt!.Cells.Values.OfType<SchedulePortEvidence>().Any()||
                !connected.MemberValues[delivery.Id].SourceReceipt!.Cells.Values.OfType<ScheduleVolumeEvidence>().Any())
                throw new Exception("Instrument must retain provider evidence separately from received port values.");
            Equal(connected.Value.PresentValue.ToExactString(),"7210");
            var masked=execution.Evaluate(payment.Id,sources,m.Build,df,coverage:new(f.From.AddHours(1),f.From.AddHours(3)));
            var provider=masked.MemberValues[delivery.Id];
            var providerEvidence=provider.SourceReceipt!.Cells.Values.OfType<ScheduleVolumeEvidence>().ToArray();
            if(provider.RequestedOutputs.Contains("pv")||provider.SourceReceipt.Cells.Values.OfType<ScheduleDiscountEvidence>().Any()||
                providerEvidence.Length!=120/minutes||providerEvidence.Any(value=>value.Volume.Interval.Start<f.From.AddHours(1)||value.Volume.Interval.End>f.From.AddHours(3)))
                throw new Exception("Quantity-only provider receipts must contain only the instrument's demanded interval cells.");
            Equal(masked.Value.PresentValue.ToExactString(),"20");
            var originalBytes=input.Write();var alternate=f.Physical().Memberships[0].Side;var alternateProgram=Load(alternate);
            var alternateMembers=ImmutableArray.Create(new InstrumentScheduleMember(alternate.Id,alternateProgram,DefinitionIdentity.For(alternate.Terms.Normalization).Canonical,null),members[1]);
            var alternateConnections=connections.Select(connection=>connection with{OutputSide=alternate.Id}).ToImmutableArray();
            Reject("instrument_schedule_connection",()=>new InstrumentScheduleExecution(Guid.NewGuid(),alternateMembers,alternateConnections));
            var another=new InstrumentScheduleMember(Guid.NewGuid(),output,normalization,null);
            alternateMembers=[another,members[1]];alternateConnections=[..connections.Select(connection=>connection with{OutputSide=another.SideId})];
            Equal(new InstrumentScheduleExecution(Guid.NewGuid(),alternateMembers,alternateConnections).Evaluate(payment.Id,sources,m.Build,df).Value.PresentValue.ToExactString(),"7210");
            if(!originalBytes.SequenceEqual(input.Write()))throw new Exception("An instrument connection must not rewrite or specialize the independent receiving Side.");
            var combined=new FixedSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery),new(PpaRole.EnergyDelivery,1)]),normalization:f.Normalization([f.Asset.Key])),
                new FlatPrice(new(ExactDecimal.Parse("40"),f.EnergyPrice)));
            var combinedProgram=Load(combined);var combinedInputs=combinedProgram.Document.Instructions.Where(row=>row.Bindings.Any(binding=>binding.Kind=="sibling_quantity")).ToArray();
            var combinedMembers=ImmutableArray.Create(members[0],alternateMembers[0],new InstrumentScheduleMember(combined.Id,combinedProgram,normalization,null));
            var combinedConnections=combinedInputs.Select((row,n)=>new InstrumentQuantityConnection(combined.Id,row.Output.Ordinal,n==0?delivery.Id:another.SideId,root.Name,
                [..Enumerable.Range(0,combinedProgram.Document.Grids[row.Output.Grid!.Value].CellCount)],
                [..Enumerable.Range(0,combinedProgram.Document.Grids[row.Output.Grid!.Value].CellCount)])).ToImmutableArray();
            Equal(new InstrumentScheduleExecution(Guid.NewGuid(),combinedMembers,combinedConnections).Evaluate(combined.Id,sources,m.Build,df).Value.PresentValue.ToExactString(),"14420");
            var firstBranch=Guid.NewGuid();var secondBranch=Guid.NewGuid();
            var diamondMembers=ImmutableArray.Create(members[0],new InstrumentScheduleMember(firstBranch,input,normalization,null),
                new InstrumentScheduleMember(secondBranch,input,normalization,null),combinedMembers[2]);
            var diamondConnections=connections.Select(connection=>connection with{InputSide=firstBranch})
                .Concat(connections.Select(connection=>connection with{InputSide=secondBranch}))
                .Concat(combinedConnections.Select(connection=>connection with{OutputSide=connection.OutputSide==delivery.Id?firstBranch:secondBranch})).ToImmutableArray();
            var diamond=new InstrumentScheduleExecution(Guid.NewGuid(),diamondMembers,diamondConnections)
                .Evaluate(combined.Id,sources,m.Build,df,coverage:new(f.From.AddHours(1),f.From.AddHours(3)));
            Equal(diamond.Value.PresentValue.ToExactString(),"40");
            if(diamond.MemberValues.Count!=4||diamond.MemberValues.Where(pair=>pair.Key!=combined.Id).Any(pair=>pair.Value.RequestedOutputs.Contains("pv")))
                throw new Exception("A shared upstream member must execute once for merged quantity demands, without pricing intermediate sides.");
            var delivered=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(f.Volume with{Stage=QuantityStage.Delivered}),f.Asset.Location);
            var deliveredMember=new InstrumentScheduleMember(delivered.Id,Load(delivered),normalization,null);
            var mixedConnections=combinedConnections.Select(connection=>connection.OutputSide==another.SideId?connection with{OutputSide=delivered.Id}:connection).ToImmutableArray();
            Reject("ppa_quantity_source",()=>new InstrumentScheduleExecution(Guid.NewGuid(),[members[0],deliveredMember,combinedMembers[2]],mixedConnections).Evaluate(combined.Id,sources,m.Build,df));
            Reject("instrument_schedule_connection",()=>new InstrumentScheduleExecution(contract.Id,members,[]));
            Reject("instrument_schedule_connection",()=>new InstrumentScheduleExecution(contract.Id,members,connections.SetItem(0,connections[0] with{OutputSide=payment.Id})));
            Reject("instrument_schedule_connection",()=>new InstrumentScheduleExecution(contract.Id,members,connections.SetItem(0,connections[0] with{OutputCells=connections[0].OutputCells.SetItem(0,1)})));
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try{execution.Evaluate(payment.Id,sources,m.Build,df,cancelled.Token);throw new Exception("Cancelled instrument execution returned a value.");}
            catch(OperationCanceledException){}
        }
    }

    private static void InstrumentPorts()
    {
        foreach(var minutes in new[]{60,15})
        {
            var f=new PpaAcceptanceFixture(minutes);var m=f.Market(f.From.AddDays(-1));var instrument=f.Physical(volume:true);
            var oracle=f.Prepare(m,[instrument]);var context=m.Build.Graph.Plan.Context;
            var sources=new ScheduleMarketSources(context,m.Build,oracle.Reference,oracle.Read with{Curves=ImmutableDictionary<CurveKey,PreparedCurve>.Empty},
                oracle.KnowledgePolicy,oracle.Policy,[],[]);
            var side=instrument.Memberships[1].Side;var program=Load(side);var demand=program.Inputs(context);
            var ports=new Dictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>>();
            foreach(var input in demand.Sources.Where(input=>program.Document.Instructions[input.Instruction].Bindings.Any(binding=>binding.Kind=="sibling_quantity")))
            {
                var row=program.Document.Instructions[input.Instruction].Output;
                ports.Add(new(input.Instruction,m.Build.Pricing.ScenarioId),new(context,m.Build.Pricing.ScenarioId,
                    ScheduleValueColumn.Input(row,[..Enumerable.Repeat(new ScheduleNumber(minutes,240),input.Uses.Length)],
                        [..Enumerable.Repeat(ScheduleCellState.From(VolumeState.Forecast,null) with{Stage=QuantityStage.Generated},input.Uses.Length)])));
            }
            var df=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
            Equal(sources.Evaluate(demand,side.Id,m.Build,df,ports).PresentValue.ToExactString(),"7210");
            Reject("side_input_port",()=>sources.Evaluate(demand,side.Id,m.Build,df,new Dictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>>()));
            var address=ports.Keys.First();var value=ports[address];
            ports[address]=value with{ScenarioId="another_scenario"};
            Reject("side_input_port",()=>sources.Evaluate(demand,side.Id,m.Build,df,ports));
        }
    }

    private static void FixingLookups()
    {
        var fixture=new IndexFixingChecks.FixingFixture();var side=fixture.Floating();var program=Load(side);
        var lookups=side.GenerateSchedule().Rows.SelectMany(row=>row.Prices).Select(price=>price.Lookup!).Distinct().ToImmutableArray();
        var at=fixture.Fixings[1].FixingAt;
        var snapshot=fixture.Snapshot(at,2).WithKnowledge(Guid.NewGuid(),at);
        var market=fixture.Model.Build(snapshot,side.DependenciesAt(snapshot));
        var context=market.Graph.Plan.Context;var df=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var source=new ScheduleMarketSources(context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,lookups,[]);
        // (10 actual + 20 actual + 33 projected) / 3 = 21, independent of the evaluator.
        Equal(source.Evaluate(program,side.Id,market,df).PresentValue.ToExactString(),"21");
        var factor=fixture.Model.QuoteFactors(snapshot).Single(f=>f.Input.Series==fixture.Series[2].Key);
        var pair=fixture.Model.Scenarios(market,[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("3"),QuoteBump.Convention)]).Single();
        Equal(source.Evaluate(program,side.Id,pair.Up,df).PresentValue.ToExactString(),"22");
        Equal(source.Evaluate(program,side.Id,pair.Down,df).PresentValue.ToExactString(),"20");
        Reject("side_market_sources",()=>new ScheduleMarketSources(context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,
            PendingActualsPolicy.RequireActuals,[],[]).Evaluate(program,side.Id,market,df));
        var wrongContract=lookups.SetItem(0,lookups[0] with{Contract=lookups[0].Contract with{Kind=ContractKind.Future}});
        Reject("swap_definition",()=>new ScheduleMarketSources(context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,
            PendingActualsPolicy.RequireActuals,wrongContract,[]).Evaluate(program,side.Id,market,df));
        var ambiguous=lookups.Add(lookups[0] with{Fixing=lookups[0].Fixing! with{FixingAt=lookups[0].Fixing!.FixingAt.AddMinutes(1)}});
        Reject("side_market_sources",()=>new ScheduleMarketSources(context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,
            PendingActualsPolicy.RequireActuals,ambiguous,[]).Evaluate(program,side.Id,market,df));
        var missing=fixture.Snapshot(at,1).WithKnowledge(Guid.NewGuid(),at);
        var missingMarket=fixture.Model.Build(missing,side.DependenciesAt(missing));
        Reject("index_fixing_missing",()=>new ScheduleMarketSources(missingMarket.Graph.Plan.Context,missingMarket,null,null,IntervalKnowledgePolicy.ResolvedCutoff,
            PendingActualsPolicy.RequireActuals,lookups,[]).Evaluate(program,side.Id,missingMarket,df));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{source.Evaluate(program,side.Id,market,df,cancelled.Token);throw new Exception("Cancellation returned a successful price.");}
        catch(OperationCanceledException){}
    }
    private static void NativeIndexedCadence()
    {
        var f=new PpaAcceptanceFixture();var market=f.Market(f.From.AddDays(-1));var data=f.Prepare(market,[f.Physical()]);
        var key=f.C("synthetic_energy");var selection=PriceSettlementSelection.CurveTarget(f.R("synthetic_energy_index"));
        var periodCurve=new PeriodContractCurve(key,f.EnergyPrice,selection.SelectedIndex,market.Curves[key].Period(f.Month).Contracts,[]);
        var period=PreparedCurve.Prepare(periodCurve,
            [(new DateTimeOffset(f.Month.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero),new DateTimeOffset(f.Month.AddMonths(1).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero))],
            [],[],market.Snapshot.ValuationAt,market.Snapshot.Knowledge!.KnownAt);
        var side=new FloatingSide(Guid.NewGuid(),f.Terms(f.Profile),new(key,ExactDecimal.Parse("1"),new(default,f.EnergyPrice),selection));
        var source=new ScheduleMarketSources(market.Build.Graph.Plan.Context,market.Build,data.Reference,
            data.Read with{Curves=data.Read.Curves.SetItem(key,period)},data.KnowledgePolicy,data.Policy,[],[selection]);
        var program=Load(side);var df=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        Reject("ppa_cadence",()=>source.Evaluate(program,side.Id,market.Build,df,nativePriceAttribute:ValuedAttribute.Energy));
        Reject("ppa_quantity_unit",()=>source.Evaluate(program,side.Id,market.Build,df,nativePriceAttribute:ValuedAttribute.Capacity));
        var nativeSide=new FloatingSide(Guid.NewGuid(),f.Terms(f.Profile),new(key,ExactDecimal.Parse("2"),new(ExactDecimal.Parse("3"),f.EnergyPrice),null));
        var nativeProgram=Load(nativeSide);
        var nativeSource=new ScheduleMarketSources(market.Build.Graph.Plan.Context,market.Build,data.Reference,data.Read,data.KnowledgePolicy,data.Policy,[],[]);
        Reject("side_market_sources",()=>nativeSource.Evaluate(nativeProgram,nativeSide.Id,market.Build,df));
        Equal(nativeSource.Evaluate(nativeProgram,nativeSide.Id,market.Build,df,nativePriceAttribute:ValuedAttribute.Energy).PresentValue.ToExactString(),"62727");
    }
    private static void CertificateSources()
    {
        var f=new PpaAcceptanceFixture();var market=f.Market(f.To.AddDays(20),actuals:_=>"42",capacityFixing:true);
        var instrument=f.WithCertificate(cash:false);var data=f.Prepare(market,[instrument],meter:f.Meter(_=>"50",learned:f.To));
        var side=instrument.Memberships[2].Side;var program=Load(side);
        var source=new ScheduleMarketSources(market.Build.Graph.Plan.Context,market.Build,data.Reference,data.Read,data.KnowledgePolicy,data.Policy,[],[]);
        var df=new ExplicitDiscounting(ExactDecimal.Parse("0.9"),DiscountFactorSource.Explicit);
        // 180.25 delivered certificates are held at 5, without payment discounting.
        Equal(source.Evaluate(program,side.Id,market.Build,df).PresentValue.ToExactString(),"901.25");
        var pair=market.Model.Scenarios(market.Build,[market.Bumps["synthetic_certificate"]]).Single();
        Equal(source.Evaluate(program,side.Id,pair.Up,df).PresentValue.ToExactString(),"1081.5");
        Equal(source.Evaluate(program,side.Id,pair.Down,df).PresentValue.ToExactString(),"721");
    }
    private static void PhysicalQuoteScale()
    {
        var f=new PpaAcceptanceFixture();var market=f.Market(f.From.AddDays(-1),energyQuoteMultiplier:ExactDecimal.Parse("0.1"));
        var instrument=f.Physical(volume:true);
        var assignments=f.Assignments.Select(value=>value.Attribute==ValuedAttribute.Energy?
            value with{Unit=new(f.EnergyPrice.Currency,f.EnergyPrice.Quantity,ExactDecimal.Parse("0.1")),Adjustment=ExactDecimal.Parse("2")}:value).ToImmutableArray();
        var data=f.Prepare(market,[instrument],assignments:assignments);var side=instrument.Memberships[0].Side;
        var source=new ScheduleMarketSources(market.Build.Graph.Plan.Context,market.Build,data.Reference,data.Read,data.KnowledgePolicy,data.Policy,[],[]);
        Equal(source.Evaluate(Load(side),side.Id,market.Build,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))
            .PresentValue.ToExactString(),"793.1"); //721×0.25×(42+2)×0.1
    }
    private static void HistoricalSources()
    {
        var f=new PpaAcceptanceFixture();var at=f.To.AddDays(20);
        var market=f.Market(at,actuals:_=>"42",capacityFixing:true);var instrument=f.Physical(volume:true);
        var data=f.Prepare(market,[instrument],meter:f.Meter(_=>"50",learned:f.To));
        var source=new ScheduleMarketSources(market.Build.Graph.Plan.Context,market.Build,data.Reference,data.Read,data.KnowledgePolicy,data.Policy,[],[]);
        var side=instrument.Memberships[0].Side;var program=Load(side);
        var df=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var result=source.Evaluate(program,side.Id,market.Build,df);
        CheckReceipt(result,program);
        var volumes=result.SourceReceipt!.Cells.Values.OfType<ScheduleVolumeEvidence>().ToArray();
        var prices=result.SourceReceipt.Cells.Values.OfType<ScheduleNativePriceEvidence>().ToArray();
        if(volumes.Length==0||prices.Length==0||volumes.Any(value=>value.Volume.State!=VolumeState.Metered||value.Volume.KnownAt!=f.To)||
            prices.Any(value=>value.Resolution.Actual is null||value.Resolution.State!=IntervalPriceState.Actual))
            throw new Exception("Historical receipts must retain selected meter knowledge and actual price observations.");
        Equal(result.PresentValue.ToExactString(),"0");
        // Physical delivery is outside PV after delivery/value date; cash evidence remains 721×50/200×42.
        var amounts=result.Contributions.SelectMany(c=>c.Amount.Values).ToArray();
        var cash=amounts.Aggregate(ExactMoney.FromFraction(0,1,instrument.Currency),(sum,value)=>sum.Add(ExactMoney.FromFraction(value.Numerator,value.Denominator,instrument.Currency)));
        Equal(cash.ToExactString(),"7570.5");
        if(result.Contributions.SelectMany(c=>c.Amount.States).Any(state=>state.Quantity!=VolumeState.Metered||state.Prices!=SchedulePriceFacts.Actual))
            throw new Exception("Delivered quantities and prices must retain their actual states.");
    }
    private static void CheckReceipt(BookedSideEvaluation value,BookedSideProgram program)
    {
        var receipt=value.SourceReceipt??throw new Exception("Missing source receipt.");
        var expected=new HashSet<ScheduleSourceCell>();
        foreach(var source in program.Inputs(value.Context,coverage:value.Coverage).Sources)
        foreach(var lane in new[]{(Id:value.BaseScenarioId,Mask:ScheduleInputLanes.Base),(Id:value.ScenarioId,Mask:ScheduleInputLanes.Scenario)})
        for(var cell=0;cell<source.Uses.Length;cell++)
            if((source.Uses[cell]&lane.Mask)!=0)expected.Add(new(source.Instruction,cell,lane.Id));
        if(!expected.SetEquals(receipt.Cells.Keys))throw new Exception("Receipt must cover exactly the demanded source cells and lanes.");
        Equal(receipt.Program.ContentDigest,BookedSideProgram.Digest(program.Write()));
    }
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
    private static BookedSideProgram Load(Side side)
    {
        var definition=ScheduleDefinitionCompiler.Generate(side);var bytes=BookedSideProgram.Compile(definition).Write();
        return BookedSideProgram.Read(bytes,new(BookedSideProgram.Digest(bytes),definition.Manifest,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity));
    }
    private static void Equal(string actual,string expected)
    {if(actual!=expected)throw new Exception($"Expected {expected}, received {actual}.");}
}
