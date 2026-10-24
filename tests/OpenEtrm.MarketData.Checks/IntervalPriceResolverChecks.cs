using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

internal static class IntervalPriceResolverChecks
{
    private static void Equal<T>(T actual,T expected)
    {if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    public static int Run()
    {
        var f=new Fixture();
        var quote=f.Quotes.Single(q=>q.Key.Code=="power_out");
        var unit=PriceUnit.FromQuote(quote);
        var quantity=ExactQuantity.FromQuantity(new(ExactDecimal.Parse("2.75"),unit.Quantity));
        var price=ExactUnitPrice.FromPrice(new(ExactDecimal.Parse("400"),unit)).Divide(11);
        Equal(quantity.Times(price).Numerator,"100");
        Equal(quantity.Times(price).Denominator,"1");
        var energy=new QuantitySpecification(unit.Quantity,f.R("power"),f.R("basis"),f.R("megawatt_hour"),f.R("energy"));
        var fractional=PowerQuantitySchedule.Compile(new(ExactDecimal.Parse("72"),unit.Quantity),PowerQuantityBasis.TotalEnergy,energy,energy,new(2031,1,1),new(2031,1,8),PowerClock.Capture(TimeZoneInfo.Utc),TimeOfUse.All(60),[],60,PowerAllocation.FlatRate).Intervals[0].Energy;
        Equal(fractional.Times(price).Numerator,"1200");
        Equal(fractional.Times(price).Denominator,"77");
        var cents=PriceUnit.FromQuote(quote with{Multiplier=ExactDecimal.Parse("0.01")});
        Equal(quantity.Times(ExactUnitPrice.FromPrice(new(ExactDecimal.Parse("400"),cents)).Divide(11)).Numerator,"1");
        Reject("quantity_mismatch",()=>ExactQuantity.FromQuantity(new(ExactDecimal.Parse("1"),f.R("other"))).Times(price));
        Console.WriteLine("PASS resolver: exact rational price times quantity, quote multiplier once, incompatible units refused");
        FrozenAnchors();
        return 2;
    }
    private static void FrozenAnchors()
    {
        var f=new Fixture();var quote=f.Quotes.Single(q=>q.Key.Code=="power_out");
        var expiry=Fixture.Utc("2031-11-01T00:00:00Z");
        var contract=new MarketContract(f.K(901),ContractKind.Forward,new(2031,11,1),new(2031,12,1),expiry,null);
        var curve=new Curve(f.C("block"),quote.Key,f.R("location"),f.R("hourly"),new(SettlementKind.Index,f.R("index"),null),
            CurveRule.Direct,[contract.Key],[],ObservationRole.TraderMark,f.R("source"),null);
        CurveModel Model(Curve c)=>new(f.Revision,f.Reference,f.Quantities,f.Quotes,[contract],[c],[],[],[new(f.S(901),contract.Key,quote.Key,null)]);
        var observation=new MarketObservation(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(901),f.R("source"),ObservationRole.FinalContractSettlement,
            PublicationState.Final,expiry.AddHours(1),ExactDecimal.Parse("58"));
        MarketSnapshot Snapshot(MarketObservation obs)=>new(Guid.NewGuid(),f.Revision,expiry.AddHours(2),expiry.AddHours(2),
            InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[new(new(f.S(901),InputUse.FrozenFact),obs)]);
        ImmutableArray<CurveRequest> requests=[new(curve.Key,contract.Key)];
        Reject("source_expired",()=>Model(curve).Build(Snapshot(observation),requests));
        var frozen=Model(curve with{ConstituentExpiry=ConstituentExpiryRule.FreezeFinalSettlement});
        var build=frozen.Build(Snapshot(observation),requests);
        Equal(build.Values[0].Price.Value.ToString(),"58");
        Equal(build.Evaluations[0].Use,InputUse.FrozenFact);Equal(frozen.QuoteFactors(build.Snapshot).Length,0);
        Equal(build.Values[0].Lineage.SetEquals([observation.Key]),true);
        Reject("frozen_fact",()=>frozen.Build(Snapshot(observation with{ObservedAt=expiry.AddTicks(-1)}),requests));
        Reject("selection_mismatch",()=>frozen.Build(Snapshot(observation with{Source=f.R("other_source")}),requests));
        Reject("frozen_fact",()=>frozen.Build(Snapshot(observation with{Role=ObservationRole.TraderMark}),requests));
        Reject("frozen_fact",()=>frozen.Build(Snapshot(observation with{Role=ObservationRole.DailySettlement}),requests));
        Reject("frozen_fact",()=>frozen.Build(Snapshot(observation with{State=PublicationState.Preliminary}),requests));
        Reject("observation_cutoff",()=>frozen.Build(Snapshot(observation with{ObservedAt=expiry.AddHours(3)}),requests));
        Reject("missing_observation",()=>frozen.Build(Snapshot(observation) with{Selections=[]},requests));
        Console.WriteLine("PASS resolver: frozen final eligibility, exact lineage, no live factor, absent/early/wrong/late finals refused");
    }
}
