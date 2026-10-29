using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

// Every transport field, including union nulls and empty arrays, is explicit.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaDeliveryInput([property:JsonRequired]string Clock,
    [property:JsonRequired]int IntervalMinutes,
    [property:JsonRequired]string From,
    [property:JsonRequired]string To);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaSlotInput([property:JsonRequired]string Role,
    [property:JsonRequired]int Ordinal);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaHoursInput([property:JsonRequired]string Membership,
    [property:JsonRequired]Guid? TouProduct,
    [property:JsonRequired]int? TouVersion,
    [property:JsonRequired]string Calendar);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaQuantityInput([property:JsonRequired]string Kind,
    [property:JsonRequired]string[] Sources,
    [property:JsonRequired]string? Stage,
    [property:JsonRequired]string? Capacity,
    [property:JsonRequired]string? Energy,
    [property:JsonRequired]PpaHoursInput? Hours,
    [property:JsonRequired]PpaSlotInput[] Slots,
    [property:JsonRequired]string[] Assets);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaPaymentInput([property:JsonRequired]string Frequency,
    [property:JsonRequired]string Rule,
    [property:JsonRequired]int? Days,
    [property:JsonRequired]int? DayOfMonth,
    [property:JsonRequired]string? DayBasis,
    [property:JsonRequired]string? ShortMonth,
    [property:JsonRequired]string Adjustment,
    [property:JsonRequired]string? Calendar,
    [property:JsonRequired]string Implementation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaStepInput([property:JsonRequired]string From,
    [property:JsonRequired]string Price);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaFixedInput([property:JsonRequired]string Kind,
    [property:JsonRequired]string Quantity,
    [property:JsonRequired]string Multiplier,
    [property:JsonRequired]string? Price,
    [property:JsonRequired]string? Rate,
    [property:JsonRequired]string? BaseDate,
    [property:JsonRequired]string? Basis,
    [property:JsonRequired]PpaStepInput[] Steps);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaIndexInput([property:JsonRequired]string Curve,
    [property:JsonRequired]string Quantity,
    [property:JsonRequired]string Multiplier,
    [property:JsonRequired]string IndexMultiplier,
    [property:JsonRequired]string Adder,
    [property:JsonRequired]PriceSettlementInput? Settlement);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PriceSettlementInput([property:JsonRequired]string Mode,
    [property:JsonRequired]string? Index,[property:JsonRequired]string? ProjectionPolicy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaAttributeInput([property:JsonRequired]string Quantity,
    [property:JsonRequired]string? Conversion,
    [property:JsonRequired]string? Eligibility,
    [property:JsonRequired]string? Holding,
    [property:JsonRequired]string? Disposition,
    [property:JsonRequired]string? Rule);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaSideInput([property:JsonRequired]string Kind,
    [property:JsonRequired]PpaQuantityInput Quantity,
    [property:JsonRequired]PpaPaymentInput PaymentRule,
    [property:JsonRequired]string? Location,
    [property:JsonRequired]PpaFixedInput? FixedPrice,
    [property:JsonRequired]PpaIndexInput? IndexPrice,
    [property:JsonRequired]PpaAttributeInput? Attribute);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaMemberInput([property:JsonRequired]string Role,
    [property:JsonRequired]int Ordinal,
    [property:JsonRequired]string Coefficient,
    [property:JsonRequired]PpaSideInput Side);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaTermsInput([property:JsonRequired]Guid DefinitionRevisionId,
    [property:JsonRequired]Guid ReferenceCatalogId,
    [property:JsonRequired]Guid ReferenceRevisionId,
    [property:JsonRequired]PpaDeliveryInput Delivery,
    [property:JsonRequired]string Currency,
    [property:JsonRequired]string UnitQuantity,
    [property:JsonRequired]bool Discounting,
    [property:JsonRequired]string Implementation,
    [property:JsonRequired]string[] NormalizationAssets,
    [property:JsonRequired]PpaMemberInput[] Memberships);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaBookingInput([property:JsonRequired]Guid LegalEntityId,
    [property:JsonRequired]int LegalEntityRevision,
    [property:JsonRequired]Guid CounterpartyId,
    [property:JsonRequired]int CounterpartyRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaWriteRequest([property:JsonRequired]Guid OperationId,
    [property:JsonRequired]Guid SnapshotId,
    [property:JsonRequired]Guid? TradeId,
    [property:JsonRequired]int? ExpectedRevision,
    [property:JsonRequired]Guid BookId,
    [property:JsonRequired]string? BoundaryAt,
    [property:JsonRequired]string? CoverageFrom,
    [property:JsonRequired]string? CoverageTo,
    [property:JsonRequired]string? Quantity,
    [property:JsonRequired]string Reason,
    [property:JsonRequired]string Memo,
    [property:JsonRequired]PpaBookingInput? Booking,
    [property:JsonRequired]PpaTermsInput? Terms);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaValidateRequest([property:JsonRequired]Guid SnapshotId,
    [property:JsonRequired]PpaTermsInput Terms,
    [property:JsonRequired]string Quantity,
    [property:JsonRequired]string CoverageFrom,
    [property:JsonRequired]string CoverageTo);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaPreviewRequest([property:JsonRequired]Guid SnapshotId,
    [property:JsonRequired]Guid DefinitionRevisionId,
    [property:JsonRequired]Guid ReferenceCatalogId,
    [property:JsonRequired]Guid ReferenceRevisionId,
    [property:JsonRequired]string Scope,
    [property:JsonRequired]Guid[] SelectedIds,
    [property:JsonRequired]bool ExplicitSubset,
    [property:JsonRequired]PpaTermsInput? Terms,
    [property:JsonRequired]string? Quantity,
    [property:JsonRequired]string? CoverageFrom,
    [property:JsonRequired]string? CoverageTo,
    [property:JsonRequired]string KnownAt,
    [property:JsonRequired]string PendingActuals,
    [property:JsonRequired]string[] DetailMonths);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaEditorRequest([property:JsonRequired]Guid SnapshotId,
    [property:JsonRequired]Guid TradeId,
    [property:JsonRequired]string KnownAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaStatusRequest([property:JsonRequired]Guid OperationId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaLegsRequest([property:JsonRequired]Guid SnapshotId,
    [property:JsonRequired]Guid TradeId,
    [property:JsonRequired]string KnownAt,
    [property:JsonRequired]int Offset,
    [property:JsonRequired]int Limit);

internal sealed class PpaJsonResult(byte[] bytes,int status):IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.StatusCode=status;context.Response.ContentType="application/json";context.Response.ContentLength=bytes.Length;
        await context.Response.Body.WriteAsync(bytes,context.RequestAborted);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaDirectoryRequest([property:JsonRequired]Guid SnapshotId,
    [property:JsonRequired]Guid[] BookIds,
    [property:JsonRequired]string KnownAt,
    [property:JsonRequired]int Offset,
    [property:JsonRequired]int Limit);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaInspectRequest([property:JsonRequired]PpaPreviewRequest Preview,
    [property:JsonRequired]Guid BindingId,
    [property:JsonRequired]Guid SideId,
    [property:JsonRequired]int Offset,
    [property:JsonRequired]int Limit);
