using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

// Synthetic definitions and observations only; no expected values are obtained
// from the managed valuation being tested.
internal sealed class PpaAcceptanceFixture
{
    internal static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
    internal static DateTimeOffset At(string value)=>DateTimeOffset.Parse(value,System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
    internal DefinitionRevision Definitions { get; }=new(Guid.NewGuid(),Guid.NewGuid());
    internal ReferenceRevision Reference { get; }=new(Guid.NewGuid(),Guid.NewGuid());
    internal Guid QuantityKnowledge { get; }=Guid.NewGuid();
    internal ReferenceKey R(string code)=>new(Reference,code);
    internal CurveKey C(string code)=>new(Definitions,code);
    internal PowerClock Clock { get; }=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
    internal DateOnly Month { get; }=new(2031,11,1);
    internal int Minutes { get; }
    internal DateTimeOffset From=>Clock.StartOfDay(Month).ToUniversalTime();
    internal DateTimeOffset To=>Clock.StartOfDay(Month.AddMonths(1)).ToUniversalTime();
    internal ImmutableArray<PowerInterval> Hours { get; }
    internal QuantitySpecification Energy { get; }
    internal QuantitySpecification Capacity { get; }
    internal QuantitySpecification Certificates { get; }
    internal QuantitySpecification CapacityDays { get; }
    internal Quantity UnitMw=>new(D("1"),Capacity.Key);
    internal PriceUnit EnergyPrice=>new(R("synthetic_usd"),Energy.Key,D("1"));
    internal PriceUnit CertificatePrice=>new(R("synthetic_usd"),Certificates.Key,D("1"));
    internal PriceUnit CapacityPrice=>new(R("synthetic_usd"),CapacityDays.Key,D("1"));
    internal GenerationAsset Asset { get; }
    internal VolumeSourceDefinition Source { get; }
    internal ImmutableArray<LocationAssignment> Assignments { get; }
    internal PaymentRule Payment(int day=10)=>new(PaymentFrequency.Monthly,PaymentDateRule.DayOfFollowingMonth,null,day,null,
        ShortMonth.Reject,DateRoll.Unadjusted,null,PaymentRule.Implementation);
    internal PpaDelivery Delivery=>new(R("synthetic_clock"),Minutes,Month,Month.AddMonths(1));
    internal IntervalScheduleParameters Schedule(PpaQuantity quantity)=>new(Delivery,Clock,
        quantity is AccreditedQuantity || quantity is SlotQuantity slots && slots.Slots.All(s=>s.Role==PpaRole.CapacityDelivery)
            ?IntervalScheduleCadence.OperatingMonths:IntervalScheduleCadence.NativeIntervals,
        quantity is AccreditedQuantity || quantity is SlotQuantity capacity && capacity.Slots.All(s=>s.Role==PpaRole.CapacityDelivery)
            ?null:quantity is ProfileQuantity profile?profile.Hours:new(BlockMembership.All,null,[]));
    internal PpaNormalization Normalization(ImmutableArray<AssetKey> assets)=>new(assets,Capacity.Key,
        assets.IsEmpty?PpaNormalizationPolicy.ContractUnit:PpaNormalizationPolicy.SumCapacityInForce);
    internal PpaSideTerms Terms(PpaQuantity quantity,int paymentDay=10,PpaNormalization? normalization=null,ReferenceKey? scheduleQuantity=null)
    {
        var schedule=Schedule(quantity);
        var assets=quantity switch {VolumeQuantity=>ImmutableArray.Create(Asset.Key),AccreditedQuantity a=>a.Assets,
            SlotQuantity slots when slots.Slots.All(s=>s.Role is PpaRole.CertificateDelivery or PpaRole.CapacityDelivery)=>[Asset.Key],_=>[]};
        var ports=quantity is SlotQuantity linked?linked.Slots.Select(slot=>new ScheduleInputPort(slot,
            slot.Role switch{PpaRole.CertificateDelivery=>Certificates.Key,PpaRole.CapacityDelivery=>CapacityDays.Key,_=>Energy.Key},schedule)).ToImmutableArray():[];
        var output=scheduleQuantity??(quantity is AccreditedQuantity?CapacityDays.Key:ports.IsEmpty?Energy.Key:ports[0].Quantity);
        return new(Delivery,quantity,R("synthetic_usd"),UnitMw,Payment(paymentDay),schedule,output,normalization??Normalization(assets),ports);
    }
    internal ProfileQuantity Profile=>new(Capacity.Key,Energy.Key,new(BlockMembership.All,null,[]));
    internal VolumeQuantity Volume=>new([Source.Key],QuantityStage.Generated,Energy.Key);

    internal PpaAcceptanceFixture(int minutes=60)
    {
        Minutes=minutes;
        Energy=new(R("synthetic_mwh"),R("synthetic_power"),R("synthetic_electricity"),R("megawatt_hour"),R("energy"));
        Capacity=new(R("synthetic_mw"),R("synthetic_power"),R("synthetic_electricity"),R("megawatt"),R("power"));
        Certificates=new(R("synthetic_rec"),R("synthetic_power"),R("synthetic_generation_certificate"),R("rec"),R("certificate"));
        CapacityDays=new(R("synthetic_mw_day"),R("synthetic_power"),R("synthetic_capacity_entitlement"),R("megawatt_day"),R("energy"));
        Hours=[..PowerHours.Compile(Month,Month.AddMonths(1),Clock,TimeOfUse.All(Minutes),[],Minutes)];
        Asset=new(new(Definitions,"synthetic_generator"),R("synthetic_node"),Capacity,R("synthetic_clock"),Clock,
            [new(From,To,D("200"),D("160"))]);
        Source=new(new(Definitions,"synthetic_generation"),Asset.Key,Guid.NewGuid(),QuantityStage.Generated,Guid.NewGuid(),QuantityStage.Generated,
            D("0"),new(null,true),new(null,true),null,true,VolumeRange.GenerationNonNegative,R("synthetic_clock"),Minutes,new("meter_due_v1",129600));
        Assignments=[new("synthetic_energy_assignment",Asset.Location,ValuedAttribute.Energy,From,To,C("synthetic_energy"),EnergyPrice,D("0")),
            new("synthetic_certificate_assignment",Asset.Location,ValuedAttribute.Certificate,From,To,C("synthetic_certificate"),CertificatePrice,D("0")),
            new("synthetic_capacity_assignment",Asset.Location,ValuedAttribute.Capacity,From,To,C("synthetic_capacity"),CapacityPrice,D("0"))];
    }

    internal QuantitySeries Forecast(string factor="0.25")=>new(Source.ForecastSeries,"capacity_factor_forecast",Source.Clock,Minutes,null,
        [..Hours.Select(h=>new QuantityCell(h.Start,h.End,D(factor),1,From.AddDays(-10),QuantityKnowledge))]);
    internal QuantitySeries Meter(Func<PowerInterval,string?>? values=null,DateTimeOffset? learned=null)=>new(Source.MeteredSeries,"metered_energy",Source.Clock,Minutes,Energy.Key,
        values is null?[]:[..Hours.Where(h=>values(h) is not null).Select(h=>new QuantityCell(h.Start,h.End,D(values(h)!),1,learned??To,QuantityKnowledge))]);

    internal IntervalValuationInputs Prepare(MarketInputs market,IEnumerable<PpaInstrument> instruments,QuantitySeries? meter=null,
        GenerationAsset? asset=null,ImmutableArray<LocationAssignment>? assignments=null,PendingActualsPolicy pending=PendingActualsPolicy.EstimateUntilActual,
        DateTimeOffset? knownAt=null,QuantitySeries? forecastOverride=null,ImmutableArray<VolumeSourceDefinition> extraSources=default,
        ImmutableArray<QuantitySeries> extraSeries=default,ImmutableArray<GenerationAsset> extraAssets=default,VolumeSourceDefinition? sourceOverride=null,
        ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>>? coverage=null)
    {
        var selectedInstruments=instruments.ToImmutableArray();
        var selectedCoverage=coverage??selectedInstruments.ToImmutableDictionary(i=>i.Id,i=>ImmutableArray.Create(new DeliveryCoverage(From,To)));
        var forecast=forecastOverride??Forecast();var selectedMeter=meter??Meter();var selectedAsset=asset??Asset;
        var reference=new IntervalReferenceData(Definitions,Reference,new Dictionary<ReferenceKey,PowerClock>{{R("synthetic_clock"),Clock}}.ToImmutableDictionary(),
            new[]{Energy,Capacity,Certificates,CapacityDays}.ToImmutableDictionary(q=>q.Key),new[]{selectedAsset}.Concat(extraAssets.IsDefault?[]:extraAssets).ToImmutableDictionary(a=>a.Key),
            new[]{sourceOverride??Source}.Concat(extraSources.IsDefault?[]:extraSources).ToImmutableDictionary(s=>s.Key),assignments??Assignments);
        var reads=new IntervalReadData(new[]{forecast,selectedMeter}.Concat(extraSeries.IsDefault?[]:extraSeries).ToImmutableDictionary(s=>s.Id),market.Curves.ToImmutableDictionary());
        return IntervalValuationInputs.Prepare(reference,reads,selectedInstruments,market.Build,knownAt??market.Snapshot.Knowledge!.KnownAt,
            QuantityKnowledge,IntervalKnowledgePolicy.ResolvedCutoff,pending,"ppa_coverage_v1",ImmutableHashSet.Create(Month),selectedCoverage);
    }

    internal PpaInstrument Physical(string strike="40",bool volume=false,bool discounting=false)
    {
        var normalization=Normalization(volume?[Asset.Key]:[]);
        var physical=new PowerPhysicalSide(Guid.NewGuid(),Terms(volume?Volume:Profile,normalization:normalization),Asset.Location);
        var cash=new FixedSide(Guid.NewGuid(),Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)]),normalization:normalization),new FlatPrice(new(D(strike),EnergyPrice)));
        return new(Guid.NewGuid(),Definitions,Reference,[new(new(PpaRole.EnergyDelivery),physical,D("1")),new(new(PpaRole.StrikePayment),cash,D("-1"))],discounting,"ppa_instrument_v2");
    }

    internal PpaInstrument CapacityAgreement(string strike="0")
    {
        var physical=new CapacityDeliverySide(Guid.NewGuid(),Terms(new AccreditedQuantity([Asset.Key])),CapacityDays.Key,"mw_day_per_operating_day_v1");
        var cash=new FixedSide(Guid.NewGuid(),Terms(new SlotQuantity([new(PpaRole.CapacityDelivery)])),new FlatPrice(new(D(strike),CapacityPrice)));
        return new(Guid.NewGuid(),Definitions,Reference,[new(new(PpaRole.CapacityDelivery),physical,D("1")),new(new(PpaRole.CapacityPayment),cash,D("-1"))],false,"ppa_instrument_v2");
    }

    internal PpaInstrument WithCertificate(bool cash=true)
    {
        var normalization=Normalization([Asset.Key]);
        var physical=new PowerPhysicalSide(Guid.NewGuid(),Terms(Volume),Asset.Location);
        var strike=new FixedSide(Guid.NewGuid(),Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)]),normalization:normalization),new FlatPrice(new(D("0"),EnergyPrice)));
        var certificate=new CertificateDeliverySide(Guid.NewGuid(),Terms(Volume,scheduleQuantity:Certificates.Key),Certificates.Key,D("1"),"asset_generation_at_stage_v1","held_at_current_vintage_mark_v1","none_recorded_v1");
        var members=ImmutableArray.CreateBuilder<PpaMembership>();
        members.Add(new(new(PpaRole.EnergyDelivery),physical,D("1")));members.Add(new(new(PpaRole.StrikePayment),strike,D("-1")));
        members.Add(new(new(PpaRole.CertificateDelivery),certificate,D("1")));
        if(cash)
        {
            var payment=new FixedSide(Guid.NewGuid(),Terms(new SlotQuantity([new(PpaRole.CertificateDelivery)])),new FlatPrice(new(D("3"),CertificatePrice)));
            members.Add(new(new(PpaRole.CertificatePayment),payment,D("-1")));
        }
        return new(Guid.NewGuid(),Definitions,Reference,members.ToImmutable(),false,"ppa_instrument_v2");
    }

    internal MarketInputs Market(DateTimeOffset valuationAt,DateTimeOffset? knownAt=null,string energy="42",string certificate="5",string capacity="4",string hub="30",
        Func<PowerInterval,string?>? actuals=null,int priceDeadlineMinutes=10080,bool annualCertificate=false,bool capacityFixing=false,ImmutableArray<(DateTimeOffset From,DateTimeOffset To)> priceSpans=default,
        ExactDecimal? energyQuoteMultiplier=null)
    {
        var cutoff=knownAt??valuationAt;
        var energyUnit=new PriceUnit(EnergyPrice.Currency,EnergyPrice.Quantity,energyQuoteMultiplier??D("1"));
        var specifications=new[]{("synthetic_energy",energyUnit,energy),("synthetic_certificate",CertificatePrice,certificate),("synthetic_capacity",CapacityPrice,capacity),("synthetic_hub",EnergyPrice,hub),("synthetic_capacity_other",CapacityPrice,"5")};
        var contracts=specifications.Select((s,i)=>new MarketContract(new(Definitions,Guid.NewGuid()),ContractKind.Forward,
            i==1 && annualCertificate?new(Month.Year,1,1):Month,i==1 && annualCertificate?new(Month.Year+1,1,1):Month.AddMonths(1),To.AddYears(2),null)).ToImmutableArray();
        var quotes=specifications.Select(s=>new QuoteSpecification(R(s.Item1+"_quote"),QuoteKind.Outright,s.Item2.Currency,s.Item2.Quantity,s.Item2.Multiplier,D("0.01"),D("0.01"),D("-10000"),D("10000"))).ToImmutableArray();
        var curves=specifications.Select((s,i)=>new Curve(C(s.Item1),quotes[i].Key,R("synthetic_node"),R("synthetic_all_hours"),
            new(SettlementKind.Index,R(s.Item1+"_index"),null),CurveRule.Direct,[contracts[i].Key],[],ObservationRole.TraderMark,R("synthetic_source"),null)).ToImmutableArray();
        var series=contracts.Select((c,i)=>new MarketSeries(new(Definitions,Guid.NewGuid()),c.Key,quotes[i].Key,null)).ToImmutableArray();
        var model=new CurveModel(Definitions,Reference,[Energy,Capacity,Certificates,CapacityDays],quotes,contracts,curves,[],[],series);
        var snapshot=new MarketSnapshot(Guid.NewGuid(),Definitions,valuationAt,valuationAt,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [..series.Select((s,i)=>new SelectedObservation(new(s.Key,InputUse.Variable),new(new(Definitions.CatalogId,Guid.NewGuid()),s.Key,R("synthetic_source"),
                ObservationRole.TraderMark,PublicationState.Final,valuationAt,D(specifications[i].Item3))))]);
        var fixing=new IndexFixing(R("synthetic_capacity_index"),quotes[2].Key,Month,Month.AddMonths(1),To,series[2].Key,R("synthetic_fixing_source"),IndexFixing.SupportedImplementation);
        if(capacityFixing)
            snapshot=snapshot with {Selections=snapshot.Selections.Add(new(new(series[2].Key,InputUse.FrozenFact),
                new(new(Definitions.CatalogId,Guid.NewGuid()),series[2].Key,fixing.Source,ObservationRole.IndexFixing,PublicationState.Final,fixing.FixingAt,D(capacity))))};
        var energyActuals=actuals is null?[]:Hours.Where(h=>actuals(h) is not null).Select(h=>new IntervalActual(h.Start,h.End,new(D(actuals(h)!),energyUnit),h.End,Guid.NewGuid(),1)).ToArray();
        var prepared=new Dictionary<CurveKey,PreparedCurve>();
        foreach(var i in new[]{0,3})
        {
            var shape=new ShapedCurveDefinition("synthetic_ppa_shape_"+i,ShapeMethod.Additive,BlockShaping.CalibrationPolicy,BlockShaping.OperatorVersion,Guid.NewGuid(),
                new(Clock,Minutes,[]),[new(curves[i].Key,BlockMembership.All,null,[contracts[i]])]);
            var hourly=new ShapedIntervalCurve(curves[i].Key,specifications[i].Item2,new("interval_end",priceDeadlineMinutes,IntervalActualRule.SupportedVersion),shape);
            prepared.Add(curves[i].Key,PreparedCurve.Prepare(hourly,priceSpans.IsDefault?[(From,To)]:priceSpans,[..Hours.Select(h=>new SeriesCell(h.Start,h.End,D("0"),1))],energyActuals,valuationAt,cutoff));
        }
        foreach(var i in new[]{1,2,4})
            prepared.Add(curves[i].Key,PreparedCurve.Prepare(new PeriodContractCurve(curves[i].Key,specifications[i].Item2,R(specifications[i].Item1+"_index"),[contracts[i]],i==2 && capacityFixing?[fixing]:[]),
                [(new DateTimeOffset(contracts[i].DeliveryFrom.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero),new DateTimeOffset(contracts[i].DeliveryTo.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero))],[],[],valuationAt,cutoff));
        snapshot=snapshot.WithKnowledge(QuantityKnowledge,cutoff);
        var requests=prepared.Values.SelectMany(p=>p.Months.SelectMany(month=>p.Dependencies(month))).Distinct().ToArray();
        var build=model.Build(snapshot,[..requests]);
        var bumps=specifications.Select((s,i)=>new KeyValuePair<string,QuoteBump>(s.Item1,new(new(series[i].Key,InputUse.Variable),quotes[i].Key,s.Item2,D("1"),QuoteBump.Convention))).ToImmutableDictionary();
        return new(model,snapshot,build,prepared,bumps);
    }

    internal sealed record MarketInputs(CurveModel Model,MarketSnapshot Snapshot,CurveBuild Build,IReadOnlyDictionary<CurveKey,PreparedCurve> Curves,ImmutableDictionary<string,QuoteBump> Bumps);
}
