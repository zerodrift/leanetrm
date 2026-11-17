using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

// The custody profile retains title. Its absolute fee and independently chosen
// direction are not gas quantities, and provider custody has no owned account.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasParkCustodyBooking(Guid OperationId,Guid SnapshotId,Guid ContextId,Guid DefinitionRevisionId,
    string Convention,string KnowledgeMode,string Profile,Guid BookId,Guid LegalEntityId,int LegalEntityRevision,
    Guid CounterpartyId,int CounterpartyRevision,Guid ReferenceCatalogId,Guid ReferenceRevisionId,
    string Point,string QuantitySpecification,string Currency,[property:JsonRequired]Guid? OwnedAccountId,
    string ServiceKind,string Perspective,string Quantity,DateTimeOffset ParkAt,DateTimeOffset ReturnAt,
    string TitlePolicy,string ReturnPolicy,string ReturnTiming,string ReturnOrder,string UsePolicy,string LossPolicy,
    string PhysicalPolicy,string PlanPolicy,string FixedFeeAmount,string FeeDirection,
    [property:JsonRequired]DateTimeOffset? FeeDueAt,string FeeRecognition);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GasParkCustodyEvidenceSelection(string SourceKey,int ExpectedRevision,string Kind,string Quantity,
    DateTimeOffset ObservedAt,string Evidence);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GasParkCustodyLotSelection(Guid LotId,string Quantity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GasParkCustodyAcceptSelection(int ExpectedVersion,Guid AgreementEvidenceId,Guid ReceiptEvidenceId,
    DateTimeOffset EffectiveAt,GasParkCustodyLotSelection[] Allocations);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GasParkCustodyReleaseSelection(Guid ReservationId,string Quantity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GasParkCustodyReturnSelection(int ExpectedVersion,string BusinessKey,Guid EvidenceId,string Quantity,
    DateTimeOffset EffectiveAt,GasParkCustodyReleaseSelection[] Allocations);
