using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Trading;

public enum PpaRole { EnergyDelivery=1,CertificateDelivery,CapacityDelivery,StrikePayment,IndexedPayment,FixedCharge,CertificatePayment,CapacityPayment }
[EconomicDefinition("ppa_slot_v1")]
public sealed record SideSlot([property:IdentityField]PpaRole Role,[property:IdentityField]int Ordinal=0);
[EconomicDefinition("ppa_delivery_v1")]
public sealed record PpaDelivery([property:IdentityField]ReferenceKey Clock,[property:IdentityField]int IntervalMinutes,
    [property:IdentityField]DateOnly From,[property:IdentityField]DateOnly To);
public abstract record PpaQuantity;
[EconomicDefinition("ppa_volume_quantity_v2")]
public sealed record VolumeQuantity([property:IdentityField]ImmutableArray<VolumeSourceKey> Sources,[property:IdentityField]QuantityStage Stage,
    [property:IdentityField]ReferenceKey Energy):PpaQuantity;
[EconomicDefinition("ppa_profile_quantity_v1")]
public sealed record ProfileQuantity([property:IdentityField]ReferenceKey Capacity,[property:IdentityField]ReferenceKey Energy,
    [property:IdentityField]DeliveryHours Hours):PpaQuantity;
[EconomicDefinition("ppa_slot_quantity_v1")]
public sealed record SlotQuantity([property:IdentityField]ImmutableArray<SideSlot> Slots):PpaQuantity;
[EconomicDefinition("ppa_accreditation_quantity_v1")]
public sealed record AccreditedQuantity([property:IdentityField]ImmutableArray<AssetKey> Assets):PpaQuantity;
public enum PpaNormalizationPolicy { ContractUnit=1,SumCapacityInForce }
[EconomicDefinition("ppa_normalization_v1")]
public sealed record PpaNormalization
{
    [IdentityField]public ImmutableArray<AssetKey> Assets { get; }
    [IdentityField]public ReferenceKey Basis { get; }
    [IdentityField]public PpaNormalizationPolicy Policy { get; }
    public PpaNormalization(ImmutableArray<AssetKey> assets,ReferenceKey basis,PpaNormalizationPolicy policy)
    {
        if(assets.IsDefault||assets.Distinct().Count()!=assets.Length||!Enum.IsDefined(policy)||
            policy==PpaNormalizationPolicy.ContractUnit&&!assets.IsEmpty||policy==PpaNormalizationPolicy.SumCapacityInForce&&assets.IsEmpty)
            throw PpaErrors.Error("ppa_normalization","policy","Select an explicit contract-unit basis or a distinct nonempty asset set normalized by capacity in force.");
        Money.RequireKey(basis);
        Assets=[..assets.OrderBy(a=>a.Code,StringComparer.Ordinal)];Basis=basis;Policy=policy;
    }
}
[EconomicDefinition("schedule_quantity_input_port_v1")]
public sealed record ScheduleInputPort([property:IdentityField]SideSlot Slot,[property:IdentityField]ReferenceKey Quantity,
    [property:IdentityField]IntervalScheduleParameters Schedule);
[EconomicDefinition("ppa_side_terms_v2")]
public sealed record PpaSideTerms([property:IdentityField]PpaDelivery Delivery,[property:IdentityField]PpaQuantity Quantity,
    [property:IdentityField]ReferenceKey Currency,[property:IdentityField]Quantity UnitQuantity,[property:IdentityField]PaymentRule DateRule,
    [property:IdentityField]IntervalScheduleParameters Schedule,[property:IdentityField]ReferenceKey ScheduleQuantity,[property:IdentityField]PpaNormalization Normalization,
    [property:IdentityField]ImmutableArray<ScheduleInputPort> InputPorts);

public abstract record FixedPriceTerm
{
    public abstract PriceUnit Unit { get; }
    public abstract ExactUnitPrice At(DateOnly operatingDate);
}
[EconomicDefinition("ppa_flat_price_v1")]
public sealed record FlatPrice([property:IdentityField]UnitPrice Price):FixedPriceTerm
{
    private UnitPrice Required=>Price??throw PpaErrors.Error("ppa_price_rule","price","Supply an explicit fixed price.");
    public override PriceUnit Unit=>Required.Unit;
    public override ExactUnitPrice At(DateOnly operatingDate)=>ExactUnitPrice.FromPrice(Required);
}
[EconomicDefinition("ppa_escalated_price_v1")]
public sealed record EscalatedPrice([property:IdentityField]UnitPrice Price,[property:IdentityField]ExactDecimal Rate,
    [property:IdentityField]DateOnly BaseDate,[property:IdentityField]ContractYearBasis Basis):FixedPriceTerm
{
    private UnitPrice Required=>Price??throw PpaErrors.Error("ppa_price_rule","price","Supply an explicit escalation base price.");
    public override PriceUnit Unit=>Required.Unit;
    public override ExactUnitPrice At(DateOnly operatingDate)=>ContractFactors.Escalate(ExactUnitPrice.FromPrice(Required),Rate,Basis,BaseDate,operatingDate);
}
[EconomicDefinition("ppa_price_step_v1")]
public sealed record PriceStep([property:IdentityField]DateOnly From,[property:IdentityField]UnitPrice Price);
[EconomicDefinition("ppa_stepped_price_v1")]
public sealed record SteppedPrice:FixedPriceTerm
{
    [IdentityField]public ImmutableArray<PriceStep> Steps { get; }
    public override PriceUnit Unit=>Steps[0].Price.Unit;
    public SteppedPrice(ImmutableArray<PriceStep> steps)
    {
        if(steps.IsDefaultOrEmpty || steps.Any(s=>s is null || s.Price is null) || steps.Select(s=>s.From).Distinct().Count()!=steps.Length ||
            steps.Select(s=>s.Price.Unit).Distinct().Count()!=1)throw PpaErrors.Error("ppa_price_rule","price","Supply distinct dated strike steps in one exact unit.");
        Steps=[..steps.OrderBy(s=>s.From)];
    }
    public override ExactUnitPrice At(DateOnly operatingDate)=>ExactUnitPrice.FromPrice(Steps.LastOrDefault(s=>s.From<=operatingDate)?.Price
        ??throw PpaErrors.Error("ppa_price_rule","price","The strike steps do not cover delivery."));
}
[EconomicDefinition("ppa_index_price_v2")]
public sealed record IndexPrice([property:IdentityField]CurveKey Curve,[property:IdentityField]ExactDecimal Multiplier,[property:IdentityField]UnitPrice Adder,
    [property:IdentityField]PriceSettlementSelection? Settlement);
[EconomicDefinition("ppa_member_v1")]
public sealed record PpaMembership([property:IdentityField]SideSlot Slot,[property:IdentityField]Side Side,[property:IdentityField]ExactDecimal Coefficient);
internal static class PpaErrors
{
    internal static MarketDataException Error(string code,string term,string message)=>new(code,$"{PpaCapabilities.Version}: {term}: {message}");
    internal static readonly ExactDecimal One=ExactDecimal.Parse("1");
}
