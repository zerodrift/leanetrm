using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

internal static class ConnectedSampleChecks
{
    public static void Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
        var at=new DateTimeOffset(2021,1,1,16,0,0,TimeSpan.Zero);
        var source=new CurveKey(revision,"synthetic_source");
        var contracts=Enumerable.Range(0,3).Select(_=>new ContractKey(revision,Guid.NewGuid())).ToArray();
        MarketSnapshot Frame()=>new(Guid.NewGuid(),revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[]);
        var first=Frame();var second=Frame();
        // Receipt selection uses frame identity; no numerical model runs here.
        ConnectedRecipeSample Sample(MarketSnapshot frame,int contract)=>new(source,DateOnly.FromDateTime(at.UtcDateTime),contracts[contract],null!,frame,true);
        var a=Sample(first,0);var b=Sample(first,1);var c=Sample(second,1);var d=Sample(second,2);
        void Equal(ImmutableArray<ConnectedRecipeSample> actual,params ConnectedRecipeSample[] expected)
        {if(!actual.SequenceEqual(expected))throw new Exception("Receipt coverage changed existing rows or lost an independent frame.");}
        Equal(CustomCurves.RetainSamples([a,b,c,d]),a,b,d);
        Equal(CustomCurves.RetainSamples([a,b,Sample(second,0)]),a,b,Sample(second,0));
        Equal(CustomCurves.RetainSamples([a,b,a,b]),a,b);
        Equal(CustomCurves.RetainSamples([a,b,c,d,Sample(second,0)]),a,b,d);
        Console.WriteLine("PASS connected samples: prior covered receipts stay identical, subset frames remain admitted and duplicates add no evidence");
    }
}
