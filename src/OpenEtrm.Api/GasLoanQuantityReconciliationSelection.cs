using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasLoanQuantityReconciliationSelection(
    [property:JsonRequired]Guid LoanId,
    [property:JsonRequired]int ExpectedRevision,
    [property:JsonRequired]int StockExpectedRevision,
    [property:JsonRequired]int ReturnExpectedRevision,
    [property:JsonRequired]string Quantity,
    [property:JsonRequired]Guid TitleEvidenceId,
    [property:JsonRequired]Guid AgreementEvidenceId,
    [property:JsonRequired]string SourceAllocation,
    [property:JsonRequired]string FeeDisposition,
    [property:JsonRequired]ImmutableArray<GasOwnedConsumptionQuantityCorrection> Consumptions,
    [property:JsonRequired]string Evidence);
