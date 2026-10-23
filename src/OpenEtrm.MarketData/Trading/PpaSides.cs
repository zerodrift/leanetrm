using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public abstract record PpaSide : Side
{
    [IdentityField]public PpaSideTerms Terms { get; }
    protected PpaSide(Guid id,PpaSideTerms terms):base(id)
    {Terms=terms??throw PpaErrors.Error("ppa_side","side","Explicit side terms are required.");}
    public override ImmutableArray<CurveRequest> Dependencies=>throw PpaErrors.Error("ppa_context","dependencies","Prepare the exact interval inputs before declaring dependencies.");
    public override IntervalSideSchedule GenerateSchedule()=>new(this);
    public override IntervalSideValuation PV(SideValuationContext context)=>context.Interval is {} interval?
        interval.Session.Evaluate(this,interval.Instrument,interval.Coverage,context):throw PpaErrors.Error("ppa_context","side","Use this instrument's prepared interval scope.");
}
[EconomicDefinition("power_physical_side_v1")]
public sealed record PowerPhysicalSide:PpaSide
{
    [IdentityField]public ReferenceKey Location { get; }
    public PowerPhysicalSide(Guid id,PpaSideTerms terms,ReferenceKey location):base(id,terms){Money.RequireKey(location);Location=location;}
}
[EconomicDefinition("ppa_fixed_side_v1")]
public sealed record PpaFixedSide:PpaSide
{
    [IdentityField]public FixedPriceTerm Price { get; }
    public PpaFixedSide(Guid id,PpaSideTerms terms,FixedPriceTerm price):base(id,terms)
    {Price=price??throw PpaErrors.Error("ppa_price_rule","fixed_price","Select an explicit price term.");}
}
[EconomicDefinition("ppa_floating_side_v1")]
public sealed record PpaFloatingSide:PpaSide
{
    [IdentityField]public IndexPrice Price { get; }
    public PpaFloatingSide(Guid id,PpaSideTerms terms,IndexPrice price):base(id,terms)
    {
        if(price is null || price.Multiplier.CompareTo(default)<=0 || price.Adder is null || string.IsNullOrWhiteSpace(price.Curve.Code))
            throw PpaErrors.Error("ppa_price_rule","index","Select an exact curve, positive multiplier and compatible explicit adder.");
        Price=price;
    }
}
[EconomicDefinition("ppa_certificate_delivery_v1")]
public sealed record CertificateDeliverySide:PpaSide
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
[EconomicDefinition("ppa_capacity_delivery_v1")]
public sealed record CapacityDeliverySide:PpaSide
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
    public PpaSide Side { get; }
    internal IntervalSideSchedule(PpaSide side):base(side.Id,side.Terms.Currency,side.Terms.UnitQuantity){Side=side;}
}
public abstract record PpaSchedule:SideSchedule
{
    protected PpaSchedule(Guid id,ReferenceKey currency,Quantity unit):base(id,currency,unit,"interval_side_schedule_v1"){}
    public override ImmutableArray<CurveRequest> Dependencies=>throw PpaErrors.Error("ppa_context","dependencies","Dependencies require prepared delivery coverage.");
}
internal sealed record IntervalSideContext(IntervalValuationSession Session,PpaInstrument Instrument,DeliveryCoverage Coverage);
public sealed partial class SideValuationContext
{
    internal IntervalSideContext? Interval { get; init; }
}
