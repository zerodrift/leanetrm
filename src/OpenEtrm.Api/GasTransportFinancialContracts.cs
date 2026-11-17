using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

internal static class GasTransportFinancialProfile
{
    internal const string Version="physical_gas_transport_direct_rights_v1";
    internal const string Fee="unconditional_one_off_fixed_amount_at_booking_v1";
    internal const string Expiry="fixed_window_capacity_rights_expire_at_through_v1";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasTransportFinancialBooking(Guid OperationId,Guid SnapshotId,Guid ContextId,Guid DefinitionRevisionId,
    string KnowledgeMode,string Profile,string BusinessKey,Guid BookId,Guid LegalEntityId,int LegalEntityRevision,
    Guid CounterpartyId,int CounterpartyRevision,Guid ValuationReferenceCatalogId,Guid ValuationReferenceRevisionId,
    Guid EntitlementId,Guid CapacityVersionId,string Perspective,string PackageQuantity,string Currency,
    string RightsValuationPolicy,string FeeProfile,string FixedFeeAmount,string FeeDirection,
    [property:JsonRequired]DateTimeOffset? FeeDueAt,string ExpiryPolicy,string Evidence);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasTransportFinancialWriteRequest([property:JsonRequired]LifecycleReadContext Context,
    [property:JsonRequired]string OwnerId,Guid OperationId,[property:JsonRequired]string EffectiveAt,
    [property:JsonRequired]JsonElement Command);
