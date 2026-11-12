using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.Rates;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Calculation;

internal static class RatesCurveChecks
{
    public static int Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var refs=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(refs,code);
        var at=new DateTimeOffset(2030,1,1,0,0,0,TimeSpan.Zero);var date=new DateOnly(2030,1,1);
        var a=new SeriesKey(revision,Guid.NewGuid());var b=new SeriesKey(revision,Guid.NewGuid());
        var definition=new RateCurveDefinition(new(revision,"rates"),R("ccy"),R("source"),RateConventionSet.Parse("actual_365_fixed","simple","log_linear_discount","none","decimal_per_year"),[new("10D",a,date.AddDays(10)),new("20D",b,date.AddDays(20))],Guid.NewGuid(),1);
        MarketObservation Observation(SeriesKey series,string rate)=>new(new(revision.CatalogId,Guid.NewGuid()),series,R("source"),ObservationRole.Parameter,PublicationState.Final,at,ExactDecimal.Parse(rate));
        var snapshot=new MarketSnapshot(Guid.NewGuid(),revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [new(new(a,InputUse.Variable),Observation(a,"0")),new(new(b,InputUse.Variable),Observation(b,"0.73"))]);
        var curve=new RatesCurve(definition,snapshot);
        var selected=curve.Lookup(date.AddDays(15));
        if(selected.Policy is not RatesCurvePolicy{Conventions:{DayCount:"actual_365_fixed",Compounding:"simple"}}||selected.Inputs.Length!=2)
            throw new Exception("A discount lookup must retain its selected interpolation/construction policies and exact two-pillar evidence.");
        var values=curve.LookupMany([date.AddDays(10),date.AddDays(15),date.AddDays(15),date.AddDays(20)]).Select(result=>result.Value).ToImmutableArray();
        if(values[0].Value.ToString()!="1"||values[1].Value.ToString()!="0.980580675690920159"||values[3].Value.ToString()!="0.961538461538461538"||
            !ReferenceEquals(values[1],values[2])||values[1].Pillars.Length!=2||values[1].SnapshotId!=snapshot.Id)
            throw new Exception("Independent rate construction/interpolation oracle or retained bracket evidence failed.");
        // t_0 discounts at exactly 1 from no inputs; before the first pillar t_0 is the left bracket (D359); nothing extrapolates.
        if(curve.Lookup(date) is not {Inputs.Length:0,Value.Value:var today}||today.ToString()!="1"||curve.Lookup(date.AddDays(9)).Inputs.Length!=1)
            throw new Exception("The t_0 anchor must bracket the first pillar from no inputs of its own.");
        try{curve.Lookup(date.AddDays(21));throw new Exception("Unsupported extrapolation succeeded.");}catch(MarketDataException e)when(e.Code=="discount_coverage"){}
        Console.WriteLine("PASS rates curve: shared typed evaluator, exact factors, log-discount brackets, the t_0 anchor and coverage rejection");

        if(RateCurveMath.DiscountRatio(ExactDecimal.Parse("1"),ExactDecimal.Parse("134217728"),RateCurveMath.DiscountRatioPolicy).ToString()!="0.000000007450580597"||
            RateCurveMath.DiscountRatio(ExactDecimal.Parse("1"),ExactDecimal.Parse("524288"),RateCurveMath.DiscountRatioPolicy).ToString()!="0.000001907348632812")
            throw new Exception("Independent exact rational discount-ratio rounding oracles failed.");
        var key=new SidePaymentKey(Guid.NewGuid(),0,at.AddDays(20),R("ccy"));
        var discounting=new CurrencyDefaultDiscounting([key],snapshot,true,new(Guid.NewGuid(),R("ccy"),definition));
        var rolled=discounting.ResolveForValuation([key],snapshot,at.AddDays(15),ClockChange.FrozenInputs).Single();
        var difference=rolled.DiscountFactor-ExactDecimal.Parse("0.980580675690920159");
        if(difference.CompareTo(ExactDecimal.Parse("0.000000000000000002"))>0||difference.CompareTo(ExactDecimal.Parse("-0.000000000000000002"))<0||
            !ReferenceEquals(rolled.Snapshot,snapshot)||rolled.ClockRoll is not {AnchorObservations.Length:2})
            throw new Exception("Frozen dated discount roll did not rebase the full retained curve with source evidence.");
        try{discounting.ResolveForValuation([key],snapshot,at.AddDays(21),ClockChange.FrozenInputs);throw new Exception("A payment crossing was admitted.");}
        catch(MarketDataException e)when(e.Code=="valuation_time_event"){}
        Console.WriteLine("PASS rates curve: conditional frozen-curve roll retains both numerator and denominator sources");

        // D359: a payment discounts from a definition's own conventions; the factor is an independent 50-digit oracle.
        var continuous=RateConventionSet.Parse("actual_365_fixed","continuous","log_linear_discount","flat_forward","percent_per_year");
        var rolling=new RateCurveDefinition(new(revision,"usd_rolling"),R("usd"),R("source"),continuous,[new("1Y",a,date.AddDays(365)),new("2Y",b,date.AddDays(730))],Guid.NewGuid(),1);
        var marks=snapshot with{Selections=[new(new(a,InputUse.Variable),Observation(a,"4.30")),new(new(b,InputUse.Variable),Observation(b,"4.10"))]};
        var mapping=new CurrencyDiscountCurve(Guid.NewGuid(),R("usd"),rolling);
        var payment=PaymentDiscount.Resolve(true,R("usd"),date.AddDays(500),marks,mapping);
        if(payment.Factor.ToString()!="0.944193025537330858"||payment.Conventions!=continuous||payment.Pillars.Length!=2)
            throw new Exception($"A rolling-curve payment factor {payment.Factor} differs from the oracle.");
        var extrapolated=PaymentDiscount.Resolve(true,R("usd"),date.AddDays(900),marks,mapping).Factor-ExactDecimal.Parse("0.904688689863042511");
        if(extrapolated.CompareTo(ExactDecimal.Parse("0.000000000000000001"))>0||extrapolated.CompareTo(ExactDecimal.Parse("-0.000000000000000001"))<0)
            throw new Exception("Flat-forward extrapolation after the last pillar is not within one unit of the 18th place of the oracle.");
        try{PaymentDiscount.Resolve(true,R("usd"),date.AddDays(500),marks,mapping with{Definition=rolling with{Currency=R("eur")}});throw new Exception("A curve in another currency discounted a USD payment.");}
        catch(MarketDataException e)when(e.Code=="discount_curve"){}
        Console.WriteLine("PASS rates curve: a payment discounts from its definition's own conventions and currency");return 2;
    }
}
