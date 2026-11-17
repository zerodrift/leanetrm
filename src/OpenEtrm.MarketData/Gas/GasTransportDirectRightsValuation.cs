using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public static class GasTransportDirectRightsPolicy
{
    public const string Version="signed_current_whole_remaining_rights_pv_excluding_fees_and_owned_gas_v1";
    public const string ExpiryAtThrough="fixed_window_capacity_rights_expire_at_through_v1";
}

[EconomicDefinition("gas_transport_direct_rights_terms_v1")]
public sealed record GasTransportDirectRightsTerms([property:IdentityField]string Policy,
    [property:IdentityField]ReferenceKey PackageQuantity,[property:IdentityField]ReferenceKey Currency);

// Supplied by the admitted commercial source. The financial identity excludes
// optimistic write controls and operational heads not declared by that contract.
[EconomicDefinition("gas_transport_direct_rights_state_v1")]
public sealed record GasTransportDirectRightsState([property:IdentityField]Guid AgreementId,[property:IdentityField]Guid EntitlementId,
    [property:IdentityField]Guid CapacityVersionId,[property:IdentityField]string RemainingRightsIdentity);

// Recorded provenance is distinct from a later authorized read of the same fact.
// Neither this value nor a calculation context establishes native authorization.
public sealed record GasTransportDirectRightsMark(GasTransportDirectRightsState State,CurvePointKey Point,
    InputKey Input,ObservationKey Observation,ReferenceKey Source,ReferenceKey Quote,ObservationRole Role,
    ResolvedCalculationContext ProvenanceContext);

public sealed record GasTransportDirectRightsEvaluation(ResolvedCalculationContext Context,string BaseScenarioId,
    string NumericalPolicy);

public static class GasTransportDirectRightsValuation
{
    // A whole-package monetary estimate is not a price per unit of gas or capacity.
    public static bool SupportsQuantityScaling=>false;

    public static ExactMoney PV(GasTransportDirectRightsTerms terms,GasTransportDirectRightsState currentState,
        GasTransportDirectRightsMark? mark,GasTransportDirectRightsEvaluation evaluation,ConnectedScenario scenario)
    {
        RequireContract(terms,currentState);
        if(mark is null)throw Error("mark","A missing current rights mark is unpriced.");
        if(!State(mark.State)||currentState!=mark.State)
            throw Error("state","The mark must identify this exact agreement, capacity version and admitted remaining-rights state.");
        if(evaluation is null||scenario is null||!Context(evaluation.Context)||scenario.Plan.Context!=evaluation.Context||
            evaluation.NumericalPolicy!=ConnectedCurves.NumericalPolicy)
            throw Error("context","Retain this read's exact admitted context, numerical policy and original scenario envelope.");
        var root=scenario;
        while(root.Parent is {} parent)root=parent;
        if(evaluation.BaseScenarioId!=root.Id)
            throw Error("context","A rights valuation must remain under its exact original admitted base scenario.");
        var at=evaluation.Context.ValuationAt;
        if(scenario.CalculationAt!=at)
            throw Error("time","A current whole-rights mark has no qualified clock continuation.");
        if(!Context(mark.ProvenanceContext)||mark.ProvenanceContext.ValuationAt!=at||
            mark.ProvenanceContext.KnownAt>evaluation.Context.KnownAt)
            throw Error("mark","The exact current mark and its recorded provenance must be covered by this read.");
        if(!scenario.Plan.Points.TryGetValue(mark.Point,out var point)||point.Expression is not ConnectedSource source||
            source.Input.Use!=InputUse.Variable||source.Quote.Kind!=QuoteKind.Outright||
            source.Request.Curve!=mark.Point.Curve||point.Contract?.Key!=source.Request.Contract||source.Input!=mark.Input||source.Observation.Key!=mark.Observation||
            source.Observation.Source!=mark.Source||source.Quote.Key!=mark.Quote||source.Observation.Role!=mark.Role||
            mark.Role!=ObservationRole.TraderMark||source.Observation.State!=PublicationState.Final||source.Observation.ObservedAt!=at)
            throw Error("mark","Select the exact current direct trader mark and primitive input; a derived, stale or different source is unavailable.");
        if(point.Unit!=PriceUnit.FromQuote(source.Quote)||point.Unit.Quantity!=terms.PackageQuantity||
            point.Unit.Currency!=terms.Currency||point.Unit.Multiplier!=ExactDecimal.Parse("1"))
            throw Error("unit","The signed current money mark must quote one exact whole rights package in its stated currency, without another multiplier.");
        return scenario.Price(mark.Point).ExactTimes(new(ExactDecimal.Parse("1"),terms.PackageQuantity));
    }

    internal static void RequireContract(GasTransportDirectRightsTerms terms,GasTransportDirectRightsState state)
    {
        if(terms is null||terms.Policy!=GasTransportDirectRightsPolicy.Version)
            throw Error("policy","Select the explicit signed current whole-rights PV policy excluding independent fees and owned gas.");
        if(!Key(terms.PackageQuantity)||!Key(terms.Currency)||terms.PackageQuantity.Revision!=terms.Currency.Revision)
            throw Error("unit","Retain the exact whole-package quantity basis and currency reference.");
        if(!State(state))throw Error("state","Retain the exact admitted agreement, capacity version and remaining-rights state.");
    }

    static bool State(GasTransportDirectRightsState? state)=>state is not null&&state.AgreementId!=Guid.Empty&&
        state.EntitlementId!=Guid.Empty&&state.CapacityVersionId!=Guid.Empty&&
        !string.IsNullOrWhiteSpace(state.RemainingRightsIdentity)&&state.RemainingRightsIdentity==state.RemainingRightsIdentity.Trim();
    static bool Context(ResolvedCalculationContext? context)=>context is not null&&
        new[]{context.EffectiveAt,context.KnownAt,context.ValuationAt,context.MarketAsOf}.All(TradingInstant.IsSupported)&&
        context.EffectiveAt==context.ValuationAt&&context.MarketAsOf==context.ValuationAt&&context.KnownAt>=context.ValuationAt;
    static bool Key(ReferenceKey key)=>key.Revision.CatalogId!=Guid.Empty&&key.Revision.RevisionId!=Guid.Empty&&
        !string.IsNullOrWhiteSpace(key.Code)&&key.Code==key.Code.Trim();
    static MarketDataException Error(string suffix,string message)=>new("gas_transport_rights_"+suffix,message);
}
