using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public readonly record struct RetainedScheduleRowReference([property:System.Text.Json.Serialization.JsonIgnore]ScheduleDefinition Definition,int RootOrdinal,int? CellOrdinal)
{
    public Guid SideId=>Definition.SideId;
    public string Manifest=>Definition.Manifest;
    public ScheduleObligationIdentity Obligation=>Definition.ObligationIdentity(RootOrdinal,CellOrdinal);
}
internal sealed record IntervalRowBlock(PpaSideTerms Terms,IntervalScheduleMonth Month,IntervalSchedulePrice Price,RetainedSideSchedule? Retained=null);

// A row is a small view over shared immutable terms and an interval grid. Native
// schedules retain one block per month rather than one object for every row.
public readonly record struct ScheduleRow
{
    private readonly DatedScheduleRow? dated;
    private readonly IntervalRowBlock? block;
    private readonly int offset;
    public bool IsValid=>dated is not null||block is not null;
    private DatedScheduleRow Dated=>dated??throw new MarketDataException("schedule_row_kind","This row has native interval input references.");
    private IntervalRowBlock Block=>block??throw new MarketDataException("schedule_row_kind","This row has dated contractual payment terms.");
    public SidePaymentKey Key=>dated?.Key??Block.Month.Payment;
    public Guid SideId=>Key.SideId;
    public int Ordinal=>dated?.Ordinal??Block.Month.FirstRowOrdinal+offset;
    public DeliveryInterval? Interval=>block?.Month.Intervals[offset];
    public ScheduleDeliveryWindow DeliveryWindow=>dated?.DeliveryWindow??new IntervalScheduleWindow(Block.Month.Intervals[offset]);
    public DateOnly DeliveryFrom=>Dated.DeliveryFrom;
    public DateOnly DeliveryTo=>Dated.DeliveryTo;
    public DateTimeOffset PaymentAt=>Key.PaymentAt;
    public DateOnly PaymentDate=>DateOnly.FromDateTime(PaymentAt.UtcDateTime);
    public ReferenceKey Currency=>Key.Currency;
    public Quantity UnitQuantity=>dated?.UnitQuantity??Block.Terms.UnitQuantity;
    public ExactQuantity AllocatedQuantity=>Dated.AllocatedQuantity;
    public ScheduleQuantityReference QuantityReference=>dated?.QuantityReference??new IntervalScheduleQuantity(Block.Terms.Quantity,Block.Month.Intervals[offset]);
    public RetainedScheduleRowReference? RetainedReference=>dated?.RetainedReference??(block?.Retained is {} retained?
        new(retained.Definition,retained.NativeRows[Ordinal].Root,retained.NativeRows[Ordinal].CellOrdinal):null);
    public IntervalSchedulePrice? IntervalPrice=>block?.Retained is {} retained&&retained.NativeRows[Ordinal].FixedPrice is {} price?
        new FixedIntervalSchedulePrice(new RetainedFixedPrice(price)):block?.Price;
    public GeneratedPaymentDate? GeneratedDate=>dated?.GeneratedDate;
    public PaymentRule? PaymentConvention=>dated is not null?dated.PaymentConvention:Block.Terms.DateRule;
    public DateTimeOffset? FirstFixingAt=>Dated.FirstFixingAt;
    public FloatingAveraging? Averaging=>Dated.Averaging;
    public SwapObservationCalendar? Calendar=>Dated.Calendar;
    public ImmutableArray<SchedulePriceTerm> Prices=>Dated.Prices;
    public ImmutableArray<SchedulePriceApplication> Applications=>Dated.Applications;
    public string Implementation=>dated?.Implementation??"interval_side_schedule_v2";

    internal ScheduleRow(Guid sideId,DateOnly from,DateOnly to,DateTimeOffset paymentAt,ReferenceKey currency,Quantity unitQuantity,
        DateTimeOffset? firstFixingAt,FloatingAveraging? averaging,SwapObservationCalendar? calendar,ImmutableArray<SchedulePriceTerm> prices,
        string implementation,ImmutableArray<FloatingObservationApplication> applications,string mappingRule,
        GeneratedPaymentDate? generatedDate=null,PaymentRule? paymentConvention=null)
    {
        dated=new(sideId,from,to,paymentAt,currency,unitQuantity,firstFixingAt,averaging,calendar,prices,implementation,applications,mappingRule,generatedDate,paymentConvention);
        block=null;offset=0;
    }
    internal ScheduleRow(ScheduleRow period,Guid sideId,int ordinal,ExactQuantity allocation)
    {dated=new(period.Dated,sideId,ordinal,allocation);block=null;offset=0;}
    private ScheduleRow(DatedScheduleRow row){dated=row;block=null;offset=0;}
    internal ScheduleRow WithRetainedReference(ScheduleDefinition definition,int root)=>new(Dated with{RetainedReference=new(definition,root,null)});
    internal ScheduleRow(IntervalRowBlock block,int offset)
    {
        if(block is null||(uint)offset>=(uint)block.Month.Intervals.Length)throw new MarketDataException("schedule_row","A row must select an exact native interval from its shared schedule block.");
        this.block=block;this.offset=offset;dated=null;
    }
}
