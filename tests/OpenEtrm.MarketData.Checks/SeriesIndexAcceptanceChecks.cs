using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

// Independent preservation checks: expected quantities are direct synthetic inputs
// divided by a 200 MW basis, with an external 80 MW binding applied once.
internal static class SeriesIndexAcceptanceChecks
{
    private static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
    private static void Reject(string code,Action run)
    {
        try{run();}catch(MarketDataException e)when(e.Code==code){return;}
        throw new Exception("Expected "+code);
    }
    private static void Equal<T>(T actual,T expected)
    {if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
    private static ExactQuantity Delivery(IntervalValuationInputs inputs,PpaAcceptanceFixture.MarketInputs market,PpaInstrument instrument,DeliveryCoverage scope)
    {
        var result=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,scope,new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted))).Scale(D("80"));
        return result.Rows.Single(r=>r.Kind==ComponentKind.Delivery).Quantity;
    }
    private static void Quantity(ExactQuantity actual,string numerator,string denominator="1")
    {Equal(actual.Numerator,numerator);Equal(actual.Denominator,denominator);}
    public static int Run()
    {
        var count=0;
        void Check(string name,Action test){test();count++;Console.WriteLine("PASS series index independent: "+name);}
        var f=new PpaAcceptanceFixture();var instrument=f.Physical(volume:true);
        var h=f.Hours[0];var scope=new DeliveryCoverage(h.Start,h.End);
        var scopes=ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>>.Empty.Add(instrument.Id,[scope]);
        var future=f.Market(f.From.AddDays(-1),priceSpans:[(h.Start,h.End)]);
        IntervalValuationInputs Prepare(QuantitySeries forecast)=>f.Prepare(future,[instrument],forecastOverride:forecast,coverage:scopes);
        Check("invalid grid and uncertified cells outside selected coverage still refuse",()=>{
            var forecast=f.Forecast();var outside=forecast.Cells[10];
            foreach(var invalid in new[]{outside with{Start=outside.Start.AddMinutes(1)},outside with{ContextId=Guid.Empty},outside with{Revision=0}})
                Reject("volume_grid",()=>Prepare(forecast with{Cells=forecast.Cells.SetItem(10,invalid)}));
        });
        Check("duplicate observations outside coverage cannot be hidden by the index",()=>{
            var forecast=f.Forecast();Reject("volume_grid",()=>Prepare(forecast with{Cells=forecast.Cells.Add(forecast.Cells[10])}));
        });
        Check("revision certification consistency and order apply to the complete supplied history",()=>{
            var forecast=f.Forecast();var outside=forecast.Cells[10];
            Reject("volume_grid",()=>Prepare(forecast with{Cells=forecast.Cells.SetItem(10,outside with{KnownAt=outside.KnownAt.AddSeconds(1)})}));
            Reject("volume_grid",()=>Prepare(forecast with{Cells=forecast.Cells.Add(outside with{Revision=2,KnownAt=outside.KnownAt.AddSeconds(-1)})}));
        });
        Check("explicit zero forecast is distinct from missing selected forecast",()=>{
            Quantity(Delivery(Prepare(f.Forecast("0")),future,instrument,scope),"0");
            Reject("volume_missing",()=>Prepare(f.Forecast() with{Cells=[..f.Forecast().Cells.Where(c=>c.Start!=h.Start)]}));
        });
        Check("future-known forecast revision cannot replace an eligible earlier revision",()=>{
            var forecast=f.Forecast();var newer=forecast.Cells[0] with{Value=D("0.5"),Revision=2,KnownAt=future.Snapshot.ValuationAt.AddHours(1)};
            Quantity(Delivery(Prepare(forecast with{Cells=forecast.Cells.Add(newer)}),future,instrument,scope),"20");
        });
        Check("same series IDs in fresh requests do not reuse changed input values",()=>{
            var original=Prepare(f.Forecast("0.25"));var replacement=Prepare(f.Forecast("0.5"));
            Quantity(Delivery(original,future,instrument,scope),"20");
            Quantity(Delivery(replacement,future,instrument,scope),"40");
            Quantity(Delivery(original,future,instrument,scope),"20");
        });
        Check("later eligible meter correction changes only a separately prepared cutoff",()=>{
            var valuation=h.End.AddHours(2);var oldCutoff=h.End.AddMinutes(1);var newCutoff=h.End.AddMinutes(2);
            var meter=f.Meter() with{Cells=[new(h.Start,h.End,D("50"),1,oldCutoff,f.QuantityKnowledge),new(h.Start,h.End,D("60"),2,newCutoff,f.QuantityKnowledge)]};
            var oldMarket=f.Market(valuation,oldCutoff,actuals:p=>p.Start==h.Start?"40":null,priceSpans:[(h.Start,h.End)]);
            var newMarket=f.Market(valuation,newCutoff,actuals:p=>p.Start==h.Start?"40":null,priceSpans:[(h.Start,h.End)]);
            var before=f.Prepare(oldMarket,[instrument],meter,knownAt:oldCutoff,coverage:scopes);
            var after=f.Prepare(newMarket,[instrument],meter,knownAt:newCutoff,coverage:scopes);
            Quantity(Delivery(before,oldMarket,instrument,scope),"20");Quantity(Delivery(after,newMarket,instrument,scope),"24");
            Quantity(Delivery(before,oldMarket,instrument,scope),"20");
        });
        Check("live intervals never consume meter observations despite a later knowledge cutoff",()=>{
            var cutoff=h.End.AddHours(1);var market=f.Market(h.Start.AddMinutes(-1),cutoff,priceSpans:[(h.Start,h.End)]);
            var meter=f.Meter() with{Cells=[new(h.Start,h.End,D("999"),1,cutoff,f.QuantityKnowledge)]};
            var inputs=f.Prepare(market,[instrument],meter,knownAt:cutoff,coverage:scopes);
            Quantity(Delivery(inputs,market,instrument,scope),"20");
        });
        Check("deadline equality is checked per selected interval after indexing",()=>{
            var source=f.Source with{MeterDue=new(AssetVolumes.DeadlineVersion,60)};var valuation=h.End.AddMinutes(60);
            var first=f.Market(valuation,actuals:p=>p.Start==h.Start?"40":null,priceSpans:[(h.Start,h.End)]);
            Reject("meter_actual_missing",()=>f.Prepare(first,[instrument],sourceOverride:source,coverage:scopes));
            var next=f.Hours[1];var nextScope=new DeliveryCoverage(next.Start,next.End);
            var nextScopes=scopes.SetItem(instrument.Id,[nextScope]);
            var second=f.Market(valuation,actuals:p=>p.Start==next.Start?"40":null,priceSpans:[(next.Start,next.End)]);
            var inputs=f.Prepare(second,[instrument],sourceOverride:source,coverage:nextScopes);
            Quantity(Delivery(inputs,second,instrument,nextScope),"20");
            var earlier=f.Physical("41",volume:true);
            var both=f.Market(valuation,actuals:p=>p.Start==h.Start||p.Start==next.Start?"40":null,priceSpans:[(h.Start,next.End)]);
            // The non-due consumer warms the series index before another consumer
            // requests the earlier cell whose meter is due at this same instant.
            Reject("meter_actual_missing",()=>f.Prepare(both,[instrument,earlier],sourceOverride:source,
                coverage:nextScopes.Add(earlier.Id,[scope])));

        });
        Check("reader-absent meter cell does not resurrect a previous request's observation",()=>{
            var valuation=h.End.AddMinutes(1);var market=f.Market(valuation,actuals:p=>p.Start==h.Start?"40":null,priceSpans:[(h.Start,h.End)]);
            var observed=f.Meter() with{Cells=[new(h.Start,h.End,D("60"),1,h.End,f.QuantityKnowledge)]};
            var before=f.Prepare(market,[instrument],observed,coverage:scopes);
            var absent=f.Prepare(market,[instrument],f.Meter(),coverage:scopes);
            Quantity(Delivery(before,market,instrument,scope),"24");Quantity(Delivery(absent,market,instrument,scope),"20");
            Reject("actuals_required",()=>f.Prepare(market,[instrument],f.Meter(),pending:PendingActualsPolicy.RequireActuals,coverage:scopes));
        });
        Check("an eligible zero meter suppresses forecast rather than becoming missing",()=>{
            var valuation=h.End.AddMinutes(1);var market=f.Market(valuation,actuals:p=>p.Start==h.Start?"40":null,priceSpans:[(h.Start,h.End)]);
            var meter=f.Meter() with{Cells=[new(h.Start,h.End,D("0"),1,h.End,f.QuantityKnowledge)]};
            Quantity(Delivery(f.Prepare(market,[instrument],meter,pending:PendingActualsPolicy.RequireActuals,coverage:scopes),market,instrument,scope),"0");
        });
        Check("a reused series still checks the next consumer's exact header",()=>{
            var secondSource=f.Source with{Key=new(f.Definitions,"synthetic_quarter_source"),MeteredSeries=Guid.NewGuid(),IntervalMinutes=15};
            var secondDelivery=f.Delivery with{IntervalMinutes=15};
            var secondTerms=f.Terms(new VolumeQuantity([secondSource.Key],QuantityStage.Generated,f.Energy.Key)) with{Delivery=secondDelivery,
                Schedule=new(secondDelivery,f.Clock,IntervalScheduleCadence.NativeIntervals,new(BlockMembership.All,null,[]))};
            var physical=new PowerPhysicalSide(Guid.NewGuid(),secondTerms,f.Asset.Location);
            var strike=new FixedSide(Guid.NewGuid(),secondTerms with{Quantity=new SlotQuantity([new(PpaRole.EnergyDelivery)]),InputPorts=[new(new(PpaRole.EnergyDelivery),f.Energy.Key,secondTerms.Schedule)]},new FlatPrice(new(D("40"),f.EnergyPrice)));
            var second=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[new(new(PpaRole.EnergyDelivery),physical,D("1")),new(new(PpaRole.StrikePayment),strike,D("-1"))],false,PpaInstrument.Implementation);
            var meter=new QuantitySeries(secondSource.MeteredSeries,"metered_energy",f.Source.Clock,15,f.Energy.Key,[]);
            // The first instrument populates the hourly forecast index. The second
            // declares15-minute delivery but points to that same60-minute series.
            Reject("volume_source",()=>f.Prepare(future,[instrument,second],extraSources:[secondSource],extraSeries:[meter],
                coverage:scopes.Add(second.Id,[scope])));
        });
        return count;
    }
}
