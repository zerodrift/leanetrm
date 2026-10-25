using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class TemporalBindingChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action test){test();count++;Console.WriteLine($"PASS temporal bindings: {name}");}
        void Equal<T>(T actual,T expected) where T:notnull
        {if(!actual.Equals(expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(string code,Action action)
        {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception($"Expected {code}.");}
        var catalog=Guid.Parse("10000000-0000-0000-0000-000000000001");
        var logical=new LogicalCurveKey(catalog,"synthetic_curve");
        CurveKey Version(int version)=>new(new(catalog,Guid.Parse($"20000000-0000-0000-0000-{version:D12}")),"synthetic_curve");
        var old=Version(1);var correction=Version(2);
        var start=At(1);var amended=At(5);var known=At(10);var end=At(30);
        CurvePublication[] publications=[new(old,start),new(correction,known)];
        TemporalCurveBinding[] history=[
            new(logical,old,start,end,start,known),
            new(logical,old,start,amended,known,end),
            new(logical,correction,amended,end,known,end)];
        Check("a later backdated correction changes historical structure only under later knowledge",()=>
        {
            var resolver=new TemporalCurveBindings(history,publications);
            Equal(resolver.Resolve(logical,Context(At(7),At(7))),old);
            Equal(resolver.Resolve(logical,Context(At(7),At(11))),correction);
            Equal(resolver.Resolve(logical,Context(At(3),At(11))),old);
            Equal(resolver.Resolve(logical,Context(At(7),At(7))),old);
        });
        Check("both timeline axes use inclusive starts and exclusive ends",()=>
        {
            var resolver=new TemporalCurveBindings(history,publications);
            Equal(resolver.Resolve(logical,Context(amended.AddTicks(-1),known)),old);
            Equal(resolver.Resolve(logical,Context(amended,known)),correction);
            Equal(resolver.Resolve(logical,Context(At(7),known.AddTicks(-1))),old);
            Reject("temporal_binding_missing",()=>resolver.Resolve(logical,Context(end,At(11))));
            Reject("temporal_binding_missing",()=>resolver.Resolve(logical,Context(At(7),end)));
        });
        Check("exact references keep their version outside its activation interval but cannot see future publication",()=>
        {
            ICurveBindingResolver resolver=new TemporalCurveBindings(history,publications);
            resolver.RequireVisible(old,Context(At(7),At(11)));
            resolver.RequireVisible(correction,Context(At(3),At(11)));
            Reject("temporal_definition_unavailable",()=>resolver.RequireVisible(correction,Context(At(7),At(7))));
            Reject("temporal_definition_unavailable",()=>resolver.RequireVisible(Version(99),Context(At(7),At(11))));
        });
        Check("missing and ambiguous bindings fail without a latest-version fallback",()=>
        {
            var resolver=new TemporalCurveBindings(history,publications);
            Reject("temporal_binding_missing",()=>resolver.Resolve(new(catalog,"absent"),Context(At(7),At(11))));
            Reject("temporal_binding_ambiguous",()=>new TemporalCurveBindings([..history,history[2]],publications));
            Reject("temporal_binding_ambiguous",()=>new TemporalCurveBindings(
                [new(logical,old,start,end,start,end),new(logical,correction,amended,end,known,end)],publications));
        });
        Check("every binding targets an exact published version available when that assertion became known",()=>
        {
            Reject("temporal_definition_unavailable",()=>new TemporalCurveBindings(history,[new(old,start)]));
            Reject("temporal_definition_unavailable",()=>new TemporalCurveBindings(
                [new(logical,correction,start,end,start,end)],publications));
            Reject("temporal_definition_ambiguous",()=>new TemporalCurveBindings([], [new(old,start),new(old,known)]));
            Reject("temporal_definition_ambiguous",()=>new TemporalCurveBindings([], [new(old,start),new(old,start)]));
        });
        Check("invalid intervals and missing identities are refused at admission",()=>
        {
            Reject("temporal_interval",()=>new TemporalCurveBindings([new(logical,old,start,start,start,end)],publications));
            Reject("temporal_interval",()=>new TemporalCurveBindings([new(logical,old,start,end,end,start)],publications));
            Reject("temporal_interval",()=>new TemporalCurveBindings([new(logical,old,start,end,start.ToOffset(TimeSpan.FromHours(1)),end)],publications));
            Reject("temporal_interval",()=>new TemporalCurveBindings([new(logical,old,start,TemporalCurveBindings.OpenEnd.AddHours(1),start,end)],publications));
            Reject("temporal_binding",()=>new TemporalCurveBindings([new(default,old,start,end,start,end)],publications));
            Reject("temporal_definition",()=>new TemporalCurveBindings([], [new(default,start)]));
        });
        Check("resolved context retains independent explicit provenance and rejects unsupported coordinate combinations",()=>
        {
            var context=Context(At(7),At(11),At(6));
            Equal(context.ContextId,Guid.Parse("30000000-0000-0000-0000-000000000001"));
            Equal(context.ManifestId,"synthetic_manifest_v1");Equal(context.MarketAsOf,At(6));
            Reject("temporal_context",()=>new ResolvedCalculationContext(Guid.Empty,"synthetic_manifest_v1",At(7),At(11),At(7),At(6)));
            Reject("temporal_context",()=>new ResolvedCalculationContext(context.ContextId,"",At(7),At(11),At(7),At(6)));
            Reject("temporal_context",()=>new ResolvedCalculationContext(context.ContextId,context.ManifestId,At(7),At(11),At(8),At(6)));
            Reject("temporal_context",()=>Context(At(7),At(11),At(8)));
            Reject("temporal_context",()=>Context(At(7).ToOffset(TimeSpan.FromHours(1)),At(11)));
            Reject("temporal_context",()=>Context(At(7),TemporalCurveBindings.OpenEnd));
        });
        Check("independent contexts resolve concurrently without mutating the retained assertions",()=>
        {
            var resolver=new TemporalCurveBindings(history,publications);
            var results=Task.WhenAll(Enumerable.Range(0,64).Select(n=>Task.Run(()=>
                resolver.Resolve(logical,Context(At(7),n%2==0?At(7):At(11)))))).GetAwaiter().GetResult();
            for(var n=0;n<results.Length;n++)Equal(results[n],n%2==0?old:correction);
            Equal(history[0].Definition,old);Equal(history[0].KnownTo,known);
        });
        Check("logical identities include their catalog and later caller edits cannot change the index",()=>
        {
            var other=new LogicalCurveKey(Guid.Parse("10000000-0000-0000-0000-000000000002"),logical.Code);
            var otherVersion=correction with{Revision=correction.Revision with{CatalogId=other.CatalogId}};
            Reject("temporal_binding",()=>new TemporalCurveBindings([new(other,correction,start,end,known,end)],publications));
            var supplied=history.ToList();
            supplied.Add(new(other,otherVersion,start,end,known,end));
            supplied.AddRange(Enumerable.Range(0,10_000).Select(n=>new TemporalCurveBinding(
                new(catalog,$"synthetic_unrelated_{n}"),old,start,end,start,end)));
            var suppliedPublications=publications.ToList();
            suppliedPublications.Add(new(otherVersion,known));
            var resolver=new TemporalCurveBindings(supplied,suppliedPublications);
            supplied.Clear();suppliedPublications.Clear();
            Equal(resolver.Resolve(logical,Context(At(3),At(11))),old);
            Equal(resolver.Resolve(other,Context(At(3),At(11))),otherVersion);
        });
        Check("historical topology resolution changes exact curve prices and instrument PV without rebinding older plans",HistoricalPricing);
        Console.WriteLine($"{count} temporal binding checks passed.");return count;
    }

    private static void HistoricalPricing()
    {
        var f=new ConnectedCurveChecks.Fixture();var effective=f.Context.EffectiveAt;
        var start=effective.AddDays(-30);var correctedAt=effective.AddDays(10);var end=TemporalCurveBindings.OpenEnd;
        var logical=new LogicalCurveKey(f.Mean.Key.Curve.Revision.CatalogId,"synthetic_historical_mean");
        var correctedCurve=f.Mean.Key.Curve with{Revision=f.Mean.Key.Curve.Revision with{RevisionId=Guid.NewGuid()}};
        var originalMean=f.Mean with{Expression=new ConnectedMean(f.RawPoints.Take(2).Select(p=>CurvePointReference.Exact(p.Key)).ToImmutableArray(),ConnectedCurves.UniformMean)};
        var correctedMean=f.Mean with{Key=new(correctedCurve,f.Mean.Key.PointId)};
        var logicalSpread=f.Spread with{Expression=new ConnectedWeightedSum([
            new(new(new LogicalCurveReference(logical),f.Mean.Key.PointId),ExactDecimal.Parse("2")),
            new(CurvePointReference.Exact(f.RawPoints[0].Key),ExactDecimal.Parse("-1"))])};
        var pinnedSpread=f.Spread with{Key=new(new(f.Spread.Key.Curve.Revision,"synthetic_exact_spread"),Guid.NewGuid())};
        ImmutableArray<ConnectedCurveDefinition> definitions=[f.Definitions[0],new(originalMean.Key.Curve,[originalMean]),
            new(correctedCurve,[correctedMean]),new(logicalSpread.Key.Curve,[logicalSpread]),new(pinnedSpread.Key.Curve,[pinnedSpread])];
        var resolver=new TemporalCurveBindings([
            new(logical,originalMean.Key.Curve,start,end,start,correctedAt),
            new(logical,originalMean.Key.Curve,start,effective.AddDays(-1),correctedAt,end),
            new(logical,correctedCurve,effective.AddDays(-1),end,correctedAt,end)],
            definitions.Select(d=>new CurvePublication(d.Key,d.Key==correctedCurve?correctedAt:start)));
        var later=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic_corrected_topology",effective,correctedAt.AddDays(1),effective,effective);
        var oldPlan=ConnectedCurves.Compile(definitions,[logicalSpread.Key],f.Context,resolver);
        var newPlan=ConnectedCurves.Compile(definitions,[logicalSpread.Key],later,resolver);
        var pinnedPlan=ConnectedCurves.Compile(definitions,[pinnedSpread.Key],later,resolver);
        // The original mean uses (1+2)/2; the correction includes 4 as well.
        // Thus 2*mean-1 independently gives 2, then 11/3 at the same business date.
        Price(oldPlan,originalMean.Key,"3","2");Price(newPlan,correctedMean.Key,"7","3");
        Price(oldPlan,logicalSpread.Key,"2","1");Price(newPlan,logicalSpread.Key,"11","3");
        Price(pinnedPlan,pinnedSpread.Key,"2","1");
        if(oldPlan.Primitives.Length!=2||newPlan.Primitives.Length!=3||
            oldPlan.Points.ContainsKey(correctedMean.Key)||newPlan.Points.ContainsKey(originalMean.Key)||
            oldPlan.ManifestId==newPlan.ManifestId)
            throw new Exception("Historical contexts must select different exact graph structures, not substitute values in a current graph.");
        var request=new CurveRequest(logicalSpread.Key.Curve,f.RawPoints[0].Contract!.Key);
        var instrument=new TemporalCurvePv(Guid.NewGuid(),originalMean.Unit,request);
        var bindings=ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(request,logicalSpread.Key);
        var oldRun=ConnectedValuation.Prepare(ConnectedScenario.Base(oldPlan),Snapshot(oldPlan));
        var newRun=ConnectedValuation.Prepare(ConnectedScenario.Base(newPlan),Snapshot(newPlan));
        Money(oldRun.PV(instrument,bindings),"2","1");Money(newRun.PV(instrument,bindings),"11","3");
        Money(oldRun.PV(instrument,bindings),"2","1");Price(oldPlan,logicalSpread.Key,"2","1");

        MarketSnapshot Snapshot(ConnectedCurvePlan plan)=>new(Guid.NewGuid(),f.RawPoints[0].Key.Curve.Revision,
            plan.Context.ValuationAt,plan.Context.MarketAsOf,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            plan.Primitives.Select(p=>new SelectedObservation(p.Source.Input,p.Source.Observation)).ToImmutableArray());
        static void Price(ConnectedCurvePlan plan,CurvePointKey point,string numerator,string denominator)
        {
            var value=plan.Evaluate(point);
            if(value.Numerator!=numerator||value.Denominator!=denominator)
                throw new Exception($"Expected historical curve price {numerator}/{denominator}; got {value.Numerator}/{value.Denominator}.");
        }
        static void Money(InstrumentValuation value,string numerator,string denominator)
        {
            if(value.PresentValue.Numerator!=numerator||value.PresentValue.Denominator!=denominator)
                throw new Exception($"Expected historical instrument PV {numerator}/{denominator}; got {value.PresentValue.Numerator}/{value.PresentValue.Denominator}.");
        }
    }

    private sealed record TemporalCurvePv : Instrument
    {
        public override bool SupportsQuantityScaling=>true;
        private const string Model="synthetic_temporal_curve_pv_v1";
        private readonly CurveRequest request;
        public override ReferenceKey Currency{get;}
        public override Quantity UnitQuantity{get;}
        public TemporalCurvePv(Guid id,PriceUnit unit,CurveRequest request):base(id)
        {Currency=unit.Currency;UnitQuantity=new(ExactDecimal.Parse("1"),unit.Quantity);this.request=request;}
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,Model,[request],UnitQuantity.Specification,Currency);
        public override InstrumentValuation PV(SideValuationContext context)=>new(this,context,context.ExactPrice(request).ExactTimes(UnitQuantity),Model);
    }

    private static DateTimeOffset At(int day)=>new(2020,5,day,0,0,0,TimeSpan.Zero);
    private static ResolvedCalculationContext Context(DateTimeOffset effective,DateTimeOffset known,DateTimeOffset? market=null)=>
        new(Guid.Parse("30000000-0000-0000-0000-000000000001"),"synthetic_manifest_v1",effective,known,effective,market??effective);
}
