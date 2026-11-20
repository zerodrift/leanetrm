using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public enum OperatingOutputKind { EnergyOutput=1,FuelInput,ChargingEnergy,DischargingEnergy,PaymentEnergy,CertificateEnergy,Starts,GuaranteedFuelInput,ConsumptionFuelInput,StorageInjection,StorageWithdrawal }
[EconomicDefinition("operating_plan_reference_v1")]
public sealed record OperatingPlanReference([property:IdentityField]DefinitionRevision Definitions,
    [property:IdentityField]Guid Id,[property:IdentityField]int Version);
[EconomicDefinition("operating_output_port_v1")]
public sealed record OperatingOutputPort([property:IdentityField]OperatingPlanReference Plan,
    [property:IdentityField]AssetKey Resource,[property:IdentityField]OperatingOutputKind Kind,
    [property:IdentityField]ReferenceKey Quantity);
[EconomicDefinition("operating_quantity_term_v1")]
public sealed record OperatingQuantityTerm([property:IdentityField]OperatingOutputPort? Port,
    [property:IdentityField]Quantity? Literal);
[EconomicDefinition("operating_side_period_v1")]
public record OperatingSidePeriod
{
    public DeliveryInterval Interval { get; }
    [IdentityField]public DateTimeOffset From=>Interval.Start;
    [IdentityField]public DateTimeOffset To=>Interval.End;
    [IdentityField]public DateOnly OperatingDate=>Interval.OperatingDate;
    [IdentityField]public int Hour=>Interval.HourBeginning;
    [IdentityField]public int Minute=>Interval.Minute;
    [IdentityField]public int Occurrence=>Interval.Occurrence;
    [IdentityField]public DateOnly PaymentFrom { get; }
    [IdentityField]public DateOnly PaymentTo { get; }
    [IdentityField]public OperatingQuantityTerm Quantity { get; }
    [IdentityField]public CurveRequest? Projection { get; }
    public OperatingSidePeriod(DeliveryInterval interval,DateOnly paymentFrom,DateOnly paymentTo,
        OperatingQuantityTerm quantity,CurveRequest? projection)
    {Interval=interval;PaymentFrom=paymentFrom;PaymentTo=paymentTo;Quantity=quantity;Projection=projection;}
}
[EconomicDefinition("locational_operating_side_period_v1")]
public sealed record LocationalOperatingSidePeriod : OperatingSidePeriod
{
    [IdentityField] public ExactDecimal PriceAdjustment { get; }
    public LocationalOperatingSidePeriod(DeliveryInterval interval,DateOnly paymentFrom,DateOnly paymentTo,
        OperatingQuantityTerm quantity,CurveRequest projection,ExactDecimal priceAdjustment)
        :base(interval,paymentFrom,paymentTo,quantity,projection) => PriceAdjustment=priceAdjustment;
}
[EconomicDefinition("operating_side_terms_v1")]
public sealed record OperatingSideTerms([property:IdentityField]Quantity UnitQuantity,
    [property:IdentityField]QuoteSpecification Quote,[property:IdentityField]PaymentRule? Payment,
    [property:IdentityField]ComponentKind Kind,[property:IdentityField]ImmutableArray<OperatingSidePeriod> Periods,
    [property:IdentityField]string Implementation,[property:IdentityField]SideCalendarPolicy CalendarPolicy)
{
    public const string SupportedImplementation="supplied_operating_side_v1";
}
public sealed record OperatingQuantityCell(OperatingOutputPort Port,DeliveryInterval Interval,ExactQuantity Quantity,
    VolumeState State,Guid EvidenceId,DateTimeOffset KnownAt);
public sealed record OperatingScenarioQuantityCell(OperatingOutputPort Port,DeliveryInterval Interval,ExactQuantity Quantity);
internal sealed record OperatingResolvedQuantity(ExactQuantity Quantity,VolumeState State,OperatingQuantityCell? Evidence);
public sealed class OperatingQuantityInputs
{
    public const string ScenarioImplementation="hypothetical_supplied_operating_plan_v1";
    public MarketSnapshot Snapshot { get; }
    public DateTimeOffset? KnownAt { get; }
    public IntervalKnowledgePolicy? KnowledgePolicy { get; }
    public ImmutableArray<OperatingQuantityCell> Cells { get; }
    public ImmutableArray<OperatingScenarioQuantityCell> ScenarioCells { get; }
    public string? ScenarioAssumption { get; }
    private readonly ImmutableDictionary<(OperatingOutputPort,DeliveryInterval),OperatingResolvedQuantity> values;
    public OperatingQuantityInputs(MarketSnapshot snapshot,DateTimeOffset knownAt,ImmutableArray<OperatingQuantityCell> cells,
        IntervalKnowledgePolicy knowledgePolicy)
    {
        if(snapshot is null||!Enum.IsDefined(knowledgePolicy)||snapshot.Knowledge is not {} knowledge||(knowledgePolicy switch{
            IntervalKnowledgePolicy.ResolvedCutoff=>knownAt!=knowledge.KnownAt,
            IntervalKnowledgePolicy.ValuationInstant=>knownAt!=snapshot.ValuationAt||knownAt>knowledge.KnownAt,
            _=>true}))
            throw OperatingSchedules.Invalid("operating_context","Use the snapshot's certified knowledge and an explicit resolved-cutoff or valuation-instant source policy.");
        if(snapshot is null||!TradingInstant.IsSupported(knownAt)||cells.IsDefault||cells.Length>4096||cells.Any(c=>c is null||c.Port is null||c.Quantity is null||
            c.Port.Plan is null||c.Port.Plan.Definitions!=snapshot.Definitions||c.Quantity.Specification!=c.Port.Quantity||
            c.Quantity.NumeratorValue.Sign<0||!Enum.IsDefined(c.State)||c.EvidenceId==Guid.Empty||!TradingInstant.IsSupported(c.KnownAt)||c.KnownAt>knownAt||
            c.State is VolumeState.Forecast or VolumeState.Estimated&&c.KnownAt>snapshot.ValuationAt||
            c.State==VolumeState.Metered&&c.Interval is {} interval&&(interval.End>snapshot.ValuationAt||c.KnownAt<interval.End)))
            throw OperatingSchedules.Invalid("operating_inputs","Supply complete typed quantities and eligible evidence under one exact snapshot, definition revision and explicit knowledge cutoff.");
        foreach(var cell in cells){OperatingSchedules.Port(cell.Port);OperatingSchedules.Interval(cell.Interval);}
        if(cells.GroupBy(c=>(c.Port,c.Interval.Start)).Any(g=>g.Count()!=1))
            throw OperatingSchedules.Invalid("operating_inputs","One source port has exactly one cell at each native instant.");
        Snapshot=snapshot;KnownAt=knownAt;KnowledgePolicy=knowledgePolicy;Cells=cells;ScenarioCells=[];
        values=cells.ToImmutableDictionary(c=>(c.Port,c.Interval),c=>new OperatingResolvedQuantity(c.Quantity,c.State,c));
    }
    private OperatingQuantityInputs(MarketSnapshot snapshot,ImmutableArray<OperatingScenarioQuantityCell> cells,string implementation)
    {
        if(snapshot is null||implementation!=ScenarioImplementation||cells.IsDefault||cells.Length>4096||
            cells.Any(c=>c is null||c.Port is null||c.Port.Plan is null||c.Quantity is null||c.Port.Plan.Definitions!=snapshot.Definitions||
                c.Quantity.Specification!=c.Port.Quantity||c.Quantity.NumeratorValue.Sign<0))
            throw OperatingSchedules.Invalid("operating_scenario","Supply explicitly hypothetical typed quantities under the selected market and implementation.");
        foreach(var cell in cells){OperatingSchedules.Port(cell.Port);OperatingSchedules.Interval(cell.Interval);}
        if(cells.GroupBy(c=>(c.Port,c.Interval.Start)).Any(g=>g.Count()!=1))
            throw OperatingSchedules.Invalid("operating_scenario","Each hypothetical quantity port has one cell at each native instant.");
        Snapshot=snapshot;Cells=[];ScenarioCells=cells;ScenarioAssumption=implementation;
        values=cells.ToImmutableDictionary(c=>(c.Port,c.Interval),c=>new OperatingResolvedQuantity(c.Quantity,VolumeState.Forecast,null));
    }
    public static OperatingQuantityInputs Scenario(MarketSnapshot snapshot,ImmutableArray<OperatingScenarioQuantityCell> cells,string implementation)=>new(snapshot,cells,implementation);
    internal OperatingResolvedQuantity Resolve(OperatingOutputPort port,DeliveryInterval interval)=>values.TryGetValue((port,interval),out var value)?value:
        throw OperatingSchedules.Invalid("operating_quantity_missing","The admitted operating plan does not cover the exact quantity port and interval.");
}

public sealed partial record FixedSide
{
    [IdentityField]public OperatingSideTerms? OperatingDefinition { get; }
    public FixedSide(Guid id,OperatingSideTerms terms,FixedPriceTerm price):base(id,(terms??throw OperatingSchedules.Invalid("operating_terms","Supply explicit operating terms.")).CalendarPolicy)
    {
        OperatingSchedules.Validate(terms,false);
        if(price is null||price.Unit!=PriceUnit.FromQuote(terms.Quote))throw OperatingSchedules.Invalid("operating_price","The fixed price must use the exact declared native quantity and currency.");
        OperatingDefinition=terms;Price=price;UnitQuantity=terms.UnitQuantity;
    }
}
public sealed partial record FloatingSide
{
    [IdentityField]public OperatingSideTerms? OperatingDefinition { get; }
    public FloatingSide(Guid id,OperatingSideTerms terms):base(id,(terms??throw OperatingSchedules.Invalid("operating_terms","Supply explicit operating terms.")).CalendarPolicy)
    {OperatingSchedules.Validate(terms,true);OperatingDefinition=terms;DateQuote=terms.Quote;UnitQuantity=terms.UnitQuantity;}
}

public sealed partial class SideValuationContext
{
    private OperatingQuantityInputs? operatingQuantities;
    public SideValuationContext WithOperatingQuantities(OperatingQuantityInputs inputs)
    {
        if(inputs is null||!Snapshot.HasSameInputs(inputs.Snapshot))throw OperatingSchedules.Invalid("operating_context","Operating quantities must belong to the complete selected market context.");
        var copy=(SideValuationContext)MemberwiseClone();copy.operatingQuantities=inputs;
        copy.sideValues=new(ReferenceEqualityComparer.Instance);return copy;
    }
    internal OperatingResolvedQuantity? OperatingQuantity(OperatingQuantityTerm term,DeliveryInterval interval)=>term.Literal is not null?null:
        operatingQuantities is {} inputs?inputs.Resolve(term.Port!,interval):
        throw OperatingSchedules.Invalid("operating_inputs","Supply this operation's complete operating quantity inputs.");
}

[EconomicDefinition("operating_right_instrument_v1")]
public sealed record OperatingRightInstrument:SidedInstrument
{
    [IdentityField]public AssetKey Resource { get; }
    [IdentityField]public override ReferenceKey Currency { get; }
    [IdentityField]public override Quantity UnitQuantity { get; }
    public override bool SupportsQuantityScaling=>false;
    public override bool SupportsFixedUnitBinding=>true;
    internal override bool SupportsSettlement=>false;
    public OperatingRightInstrument(Guid id,AssetKey resource,Quantity unitQuantity,ReferenceKey currency,
        ImmutableArray<SideMembership> sides):base(id,sides)
    {
        if(resource is null||unitQuantity is null||unitQuantity.Amount!=ExactDecimal.Parse("1")||resource.Revision.CatalogId==Guid.Empty||
            resource.Revision.RevisionId==Guid.Empty||string.IsNullOrWhiteSpace(resource.Code)||sides.Length>16||sides.Select(s=>s.Side.Id).Distinct().Count()!=sides.Length||
            sides.Any(s=>s.Coefficient!=ExactDecimal.Parse("1")&&s.Coefficient!=ExactDecimal.Parse("-1")||s.QuantityRatio is not null))
            throw OperatingSchedules.Invalid("operating_instrument","Supply one complete resource right and distinct explicitly signed side memberships.");
        Money.RequireKey(currency);Money.RequireKey(unitQuantity.Specification);
        var terms=sides.Select(s=>OperatingSchedules.Terms(s.Side)).ToArray();
        if(terms.Any(t=>t.UnitQuantity!=unitQuantity||t.Quote.Currency!=currency)||currency.Revision!=unitQuantity.Specification.Revision)
            throw OperatingSchedules.Invalid("operating_instrument","Each side must normalize to this exact resource-right basis and currency.");
        var ports=terms.SelectMany(t=>t.Periods).Select(p=>p.Quantity.Port).OfType<OperatingOutputPort>().ToArray();
        if(ports.Length==0||ports.Any(p=>p.Resource!=resource)||ports.Select(p=>p.Plan).Distinct().Count()!=1)
            throw OperatingSchedules.Invalid("operating_plan","Coupled sides require one exact operating plan and resource version.");
        Resource=resource;UnitQuantity=unitQuantity;Currency=currency;
    }
    public override void ValidateBinding(Quantity quantity,DeliveryCoverage? coverage)
    {
        if(quantity!=UnitQuantity)throw OperatingSchedules.Invalid("operating_right_quantity","A specified exclusive resource right is booked once; fractional or multiplied rights need a separately qualified contract.");
        base.ValidateBinding(quantity,coverage);
    }
}

internal sealed record OperatingScheduleRow(SidePaymentKey Key,Quantity UnitQuantity,OperatingSidePeriod Period,
    GeneratedPaymentDate? GeneratedDate,PaymentRule? Rule,FixedPriceTerm? FixedPrice,QuoteSpecification Quote,ComponentKind Kind);

internal static class OperatingSchedules
{
    internal static MarketDataException Invalid(string code,string message)=>new(code,message);
    internal static OperatingSideTerms Terms(Side side)=>side switch
    {FixedSide {OperatingDefinition:{} terms}=>terms,FloatingSide {OperatingDefinition:{} terms}=>terms,
        _=>throw Invalid("operating_side","A resource-right composition requires explicitly supported operating quantity terms on each side.")};
    internal static void Interval(DeliveryInterval interval)
    {
        if(interval is null||!TradingInstant.IsSupported(interval.Start)||!TradingInstant.IsSupported(interval.End)||interval.Start>=interval.End||
            interval.HourBeginning is <0 or >23||interval.Minute is <0 or >59||interval.Occurrence is not(1 or 2))
            throw Invalid("operating_grid","Supply exact supported UTC interval boundaries and retained operating labels.");
    }
    internal static void Port(OperatingOutputPort port)
    {
        if(port is null||port.Plan is null||port.Resource is null||port.Plan.Definitions.CatalogId==Guid.Empty||port.Plan.Definitions.RevisionId==Guid.Empty||
            port.Plan.Id==Guid.Empty||port.Plan.Version<1||port.Resource.Revision!=port.Plan.Definitions||string.IsNullOrWhiteSpace(port.Resource.Code)||!Enum.IsDefined(port.Kind))
            throw Invalid("operating_port","A quantity port needs exact plan/resource versions and an explicit supported output kind.");
        Money.RequireKey(port.Quantity);
    }
    internal static void Validate(OperatingSideTerms terms,bool floating)
    {
        if(terms is null||terms.UnitQuantity is null||terms.Quote is null||(terms.Kind==ComponentKind.Payment)!=(terms.Payment is not null)||terms.Implementation!=OperatingSideTerms.SupportedImplementation||
            terms.UnitQuantity.Amount!=ExactDecimal.Parse("1")||terms.Kind is not(ComponentKind.Payment or ComponentKind.Delivery)||
            terms.Periods.IsDefaultOrEmpty||terms.Periods.Length>4096)
            throw Invalid("operating_terms","Supply explicit whole-term side, quantity, price, payment and implementation terms.");
        Money.RequireKey(terms.UnitQuantity.Specification);Money.RequireKey(terms.Quote.Key);
        if(terms.UnitQuantity.Specification.Revision!=terms.Quote.Key.Revision||terms.Quote.Currency.Revision!=terms.Quote.Key.Revision||
            terms.Quote.Quantity.Revision!=terms.Quote.Key.Revision||terms.Quote.Kind!=QuoteKind.Outright)
            throw Invalid("operating_terms","Resource-right and native quote references must share one exact reference revision.");
        _=PriceUnit.FromQuote(terms.Quote);
        terms.CalendarPolicy.RequirePurposes(false,false,false);
        if(terms.Payment is {} payment&&(payment.Calendar is not null||payment.DayBasis==OpenEtrm.MarketData.Trading.PaymentDayBasis.BusinessDays||
            payment.Adjustment!=OpenEtrm.Shared.Temporal.DateRoll.Unadjusted))
            throw Invalid("operating_calendar","The supplied native grid and calendar-day unadjusted payments require explicit not-applicable calendar bindings.");
        DateTimeOffset? end=null;
        foreach(var period in terms.Periods)
        {
            if(period is null||period.Quantity is null||(period.Quantity.Port is null)==(period.Quantity.Literal is null)||floating!=(period.Projection is not null))
                throw Invalid("operating_terms","Each row requires one explicit literal or source quantity and its selected price mode.");
            Interval(period.Interval);
            if(end is {} previous&&period.Interval.Start<previous)throw Invalid("operating_grid","Side rows retain explicit order without overlapping delivery intervals.");
            end=period.Interval.End;
            var generated=terms.Payment?.Generate(period.PaymentFrom,period.PaymentTo);
            if(period.Interval.OperatingDate<period.PaymentFrom||period.Interval.OperatingDate>=period.PaymentTo||
                generated is {} selected&&FixedSide.PaymentBoundary(selected.Date)<period.Interval.End)
                throw Invalid("operating_payment","The row must belong to its explicit payment period, with payment no earlier than delivery ends.");
            var quantity=period.Quantity.Literal?.Specification??period.Quantity.Port!.Quantity;
            if(quantity!=terms.Quote.Quantity||period.Quantity.Literal is {} literal&&literal.Amount.CompareTo(default)<0)
                throw Invalid("quantity_mismatch","An operating row requires nonnegative quantities in its exact native price denominator.");
            if(period.Quantity.Port is {} port)Port(port);
            if(period.Projection is {} projection&&(projection.Curve.Revision!=projection.Contract.Revision||
                projection.Curve.Revision.CatalogId==Guid.Empty||projection.Curve.Revision.RevisionId==Guid.Empty||projection.Contract.Id==Guid.Empty||string.IsNullOrWhiteSpace(projection.Curve.Code)||
                period.Quantity.Port is {} source&&source.Plan.Definitions!=projection.Curve.Revision))
                throw Invalid("operating_price","Each projected price needs one exact curve/contract under its quantity source's definition revision.");
        }
    }
    internal static SideSchedule Schedule(Side side)=>new OperatingSideSchedule(side,Terms(side));
}

public sealed record OperatingSideSchedule:SideSchedule
{
    public ImmutableArray<ScheduleRow> Periods { get; }
    public override IReadOnlyList<ScheduleRow> Rows=>Periods;
    public override ImmutableArray<CurveRequest> Dependencies { get; }
    internal OperatingSideSchedule(Side side,OperatingSideTerms terms):base(side.Id,terms.Quote.Currency,terms.UnitQuantity,terms.Implementation)
    {
        var fixedPrice=(side as FixedSide)?.Price;
        Periods=[..terms.Periods.Select((period,n)=>
        {
            var date=terms.Payment?.Generate(period.PaymentFrom,period.PaymentTo);
            var key=new SidePaymentKey(side.Id,n,date is {} selected?FixedSide.PaymentBoundary(selected.Date):period.Interval.End,Currency)
                {Timing=terms.Kind==ComponentKind.Payment?ValueTimingKind.UtcPaymentDay:ValueTimingKind.Instant};
            return new ScheduleRow(new OperatingScheduleRow(key,UnitQuantity,
                period,date,terms.Payment,fixedPrice,terms.Quote,terms.Kind));
        })];
        Dependencies=[..terms.Periods.Select(p=>p.Projection).OfType<CurveRequest>().Distinct()];
    }
    public override SideValuation Evaluate(SideValuationContext context)
    {
        if(context is null)throw OperatingSchedules.Invalid("operating_context","Supply this side's explicit valuation operation.");
        var keys=Periods.Select(p=>p.Key).ToImmutableArray();
        foreach(var key in keys)key.RequireUndue(context.Snapshot);
        var factors=context.Discounting.Resolve(keys,context.Snapshot);
        if(factors.IsDefault||factors.Length!=keys.Length||factors.Any(f=>f is null||f.DiscountFactor.CompareTo(default)<=0||!Enum.IsDefined(f.Source)||!f.HasValidPaymentDayTerms(context.Snapshot.ValuationAt)||
            f.Source==DiscountFactorSource.Undiscounted&&f.DiscountFactor!=ExactDecimal.Parse("1")||!context.Snapshot.HasSameInputs(f.Snapshot))||
            factors.Select(f=>f.Payment).Distinct().Count()!=keys.Length||!factors.Select(f=>f.Payment).ToHashSet().SetEquals(keys))
            throw OperatingSchedules.Invalid("side_payment_factors","Supply one exact positive discount factor per owned occurrence and full context.");
        var selected=factors.ToDictionary(f=>f.Payment);
        var rows=Periods.Select(row=>
        {
            var operation=row.Operating!;var selectedQuantity=context.OperatingQuantity(operation.Period.Quantity,operation.Period.Interval);
            var quantity=selectedQuantity?.Quantity??ExactQuantity.FromQuantity(operation.Period.Quantity.Literal!);
            var price=operation.FixedPrice is {} fixedPrice?fixedPrice.At(operation.Period.Interval.OperatingDate):context.ExactPrice(operation.Period.Projection!.Value);
            if(price.Unit!=PriceUnit.FromQuote(operation.Quote))throw OperatingSchedules.Invalid("operating_price","Resolved operating prices require the exact selected quote unit.");
            if(operation.Period is LocationalOperatingSidePeriod location)price=price.Add(ExactUnitPrice.FromPrice(new UnitPrice(location.PriceAdjustment,price.Unit)));
            var amount=quantity.Times(price);var factor=selected[row.Key];
            return new ScheduleRowValue(row.SideId,null,operation.Kind,operation.Period.Interval,quantity,selectedQuantity?.State??VolumeState.Contractual,null,[],[],
                [new PriceComponent(quantity,price,operation.FixedPrice is null?RowPriceState.Projected:RowPriceState.Fixed,operation.Period.Projection?.Curve,null,ExactDecimal.Parse("1"),null)],
                amount,ExactDecimal.Parse("1"),operation.Rule is null?OperatingSideTerms.SupportedImplementation:PaymentRule.Implementation,row.PaymentAt,factor,amount.Scale(factor.DiscountFactor),null,null,ValueCoverage.Discounted,null){Row=row,OperatingEvidence=selectedQuantity?.Evidence};
        }).ToImmutableArray();
        return new OperatingSideValuation(this,context,rows);
    }
}
public sealed record OperatingSideValuation:SideValuation
{
    public ImmutableArray<ScheduleRowValue> Rows { get; }
    public override SideScheduleSnapshot InspectSchedule(string manifest,ExactDecimal scale,OpenEtrm.Shared.Numerics.SignedRatio coefficient,Func<DateOnly,DateTimeOffset> boundary)=>
        SideScheduleSnapshot.FromRows(this,Rows,manifest,scale,coefficient,boundary);
    internal OperatingSideValuation(OperatingSideSchedule schedule,SideValuationContext context,ImmutableArray<ScheduleRowValue> rows)
        :base(schedule,context,rows.Select(r=>r.RequirePresentValue()).Aggregate((a,b)=>a.Add(b))){Rows=rows;}
}
