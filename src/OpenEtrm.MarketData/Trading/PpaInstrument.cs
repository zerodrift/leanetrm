using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

[EconomicDefinition("ppa_instrument_v2")]
public sealed record PpaInstrument:SidedInstrument
{
    public const string Implementation="ppa_instrument_v2";
    [IdentityField]public DefinitionRevision Definitions { get; }
    [IdentityField]public ReferenceRevision Reference { get; }
    [IdentityField]public ImmutableArray<PpaMembership> Memberships { get; internal init; }
    public PpaNormalization Normalization=>Memberships[0].Side.Terms.Normalization;
    public ImmutableArray<AssetKey> NormalizationAssets=>Normalization.Assets;
    [IdentityField]public bool Discounting { get; }
    public PpaDelivery Delivery=>Memberships[0].Side.Terms.Delivery;
    public override ReferenceKey Currency=>Memberships[0].Side.Terms.Currency;
    public override Quantity UnitQuantity=>Memberships[0].Side.Terms.UnitQuantity;
    public string Capability { get; }
    public override void ValidateBinding(Quantity quantity,DeliveryCoverage? coverage)
    {
        if(coverage is null||quantity.Amount.CompareTo(default)<=0)
            throw new MarketDataException("ppa_segment_coverage","PPA bindings require explicit coverage and positive MW; memberships retain direction.");
    }
    public override long WorkRows(UnitValuationContext context)=>context.Setup.Interval is {} inputs&&context.Coverage is {} coverage
        ?Memberships.Sum(m=>(long)inputs.Rows(this,m.Side,coverage).Length):0;
    public PpaInstrument(Guid id,DefinitionRevision definitions,ReferenceRevision reference,ImmutableArray<PpaMembership> memberships,
        bool discounting,string implementation):base(id,Members(memberships))
    {
        if(implementation!=Implementation)throw PpaErrors.Error("ppa_term_unsupported","implementation","Unknown instrument implementation.");
        Definitions=definitions;Reference=reference;Memberships=memberships;Discounting=discounting;
        Capability=PpaCapabilities.Validate(this);
    }
    private static ImmutableArray<SideMembership> Members(ImmutableArray<PpaMembership> members)
    {
        if(members.IsDefaultOrEmpty || members.Any(m=>m is null || m.Side is null || m.Slot is null))throw PpaErrors.Error("ppa_instrument","memberships","Explicit side memberships are required.");
        return [..members.Select(m=>new SideMembership(m.Side,m.Coefficient))];
    }
    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        if(context.Measure!=ValuationMeasure.PresentValue || context.Setup.Interval is not {} inputs || context.Coverage is not {} coverage)
            throw PpaErrors.Error("ppa_context","valuation","Select prepared interval inputs, explicit coverage and present-value measurement.");
        return new(Id,"ppa_interval_leg_pv_v1",inputs.Dependencies(this,coverage),UnitQuantity.Specification,Currency);
    }
    public override ExactMoney Value(UnitValuationContext context)
    {
        if(context.IntervalSession is not {} session || context.Coverage is not {} coverage)
            throw PpaErrors.Error("ppa_context","valuation","Use one prepared request session and explicit coverage.");
        var value=session.Value(this,coverage,context.DiscountProvider,context.Cancellation);
        context.RecordMeasures(ValueMeasures.From(value));
        return value.PresentValue;
    }
}

public static class PpaCapabilities
{
    public const string Version="ppa_capabilities_v1";
    public const int MaximumSlotOrdinal=7;
    internal static string Validate(PpaInstrument instrument)
    {
        var members=instrument.Memberships;var first=members[0].Side.Terms;
        if(instrument.Definitions.CatalogId==Guid.Empty || instrument.Definitions.RevisionId==Guid.Empty || instrument.Reference.CatalogId==Guid.Empty || instrument.Reference.RevisionId==Guid.Empty)
            throw PpaErrors.Error("ppa_reference_revision","pins","Select exact nonempty definition and reference revisions.");
        if(instrument.Normalization is null || instrument.NormalizationAssets.IsDefault || instrument.NormalizationAssets.Distinct().Count()!=instrument.NormalizationAssets.Length ||
            instrument.NormalizationAssets.Any(a=>a.Revision!=instrument.Definitions || string.IsNullOrWhiteSpace(a.Code)))
            throw PpaErrors.Error("ppa_normalization","assets","Supply a distinct exact asset set, empty only for contractual profiles.");
        if(members.Select(m=>m.Slot).Distinct().Count()!=members.Length || members.Select(m=>m.Side.Id).Distinct().Count()!=members.Length)
            throw PpaErrors.Error("ppa_instrument","memberships","Slots and side definitions must be unique within an instrument.");
        foreach(var m in members)
        {
            var t=m.Side.Terms;var slot=m.Slot.ToString();
            if(t.Normalization is null||t.Normalization.Basis!=t.UnitQuantity.Specification||
                DefinitionIdentity.For(t.Normalization)!=DefinitionIdentity.For(first.Normalization))
                throw PpaErrors.Error("ppa_normalization",slot,"Every side must retain the same explicit normalization assets, exact basis and policy.");
            if(t.InputPorts.IsDefault||t.InputPorts.Any(p=>p is null||p.Slot is null||p.Schedule is null||p.Quantity.Revision!=instrument.Reference))
                throw PpaErrors.Error("ppa_quantity_reference",slot,"Declare explicit typed quantity input ports; use an empty array for independent quantities.");
            if(t.Quantity is not SlotQuantity&&!t.InputPorts.IsEmpty)
                throw PpaErrors.Error("ppa_quantity_reference",slot,"Independent quantities cannot declare sibling input ports.");
            if(t.Quantity is VolumeQuantity or AccreditedQuantity && t.Normalization.Policy!=PpaNormalizationPolicy.SumCapacityInForce)
                throw PpaErrors.Error("ppa_normalization",slot,"The quantity rule must use its explicitly selected normalization policy.");
            if(t.ScheduleQuantity.Revision!=instrument.Reference)
                throw PpaErrors.Error("ppa_reference_revision",slot,"The native schedule quantity must retain the instrument's exact reference revision.");
            if(t.Quantity is VolumeQuantity volume&&volume.Energy.Revision!=instrument.Reference||
                t.Quantity is ProfileQuantity profileTypes&&(profileTypes.Capacity.Revision!=instrument.Reference||profileTypes.Energy.Revision!=instrument.Reference))
                throw PpaErrors.Error("ppa_reference_revision",slot,"Quantity source types must retain the instrument's exact reference revision.");
            var declaredOutput=m.Side switch
            {
                FixedSide fixedOutput=>fixedOutput.Price.Unit.Quantity,
                FloatingSide floatingOutput=>floatingOutput.Price.Adder.Unit.Quantity,
                CertificateDeliverySide certificate=>certificate.CertificateQuantity,
                CapacityDeliverySide capacity=>capacity.CapacityQuantity,
                PowerPhysicalSide when t.Quantity is ProfileQuantity profileOutput=>profileOutput.Energy,
                PowerPhysicalSide when t.Quantity is VolumeQuantity volumeOutput=>volumeOutput.Energy,
                _=>t.ScheduleQuantity
            };
            if(declaredOutput!=t.ScheduleQuantity)
                throw PpaErrors.Error("ppa_quantity_unit",slot,"The side's native schedule output must match its exact quantity or price contract.");
            if(!Enum.IsDefined(m.Slot.Role)||m.Slot.Ordinal is <0 or >MaximumSlotOrdinal || m.Coefficient!=PpaErrors.One && m.Coefficient!=ExactDecimal.Parse("-1"))
                throw PpaErrors.Error("ppa_side",slot,"Select a supported slot with ordinal 0–7 and a direction coefficient of plus or minus one.");
            if(t.Delivery is null || t.DateRule is null || t.Quantity is null || t.Schedule is null || t.Schedule.Delivery!=t.Delivery || t.Delivery!=first.Delivery || t.UnitQuantity!=first.UnitQuantity || t.UnitQuantity.Amount!=PpaErrors.One)
                throw PpaErrors.Error("ppa_side",slot,"Sides require compatible explicit monthly delivery, date rule and one-MW contract unit.");
            if(t.Currency!=first.Currency)throw PpaErrors.Error("ppa_currency",slot,"All sides use the same exact currency.");
            if(t.Currency.Revision!=instrument.Reference || t.UnitQuantity.Specification.Revision!=instrument.Reference || t.Delivery.Clock.Revision!=instrument.Reference)
                throw PpaErrors.Error("ppa_reference_revision",slot,"Side references must retain the instrument revision.");
            if(t.DateRule.Calendar is {} calendar && calendar.Key.Revision!=instrument.Reference)
                throw PpaErrors.Error("ppa_reference_revision",slot,"The value-date calendar must retain the instrument reference revision.");
            if(t.Quantity is ProfileQuantity profile && (t.Schedule.Cadence!=IntervalScheduleCadence.NativeIntervals ||
                t.Schedule.Hours is not {} hours || hours.Membership!=profile.Hours.Membership || hours.Peak!=profile.Hours.Peak || !hours.Holidays.SequenceEqual(profile.Hours.Holidays)) ||
                t.Quantity is VolumeQuantity && (t.Schedule.Cadence!=IntervalScheduleCadence.NativeIntervals || t.Schedule.Hours?.Membership!=BlockMembership.All) ||
                t.Quantity is AccreditedQuantity && t.Schedule.Cadence!=IntervalScheduleCadence.OperatingMonths)
                throw PpaErrors.Error("ppa_cadence",slot,"The generated schedule must retain its quantity rule's exact delivery cadence and mask.");
            Money.RequireKey(t.Currency);Money.RequireKey(t.UnitQuantity.Specification);Money.RequireKey(t.Delivery.Clock);Money.RequireKey(t.ScheduleQuantity);
            var d=t.Delivery;var months=(d.To.Year-d.From.Year)*12+d.To.Month-d.From.Month;
            if(d.IntervalMinutes is not(15 or 60))throw PpaErrors.Error("ppa_cadence",slot,"Only native 15- or 60-minute intervals are supported.");
            if(d.From.Day!=1 || d.To.Day!=1 || d.From>=d.To || d.From.Year<2000 || d.To>new DateOnly(2100,1,1))
                throw PpaErrors.Error("ppa_segment_boundary",slot,"Terms use complete supported operating months.");
            if(months>(d.IntervalMinutes==60?360:120))throw PpaErrors.Error("ppa_size",slot,"The term exceeds the selected cadence limit.");
            bool kind=m.Slot.Role switch {
                PpaRole.EnergyDelivery=>m.Side is PowerPhysicalSide,
                PpaRole.CertificateDelivery=>m.Side is CertificateDeliverySide,
                PpaRole.CapacityDelivery=>m.Side is CapacityDeliverySide,
                PpaRole.StrikePayment or PpaRole.FixedCharge=>m.Side is FixedSide,
                PpaRole.IndexedPayment=>m.Side is FloatingSide,
                PpaRole.CertificatePayment or PpaRole.CapacityPayment=>m.Side is FixedSide or FloatingSide,_=>false};
            if(!kind)throw PpaErrors.Error("ppa_side",slot,"The role does not accept this side kind.");
            if(t.Quantity is SlotQuantity q)
            {
                if(m.Side is not(FixedSide or FloatingSide) || q.Slots.IsDefaultOrEmpty || q.Slots.Distinct().Count()!=q.Slots.Length)
                    throw PpaErrors.Error("ppa_quantity_reference",slot,"Select distinct delivery slots as quantity targets.");
                if(!t.InputPorts.Select(p=>p.Slot).SequenceEqual(q.Slots))
                    throw PpaErrors.Error("ppa_quantity_reference",slot,"Declare one typed input port for every selected quantity slot, in the selected order.");
                var priceUnit=m.Side is FixedSide fixedPayment?fixedPayment.Price.Unit:((FloatingSide)m.Side).Price.Adder.Unit;
                foreach(var port in t.InputPorts)
                {
                    var target=port.Slot;
                    if(port.Quantity!=priceUnit.Quantity||DefinitionIdentity.For(port.Schedule)!=DefinitionIdentity.For(t.Schedule))
                        throw PpaErrors.Error("ppa_quantity_unit",slot,"Each quantity input must retain the price denominator and exact declared schedule.");
                    var bound=members.SingleOrDefault(x=>x.Slot==target);
                    if(bound is null || bound.Side is FixedSide or FloatingSide)throw PpaErrors.Error("ppa_quantity_reference",slot,"Payments cannot provide quantities; no cycles are allowed.");
                    if(port.Quantity!=bound.Side.Terms.ScheduleQuantity)
                        throw PpaErrors.Error("ppa_quantity_unit",slot,"The selected delivery slot must supply the port's exact declared quantity type and unit.");
                    if(DefinitionIdentity.For(t.Schedule)!=DefinitionIdentity.For(bound.Side.Terms.Schedule))
                        throw PpaErrors.Error("ppa_cadence",slot,"A linked payment must retain its delivery slot's exact structural schedule.");
                    if(m.Slot.Role==PpaRole.CertificatePayment && bound.Side is not CertificateDeliverySide || m.Slot.Role==PpaRole.CapacityPayment && bound.Side is not CapacityDeliverySide ||
                        m.Slot.Role is PpaRole.StrikePayment or PpaRole.IndexedPayment or PpaRole.FixedCharge && bound.Side is not PowerPhysicalSide)
                        throw PpaErrors.Error("ppa_quantity_unit",slot,"Attribute and energy payments bind their matching delivery quantities.");
                }
            }
            else if(m.Slot.Role is PpaRole.CertificatePayment or PpaRole.CapacityPayment or PpaRole.FixedCharge)
                throw PpaErrors.Error("ppa_quantity_reference",slot,"Attribute payments require an explicit matching delivery slot.");
            if(m.Side is FixedSide fixedSide && fixedSide.Price.Unit.Currency!=t.Currency || m.Side is FloatingSide floating && (floating.Price.Adder.Unit.Currency!=t.Currency || floating.Price.Curve.Revision!=instrument.Definitions))
                throw PpaErrors.Error("ppa_price_rule",slot,"Prices retain exact currency and curve revision.");
        }
        int Count(PpaRole role)=>members.Count(m=>m.Slot.Role==role);
        var energy=Count(PpaRole.EnergyDelivery);var strike=Count(PpaRole.StrikePayment);var indexed=Count(PpaRole.IndexedPayment);var cap=Count(PpaRole.CapacityDelivery);
        if(Count(PpaRole.CertificateDelivery)>1 || Count(PpaRole.CertificatePayment)>1 || Count(PpaRole.FixedCharge)>1 || Count(PpaRole.CertificatePayment)>Count(PpaRole.CertificateDelivery))
            throw PpaErrors.Error("ppa_composition","memberships","Attribute and fixed-charge role cardinalities are unsupported.");
        if(cap==1 && energy==0 && strike==0 && indexed==0 && Count(PpaRole.CapacityPayment)==1 && Count(PpaRole.CertificateDelivery)==0 && Count(PpaRole.FixedCharge)==0)return "capacity_agreement";
        if(cap!=0 || Count(PpaRole.CapacityPayment)!=0)throw PpaErrors.Error("ppa_composition","capacity","Select the complete capacity agreement composition.");
        if(energy==1 && (strike==1 && indexed==0 || strike==0 && indexed==1))
        {
            var delivery=members.Single(m=>m.Slot.Role==PpaRole.EnergyDelivery).Slot;
            var payment=members.Single(m=>m.Slot.Role is PpaRole.StrikePayment or PpaRole.IndexedPayment);
            if(payment.Side.Terms.Quantity is not SlotQuantity binding || binding.Slots.Length!=1 || binding.Slots[0]!=delivery)
                throw PpaErrors.Error("ppa_composition",payment.Slot.ToString(),"A physical contract's strike or index payment must bind its energy-delivery slot.");
            return strike==1?"fixed_physical":"indexed_physical";
        }
        if(energy==0 && strike==1 && indexed==1)return "virtual_fixed_for_floating";
        if(energy==0 && strike==0 && indexed==2 && Count(PpaRole.CertificateDelivery)==0 && Count(PpaRole.FixedCharge)==0)
        {
            var prices=members.Select(m=>m.Side).OfType<FloatingSide>().ToArray();
            if(prices[0].Price.Curve==prices[1].Price.Curve || DefinitionIdentity.For(prices[0].Terms.Quantity)!=DefinitionIdentity.For(prices[1].Terms.Quantity))
                throw PpaErrors.Error("ppa_composition","basis_cash","Two distinct index curves must use one explicit quantity binding.");
            return "basis_cash";
        }
        throw PpaErrors.Error("ppa_composition","memberships","The role composition is not supported.");
    }
}
