using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public abstract record IntervalPhysicalSide : Side
{
    [IdentityField]public override PpaSideTerms Terms { get; }
    protected IntervalPhysicalSide(Guid id,PpaSideTerms terms):base(id)
    {Terms=terms??throw PpaErrors.Error("ppa_side","side","Explicit side terms are required.");}
    public override ImmutableArray<CurveRequest> Dependencies=>throw PpaErrors.Error("ppa_context","dependencies","Prepare the exact interval inputs before declaring dependencies.");
    public override IntervalSideSchedule GenerateSchedule()=>new(this);
    public override IntervalSideValuation PV(SideValuationContext context)=>GenerateSchedule().Evaluate(context);
}
[EconomicDefinition("power_physical_side_v2")]
public sealed record PowerPhysicalSide:IntervalPhysicalSide
{
    [IdentityField]public ReferenceKey Location { get; }
    public PowerPhysicalSide(Guid id,PpaSideTerms terms,ReferenceKey location):base(id,terms){Money.RequireKey(location);Location=location;}
}
[EconomicDefinition("ppa_certificate_delivery_v2")]
public sealed record CertificateDeliverySide:IntervalPhysicalSide
{
    [IdentityField]public ReferenceKey CertificateQuantity { get; }
    [IdentityField]public ExactDecimal Conversion { get; }
    [IdentityField]public string Eligibility { get; }
    [IdentityField]public string Holding { get; }
    [IdentityField]public string Disposition { get; }
    public CertificateDeliverySide(Guid id,PpaSideTerms terms,ReferenceKey certificateQuantity,ExactDecimal conversion,string eligibility,string holding,string disposition):base(id,terms)
    {
        if(conversion.CompareTo(default)<=0 || eligibility!="asset_generation_at_stage_v1" || holding!="held_at_current_vintage_mark_v1" || disposition!="none_recorded_v1" || terms.Quantity is not VolumeQuantity)
            throw PpaErrors.Error("ppa_term_unsupported","certificate","Select asset generation, positive conversion, current vintage holdings and explicit no-disposition policy.");
        Money.RequireKey(certificateQuantity);CertificateQuantity=certificateQuantity;Conversion=conversion;Eligibility=eligibility;Holding=holding;Disposition=disposition;
    }
}
[EconomicDefinition("ppa_capacity_delivery_v2")]
public sealed record CapacityDeliverySide:IntervalPhysicalSide
{
    [IdentityField]public ReferenceKey CapacityQuantity { get; }
    [IdentityField]public string Rule { get; }
    public CapacityDeliverySide(Guid id,PpaSideTerms terms,ReferenceKey capacityQuantity,string rule):base(id,terms)
    {
        if(rule!="mw_day_per_operating_day_v1" || terms.Quantity is not AccreditedQuantity)throw PpaErrors.Error("ppa_term_unsupported","capacity","Select whole-month accredited MW-day quantities explicitly.");
        Money.RequireKey(capacityQuantity);CapacityQuantity=capacityQuantity;Rule=rule;
    }
}

public sealed record IntervalSideSchedule:PpaSchedule
{
    public Side Side { get; }
    public ImmutableArray<IntervalScheduleMonth> Months { get; }
    public override IReadOnlyList<ScheduleRow> Rows { get; }
    public override IntervalSideValuation Evaluate(SideValuationContext context)=>context.Interval is {} interval?
        interval.Session.Evaluate(Side,interval.Instrument,interval.Coverage,context):throw PpaErrors.Error("ppa_context","side","Use this instrument's prepared interval scope.");
    internal IntervalSideSchedule(Side side):base(side.Id,side.Terms.Currency,side.Terms.UnitQuantity)
    {
        if(side.Terms.Schedule is null||side.Terms.Schedule.Delivery!=side.Terms.Delivery)
            throw PpaErrors.Error("side_schedule_parameters","schedule","The side must retain its exact whole-term schedule parameters.");
        Side=side;Months=side.RetainedPlan is {} retained?retained.Months():side.Terms.Schedule.Generate(side.Id,side.Terms.Currency,side.Terms.DateRule);
        IntervalSchedulePrice price=side switch
        {
            FixedSide fixedSide=>new FixedIntervalSchedulePrice(fixedSide.Price),
            FloatingSide floating=>new IndexedIntervalSchedulePrice(floating.Price),
            PowerPhysicalSide physical=>new LocationIntervalSchedulePrice(ValuedAttribute.Energy,physical.Location),
            CertificateDeliverySide=>new LocationIntervalSchedulePrice(ValuedAttribute.Certificate,null),
            CapacityDeliverySide=>new LocationIntervalSchedulePrice(ValuedAttribute.Capacity,null),
            _=>throw PpaErrors.Error("ppa_side","schedule","The side has no declared schedule price reference.")
        };
        Rows=new IntervalRows(side,Months,price);
    }
    private sealed class IntervalRows(Side side,ImmutableArray<IntervalScheduleMonth> months,IntervalSchedulePrice price):IReadOnlyList<ScheduleRow>
    {
        private readonly ImmutableArray<IntervalRowBlock> blocks=[..months.Select(month=>new IntervalRowBlock(RetainedSideSchedules.Terms(side),month,price,side.RetainedPlan))];
        public int Count { get; }=months.Sum(m=>m.Intervals.Length);
        public ScheduleRow this[int index]
        {
            get
            {
                if((uint)index>=(uint)Count)throw new ArgumentOutOfRangeException(nameof(index));
                var low=0;var high=months.Length-1;
                while(low<high){var middle=low+(high-low+1)/2;if(months[middle].FirstRowOrdinal<=index)low=middle;else high=middle-1;}
                return new(blocks[low],index-months[low].FirstRowOrdinal);
            }
        }
        public IEnumerator<ScheduleRow> GetEnumerator()
        {
            foreach(var block in blocks)
                for(var n=0;n<block.Month.Intervals.Length;n++)yield return new(block,n);
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
}
public abstract record PpaSchedule:SideSchedule
{
    protected PpaSchedule(Guid id,ReferenceKey currency,Quantity unit):base(id,currency,unit,"interval_side_schedule_v1"){}
    public override ImmutableArray<CurveRequest> Dependencies=>throw PpaErrors.Error("ppa_context","dependencies","Dependencies require prepared delivery coverage.");
}
internal sealed record IntervalSideContext(IntervalValuationSession Session,PpaInstrument Instrument,DeliveryCoverage Coverage,
    PpaScheduleGraph Graph,ScheduleGraphSession Values,ImmutableArray<SidePaymentFactor> Factors,CancellationToken Cancellation);
public sealed partial class SideValuationContext
{
    internal IntervalSideContext? Interval { get; init; }
}
