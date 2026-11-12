using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Publications;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PricePublicationSaveRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] Guid SnapshotId,
    [property: JsonRequired] int ExpectedVersion, [property: JsonRequired] PricePublicationDefinition Definition);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PricePublicationPreviewRequest([property: JsonRequired] Guid SnapshotId,
    [property: JsonRequired] PricePublicationDefinition Definition, [property: JsonRequired] PricePublicationOccurrence Occurrence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PricePublicationOccurrenceSaveRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] Guid SnapshotId,
    [property: JsonRequired] Guid Id, [property: JsonRequired] PricePublicationOccurrence Occurrence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PricePublicationFinalIngestRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] Guid SnapshotId,
    [property: JsonRequired] Guid OccurrenceId, [property: JsonRequired] string IndexCode, [property: JsonRequired] string SourceCode,
    [property: JsonRequired] string SourceEventId, [property: JsonRequired] string SourceRevisionId,
    [property: JsonRequired, JsonConverter(typeof(PublicationInstantConverter))] DateTimeOffset PublishedAt,
    [property: JsonRequired] string Value, [property: JsonRequired] Guid? CorrectsReleaseId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerPublicationPreviewRequest([property: JsonRequired] Guid SnapshotId,
    [property: JsonRequired] PricePublicationKey Publication, [property: JsonRequired] string IndexCode,
    [property: JsonRequired] Guid SourceId, [property: JsonRequired] string Market,
    [property: JsonRequired] DateOnly FromDate, [property: JsonRequired] DateOnly ThroughDate,
    [property: JsonRequired, JsonConverter(typeof(PublicationInstantConverter))] DateTimeOffset KnownAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerProductPreviewRequest([property: JsonRequired] PowerPublicationPreviewRequest Capture,
    [property: JsonRequired] string Product, [property: JsonRequired] int ProductVersion,
    [property: JsonRequired] string Block, [property: JsonRequired] string Averaging);
public sealed record SavedPricePublicationOccurrence(Guid Id, PricePublicationOccurrence Occurrence);
public sealed record PricePublicationChoice(ReferenceKey Reference, string Name);
public sealed record PricePublicationIndexChoice(ReferenceKey Key, ReferenceKey Commodity, ReferenceKey Location,
    ReferenceKey Quote, PriceUnit Unit, string Name);
public sealed record PricePublicationCatalog(Guid CatalogId, ReferenceRevision References, bool CanEdit, bool CanIngest,
    ImmutableArray<PricePublisher> Publishers, ImmutableArray<PricePublicationDefinition> Publications,
    ImmutableArray<PricePublicationChoice> Commodities, ImmutableArray<PricePublicationIndexChoice> Indices,
    ImmutableArray<PricePublicationChoice> Calendars, ImmutableArray<PricePublicationChoice> Clocks,
    ImmutableArray<PricePublicationChoice> Sources, ImmutableArray<string> Implementations,
    ImmutableArray<SavedPricePublicationOccurrence> Occurrences, ImmutableArray<JsonElement> Releases)
{
}

public static class PricePublications
{
    public static void MapPricePublications(this RouteGroupBuilder api, PreviewDatabase database, CustomCurveOperation operation)
    {
        api.MapGet("/price-publications", (Func<HttpContext,Task<IResult>>)((HttpContext context) => operation(context, "ReadMarketData", ContextVersions.CatalogueSnapshot(context.Request.Query),
            async (connection, pin, ct) => Results.Ok(await new PricePublicationStore(database).Catalog(connection, pin, ct)))));
        api.MapPost("/price-publications/save", (HttpContext context, PricePublicationSaveRequest request) =>
            operation(context, "WriteMarketDefinitions", request.SnapshotId,
                async (connection, pin, ct) => Results.Ok(await new PricePublicationStore(database).Save(connection, request, ct))));
        api.MapPost("/price-publications/preview", (HttpContext context, PricePublicationPreviewRequest request) =>
            operation(context, "ReadMarketData", request.SnapshotId,
                async (connection, pin, ct) => Results.Ok(await new PricePublicationStore(database).Preview(connection, request, ct))));
        api.MapPost("/price-publications/power/preview",(HttpContext context,PowerPublicationPreviewRequest request)=>
            operation(context,"ReadMarketData",request.SnapshotId,
                async(connection,pin,ct)=>Results.Ok(await new PricePublicationStore(database).InspectPower(connection,pin,request,ct))));
        api.MapGet("/price-publications/power/products",(HttpContext context,Guid? snapshotId)=>
            operation(context,"ReadMarketData",snapshotId,
                async(connection,pin,ct)=>Results.Ok(await new PricePublicationStore(database).PowerProducts(connection,pin,ct))));
        api.MapPost("/price-publications/power/product-preview",(HttpContext context,PowerProductPreviewRequest request)=>
            operation(context,"ReadMarketData",request.Capture?.SnapshotId,
                async(connection,pin,ct)=>Results.Ok(await new PricePublicationStore(database).InspectPowerProduct(connection,pin,request,ct))));
        api.MapPost("/price-publications/occurrences/save", (HttpContext context, PricePublicationOccurrenceSaveRequest request) =>
            operation(context, "WriteMarketDefinitions", request.SnapshotId,
                async (connection, pin, ct) => Results.Ok(await new PricePublicationStore(database).SaveOccurrence(connection, request, ct))));
        api.MapPost("/price-publications/ingest", (HttpContext context, PricePublicationFinalIngestRequest request) =>
            operation(context, "ImportObservations", request.SnapshotId,
                async (connection, pin, ct) => Results.Ok(await new PricePublicationStore(database).IngestFinal(connection, request, ct))));
    }
}

public sealed class PublicationInstantConverter:JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
    {
        if(reader.TokenType!=JsonTokenType.String||reader.GetString() is not {} text||text.EndsWith(".Z",StringComparison.Ordinal)||!DateTimeOffset.TryParseExact(text,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'","yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'"],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var value))
            throw new JsonException("Supply an explicit UTC Z instant with at most six fractional digits.");
        return value;
    }
    public override void Write(Utf8JsonWriter writer,DateTimeOffset value,JsonSerializerOptions options)
    {
        if(value.Offset!=TimeSpan.Zero||value.Ticks%10!=0)throw new JsonException("The instant requires exact UTC microseconds.");
        writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture));
    }
}
