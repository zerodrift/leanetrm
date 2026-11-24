using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasSourceGeometryChecks
{
    internal static void Run()
    {
        foreach(var laterClock in new[]{false,true})
        foreach(var follows in new[]{false,true})
        {
            var f=new Sources(laterClock,follows);var context=f.Context(f.Instrument);
            var result=PhysicalLifecycle.FromValuation(f.Instrument.PV(context));
            var first=result.Rows.Single(r=>r.Slot==2);var second=result.Rows.Single(r=>r.Slot==3);
            Equal(first.Accrued,"-6000");Equal(first.Unrealized,"-3000");
            Equal(second.Accrued,laterClock?"-4000":"-8000");Equal(second.Unrealized,laterClock?"-4000":"0");
            Equal(result.RealizedUnsettled,laterClock?"-10000":"-14000");
            Equal(result.Unrealized,laterClock?"2600":"3400");
            // Reusing this prepared occurrence is safe; another source map on
            // identical legacy programs must not consume its geometry or cache.
            if(!follows)
            {
                var changed=new RetainedGasInstrument(f.Instrument.Id,f.Instrument.Members,f.Instrument.Assignments,
                    physicalSources:[0,1,1,1]);
                Require(changed.EconomicIdentity==f.Instrument.EconomicIdentity,"Source execution wiring preserves legacy canonical bytes.");
                Require(changed.RetainedScheduleIdentity!=f.Instrument.RetainedScheduleIdentity,"The source map belongs to the schedule's reuse fence.");
                Refused("instrument_identity",()=>f.Instrument.RequireSameDefinition(changed));
                Refused(PhysicalLifecycle.ContextRefusal,()=>changed.PV(context));
            }
            Equal(PhysicalLifecycle.FromValuation(f.Instrument.PV(context)).Total,result.Total.ToExactString());
            Require(context.EvaluatedSides==4,"An unchanged source map retains the four occurrence calculations.");
        }
        Console.WriteLine("PASS gas source geometry: independent partial terms and clocks, exact accrual, legacy identity and source-map reuse fences");
    }
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Equal(ExactMoney amount,string expected)=>Require(amount.ToExactString()==expected,$"Expected {expected}, got {amount.ToExactString()}.");
    static void Refused(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}

    sealed class Sources
    {
        readonly Fixture f=new();
        readonly CurveModel model;
        readonly MarketSnapshot snapshot;
        readonly PhysicalCashState state;
        internal RetainedGasInstrument Instrument {get;}
        internal Sources(bool laterClock,bool follows)
        {
            var from=new DateOnly(2041,1,1);var to=from.AddDays(4);var quote=f.Quotes[0];
            var clock=GasDayClock.Capture(f.R("synthetic_first_clock"),TimeZoneInfo.Utc,360);
            var secondClock=GasDayClock.Capture(f.R("synthetic_second_clock"),TimeZoneInfo.Utc,laterClock?1080:360);
            var contracts=Enumerable.Range(0,4).Select(n=>new MarketContract(f.K(100+n),ContractKind.Forward,
                from.AddDays(n),from.AddDays(n+1),clock.Boundary(to).AddDays(5),null)).ToImmutableArray();
            var series=contracts.Select((c,n)=>new MarketSeries(f.S(100+n),c.Key,quote.Key,null)).ToImmutableArray();
            var curve=f.Direct("synthetic_source_geometry",contracts[0].Key,quote.Key,SettlementKind.Index) with{Constituents=[..contracts.Select(c=>c.Key)]};
            model=new(f.Revision,f.Reference,[f.Quantities[0]],[quote],contracts,[curve],[],[],series);
            var location=new GasDeliveryLocation(f.R("pipeline"),f.R("zone"),f.R("point"));
            var assignments=contracts.Select((c,n)=>new GasValuationAssignment(f.R("synthetic_assignment_"+n),location,f.R("gas"),
                c.DeliveryFrom,c.DeliveryTo,PriceSettlementSelection.CurveTarget(f.R("index")),quote,new(new(curve.Key,c.Key),c),
                new(f.R("index"),quote.Key,c.DeliveryFrom,c.DeliveryTo,clock.Boundary(c.DeliveryTo),series[n].Key,f.R("publisher"),IndexFixing.SupportedImplementation),new(default,quote))).ToImmutableArray();
            PhysicalGasSide Physical(GasDayClock selected,int count)=>new(Guid.NewGuid(),location,f.R("gas"),
                new(selected,from,from.AddDays(count),GasQuantityBasis.PerOperatingDay,new(ExactDecimal.Parse("1"),quote.Quantity),GasFuelConvention.None,default),
                [..assignments.Take(count)],SyntheticSideCalendars.Physical());
            FixedSide Cash(GasDayClock selected,int count,string price)=>new(Guid.NewGuid(),new FlatPrice(new UnitPrice(ExactDecimal.Parse(price),quote)),
                quote,new(ExactDecimal.Parse("1"),quote.Quantity),[new(from,from.AddDays(count),clock.Boundary(to).AddDays(2),new(count,1),null)
                {DeliveryCompletionAt=selected.Boundary(from.AddDays(count))}],SyntheticSideCalendars.Dated(false),gasDeliveryClock:selected);
            Side[] sides=[Physical(clock,4),Physical(secondClock,2),Cash(clock,3,"3"),Cash(secondClock,2,"4")];
            var members=sides.Select((side,n)=>new RetainedGasMember(side.Id,ExactDecimal.Parse(n<2?"1":"-1"),ExactRatio.One,
                BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,lifecycle:ScheduleLifecycleSelection.StartedPhysicalDelivery)),
                follows?n%2:null,follows&&n<2?GasHeldQuantities.DeliverySchedulePolicy:null)).ToImmutableArray();
            Instrument=new(Guid.NewGuid(),members,assignments,physicalSources:follows?default:[0,1,0,1]);
            var at=clock.Boundary(from.AddDays(2)).AddHours(1);
            snapshot=new MarketSnapshot(Guid.NewGuid(),f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                [..series.Select(s=>new SelectedObservation(new(s.Key,InputUse.Variable),new(new(f.Revision.CatalogId,Guid.NewGuid()),s.Key,f.R("source"),
                    ObservationRole.TraderMark,PublicationState.Final,clock.Boundary(from).AddDays(-1),ExactDecimal.Parse("3.2"))))]).WithKnowledge(Guid.NewGuid(),at);
            var quantity=new Quantity(ExactDecimal.Parse("1000"),quote.Quantity);var exact=ExactQuantity.FromQuantity(quantity);
            ImmutableArray<HeldDayQuantity> ownerDays=follows?
                [..Enumerable.Range(0,2).SelectMany(slot=>Enumerable.Range(0,slot==0?4:2)
                    .Select(day=>new HeldDayQuantity(slot,from.AddDays(day),exact,exact,PhysicalLifecycle.ContractQuantity,[])))]:[];
            state=new(Guid.NewGuid(),Instrument.Id,quantity,Guid.NewGuid(),[],PhysicalCashState.Policy,ownerDays);
        }
        internal SideValuationContext Context(RetainedGasInstrument instrument)
        {
            var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discount);
            var unit=new UnitValuationContext(snapshot,setup,bindingState:state);
            var market=model.Build(snapshot,instrument.Declare(unit).Dependencies);
            return instrument.PrepareValuation(market,market,discount,state);
        }
    }
}
