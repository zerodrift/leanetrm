using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

internal static class SeriesIndexWorkChecks
{
    public static void Run()
    {
        var shaped=new ResolverReuseFixture(2);var f=shaped.F;
        var energy=f.Quantities.Single(q=>q.Key==shaped.Unit.Quantity) with {Unit=f.R("megawatt_hour")};
        var capacity=new QuantitySpecification(f.R("synthetic_capacity"),energy.Commodity,f.R("synthetic_capacity_basis"),f.R("megawatt"),f.R("power"));
        var clock=f.R("synthetic_clock");var from=shaped.Spans[0].From;var to=shaped.Spans[^1].To;
        var asset=new GenerationAsset(new(f.Revision,"synthetic_generator"),f.R("location"),capacity,clock,shaped.Clock,[new(from,to,D("100"),null)]);
        var source=new VolumeSourceDefinition(new(f.Revision,"synthetic_volume"),asset.Key,Guid.NewGuid(),QuantityStage.Generated,Guid.NewGuid(),QuantityStage.Generated,
            default,new(null,true),new(null,true),null,true,VolumeRange.GenerationNonNegative,clock,60,new(AssetVolumes.DeadlineVersion,60));
        var forecast=new QuantitySeries(source.ForecastSeries,"capacity_factor_forecast",clock,60,null,
            [..shaped.Shape.Select(c=>new QuantityCell(c.Start,c.End,D("0.5"),1,shaped.At,Guid.NewGuid()))]);
        var meter=new QuantitySeries(source.MeteredSeries,"metered_energy",clock,60,energy.Key,[]);
        var reference=new IntervalReferenceData(f.Revision,f.Reference,ImmutableDictionary<ReferenceKey,PowerClock>.Empty.Add(clock,shaped.Clock),
            new[]{energy,capacity}.ToImmutableDictionary(q=>q.Key),ImmutableDictionary<AssetKey,GenerationAsset>.Empty.Add(asset.Key,asset),
            ImmutableDictionary<VolumeSourceKey,VolumeSourceDefinition>.Empty.Add(source.Key,source),
            [new("synthetic_assignment",asset.Location,ValuedAttribute.Energy,from,to,shaped.Curve.Key,shaped.Unit,default)]);
        var reads=new IntervalReadData(new[]{forecast,meter}.ToImmutableDictionary(s=>s.Id),ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(shaped.Curve.Key,shaped.Prepare()));
        PpaInstrument Instrument(string strike)
        {
            var delivery=new PpaDelivery(clock,60,shaped.First,shaped.First.AddMonths(2));
            var dates=new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DayOfFollowingMonth,null,10,null,ShortMonth.Reject,DateRoll.Unadjusted,null,PaymentRule.Implementation);
            var schedule=new IntervalScheduleParameters(delivery,shaped.Clock,IntervalScheduleCadence.NativeIntervals,new(BlockMembership.All,null,[]));
            var normalization=new PpaNormalization([asset.Key],capacity.Key,PpaNormalizationPolicy.SumCapacityInForce);
            PpaSideTerms Terms(PpaQuantity q)=>new(delivery,q,shaped.Unit.Currency,new(D("1"),capacity.Key),dates,schedule,energy.Key,normalization,
                q is SlotQuantity linked?[..linked.Slots.Select(slot=>new ScheduleInputPort(slot,energy.Key,schedule))]:[]);
            var physical=new PowerPhysicalSide(Guid.NewGuid(),Terms(new VolumeQuantity([source.Key],QuantityStage.Generated,energy.Key)),asset.Location);
            var payment=new FixedSide(Guid.NewGuid(),Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)])),new FlatPrice(new(D(strike),shaped.Unit)));
            return new(Guid.NewGuid(),f.Revision,f.Reference,[new(new(PpaRole.EnergyDelivery),physical,D("1")),new(new(PpaRole.StrikePayment),payment,D("-1"))],false,PpaInstrument.Implementation);
        }
        var instruments=ImmutableArray.Create(Instrument("40"),Instrument("41"));
        var context=Guid.NewGuid();var market=shaped.Model.Build(shaped.Snapshot.WithKnowledge(context,shaped.At),shaped.Requests);
        IntervalValuationInputs Prepare(IntervalReadData? selected=null)=>IntervalValuationInputs.Prepare(reference,selected??reads,instruments,market,shaped.At,context,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,
            "ppa_coverage_v1",[],instruments.ToImmutableDictionary(i=>i.Id,i=>ImmutableArray.Create(new DeliveryCoverage(from,to))));
        var first=Prepare();
        if(first.VolumeComputations!=2)throw new Exception($"Expected two raw source/month computations, got {first.VolumeComputations}.");
        if(first.SeriesValidations!=2 || first.SeriesSelections!=2)
            throw new Exception($"Expected one validation and selection per exact forecast/meter series across two months and four sides; got {first.SeriesValidations} validations / {first.SeriesSelections} selections.");
        if(first.PriceIntervalInspections>shaped.Shape.Length)
            throw new Exception($"Price cadence admission must inspect each prepared interval at most once across both instruments; inspected {first.PriceIntervalInspections} for {shaped.Shape.Length} intervals.");
        var dependencies=first.Dependencies(instruments[0],new(from,to));
        var firstMonth=reads.Curves[shaped.Curve.Key].Dependencies(shaped.First);
        var secondMonth=reads.Curves[shaped.Curve.Key].Dependencies(shaped.First.AddMonths(1));
        if(!dependencies.ToHashSet().SetEquals(shaped.Requests)||!dependencies.SequenceEqual(firstMonth.AddRange(secondMonth).Distinct()))
            throw new Exception("Both monthly peak/off-peak source contracts must remain exact dependencies in first-seen order.");
        if(first.PriceDependencyResolutions>2)
            throw new Exception($"Two monthly curve selections must resolve at most twice, not per interval; got {first.PriceDependencyResolutions}.");
        var narrow=first.Dependencies(instruments[0],new(from,from.AddHours(1)));
        if(!narrow.SequenceEqual(firstMonth)||first.PriceDependencyResolutions!=3)
            throw new Exception("A narrower call must independently retain only its month and exact dependency order.");
        var second=Prepare();
        if(second.PriceIntervalInspections!=first.PriceIntervalInspections)
            throw new Exception("An independent request must perform its own exact price cadence admission.");
        if(second.SeriesValidations!=2 || second.SeriesSelections!=2)
            throw new Exception("An independent request must own two fresh validated/selected indexes.");
        var halfHourlyCurve=shaped.Curve with {Definition=shaped.Curve.Definition with {Grid=shaped.Curve.Definition.Grid with {IntervalMinutes=30}}};
        var halfHourlyShape=shaped.Shape.SelectMany(cell=>new[]{cell with {End=cell.Start.AddMinutes(30)},cell with {Start=cell.Start.AddMinutes(30)}}).ToArray();
        var halfHourly=PreparedCurve.Prepare(halfHourlyCurve,shaped.Spans,halfHourlyShape,[],shaped.At,shaped.At);
        try
        {
            Prepare(reads with {Curves=reads.Curves.SetItem(shaped.Curve.Key,halfHourly)});
            throw new Exception("Matching starts with different interval ends must not admit hourly quantities against half-hourly prices.");
        }
        catch(MarketDataException error) when(error.Code=="ppa_cadence") { }
        Console.WriteLine("PASS series index work: two months, two instruments and four sides validate/select each exact series once per request");
    }
    private static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
}
