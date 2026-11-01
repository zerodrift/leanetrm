using System.Collections.Immutable;
using System.Collections.Concurrent;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public sealed record IntervalReferenceData(DefinitionRevision Definitions,ReferenceRevision Reference,
    ImmutableDictionary<ReferenceKey,PowerClock> Clocks,ImmutableDictionary<ReferenceKey,QuantitySpecification> Quantities,
    ImmutableDictionary<AssetKey,GenerationAsset> Assets,ImmutableDictionary<VolumeSourceKey,VolumeSourceDefinition> Sources,
    ImmutableArray<LocationAssignment> Assignments);
public enum IntervalKnowledgePolicy { ResolvedCutoff = 1, ValuationInstant }
public sealed record IntervalReadData(ImmutableDictionary<Guid,QuantitySeries> Series,ImmutableDictionary<CurveKey,PreparedCurve> Curves);
internal sealed record PreparedPriceBinding(ExactQuantity Quantity,CurveKey Curve,ExactDecimal Multiplier,UnitPrice Adder,PriceSettlementSelection? Settlement,AssetKey? Asset=null);
internal sealed record PreparedRowInputs(ScheduleRow Schedule,PreparedQuantityRow Quantity,ImmutableArray<PreparedPriceBinding> Prices)
{ internal DateTimeOffset ValueAt=>Schedule.PaymentAt; }
internal sealed record PreparedScheduleScope(long Rows,long DetailRows,ImmutableArray<SidePaymentKey> FutureKeys);
public sealed record ShapeRevisionPin(CurveKey Curve,Guid Series,int Revision);
public sealed record ValuationReceipt(DefinitionRevision Definitions,ReferenceRevision Reference,Guid Snapshot,DateTimeOffset ValuationAt,
    DateTimeOffset KnownAt,Guid KnowledgeContext,PendingActualsPolicy Pending,string Coverage,
    ImmutableArray<(Guid Series,int Revision,DateTimeOffset KnownAt,Guid Context)> QuantityRevisions,
    ImmutableArray<(Guid Batch,int? Revision,DateTimeOffset KnownAt)> Actuals,ImmutableArray<string> Implementations)
{
    public required IntervalKnowledgePolicy KnowledgePolicy { get; init; }
    public required MarketSnapshot MarketInputs { get; init; }
    public required IntervalReferenceData ReferenceInputs { get; init; }
    public required ImmutableArray<PpaInstrument> Instruments { get; init; }
    public required ImmutableArray<ShapeRevisionPin> Shapes { get; init; }
    public required ImmutableArray<IndexFixing> FixingRules { get; init; }
    public required ImmutableArray<AssetVersionKey> AssetVersions { get; init; }
    public required ImmutableHashSet<DateOnly> DetailMonths { get; init; }
    public required ImmutableDictionary<CurveKey,PreparedCurve> CurveInputs { get; init; }
    public required ImmutableArray<PriceCurve> PriceDefinitions { get; init; }
    public required ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>> RequestedCoverage { get; init; }
}

// Inputs are owned by one authorized calculation. No global cache or implicit current-data read exists.
public sealed class IntervalValuationInputs
{
    public IntervalReferenceData Reference { get; }
    public IntervalReadData Read { get; }
    public MarketSnapshot Snapshot=>BaseMarket.Snapshot;
    public DateTimeOffset KnownAt { get; }
    public IntervalKnowledgePolicy KnowledgePolicy { get; }
    public PendingActualsPolicy Policy { get; }
    public VolumeContext Context=>new(Reference.Definitions,Reference.Reference,Snapshot.ValuationAt,KnownAt,Policy);
    public Guid KnowledgeContext { get; }
    public ImmutableHashSet<DateOnly> DetailMonths { get; }
    public ValuationReceipt Receipt { get; private set; }=null!;
    public int VolumeComputations=>quantities.VolumeComputations;
    public int VolumeNormalizations=>quantities.VolumeNormalizations;
    public int SeriesValidations=>quantities.SeriesValidations;
    public int SeriesSelections=>quantities.SeriesSelections;
    public int PriceIntervalInspections { get; private set; }
    private int priceDependencyResolutions;
    public int PriceDependencyResolutions=>Volatile.Read(ref priceDependencyResolutions);
    internal CurveBuild BaseMarket { get; }
    internal IntervalPriceResolver BaseResolver { get; }
    private readonly ImmutableDictionary<Guid,PpaInstrument> instruments;
    private readonly Dictionary<(Guid Instrument,Guid Side),ImmutableArray<PreparedRowInputs>> rows=[];
    private readonly Dictionary<(Guid Instrument,Guid Side),IntervalSideSchedule> schedules=[];
    private readonly ConcurrentDictionary<(Guid Instrument,Guid Side,DeliveryCoverage Coverage),ImmutableArray<PreparedRowInputs>> scopedRows=[];
    private readonly ConcurrentDictionary<(Guid Instrument,DeliveryCoverage Coverage),PreparedScheduleScope> scopes=[];
    private readonly Dictionary<(Guid Instrument,Guid Side),ImmutableArray<PreparedQuantityRow>> quantityRows=[];
    private readonly PpaQuantityPreparation quantities;
    private readonly Dictionary<(CurveKey Curve,DateOnly Month),HashSet<(DateTimeOffset Start,DateTimeOffset End)>> priceIntervals=[];
    private readonly ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>> preparedCoverage;
    private IntervalValuationInputs(IntervalReferenceData reference,IntervalReadData read,ImmutableArray<PpaInstrument> instruments,
        CurveBuild baseMarket,DateTimeOffset knownAt,Guid knowledgeContext,IntervalKnowledgePolicy knowledgePolicy,PendingActualsPolicy pending,string coveragePolicy,ImmutableHashSet<DateOnly> detailMonths,
        ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>> coverage)
    {
        if(reference is null || read is null || reference.Clocks is null || reference.Quantities is null || reference.Assets is null || reference.Sources is null || reference.Assignments.IsDefault ||
            read.Series is null || read.Curves is null || baseMarket is null || baseMarket.Shock is not null || baseMarket.Snapshot.Definitions!=reference.Definitions ||
            knownAt.Offset!=TimeSpan.Zero || knownAt.Ticks%10!=0 || knowledgeContext==Guid.Empty || !Enum.IsDefined(knowledgePolicy) || !Enum.IsDefined(pending) || coveragePolicy!="ppa_coverage_v1" || detailMonths is null ||
            detailMonths.Any(d=>d.Day!=1) || instruments.IsDefaultOrEmpty || instruments.Any(i=>i is null) || instruments.Select(i=>i.Id).Distinct().Count()!=instruments.Length)
            throw PpaErrors.Error("ppa_context","inputs","Prepare exact base-market inputs, certified knowledge and explicit valuation policies.");
        if(baseMarket.Snapshot.Knowledge is not{} knowledge||knowledge.ContextId!=knowledgeContext||
            (knowledgePolicy switch {
                IntervalKnowledgePolicy.ResolvedCutoff=>knownAt!=knowledge.KnownAt,
                IntervalKnowledgePolicy.ValuationInstant=>knownAt!=baseMarket.Snapshot.ValuationAt||knownAt>knowledge.KnownAt,
                _=>true }))
            throw PpaErrors.Error("ppa_context","knowledge","Use one resolved context and the explicit resolved-cutoff or valuation-instant source policy.");
        if(coverage is null || coverage.Count!=instruments.Length || instruments.Any(i=>!coverage.ContainsKey(i.Id)) ||
            coverage.Values.Any(v=>v.IsDefaultOrEmpty || v.Any(c=>c is null)))
            throw PpaErrors.Error("ppa_segment_coverage","prepared_coverage","Declare nonempty exact input coverage for every selected instrument.");
        Reference=reference;Read=read;BaseMarket=baseMarket;KnownAt=knownAt;KnowledgeContext=knowledgeContext;KnowledgePolicy=knowledgePolicy;Policy=pending;DetailMonths=detailMonths;
        this.instruments=instruments.ToImmutableDictionary(i=>i.Id);quantities=new(this);
        preparedCoverage=coverage.ToImmutableDictionary(p=>p.Key,p=>Union(p.Value));
        if(read.Curves.Any(p=>p.Key!=p.Value.Key || p.Key.Revision!=reference.Definitions || p.Value.ValuationAt!=Snapshot.ValuationAt || p.Value.KnownAt!=knownAt))
            throw PpaErrors.Error("ppa_context","curves","Every curve must retain the request's exact pins and cutoffs.");
        BaseResolver=new(baseMarket,read.Curves);
        foreach(var instrument in instruments)
        {
            Require(instrument);
            foreach(var span in coverage[instrument.Id])ValidateTermCoverage(instrument,span);
            if(!reference.Quantities.TryGetValue(instrument.UnitQuantity.Specification,out var unit) || unit.Unit.Code!="megawatt" || unit.Dimension.Code!="power")
                throw PpaErrors.Error("ppa_quantity_unit","contract_unit","The contract unit is exactly one MW.");
            var assets=instrument.Memberships.SelectMany(m=>m.Side.Terms.Quantity switch {
                VolumeQuantity v=>v.Sources.Select(k=>reference.Sources.TryGetValue(k,out var s)?s.Asset:throw PpaErrors.Error("volume_source",m.Slot.ToString(),"Missing exact source.")),
                AccreditedQuantity a=>a.Assets,_=>[]}).Distinct().ToHashSet();
            if(!assets.SetEquals(instrument.NormalizationAssets))throw PpaErrors.Error("ppa_normalization","assets","Normalization assets must equal the declared asset-derived source set.");
            foreach(var member in instrument.Memberships)
            {
                if(!Reference.Clocks.TryGetValue(member.Side.Terms.Delivery.Clock,out var captured)||captured.Fingerprint!=member.Side.Terms.Schedule.Clock.Fingerprint)
                    throw PpaErrors.Error("ppa_reference_revision","clock","The side's captured delivery clock must equal its exact pinned reference rule.");
                if(member.Side.GenerateSchedule() is not IntervalSideSchedule schedule)
                    throw PpaErrors.Error("ppa_cadence","schedule","An interval instrument requires interval schedules from every shared side.");
                schedules.Add((instrument.Id,member.Side.Id),schedule);
            }
            foreach(var member in instrument.Memberships)Quantity(instrument,member.Side);
            foreach(var member in instrument.Memberships)rows.Add((instrument.Id,member.Side.Id),PrepareRows(instrument,member.Side));
        }
        priceIntervals.Clear();priceIntervals.TrimExcess();
        var volumes=quantityRows.Values.SelectMany(v=>v).SelectMany(r=>r.Lineage).Distinct().ToArray();
        var actuals=read.Curves.Values.SelectMany(c=>c.Periods.Values).SelectMany(p=>p.Actuals.Values).Distinct().ToArray();
        Receipt=new(reference.Definitions,reference.Reference,Snapshot.Id,Snapshot.ValuationAt,knownAt,knowledgeContext,pending,coveragePolicy,
            [..volumes.Select(v=>(v.Series,v.Revision,v.KnownAt,v.ContextId)).Distinct()],
            [..actuals.Select(a=>(a.BatchId,a.Revision,a.KnownAt)).Distinct()],
            ["exact_interval_leg_pv_v1","ppa_coverage_v1",PpaCapabilities.Version,AssetVolumes.Implementation,CapacityNormalization.Implementation,
             "anniversary_step_v1","mw_day_per_operating_day_v1","held_at_current_vintage_mark_v1","none_recorded_v1",
             BlockShaping.OperatorVersion,BlockShaping.CalibrationPolicy,IntervalPricing.AggregatePolicy])
        {
            KnowledgePolicy=knowledgePolicy,MarketInputs=Snapshot,ReferenceInputs=reference,Instruments=instruments,DetailMonths=detailMonths,RequestedCoverage=coverage,
            CurveInputs=read.Curves,PriceDefinitions=[..read.Curves.Values.Select(c=>c.Curve)],
            Shapes=[..read.Curves.Values.SelectMany(curve=>curve.Periods.Values.Where(period=>period.Shape is not null)
                .SelectMany(period=>period.Shape!.Blocks.SelectMany(block=>block.Cells.Select(cell=>
                    new ShapeRevisionPin(curve.Key,period.Shape.Definition.ShapeSeries,cell.Revision))))).Distinct()],
            FixingRules=[..read.Curves.Values.Select(c=>c.Curve).OfType<PeriodContractCurve>().SelectMany(c=>c.Fixings).Distinct()],
            AssetVersions=[..quantityRows.Values.SelectMany(v=>v).SelectMany(row=>row.CapacityVersions.Concat(row.Lineage.Select(v=>v.AssetVersion))).Distinct()]
        };
    }
    public static IntervalValuationInputs Prepare(IntervalReferenceData reference,IntervalReadData read,ImmutableArray<PpaInstrument> instruments,
        CurveBuild baseMarket,DateTimeOffset knownAt,Guid knowledgeContext,IntervalKnowledgePolicy knowledgePolicy,PendingActualsPolicy pending,string coveragePolicy,ImmutableHashSet<DateOnly> detailMonths,
        ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>> coverage)=>
        new(reference,read,instruments,baseMarket,knownAt,knowledgeContext,knowledgePolicy,pending,coveragePolicy,detailMonths,coverage);
    private static ImmutableArray<DeliveryCoverage> Union(ImmutableArray<DeliveryCoverage> coverage)
    {
        var spans=new List<DeliveryCoverage>();
        foreach(var span in coverage.OrderBy(c=>c.From))
            if(spans.Count>0 && span.From<=spans[^1].To)
                spans[^1]=new(spans[^1].From,span.To>spans[^1].To?span.To:spans[^1].To);
            else spans.Add(span);
        return [..spans];
    }
    internal ImmutableArray<DeliveryCoverage> PreparedCoverage(PpaInstrument instrument)=>preparedCoverage[instrument.Id];
    internal bool Includes(PpaInstrument instrument,DeliveryInterval interval)=>
        preparedCoverage[instrument.Id].Any(c=>c.Contains(interval.Start,interval.End));
    public IntervalValuationSession Session(CurveBuild scenario,IntervalValuationSession? basis=null)=>new(this,scenario,basis);
    internal void Require(PpaInstrument instrument)
    {
        if(instrument.Definitions!=Reference.Definitions)throw PpaErrors.Error("ppa_definition_revision","instrument","The definition revision differs from the selected inputs.");
        if(instrument.Reference!=Reference.Reference)throw PpaErrors.Error("ppa_reference_revision","instrument","The reference revision differs from the selected inputs.");
        if(!instruments.TryGetValue(instrument.Id,out var retained) || retained.EconomicIdentity!=instrument.EconomicIdentity ||
            retained.RetainedScheduleIdentity!=instrument.RetainedScheduleIdentity||
            !retained.Memberships.Select(m=>(m.Slot,m.Side.Id)).SequenceEqual(instrument.Memberships.Select(m=>(m.Slot,m.Side.Id))))
            throw PpaErrors.Error("ppa_context","instrument","Only exact immutable instruments prepared in this request can be valued.");
    }
    internal ImmutableArray<PreparedQuantityRow> Quantity(PpaInstrument instrument,Side side)
    {
        var key=(instrument.Id,side.Id);if(quantityRows.TryGetValue(key,out var found))return found;
        var result=quantities.Prepare(side,instrument,slot=>Quantity(instrument,instrument.Memberships.Single(m=>m.Slot==slot).Side));
        quantityRows.Add(key,result);return result;
    }
    private ImmutableArray<PreparedRowInputs> PrepareRows(PpaInstrument instrument,Side side)
    {
        var quantities=Quantity(instrument,side);var bindings=new Dictionary<ReferenceKey,ImmutableArray<LocationAssignment>>();
        var physical=side is not(FixedSide or FloatingSide);
        var role=instrument.Memberships.Single(m=>ReferenceEquals(m.Side,side)).Slot.Role;
        var attribute=role is PpaRole.CertificateDelivery or PpaRole.CertificatePayment or PpaRole.CapacityDelivery or PpaRole.CapacityPayment;
        if(physical)
        {
            var locations=side.RetainedPlan is {} retained?retained.PriceReferences.Select(RetainedLocation).Distinct():
                quantities.SelectMany(q=>Components(side,q)).Select(c=>c.Location).Distinct();
            foreach(var location in locations)bindings[location]=LocationValuation.Partition(Reference.Assignments,location,
                side is CertificateDeliverySide?ValuedAttribute.Certificate:side is CapacityDeliverySide?ValuedAttribute.Capacity:ValuedAttribute.Energy,
                [..quantities.Select(q=>q.Interval)],Reference.Definitions);
        }
        var selected=quantities.Select((q,n)=>(q,n)).ToDictionary(p=>p.q.Interval.Start);
        return [..schedules[(instrument.Id,side.Id)].Rows.Where(row=>Includes(instrument,row.Interval!)).Select(row=>
        {
            if(!selected.TryGetValue(row.Interval!.Start,out var prepared)||prepared.q.Interval!=row.Interval)
                throw PpaErrors.Error("ppa_cadence","quantity","Each generated schedule row requires its exact native quantity input.");
            var q=prepared.q;var n=prepared.n;
            var date=row.Interval.OperatingDate;var month=new DateOnly(date.Year,date.Month,1);
            ImmutableArray<PreparedPriceBinding> priceBindings=side.RetainedPlan is {} retained?
                [..retained.NativeRows[row.Ordinal].Prices.Select(price=>
                {
                    if(price.Curve is {} curve)return new PreparedPriceBinding(q.Quantity,curve,price.Multiplier,price.Adder,price.Settlement);
                    var location=RetainedLocation(price);var assignment=bindings[location][n];
                    if(assignment.Unit.Currency!=price.OutputUnit.Currency||assignment.Unit.Quantity!=price.OutputUnit.Quantity||price.OutputUnit.Multiplier!=PpaErrors.One)
                        throw RetainedSideSchedules.Error("A retained physical lookup requires its exact currency, quantity and canonical output quote unit.");
                    // The lookup declares canonical units. Keeping the raw exact
                    // quote scale until Amount performs the same rational unit
                    // conversion without an intermediate decimal projection.
                    var asset=price.Asset??(price.Source is {} source?Reference.Sources[source].Asset:null);
                    var componentQuantity=asset is null?q.Quantity:q.Components.Single(c=>c.Asset==asset).Quantity;
                    if(!Read.Curves.TryGetValue(assignment.Curve,out var assignedCurve))throw PpaErrors.Error("interval_price_source","curve","Prepare every assigned exact price curve.");
                    var selection=assignedCurve.Curve is PeriodContractCurve period?PriceSettlementSelection.CurveTarget(period.SettlementIndex):null;
                    return new PreparedPriceBinding(componentQuantity,assignment.Curve,price.Multiplier,new(assignment.Adjustment,assignment.Unit),selection,asset);
                })]:side switch {
                FloatingSide floating=>[new(q.Quantity,floating.Price.Curve,floating.Price.Multiplier,floating.Price.Adder,floating.Price.Settlement)],
                FixedSide=>[],_=>[..Components(side,q).Select(c=>
                {
                    var assignment=bindings[c.Location][n];
                    if(!Read.Curves.TryGetValue(assignment.Curve,out var assignedCurve))
                        throw PpaErrors.Error("interval_price_source","curve","Prepare every assigned exact price curve.");
                    var selection=assignedCurve.Curve is PeriodContractCurve period?PriceSettlementSelection.CurveTarget(period.SettlementIndex):null;
                    return new PreparedPriceBinding(c.Quantity,assignment.Curve,PpaErrors.One,new(assignment.Adjustment,assignment.Unit),selection,c.Asset);
                })]};
            if(side is FixedSide fixedSide && (fixedSide.Price.Unit.Quantity!=q.Quantity.Specification || fixedSide.Price.Unit.Currency!=instrument.Currency))
                throw PpaErrors.Error("ppa_quantity_unit","fixed_price","The price denominator must match the bound quantity.");
            foreach(var binding in priceBindings)
            {
                if(!Read.Curves.TryGetValue(binding.Curve,out var curve))throw PpaErrors.Error("interval_price_source","curve","Prepare every selected exact price curve.");
                if((curve.Curve is PeriodContractCurve)!=(binding.Settlement is not null))
                    throw PpaErrors.Error("price_settlement_selection","curve","Period price terms require explicit settlement selection; interval price terms take none.");
                _=curve.Dependencies(month,binding.Settlement);
                if(curve.Unit!=binding.Adder.Unit || curve.Unit.Quantity!=binding.Quantity.Specification || curve.Unit.Currency!=instrument.Currency)
                    throw PpaErrors.Error("ppa_quantity_unit","price","Price components require the exact quantity denominator, currency and quote unit.");
                var period=curve.Curve is PeriodContractCurve;
                if(attribute)
                {
                    if(!period)throw PpaErrors.Error("ppa_cadence","attribute","Attribute prices require exact whole-period curve contracts.");
                    var contracts=curve.Period(month).Contracts.Where(c=>c.DeliveryFrom<=date && date<c.DeliveryTo).ToArray();
                    if(contracts.Length!=1 || role is PpaRole.CapacityDelivery or PpaRole.CapacityPayment &&
                        (contracts[0].DeliveryFrom!=month || contracts[0].DeliveryTo!=month.AddMonths(1)))
                        throw PpaErrors.Error("ppa_cadence","attribute","Select one exact generation vintage; capacity pricing requires a complete monthly contract.");
                }
                else if(period || !HasPriceInterval(curve,month,q.Interval))
                    throw PpaErrors.Error("ppa_cadence","price","Resolved prices and quantities must share exact native intervals.");
            }
            return new PreparedRowInputs(row,q,priceBindings);
        })];
        ReferenceKey RetainedLocation(RetainedNativePrice price)
        {
            if(price.Location is {} location)return location;
            var asset=price.Asset??(price.Source is {} source&&Reference.Sources.TryGetValue(source,out var selected)?selected.Asset:
                throw PpaErrors.Error("ppa_quantity_source","retained_price","The retained physical price requires its exact source or asset."));
            return Reference.Assets.TryGetValue(asset,out var definition)?definition.Location:
                throw PpaErrors.Error("ppa_quantity_source","retained_price","The retained physical price requires its exact asset definition.");
        }
    }
    private bool HasPriceInterval(PreparedCurve curve,DateOnly month,DeliveryInterval interval)
    {
        var key=(curve.Key,month);
        if(!priceIntervals.TryGetValue(key,out var cells))
        {
            cells=[];
            foreach(var cell in curve.Period(month).Intervals)
            {
                PriceIntervalInspections++;
                cells.Add((cell.Start,cell.End));
            }
            priceIntervals.Add(key,cells);
        }
        return cells.Contains((interval.Start,interval.End));
    }
    private static IEnumerable<QuantityComponent> Components(Side side,PreparedQuantityRow row)=>row.Components.IsEmpty && side is PowerPhysicalSide physical?
        [new(physical.Location,row.Quantity,null)]:row.Components;
    public DeliveryCoverage FullCoverage(PpaInstrument instrument)
    {
        Require(instrument);var clock=Clock(instrument);
        return new(clock.StartOfDay(instrument.Delivery.From).ToUniversalTime(),clock.StartOfDay(instrument.Delivery.To).ToUniversalTime());
    }
    internal PowerClock Clock(PpaInstrument instrument)=>Reference.Clocks.TryGetValue(instrument.Delivery.Clock,out var clock)?clock:
        throw PpaErrors.Error("ppa_reference_revision","clock","The exact delivery clock is missing.");
    internal void ValidateCoverage(PpaInstrument instrument,DeliveryCoverage coverage)
    {
        ValidateTermCoverage(instrument,coverage);
        if(!preparedCoverage[instrument.Id].Any(c=>c.Contains(coverage.From,coverage.To)))
            throw PpaErrors.Error("ppa_segment_coverage","prepared_coverage","The selected valuation coverage extends outside the explicitly prepared input coverage.");
    }
    private void ValidateTermCoverage(PpaInstrument instrument,DeliveryCoverage coverage)
    {
        Require(instrument);var full=FullCoverage(instrument);
        if(coverage is null || !full.Contains(coverage.From,coverage.To))throw PpaErrors.Error("ppa_segment_coverage","coverage","Select coverage within the full instrument term.");
        var clock=Clock(instrument);var grid=TimeSpan.FromMinutes(instrument.Delivery.IntervalMinutes);
        if(!PowerHours.IsCell(clock.Zone,coverage.From,grid) || !PowerHours.IsCell(clock.Zone,coverage.To,grid))throw PpaErrors.Error("ppa_segment_boundary","coverage","A boundary cannot split a native interval.");
        if(instrument.Memberships.Any(m=>m.Side is CapacityDeliverySide))
        {
            bool MonthStart(DateTimeOffset at){var date=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at,clock.Zone).DateTime);return date.Day==1 && clock.StartOfDay(date).ToUniversalTime()==at;}
            if(!MonthStart(coverage.From)||!MonthStart(coverage.To))throw PpaErrors.Error("ppa_segment_boundary","capacity","Capacity bindings require complete operating months.");
        }
    }
    internal IntervalSideSchedule Schedule(PpaInstrument instrument,Side side)=>schedules[(instrument.Id,side.Id)];
    internal ImmutableArray<PreparedRowInputs> Rows(PpaInstrument instrument,Side side,DeliveryCoverage coverage)=>
        scopedRows.GetOrAdd((instrument.Id,side.Id,coverage),key=>
        {
            var retained=rows[(key.Instrument,key.Side)];
            if(retained.IsEmpty||coverage.Contains(retained[0].Quantity.Interval.Start,retained[^1].Quantity.Interval.End))return retained;
            return [..retained.Where(r=>coverage.Contains(r.Quantity.Interval.Start,r.Quantity.Interval.End))];
        });
    internal PreparedScheduleScope Scope(PpaInstrument instrument,DeliveryCoverage coverage,CancellationToken cancellation=default)=>scopes.GetOrAdd((instrument.Id,coverage),_=>
    {
        long work=0,detail=0;var keys=new HashSet<SidePaymentKey>();var ordered=ImmutableArray.CreateBuilder<SidePaymentKey>();
        foreach(var member in instrument.Memberships)
            foreach(var row in Rows(instrument,member.Side,coverage))
            {
                cancellation.ThrowIfCancellationRequested();work++;if(DetailMonths.Contains(new(row.Quantity.Interval.OperatingDate.Year,row.Quantity.Interval.OperatingDate.Month,1)))detail++;
                if(row.ValueAt>Snapshot.ValuationAt&&!(member.Side is CertificateDeliverySide&&row.Quantity.Interval.End<=Snapshot.ValuationAt)&&keys.Add(row.Schedule.Key))ordered.Add(row.Schedule.Key);
            }
        return new(work,detail,ordered.ToImmutable());
    });
    internal static bool InMeasure(Side side,PreparedRowInputs row,DateTimeOffset valuation)=>
        side is CertificateDeliverySide && row.Quantity.Interval.End<=valuation || row.ValueAt>valuation;
    public ImmutableArray<CurveRequest> Dependencies(PpaInstrument instrument,DeliveryCoverage coverage)
    {
        ValidateCoverage(instrument,coverage);
        var selections=new HashSet<(CurveKey Curve,DateOnly Month,PriceSettlementSelection? Settlement)>();
        var seen=new HashSet<CurveRequest>();
        var dependencies=ImmutableArray.CreateBuilder<CurveRequest>();
        foreach(var member in instrument.Memberships)
            foreach(var row in Rows(instrument,member.Side,coverage))
            {
                if(!InMeasure(member.Side,row,Snapshot.ValuationAt))continue;
                var date=row.Quantity.Interval.OperatingDate;var month=new DateOnly(date.Year,date.Month,1);
                foreach(var price in row.Prices)
                {
                    if(!selections.Add((price.Curve,month,price.Settlement)))continue;
                    Interlocked.Increment(ref priceDependencyResolutions);
                    foreach(var dependency in Read.Curves[price.Curve].Dependencies(month,price.Settlement))
                        if(seen.Add(dependency))dependencies.Add(dependency);
                }
            }
        return dependencies.ToImmutable();
    }
}
