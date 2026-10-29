using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public abstract record ScheduleDeliveryWindow;
public sealed record DatedScheduleWindow(DateOnly From,DateOnly To):ScheduleDeliveryWindow;
public sealed record IntervalScheduleWindow(DeliveryInterval Interval):ScheduleDeliveryWindow;

public abstract record ScheduleQuantityReference;
public sealed record ContractualScheduleQuantity(ExactQuantity Amount) : ScheduleQuantityReference;
public sealed record IntervalScheduleQuantity(PpaQuantity Source, DeliveryInterval Interval) : ScheduleQuantityReference;

public abstract record IntervalSchedulePrice;
public sealed record FixedIntervalSchedulePrice(FixedPriceTerm Term) : IntervalSchedulePrice;
public sealed record IndexedIntervalSchedulePrice(IndexPrice Term) : IntervalSchedulePrice;
public sealed record LocationIntervalSchedulePrice(ValuedAttribute Attribute, ReferenceKey? Location) : IntervalSchedulePrice;
