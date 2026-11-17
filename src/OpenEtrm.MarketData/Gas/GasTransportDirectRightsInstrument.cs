using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

[EconomicDefinition("gas_transport_direct_rights_expiry_v1")]
public sealed record GasTransportDirectRightsExpiry([property:IdentityField]Guid Id,
    [property:IdentityField]GasTransportDirectRightsState State,[property:IdentityField]Guid PreviousLiveStateId,
    [property:IdentityField]DateTimeOffset Through,[property:IdentityField]DateTimeOffset EffectiveAt,[property:IdentityField]DateTimeOffset KnownAt);

// The native adapter authenticates financial-state/expiry lineage and authority.
// This pricer never turns an elapsed window or an absent mark into expiry.
[EconomicDefinition("gas_transport_direct_rights_instrument_v1")]
public sealed record GasTransportDirectRightsInstrument:SidedInstrument
{
    public const string ModelVersion="gas_transport_direct_rights_whole_position_pv_v1";
    private readonly GasTransportDirectRightsSide rights;
    [IdentityField]public GasTransportDirectRightsTerms Terms=>rights.RightsTerms;
    [IdentityField]public GasTransportDirectRightsState State=>rights.State;
    [IdentityField]public DateTimeOffset Through=>rights.Through;
    [IdentityField]public string ExpiryPolicy=>rights.ExpiryPolicy;
    [IdentityField]public GasTransportDirectRightsExpiry? Expiry=>rights.Expiry;
    public CapturedPriceSource? Source=>rights.Source;
    public GasTransportDirectRightsMark? Mark=>rights.Mark;
    public GasTransportDirectRightsEvaluation? Evaluation=>rights.Evaluation;
    // These memberships derive from the existing published economic fields.
    // Neither this view nor a transient claim schedule changes that identity.
    public new ImmutableArray<SideMembership> Sides=>base.Sides;
    public override string? RetainedScheduleIdentity=>null;
    public override bool SupportsQuantityScaling=>false;
    public override bool SupportsWholePosition=>true;
    internal override bool SupportsSettlement=>false;
    public override ReferenceKey Currency=>Terms.Currency;
    public override Quantity UnitQuantity=>new(ExactDecimal.Parse("1"),Terms.PackageQuantity);
    public override DefinitionIdentity PricingBindingIdentity=>rights.PricingBindingIdentity;

    public GasTransportDirectRightsInstrument(Guid id,GasTransportDirectRightsTerms terms,GasTransportDirectRightsState state,
        DateTimeOffset through,string expiryPolicy,GasTransportDirectRightsExpiry? expiry,CapturedPriceSource? source,
        GasTransportDirectRightsMark? mark,GasTransportDirectRightsEvaluation? evaluation):base(id)
    {rights=new(id,terms,state,through,expiryPolicy,expiry,source,mark,evaluation);}

    protected override ImmutableArray<SideMembership> ComposeSides()=>[new(rights,ExactDecimal.Parse("1"))];
    internal override UnitPricingContract DeclareScope(UnitValuationContext context,bool lifecycle)=>Declare(context);
    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if(state is not null)
            throw new MarketDataException("settlement_scope","This instrument does not support the supplied binding valuation state.");
    }

    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        RequireContext(context);
        return new(Id,ModelVersion,Source is {} source?[source.Market]:[],Terms.PackageQuantity,Currency){Time=ValuationTimeDependency.Pinned};
    }
    public override ExactMoney Value(UnitValuationContext context)
    {
        RequireContext(context);
        return base.Value(context);
    }
    private void RequireContext(UnitValuationContext context)
    {
        if(context is null||context.Measure!=ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure","A whole service-rights position implements present value only.");
        if(context.IsLifecycleProjection||context.CalculationAt!=context.Snapshot.ValuationAt)
            throw Error("time","The current rights profile has no projected or shifted-clock valuation.");
        rights.RequireSnapshot(context.Snapshot);
    }
    static MarketDataException Error(string suffix,string message)=>new("gas_transport_rights_"+suffix,message);
}
