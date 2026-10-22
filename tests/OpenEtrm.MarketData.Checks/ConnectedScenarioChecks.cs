using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class ConnectedScenarioChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string label,Action test){test();count++;Console.WriteLine($"PASS connected scenarios: {label}");}
        void Equal(ExactUnitPrice price,string numerator,string denominator="1")
        {if(price.Numerator!=numerator||price.Denominator!=denominator)throw new Exception($"Expected {numerator}/{denominator}; got {price.Numerator}/{price.Denominator}.");}
        void Reject(string code,Action run)
        {try{run();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception($"Expected {code}.");}
        var f=new ConnectedCurveChecks.Fixture();
        var roots=f.Definitions.SelectMany(d=>d.Points).Select(p=>p.Key).ToImmutableArray();
        var plan=ConnectedCurves.Compile(f.Definitions,roots,f.Context,f.Resolver);
        var basis=ConnectedScenario.Base(plan);
        var raw=f.RawPoints;var unit=raw[0].Unit;
        CurveShift Shift(CurveShockCoordinate coordinate,CurveShockTarget target,string amount)=>
            new(coordinate,target,unit,ExactDecimal.Parse(amount),CurveShift.ParentAdditive);
        var first=Shift(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(raw[0].Key.Curve,raw[0].Contract!.Key),"3");
        var output=Shift(CurveShockCoordinate.ComputedOutput,new ParallelCurveTarget(f.Mean.Key.Curve),"2");
        Check("a contract shock flows through mean, basket and ordinary instrument PV",()=>
        {
            Equal(basis.Price(f.Spread.Key),"11","3");
            var parent=basis.WithShifts([first]);
            // (4+2+4)/3; 2*(10/3)-4 = 8/3. Dependency does not imply positive exposure.
            Equal(parent.Price(f.Mean.Key),"10","3");Equal(parent.Price(f.Spread.Key),"8","3");
            var request=new CurveRequest(f.Spread.Key.Curve,raw[0].Contract!.Key);
            var instrument=new CurvePv(Guid.NewGuid(),unit,[request],context=>context.ExactPrice(request).ExactTimes(new(ExactDecimal.Parse("1"),unit.Quantity)));
            var bindings=ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(request,f.Spread.Key);
            var snapshot=Snapshot(f);
            Reject("instrument_context",()=>ConnectedValuation.Prepare(basis,snapshot with{Definitions=default}));
            Reject("instrument_context",()=>ConnectedValuation.Prepare(basis,snapshot with{ValuationAt=snapshot.ValuationAt.ToOffset(TimeSpan.FromHours(1))}));
            Reject("instrument_context",()=>ConnectedValuation.Prepare(basis,snapshot with{MarketCutoff=snapshot.MarketCutoff.ToOffset(TimeSpan.FromHours(1))}));
            var prepared=ConnectedValuation.Prepare(basis,snapshot);
            var before=prepared.PV(instrument,bindings);
            var after=prepared.WithScenario(parent).PV(instrument,bindings);
            if(before.PresentValue.Numerator!="11"||before.PresentValue.Denominator!="3"||after.PresentValue.Numerator!="8"||after.PresentValue.Denominator!="3")
                throw new Exception("Common Instrument.PV must consume the active exact scenario without copied prices.");
            var stale=instrument with{Saved=before};
            Reject("instrument_context",()=>prepared.WithScenario(parent).PV(stale,bindings));
            var hidden=new CurvePv(Guid.NewGuid(),unit,[],context=>context.ExactPrice(request).ExactTimes(new(ExactDecimal.Parse("1"),unit.Quantity)));
            Reject("risk_dependency",()=>prepared.WithScenario(parent).PV(hidden,ImmutableDictionary<CurveRequest,CurvePointKey>.Empty));
            var otherPlan=ConnectedCurves.Compile(f.Definitions,roots,f.Context,f.Resolver);
            Reject("instrument_context",()=>prepared.WithScenario(ConnectedScenario.Base(otherPlan)));
        });
        Check("nested output shifts retain parent input shocks and never alter ancestors",()=>
        {
            var parent=basis.WithShifts([first]);var inner=parent.WithShifts([output]);
            Equal(inner.Price(raw[0].Key),"4");Equal(inner.Price(f.Mean.Key),"16","3");Equal(inner.Price(f.Spread.Key),"20","3");
            Equal(parent.Price(f.Spread.Key),"8","3");Equal(basis.Price(f.Spread.Key),"11","3");
            var inverse=inner.WithShifts([output with{Size=ExactDecimal.Parse("-2")}]);
            Equal(inverse.Price(f.Spread.Key),"8","3");
            // An inherited output shift also survives a later upstream quote change.
            Equal(basis.WithShifts([output]).WithShifts([first]).Price(f.Spread.Key),"20","3");
        });
        Check("parallel quote moves and exact dated buckets select distinct work",()=>
        {
            var parallel=Shift(CurveShockCoordinate.MarketQuote,new ParallelCurveTarget(raw[0].Key.Curve),"1");
            Equal(basis.WithShifts([parallel]).Price(f.Spread.Key),"14","3");
            var bucket=Shift(CurveShockCoordinate.MarketQuote,new DeliveryBucketCurveTarget(raw[0].Key.Curve,
                raw[0].DeliveryFrom,raw[0].DeliveryTo,DeliveryBucketCurveTarget.WholeDelivery),"3");
            Equal(basis.WithShifts([bucket]).Price(f.Spread.Key),"8","3");
            Reject("scenario_bucket",()=>basis.WithShifts([bucket with{Target=new DeliveryBucketCurveTarget(raw[0].Key.Curve,
                raw[0].DeliveryFrom.AddDays(1),raw[0].DeliveryTo,DeliveryBucketCurveTarget.WholeDelivery)}]));
            Reject("scenario_target",()=>basis.WithShifts([first with{Target=new ContractCurveTarget(raw[0].Key.Curve,new(raw[0].Contract!.Key.Revision,Guid.NewGuid()))}]));
        });
        Check("individual forward contracts use the same explicit input-shock contract",()=>
        {
            var source=(ConnectedSource)raw[0].Expression;
            var contract=raw[0].Contract! with{Key=new(raw[0].Contract!.Key.Revision,Guid.NewGuid()),Kind=ContractKind.Forward};
            var forward=raw[0] with{Contract=contract,Expression=source with{Request=new(source.Request.Curve,contract.Key),Series=source.Series with{Contract=contract.Key}}};
            var definitions=f.Definitions.SetItem(0,f.Definitions[0] with{Points=raw.SetItem(0,forward)});
            var current=ConnectedScenario.Base(ConnectedCurves.Compile(definitions,roots,f.Context,f.Resolver));
            var move=first with{Target=new ContractCurveTarget(forward.Key.Curve,contract.Key)};
            Equal(current.WithShifts([move]).Price(f.Spread.Key),"8","3");
            Equal(current.Price(forward.Key),"1");
        });
        Check("abstract output buckets shift selected computed points while holding inputs fixed",()=>
        {
            var key=new CurveKey(f.Mean.Key.Curve.Revision,"computed_buckets");
            ConnectedCurvePoint Point(int i)=>new(new(key,Guid.NewGuid()),raw[i].DeliveryFrom,raw[i].DeliveryTo,unit,
                new ConnectedMean([CurvePointReference.Exact(raw[i].Key),CurvePointReference.Exact(raw[i+1].Key)],ConnectedCurves.UniformMean));
            var a=Point(0);var b=Point(1);
            var definition=new ConnectedCurveDefinition(key,[a,b]);
            var current=ConnectedScenario.Base(ConnectedCurves.Compile(f.Definitions.Add(definition),[a.Key,b.Key],f.Context,f.Resolver));
            var bucket=Shift(CurveShockCoordinate.ComputedOutput,new DeliveryBucketCurveTarget(key,a.DeliveryFrom,a.DeliveryTo,DeliveryBucketCurveTarget.WholeDelivery),"0.5");
            var moved=current.WithShifts([bucket]);Equal(moved.Price(a.Key),"2");Equal(moved.Price(b.Key),"3");
            Equal(moved.Price(raw[0].Key),"1");Equal(moved.Price(raw[1].Key),"2");
            var all=current.WithShifts([bucket with{Target=new ParallelCurveTarget(key)}]);Equal(all.Price(a.Key),"2");Equal(all.Price(b.Key),"7","2");
        });
        Check("invalid coordinates, frozen inputs, overlaps and unit mistakes fail explicitly",()=>
        {
            Reject("scenario_coordinate",()=>basis.WithShifts([output with{Coordinate=CurveShockCoordinate.MarketQuote}]));
            Reject("scenario_coordinate",()=>basis.WithShifts([first with{Coordinate=CurveShockCoordinate.ComputedOutput}]));
            Reject("scenario_coordinate",()=>basis.WithShifts([first with{Coordinate=(CurveShockCoordinate)99}]));
            Reject("scenario_shift",()=>basis.WithShifts([first with{Size=default}]));
            Reject("scenario_shift",()=>basis.WithShifts([first with{Convention="unselected"}]));
            Reject("scenario_target",()=>basis.WithShifts([first,first]));
            Reject("unit_mismatch",()=>basis.WithShifts([first with{Unit=new(unit.Currency,unit.Quantity,ExactDecimal.Parse("100"))}]));
            Reject("price_domain",()=>basis.WithShifts([first with{Size=ExactDecimal.Parse("1000")}]));
            var source=(ConnectedSource)raw[0].Expression;
            var frozen=raw[0] with{Expression=source with{Input=source.Input with{Use=InputUse.FrozenFact}}};
            var definitions=f.Definitions.SetItem(0,f.Definitions[0] with{Points=raw.SetItem(0,frozen)});
            var fixedScenario=ConnectedScenario.Base(ConnectedCurves.Compile(definitions,roots,f.Context,f.Resolver));
            Reject("scenario_coordinate",()=>fixedScenario.WithShifts([first]));
        });
        Check("scenario identity retains exact context and is independent of request ordering",()=>
        {
            var second=first with{Target=new ContractCurveTarget(raw[0].Key.Curve,raw[1].Contract!.Key),Size=ExactDecimal.Parse("1")};
            if(basis.WithShifts([first,second]).Id!=basis.WithShifts([second,first]).Id)
                throw new Exception("Equivalent declared layers require stable canonical identities.");
            if(basis.WithShifts([first]).Id==basis.WithShifts([second]).Id)throw new Exception("Different target contracts must not share scenario identity.");
            if(basis.Id==basis.WithShifts([first]).Id)throw new Exception("A child scenario must retain its own identity.");
        });
        Check("reordering aliases of one selected primitive preserves scenario identity",()=>
        {
            var alias=raw[0] with{Key=new(raw[0].Key.Curve,Guid.NewGuid())};
            var definitions=f.Definitions.SetItem(0,f.Definitions[0] with{Points=raw.Add(alias)});
            var current=ConnectedScenario.Base(ConnectedCurves.Compile(definitions,roots.Add(alias.Key),f.Context,f.Resolver));
            var a=first with{Target=new PointCurveTarget(raw[0].Key.Curve,[raw[0].Key.PointId,alias.Key.PointId])};
            var b=a with{Target=new PointCurveTarget(raw[0].Key.Curve,[alias.Key.PointId,raw[0].Key.PointId])};
            if(current.WithShifts([a]).Id!=current.WithShifts([b]).Id)
                throw new Exception("A selected set of source aliases must not depend on request ordering.");
        });
        return count;
    }

    private static MarketSnapshot Snapshot(ConnectedCurveChecks.Fixture f)=>new(f.Context.ContextId,f.RawPoints[0].Key.Curve.Revision,
        f.Context.ValuationAt,f.Context.MarketAsOf,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
        f.RawPoints.Select(p=>(ConnectedSource)p.Expression).Select(s=>new SelectedObservation(s.Input,s.Observation)).ToImmutableArray());

    private sealed record CurvePv : Instrument
    {
        private readonly ImmutableArray<CurveRequest> dependencies;
        private readonly Func<SideValuationContext,ExactMoney> calculate;
        public override ReferenceKey Currency{get;}
        public override Quantity UnitQuantity{get;}
        public InstrumentValuation? Saved{get;init;}
        public CurvePv(Guid id,PriceUnit unit,ImmutableArray<CurveRequest> dependencies,Func<SideValuationContext,ExactMoney> calculate):base(id)
        {Currency=unit.Currency;UnitQuantity=new(ExactDecimal.Parse("1"),unit.Quantity);this.dependencies=dependencies;this.calculate=calculate;}
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"synthetic_connected_curve_pv_v1",dependencies,UnitQuantity.Specification,Currency);
        public override InstrumentValuation PV(SideValuationContext context)=>Saved??new(this,context,calculate(context),"synthetic_connected_curve_pv_v1");
    }
}
