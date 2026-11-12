using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class SelectedRateSourceChecks
{
    internal static void Run()
    {
        var first=new CatalogueSource(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        var second=new CatalogueSource(Guid.NewGuid(),first.ReferenceCatalogId,Guid.NewGuid());
        var at=DateTimeOffset.Parse("2032-10-04T20:00:00Z");
        MarketSourceInputs Part(CatalogueSource source,params string[] roots)=>new(source,[],[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,[..roots]);
        var frame=new InputFrame(Guid.NewGuid(),"Selected close",at,DateOnly.FromDateTime(at.UtcDateTime),[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty)
        {SourceInputs=ImmutableDictionary<Guid,MarketSourceInputs>.Empty.Add(first.DefinitionRevisionId,Part(first,"gas")).Add(second.DefinitionRevisionId,Part(second))};
        CatalogueCurve Rate(CatalogueSource source,string code)=>new(code,"rate",source.DefinitionRevisionId,source.ReferenceCatalogId,source.ReferenceRevisionId);
        var rates=ImmutableArray.Create(Rate(first,"usd"),Rate(second,"gbp"));
        CataloguePublication Catalogue(params CatalogueCurve[] choices)=>new(Guid.NewGuid(),null,new(2032,1,1),new(2033,1,1),1,[..choices],ImmutableDictionary<string,CatalogueSource>.Empty.Add("primary",first));
        var catalogue=Catalogue(rates.ToArray());
        Require(MarketInputSelection.SelectedCurve(frame,"gbp",catalogue,rates)==second,"A nonprimary published rate must select its own exact input source.");
        Require(MarketInputSelection.SelectedCurve(frame,"usd",catalogue,rates)==first,"The published rate owner must remain exact.");
        Require(MarketInputSelection.SelectedCurve(frame,"gas",catalogue,rates)==first,"Commodity signed roots must remain exact.");
        Reject("curve_not_selected",()=>MarketInputSelection.SelectedCurve(frame,"unselected",catalogue,rates));
        var replaced=Rate(second,"usd");
        Require(MarketInputSelection.SelectedCurve(frame,"usd",Catalogue(replaced),[replaced])==second,"A published ownership change must not fall back to the primary source.");
        Reject("market_inputs_missing",()=>MarketInputSelection.SelectedCurve(frame with{SourceInputs=frame.SourceInputs.Remove(second.DefinitionRevisionId)},"usd",Catalogue(replaced),[]));
        Reject("market_inputs",()=>MarketInputSelection.SelectedCurve(frame,"gas",Catalogue(Rate(second,"gas")),[Rate(second,"gas")]));
        Reject("market_inputs",()=>MarketInputSelection.SelectedCurve(frame,"gbp",Catalogue(Rate(second with{ReferenceRevisionId=Guid.NewGuid()},"gbp")),rates));
        Console.WriteLine("PASS exact selected rate sources: published ownership, secondary rates, commodity roots, missing inputs, reference identity and ambiguous names");
    }
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Reject(string code,Action action)
    {
        try{action();}catch(MarketDataException error)when(error.Code==code){return;}
        throw new Exception("Expected structured refusal: "+code);
    }
}
