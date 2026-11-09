using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Curves;
using OpenEtrm.MarketData.Publications;

internal static class PublicationPriceResolutionChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action action){action();count++;Console.WriteLine("PASS publication price resolution: "+name);}
        void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var f=new IndexFixingChecks.FixingFixture(monthly:true,delayedMonthlyPublication:true);
        var contract=f.Projections[0].Contract;var request=f.Projections[0].Market;var fixing=f.Fixings[0];
        var publication=new PricePublicationDefinition(new(f.Base.Revision.CatalogId,Guid.NewGuid(),1),f.Base.Reference,
            new(new(f.Base.Revision.CatalogId,Guid.NewGuid(),1),"Synthetic publisher"),"Synthetic monthly physical index",[f.Base.R("gas")],
            [new(fixing.Index,f.Base.R("gas"),f.Base.R("location"),f.Quote.Key,PriceUnit.FromQuote(f.Quote),"SYNTH-MONTH","monthly")],
            f.Base.R("calendar"),f.Base.R("clock"),DatedGasPublication.MonthlyImplementation);
        var occurrence=new PricePublicationOccurrence(publication.Key,contract.Expiry.AddDays(-3),contract.Expiry,
            DateOnly.FromDateTime(contract.Expiry.UtcDateTime),[new(contract.DeliveryFrom,contract.DeliveryTo)],fixing.FixingAt);
        var snapshot=f.Snapshot(fixing.FixingAt,1);
        var selected=snapshot.Selections.Single(s=>s.Input==new InputKey(fixing.Series,InputUse.FrozenFact));
        var release=new PricePublicationRelease(Guid.NewGuid(),publication.Key,fixing.Index,fixing.Source,occurrence,PublicationState.Final,
            fixing.FixingAt,fixing.FixingAt.AddMinutes(1),fixing.FixingAt.AddMinutes(2),"synthetic-1",null,new(selected.Observation.Value,f.Quote));
        var binding=new ContractPriceTermsBinding(new("published_release_v1",publication.Key,fixing.Index,
            ContractPriceTerms.SingleAssessment,ContractPriceTerms.RequireFinal,null),publication,f.Base.R("gas"),fixing)
            {PublicationPrices=new(Guid.NewGuid(),occurrence,release.KnownAt,[new(release,selected.Observation.Key)])};
        ConnectedCurvePlan Compile(MarketSnapshot input,ContractPriceTermsBinding? terms=null)
        {
            terms??=binding;
            var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-publication-state",input.ValuationAt,
                terms.PublicationPrices!.KnownAt,input.ValuationAt,input.MarketCutoff);
            var graph=f.Model.CompileContractTerms(input,[request],new Dictionary<CurveRequest,ContractPriceTermsBinding>{{request,terms}},false,context);
            return ConnectedCurves.Compile(graph.Definitions,[graph.Points[(request,InputUse.Variable)]],context,new Visible());
        }
        Check("last trade before publication is explicitly awaiting final publication",()=>
            Reject("awaiting_publication",()=>Compile(f.Snapshot(contract.Expiry))));
        Check("live forward retains ordinary shock exposure before the final is published",()=>
        {
            var graph=Compile(f.Snapshot(contract.Expiry.AddHours(-1)));
            var point=graph.Points.Keys.Single();var scenario=ConnectedScenario.Base(graph);
            if(scenario.Price(point).ToExactString()!="11"||graph.Primitives.Single().Source.Input.Use!=InputUse.Variable)throw new Exception("Forward changed.");
            var shifted=scenario.WithShifts([new(CurveShockCoordinate.MarketQuote,new ParallelCurveTarget(f.Curve.Key),PriceUnit.FromQuote(f.Quote),ExactDecimal.Parse("1"),CurveShift.ParentAdditive)]);
            if(shifted.Price(point).ToExactString()!="12")throw new Exception("Live source shock was lost.");
        });
        Check("published and known final freezes the exact actual and removes quote exposure",()=>
        {
            var graph=Compile(snapshot);var point=graph.Points.Keys.Single();
            if(graph.Evaluate(point).ToExactString()!="10"||graph.Primitives.Any(p=>p.Source.Input.Use!=InputUse.FrozenFact))throw new Exception("Final did not freeze.");
        });
        Check("future publication and unknown receipt cannot leak through a preselected observation",()=>
        {
            Reject("awaiting_publication",()=>Compile(snapshot with{ValuationAt=fixing.FixingAt.AddMinutes(-1),MarketCutoff=fixing.FixingAt.AddMinutes(-1),Selections=[]}));
            Reject("awaiting_publication",()=>Compile(snapshot,binding with{PublicationPrices=binding.PublicationPrices! with{KnownAt=release.KnownAt.AddTicks(-10)}}));
        });
        Check("preliminary publication never satisfies require-final",()=>
            Reject("awaiting_publication",()=>Compile(snapshot,binding with{PublicationPrices=binding.PublicationPrices! with{Releases=[new(release with{Stage=PublicationState.Preliminary},selected.Observation.Key)]}})));
        Check("actual release controls fixing independently of expected release and the legacy boundary",()=>
        {
            var later=occurrence with{ExpectedReleaseAt=release.PublishedAt.AddDays(5)};
            var explicitRelease=binding with{Fixing=fixing with{FixingAt=release.PublishedAt.AddDays(2)},
                PublicationPrices=binding.PublicationPrices! with{Occurrence=later,Releases=[new(release with{Occurrence=later},selected.Observation.Key)]}};
            var graph=Compile(snapshot,explicitRelease);
            if(graph.Evaluate(graph.Points.Keys.Single()).ToExactString()!="10")throw new Exception("Expected date became an economic boundary.");
        });
        Check("corrections obey their own publication and knowledge times while retaining the source fact time",()=>
        {
            var corrected=selected.Observation with{Key=new(f.Base.Revision.CatalogId,Guid.NewGuid()),Value=ExactDecimal.Parse("12")};
            var correction=release with{Id=Guid.NewGuid(),PublishedAt=release.PublishedAt.AddDays(1),ReceivedAt=release.ReceivedAt.AddDays(1),
                KnownAt=release.KnownAt.AddDays(1),CorrectsId=release.Id,VendorRevision="synthetic-2",Price=new(corrected.Value,f.Quote)};
            var revised=binding with{PublicationPrices=binding.PublicationPrices! with{KnownAt=correction.KnownAt,
                Releases=[new(release,selected.Observation.Key),new(correction,corrected.Key)]}};
            var before=Compile(snapshot,revised);
            if(before.Evaluate(before.Points.Keys.Single()).ToExactString()!="10")throw new Exception("A correction appeared before it was issued.");
            Reject("publication_fixing_selection",()=>Compile(snapshot with{Selections=snapshot.Selections.Select(s=>s.Input==selected.Input?s with{Observation=corrected}:s).ToImmutableArray()},revised));
            var laterSnapshot=snapshot with{ValuationAt=correction.PublishedAt,MarketCutoff=correction.PublishedAt};
            var unknown=Compile(laterSnapshot,revised with{PublicationPrices=revised.PublicationPrices! with{KnownAt=correction.KnownAt.AddTicks(-10)}});
            if(unknown.Evaluate(unknown.Points.Keys.Single()).ToExactString()!="10")throw new Exception("Unknown correction replaced original.");
            var correctedSnapshot=laterSnapshot with{Selections=laterSnapshot.Selections.Select(s=>s.Input==selected.Input?s with{Observation=corrected}:s).ToImmutableArray()};
            var after=Compile(correctedSnapshot,revised);
            if(after.Evaluate(after.Points.Keys.Single()).ToExactString()!="12"||after.Primitives.Single().Source.Observation.ObservedAt!=release.PublishedAt)
                throw new Exception("Correction lost its price or original source-fact time.");
            Reject("publication_fixing_selection",()=>Compile(laterSnapshot,revised));
        });
        Check("unrelated final releases are ambiguous instead of chosen by insertion order",()=>
            Reject("publication_price_ambiguity",()=>Compile(snapshot,binding with{PublicationPrices=binding.PublicationPrices! with
                {Releases=[new(release,selected.Observation.Key),new(release with{Id=Guid.NewGuid(),VendorRevision="unrelated"},new(f.Base.Revision.CatalogId,Guid.NewGuid()))]}})));
        return count;
    }
    sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
}
