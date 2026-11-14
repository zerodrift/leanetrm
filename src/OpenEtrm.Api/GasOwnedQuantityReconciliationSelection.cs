using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedPositionQuantityCorrection(Guid PositionId,[property:JsonRequired]int ExpectedRevision,string Quantity,
    [property:JsonRequired]bool Retracted,Guid EvidenceId,string SourceAllocation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedConsumptionQuantityCorrection(Guid ConsumptionId,[property:JsonRequired]int ExpectedRevision,Guid LotId,string Quantity,
    [property:JsonRequired]bool Retracted,Guid EvidenceId,[property:JsonRequired]Guid? ReservationId,string SourceAllocation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedQuantityReconciliationSelection(
    [property:JsonRequired]ImmutableArray<GasOwnedPositionQuantityCorrection> Positions,
    [property:JsonRequired]ImmutableArray<GasOwnedConsumptionQuantityCorrection> Consumptions,string Evidence);
