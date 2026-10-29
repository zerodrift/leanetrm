using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

internal static class PpaNormalizationReuseChecks
{
    private static void Equal<T>(T actual,T expected)
    {if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
    public static void Run()
    {
        var f=new PpaAcceptanceFixture();var market=f.Market(f.From.AddDays(-2));
        var sealedInstrument=f.WithCertificate();
        var originalSide=(PowerPhysicalSide)sealedInstrument.Memberships[0].Side;
        var changedNormalization=f.Normalization([new(f.Definitions,"synthetic_other_denominator")]);
        var changedSide=new PowerPhysicalSide(originalSide.Id,originalSide.Terms with{Normalization=changedNormalization},originalSide.Location);
        if(DefinitionIdentity.For(changedSide)==DefinitionIdentity.For(originalSide))
            throw new Exception("A side's economic identity must include all normalization assets, basis and policy.");
        var rejected=false;
        try
        {
            _=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,
                sealedInstrument.Memberships.SetItem(0,sealedInstrument.Memberships[0] with{Side=changedSide}),false,PpaInstrument.Implementation);
        }
        catch(MarketDataException error)when(error.Code=="ppa_normalization"){rejected=true;}
        if(!rejected)throw new Exception("A reusable side's normalization denominator cannot be changed by placing it in another instrument.");
        var cash=(FixedSide)sealedInstrument.Memberships[1].Side;
        PpaInstrument WithCashTerms(PpaSideTerms terms)=>new(Guid.NewGuid(),f.Definitions,f.Reference,
            sealedInstrument.Memberships.SetItem(1,sealedInstrument.Memberships[1] with{Side=new FixedSide(cash.Id,terms,cash.Price)}),false,PpaInstrument.Implementation);
        Reject("ppa_quantity_reference",()=>WithCashTerms(cash.Terms with{InputPorts=default}));
        Reject("ppa_quantity_reference",()=>WithCashTerms(cash.Terms with{InputPorts=[]}));
        Reject("ppa_quantity_unit",()=>WithCashTerms(cash.Terms with{InputPorts=[cash.Terms.InputPorts[0] with{Quantity=f.CapacityDays.Key}]}));
        Reject("ppa_quantity_unit",()=>WithCashTerms(cash.Terms with{ScheduleQuantity=f.CapacityDays.Key}));
        Reject("ppa_normalization",()=>new PpaNormalization([f.Asset.Key],f.Capacity.Key,PpaNormalizationPolicy.ContractUnit));
        Reject("ppa_normalization",()=>new PpaNormalization([],f.Capacity.Key,PpaNormalizationPolicy.SumCapacityInForce));
        PpaInstrument Instrument(VolumeQuantity volume,string strike="0",string conversion="1",ImmutableArray<AssetKey> assets=default)
        {
            var original=f.WithCertificate();
            var normalization=f.Normalization(assets.IsDefault?[f.Asset.Key]:assets);
            var members=original.Memberships.Select(m=>m with {Side=m.Side switch {
                PowerPhysicalSide side=>new PowerPhysicalSide(side.Id,side.Terms with {Quantity=volume,Normalization=normalization},side.Location),
                CertificateDeliverySide side=>new CertificateDeliverySide(side.Id,side.Terms with {Quantity=volume,Normalization=normalization},side.CertificateQuantity,
                    ExactDecimal.Parse(conversion),side.Eligibility,side.Holding,side.Disposition),
                FixedSide side=>new FixedSide(side.Id,side.Terms with{Normalization=normalization},m.Slot.Role==PpaRole.StrikePayment?new FlatPrice(new(ExactDecimal.Parse(strike),f.EnergyPrice)):side.Price),
                _=>m.Side
            }}).ToImmutableArray();
            return new(Guid.NewGuid(),f.Definitions,f.Reference,members,false,PpaInstrument.Implementation);
        }
        var first=Instrument(f.Volume);var other=Instrument(f.Volume,"40","2");
        var inputs=f.Prepare(market,[first,other]);
        // Two independently constructed instruments each declare energy and certificate
        // volumes. Prices, side identities and certificate conversion are not quantities.
        Equal(inputs.VolumeNormalizations,721);
        foreach(var instrument in new[]{first,other})CheckEnergy(inputs,market,instrument,new(f.From,f.To),"721","4");
        var firstCash=Value(inputs,market,first,new(f.From,f.To)).ValueExcludingOverdue;
        var otherCash=Value(inputs,market,other,new(f.From,f.To)).ValueExcludingOverdue;
        Equal(firstCash.Numerator,"7931");Equal(firstCash.Denominator,"1");
        Equal(otherCash.Numerator,"2163");Equal(otherCash.Denominator,"2");
        var changed=f.Prepare(market,[first,other],forecastOverride:f.Forecast("0.5"));
        Equal(changed.VolumeNormalizations,721);
        CheckEnergy(changed,market,first,new(f.From,f.To),"721","2");
        var partial=f.Prepare(market,[first,other],coverage:new Dictionary<Guid,ImmutableArray<DeliveryCoverage>>
        {
            [first.Id]=[new(f.From,f.From.AddHours(1))],
            [other.Id]=[new(f.From,f.From.AddHours(2))]
        }.ToImmutableDictionary());
        Equal(partial.VolumeNormalizations,3);
        CheckEnergy(partial,market,first,new(f.From,f.From.AddHours(1)),"1","4");
        CheckEnergy(partial,market,other,new(f.From,f.From.AddHours(2)),"1","2");
        var delivered=Instrument(f.Volume with {Stage=QuantityStage.Delivered});
        var stages=f.Prepare(market,[first,delivered],sourceOverride:f.Source with {Loss=ExactDecimal.Parse("0.1")});
        Equal(stages.VolumeNormalizations,1442);
        CheckEnergy(stages,market,first,new(f.From,f.To),"721","4");
        CheckEnergy(stages,market,delivered,new(f.From,f.To),"6489","40");
        var asset=f.Asset with {Key=new(f.Definitions,"synthetic_other_asset")};
        var source=f.Source with {Key=new(f.Definitions,"synthetic_other_source"),Asset=asset.Key,ForecastSeries=Guid.NewGuid(),MeteredSeries=Guid.NewGuid()};
        var different=Instrument(new([source.Key],QuantityStage.Generated,f.Energy.Key),assets:[asset.Key]);
        var sources=f.Prepare(market,[first,different],extraSources:[source],extraAssets:[asset],
            extraSeries:[f.Forecast("0.75") with {Id=source.ForecastSeries},f.Meter() with {Id=source.MeteredSeries}]);
        Equal(sources.VolumeNormalizations,1442);
        CheckEnergy(sources,market,different,new(f.From,f.To),"2163","4");
        var actualMarket=f.Market(f.To,actuals:_=>"42");
        var actual=f.Prepare(actualMarket,[first,other],meter:f.Meter(_=>"50"));
        var revised=f.Prepare(actualMarket,[first,other],meter:f.Meter(_=>"50"),asset:f.Asset with {Versions=[new(f.From,f.To,ExactDecimal.Parse("100"),ExactDecimal.Parse("80"))]});
        Equal(actual.VolumeNormalizations,721);Equal(revised.VolumeNormalizations,721);
        CheckEnergy(actual,actualMarket,first,new(f.From,f.To),"721","4");
        CheckEnergy(revised,actualMarket,first,new(f.From,f.To),"721","2");
        Console.WriteLine("PASS normalized volume reuse: matching source scopes normalize once across sides and instruments; new contexts and different coverage remain independent");
    }
    private static ValueMeasureSet Value(IntervalValuationInputs inputs,PpaAcceptanceFixture.MarketInputs market,PpaInstrument instrument,DeliveryCoverage coverage)=>
        ValueMeasures.From(inputs.Session(market.Build).Value(instrument,coverage,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
    private static void CheckEnergy(IntervalValuationInputs inputs,PpaAcceptanceFixture.MarketInputs market,PpaInstrument instrument,DeliveryCoverage coverage,string numerator,string denominator)
    {
        var energy=Value(inputs,market,instrument,coverage).Rows.Where(r=>r.Slot.Role==PpaRole.EnergyDelivery).Select(r=>r.Quantity).Aggregate((a,b)=>a.Add(b));
        Equal(energy.Numerator,numerator);Equal(energy.Denominator,denominator);
    }
}
