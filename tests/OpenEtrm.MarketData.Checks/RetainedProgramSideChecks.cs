using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class RetainedProgramSideChecks
{
    internal static void Operating(RetainedOperatingInstrument instrument,CurveModel model,MarketSnapshot snapshot,QuoteBump bump)
    {
        RetainedInstrumentContractChecks.Operating(instrument);
        var memberships=instrument.Sides;
        if(memberships.Length!=3||memberships.Any(m=>m.Side is not RetainedProgramSide)||
            !ReferenceEquals(memberships[0].Side,instrument.Sides[0].Side))
            throw new Exception("A retained operating right must retain stable concrete Side members.");
        for(var n=0;n<memberships.Length;n++)
        {
            var side=(RetainedProgramSide)memberships[n].Side;
            if(side.Id!=instrument.Members[n].SideId||!ReferenceEquals(side.Program,instrument.Members[n].Program)||
                !ReferenceEquals(side.GenerateSchedule().Program,side.Program)||side.ProgramDigest!=instrument.Members[n].ProgramDigest||
                !side.GenerateSchedule().PriceTerms.IsEmpty||
                memberships[n].Coefficient!=instrument.Members[n].Coefficient)
                throw new Exception("Side membership must retain the exact sealed program, identity and sign without recompilation.");
            _=DefinitionIdentity.For(side);
        }
        new InstrumentIdentityMap(Guid.NewGuid()).Retain(instrument);
        Refuse("instrument_definition",()=>{SidedInstrument projected=instrument;_=projected with{Sides=[new(memberships[0].Side,ExactDecimal.Parse("-1"))]};});
        var scenarios=model.BuildScenarios(snapshot,instrument.DependenciesAt(snapshot),[bump]);
        var one=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var context=SideValuationContext.ForRetainedPrograms(scenarios.Base,scenarios.Base,one);
        var first=(RetainedProgramSide)memberships[0].Side;
        var independent=RetainedProgramSide.ForOperating(first.Id,first.Program,instrument.UnitQuantity,instrument.Currency);
        Equal(independent.PV(context).PresentValue,"300");
        Refuse("retained_side",()=>RetainedProgramSide.ForOperating(first.Id,first.Program,new(ExactDecimal.Parse("1"),new(instrument.UnitQuantity.Specification.Revision,"wrong_unit")),instrument.Currency));
        Refuse("retained_side",()=>RetainedProgramSide.ForOperating(first.Id,first.Program,instrument.UnitQuantity,new(instrument.Currency.Revision,"wrong_currency")));
        Equal(first.PV(context).PresentValue,"300");
        var complete=instrument.PV(context);
        Equal(complete.PresentValue,"185");
        if(complete.Sides.Length!=3||context.EvaluatedSides!=3)
            throw new Exception("Ordinary Instrument PV must aggregate the three independently evaluated Sides.");
        Equal(complete.Sides[1].Value.PresentValue,"105");
        Equal(complete.Sides[1].Contribution,"-105");
        var other=new AssemblyInstrument(Guid.NewGuid(),instrument.UnitQuantity,instrument.Currency,
            [new(memberships[0].Side,ExactDecimal.Parse("-1")),new(memberships[1].Side,ExactDecimal.Parse("1"))]);
        Equal(other.PV(context).PresentValue,"-195");
        if(context.EvaluatedSides!=3)
            throw new Exception("Reusing a Side in another explicit assembly must reuse its context-bound valuation.");
        var repeated=new RetainedOperatingInstrument(Guid.NewGuid(),instrument.Resource,instrument.UnitQuantity,instrument.Currency,
            instrument.Members.Add(new(first.Id,ExactDecimal.Parse("-1"),first.Program)));
        if(!ReferenceEquals(repeated.Sides[0].Side,repeated.Sides[3].Side))
            throw new Exception("Repeated memberships of one sealed Side must reuse the concrete Side without merging their signs.");
        var repeatedContext=SideValuationContext.ForRetainedPrograms(scenarios.Base,scenarios.Base,one);
        var repeatedValue=repeated.PV(repeatedContext);
        Equal(repeatedValue.PresentValue,"-115");
        if(repeatedContext.EvaluatedSides!=3||repeatedValue.Sides.Length!=4||repeatedValue.Sides[3].Contribution.ToExactString()!="-300")
            throw new Exception("Independent repeated memberships must each contribute once while the common Side evaluates once.");
        Refuse("side_identity",()=>{_=new RetainedOperatingInstrument(Guid.NewGuid(),instrument.Resource,instrument.UnitQuantity,instrument.Currency,
            instrument.Members.Add(new(first.Id,ExactDecimal.Parse("-1"),instrument.Members[1].Program))).Sides;});
        var state=new PhysicalCashState(Guid.NewGuid(),Guid.NewGuid(),instrument.UnitQuantity,null,[],PhysicalCashState.Policy);
        Refuse(PhysicalLifecycle.ContextRefusal,()=>instrument.PV(context.WithBindingState(state)));
        Refuse("settlement_scope",()=>instrument.PV(SideValuationContext.ForRetainedPrograms(scenarios.Base,scenarios.Base,one)
            .WithBindingState(new UnsupportedState(Guid.NewGuid(),Guid.NewGuid(),null,instrument.UnitQuantity))));
        var inspected=complete.Sides[0].Value.InspectSchedule(first.Program.Document.ScheduleManifest,ExactDecimal.Parse("1"),new SignedRatio(1,1),
            date=>new(date.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero));
        var row=inspected.Read(0,8).Rows.Single();
        if(row.Quantity!="5"||row.Amount!="300"||row.PresentValue!="300"||
            inspected.Calculation(row.Key,0,32).Rows.Single(r=>r.Operation==OperatingContractScheduleSource.Operation).Lineage.IsEmpty)
            throw new Exception("Concrete retained Side inspection must expose original quantities, gross cash and exact source lineage.");
        var up=SideValuationContext.ForRetainedPrograms(scenarios.Base,scenarios.Factors[0].Up,one);
        Equal(instrument.PV(up).PresentValue,"190");
        Equal(instrument.PV(context).PresentValue,"185");
        var half=SideValuationContext.ForRetainedPrograms(scenarios.Base,scenarios.Base,new ExplicitDiscounting(ExactDecimal.Parse("0.5"),DiscountFactorSource.Explicit));
        Equal(instrument.PV(half).PresentValue,"92.5");
        Equal(instrument.PV(context).PresentValue,"185");
        Refuse("native_program_context",()=>first.PV(new SideValuationContext(scenarios.Base,one)));
        var changed=snapshot with{ValuationAt=snapshot.ValuationAt.AddMinutes(1)};
        Refuse("native_program_context",()=>SideValuationContext.ForRetainedPrograms(scenarios.Base,model.Build(changed,instrument.Dependencies),one));
        var start=instrument.Members.SelectMany(m=>m.Program.Document.Instructions).Where(i=>i.Output.Operation=="output_guard").Min(i=>i.Output.Interval!.Start);
        var started=(snapshot with{ValuationAt=start}).WithKnowledge(Guid.NewGuid(),start);
        var startedMarket=model.Build(started,instrument.DependenciesAt(started));
        var startedContext=SideValuationContext.ForRetainedPrograms(startedMarket,startedMarket,one);
        // These payment Side programs still have future cash. The full context
        // can price them, but the operating right needs its unsupported started
        // operation lifecycle rather than their unadjusted contractual sum.
        Equal(new AssemblyInstrument(Guid.NewGuid(),instrument.UnitQuantity,instrument.Currency,memberships).PV(startedContext).PresentValue,"185");
        Refuse("operating_delivery_lifecycle",()=>instrument.PV(startedContext));
        Console.WriteLine("PASS retained concrete Sides: independent schedule/PV/inspection, signed assembly, reuse and scenario/discount isolation");
    }
    private static void Equal(ExactMoney actual,string expected)
    {if(actual.ToExactString()!=expected)throw new Exception($"Expected {expected}; received {actual.ToExactString()}.");}
    private static void Refuse(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
    [EconomicDefinition("synthetic_retained_side_assembly_v1")]
    private sealed record AssemblyInstrument:SidedInstrument
    {
        public override Quantity UnitQuantity {get;}
        public override ReferenceKey Currency {get;}
        internal AssemblyInstrument(Guid id,Quantity unit,ReferenceKey currency,ImmutableArray<SideMembership> members):base(id,members)
        {UnitQuantity=unit;Currency=currency;}
    }
    private sealed record UnsupportedState(Guid TradeId,Guid BindingId,Guid? BookId,Quantity Quantity):IBindingValuationState;
}
