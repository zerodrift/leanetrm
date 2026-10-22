using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public sealed record IntervalReferenceData(DefinitionRevision Definitions,ReferenceRevision Reference,
    ImmutableDictionary<ReferenceKey,PowerClock> Clocks,ImmutableDictionary<ReferenceKey,QuantitySpecification> Quantities,
    ImmutableDictionary<AssetKey,GenerationAsset> Assets,ImmutableDictionary<VolumeSourceKey,VolumeSourceDefinition> Sources,
    ImmutableArray<LocationAssignment> Assignments);
public sealed record IntervalReadData(ImmutableDictionary<Guid,QuantitySeries> Series,ImmutableDictionary<CurveKey,PreparedCurve> Curves);
internal sealed record PreparedPriceBinding(ExactQuantity Quantity,CurveKey Curve,ExactDecimal Multiplier,UnitPrice Adder);
internal sealed record PreparedSideRow(PreparedQuantityRow Quantity,DateTimeOffset ValueAt,ImmutableArray<PreparedPriceBinding> Prices);
public sealed record ShapeRevisionPin(CurveKey Curve,Guid Series,int Revision);
public sealed record ValuationReceipt(DefinitionRevision Definitions,ReferenceRevision Reference,Guid Snapshot,DateTimeOffset ValuationAt,
    DateTimeOffset KnownAt,Guid KnowledgeContext,PendingActualsPolicy Pending,string Coverage,
    ImmutableArray<(Guid Series,int Revision,DateTimeOffset KnownAt,Guid Context)> QuantityRevisions,
    ImmutableArray<(Guid Batch,int? Revision,DateTimeOffset KnownAt)> Actuals,ImmutableArray<string> Implementations)
{
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
    public PendingActualsPolicy Policy { get; }
    public VolumeContext Context=>new(Reference.Definitions,Reference.Reference,Snapshot.ValuationAt,KnownAt,Policy);
    public Guid KnowledgeContext { get; }
    public ImmutableHashSet<DateOnly> DetailMonths { get; }
    public ValuationReceipt Receipt { get; private set; }=null!;
    public int VolumeComputations=>quantities.VolumeComputations;
    public int SeriesValidations=>quantities.SeriesValidations;
    public int SeriesSelections=>quantities.SeriesSelections;
    internal CurveBuild BaseMarket { get; }
    internal IntervalPriceResolver BaseResolver { get; }
    private readonly ImmutableDictionary<Guid,PpaInstrument> instruments;
    private readonly Dictionary<(Guid Instrument,Guid Side),ImmutableArray<PreparedSideRow>> rows=[];
    private readonly Dictionary<(Guid Instrument,Guid Side),ImmutableArray<PreparedQuantityRow>> quantityRows=[];
    private readonly PpaQuantityPreparation quantities;
    private readonly ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>> preparedCoverage;
    private IntervalValuationInputs(IntervalReferenceData reference,IntervalReadData read,ImmutableArray<PpaInstrument> instruments,
        CurveBuild baseMarket,DateTimeOffset knownAt,Guid knowledgeContext,PendingActualsPolicy pending,string coveragePolicy,ImmutableHashSet<DateOnly> detailMonths,
        ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>> coverage)
    {
        if(reference is null || read is null || reference.Clocks is null || reference.Quantities is null || reference.Assets is null || reference.Sources is null || reference.Assignments.IsDefault ||
            read.Series is null || read.Curves is null || baseMarket is null || baseMarket.Shock is not null || baseMarket.Snapshot.Definitions!=reference.Definitions ||
            knownAt.Offset!=TimeSpan.Zero || knownAt.Ticks%10!=0 || knowledgeContext==Guid.Empty || !Enum.IsDefined(pending) || coveragePolicy!="ppa_coverage_v1" || detailMonths is null ||
            detailMonths.Any(d=>d.Day!=1) || instruments.IsDefaultOrEmpty || instruments.Any(i=>i is null) || instruments.Select(i=>i.Id).Distinct().Count()!=instruments.Length)
            throw PpaErrors.Error("ppa_context","inputs","Prepare exact base-market inputs, certified knowledge and explicit valuation policies.");
        if(coverage is null || coverage.Count!=instruments.Length || instruments.Any(i=>!coverage.ContainsKey(i.Id)) ||
            coverage.Values.Any(v=>v.IsDefaultOrEmpty || v.Any(c=>c is null)))
            throw PpaErrors.Error("ppa_segment_coverage","prepared_coverage","Declare nonempty exact input coverage for every selected instrument.");
        Reference=reference;Read=read;BaseMarket=baseMarket;KnownAt=knownAt;KnowledgeContext=knowledgeContext;Policy=pending;DetailMonths=detailMonths;
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
            foreach(var member in instrument.Memberships)Quantity(instrument,member.Side);
            foreach(var member in instrument.Memberships)rows.Add((instrument.Id,member.Side.Id),PrepareRows(instrument,member.Side));
        }
        var volumes=quantityRows.Values.SelectMany(v=>v).SelectMany(r=>r.Lineage).Distinct().ToArray();
        var actuals=read.Curves.Values.SelectMany(c=>c.Periods.Values).SelectMany(p=>p.Actuals.Values).Distinct().ToArray();
        Receipt=new(reference.Definitions,reference.Reference,Snapshot.Id,Snapshot.ValuationAt,knownAt,knowledgeContext,pending,coveragePolicy,
            [..volumes.Select(v=>(v.Series,v.Revision,v.KnownAt,v.ContextId)).Distinct()],
            [..actuals.Select(a=>(a.BatchId,a.Revision,a.KnownAt)).Distinct()],
            ["exact_interval_leg_pv_v1","ppa_coverage_v1",PpaCapabilities.Version,AssetVolumes.Implementation,CapacityNormalization.Implementation,
             "anniversary_step_v1","mw_day_per_operating_day_v1","held_at_current_vintage_mark_v1","none_recorded_v1",
             BlockShaping.OperatorVersion,BlockShaping.CalibrationPolicy,IntervalPricing.AggregatePolicy])
        {
            MarketInputs=Snapshot,ReferenceInputs=reference,Instruments=instruments,DetailMonths=detailMonths,RequestedCoverage=coverage,
            CurveInputs=read.Curves,PriceDefinitions=[..read.Curves.Values.Select(c=>c.Curve)],
            Shapes=[..read.Curves.Values.SelectMany(curve=>curve.Periods.Values.Where(period=>period.Shape is not null)
                .SelectMany(period=>period.Shape!.Blocks.SelectMany(block=>block.Cells.Select(cell=>
                    new ShapeRevisionPin(curve.Key,period.Shape.Definition.ShapeSeries,cell.Revision))))).Distinct()],
            FixingRules=[..read.Curves.Values.Select(c=>c.Curve).OfType<PeriodContractCurve>().SelectMany(c=>c.Fixings).Distinct()],
            AssetVersions=[..quantityRows.Values.SelectMany(v=>v).SelectMany(row=>row.CapacityVersions.Concat(row.Lineage.Select(v=>v.AssetVersion))).Distinct()]
        };
    }
    public static IntervalValuationInputs Prepare(IntervalReferenceData reference,IntervalReadData read,ImmutableArray<PpaInstrument> instruments,
        CurveBuild baseMarket,DateTimeOffset knownAt,Guid knowledgeContext,PendingActualsPolicy pending,string coveragePolicy,ImmutableHashSet<DateOnly> detailMonths,
        ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>> coverage)=>
        new(reference,read,instruments,baseMarket,knownAt,knowledgeContext,pending,coveragePolicy,detailMonths,coverage);
    private static ImmutableArray<DeliveryCoverage> Union(ImmutableArray<DeliveryCoverage> coverage)
    {
        var spans=new List<DeliveryCoverage>();
        foreach(var span in coverage.OrderBy(c=>c.From))
            if(spans.Count>0 && span.From<=spans[^1].To)
                spans[^1]=new(spans[^1].From,span.To>spans[^1].To?span.To:spans[^1].To);
            else spans.Add(span);
        return [..spans];
    }
    internal bool Includes(PpaInstrument instrument,DeliveryInterval interval)=>
        preparedCoverage[instrument.Id].Any(c=>c.Contains(interval.Start,interval.End));
    public IntervalValuationSession Session(CurveBuild scenario,IntervalValuationSession? basis=null)=>new(this,scenario,basis);
    internal void Require(PpaInstrument instrument)
    {
        if(instrument.Definitions!=Reference.Definitions)throw PpaErrors.Error("ppa_definition_revision","instrument","The definition revision differs from the selected inputs.");
        if(instrument.Reference!=Reference.Reference)throw PpaErrors.Error("ppa_reference_revision","instrument","The reference revision differs from the selected inputs.");
        if(!instruments.TryGetValue(instrument.Id,out var retained) || retained.EconomicIdentity!=instrument.EconomicIdentity ||
            !retained.Memberships.Select(m=>(m.Slot,m.Side.Id)).SequenceEqual(instrument.Memberships.Select(m=>(m.Slot,m.Side.Id))))
            throw PpaErrors.Error("ppa_context","instrument","Only exact immutable instruments prepared in this request can be valued.");
    }
    internal ImmutableArray<PreparedQuantityRow> Quantity(PpaInstrument instrument,PpaSide side)
    {
        var key=(instrument.Id,side.Id);if(quantityRows.TryGetValue(key,out var found))return found;
        var result=quantities.Prepare(side,instrument,slot=>Quantity(instrument,instrument.Memberships.Single(m=>m.Slot==slot).Side));
        quantityRows.Add(key,result);return result;
    }
    private ImmutableArray<PreparedSideRow> PrepareRows(PpaInstrument instrument,PpaSide side)
    {
        var quantities=Quantity(instrument,side);var bindings=new Dictionary<ReferenceKey,ImmutableArray<LocationAssignment>>();
        var physical=side is not(PpaFixedSide or PpaFloatingSide);
        var role=instrument.Memberships.Single(m=>ReferenceEquals(m.Side,side)).Slot.Role;
        var attribute=role is PpaRole.CertificateDelivery or PpaRole.CertificatePayment or PpaRole.CapacityDelivery or PpaRole.CapacityPayment;
        if(physical)
        {
            var locations=quantities.SelectMany(q=>Components(side,q)).Select(c=>c.Location).Distinct();
            foreach(var location in locations)bindings[location]=LocationValuation.Partition(Reference.Assignments,location,
                side is CertificateDeliverySide?ValuedAttribute.Certificate:side is CapacityDeliverySide?ValuedAttribute.Capacity:ValuedAttribute.Energy,
                [..quantities.Select(q=>q.Interval)],Reference.Definitions);
        }
        return [..quantities.Select((q,n)=>
        {
            var date=q.Interval.OperatingDate;var month=new DateOnly(date.Year,date.Month,1);
            var valueAt=new DateTimeOffset(side.Terms.DateRule.Generate(month,month.AddMonths(1)).Date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
            ImmutableArray<PreparedPriceBinding> priceBindings=side switch {
                PpaFloatingSide floating=>[new(q.Quantity,floating.Price.Curve,floating.Price.Multiplier,floating.Price.Adder)],
                PpaFixedSide=>[],_=>[..Components(side,q).Select(c=>
                {
                    var assignment=bindings[c.Location][n];
                    return new PreparedPriceBinding(c.Quantity,assignment.Curve,PpaErrors.One,new(assignment.Adjustment,assignment.Unit));
                })]};
            if(side is PpaFixedSide fixedSide && (fixedSide.Price.Unit.Quantity!=q.Quantity.Specification || fixedSide.Price.Unit.Currency!=instrument.Currency))
                throw PpaErrors.Error("ppa_quantity_unit","fixed_price","The price denominator must match the bound quantity.");
            foreach(var binding in priceBindings)
            {
                if(!Read.Curves.TryGetValue(binding.Curve,out var curve))throw PpaErrors.Error("interval_price_source","curve","Prepare every selected exact price curve.");
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
                else if(period || !curve.Period(month).Intervals.Any(i=>i.Start==q.Interval.Start && i.End==q.Interval.End))
                    throw PpaErrors.Error("ppa_cadence","price","Resolved prices and quantities must share exact native intervals.");
            }
            return new PreparedSideRow(q,valueAt,priceBindings);
        })];
    }
    private static IEnumerable<QuantityComponent> Components(PpaSide side,PreparedQuantityRow row)=>row.Components.IsEmpty && side is PowerPhysicalSide physical?
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
    internal ImmutableArray<PreparedSideRow> Rows(PpaInstrument instrument,PpaSide side,DeliveryCoverage coverage)=>
        [..rows[(instrument.Id,side.Id)].Where(r=>coverage.Contains(r.Quantity.Interval.Start,r.Quantity.Interval.End))];
    internal static bool InMeasure(PpaSide side,PreparedSideRow row,DateTimeOffset valuation)=>
        side is CertificateDeliverySide && row.Quantity.Interval.End<=valuation || row.ValueAt>valuation;
    public ImmutableArray<CurveRequest> Dependencies(PpaInstrument instrument,DeliveryCoverage coverage)
    {
        ValidateCoverage(instrument,coverage);
        return [..instrument.Memberships.SelectMany(m=>Rows(instrument,m.Side,coverage).Where(r=>InMeasure(m.Side,r,Snapshot.ValuationAt))
            .SelectMany(r=>r.Prices.SelectMany(p=>Read.Curves[p.Curve].Dependencies(new(r.Quantity.Interval.OperatingDate.Year,r.Quantity.Interval.OperatingDate.Month,1))))).Distinct()];
    }
}
