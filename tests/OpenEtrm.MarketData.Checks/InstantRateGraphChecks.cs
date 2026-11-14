using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class InstantRateGraphChecks
{
    public static void Run()
    {
        var f=new Fixture();var quote=f.Quotes[0];var day=new DateOnly(2031,1,1);
        var midnight=Fixture.Utc("2031-01-01T00:00:00Z");var at=midnight.AddHours(6);var due=midnight.AddDays(2).AddHours(12);
        var specifications=new ValueSpecification(f.R("annual_rate"),NumericKind.Dimensionless,null,null,ExactDecimal.Parse("-0.1"),ExactDecimal.Parse("1"));
        var terms=new[]{(Days:1,Series:f.S(9701)),(Days:2,Series:f.S(9702)),(Days:4,Series:f.S(9703)),(Days:8,Series:f.S(9704))};
        var observations=terms.Select(p=>new MarketObservation(new(f.Revision.CatalogId,Guid.NewGuid()),p.Series,f.R("source"),ObservationRole.Parameter,
            PublicationState.Final,midnight,ExactDecimal.Parse("0.36"))).ToArray();
        var snapshot=new MarketSnapshot(Guid.NewGuid(),f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [..observations.Select(o=>new SelectedObservation(new(o.Series,InputUse.Variable),o))]);
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,[],[],[],[],[specifications],
            terms.Select(p=>new MarketSeries(p.Series,null,null,specifications.Key)));
        var market=model.Build(snapshot,[]);var limits=new ScheduleGraphLimits(128,256,128,128);
        var conventions=RateConventionSet.Parse("actual_360","simple","linear_zero","none","decimal_per_year");
        var definition=new RateDiscountDefinition(Guid.NewGuid(),f.C("rates"),quote.Currency,f.R("source"),day,day.AddDays(2),conventions,RateCurveMath.Policy,
            [..terms.Take(3).Select((p,i)=>new RateDiscountPillar(day.AddDays(p.Days),observations[i]))])
            {Timing=new(at,due,RateDiscountTiming.Version),CurveTerms=new(Guid.NewGuid(),1,[..terms.Select(p=>new RateDiscountTenor(p.Series,day.AddDays(p.Days)))])};
        var factor=new SidePaymentFactor(new(Guid.NewGuid(),0,due,quote.Currency),ExactDecimal.Parse("0.997755672276703731"),snapshot,DiscountFactorSource.CurrencyDefault)
            {RateDefinition=definition};
        ScheduleGraphBuilder Builder()=>new(market.Graph,snapshot,limits);
        var builder=Builder();var discount=builder.DiscountInput(factor);
        var flat=new FlatPrice(new(ExactDecimal.Parse("100"),PriceUnit.FromQuote(quote)));
        var price=builder.FixedPrice(new ScheduleSourceKey.Contract(DefinitionIdentity.For(flat),0),ExactUnitPrice.FromPrice(flat.Price));
        var quantity=builder.QuantityInput(new ScheduleSourceKey.Contract(DefinitionIdentity.For(flat),1),ExactQuantity.FromQuantity(new(ExactDecimal.Parse("1"),quote.Quantity)),ScheduleInputUse.Frozen);
        var cash=builder.Amount(price,quantity);var pv=builder.Discount(cash,discount);var third=builder.RateInput(terms[2].Series);
        var basis=builder.Compile().Base();var unchangedCash=basis.Cash(cash);
        Equal(basis.Cash(pv).ToExactString(),"99.7755672276703731");
        var shocked=basis.WithRateInputs(new Dictionary<ScheduleRateNode,ExactDecimal>{{third,ExactDecimal.Parse("0.60")}});
        Equal(shocked.Cash(pv).ToExactString(),"99.7259029827239117");
        var middle=basis.WithRateInputs(new Dictionary<ScheduleRateNode,ExactDecimal>{{builder.RateInput(terms[1].Series),ExactDecimal.Parse("0.60")}});
        Equal(middle.Cash(pv).ToExactString(),"99.6595738593997613");
        if(!ReferenceEquals(unchangedCash,shocked.Cash(cash)))throw new Exception("A rate change must preserve the unchanged cash calculation.");
        Reject("discount_timing",()=>Builder().DiscountInput(factor with{RateDefinition=definition with{Timing=definition.Timing with{CalculationAt=at.AddMicroseconds(1)}}}));
        Reject("discount_timing",()=>Builder().DiscountInput(factor with{RateDefinition=definition with{Timing=definition.Timing with{PaymentAt=due.AddMicroseconds(1)}}}));
        Reject("rate_observation",()=>Builder().DiscountInput(factor with{RateDefinition=definition with{Source=f.R("other_source")}}));
        Reject("discount_curve",()=>Builder().DiscountInput(factor with{DiscountFactor=ExactDecimal.Parse("1")}));
        Reject("discount_curve",()=>Builder().DiscountInput(factor with{RateDefinition=definition with{Pillars=definition.Pillars.Add(new(day.AddDays(8),observations[3]))}}));
        // Equal rates do not make a native curve pillar cease to be a dependency.
        Reject("discount_curve",()=>Builder().DiscountInput(factor with{RateDefinition=definition with{Pillars=definition.Pillars.RemoveAt(1)}}));
        Reject("discount_curve",()=>Builder().DiscountInput(factor with{RateDefinition=definition with{CurveTerms=null}}));
        Reject("discount_curve",()=>Builder().DiscountInput(factor with{RateDefinition=definition with{Pillars=definition.Pillars.SetItem(1,definition.Pillars[1] with{
            Observation=observations[3]})}}));
        Console.WriteLine("PASS exact-instant rate graph: three-pillar replay, independent conditional PV, rate scenario reuse and exact timing/source/geometry fences");
    }
    static void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}, got {actual}.");}
    static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
