using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

// One immutable input context owns this preparation cache. It stores raw asset work, never a side
// value: typed sibling bindings and the side's complete normalization remain calculation inputs.
internal sealed class PpaQuantityPreparation(IntervalValuationInputs inputs)
{
    private readonly record struct RawScope(VolumeSourceKey Source,QuantityStage Stage,ReferenceKey Energy,DateOnly Month);
    private readonly Dictionary<RawScope,Dictionary<DateTimeOffset,AssetVolume>> raw=[];
    private readonly QuantitySeriesIndexes seriesIndexes=new(inputs.Context);
    private sealed record NormalizedVolume(PpaDelivery Delivery,QuantityStage Stage,ImmutableArray<VolumeSourceKey> Sources,
        DefinitionIdentity Normalization,ReferenceKey Energy,ImmutableArray<DeliveryCoverage> Coverage,
        ImmutableArray<PreparedQuantityRow> Rows);
    private readonly List<NormalizedVolume> normalizedVolumes=[];
    internal int VolumeComputations { get; private set; }
    internal int VolumeNormalizations { get; private set; }
    internal int SeriesValidations=>seriesIndexes.Validations;
    internal int SeriesSelections=>seriesIndexes.Selections;

    internal ImmutableArray<PreparedQuantityRow> Prepare(Side side,PpaInstrument instrument,
        Func<SideSlot,ImmutableArray<PreparedQuantityRow>> sibling)
    {
        var terms=RetainedSideSchedules.Terms(side);
        var prepared=terms.Quantity switch
        {
            VolumeQuantity volume=>Volume(side,terms,volume,instrument),
            ProfileQuantity profile=>Profile(side,terms.Delivery,profile,instrument),
            SlotQuantity slot=>Slots(slot,terms.InputPorts,sibling),
            AccreditedQuantity accredited when side is CapacityDeliverySide capacity=>Capacity(terms,accredited,capacity,instrument),
            _=>throw Invalid("Select a supported explicit quantity source for this side.")
        };
        if(side is PowerPhysicalSide physical)
        {
            if(physical.Location.Revision!=inputs.Reference.Reference)
                throw PpaErrors.Error("ppa_reference_revision","location","The physical location must retain the selected reference revision.");
            if(prepared.SelectMany(r=>r.Components).Any(c=>c.Location!=physical.Location))
                throw PpaErrors.Error("ppa_delivery_location","location","Every energy asset must deliver at the physical side's explicit location.");
        }
        if(side is CertificateDeliverySide certificate)
        {
            var target=Quantity(certificate.CertificateQuantity);var conversion=side.RetainedPlan?.CertificateConversion??certificate.Conversion;
            if(target.Dimension.Code!="certificate" || conversion.CompareTo(default)<=0 || prepared.Any(r=>Quantity(r.Quantity.Specification).Commodity!=target.Commodity))
                throw Invalid("Certificate conversion requires positive certificates per MWh of the same exact commodity.");
            prepared=[..prepared.Select(row=>row with {Quantity=Convert(row.Quantity,target.Key,conversion),
                Components=[..row.Components.Select(c=>c with {Quantity=Convert(c.Quantity,target.Key,conversion)})]})];
        }
        if(prepared.Any(row=>row.Quantity.Specification!=side.Terms.ScheduleQuantity))
            throw PpaErrors.Error("ppa_quantity_unit","schedule_quantity","Resolved rows must retain the side's declared native quantity type and unit.");
        return prepared;
    }

    private ImmutableArray<PreparedQuantityRow> Volume(Side side,PpaSideTerms terms,VolumeQuantity volume,PpaInstrument instrument)
    {
        var delivery=terms.Delivery;var normalization=terms.Normalization;var assets=Assets(normalization.Assets);
        if(volume.Sources.IsDefaultOrEmpty || volume.Sources.Distinct().Count()!=volume.Sources.Length)
            throw Invalid("Declare distinct exact volume sources.");
        var sources=volume.Sources.Select(key=>inputs.Reference.Sources.TryGetValue(key,out var value) && value.Key==key?value:throw Invalid("A selected volume source is absent from the exact input context.")).ToArray();
        if(sources.Length!=assets.Length || sources.Select(s=>s.Asset).Distinct().Count()!=sources.Length ||
            !sources.Select(s=>s.Asset).ToHashSet().SetEquals(assets.Select(a=>a.Key)))
            throw PpaErrors.Error("ppa_normalization","assets","The source contributions must cover exactly the side's declared normalization assets.");
        if(normalization.Policy!=PpaNormalizationPolicy.SumCapacityInForce||assets.Any(a=>a.CapacityQuantity.Key!=normalization.Basis))
            throw PpaErrors.Error("ppa_normalization","basis","Every asset must retain the side's selected capacity-in-force basis and policy.");
        if(sources.Any(s=>s.Clock!=delivery.Clock || s.IntervalMinutes!=delivery.IntervalMinutes) || assets.Any(a=>a.Clock!=delivery.Clock))
            throw Cadence();
        var energies=sources.Select(s=>Series(s.MeteredSeries).Quantity).Distinct().ToArray();
        if(energies.Length!=1 || energies[0] is not {} energyKey)throw Invalid("Every source must explicitly declare the same exact metered energy specification.");
        if(energyKey!=volume.Energy)throw PpaErrors.Error("ppa_quantity_unit","volume","The selected meter headers must match the side's declared exact energy unit.");
        var energy=Quantity(energyKey);var coverage=inputs.PreparedCoverage(instrument);
        // The owner fixes all source revisions, asset versions, valuation/knowledge instants
        // and policies. Only identical quantity scopes share pre-certificate MWh rows.
        var retained=normalizedVolumes.FirstOrDefault(v=>v.Delivery==delivery && v.Stage==volume.Stage &&
            v.Normalization==DefinitionIdentity.For(normalization) && v.Energy==energy.Key && v.Sources.SequenceEqual(volume.Sources) && v.Coverage.SequenceEqual(coverage));
        if(retained is not null)return retained.Rows;
        var rows=ImmutableArray.CreateBuilder<PreparedQuantityRow>();
        CapacityNormalization.Prepared? normalizationInputs=null;
        foreach(var month in Months(delivery))
        {
            var periods=Periods(side,delivery,month).Where(p=>inputs.Includes(instrument,p.Delivery)).ToArray();
            if(periods.Length==0)continue;
            var contributions=sources.SelectMany(source=>Raw(source,assets.Single(a=>a.Key==source.Asset),volume.Stage,energy,month,periods))
                .GroupBy(v=>v.Interval.Start).ToDictionary(g=>g.Key,g=>g.ToImmutableArray());
            foreach(var period in periods)
            {
                VolumeNormalizations++;
                normalizationInputs??=CapacityNormalization.Prepare(assets,inputs.Context);
                var normalized=normalizationInputs.PerUnit(contributions[period.Start],period.Delivery,volume.Stage);
                rows.Add(new(period.Delivery,normalized.PerUnit,normalized.State,volume.Stage,
                    [..normalized.Contributions.Select(c=>new QuantityComponent(assets.Single(a=>a.Key==c.Volume.Asset).Location,c.PerUnit,c.Volume.Asset))],
                    [..normalized.Contributions.Select(c=>c.Volume)],[]));
            }
        }
        var result=rows.ToImmutable();
        normalizedVolumes.Add(new(delivery,volume.Stage,volume.Sources,DefinitionIdentity.For(normalization),energy.Key,coverage,result));
        return result;
    }

    private ImmutableArray<AssetVolume> Raw(VolumeSourceDefinition source,GenerationAsset asset,QuantityStage stage,
        QuantitySpecification energy,DateOnly month,PowerInterval[] periods)
    {
        var scope=new RawScope(source.Key,stage,energy.Key,month);
        if(!raw.TryGetValue(scope,out var retained))raw.Add(scope,retained=[]);
        var missing=periods.Where(p=>!retained.ContainsKey(p.Start)).ToArray();
        if(missing.Length>0)
        {
            var computed=AssetVolumes.Compute(source,asset,stage,missing,Series(source.ForecastSeries),Series(source.MeteredSeries),inputs.Context,energy,seriesIndexes);
            foreach(var value in computed)retained.Add(value.Interval.Start,value);
            VolumeComputations++;
        }
        return [..periods.Select(p=>retained[p.Start])];
    }

    private ImmutableArray<PreparedQuantityRow> Profile(Side side,PpaDelivery delivery,ProfileQuantity profile,PpaInstrument instrument)
    {
        if(side.RetainedPlan is {} retained)return [..retained.NativeRows.Where(row=>inputs.Includes(instrument,row.Obligation.Interval!))
            .Select(row=>new PreparedQuantityRow(row.Obligation.Interval!,retained.ProfileQuantity(row),VolumeState.Contractual,null,[],[],[]))];
        var source=new ContractProfileQuantity(Clock(delivery.Clock),delivery.IntervalMinutes,profile.Hours,Quantity(profile.Capacity),Quantity(profile.Energy));
        var rows=ImmutableArray.CreateBuilder<PreparedQuantityRow>();
        foreach(var month in Months(delivery))
        {
            var selected=Periods(side,delivery,month).Where(p=>inputs.Includes(instrument,p.Delivery)).Select(p=>p.Start).ToHashSet();
            if(selected.Count==0)continue;
            rows.AddRange(source.Rows(month).Where(r=>selected.Contains(r.Interval.Start))
                .Select(r=>new PreparedQuantityRow(r.Interval,r.PerUnit,r.State,null,[],[],[])));
        }
        return rows.ToImmutable();
    }

    private static ImmutableArray<PreparedQuantityRow> Slots(SlotQuantity quantity,ImmutableArray<ScheduleInputPort> ports,Func<SideSlot,ImmutableArray<PreparedQuantityRow>> sibling)
    {
        if(quantity.Slots.IsDefaultOrEmpty || quantity.Slots.Distinct().Count()!=quantity.Slots.Length)
            throw Invalid("Bind distinct delivery slots explicitly.");
        var selected=quantity.Slots.Select(sibling).ToArray();
        if(ports.IsDefault||!ports.Select(p=>p.Slot).SequenceEqual(quantity.Slots)||
            selected.Where((rows,n)=>rows.Any(row=>row.Quantity.Specification!=ports[n].Quantity)).Any())
            throw PpaErrors.Error("ppa_quantity_unit","input_port","A bound quantity must match its declared exact input type and unit.");
        if(selected.Any(rows=>rows.IsDefault || rows.Select(r=>r.Interval.Start).Distinct().Count()!=rows.Length))
            throw Invalid("Every bound delivery slot must supply distinct native quantity rows.");
        var first=selected[0];
        // One immutable sibling already has the complete native quantity and evidence.
        // Keep the validation above; no aggregation or row reconstruction is needed.
        if(selected.Length==1)
        {
            foreach(var row in first)RequireState(row.State);
            return first;
        }
        if(selected.Any(rows=>rows.Length!=first.Length || !rows.Select(r=>r.Interval).SequenceEqual(first.Select(r=>r.Interval))))throw Cadence();
        return [..Enumerable.Range(0,first.Length).Select(n=>
        {
            var rows=selected.Select(values=>values[n]).ToArray();
            if(rows.Select(r=>r.Quantity.Specification).Distinct().Count()!=1 || rows.Select(r=>r.Stage).Distinct().Count()!=1)
                throw Invalid("Bound delivery rows must retain identical quantity specifications and stages.");
            return new PreparedQuantityRow(first[n].Interval,rows.Select(r=>r.Quantity).Aggregate((a,b)=>a.Add(b)),State(rows.Select(r=>r.State)),first[n].Stage,
                [..rows.SelectMany(r=>r.Components)],[..rows.SelectMany(r=>r.Lineage)],[..rows.SelectMany(r=>r.CapacityVersions)])
                {CapacityDays=[..rows.SelectMany(r=>r.CapacityDays)]};
        })];
    }

    private ImmutableArray<PreparedQuantityRow> Capacity(PpaSideTerms terms,AccreditedQuantity quantity,CapacityDeliverySide side,PpaInstrument instrument)
    {
        var delivery=terms.Delivery;
        if(delivery.From.Day!=1 || delivery.To.Day!=1)
            throw PpaErrors.Error("ppa_segment_boundary","capacity","Capacity quantity requires complete operating months.");
        if(quantity.Assets.IsDefaultOrEmpty || quantity.Assets.Distinct().Count()!=quantity.Assets.Length ||
            !quantity.Assets.ToHashSet().SetEquals(terms.Normalization.Assets))
            throw PpaErrors.Error("ppa_normalization","assets","Capacity entitlement requires the side's explicit normalization asset set.");
        var assets=Assets(quantity.Assets);var target=Quantity(side.CapacityQuantity);var clock=Clock(delivery.Clock);
        if(terms.Normalization.Policy!=PpaNormalizationPolicy.SumCapacityInForce||assets.Any(a=>a.CapacityQuantity.Key!=terms.Normalization.Basis))
            throw PpaErrors.Error("ppa_normalization","basis","Every accredited asset must retain the side's selected capacity-in-force basis and policy.");
        if(assets.Any(a=>a.Clock!=delivery.Clock))throw Cadence();
        if(target.Unit.Code!="megawatt_day" || target.Dimension.Code!="energy" || assets.Any(a=>a.CapacityQuantity.Commodity!=target.Commodity))throw Invalid("MW-day entitlement must retain the assets' exact commodity.");
        var result=ImmutableArray.CreateBuilder<PreparedQuantityRow>();
        foreach(var month in Months(delivery))
        {
            var interval=side.RetainedPlan is {} retained?retained.NativeRows.Single(row=>row.Obligation.Interval!.OperatingDate==month).Obligation.Interval!:
                new DeliveryInterval(clock.StartOfDay(month).ToUniversalTime(),clock.StartOfDay(month.AddMonths(1)).ToUniversalTime(),month,0,0,1);
            if(!inputs.Includes(instrument,interval))continue;
            var components=assets.ToDictionary(a=>a.Key,_=>ExactQuantity.FromQuantity(new(default,target.Key)));
            var versions=ImmutableArray.CreateBuilder<AssetVersionKey>();var days=ImmutableArray.CreateBuilder<CapacityDay>();
            var dailyIntervals=side.RetainedPlan is {} stored?stored.CapacityDays.Where(day=>day.OperatingDate>=month&&day.OperatingDate<month.AddMonths(1)):
                Enumerable.Range(0,DateTime.DaysInMonth(month.Year,month.Month)).Select(n=>new DeliveryInterval(clock.StartOfDay(month.AddDays(n)).ToUniversalTime(),clock.StartOfDay(month.AddDays(n+1)).ToUniversalTime(),month.AddDays(n),0,0,1));
            foreach(var dayInterval in dailyIntervals)
            {
                var day=dayInterval.OperatingDate;var daily=CapacityNormalization.AccreditedPerInterval(assets,dayInterval,inputs.Context);
                versions.AddRange(daily.Versions);
                foreach(var asset in assets)
                {
                    var selected=daily.Versions.Single(v=>v.Asset==asset.Key);
                    var version=asset.Versions.Single(v=>v.From==selected.From);
                    // One operating day, including either DST day, contributes its declared MW ratio.
                    var value=new ExactQuantity(version.AccreditedCapacity!.Value.ScaledValue*daily.CapacityBasis.DenominatorValue,
                        ExactDecimal.WorkingScale*daily.CapacityBasis.NumeratorValue,target.Key).Scale(side.RetainedPlan?.CapacityEntitlementFactor??PpaErrors.One);
                    components[asset.Key]=components[asset.Key].Add(value);
                    days.Add(new(day,selected,asset.Location,value));
                }
            }
            result.Add(new(interval,components.Values.Aggregate((a,b)=>a.Add(b)),VolumeState.Contractual,null,
                [..assets.Select(a=>new QuantityComponent(a.Location,components[a.Key],a.Key))],[],versions.ToImmutable()) {CapacityDays=days.ToImmutable()});
        }
        return result.ToImmutable();
    }

    private ImmutableArray<GenerationAsset> Assets(ImmutableArray<AssetKey> keys)
    {
        if(keys.IsDefaultOrEmpty || keys.Distinct().Count()!=keys.Length)throw Invalid("Supply distinct exact normalization assets.");
        return [..keys.Select(key=>inputs.Reference.Assets.TryGetValue(key,out var asset) && asset.Key==key?asset:throw Invalid("A required asset is absent from the exact input context."))];
    }
    private PowerInterval[] Periods(Side side,PpaDelivery delivery,DateOnly month)=>side.RetainedPlan is {} retained
        ?[..retained.MonthIntervals(month)
            .Select(interval=>{var local=TimeZoneInfo.ConvertTime(interval.Start,Clock(delivery.Clock).Zone);
                return new PowerInterval(interval.Start,interval.End,interval.OperatingDate,interval.HourBeginning,interval.HourBeginning+1,interval.Minute,(int)local.Offset.TotalMinutes,interval.Occurrence,false);})]
        :PowerHours.Compile(delivery.From>month?delivery.From:month,delivery.To<month.AddMonths(1)?delivery.To:month.AddMonths(1),Clock(delivery.Clock),TimeOfUse.All(delivery.IntervalMinutes),[],delivery.IntervalMinutes);
    private static IEnumerable<DateOnly> Months(PpaDelivery delivery)
    {
        for(var month=new DateOnly(delivery.From.Year,delivery.From.Month,1);month<delivery.To;month=month.AddMonths(1))yield return month;
    }
    private QuantitySeries Series(Guid id)=>inputs.Read.Series.TryGetValue(id,out var series) && series.Id==id?series:throw Invalid("A required exact forecast or meter series is absent from the selected read.");
    private QuantitySpecification Quantity(ReferenceKey key)
    {
        if(key.Revision!=inputs.Reference.Reference)
            throw PpaErrors.Error("ppa_reference_revision","quantity","Quantity specifications must retain the selected reference revision.");
        if(!inputs.Reference.Quantities.TryGetValue(key,out var value) || value.Key!=key)
            throw Invalid("A required exact quantity specification is absent from the selected reference revision.");
        AssetDefinitions.Specification(value,inputs.Reference.Reference);
        return value;
    }
    private PowerClock Clock(ReferenceKey key)=>inputs.Reference.Clocks.TryGetValue(key,out var value)?value:throw Invalid("A required exact operating clock is absent from the selected reference revision.");
    private static ExactQuantity Convert(ExactQuantity value,ReferenceKey target,ExactDecimal factor)=>new(value.NumeratorValue*factor.ScaledValue,value.DenominatorValue*ExactDecimal.WorkingScale,target);
    private static void RequireState(VolumeState state)
    {
        if(!Enum.IsDefined(state))throw Invalid("Every quantity contribution must retain its explicit state.");
    }
    private static VolumeState State(IEnumerable<VolumeState> values)
    {
        var states=values.ToArray();
        foreach(var state in states)RequireState(state);
        return states.Contains(VolumeState.Estimated)?VolumeState.Estimated:states.Contains(VolumeState.Forecast)?VolumeState.Forecast:
            states.Contains(VolumeState.Metered)?VolumeState.Metered:VolumeState.Contractual;
    }
    private static MarketDataException Cadence()=>PpaErrors.Error("ppa_cadence","quantity","Delivery quantities require identical native clocks and row grids; no resampling is installed.");
    private static MarketDataException Invalid(string message)=>PpaErrors.Error("ppa_quantity_source","quantity",message);
}
