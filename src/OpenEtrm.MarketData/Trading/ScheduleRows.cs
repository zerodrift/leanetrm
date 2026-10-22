using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public enum ComponentKind { Delivery=1,Payment,Holding }
public enum EconomicClass { Realised=1,Estimated,Held,Unrealised }
public enum PaymentLifecycle { RemainingProjected=1,DeliveredUnsettled }
public enum ValueCoverage { Discounted=1,HoldingValue,OverdueBalance,OverdueEstimate,DeliveredValue }
public enum RowPriceState { Fixed=1,Projected,Actual,Modeled,PublishedFixing,Estimated }
public sealed record QuantityComponent(ReferenceKey Location,ExactQuantity Quantity,AssetKey? Asset);
public sealed record CapacityDay(DateOnly OperatingDay,AssetVersionKey Version,ReferenceKey Location,ExactQuantity PerUnit);
public sealed record PreparedQuantityRow(DeliveryInterval Interval,ExactQuantity Quantity,VolumeState State,QuantityStage? Stage,
    ImmutableArray<QuantityComponent> Components,ImmutableArray<AssetVolume> Lineage,ImmutableArray<AssetVersionKey> CapacityVersions)
{ public ImmutableArray<CapacityDay> CapacityDays { get; init; }=[]; }
public sealed record PriceComponent(ExactQuantity Quantity,ExactUnitPrice Price,RowPriceState State,CurveKey? Curve,
    IntervalPriceResolution? Resolution,ExactDecimal Multiplier,UnitPrice? Adder);
public sealed record ScheduleRowValue(Guid SideId,SideSlot Slot,ComponentKind Kind,DeliveryInterval Interval,
    ExactQuantity Quantity,VolumeState QuantityState,QuantityStage? Stage,ImmutableArray<AssetVolume> QuantityLineage,
    ImmutableArray<AssetVersionKey> CapacityVersions,ImmutableArray<PriceComponent> PriceComponents,ExactMoney Amount,
    ExactDecimal Coefficient,string DateRule,DateTimeOffset ValueAt,SidePaymentFactor? Discount,ExactMoney? PresentValue,
    EconomicClass Class,PaymentLifecycle? Lifecycle,ValueCoverage Coverage,string? UnavailableReason)
{
    public ImmutableArray<CapacityDay> CapacityDays { get; init; }=[];
    public ScheduleRowValue Scale(ExactDecimal coefficient,ExactDecimal mw)=>this with {
        Quantity=Quantity.Scale(mw),Amount=Amount.Scale(coefficient).Scale(mw),Coefficient=Coefficient*coefficient,
        PresentValue=PresentValue?.Scale(coefficient).Scale(mw),
        PriceComponents=[..PriceComponents.Select(p=>p with {Quantity=p.Quantity.Scale(mw)})]};
}

public sealed record SidePeriodKey(Guid SideId,SideSlot Slot,DateOnly Month,ComponentKind Kind,VolumeState QuantityState,
    EconomicClass Class,PaymentLifecycle? Lifecycle,ValueCoverage Coverage,DateTimeOffset ValueAt);
public sealed record SidePeriodSummary(SidePeriodKey Key,long Rows,ExactQuantity Quantity,ExactMoney Amount,ExactMoney? PresentValue)
{
    internal SidePeriodSummary Scale(ExactDecimal coefficient,ExactDecimal mw)=>this with {
        Quantity=Quantity.Scale(mw),Amount=Amount.Scale(coefficient*mw),PresentValue=PresentValue?.Scale(coefficient*mw)};
}

public sealed record ValueMeasureSet
{
    public const string Label="PV of not-yet-due rows plus current holding value; excludes overdue cash and delivered value.";
    public ReferenceKey Currency { get; }
    public ExactMoney ValueExcludingOverdue { get; }
    public ExactMoney? CompletePv=>HasOverdue?null:ValueExcludingOverdue;
    public string? UnavailableReason=>HasOverdue?"due_cash_policy_required":null;
    public bool HasOverdue { get; }
    public ExactMoney OverdueKnownBalance { get; }
    public ExactMoney OverdueEstimated { get; }
    public ExactMoney RealisedCash { get; }
    public ExactMoney RealisedDeliveryValue { get; }
    public ExactMoney EstimatedHoldingValue { get; }
    public ExactMoney ModeledFromFrozenAnchor { get; }
    public ImmutableDictionary<EconomicClass,ExactMoney> Classes { get; }
    public ImmutableDictionary<ValueCoverage,ExactMoney> Coverages { get; }
    public ImmutableDictionary<PaymentLifecycle,ExactMoney> Lifecycles { get; }
    public ImmutableArray<ScheduleRowValue> Rows { get; }
    public ImmutableArray<SidePeriodSummary> Periods { get; init; }=[];
    internal ValueMeasureSet(ReferenceKey currency,ExactMoney value,bool overdue,ExactMoney known,ExactMoney estimate,
        ExactMoney realisedCash,ExactMoney realisedDelivery,ExactMoney estimatedHolding,ExactMoney modeled,
        ImmutableDictionary<EconomicClass,ExactMoney> classes,ImmutableDictionary<ValueCoverage,ExactMoney> coverages,
        ImmutableDictionary<PaymentLifecycle,ExactMoney> lifecycles,ImmutableArray<ScheduleRowValue> rows)
    {Currency=currency;ValueExcludingOverdue=value;HasOverdue=overdue;OverdueKnownBalance=known;OverdueEstimated=estimate;
        RealisedCash=realisedCash;RealisedDeliveryValue=realisedDelivery;EstimatedHoldingValue=estimatedHolding;ModeledFromFrozenAnchor=modeled;
        Classes=classes;Coverages=coverages;Lifecycles=lifecycles;Rows=rows;}
    public ValueMeasureSet Scale(ExactDecimal factor)=>Scale(PpaErrors.One,factor);
    public ValueMeasureSet Scale(ExactDecimal coefficient,ExactDecimal mw)
    {
        var factor=coefficient*mw;
        return new(Currency,ValueExcludingOverdue.Scale(factor),HasOverdue,OverdueKnownBalance.Scale(factor),OverdueEstimated.Scale(factor),
            RealisedCash.Scale(factor),RealisedDeliveryValue.Scale(factor),EstimatedHoldingValue.Scale(factor),ModeledFromFrozenAnchor.Scale(factor),
            Classes.ToImmutableDictionary(p=>p.Key,p=>p.Value.Scale(factor)),Coverages.ToImmutableDictionary(p=>p.Key,p=>p.Value.Scale(factor)),
            Lifecycles.ToImmutableDictionary(p=>p.Key,p=>p.Value.Scale(factor)),[..Rows.Select(r=>r.Scale(coefficient,mw))]) {Periods=[..Periods.Select(p=>p.Scale(coefficient,mw))]};
    }
}
internal sealed class MeasureAccumulator(ReferenceKey currency)
{
    private ExactMoney Zero=>ExactMoney.FromMoney(new(default,currency));
    private readonly Dictionary<EconomicClass,ExactMoney> classes=[];
    private readonly Dictionary<ValueCoverage,ExactMoney> coverages=[];
    private readonly Dictionary<PaymentLifecycle,ExactMoney> lifecycles=[];
    private readonly List<ScheduleRowValue> rows=[];
    private ExactMoney? value,known,estimate,realisedCash,realisedDelivery,estimatedHolding,modeled;
    private bool overdue;
    private readonly Dictionary<SidePeriodKey,SidePeriodSummary> periods=[];
    private void Period(SidePeriodSummary next)
    {
        if(periods.TryGetValue(next.Key,out var old))
            periods[next.Key]=old with {Rows=old.Rows+next.Rows,Quantity=old.Quantity.Add(next.Quantity),Amount=old.Amount.Add(next.Amount),PresentValue=old.PresentValue is {} pv?pv.Add(next.PresentValue??throw new MarketDataException("ppa_coverage","A period cannot mix discounted and excluded rows.")):null};
        else {if(periods.Count>=10_000)throw new MarketDataException("ppa_size","The bounded period summary limit is 10,000 groups.");periods.Add(next.Key,next);}
    }
    private static void Add<T>(Dictionary<T,ExactMoney> map,T key,ExactMoney amount)where T:notnull=>map[key]=map.TryGetValue(key,out var old)?old.Add(amount):amount;
    private static void Sum(ref ExactMoney? total,ExactMoney amount)=>total=total is null?amount:total.Add(amount);
    internal void Add(ScheduleRowValue row,bool detail)
    {
        Period(new(new(row.SideId,row.Slot,new(row.Interval.OperatingDate.Year,row.Interval.OperatingDate.Month,1),row.Kind,row.QuantityState,row.Class,row.Lifecycle,row.Coverage,row.ValueAt),1,row.Quantity,row.Amount,row.PresentValue));
        Add(classes,row.Class,row.Amount);Add(coverages,row.Coverage,row.Amount);
        if(row.Lifecycle is {} lifecycle)Add(lifecycles,lifecycle,row.Amount);
        if(row.Coverage is ValueCoverage.Discounted or ValueCoverage.HoldingValue)Sum(ref value,row.PresentValue!);
        if(row.Coverage==ValueCoverage.OverdueBalance){overdue=true;Sum(ref known,row.Amount);}
        if(row.Coverage==ValueCoverage.OverdueEstimate){overdue=true;Sum(ref estimate,row.Amount);}
        if(row.Class==EconomicClass.Realised && row.Kind==ComponentKind.Payment)Sum(ref realisedCash,row.Amount);
        if(row.Class==EconomicClass.Realised && row.Kind==ComponentKind.Delivery)Sum(ref realisedDelivery,row.Amount);
        if(row.Class==EconomicClass.Estimated && row.Coverage==ValueCoverage.HoldingValue)Sum(ref estimatedHolding,row.Amount);
        if(row.PriceComponents.Any(p=>p.State==RowPriceState.Modeled))Sum(ref modeled,row.Amount);
        if(detail)rows.Add(row);
    }
    internal void Add(ValueMeasureSet m)
    {
        if(m.Currency!=currency)throw new MarketDataException("ppa_currency","Measure aggregation requires an exact currency.");
        Sum(ref value,m.ValueExcludingOverdue);Sum(ref known,m.OverdueKnownBalance);Sum(ref estimate,m.OverdueEstimated);
        Sum(ref realisedCash,m.RealisedCash);Sum(ref realisedDelivery,m.RealisedDeliveryValue);Sum(ref estimatedHolding,m.EstimatedHoldingValue);Sum(ref modeled,m.ModeledFromFrozenAnchor);
        overdue|=m.HasOverdue;
        foreach(var p in m.Classes)Add(classes,p.Key,p.Value);foreach(var p in m.Coverages)Add(coverages,p.Key,p.Value);foreach(var p in m.Lifecycles)Add(lifecycles,p.Key,p.Value);
        rows.AddRange(m.Rows);foreach(var p in m.Periods)Period(p);
    }
    internal ValueMeasureSet Finish()=>new(currency,value??Zero,overdue,known??Zero,estimate??Zero,realisedCash??Zero,realisedDelivery??Zero,estimatedHolding??Zero,modeled??Zero,
        classes.ToImmutableDictionary(),coverages.ToImmutableDictionary(),lifecycles.ToImmutableDictionary(),[..rows]) {Periods=[..periods.Values.OrderBy(p=>p.Key.SideId).ThenBy(p=>p.Key.Month).ThenBy(p=>p.Key.ValueAt).ThenBy(p=>p.Key.Coverage).ThenBy(p=>p.Key.Class).ThenBy(p=>p.Key.QuantityState)]};
}
public static class ValueMeasures
{
    public static ValueMeasureSet From(InstrumentValuation value)
    {
        var total=new MeasureAccumulator(value.Currency);
        foreach(var contribution in value.Sides)
        {
            if(contribution.Value is not IntervalSideValuation side)throw new MarketDataException("ppa_coverage","Use interval row measures for this valuation.");
            total.Add(side.Measures.Scale(contribution.Coefficient,PpaErrors.One));
        }
        return total.Finish();
    }
}
public sealed record IntervalScopeReceipt(ValuationReceipt Inputs,DefinitionIdentity Instrument,Guid Side,SideSlot Slot,
    DeliveryCoverage Coverage,Risk.QuoteShock? Shock,string Model,string NumericalPolicy,ImmutableArray<SidePaymentFactor> Factors);

public sealed record IntervalSideValuation:SideValuation
{
    public ValueMeasureSet Measures { get; }
    public IntervalScopeReceipt Receipt { get; }
    public ImmutableArray<ScheduleRowValue> Rows=>Measures.Rows;
    public override string NumericalPolicy=>"exact_interval_leg_pv_v1";
    internal IntervalSideValuation(IntervalSideSchedule schedule,SideValuationContext context,ValueMeasureSet measures,IntervalScopeReceipt receipt):base(schedule,context,measures.ValueExcludingOverdue)
    {Measures=measures;Receipt=receipt;}
}
