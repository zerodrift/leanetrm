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
        Require(MarketInputSelection.SelectedCurve(frame,"gbp",null,rates)==second,"A nonprimary typed rate must select its own exact input source.");
        Require(MarketInputSelection.SelectedCurve(frame,"usd",null,rates)==first,"Primary rate selection must remain exact.");
        Require(MarketInputSelection.SelectedCurve(frame,"gas",null,rates)==first,"Commodity signed roots must remain exact.");
        Reject("curve_not_selected",()=>MarketInputSelection.SelectedCurve(frame,"unselected",null,rates));
        Reject("market_inputs",()=>MarketInputSelection.SelectedCurve(frame,"usd",null,rates.Add(Rate(second,"usd"))));
        Reject("market_inputs",()=>MarketInputSelection.SelectedCurve(frame,"gas",null,rates.Add(Rate(second,"gas"))));
        Reject("market_inputs",()=>MarketInputSelection.SelectedCurve(frame with{SourceInputs=frame.SourceInputs.Remove(second.DefinitionRevisionId)},"gbp",null,rates));
        Reject("market_inputs",()=>MarketInputSelection.SelectedCurve(frame,"gbp",null,[Rate(second with{ReferenceRevisionId=Guid.NewGuid()},"gbp")]));
        Console.WriteLine("PASS exact selected rate sources: secondary and primary rates, commodity roots, missing inputs, reference identity and ambiguous names");
    }
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Reject(string code,Action action)
    {
        try{action();}catch(MarketDataException error)when(error.Code==code){return;}
        throw new Exception("Expected structured refusal: "+code);
    }
}
