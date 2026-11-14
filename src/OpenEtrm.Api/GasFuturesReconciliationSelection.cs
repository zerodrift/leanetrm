using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

// An amendment keeps the observed occurrence and business reference. Changing
// either requires an explicit advance/return contract rather than a new date.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasFuturesSupplyReconciliationSelection(
    [property:JsonRequired]Guid? ConsumptionId,
    [property:JsonRequired]int ExpectedRevision,
    Guid LotId,Guid EvidenceId,[property:JsonRequired]Guid? ReservationId,
    string Quantity,[property:JsonRequired]bool Retracted);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasFuturesQuantityReconciliationSelection(
    Guid PositionId,Guid PerformanceId,[property:JsonRequired]int ExpectedRevision,
    [property:JsonRequired]int ExpectedPrincipalVersion,string Quantity,
    [property:JsonRequired]bool Retracted,[property:JsonRequired]Guid? TitleEvidenceId,
    [property:JsonRequired]ImmutableArray<GasFuturesSupplyReconciliationSelection> Supply,
    string SourceAllocation,string Evidence);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasFuturesQuantityBatchSelection(
    [property:JsonRequired]ImmutableArray<GasFuturesQuantityReconciliationSelection> Corrections);
