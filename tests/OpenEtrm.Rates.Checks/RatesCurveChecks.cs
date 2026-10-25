using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.Rates;

internal static class RatesCurveChecks
{
    public static int Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var refs=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(refs,code);
        var at=new DateTimeOffset(2030,1,1,0,0,0,TimeSpan.Zero);var date=new DateOnly(2030,1,1);
        var a=new SeriesKey(revision,Guid.NewGuid());var b=new SeriesKey(revision,Guid.NewGuid());
        var definition=new ZeroRateDefinition(new(revision,"rates"),R("terms"),R("ccy"),"actual_365_fixed","simple",RateConventions.Implementation,
            "valuation_utc_date",R("source"),[new(a,date.AddDays(10)),new(b,date.AddDays(20))]);
        MarketObservation Observation(SeriesKey series,string rate)=>new(new(revision.CatalogId,Guid.NewGuid()),series,R("source"),ObservationRole.Parameter,PublicationState.Final,at,ExactDecimal.Parse(rate));
        var snapshot=new MarketSnapshot(Guid.NewGuid(),revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [new(new(a,InputUse.Variable),Observation(a,"0")),new(new(b,InputUse.Variable),Observation(b,"0.73"))]);
        var curve=new RatesCurve(definition,snapshot,DiscountInterpolation.Policy);
        var values=curve.EvaluateMany([date.AddDays(10),date.AddDays(15),date.AddDays(15),date.AddDays(20)]);
        if(values[0].Value.ToString()!="1"||values[1].Value.ToString()!="0.980580675690920159"||values[3].Value.ToString()!="0.961538461538461538"||
            !ReferenceEquals(values[1],values[2])||values[1].Pillars.Length!=2||values[1].SnapshotId!=snapshot.Id)
            throw new Exception("Independent rate construction/interpolation oracle or retained bracket evidence failed.");
        foreach(var uncovered in new[]{date,date.AddDays(9),date.AddDays(21)})
        {try{curve.Evaluate(uncovered);}catch(MarketDataException e)when(e.Code=="discount_coverage"){continue;}throw new Exception("Unsupported extrapolation succeeded.");}
        Console.WriteLine("PASS rates curve: shared typed evaluator, exact factors, log-discount brackets, batch duplicates and coverage rejection");return 1;
    }
}
